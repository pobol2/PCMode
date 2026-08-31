using System.Collections.Concurrent;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Engine.Contracts;
using PCMode.Audio.Engine.Runtime;
using PCMode.Audio.Protocol;
using PCMode.Audio.Protocol.Messages;

namespace PCMode.Audio.Host;

public sealed class AudioHostRuntime :
    IAsyncDisposable
{
    private const int MaxPendingCommands =
        128;

    private const int InitialRecoveryDelayMilliseconds =
        250;

    private const int MaximumRecoveryDelayMilliseconds =
        5_000;

    private readonly AudioEndpointId _sourceId;

    private readonly Func<IAudioEngine>
        _engineFactory;

    private readonly Func<IDisposable>?
        _threadRegistrationFactory;

    private readonly Func<AudioEngineState, AudioInputHealth>?
        _inputHealthProvider;

    private readonly ConcurrentQueue<RuntimeCommand>
        _commands =
            new();

    private readonly AutoResetEvent _wake =
        new(false);

    private readonly ManualResetEventSlim _started =
        new(false);

    private readonly ManualResetEventSlim _stopped =
        new(false);

    private Thread? _thread;

    private Exception? _threadFailure;

    private volatile bool _shutdownRequested;

    private DateTimeOffset _startedUtc;

    private int _pendingCommandCount;

    private AudioTarget? _desiredTarget;

    private long _nextRecoveryTick;

    private int _recoveryDelayMilliseconds =
        InitialRecoveryDelayMilliseconds;

    private bool _recoveryScheduled;

    public AudioHostRuntime(
        AudioEndpointId sourceId,
        Func<IAudioEngine> engineFactory,
        Func<IDisposable>? threadRegistrationFactory =
            null,
        Func<AudioEngineState, AudioInputHealth>?
            inputHealthProvider = null)
    {
        if (sourceId == default)
        {
            throw new ArgumentException(
                "Source endpoint is required.",
                nameof(sourceId));
        }

        ArgumentNullException.ThrowIfNull(
            engineFactory);

        _sourceId =
            sourceId;

        _engineFactory =
            engineFactory;

        _threadRegistrationFactory =
            threadRegistrationFactory;

        _inputHealthProvider =
            inputHealthProvider;
    }

    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException(
                "Audio Host runtime is already started.");
        }

        _thread =
            new Thread(
                ThreadMain)
            {
                Name =
                    "PCMode.Audio.Host.Audio",

                IsBackground =
                    false
            };

        // MTA, deliberately, and the single most important line in this
        // method. An STA thread that touches COM is given a hidden
        // top-level OleMainThreadWndClass window, and every blocking wait it
        // makes -- including the WaitHandle.WaitOne inside the WASAPI render
        // loop, waiting for the endpoint to ask for more audio -- becomes a
        // message-pumping wait that dispatches whatever arrives at that
        // window before it returns.
        //
        // A display topology change broadcasts WM_DISPLAYCHANGE and friends
        // to every top-level window in the session, synchronously, and the
        // broadcast does not complete until each recipient has handled it.
        // With this thread in an STA that put the render loop inside the
        // broadcast: it sat in its buffer wait dispatching messages for as
        // long as the slowest participant took -- with a TV on HDMI, most of
        // a second -- while the endpoint it was supposed to be feeding ran
        // dry and the physical output went silent. Measured at 608ms of a
        // 608ms write, all of it inside the wait.
        //
        // Nothing here needs an STA. The Host has no UI, and the Core Audio
        // interfaces this thread uses are both-threaded; MTA is what
        // Microsoft recommends for a WASAPI streaming thread precisely
        // because it does not pump. The input pump thread has always been
        // MTA, which is why the input side of this pipeline stayed clean
        // through every topology change while the render side stalled.
        _thread.SetApartmentState(
            ApartmentState.MTA);

        _thread.Start();

        if (!_started.Wait(
                TimeSpan.FromSeconds(
                    10)))
        {
            throw new TimeoutException(
                "Audio Host runtime startup timed out.");
        }

        if (_threadFailure is not null)
        {
            throw new InvalidOperationException(
                "Audio Host runtime failed to start.",
                _threadFailure);
        }
    }

    public Task ApplyAsync(
        AudioTarget target)
    {
        ArgumentNullException.ThrowIfNull(
            target);

        return InvokeAsync<object?>(
            engine =>
            {
                _desiredTarget =
                    target;

                try
                {
                    engine.Apply(
                        target);

                    UpdateRecoveryAfterApply(
                        engine);
                }
                catch
                {
                    ScheduleRecovery();

                    throw;
                }

                return null;
            });
    }

    /// <summary>
    /// Recalibrates one running route's gain, mute, endpoint volume,
    /// parametric EQ and delay, without reapplying the whole target.
    /// </summary>
    /// <remarks>
    /// Updates <see cref="_desiredTarget"/> the same way a successful
    /// <see cref="ApplyAsync"/> does, so that if this route later drops for
    /// an unrelated reason (a disconnected endpoint, say) the bounded
    /// recovery loop reconnects it with the calibrated values rather than
    /// the ones the target was originally started with.
    /// </remarks>
    public Task RecalibrateAsync(
        AudioEndpointId endpointId,
        float volume,
        bool muted,
        float gainDb,
        TimeSpan delay,
        ParametricEqProfile? parametricEq)
    {
        return InvokeAsync<object?>(
            engine =>
            {
                try
                {
                    engine.Recalibrate(
                        endpointId,
                        volume,
                        muted,
                        gainDb,
                        delay,
                        parametricEq);

                    _desiredTarget =
                        engine.ActiveTarget;

                    UpdateRecoveryAfterApply(
                        engine);
                }
                catch
                {
                    _desiredTarget =
                        engine.ActiveTarget;

                    ScheduleRecovery();

                    throw;
                }

                return null;
            });
    }

    public Task StopAsync()
    {
        return InvokeAsync<object?>(
            engine =>
            {
                _desiredTarget =
                    null;

                ResetRecoveryBackoff();

                engine.Stop();

                return null;
            });
    }

    public async Task<AudioEngineHealth>
        GetHealthAsync()
    {
        EngineHealthSnapshot snapshot =
            await InvokeAsync(
                engine =>
                    new EngineHealthSnapshot(
                        engine.GetHealth(),
                        engine.State));

        if (_inputHealthProvider is null)
        {
            return snapshot.Health;
        }

        try
        {
            return snapshot.Health.WithInput(
                _inputHealthProvider(
                    snapshot.EngineState));
        }
        catch (Exception ex)
        {
            return snapshot.Health.WithInput(
                new AudioInputHealth(
                    AudioInputKind.Unknown,
                    AudioInputHealthState.Failed,
                    lastFailure:
                        AudioFailure.FromException(
                            ex)));
        }
    }

    public Task<AudioHostStatus>
        GetStatusAsync()
    {
        return InvokeAsync(
            engine =>
            {
                AudioTarget? target =
                    engine.ActiveTarget;

                return new AudioHostStatus(
                    HostProtocolVersion.Current,
                    Environment.ProcessId,
                    _startedUtc,
                    _sourceId.Value,
                    ToRuntimeState(
                        engine.State),
                    target?.Id,
                    target?.Name);
            });
    }

    public async Task ShutdownAsync(
        CancellationToken cancellationToken =
            default)
    {
        Thread? thread =
            _thread;

        if (thread is null ||
            _stopped.IsSet)
        {
            return;
        }

        try
        {
            await InvokeAsync<object?>(
                engine =>
                {
                    _desiredTarget =
                        null;

                    ResetRecoveryBackoff();

                    engine.Stop();

                    return null;
                },
                requestShutdown: true);
        }
        catch when (_stopped.IsSet)
        {
            return;
        }

        bool stopped =
            await Task.Run(
                () =>
                    _stopped.Wait(
                        TimeSpan.FromSeconds(
                            5)),
                cancellationToken);

        if (!stopped)
        {
            throw new TimeoutException(
                "Audio Host runtime did not stop cleanly.");
        }

        if (_threadFailure is not null)
        {
            throw new InvalidOperationException(
                "Audio Host runtime failed.",
                _threadFailure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ShutdownAsync();
        }
        finally
        {
            _wake.Dispose();
            _started.Dispose();
            _stopped.Dispose();
        }
    }

    private Task<T> InvokeAsync<T>(
        Func<IAudioEngine, T> operation,
        bool requestShutdown = false)
    {
        ArgumentNullException.ThrowIfNull(
            operation);

        if (_thread is null)
        {
            throw new InvalidOperationException(
                "Audio Host runtime is not started.");
        }

        if (_stopped.IsSet)
        {
            throw new InvalidOperationException(
                "Audio Host runtime is stopped.");
        }

        if (_threadFailure is not null)
        {
            throw new InvalidOperationException(
                "Audio Host runtime has failed.",
                _threadFailure);
        }

        int pending =
            Interlocked.Increment(
                ref _pendingCommandCount);

        if (pending >
            MaxPendingCommands)
        {
            Interlocked.Decrement(
                ref _pendingCommandCount);

            throw new InvalidOperationException(
                $"Audio Host command queue is full ({MaxPendingCommands} pending commands)." );
        }

        var completion =
            new TaskCompletionSource<object?>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);

        _commands.Enqueue(
            new RuntimeCommand(
                engine =>
                    operation(
                        engine),
                completion,
                requestShutdown));

        _wake.Set();

        return AwaitResultAsync<T>(
            completion.Task);
    }

    private static async Task<T> AwaitResultAsync<T>(
        Task<object?> task)
    {
        object? result =
            await task;

        return (T)result!;
    }

    private void ThreadMain()
    {
        IAudioEngine? engine =
            null;

        IDisposable? threadRegistration =
            null;

        try
        {
            threadRegistration =
                _threadRegistrationFactory
                    ?.Invoke();

            engine =
                _engineFactory();

            _startedUtc =
                DateTimeOffset.UtcNow;

            _started.Set();

            while (!_shutdownRequested)
            {
                DrainCommands(
                    engine);

                if (_shutdownRequested)
                {
                    break;
                }

                if (engine.State ==
                    AudioEngineState.Running)
                {
                    try
                    {
                        engine.ProcessNextBlock();
                    }
                    catch
                    {
                        // AudioEngine already records the concrete failure.
                        // Keep the Host thread alive and let the bounded
                        // recovery loop converge the desired target again.
                        ScheduleRecovery();
                    }
                }

                TryRecoverDesiredTarget(
                    engine);

                if (engine.State !=
                    AudioEngineState.Running)
                {
                    _wake.WaitOne(
                        TimeSpan.FromMilliseconds(
                            25));
                }
            }
        }
        catch (Exception ex)
        {
            _threadFailure =
                ex;

            _started.Set();

            FailPendingCommands(
                ex);
        }
        finally
        {
            if (engine is not null)
            {
                try
                {
                    engine.Stop();
                }
                catch
                {
                }

                try
                {
                    engine.Dispose();
                }
                catch
                {
                }
            }

            try
            {
                threadRegistration?.Dispose();
            }
            catch
            {
            }

            FailPendingCommands(
                new InvalidOperationException(
                    "Audio Host runtime has stopped."));

            _stopped.Set();
        }
    }

    private void TryRecoverDesiredTarget(
        IAudioEngine engine)
    {
        AudioTarget? target =
            _desiredTarget;

        if (target is null)
        {
            return;
        }

        if (engine.State ==
                AudioEngineState.Running &&
            !engine.NeedsRecovery)
        {
            return;
        }

        if (!_recoveryScheduled)
        {
            // Route failures discovered without throwing take the same short
            // debounce as exception-driven failures. HDMI endpoint state often
            // chatters while a display topology is settling.
            ScheduleRecovery();

            return;
        }

        long now =
            Environment.TickCount64;

        if (now <
            _nextRecoveryTick)
        {
            return;
        }

        _recoveryScheduled =
            false;

        try
        {
            engine.Apply(
                target);

            UpdateRecoveryAfterApply(
                engine);
        }
        catch
        {
            ScheduleRecovery();
        }
    }

    private void UpdateRecoveryAfterApply(
        IAudioEngine engine)
    {
        if (engine.NeedsRecovery)
        {
            ScheduleRecovery();
        }
        else
        {
            ResetRecoveryBackoff();
        }
    }

    private void ScheduleRecovery()
    {
        if (_recoveryScheduled)
        {
            return;
        }

        long now =
            Environment.TickCount64;

        _nextRecoveryTick =
            now +
            _recoveryDelayMilliseconds;

        _recoveryScheduled =
            true;

        _recoveryDelayMilliseconds =
            Math.Min(
                _recoveryDelayMilliseconds *
                    2,
                MaximumRecoveryDelayMilliseconds);
    }

    private void ResetRecoveryBackoff()
    {
        _recoveryScheduled =
            false;

        _nextRecoveryTick =
            0;

        _recoveryDelayMilliseconds =
            InitialRecoveryDelayMilliseconds;
    }

    private void DrainCommands(
        IAudioEngine engine)
    {
        while (_commands.TryDequeue(
                   out RuntimeCommand? command))
        {
            Interlocked.Decrement(
                ref _pendingCommandCount);

            try
            {
                object? result =
                    command.Operation(
                        engine);

                command.Completion
                    .TrySetResult(
                        result);

                if (command.RequestShutdown)
                {
                    _shutdownRequested =
                        true;

                    return;
                }
            }
            catch (Exception ex)
            {
                command.Completion
                    .TrySetException(
                        ex);
            }
        }
    }

    private void FailPendingCommands(
        Exception exception)
    {
        while (_commands.TryDequeue(
                   out RuntimeCommand? command))
        {
            Interlocked.Decrement(
                ref _pendingCommandCount);

            command.Completion
                .TrySetException(
                    exception);
        }
    }

    private static AudioEngineRuntimeState
        ToRuntimeState(
            AudioEngineState state)
    {
        return state switch
        {
            AudioEngineState.Stopped =>
                AudioEngineRuntimeState.Stopped,

            AudioEngineState.Starting =>
                AudioEngineRuntimeState.Starting,

            AudioEngineState.Running =>
                AudioEngineRuntimeState.Running,

            AudioEngineState.Stopping =>
                AudioEngineRuntimeState.Stopping,

            AudioEngineState.Failed =>
                AudioEngineRuntimeState.Failed,

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(state))
        };
    }

    private sealed record EngineHealthSnapshot(
        AudioEngineHealth Health,
        AudioEngineState EngineState);

    private sealed record RuntimeCommand(
        Func<IAudioEngine, object?> Operation,
        TaskCompletionSource<object?> Completion,
        bool RequestShutdown);
}

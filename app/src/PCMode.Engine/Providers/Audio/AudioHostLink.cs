using System.Diagnostics;
using PCMode.Audio.Client;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Protocol;
using PCMode.Audio.Protocol.Messages;

namespace PCMode.Engine;

/// <summary>
/// PCMode's one connection to the audio Host, and the only thing allowed to
/// start one.
/// </summary>
/// <remarks>
/// WHY OWNERSHIP IS ITS OWN OBJECT
///
/// The same reason the Party mirror needed one, for a runtime that is now a
/// process rather than two threads. An <see cref="AudioProvider"/> cannot own
/// it: providers are rebuilt whenever the configuration is re-read -- saving an
/// alias from the Hardware window rebuilds the whole engine -- and a Host
/// launched by the instance that started it would be a process the newly built
/// provider knew nothing about. The shell creates this once and hands it to
/// every engine it builds.
///
/// EXACTLY ONE HOST, AND CONNECT BEFORE LAUNCH
///
/// The Host enforces single ownership itself, with a session mutex, so two of
/// them cannot split the control plane from the data plane. This side does the
/// other half: it tries to connect before it tries to launch, so a Host that
/// survived a PCMode restart is adopted rather than fought with. Launching is
/// serialized, so two transitions racing produce one process.
///
/// THE HOST OUTLIVES PCMODE ON PURPOSE
///
/// Disposing this closes PCMode's side and leaves the Host running. It has to:
/// while a target is active the PCMode virtual endpoint is the machine's
/// playback device, and a shell that killed the Host on its way out would leave
/// Windows playing into an endpoint with nothing behind it -- silence, caused by
/// closing a tray icon. A Host with no target left is idle and costs nothing,
/// and the next PCMode start reconnects to it.
/// </remarks>
public sealed class AudioHostLink : IAudioRuntime
{
    /// <summary>How long to wait for a freshly launched Host to answer.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long "there is no Host" is believed before asking again.
    /// </summary>
    /// <remarks>
    /// Asking costs a connection attempt that has to time out, and the callers
    /// are a settings page redrawing itself and a convergence poll running four
    /// times a second. Without this, a machine with no PCMode audio subsystem
    /// installed would pay that timeout on every one of them.
    ///
    /// Only the negative answer is held. A Host that answers is cheap to ask,
    /// and its Health is the thing a diagnostics page is actually watching.
    /// </remarks>
    private const int AbsenceHeldMilliseconds = 3_000;

    /// <summary>
    /// How long to wait for the Host's pipe.
    /// </summary>
    /// <remarks>
    /// Short, because the Host is a process on this machine: either its pipe is
    /// there or it is not, and a longer wait only makes "not installed" slower
    /// to discover. Launching has its own, much longer, budget.
    /// </remarks>
    private const int ConnectTimeoutMilliseconds = 500;

    private readonly Lock _gate = new();
    private readonly IAudioHostClient _client;
    private readonly Func<Process>? _launch;
    private readonly Func<bool> _installed;

    private AudioRuntimeStatus _status = AudioRuntimeStatus.Unreachable(
        "The PCMode audio Host has not been contacted yet."
    );

    /// <summary>
    /// When <see cref="_status"/> was taken, or null before the first answer.
    /// </summary>
    /// <remarks>
    /// Not <see cref="long.MinValue"/>: <see cref="Environment.TickCount64"/>
    /// minus that overflows <see cref="long"/> for every ordinary uptime, which
    /// would make the very first <see cref="Refresh"/> after construction read
    /// the gap as small and return the placeholder answer above instead of
    /// probing at all.
    /// </remarks>
    private long? _statusTaken;

    private bool _disposed;

    /// <summary>The production link: the shipped Host, over the default pipe.</summary>
    /// <remarks>
    /// Native Audio is an optional beta capability, so this link never even
    /// tries the pipe when nothing was installed -- see <see cref="_installed"/>
    /// via the private constructor below. Direct outputs are the ordinary
    /// case, and a machine that never installed the audio subsystem must not
    /// pay a connect timeout every time it switches speakers.
    /// </remarks>
    public AudioHostLink()
        : this(
            new AudioHostClient(
                HostProtocolVersion.DefaultPipeName,
                connectTimeoutMilliseconds: ConnectTimeoutMilliseconds
            ),
            () => new AudioHostLauncher(HostPath(), HostProtocolVersion.DefaultPipeName).Launch(),
            () => File.Exists(HostPath())
        ) { }

    /// <summary>
    /// Where the shipped Host is.
    /// </summary>
    /// <remarks>
    /// It is a program with its own engine, Windows and driver assemblies, so it
    /// ships in a folder of its own beside PCMode rather than flattened in with
    /// it -- see the copy step in PCMode.Tray.csproj. The flat path is still
    /// accepted, because that is what a developer running the Host by hand, and
    /// the hardware tests, produce.
    /// </remarks>
    public static string HostPath()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "AudioHost", "PCMode.Audio.Host.exe");

        return File.Exists(folder) ? folder : Path.Combine(AppContext.BaseDirectory, "PCMode.Audio.Host.exe");
    }

    /// <param name="client">How to talk to a Host.</param>
    /// <param name="launch">
    /// How to start one when nothing answers, or null for a link that will only
    /// ever adopt a Host somebody else started. Null is what a validation-only
    /// engine gets: building a provider in order to check a document must not
    /// start a process.
    /// </param>
    /// <remarks>
    /// Always considered installed: a caller handing over its own client and
    /// launcher -- a hardware test pointed at a real Host, or a fake in a unit
    /// test -- has already decided the Host is there to be reached, and this
    /// constructor must not add a filesystem check of its own guessing at where.
    /// </remarks>
    public AudioHostLink(IAudioHostClient client, Func<Process>? launch)
        : this(client, launch, installed: () => true) { }

    /// <param name="installed">
    /// Whether the Host is installed at all, checked before every connection
    /// attempt. False answers instantly, without touching the pipe: absence is
    /// the ordinary state for a machine that only uses direct outputs, and
    /// nothing about that path may cost a connect timeout.
    /// </param>
    /// <remarks>Internal so <c>PCMode.Engine.Tests</c> can prove the short-circuit without real IO.</remarks>
    internal AudioHostLink(IAudioHostClient client, Func<Process>? launch, Func<bool> installed)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(installed);

        _client = client;
        _launch = launch;
        _installed = installed;
    }

    public AudioRuntimeStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public AudioRuntimeStatus Refresh()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                // Reading is not an operation a closed link has to refuse. This
                // is on the convergence poll and on every settings redraw, both
                // of which can still be in flight while the shell is going away,
                // and neither has anything useful to do with an exception. The
                // last answer is what it knows.
                return _status;
            }

            if (
                !_status.Reachable
                && _statusTaken is long taken
                && Environment.TickCount64 - taken < AbsenceHeldMilliseconds
            )
            {
                // Recently established that there is no Host. See
                // AbsenceHeldMilliseconds.
                return _status;
            }

            return Record(Probe());
        }
    }

    /// <summary>
    /// Straight to the Host's dedicated waveform request, bypassing
    /// <see cref="_gate"/>, <see cref="Probe"/> and the absence-caching
    /// <see cref="Refresh"/> relies on: those exist to protect a settings
    /// page's occasional redraw, not a waveform's many-times-a-second one,
    /// and serializing this behind the same lock as <see cref="Apply"/> would
    /// make an open Audio page's redraw loop wait on whatever the user is
    /// doing to the target at that moment, and vice versa.
    /// </summary>
    public async Task<IReadOnlyList<AudioRouteWaveform>> WaveformAsync(CancellationToken cancellationToken)
    {
        if (_disposed || !_installed())
        {
            return [];
        }

        try
        {
            return await _client.GetWaveformAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // No Host, or it did not answer in time: the same as nothing
            // playing, from a waveform's point of view. A redraw loop ticking
            // many times a second must not throw the caller into its own
            // error handling over what is, for this purpose, silence.
            return [];
        }
    }

    public void Apply(AudioTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            cancellationToken.ThrowIfCancellationRequested();

            AudioRuntimeStatus current = Record(Probe());

            if (!current.Reachable)
            {
                current = Launch(current.Problem, cancellationToken);
            }

            try
            {
                Wait(token => _client.ApplyTargetAsync(target, token), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Record(current);
                throw;
            }
            catch (Exception exception)
            {
                Record(Probe());

                throw new InvalidOperationException(
                    $"The PCMode audio Host could not play '{target.Name}'. {Explain(exception)}",
                    exception
                );
            }

            Record(Probe());
        }
    }

    public void Recalibrate(
        AudioEndpointId endpointId,
        float volume,
        bool muted,
        TimeSpan delay,
        AudioProcessingProfile processing,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(processing);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            cancellationToken.ThrowIfCancellationRequested();

            if (!Record(Probe()).Reachable)
            {
                throw new InvalidOperationException(
                    "The PCMode audio Host is not running, so nothing can be calibrated."
                );
            }

            try
            {
                Wait(
                    token => _client.RecalibrateRouteAsync(endpointId, volume, muted, delay, processing, token),
                    cancellationToken
                );
            }
            catch (OperationCanceledException)
            {
                Record(Probe());
                throw;
            }
            catch (Exception exception)
            {
                Record(Probe());

                throw new InvalidOperationException(
                    $"The PCMode audio Host could not calibrate '{endpointId}'. {Explain(exception)}",
                    exception
                );
            }

            Record(Probe());
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!Record(Probe()).Reachable)
            {
                // Nothing is running, because nothing is there. Recorded rather
                // than thrown: this is called on the way to applying a plain
                // device alias, and that still has to happen.
                return;
            }

            try
            {
                Wait(token => _client.StopAsync(token), CancellationToken.None);
            }
            catch (Exception exception)
            {
                Record(
                    AudioRuntimeStatus.Unreachable($"The PCMode audio Host did not stop cleanly. {Explain(exception)}")
                );

                return;
            }

            Record(Probe());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    /// <summary>Remembers an answer, and when it was taken.</summary>
    private AudioRuntimeStatus Record(AudioRuntimeStatus status)
    {
        _statusTaken = Environment.TickCount64;

        return _status = status;
    }

    /// <summary>Asks the Host what it is doing, and never starts one.</summary>
    private AudioRuntimeStatus Probe()
    {
        if (!_installed())
        {
            // Not there, and said so without a connect attempt. This is the
            // ordinary state for a machine using only direct outputs, so it must
            // not cost the same timeout as a Host that failed to answer.
            return AudioRuntimeStatus.Unreachable(
                "The PCMode audio Host is not installed. Native Audio is an optional beta capability; "
                    + "direct outputs do not need it."
            );
        }

        try
        {
            AudioHostStatus host = Wait(token => _client.GetStatusAsync(token), CancellationToken.None);
            AudioEngineHealth? health = null;

            try
            {
                health = Wait(token => _client.GetHealthAsync(token), CancellationToken.None);
            }
            catch (Exception)
            {
                // A Host that answers its status and not its Health is still a
                // Host. Reporting it as unreachable would hide the one fact this
                // call did establish.
            }

            return new AudioRuntimeStatus(true, null, host, health);
        }
        catch (Exception exception)
        {
            return AudioRuntimeStatus.Unreachable(Explain(exception));
        }
    }

    /// <summary>
    /// Starts a Host and waits for it to answer, or explains why PCMode cannot.
    /// </summary>
    private AudioRuntimeStatus Launch(string? problem, CancellationToken cancellationToken)
    {
        if (_launch is null)
        {
            throw new InvalidOperationException(
                $"The PCMode audio Host is not running and this engine may not start one. {problem}"
            );
        }

        Process process;

        try
        {
            process = _launch();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "The PCMode audio Host could not be started. "
                    + $"{Explain(exception)} PCMode audio targets need the PCMode audio subsystem installed; "
                    + "single outputs work without it.",
                exception
            );
        }

        var waited = Stopwatch.StartNew();

        while (waited.Elapsed < StartupTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                // The likeliest cause by far is that a Host was already running
                // and this one refused to be a second: see the session mutex in
                // PCMode.Audio.Host. One more read settles which it was.
                AudioRuntimeStatus adopted = Probe();

                if (adopted.Reachable)
                {
                    return adopted;
                }

                throw new InvalidOperationException(
                    $"The PCMode audio Host stopped immediately (exit code {process.ExitCode})."
                );
            }

            AudioRuntimeStatus current = Probe();

            if (current.Reachable)
            {
                return current;
            }

            Thread.Sleep(100);
        }

        // Still running but never answered: left alone, it would keep the
        // driver's single-consumer claim forever, and every later launch
        // (this engine's own retry included) would fail the same way until
        // somebody found and killed it by hand.
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
        catch { }

        throw new InvalidOperationException(
            "The PCMode audio Host was started and did not answer within " + $"{StartupTimeout.TotalSeconds:0} seconds."
        );
    }

    /// <summary>
    /// One exception, in the terms the person reading it can act on.
    /// </summary>
    /// <remarks>
    /// A protocol mismatch is not the same fault as no Host at all, and neither
    /// is the same as a Host that refused the target -- so they are told apart
    /// here rather than folded into "audio failed".
    /// </remarks>
    private static string Explain(Exception exception) =>
        exception switch
        {
            AudioHostProtocolException protocol =>
                $"The PCMode audio Host speaks a different version of the control protocol. {protocol.Message}",
            AudioHostRequestException request => request.Message,
            TimeoutException => "The PCMode audio Host is not running.",
            FileNotFoundException => "PCMode.Audio.Host.exe was not found beside PCMode.",
            AggregateException aggregate when aggregate.InnerException is not null => Explain(aggregate.InnerException),
            _ => exception.Message,
        };

    /// <summary>
    /// Runs one pipe exchange to completion, from whichever thread asked.
    /// </summary>
    /// <remarks>
    /// <see cref="Task.Run(Func{Task})"/>, and not a bare wait on a task the
    /// client has already started, and the difference matters. A resource
    /// provider's Apply is synchronous, so this blocks -- and the settings page
    /// calls it from the window's own thread. Starting the asynchronous work
    /// there would capture that thread's synchronization context, so every
    /// continuation inside the client would be posted back to a thread that is
    /// blocked waiting for them, and the window would hang for good. Started on
    /// the thread pool there is no context to capture, and nothing the blocked
    /// thread has to run before it can return.
    /// </remarks>
    private static void Wait(Func<CancellationToken, Task> operation, CancellationToken cancellationToken) =>
        Task.Run(() => operation(cancellationToken), cancellationToken).GetAwaiter().GetResult();

    private static T Wait<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        Task.Run(() => operation(cancellationToken), cancellationToken).GetAwaiter().GetResult();
}

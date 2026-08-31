using PCMode.Audio.Core.Models;
using PCMode.Audio.Core.Processing;
using PCMode.Audio.Engine.Contracts;
using PCMode.Audio.Engine.Health;

namespace PCMode.Audio.Engine.Runtime;

public sealed class AudioEngine :
    IAudioEngine
{
    private readonly IAudioInputStreamFactory _inputFactory;

    private readonly IAudioOutputStreamFactory _outputFactory;

    private readonly int _blockFrameCapacity;

    private readonly AudioHealthTracker _health =
        new();

    private AudioRouteHealthWriter[] _routeHealthWriters =
        [];

    private AudioGraph? _graph;

    private AudioTarget? _activeTarget;

    private bool _disposed;

    public AudioEngineState State { get; private set; } =
        AudioEngineState.Stopped;

    public AudioTarget? ActiveTarget =>
        _activeTarget;

    public bool NeedsRecovery
    {
        get
        {
            if (State ==
                AudioEngineState.Failed)
            {
                return _activeTarget is not null;
            }

            if (State !=
                    AudioEngineState.Running ||
                _graph is null ||
                _activeTarget is null)
            {
                return false;
            }

            if (_graph.InputFailed)
            {
                return true;
            }

            for (int i = 0;
                 i < _activeTarget.Routes.Count;
                 i++)
            {
                if (!_graph.HasOperationalRoute(
                        _activeTarget.Routes[i]
                            .EndpointId))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public AudioEngine(
        IAudioInputStreamFactory inputFactory,
        IAudioOutputStreamFactory outputFactory,
        int blockFrameCapacity = 480)
    {
        ArgumentNullException.ThrowIfNull(
            inputFactory);

        ArgumentNullException.ThrowIfNull(
            outputFactory);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            blockFrameCapacity);

        _inputFactory =
            inputFactory;

        _outputFactory =
            outputFactory;

        _blockFrameCapacity =
            blockFrameCapacity;

        _health.SetEngineState(
            AudioEngineState.Stopped);
    }

    public AudioEngineHealth GetHealth()
    {
        return _health.GetHealth();
    }

    public void Apply(
        AudioTarget target)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(
            target);

        ValidateTarget(
            target);

        bool equivalentActiveTarget =
            _graph is not null &&
            _activeTarget is not null &&
            TargetsAreEquivalent(
                _activeTarget,
                target);

        if (equivalentActiveTarget)
        {
            if (State ==
                    AudioEngineState.Running &&
                !NeedsRecovery)
            {
                _activeTarget =
                    target;

                _health.SetActiveTarget(
                    target);

                return;
            }

            if (!_graph!.InputFailed &&
                _routeHealthWriters.Length ==
                    target.Routes.Count)
            {
                RecoverEquivalentTarget(
                    target);

                return;
            }
        }

        DisposeCurrentGraph();

        State =
            AudioEngineState.Starting;

        _health.SetEngineState(
            State);

        _routeHealthWriters =
            _health.BeginTarget(
                target);

        IAudioInputStream? input =
            null;

        List<AudioRouteRuntime>? routeRuntimes =
            null;

        AudioGraph? graph =
            null;

        try
        {
            input =
                _inputFactory.Create();

            routeRuntimes =
                new List<AudioRouteRuntime>(
                    target.Routes.Count);

            for (int i = 0;
                 i < target.Routes.Count;
                 i++)
            {
                AudioRoute route =
                    target.Routes[i];

                AudioRouteHealthWriter routeHealth =
                    _routeHealthWriters[i];

                IAudioOutputStream? output =
                    null;

                try
                {
                    output =
                        _outputFactory.Create(
                            route.EndpointId,
                            input.Format);

                    if (output.Format !=
                        input.Format)
                    {
                        throw new NotSupportedException(
                            $"Output route '{route.EndpointId}' uses " +
                            $"{output.Format.SampleRate} Hz / " +
                            $"{output.Format.ChannelCount} channel(s), " +
                            $"while the input uses " +
                            $"{input.Format.SampleRate} Hz / " +
                            $"{input.Format.ChannelCount} channel(s).");
                    }

                    RouteProcessing processing =
                        CreateProcessingChain(
                            route,
                            input.Format);

                    var runtime =
                        new AudioRouteRuntime(
                            route,
                            output,
                            processing.Chain,
                            processing.Gain,
                            processing.ParametricEq,
                            _blockFrameCapacity,
                            routeHealth,
                            fadeInOnFirstStart:
                                equivalentActiveTarget);

                    output =
                        null;

                    routeRuntimes.Add(
                        runtime);
                }
                catch (Exception ex)
                {
                    TryDispose(
                        output);

                    routeHealth.MarkUnavailable(
                        ex);

                    if (route.Requirement ==
                        AudioRouteRequirement.Required)
                    {
                        if (ex is NotSupportedException)
                        {
                            throw;
                        }

                        throw new InvalidOperationException(
                            $"Failed to create required audio route '{route.EndpointId}'.",
                            ex);
                    }

                    // Optional route remains represented in Health,
                    // but is not added to the live graph.
                }
            }

            if (routeRuntimes.Count == 0)
            {
                throw new InvalidOperationException(
                    "No configured audio route could be created.");
            }

            graph =
                new AudioGraph(
                    input,
                    routeRuntimes,
                    _blockFrameCapacity);

            input =
                null;

            routeRuntimes =
                null;

            graph.Start();

            _graph =
                graph;

            graph =
                null;

            _activeTarget =
                target;

            _health.SetActiveTarget(
                target);

            State =
                AudioEngineState.Running;

            _health.SetEngineState(
                State);
        }
        catch (Exception ex)
        {
            TryDispose(
                graph);

            if (graph is null &&
                routeRuntimes is not null)
            {
                for (int i =
                         routeRuntimes.Count - 1;
                     i >= 0;
                     i--)
                {
                    TryDispose(
                        routeRuntimes[i]);
                }
            }

            TryDispose(
                input);

            _graph =
                null;

            _activeTarget =
                null;

            _health.SetActiveTarget(
                null);

            State =
                AudioEngineState.Failed;

            _health.RecordEngineFailure(
                ex);

            _health.SetEngineState(
                State);

            throw;
        }
    }

    /// <summary>
    /// Changes one running route's gain, mute, endpoint volume, parametric EQ
    /// and delay while the target that owns it keeps playing.
    /// </summary>
    /// <remarks>
    /// Gain and EQ change live, with no interruption: <see cref="AudioRouteRuntime.SetGain"/>
    /// and <see cref="AudioRouteRuntime.SetParametricEq"/> glide to their new
    /// values in place. Delay cannot: the ring buffer that gives a route its
    /// delay is sized once, when the route starts, so a different delay needs
    /// a different buffer. In that case this route alone is rebuilt through
    /// <see cref="AudioGraph.ReplaceOrAddRoute"/> -- the exact mechanism
    /// reconnect recovery already uses -- and every other route and the
    /// shared input are left untouched.
    ///
    /// The active target is updated to the calibrated values whether or not
    /// the rebuild succeeds, so a route that failed mid-calibration is
    /// reconnected by the bounded recovery loop with the values that were
    /// asked for rather than the ones it was originally started with.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// No target is running, the endpoint named is not one of its routes, or
    /// that route is not currently operational.
    /// </exception>
    public void Recalibrate(
        AudioEndpointId endpointId,
        float volume,
        bool muted,
        float gainDb,
        TimeSpan delay,
        ParametricEqProfile? parametricEq)
    {
        ThrowIfDisposed();

        if (State !=
                AudioEngineState.Running ||
            _graph is null ||
            _activeTarget is null)
        {
            throw new InvalidOperationException(
                "No audio target is running, so there is nothing to calibrate.");
        }

        int routeIndex =
            -1;

        for (int i = 0;
             i < _activeTarget.Routes.Count;
             i++)
        {
            if (_activeTarget.Routes[i]
                    .EndpointId ==
                endpointId)
            {
                routeIndex =
                    i;

                break;
            }
        }

        if (routeIndex < 0)
        {
            throw new InvalidOperationException(
                $"'{endpointId}' is not one of the routes the running target uses.");
        }

        AudioRoute currentRoute =
            _activeTarget.Routes[
                routeIndex];

        var newRoute =
            new AudioRoute(
                endpointId,
                currentRoute.Requirement,
                volume,
                muted,
                delay,
                new AudioProcessingProfile(
                    gainDb,
                    parametricEq));

        AudioRouteRuntime? runtime =
            FindRuntime(
                endpointId);

        if (runtime is null ||
            !runtime.IsOperational)
        {
            throw new InvalidOperationException(
                $"'{endpointId}' is not currently playing, so it cannot be calibrated live.");
        }

        try
        {
            if (runtime.Route.Delay !=
                delay)
            {
                RebuildRouteInPlace(
                    routeIndex,
                    newRoute);
            }
            else
            {
                runtime.SetGain(
                    gainDb,
                    volume,
                    muted);

                runtime.SetParametricEq(
                    parametricEq);
            }
        }
        finally
        {
            var updatedRoutes =
                _activeTarget.Routes
                    .ToArray();

            updatedRoutes[routeIndex] =
                newRoute;

            _activeTarget =
                new AudioTarget(
                    _activeTarget.Id,
                    _activeTarget.Name,
                    updatedRoutes);

            _health.SetActiveTarget(
                _activeTarget);
        }
    }

    public int ProcessNextBlock()
    {
        ThrowIfDisposed();

        if (State !=
                AudioEngineState.Running ||
            _graph is null)
        {
            throw new InvalidOperationException(
                "The audio engine is not running.");
        }

        try
        {
            return _graph
                .ProcessNextBlock();
        }
        catch (Exception ex)
        {
            State =
                AudioEngineState.Failed;

            _health.RecordEngineFailure(
                ex);

            _health.SetEngineState(
                State);

            throw;
        }
    }

    public void Stop()
    {
        ThrowIfDisposed();

        if (_graph is null)
        {
            _activeTarget =
                null;

            _health.SetActiveTarget(
                null);

            State =
                AudioEngineState.Stopped;

            _health.SetEngineState(
                State);

            return;
        }

        State =
            AudioEngineState.Stopping;

        _health.SetEngineState(
            State);

        AudioGraph graph =
            _graph;

        _graph =
            null;

        _activeTarget =
            null;

        _health.SetActiveTarget(
            null);

        try
        {
            graph.Dispose();

            State =
                AudioEngineState.Stopped;

            _health.SetEngineState(
                State);
        }
        catch (Exception ex)
        {
            State =
                AudioEngineState.Failed;

            _health.RecordEngineFailure(
                ex);

            _health.SetEngineState(
                State);

            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_graph is not null)
            {
                Stop();
            }
        }
        finally
        {
            _disposed =
                true;
        }
    }

    /// <summary>
    /// One route's chain, together with the two live-tunable processors
    /// inside it.
    /// </summary>
    /// <remarks>
    /// Every route gets a <see cref="ParametricEqProcessor"/> whether or not
    /// its configured profile has any bands -- a flat one is effectively a
    /// passthrough -- precisely so that switching a running route onto a
    /// named profile later is always a live <see cref="AudioRouteRuntime.SetParametricEq"/>
    /// call rather than something that only works for a route built with EQ
    /// already selected.
    /// </remarks>
    private readonly record struct RouteProcessing(
        AudioProcessingChain Chain,
        GainProcessor Gain,
        ParametricEqProcessor ParametricEq);

    private static RouteProcessing
        CreateProcessingChain(
            AudioRoute route,
            AudioFormat format)
    {
        var parametricEq =
            new ParametricEqProcessor(
                route.Processing.ParametricEq ??
                ParametricEqProfile.Flat,
                format);

        var gain =
            new GainProcessor(
                route.Processing.GainDb,
                route.Volume,
                route.Muted);

        var chain =
            new AudioProcessingChain(
                parametricEq,
                gain);

        return new RouteProcessing(
            chain,
            gain,
            parametricEq);
    }

    private void RecoverEquivalentTarget(
        AudioTarget target)
    {
        AudioGraph graph =
            _graph
            ?? throw new InvalidOperationException(
                "No audio graph is available for route recovery.");

        State =
            AudioEngineState.Starting;

        _health.SetEngineState(
            State);

        try
        {
            for (int i = 0;
                 i < target.Routes.Count;
                 i++)
            {
                AudioRoute route =
                    target.Routes[i];

                if (graph.HasOperationalRoute(
                        route.EndpointId))
                {
                    continue;
                }

                AudioRouteHealthWriter routeHealth =
                    _routeHealthWriters[i];

                IAudioOutputStream? output =
                    null;

                try
                {
                    output =
                        _outputFactory.Create(
                            route.EndpointId,
                            graph.Format);

                    if (output.Format !=
                        graph.Format)
                    {
                        throw new NotSupportedException(
                            $"Output route '{route.EndpointId}' cannot consume the engine nominal format.");
                    }

                    RouteProcessing processing =
                        CreateProcessingChain(
                            route,
                            graph.Format);

                    var replacement =
                        new AudioRouteRuntime(
                            route,
                            output,
                            processing.Chain,
                            processing.Gain,
                            processing.ParametricEq,
                            _blockFrameCapacity,
                            routeHealth,
                            fadeInOnFirstStart:
                                true);

                    output =
                        null;

                    graph.ReplaceOrAddRoute(
                        replacement);
                }
                catch (Exception ex)
                {
                    TryDispose(
                        output);

                    routeHealth.MarkUnavailable(
                        ex);

                    // Required is enforced when a target first starts. Once a
                    // graph is live, promoting a single endpoint's failed
                    // reopen to an engine failure would pause every healthy
                    // sibling until the endpoint returned. Keep the graph
                    // degraded and let the Host's bounded retry loop converge
                    // this route independently.
                }
            }

            if (!graph.HasAnyOperationalRoute)
            {
                throw new InvalidOperationException(
                    "No audio route remains operational while recovering the active target.");
            }

            _activeTarget =
                target;

            _health.SetActiveTarget(
                target);

            State =
                AudioEngineState.Running;

            _health.SetEngineState(
                State);
        }
        catch (Exception ex)
        {
            _activeTarget =
                target;

            _health.SetActiveTarget(
                target);

            State =
                AudioEngineState.Failed;

            _health.RecordEngineFailure(
                ex);

            _health.SetEngineState(
                State);

            throw;
        }
    }

    private AudioRouteRuntime? FindRuntime(
        AudioEndpointId endpointId)
    {
        AudioGraph? graph =
            _graph;

        if (graph is null)
        {
            return null;
        }

        foreach (AudioRouteRuntime candidate in
            graph.Routes)
        {
            if (candidate.Route.EndpointId ==
                endpointId)
            {
                return candidate;
            }
        }

        return null;
    }

    private void RebuildRouteInPlace(
        int routeIndex,
        AudioRoute newRoute)
    {
        AudioGraph graph =
            _graph!;

        AudioRouteHealthWriter routeHealth =
            _routeHealthWriters[
                routeIndex];

        IAudioOutputStream? output =
            null;

        try
        {
            output =
                _outputFactory.Create(
                    newRoute.EndpointId,
                    graph.Format);

            if (output.Format !=
                graph.Format)
            {
                throw new NotSupportedException(
                    $"Output route '{newRoute.EndpointId}' cannot consume the engine nominal format.");
            }

            RouteProcessing processing =
                CreateProcessingChain(
                    newRoute,
                    graph.Format);

            var replacement =
                new AudioRouteRuntime(
                    newRoute,
                    output,
                    processing.Chain,
                    processing.Gain,
                    processing.ParametricEq,
                    _blockFrameCapacity,
                    routeHealth,
                    fadeInOnFirstStart:
                        true);

            output =
                null;

            graph.ReplaceOrAddRoute(
                replacement);
        }
        catch (Exception ex)
        {
            TryDispose(
                output);

            routeHealth.MarkUnavailable(
                ex);

            throw new InvalidOperationException(
                $"Could not recalibrate audio route '{newRoute.EndpointId}'.",
                ex);
        }
    }

    private void DisposeCurrentGraph()
    {
        if (_graph is null)
        {
            return;
        }

        AudioGraph graph =
            _graph;

        _graph =
            null;

        _activeTarget =
            null;

        _health.SetActiveTarget(
            null);

        graph.Dispose();
    }

    private static void ValidateTarget(
        AudioTarget target)
    {
        var endpointIds =
            new HashSet<AudioEndpointId>();

        for (int i = 0;
             i < target.Routes.Count;
             i++)
        {
            AudioRoute route =
                target.Routes[i];

            if (!endpointIds.Add(
                    route.EndpointId))
            {
                throw new ArgumentException(
                    $"Audio target contains duplicate endpoint '{route.EndpointId}'.",
                    nameof(target));
            }

        }
    }

    private static bool TargetsAreEquivalent(
        AudioTarget first,
        AudioTarget second)
    {
        if (!string.Equals(
                first.Id,
                second.Id,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (first.Routes.Count !=
            second.Routes.Count)
        {
            return false;
        }

        for (int i = 0;
             i < first.Routes.Count;
             i++)
        {
            AudioRoute a =
                first.Routes[i];

            AudioRoute b =
                second.Routes[i];

            if (a.EndpointId !=
                    b.EndpointId ||
                a.Requirement !=
                    b.Requirement ||
                a.Volume !=
                    b.Volume ||
                a.Muted !=
                    b.Muted ||
                a.Delay !=
                    b.Delay ||
                !Equals(
                    a.Processing,
                    b.Processing))
            {
                return false;
            }
        }

        return true;
    }

    private static void TryDispose(
        IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch
        {
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }
}

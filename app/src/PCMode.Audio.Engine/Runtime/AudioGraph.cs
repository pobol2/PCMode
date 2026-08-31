using PCMode.Audio.Core.Buffers;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Engine.Contracts;

namespace PCMode.Audio.Engine.Runtime;

public sealed class AudioGraph :
    IDisposable
{
    private readonly IAudioInputStream _input;

    private AudioRouteRuntime[] _routes;

    private readonly float[] _inputSamples;

    private long _framePosition;

    private bool _started;

    private bool _disposed;

    public AudioFormat Format =>
        _input.Format;

    public int BlockFrameCapacity { get; }

    public IReadOnlyList<AudioRouteRuntime> Routes =>
        _routes;

    public bool InputFailed { get; private set; }

    public bool HasAnyOperationalRoute =>
        _routes.Any(
            route =>
                route.IsOperational);

    public AudioGraph(
        IAudioInputStream input,
        IEnumerable<AudioRouteRuntime> routes,
        int blockFrameCapacity)
    {
        ArgumentNullException.ThrowIfNull(
            input);

        ArgumentNullException.ThrowIfNull(
            routes);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            blockFrameCapacity);

        _input =
            input;

        _routes =
            routes.ToArray();

        if (_routes.Length == 0)
        {
            throw new ArgumentException(
                "An audio graph must contain at least one configured runtime route.",
                nameof(routes));
        }

        for (int i = 0;
             i < _routes.Length;
             i++)
        {
            if (_routes[i].Format !=
                input.Format)
            {
                throw new NotSupportedException(
                    $"Route '{_routes[i].Route.EndpointId}' does not match the input nominal format.");
            }
        }

        BlockFrameCapacity =
            blockFrameCapacity;

        _inputSamples =
            new float[
                checked(
                    blockFrameCapacity *
                    input.Format.ChannelCount)];
    }

    public void Start()
    {
        ThrowIfDisposed();

        if (_started)
        {
            return;
        }

        int runningRoutes =
            0;

        try
        {
            for (int i = 0;
                 i < _routes.Length;
                 i++)
            {
                AudioRouteRuntime route =
                    _routes[i];

                try
                {
                    route.Start();

                    runningRoutes++;
                }
                catch (Exception ex)
                {
                    if (route.Route.Requirement ==
                        AudioRouteRequirement.Required)
                    {
                        throw new InvalidOperationException(
                            $"Required audio route '{route.Route.EndpointId}' failed to start.",
                            ex);
                    }

                    // Optional route failure is preserved in Health and
                    // can be recovered later without replacing the input
                    // or unrelated healthy routes.
                }
            }

            if (runningRoutes == 0)
            {
                throw new InvalidOperationException(
                    "No configured audio route could be started.");
            }

            try
            {
                _input.Start();
            }
            catch (Exception ex)
            {
                InputFailed =
                    true;

                throw new InvalidOperationException(
                    "Failed to start the shared audio input stream.",
                    ex);
            }

            InputFailed =
                false;

            _started =
                true;
        }
        catch
        {
            for (int i =
                     _routes.Length - 1;
                 i >= 0;
                 i--)
            {
                TryStopRoute(
                    _routes[i]);
            }

            TryStopInput();

            throw;
        }
    }

    public int ProcessNextBlock()
    {
        ThrowIfDisposed();

        if (!_started)
        {
            throw new InvalidOperationException(
                "The audio graph must be started before processing.");
        }

        int frameCount;

        try
        {
            frameCount =
                _input.Read(
                    _inputSamples);
        }
        catch
        {
            InputFailed =
                true;

            throw;
        }

        if (frameCount < 0 ||
            frameCount >
                BlockFrameCapacity)
        {
            InputFailed =
                true;

            throw new InvalidOperationException(
                "The input stream returned an invalid frame count.");
        }

        if (frameCount == 0)
        {
            return 0;
        }

        int sampleCount =
            checked(
                frameCount *
                Format.ChannelCount);

        long blockFramePosition =
            _framePosition;

        _framePosition =
            checked(
                _framePosition +
                frameCount);

        var block =
            new AudioBlock(
                _inputSamples.AsSpan(
                    0,
                    sampleCount),
                Format,
                blockFramePosition);

        for (int i = 0;
             i < _routes.Length;
             i++)
        {
            AudioRouteRuntime route =
                _routes[i];

            if (!route.IsOperational)
            {
                continue;
            }

            try
            {
                route.Process(
                    block);
            }
            catch
            {
                // The route runtime has marked itself failed. Keep consuming
                // the shared input and feeding every other route; Required
                // controls whether target startup/recovery may succeed without
                // this endpoint, not whether a healthy sibling should be
                // interrupted by this endpoint's hot-plug failure.
            }
        }

        return frameCount;
    }

    public bool HasOperationalRoute(
        AudioEndpointId endpointId)
    {
        for (int i = 0;
             i < _routes.Length;
             i++)
        {
            AudioRouteRuntime route =
                _routes[i];

            if (route.Route.EndpointId ==
                    endpointId &&
                route.IsOperational)
            {
                return true;
            }
        }

        return false;
    }

    public void ReplaceOrAddRoute(
        AudioRouteRuntime replacement)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(
            replacement);

        if (!_started)
        {
            throw new InvalidOperationException(
                "The audio graph must be running before a route can be recovered.");
        }

        if (replacement.Format !=
            Format)
        {
            throw new NotSupportedException(
                "Recovered route does not match the graph nominal format.");
        }

        int existingIndex =
            -1;

        for (int i = 0;
             i < _routes.Length;
             i++)
        {
            if (_routes[i]
                    .Route.EndpointId ==
                replacement.Route.EndpointId)
            {
                existingIndex =
                    i;

                break;
            }
        }

        if (existingIndex >= 0)
        {
            // The previous route is already non-operational when this
            // method is used. Dispose it before the replacement starts so
            // its cleanup cannot overwrite the shared Health writer after
            // the new route has reported Running.
            TryDisposeRoute(
                _routes[existingIndex]);
        }

        try
        {
            replacement.Start();
        }
        catch
        {
            TryDisposeRoute(
                replacement);

            throw;
        }

        if (existingIndex >= 0)
        {
            _routes[existingIndex] =
                replacement;

            return;
        }

        int oldLength =
            _routes.Length;

        Array.Resize(
            ref _routes,
            oldLength + 1);

        _routes[oldLength] =
            replacement;
    }

    public void Stop()
    {
        ThrowIfDisposed();

        if (!_started)
        {
            return;
        }

        var errors =
            new List<Exception>();

        try
        {
            _input.Stop();
        }
        catch (Exception ex)
        {
            errors.Add(
                new InvalidOperationException(
                    "Failed to stop the shared audio input stream.",
                    ex));
        }

        for (int i =
                 _routes.Length - 1;
             i >= 0;
             i--)
        {
            AudioRouteRuntime route =
                _routes[i];

            try
            {
                route.Stop();
            }
            catch (Exception ex)
            {
                errors.Add(
                    new InvalidOperationException(
                        $"Failed to stop audio route '{route.Route.EndpointId}'.",
                        ex));
            }
        }

        _started =
            false;

        if (errors.Count > 0)
        {
            throw new AggregateException(
                "One or more audio graph components failed to stop.",
                errors);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var errors =
            new List<Exception>();

        if (_started)
        {
            try
            {
                Stop();
            }
            catch (AggregateException ex)
            {
                errors.AddRange(
                    ex.InnerExceptions);
            }
            catch (Exception ex)
            {
                errors.Add(
                    ex);
            }
        }

        for (int i =
                 _routes.Length - 1;
             i >= 0;
             i--)
        {
            try
            {
                _routes[i].Dispose();
            }
            catch (Exception ex)
            {
                errors.Add(
                    new InvalidOperationException(
                        $"Failed to dispose audio route '{_routes[i].Route.EndpointId}'.",
                        ex));
            }
        }

        try
        {
            _input.Dispose();
        }
        catch (Exception ex)
        {
            errors.Add(
                new InvalidOperationException(
                    "Failed to dispose the shared audio input stream.",
                    ex));
        }

        _disposed =
            true;

        if (errors.Count > 0)
        {
            throw new AggregateException(
                "One or more audio graph components failed to dispose.",
                errors);
        }
    }

    private void TryStopInput()
    {
        try
        {
            _input.Stop();
        }
        catch
        {
        }
    }

    private static void TryStopRoute(
        AudioRouteRuntime route)
    {
        try
        {
            route.Stop();
        }
        catch
        {
        }
    }

    private static void TryDisposeRoute(
        AudioRouteRuntime route)
    {
        try
        {
            route.Dispose();
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

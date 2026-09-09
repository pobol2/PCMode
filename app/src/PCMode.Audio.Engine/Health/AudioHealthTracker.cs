using PCMode.Audio.Core.Buffers;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Engine.Contracts;
using PCMode.Audio.Engine.Runtime;

namespace PCMode.Audio.Engine.Health;

internal sealed class AudioHealthTracker
{
    private AudioRouteHealthWriter[] _routes = [];

    private AudioTarget? _activeTarget;

    private AudioFailure? _lastFailure;

    private int _engineState = (int)AudioEngineState.Stopped;

    public AudioRouteHealthWriter[] BeginTarget(AudioTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var routes = new AudioRouteHealthWriter[target.Routes.Count];

        for (int i = 0; i < routes.Length; i++)
        {
            routes[i] = new AudioRouteHealthWriter(target.Routes[i]);
        }

        Volatile.Write(ref _routes, routes);

        Volatile.Write(ref _activeTarget, null);

        Volatile.Write(ref _lastFailure, null);

        return routes;
    }

    public void SetActiveTarget(AudioTarget? target)
    {
        Volatile.Write(ref _activeTarget, target);
    }

    public void SetEngineState(AudioEngineState state)
    {
        Volatile.Write(ref _engineState, (int)state);
    }

    public void RecordEngineFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Volatile.Write(ref _lastFailure, AudioFailure.FromException(exception));
    }

    public AudioEngineHealth GetHealth()
    {
        AudioRuntimeSnapshot snapshot = GetSnapshot();

        var routes = new AudioRouteHealth[snapshot.Routes.Count];

        for (int i = 0; i < routes.Length; i++)
        {
            AudioRouteRuntimeSnapshot route = snapshot.Routes[i];

            AudioRouteHealthState state = MapRouteState(route);

            routes[i] = new AudioRouteHealth(
                route.Route.EndpointId,
                route.Route.Requirement,
                state,
                route.EndpointState,
                route.Format,
                route.Route.Delay,
                route.ClockOffsetFrames,
                route.DriftCorrectionPpm,
                route.ResampleRatio,
                route.IsResynchronizing,
                route.UnderrunCount,
                route.ResyncCount,
                route.StallRecoveryCount,
                route.LongestProcessGapMilliseconds,
                route.OutputDiagnostics.RecoveryFadeCount,
                route.OutputDiagnostics.EndpointDryCount,
                route.OutputDiagnostics.LongestSubmitGapMilliseconds,
                route.OutputDiagnostics.LongestWriteMilliseconds,
                route.OutputDiagnostics.LongestBufferWaitMilliseconds,
                route.Route.Processing,
                route.LastFailure
            );
        }

        AudioTarget? target = snapshot.ActiveTarget;

        return new AudioEngineHealth(
            MapEngineState(snapshot.EngineState),
            target?.Id,
            target?.Name,
            routes,
            snapshot.LastFailure
        );
    }

    private AudioRuntimeSnapshot GetSnapshot()
    {
        AudioRouteHealthWriter[] routeWriters = Volatile.Read(ref _routes);

        var routes = new AudioRouteRuntimeSnapshot[routeWriters.Length];

        for (int i = 0; i < routes.Length; i++)
        {
            routes[i] = routeWriters[i].GetSnapshot();
        }

        return new AudioRuntimeSnapshot(
            (AudioEngineState)Volatile.Read(ref _engineState),
            Volatile.Read(ref _activeTarget),
            routes,
            Volatile.Read(ref _lastFailure)
        );
    }

    private static AudioEngineRuntimeState MapEngineState(AudioEngineState state)
    {
        return state switch
        {
            AudioEngineState.Stopped => AudioEngineRuntimeState.Stopped,

            AudioEngineState.Starting => AudioEngineRuntimeState.Starting,

            AudioEngineState.Running => AudioEngineRuntimeState.Running,

            AudioEngineState.Stopping => AudioEngineRuntimeState.Stopping,

            AudioEngineState.Failed => AudioEngineRuntimeState.Failed,

            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
    }

    /// <summary>Each route's current waveform envelope, for the dedicated waveform poll.</summary>
    public IReadOnlyList<AudioRouteWaveform> GetWaveform()
    {
        AudioRouteHealthWriter[] routeWriters = Volatile.Read(ref _routes);

        var waveforms = new AudioRouteWaveform[routeWriters.Length];

        for (int i = 0; i < waveforms.Length; i++)
        {
            waveforms[i] = routeWriters[i].GetWaveform();
        }

        return waveforms;
    }

    private static AudioRouteHealthState MapRouteState(AudioRouteRuntimeSnapshot route)
    {
        return route.State switch
        {
            AudioRouteRuntimeHealthState.Configured => AudioRouteHealthState.Inactive,

            AudioRouteRuntimeHealthState.Starting => AudioRouteHealthState.Pending,

            AudioRouteRuntimeHealthState.Running when route.IsResynchronizing => AudioRouteHealthState.Degraded,

            AudioRouteRuntimeHealthState.Running => AudioRouteHealthState.Healthy,

            AudioRouteRuntimeHealthState.Stopped => AudioRouteHealthState.Inactive,

            AudioRouteRuntimeHealthState.Unavailable => AudioRouteHealthState.Unavailable,

            AudioRouteRuntimeHealthState.Failed => AudioRouteHealthState.Failed,

            _ => throw new ArgumentOutOfRangeException(),
        };
    }
}

internal sealed class AudioRouteHealthWriter
{
    /// <summary>
    /// Buckets in the rolling waveform envelope. Within the 128-256 range a
    /// visualization needs; the exact count is not part of any contract the
    /// wire format enforces.
    /// </summary>
    private const int WaveformBucketCount = 128;

    private readonly Lock _waveformGate = new();

    private readonly float[] _waveformMin = new float[WaveformBucketCount];

    private readonly float[] _waveformMax = new float[WaveformBucketCount];

    private int _waveformWriteIndex;

    private AudioRoute _route;

    private int _state = (int)AudioRouteRuntimeHealthState.Configured;

    private int _endpointState = (int)AudioEndpointState.Unknown;

    private int _sampleRate;

    private int _channelCount;

    private int _hasFormat;

    private long _clockOffsetBits = BitConverter.DoubleToInt64Bits(0.0);

    private long _correctionBits = BitConverter.DoubleToInt64Bits(0.0);

    private long _ratioBits = BitConverter.DoubleToInt64Bits(1.0);

    private int _isResynchronizing;

    private long _underrunCount;

    private long _resyncCount;

    private long _stallRecoveryCount;

    private long _longestProcessGapBits;

    private long _outputRecoveryFadeCount;

    private long _outputEndpointDryCount;

    private long _longestOutputSubmitGapBits;

    private long _longestOutputWriteBits;

    private long _longestOutputBufferWaitBits;

    private AudioFailure? _lastFailure;

    public AudioRouteHealthWriter(AudioRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        _route = route;
    }

    /// <summary>
    /// Points Health at the route's calibrated values after a live gain or
    /// EQ change, so a settings screen reads what is actually playing rather
    /// than what this route was originally started with.
    /// </summary>
    public void UpdateRoute(AudioRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        Volatile.Write(ref _route, route);
    }

    public void MarkPrepared(AudioFormat format)
    {
        Volatile.Write(ref _sampleRate, format.SampleRate);

        Volatile.Write(ref _channelCount, format.ChannelCount);

        Volatile.Write(ref _hasFormat, 1);
    }

    public void MarkStarting()
    {
        Volatile.Write(ref _state, (int)AudioRouteRuntimeHealthState.Starting);

        Volatile.Write(ref _endpointState, (int)AudioEndpointState.Unknown);
    }

    public void MarkRunning()
    {
        Volatile.Write(ref _lastFailure, null);

        Volatile.Write(ref _state, (int)AudioRouteRuntimeHealthState.Running);

        Volatile.Write(ref _endpointState, (int)AudioEndpointState.Active);
    }

    public void MarkStopped()
    {
        Volatile.Write(ref _state, (int)AudioRouteRuntimeHealthState.Stopped);

        Volatile.Write(ref _endpointState, (int)AudioEndpointState.Unknown);
    }

    public void MarkUnavailable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Volatile.Write(ref _lastFailure, AudioFailure.FromException(exception));

        Volatile.Write(ref _endpointState, (int)AudioEndpointState.Unavailable);

        Volatile.Write(ref _state, (int)AudioRouteRuntimeHealthState.Unavailable);
    }

    public void MarkFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Volatile.Write(ref _lastFailure, AudioFailure.FromException(exception));

        Volatile.Write(ref _endpointState, (int)AudioEndpointState.Failed);

        Volatile.Write(ref _state, (int)AudioRouteRuntimeHealthState.Failed);
    }

    public void ReportTiming(
        double clockOffsetFrames,
        double correctionPpm,
        double resampleRatio,
        bool isResynchronizing,
        long underrunCount,
        long resyncCount,
        long stallRecoveryCount,
        double longestProcessGapMilliseconds,
        AudioOutputDiagnostics outputDiagnostics
    )
    {
        Interlocked.Exchange(ref _clockOffsetBits, BitConverter.DoubleToInt64Bits(clockOffsetFrames));

        Interlocked.Exchange(ref _correctionBits, BitConverter.DoubleToInt64Bits(correctionPpm));

        Interlocked.Exchange(ref _ratioBits, BitConverter.DoubleToInt64Bits(resampleRatio));

        Volatile.Write(ref _isResynchronizing, isResynchronizing ? 1 : 0);

        Interlocked.Exchange(ref _underrunCount, underrunCount);

        Interlocked.Exchange(ref _resyncCount, resyncCount);

        Interlocked.Exchange(ref _stallRecoveryCount, stallRecoveryCount);

        Interlocked.Exchange(ref _longestProcessGapBits, BitConverter.DoubleToInt64Bits(longestProcessGapMilliseconds));

        Interlocked.Exchange(ref _outputRecoveryFadeCount, outputDiagnostics.RecoveryFadeCount);

        Interlocked.Exchange(ref _outputEndpointDryCount, outputDiagnostics.EndpointDryCount);

        Interlocked.Exchange(
            ref _longestOutputSubmitGapBits,
            BitConverter.DoubleToInt64Bits(outputDiagnostics.LongestSubmitGapMilliseconds)
        );

        Interlocked.Exchange(
            ref _longestOutputWriteBits,
            BitConverter.DoubleToInt64Bits(outputDiagnostics.LongestWriteMilliseconds)
        );

        Interlocked.Exchange(
            ref _longestOutputBufferWaitBits,
            BitConverter.DoubleToInt64Bits(outputDiagnostics.LongestBufferWaitMilliseconds)
        );
    }

    /// <summary>
    /// Folds one processed block into the rolling waveform envelope: one
    /// bucket's worth of min/max per call, which -- at the audio callback
    /// rate this is called from -- gives roughly one bucket per 10 ms.
    /// </summary>
    /// <param name="processedSamples">
    /// Interleaved samples after this route's own EQ and gain, before
    /// resampling -- what the route is actually about to play.
    /// </param>
    public void ReportWaveform(ReadOnlySpan<float> processedSamples, int channelCount)
    {
        if (processedSamples.Length == 0 || channelCount <= 0)
        {
            return;
        }

        float min = float.PositiveInfinity;

        float max = float.NegativeInfinity;

        for (int frameStart = 0; frameStart < processedSamples.Length; frameStart += channelCount)
        {
            float sum = 0f;

            for (int channel = 0; channel < channelCount; channel++)
            {
                sum += processedSamples[frameStart + channel];
            }

            float mono = sum / channelCount;

            min = Math.Min(min, mono);

            max = Math.Max(max, mono);
        }

        lock (_waveformGate)
        {
            _waveformMin[_waveformWriteIndex] = min;

            _waveformMax[_waveformWriteIndex] = max;

            _waveformWriteIndex = (_waveformWriteIndex + 1) % WaveformBucketCount;
        }
    }

    /// <summary>
    /// This route's current waveform envelope, oldest bucket first, or
    /// nothing when the route is not currently running -- see
    /// <see cref="AudioRouteWaveform.Available"/>.
    /// </summary>
    public AudioRouteWaveform GetWaveform()
    {
        bool available =
            (AudioRouteRuntimeHealthState)Volatile.Read(ref _state) == AudioRouteRuntimeHealthState.Running;

        AudioRoute route = Volatile.Read(ref _route);

        if (!available)
        {
            return new AudioRouteWaveform(route.EndpointId, false, []);
        }

        var buckets = new WaveformEnvelopeSample[WaveformBucketCount];

        lock (_waveformGate)
        {
            for (int i = 0; i < WaveformBucketCount; i++)
            {
                int sourceIndex = (_waveformWriteIndex + i) % WaveformBucketCount;

                buckets[i] = new WaveformEnvelopeSample(_waveformMin[sourceIndex], _waveformMax[sourceIndex]);
            }
        }

        return new AudioRouteWaveform(route.EndpointId, true, buckets);
    }

    public AudioRouteRuntimeSnapshot GetSnapshot()
    {
        AudioFormat? format =
            Volatile.Read(ref _hasFormat) != 0
                ? new AudioFormat(Volatile.Read(ref _sampleRate), Volatile.Read(ref _channelCount))
                : null;

        return new AudioRouteRuntimeSnapshot(
            Volatile.Read(ref _route),
            (AudioRouteRuntimeHealthState)Volatile.Read(ref _state),
            (AudioEndpointState)Volatile.Read(ref _endpointState),
            format,
            BitConverter.Int64BitsToDouble(Interlocked.Read(ref _clockOffsetBits)),
            BitConverter.Int64BitsToDouble(Interlocked.Read(ref _correctionBits)),
            BitConverter.Int64BitsToDouble(Interlocked.Read(ref _ratioBits)),
            Volatile.Read(ref _isResynchronizing) != 0,
            Interlocked.Read(ref _underrunCount),
            Interlocked.Read(ref _resyncCount),
            Interlocked.Read(ref _stallRecoveryCount),
            BitConverter.Int64BitsToDouble(Interlocked.Read(ref _longestProcessGapBits)),
            new AudioOutputDiagnostics(
                Interlocked.Read(ref _outputRecoveryFadeCount),
                Interlocked.Read(ref _outputEndpointDryCount),
                BitConverter.Int64BitsToDouble(Interlocked.Read(ref _longestOutputSubmitGapBits)),
                BitConverter.Int64BitsToDouble(Interlocked.Read(ref _longestOutputWriteBits)),
                BitConverter.Int64BitsToDouble(Interlocked.Read(ref _longestOutputBufferWaitBits))
            ),
            Volatile.Read(ref _lastFailure)
        );
    }
}

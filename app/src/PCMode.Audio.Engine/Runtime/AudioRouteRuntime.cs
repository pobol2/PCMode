using System.Diagnostics;
using PCMode.Audio.Core.Buffers;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Core.Processing;
using PCMode.Audio.Engine.Contracts;
using PCMode.Audio.Engine.Health;
using PCMode.Audio.Engine.Timing;

namespace PCMode.Audio.Engine.Runtime;

public sealed class AudioRouteRuntime :
    IDisposable
{
    /// <summary>
    /// A little over five milliseconds at 48 kHz. The first resumed sample is
    /// silence and the route reaches its configured level quickly enough that
    /// recovery does not sound like a volume change.
    /// </summary>
    public const int RestartRampFrameCount =
        256;

    private readonly IAudioOutputStream _output;

    private readonly AudioProcessingChain _processing;

    private readonly GainProcessor _gain;

    private readonly ParametricEqProcessor _parametricEq;

    private readonly Resampler _resampler;

    private readonly RouteSynchronizer _synchronizer;

    private readonly AudioRingBuffer _buffer;

    private readonly AudioRouteHealthWriter _health;

    private readonly float[] _processingSamples;

    private readonly float[] _resampledSamples;

    private readonly float[] _outputSamples;

    private readonly bool _fadeInOnFirstStart;

    private bool _started;

    private bool _hasStarted;

    private int _fadeInFramesRemaining;

    private long _lastProcessTimestamp;

    private bool _failed;

    private bool _disposed;

    public AudioRoute Route { get; private set; }

    public AudioFormat Format =>
        _output.Format;

    public int BlockFrameCapacity { get; }

    public int DelayFrameCount { get; }

    public int DelaySampleCount =>
        checked(
            DelayFrameCount *
            Format.ChannelCount);

    public int BufferedFrameCount =>
        _buffer.CountFrames;

    public int BufferCapacityFrames =>
        _buffer.CapacityFrames;

    public double CurrentResampleRatio { get; private set; } =
        1.0;

    public double CurrentCorrectionPpm { get; private set; }

    public double EstimatedDriftPpm { get; private set; }

    public double TimingErrorFrames { get; private set; }

    public bool IsResynchronizing { get; private set; }

    public long ResyncCount =>
        _synchronizer.ResyncCount;

    public long UnderrunCount =>
        _buffer.UnderrunCount +
        _synchronizer.UnderrunCount;

    public long UnderrunFrameCount =>
        _buffer.UnderrunFrameCount;

    /// <summary>
    /// Times <see cref="Process"/> found itself called after an abnormally
    /// long wall-clock gap since its previous call, and reset its
    /// resampler/synchronizer/EQ/gain/buffer state in response.
    /// </summary>
    /// <remarks>
    /// Every input to this route -- <see cref="Process"/>'s own source
    /// block, the resampler, the synchronizer -- is driven by frame counts
    /// and reported clock positions, none of which register real time simply
    /// failing to pass: a render loop blocked for hundreds of milliseconds
    /// inside a single slow <see cref="IAudioOutputStream.Write"/> call (the
    /// observed case: a display topology change stalling the physical WASAPI
    /// write with no exception and no reported underrun) leaves the
    /// synchronizer facing a fabricated multi-hundred-millisecond timing
    /// error it never actually saw happen, and would otherwise spend the
    /// next several seconds fighting to "correct" a drift that was never
    /// real. This is the one signal immune to that: wall-clock time between
    /// successive calls is true regardless of what any counter inside this
    /// class believes happened.
    /// </remarks>
    /// <remarks>
    /// Does <em>not</em> fade this route's own output back in -- real-
    /// hardware testing found the block <see cref="Process"/> computes right
    /// after detecting the gap is normally a perfectly ordinary, continuous
    /// continuation of the source waveform (nothing about this route's own
    /// processing was interrupted), so forcing it down to silence first
    /// would only add a fresh, self-inflicted discontinuity. The block that
    /// actually needs fading is whichever one is sitting in
    /// <see cref="IAudioOutputStream.Write"/> when the wait happens, and
    /// only that call site knows when that is; see
    /// <c>WasapiRenderOutput.Write</c>'s own recovery fade.
    /// </remarks>
    public long StallRecoveryCount { get; private set; }

    /// <summary>
    /// The longest wall-clock gap ever observed between successive
    /// <see cref="Process"/> calls, whether or not it crossed the threshold
    /// that triggers <see cref="StallRecoveryCount"/>.
    /// </summary>
    public double LongestProcessGapMilliseconds { get; private set; }

    /// <summary>
    /// What the output stream itself saw, as distinct from what this route
    /// saw. The two answer different questions and have repeatedly been
    /// confused for one another: this route detects a gap between its own
    /// <see cref="Process"/> calls, which says nothing about whether the
    /// device actually ran out of audio to play, while the output stream
    /// detects the endpoint running dry, which is what silence in the
    /// rendered output actually is. A stall recovery with no endpoint dry-out
    /// means the device kept playing throughout and no fade was needed; a dry
    /// endpoint with no stall recovery means the interruption never grew
    /// large enough to show up between Process calls at all.
    /// </summary>
    public AudioOutputDiagnostics OutputDiagnostics { get; private set; }

    public bool IsOperational =>
        _started &&
        !_failed;

    public AudioRouteRuntime(
        AudioRoute route,
        IAudioOutputStream output,
        AudioProcessingChain processing,
        GainProcessor gain,
        ParametricEqProcessor parametricEq,
        int blockFrameCapacity)
        : this(
            route,
            output,
            processing,
            gain,
            parametricEq,
            blockFrameCapacity,
            new AudioRouteHealthWriter(
                route),
            fadeInOnFirstStart: false)
    {
    }

    internal AudioRouteRuntime(
        AudioRoute route,
        IAudioOutputStream output,
        AudioProcessingChain processing,
        GainProcessor gain,
        ParametricEqProcessor parametricEq,
        int blockFrameCapacity,
        AudioRouteHealthWriter health,
        bool fadeInOnFirstStart = false)
    {
        ArgumentNullException.ThrowIfNull(
            route);

        ArgumentNullException.ThrowIfNull(
            output);

        ArgumentNullException.ThrowIfNull(
            processing);

        ArgumentNullException.ThrowIfNull(
            gain);

        ArgumentNullException.ThrowIfNull(
            parametricEq);

        ArgumentNullException.ThrowIfNull(
            health);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            blockFrameCapacity);

        Route =
            route;

        _output =
            output;

        _processing =
            processing;

        _gain =
            gain;

        _parametricEq =
            parametricEq;

        _health =
            health;

        _fadeInOnFirstStart =
            fadeInOnFirstStart;

        BlockFrameCapacity =
            blockFrameCapacity;

        DelayFrameCount =
            ConvertDelayToFrames(
                route.Delay,
                output.Format);

        _resampler =
            new Resampler(
                output.Format.ChannelCount);

        _synchronizer =
            new RouteSynchronizer(
                output.Format.SampleRate);

        int maximumOutputFrameCount =
            Resampler.GetMaximumOutputFrameCount(
                blockFrameCapacity,
                RouteSynchronizer.MaximumResyncRatio);

        int bufferCapacityFrames =
            checked(
                DelayFrameCount +
                maximumOutputFrameCount);

        _buffer =
            new AudioRingBuffer(
                bufferCapacityFrames,
                output.Format.ChannelCount);

        int processingSampleCapacity =
            checked(
                blockFrameCapacity *
                output.Format.ChannelCount);

        int outputSampleCapacity =
            checked(
                maximumOutputFrameCount *
                output.Format.ChannelCount);

        _processingSamples =
            new float[
                processingSampleCapacity];

        _resampledSamples =
            new float[
                outputSampleCapacity];

        _outputSamples =
            new float[
                outputSampleCapacity];

        _health.MarkPrepared(
            output.Format);

        ResetBuffer();
    }

    public void Start()
    {
        ThrowIfDisposed();

        if (_started)
        {
            return;
        }

        if (_failed)
        {
            throw new InvalidOperationException(
                "A failed route runtime cannot be restarted.");
        }

        _health.MarkStarting();

        try
        {
            ResetRouteState();

            _output.Start();

            _fadeInFramesRemaining =
                _fadeInOnFirstStart ||
                _hasStarted
                    ? RestartRampFrameCount
                    : 0;

            _started =
                true;

            _hasStarted =
                true;

            _health.MarkRunning();
        }
        catch (Exception ex)
        {
            _failed =
                true;

            _health.MarkUnavailable(
                ex);

            throw;
        }
    }

    public void Process(
        AudioBlock source)
    {
        ThrowIfDisposed();

        if (!_started ||
            _failed)
        {
            throw new InvalidOperationException(
                "The route runtime is not operational.");
        }

        try
        {
            if (source.Format !=
                Format)
            {
                throw new NotSupportedException(
                    "Adaptive clock resampling currently requires matching nominal " +
                    "input/output sample rates and channel counts.");
            }

            if (source.FrameCount >
                BlockFrameCapacity)
            {
                throw new ArgumentException(
                    "The source block exceeds the route block capacity.",
                    nameof(source));
            }

            DetectProcessStall(
                source.FrameCount);

            AudioOutputClockSnapshot clock =
                _output.GetClockSnapshot();

            RouteSyncDecision decision =
                _synchronizer.Update(
                    source.FramePosition,
                    clock);

            CurrentResampleRatio =
                decision.ResampleRatio;

            CurrentCorrectionPpm =
                decision.CorrectionPpm;

            EstimatedDriftPpm =
                decision.EstimatedDriftPpm;

            TimingErrorFrames =
                decision.TimingErrorFrames;

            IsResynchronizing =
                decision.IsResynchronizing;

            int sourceSampleCount =
                checked(
                    source.FrameCount *
                    source.Format.ChannelCount);

            Span<float> processingSamples =
                _processingSamples.AsSpan(
                    0,
                    sourceSampleCount);

            source.Samples.CopyTo(
                processingSamples);

            var processingBlock =
                new AudioBlock(
                    processingSamples,
                    Format,
                    source.FramePosition);

            _processing.Process(
                processingBlock);

            ApplyRestartRamp(
                processingSamples);

            int resampledFrameCount =
                _resampler.Process(
                    processingSamples,
                    decision.ResampleRatio,
                    _resampledSamples);

            int resampledSampleCount =
                checked(
                    resampledFrameCount *
                    Format.ChannelCount);

            ReadOnlySpan<float> resampledSamples =
                _resampledSamples.AsSpan(
                    0,
                    resampledSampleCount);

            _buffer.Write(
                resampledSamples);

            Span<float> outputSamples =
                _outputSamples.AsSpan(
                    0,
                    resampledSampleCount);

            _buffer.Read(
                outputSamples);

            var outputBlock =
                new AudioBlock(
                    outputSamples,
                    Format,
                    source.FramePosition);

            _output.Write(
                outputBlock);

            OutputDiagnostics =
                _output.GetDiagnostics();

            _health.ReportTiming(
                TimingErrorFrames,
                CurrentCorrectionPpm,
                CurrentResampleRatio,
                IsResynchronizing,
                UnderrunCount,
                ResyncCount,
                StallRecoveryCount,
                LongestProcessGapMilliseconds,
                OutputDiagnostics);
        }
        catch (Exception ex)
        {
            _failed =
                true;

            _health.MarkFailed(
                ex);

            throw;
        }
    }

    /// <summary>
    /// Changes this route's gain, mute and endpoint volume while it is
    /// running, without rebuilding the route or interrupting playback.
    /// </summary>
    /// <remarks>
    /// The click-safe half of live calibration: <see cref="GainProcessor.Reconfigure"/>
    /// glides to the new level over a short ramp rather than stepping to it.
    /// <see cref="Route"/> is updated to match so Health and any later
    /// idempotency check see the calibrated value rather than the one this
    /// route was originally started with.
    /// </remarks>
    public void SetGain(
        float gainDb,
        float volume,
        bool muted)
    {
        ThrowIfDisposed();

        _gain.Reconfigure(
            gainDb,
            volume,
            muted);

        UpdateRoute(
            new AudioRoute(
                Route.EndpointId,
                Route.Requirement,
                volume,
                muted,
                Route.Delay,
                new AudioProcessingProfile(
                    gainDb,
                    Route.Processing.ParametricEq)));
    }

    /// <summary>
    /// Changes this route's parametric EQ -- including switching to a
    /// different named profile, or to no EQ at all -- while it is running,
    /// without rebuilding the route or interrupting playback.
    /// </summary>
    /// <remarks>
    /// Every route's chain carries a <see cref="ParametricEqProcessor"/>
    /// regardless of whether its configured profile has any bands, precisely
    /// so this is always available live: a route built bypassed can still be
    /// given a profile later without an audible restart, using the same
    /// crossfade <see cref="ParametricEqProcessor.Reconfigure"/> already
    /// provides for changing bands on a route that already has some.
    /// </remarks>
    public void SetParametricEq(
        ParametricEqProfile? profile)
    {
        ThrowIfDisposed();

        _parametricEq.Reconfigure(
            profile ??
            ParametricEqProfile.Flat);

        UpdateRoute(
            new AudioRoute(
                Route.EndpointId,
                Route.Requirement,
                Route.Volume,
                Route.Muted,
                Route.Delay,
                new AudioProcessingProfile(
                    Route.Processing.GainDb,
                    profile)));
    }

    private void UpdateRoute(
        AudioRoute route)
    {
        Route =
            route;

        _health.UpdateRoute(
            route);
    }

    public void ResetBuffer()
    {
        ThrowIfDisposed();

        _buffer.Reset();

        _buffer.PrimeSilence(
            DelayFrameCount);
    }

    public void Stop()
    {
        ThrowIfDisposed();

        if (!_started)
        {
            return;
        }

        try
        {
            _output.Stop();

            _started =
                false;

            ResetRouteState();

            if (!_failed)
            {
                _health.MarkStopped();
            }
        }
        catch (Exception ex)
        {
            _started =
                false;

            ResetRouteState();

            _failed =
                true;

            _health.MarkFailed(
                ex);

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
            if (_started)
            {
                Stop();
            }
        }
        finally
        {
            _output.Dispose();

            _disposed =
                true;
        }
    }

    private void ResetTimingState()
    {
        _resampler.Reset();

        _synchronizer.Reset();

        CurrentResampleRatio =
            1.0;

        CurrentCorrectionPpm =
            0.0;

        EstimatedDriftPpm =
            0.0;

        TimingErrorFrames =
            0.0;

        IsResynchronizing =
            false;
    }

    private void ResetRouteState()
    {
        ResetProcessingPipelineState();

        _fadeInFramesRemaining =
            0;

        _lastProcessTimestamp =
            0;

        StallRecoveryCount =
            0;

        LongestProcessGapMilliseconds =
            0;

        OutputDiagnostics =
            default;
    }

    /// <summary>
    /// Clears every piece of state <see cref="Process"/> accumulates between
    /// calls -- resampler and synchronizer history, EQ and gain smoothing
    /// state, and whatever is sitting in the delay/resample ring buffer --
    /// without touching the output stream itself or the diagnostic counters.
    /// </summary>
    /// <remarks>
    /// Used both by a full <see cref="Start"/> and by <see cref="DetectProcessStall"/>.
    /// A restart-style fade-in alone is not enough after either kind of
    /// discontinuity: the synchronizer's clock-drift tracking, in
    /// particular, sees a fabricated multi-hundred-millisecond timing error
    /// across a stall it never observed happening, and would otherwise spend
    /// the next several seconds fighting to "correct" a drift that was never
    /// real -- audible as exactly the kind of waveform jump the fade-in was
    /// supposed to prevent, not fixed by scaling amplitude alone.
    /// </remarks>
    private void ResetProcessingPipelineState()
    {
        ResetTimingState();

        _parametricEq.Reset();

        _gain.Reset();

        ResetBuffer();
    }

    private void DetectProcessStall(
        int frameCount)
    {
        long now =
            Stopwatch.GetTimestamp();

        if (_lastProcessTimestamp !=
            0)
        {
            TimeSpan elapsed =
                Stopwatch.GetElapsedTime(
                    _lastProcessTimestamp,
                    now);

            LongestProcessGapMilliseconds =
                Math.Max(
                    LongestProcessGapMilliseconds,
                    elapsed.TotalMilliseconds);

            TimeSpan expected =
                TimeSpan.FromSeconds(
                    frameCount /
                    (double)Format.SampleRate);

            TimeSpan maximumContinuousGap =
                TimeSpan.FromTicks(
                    Math.Max(
                        TimeSpan
                            .FromMilliseconds(25)
                            .Ticks,
                        expected.Ticks *
                        6));

            if (elapsed >
                maximumContinuousGap)
            {
                ResetProcessingPipelineState();

                StallRecoveryCount++;
            }
        }

        _lastProcessTimestamp =
            now;
    }

    private void ApplyRestartRamp(
        Span<float> samples)
    {
        if (_fadeInFramesRemaining == 0)
        {
            return;
        }

        int channels =
            Format.ChannelCount;

        for (int frameStart = 0;
             frameStart < samples.Length &&
             _fadeInFramesRemaining > 0;
             frameStart += channels)
        {
            float level =
                1.0f -
                (_fadeInFramesRemaining /
                 (float)RestartRampFrameCount);

            for (int channel = 0;
                 channel < channels;
                 channel++)
            {
                samples[
                    frameStart +
                    channel] *=
                        level;
            }

            _fadeInFramesRemaining--;
        }
    }

    private static int ConvertDelayToFrames(
        TimeSpan delay,
        AudioFormat format)
    {
        double exactFrames =
            delay.TotalSeconds *
            format.SampleRate;

        if (!double.IsFinite(
                exactFrames) ||
            exactFrames >
                int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delay),
                delay,
                "Configured delay is too large for the route format.");
        }

        return checked(
            (int)Math.Round(
                exactFrames,
                MidpointRounding.AwayFromZero));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }
}

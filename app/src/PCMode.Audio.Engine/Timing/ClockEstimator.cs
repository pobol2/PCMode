using PCMode.Audio.Engine.Contracts;

namespace PCMode.Audio.Engine.Timing;

public readonly record struct ClockEstimate(
    double DriftPpm,
    double QueuedFrames,
    double TimingErrorFrames,
    bool HasRateEstimate
);

public sealed class ClockEstimator
{
    private const double MaximumObservedDriftPpm = 10_000.0;

    /// <summary>
    /// How much of the gap between the tracked target and the just-observed
    /// queue depth closes per estimation window (~100ms by default). Small
    /// on purpose: a genuine sudden stall must still read as a real timing
    /// error for the several estimation windows a resync needs to run its
    /// course, not be absorbed away in one or two ticks. But over the
    /// seconds a route actually stays open, this is what lets the target
    /// stop being permanently wrong -- see the remark on
    /// <see cref="_targetQueuedFrames"/>.
    /// </summary>
    private const double TargetQueuedFramesTrackingFactor = 0.02;

    private readonly int _estimationWindowFrames;

    private readonly double _smoothingFactor;

    private bool _initialized;

    private bool _hasRateEstimate;

    private long _rateAnchorSourceFrame;

    private double _rateAnchorPlayedFrame;

    /// <summary>
    /// The queue depth <see cref="TimingErrorFrames"/> is measured against.
    /// It starts as a snapshot taken on the very first
    /// <see cref="Update"/> call -- before the route has written a single
    /// frame, so the real device's buffer has not reached whatever depth it
    /// naturally settles at once actually playing. Left as a frozen
    /// snapshot, that unrepresentative starting value stays wrong forever:
    /// the healthy, necessary buffering a real device keeps queued ahead
    /// once it's actually running gets misread as a permanent timing error,
    /// which was measured driving the corrector to its resync ceiling
    /// continuously rather than a real, bounded drift. Tracking it slowly
    /// toward what's actually being observed (see
    /// <see cref="TargetQueuedFramesTrackingFactor"/>) lets that initial
    /// guess correct itself once real playback shows what the right value
    /// is, without weakening detection of an actual sudden stall.
    /// </summary>
    private double _targetQueuedFrames;

    private double _estimatedDriftPpm;

    public ClockEstimator(int sampleRate, int? estimationWindowFrames = null, double smoothingFactor = 0.20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        if (!double.IsFinite(smoothingFactor) || smoothingFactor <= 0.0 || smoothingFactor > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(smoothingFactor));
        }

        _estimationWindowFrames = estimationWindowFrames ?? Math.Max(1, sampleRate / 10);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_estimationWindowFrames);

        _smoothingFactor = smoothingFactor;
    }

    public ClockEstimate Update(long sourceFramePosition, AudioOutputClockSnapshot snapshot)
    {
        if (sourceFramePosition < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceFramePosition));
        }

        if (!_initialized)
        {
            _initialized = true;

            _rateAnchorSourceFrame = sourceFramePosition;

            _rateAnchorPlayedFrame = snapshot.PlayedFramePosition;

            _targetQueuedFrames = snapshot.QueuedFrameCount;

            return new ClockEstimate(0.0, snapshot.QueuedFrameCount, 0.0, false);
        }

        long sourceDelta = sourceFramePosition - _rateAnchorSourceFrame;

        if (sourceDelta >= _estimationWindowFrames)
        {
            double playedDelta = snapshot.PlayedFramePosition - _rateAnchorPlayedFrame;

            if (playedDelta >= 0.0)
            {
                double observedRatio = playedDelta / sourceDelta;

                double observedDriftPpm = (observedRatio - 1.0) * 1_000_000.0;

                observedDriftPpm = Math.Clamp(observedDriftPpm, -MaximumObservedDriftPpm, MaximumObservedDriftPpm);

                if (_hasRateEstimate)
                {
                    _estimatedDriftPpm += (observedDriftPpm - _estimatedDriftPpm) * _smoothingFactor;
                }
                else
                {
                    _estimatedDriftPpm = observedDriftPpm;

                    _hasRateEstimate = true;
                }
            }

            _rateAnchorSourceFrame = sourceFramePosition;

            _rateAnchorPlayedFrame = snapshot.PlayedFramePosition;

            _targetQueuedFrames += (snapshot.QueuedFrameCount - _targetQueuedFrames) * TargetQueuedFramesTrackingFactor;
        }

        double timingErrorFrames = snapshot.QueuedFrameCount - _targetQueuedFrames;

        return new ClockEstimate(_estimatedDriftPpm, snapshot.QueuedFrameCount, timingErrorFrames, _hasRateEstimate);
    }

    public void Reset()
    {
        _initialized = false;

        _hasRateEstimate = false;

        _rateAnchorSourceFrame = 0;

        _rateAnchorPlayedFrame = 0.0;

        _targetQueuedFrames = 0.0;

        _estimatedDriftPpm = 0.0;
    }
}

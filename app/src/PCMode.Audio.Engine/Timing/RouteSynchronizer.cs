using PCMode.Audio.Engine.Contracts;

namespace PCMode.Audio.Engine.Timing;

public readonly record struct RouteSyncDecision(
    double ResampleRatio,
    double CorrectionPpm,
    double EstimatedDriftPpm,
    double TimingErrorFrames,
    bool IsResynchronizing);

public sealed class RouteSynchronizer
{
    public const double MaximumAdaptiveCorrectionPpm =
        500.0;

    public const double MaximumResyncCorrectionPpm =
        20_000.0;

    public const double MaximumResyncRatio =
        1.0 +
        MaximumResyncCorrectionPpm /
        1_000_000.0;

    private const double ResyncThresholdSeconds =
        0.020;

    private const double ResyncExitThresholdSeconds =
        0.005;

    private const double ResyncRecoveryPpmPerSecond =
        1_000_000.0;

    private const double HealthyQueueThresholdFrames =
        1.0;

    private readonly int _sampleRate;

    private readonly double _resyncThresholdFrames;

    private readonly double _resyncExitThresholdFrames;

    private readonly ClockEstimator _clockEstimator;

    private readonly DriftController _driftController;

    private bool _resynchronizing;

    private bool _hasSeenHealthyQueue;

    private bool _inUnderrun;

    public long UnderrunCount { get; private set; }

    public long ResyncCount { get; private set; }

    public RouteSynchronizer(
        int sampleRate,
        int? estimationWindowFrames = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            sampleRate);

        _sampleRate =
            sampleRate;

        _resyncThresholdFrames =
            sampleRate *
            ResyncThresholdSeconds;

        _resyncExitThresholdFrames =
            sampleRate *
            ResyncExitThresholdSeconds;

        _clockEstimator =
            new ClockEstimator(
                sampleRate,
                estimationWindowFrames);

        _driftController =
            new DriftController(
                sampleRate,
                MaximumAdaptiveCorrectionPpm);
    }

    public RouteSyncDecision Update(
        long sourceFramePosition,
        AudioOutputClockSnapshot snapshot)
    {
        ClockEstimate estimate =
            _clockEstimator.Update(
                sourceFramePosition,
                snapshot);

        UpdateUnderrunState(
            estimate.QueuedFrames);

        double absoluteError =
            Math.Abs(
                estimate.TimingErrorFrames);

        if (!_resynchronizing &&
            absoluteError >=
                _resyncThresholdFrames)
        {
            _resynchronizing =
                true;

            ResyncCount++;
        }
        else if (_resynchronizing &&
                 absoluteError <=
                     _resyncExitThresholdFrames)
        {
            _resynchronizing =
                false;
        }

        double correctionPpm;

        if (_resynchronizing)
        {
            double timingErrorSeconds =
                estimate.TimingErrorFrames /
                _sampleRate;

            correctionPpm =
                estimate.DriftPpm -
                timingErrorSeconds *
                ResyncRecoveryPpmPerSecond;

            correctionPpm =
                Math.Clamp(
                    correctionPpm,
                    -MaximumResyncCorrectionPpm,
                    MaximumResyncCorrectionPpm);
        }
        else
        {
            correctionPpm =
                _driftController.Update(
                    estimate.DriftPpm,
                    estimate.TimingErrorFrames);
        }

        double ratio =
            1.0 +
            correctionPpm /
            1_000_000.0;

        return new RouteSyncDecision(
            ratio,
            correctionPpm,
            estimate.DriftPpm,
            estimate.TimingErrorFrames,
            _resynchronizing);
    }

    public void Reset()
    {
        _clockEstimator.Reset();

        _driftController.Reset();

        _resynchronizing =
            false;

        _hasSeenHealthyQueue =
            false;

        _inUnderrun =
            false;

        UnderrunCount =
            0;

        ResyncCount =
            0;
    }

    private void UpdateUnderrunState(
        double queuedFrames)
    {
        if (queuedFrames >
            HealthyQueueThresholdFrames)
        {
            _hasSeenHealthyQueue =
                true;

            _inUnderrun =
                false;

            return;
        }

        if (_hasSeenHealthyQueue &&
            !_inUnderrun)
        {
            UnderrunCount++;

            _inUnderrun =
                true;
        }
    }
}
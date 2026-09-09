namespace PCMode.Audio.Engine.Timing;

public sealed class DriftController
{
    public const double DefaultMaximumCorrectionPpm = 500.0;

    private const double DefaultQueueCorrectionPpmPerSecond = 50_000.0;

    private const double DefaultMaximumSlewPpmPerUpdate = 25.0;

    private readonly int _sampleRate;

    private readonly double _maximumCorrectionPpm;

    private readonly double _queueCorrectionPpmPerSecond;

    private readonly double _maximumSlewPpmPerUpdate;

    private double _currentCorrectionPpm;

    public double CurrentCorrectionPpm => _currentCorrectionPpm;

    public DriftController(
        int sampleRate,
        double maximumCorrectionPpm = DefaultMaximumCorrectionPpm,
        double queueCorrectionPpmPerSecond = DefaultQueueCorrectionPpmPerSecond,
        double maximumSlewPpmPerUpdate = DefaultMaximumSlewPpmPerUpdate
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        if (!double.IsFinite(maximumCorrectionPpm) || maximumCorrectionPpm <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCorrectionPpm));
        }

        if (!double.IsFinite(queueCorrectionPpmPerSecond) || queueCorrectionPpmPerSecond < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCorrectionPpmPerSecond));
        }

        if (!double.IsFinite(maximumSlewPpmPerUpdate) || maximumSlewPpmPerUpdate <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSlewPpmPerUpdate));
        }

        _sampleRate = sampleRate;

        _maximumCorrectionPpm = maximumCorrectionPpm;

        _queueCorrectionPpmPerSecond = queueCorrectionPpmPerSecond;

        _maximumSlewPpmPerUpdate = maximumSlewPpmPerUpdate;
    }

    public double Update(double estimatedDriftPpm, double timingErrorFrames)
    {
        if (!double.IsFinite(estimatedDriftPpm))
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedDriftPpm));
        }

        if (!double.IsFinite(timingErrorFrames))
        {
            throw new ArgumentOutOfRangeException(nameof(timingErrorFrames));
        }

        double timingErrorSeconds = timingErrorFrames / _sampleRate;

        double desiredCorrectionPpm = estimatedDriftPpm - timingErrorSeconds * _queueCorrectionPpmPerSecond;

        desiredCorrectionPpm = Math.Clamp(desiredCorrectionPpm, -_maximumCorrectionPpm, _maximumCorrectionPpm);

        double change = desiredCorrectionPpm - _currentCorrectionPpm;

        change = Math.Clamp(change, -_maximumSlewPpmPerUpdate, _maximumSlewPpmPerUpdate);

        _currentCorrectionPpm += change;

        return _currentCorrectionPpm;
    }

    public void Reset()
    {
        _currentCorrectionPpm = 0.0;
    }
}

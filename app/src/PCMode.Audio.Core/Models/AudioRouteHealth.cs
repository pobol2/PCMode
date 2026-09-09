namespace PCMode.Audio.Core.Models;

public enum AudioRouteHealthState
{
    Inactive,
    Pending,
    Healthy,
    Degraded,
    Unavailable,
    Failed,
}

public sealed class AudioRouteHealth
{
    public AudioEndpointId EndpointId { get; }

    public AudioRouteRequirement Requirement { get; }

    public AudioRouteHealthState State { get; }

    public AudioEndpointState EndpointState { get; }

    public AudioFormat? Format { get; }

    public TimeSpan ConfiguredDelay { get; }

    public double ClockOffsetFrames { get; }

    public double? ClockOffsetMilliseconds =>
        Format is { SampleRate: > 0 } format ? ClockOffsetFrames / format.SampleRate * 1000.0 : null;

    public double DriftCorrectionPpm { get; }

    public double ResampleRatio { get; }

    public bool IsResynchronizing { get; }

    public long UnderrunCount { get; }

    public long ResyncCount { get; }

    /// <summary>
    /// Times this route found itself processing again after an abnormally
    /// long wall-clock gap since its previous block, with no exception and
    /// no reported underrun along the way -- the signature of a render loop
    /// blocked inside a slow physical output write rather than any failure
    /// this route's own error handling would otherwise catch.
    /// </summary>
    public long StallRecoveryCount { get; }

    /// <summary>
    /// The longest such wall-clock gap ever observed, in milliseconds,
    /// whether or not it was large enough to count toward
    /// <see cref="StallRecoveryCount"/>.
    /// </summary>
    public double LongestProcessGapMilliseconds { get; }

    /// <summary>
    /// Times this route's output stream decided the chunk it was about to
    /// submit was the first audio after a break in the sound, and ramped it
    /// in rather than handing the device full amplitude at an arbitrary
    /// phase.
    /// </summary>
    public long OutputRecoveryFadeCount { get; }

    /// <summary>
    /// Times the endpoint was found with nothing left queued to play, so the
    /// device was being fed silence in place of this route's audio. Unlike
    /// <see cref="StallRecoveryCount"/>, which only reports that this route
    /// was slow to come back, this reports that the sound itself actually
    /// stopped -- the two do not have to agree, and which of them moves
    /// during an interruption is what says where that interruption lives.
    /// </summary>
    public long OutputEndpointDryCount { get; }

    /// <summary>
    /// The longest wall-clock gap between one chunk reaching the device and
    /// the next, in milliseconds.
    /// </summary>
    public double LongestOutputSubmitGapMilliseconds { get; }

    /// <summary>
    /// The longest single write to the output device, in milliseconds. Close
    /// to <see cref="LongestOutputSubmitGapMilliseconds"/> means the route
    /// was blocked inside that write; far below it means the route was not
    /// writing at all for that stretch, and whatever went missing did so
    /// before this route ever saw it.
    /// </summary>
    public double LongestOutputWriteMilliseconds { get; }

    /// <summary>
    /// The longest single wait for the output endpoint to accept more audio,
    /// in milliseconds. Close to <see cref="LongestOutputWriteMilliseconds"/>
    /// means the device stopped consuming and later resumed; far below it
    /// means an interop call blocked instead.
    /// </summary>
    public double LongestOutputBufferWaitMilliseconds { get; }

    public AudioProcessingProfile Processing { get; }

    public AudioFailure? LastFailure { get; }

    public AudioRouteHealth(
        AudioEndpointId endpointId,
        AudioRouteRequirement requirement,
        AudioRouteHealthState state,
        AudioEndpointState endpointState,
        AudioFormat? format,
        TimeSpan configuredDelay,
        double clockOffsetFrames,
        double driftCorrectionPpm,
        double resampleRatio,
        bool isResynchronizing,
        long underrunCount,
        long resyncCount,
        long stallRecoveryCount,
        double longestProcessGapMilliseconds,
        long outputRecoveryFadeCount,
        long outputEndpointDryCount,
        double longestOutputSubmitGapMilliseconds,
        double longestOutputWriteMilliseconds,
        double longestOutputBufferWaitMilliseconds,
        AudioProcessingProfile processing,
        AudioFailure? lastFailure
    )
    {
        ArgumentNullException.ThrowIfNull(processing);

        if (!double.IsFinite(clockOffsetFrames))
        {
            throw new ArgumentOutOfRangeException(nameof(clockOffsetFrames));
        }

        if (!double.IsFinite(driftCorrectionPpm))
        {
            throw new ArgumentOutOfRangeException(nameof(driftCorrectionPpm));
        }

        if (!double.IsFinite(resampleRatio) || resampleRatio <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(resampleRatio));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(underrunCount);

        ArgumentOutOfRangeException.ThrowIfNegative(resyncCount);

        ArgumentOutOfRangeException.ThrowIfNegative(stallRecoveryCount);

        if (!double.IsFinite(longestProcessGapMilliseconds) || longestProcessGapMilliseconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(longestProcessGapMilliseconds));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(outputRecoveryFadeCount);

        ArgumentOutOfRangeException.ThrowIfNegative(outputEndpointDryCount);

        if (!double.IsFinite(longestOutputSubmitGapMilliseconds) || longestOutputSubmitGapMilliseconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(longestOutputSubmitGapMilliseconds));
        }

        if (!double.IsFinite(longestOutputWriteMilliseconds) || longestOutputWriteMilliseconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(longestOutputWriteMilliseconds));
        }

        if (!double.IsFinite(longestOutputBufferWaitMilliseconds) || longestOutputBufferWaitMilliseconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(longestOutputBufferWaitMilliseconds));
        }

        EndpointId = endpointId;

        Requirement = requirement;

        State = state;

        EndpointState = endpointState;

        Format = format;

        ConfiguredDelay = configuredDelay;

        ClockOffsetFrames = clockOffsetFrames;

        DriftCorrectionPpm = driftCorrectionPpm;

        ResampleRatio = resampleRatio;

        IsResynchronizing = isResynchronizing;

        UnderrunCount = underrunCount;

        ResyncCount = resyncCount;

        StallRecoveryCount = stallRecoveryCount;

        LongestProcessGapMilliseconds = longestProcessGapMilliseconds;

        OutputRecoveryFadeCount = outputRecoveryFadeCount;

        OutputEndpointDryCount = outputEndpointDryCount;

        LongestOutputSubmitGapMilliseconds = longestOutputSubmitGapMilliseconds;

        LongestOutputWriteMilliseconds = longestOutputWriteMilliseconds;

        LongestOutputBufferWaitMilliseconds = longestOutputBufferWaitMilliseconds;

        Processing = processing;

        LastFailure = lastFailure;
    }
}

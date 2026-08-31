namespace PCMode.Engine;

/// <summary>What became of the most recent attempt to observe one television value.</summary>
public enum LgTvObservationStatus
{
    NotObserved,
    Observed,
    Disabled,
    Unpaired,
    Unreachable,
    Unknown,
    Error,
}

/// <summary>A value observed from a television, or the precise reason there is no value.</summary>
public sealed record LgTvObservation(
    LgTvObservationStatus Status,
    string? Value = null,
    string? Problem = null,
    DateTimeOffset? ObservedUtc = null)
{
    public static LgTvObservation NotObserved { get; } = new(LgTvObservationStatus.NotObserved);
}

/// <summary>
/// The latest on-demand observations for one television. The connection owns this snapshot
/// because all four television providers already share that connection.
/// </summary>
public sealed record LgTvObservedState(
    LgTvObservation Power,
    LgTvObservation Input,
    LgTvObservation Profile)
{
    public static LgTvObservedState Empty { get; } = new(
        LgTvObservation.NotObserved,
        LgTvObservation.NotObserved,
        LgTvObservation.NotObserved);
}

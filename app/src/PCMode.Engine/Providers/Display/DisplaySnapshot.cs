namespace PCMode.Engine;

/// <summary>Whether a configured monitor can actually be used right now.</summary>
public enum DisplayMappingState
{
    /// <summary>Windows has a target with that device path.</summary>
    Ready,

    /// <summary>
    /// Nothing on this machine has that device path. A typo, or a monitor that
    /// has never been detected this session -- for a television that has been off
    /// since the machine started, the second is the ordinary case.
    /// </summary>
    Missing,

    /// <summary>
    /// The monitors could not be read, so nothing could be checked. Not the same
    /// as <see cref="Missing"/>, which is a statement about the machine.
    /// </summary>
    Unknown,
}

/// <summary>One monitor Windows knows about, and everything a layout needs.</summary>
/// <remarks>
/// The device path IS here, unlike the audio provider's endpoint ID. It is the
/// identifier a layout is written against precisely because it is the stable one,
/// so anything configuring displays has to be able to see it.
///
/// Placement, size and rate are only meaningful while a monitor is active, and
/// are zero otherwise. <see cref="SupportedRefreshHz"/> can only be asked of an
/// active monitor and is at its current resolution.
/// </remarks>
public sealed record DisplayMonitorInfo(
    string FriendlyName,
    string DevicePath,
    bool Active,
    bool Primary,
    int X,
    int Y,
    int Width,
    int Height,
    double RefreshHz,
    IReadOnlyList<int> SupportedRefreshHz,
    string? LogicalName
);

/// <summary>One configured logical monitor, and whether it resolves.</summary>
public sealed record DisplayMonitorMapping(string LogicalName, string DevicePath, DisplayMappingState State);

/// <summary>
/// What the display provider can see: the smallest thing a settings UI needs.
/// </summary>
/// <remarks>
/// Presentation-neutral -- names, numbers and states, no formatting and no UI
/// types. It answers the questions anything configuring displays has to ask:
/// whether displays can be read at all, what this machine's monitors are called
/// and which device paths they have, whether the names already written down still
/// resolve, and which layout the machine is currently in.
///
/// A snapshot rather than a live view: monitors are switched on and off, so a
/// value claiming to stay current would be wrong without saying when.
/// </remarks>
public sealed record DisplaySnapshot(
    bool Available,
    string? Problem,
    IReadOnlyList<DisplayMonitorInfo> Monitors,
    IReadOnlyList<DisplayMonitorMapping> Mappings,
    IReadOnlyList<string> Layouts,
    string? CurrentLayout
)
{
    /// <summary>
    /// Whether any logical monitor has been defined. Being unconfigured is not a
    /// fault: it is what a machine that has never set up displays looks like.
    /// </summary>
    public bool Configured => Mappings.Count > 0;
}

namespace PCMode.Engine;

/// <summary>
/// A VCP (Virtual Control Panel) feature PCMode knows how to name.
/// </summary>
/// <remarks>
/// Deliberately only the two controls Step 10 starts with. A code with no
/// member here is a code this build never asks a monitor about, which is what
/// keeps capability parsing from having to understand -- or silently
/// mis-describe -- every VCP feature the MCCS specification defines.
/// </remarks>
public enum DdcVcpCode : byte
{
    /// <summary>Luminance. VCP 0x10, 0-100 on the monitors this targets.</summary>
    Brightness = 0x10,

    /// <summary>Input source select. VCP 0x60.</summary>
    InputSource = 0x60,
}

/// <summary>
/// What one VCP feature's entry in a monitor's capability string said.
/// </summary>
/// <param name="SupportedValues">
/// The values the monitor enumerated for this feature, or empty when it
/// declared the feature present without listing which values are valid --
/// which the MCCS capability string is allowed to do, and which this reads as
/// "supported, but which values are real is not knowable from here."
/// </param>
public sealed record DdcVcpCapability(DdcVcpCode Code, IReadOnlyList<uint> SupportedValues);

/// <summary>
/// A monitor's capabilities, as far as this build reads them.
/// </summary>
/// <remarks>
/// Only <see cref="DdcVcpCode"/>'s members can appear here. Everything else the
/// capability string names -- and a real one names dozens of features -- is
/// read past and discarded, because nothing in this build offers a control for
/// it. That is what "hide unsupported controls" means before a control has
/// even been built: the absence is total, not a filtered display of something
/// this type could otherwise say.
/// </remarks>
public sealed record DdcCapabilities(IReadOnlyDictionary<DdcVcpCode, DdcVcpCapability> Features)
{
    /// <summary>No known feature. What a monitor that could not be read gets.</summary>
    public static readonly DdcCapabilities None = new(new Dictionary<DdcVcpCode, DdcVcpCapability>());

    public bool Supports(DdcVcpCode code) => Features.ContainsKey(code);
}

/// <summary>One VCP read: the current value, and the maximum the monitor reported.</summary>
public sealed record DdcVcpValue(uint Current, uint Maximum);

/// <summary>
/// The monitors, behind a seam so tests never probe or command the real ones.
/// </summary>
/// <remarks>
/// WHY THIS IS AN INTERFACE
///
/// The same reason the display devices and the audio endpoints are. What is
/// worth testing here -- a monitor that never answers, one that reports no
/// capabilities, one whose readback disagrees with what was just written -- is
/// exactly what cannot be arranged on whatever machine happens to be running
/// the suite, and a real implementation would be commanding somebody's actual
/// screen to do it.
///
/// NOTHING IS CACHED ACROSS CALLS
///
/// A monitor can be unplugged, put to sleep, or moved to a different output
/// between two calls, and a handle kept from an earlier one may already be
/// invalid. Every call resolves the device path to a live monitor fresh, the
/// same rule <see cref="IDisplayDevices"/> keeps and about the same hardware.
///
/// EVERY CALL IS BOUNDED
///
/// DDC/CI runs over the monitor's I2C bus, and nothing here can watch a native
/// call to know it is not coming back: a monitor that is asleep, disconnected,
/// or simply does not implement the DDC/CI specification correctly can leave a
/// blocking probe or command waiting forever. <paramref name="within"/> -- named
/// the same way on every member -- is what stops one such monitor from
/// stalling a transition that names three others. It bounds the attempt, not
/// the underlying call: a call that does not return in time is abandoned
/// rather than force-cancelled, because Windows offers no safe way to interrupt
/// one that is already in flight.
/// </remarks>
public interface IDdcMonitors
{
    /// <summary>
    /// Every monitor Windows can currently address over DDC/CI, by the same
    /// stable device path <see cref="IDisplayDevices"/> reports.
    /// </summary>
    /// <remarks>
    /// Empty rather than throwing when nothing could be enumerated in time --
    /// callers that want the reason a specific monitor is missing use
    /// <see cref="ReadCapabilities"/> or <see cref="GetVcpFeature"/> instead,
    /// which is where the reason belongs.
    /// </remarks>
    IReadOnlyList<string> KnownDevicePaths(TimeSpan within);

    /// <summary>
    /// The monitor's capabilities, or null when they could not be read within
    /// <paramref name="within"/>.
    /// </summary>
    DdcCapabilities? ReadCapabilities(string devicePath, TimeSpan within);

    /// <summary>
    /// One VCP feature's current value, or null when it could not be read
    /// within <paramref name="within"/>.
    /// </summary>
    DdcVcpValue? GetVcpFeature(string devicePath, DdcVcpCode code, TimeSpan within);

    /// <summary>
    /// Writes one VCP feature.
    /// </summary>
    /// <remarks>Throws when the monitor could not be reached or refused it.</remarks>
    void SetVcpFeature(string devicePath, DdcVcpCode code, uint value, TimeSpan within);
}

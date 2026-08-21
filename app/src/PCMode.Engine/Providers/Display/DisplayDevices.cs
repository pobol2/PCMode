namespace PCMode.Engine;

/// <summary>
/// A monitor Windows knows about, whether or not it is showing a picture.
/// </summary>
/// <remarks>
/// <see cref="DevicePath"/> is the identity everything else here is keyed by. It
/// is the only value the display API hands out that is both stable -- it survives
/// a reboot, and is not tied to whichever \\.\DISPLAYn slot Windows assigned this
/// session -- and unambiguous, which the friendly name is not: two monitors of the
/// same model report the identical name from their EDID, so a configuration that
/// named them by that would be pointing at either one.
/// </remarks>
public sealed record DisplayTarget(string DevicePath, string FriendlyName, bool Active);

/// <summary>One monitor that is currently showing a picture.</summary>
/// <remarks>
/// The rectangle is the monitor's place on the virtual desktop. Coordinates may
/// be negative: a monitor to the left of the primary starts at a negative X, and
/// that is ordinary rather than a sign of a miscalculation.
///
/// <see cref="RefreshHz"/> is what Windows actually achieved, not what anything
/// asked for. It is frequently an NTSC-derived rate -- 119.88 rather than 120 --
/// which is why nothing compares it for equality.
/// </remarks>
public sealed record ActiveDisplay(
    string DevicePath,
    string FriendlyName,
    int X,
    int Y,
    int Width,
    int Height,
    double RefreshHz)
{
    /// <summary>
    /// Whether this is the primary display, by Windows' own definition of the
    /// word: the monitor whose source sits at the virtual desktop's origin.
    /// </summary>
    public bool IsPrimary => X == 0 && Y == 0;
}

/// <summary>
/// The monitors, behind a seam so tests never look at or rearrange the real ones.
/// </summary>
/// <remarks>
/// WHY THIS IS AN INTERFACE
///
/// The same reason the cursor and the audio endpoints are. A test that exercised
/// the real implementation would blank the screens of whoever was running it, and
/// the cases that actually need testing -- a television that is switched off, a
/// refresh rate Windows quietly refused, a monitor that did not come back --
/// cannot be arranged on the machine running the suite.
///
/// NOTHING IS CACHED ACROSS CALLS
///
/// Every method starts from a fresh query. A monitor can be unplugged, replaced,
/// or simply asleep between two calls, and an adapter or target identifier kept
/// from an earlier one is a handle to something that may no longer be there. This
/// is the same rule the audio provider keeps for its HDMI endpoint, for the same
/// reason and about the same television.
/// </remarks>
public interface IDisplayDevices
{
    /// <summary>
    /// Every monitor Windows has a slot for, active or not.
    /// </summary>
    /// <remarks>
    /// Includes the inactive ones deliberately. A television that is not
    /// currently driving a picture still has a target, and this is what lets a
    /// layout name it and switch to it -- unlike an audio endpoint, which
    /// vanishes entirely while its device is off.
    /// </remarks>
    IReadOnlyList<DisplayTarget> KnownTargets();

    /// <summary>The monitors showing a picture right now, with their placement.</summary>
    IReadOnlyList<ActiveDisplay> ActiveDisplays();

    /// <summary>
    /// The refresh rates each active monitor will accept at its current
    /// resolution, keyed by device path.
    /// </summary>
    /// <remarks>
    /// Only the active ones can answer, and only about the resolution they are
    /// at. This exists so that somebody writing a rate into the configuration can
    /// see what the monitor will actually take, rather than finding out when a
    /// mode fails.
    /// </remarks>
    IReadOnlyDictionary<string, IReadOnlyList<int>> SupportedRefreshRates();

    /// <summary>
    /// Makes exactly these monitors active, deactivating every other one.
    /// </summary>
    /// <param name="refreshHz">
    /// One entry per device path. 0 means "no opinion, let Windows choose"; any
    /// positive value asks for that many Hz.
    /// </param>
    /// <remarks>Throws when the topology could not be applied.</remarks>
    void Activate(IReadOnlyList<string> devicePaths, IReadOnlyList<int> refreshHz);

    /// <summary>
    /// Moves already-active monitors to the given virtual desktop positions.
    /// </summary>
    /// <remarks>Throws when the arrangement could not be applied.</remarks>
    void Arrange(IReadOnlyList<string> devicePaths, IReadOnlyList<int> x, IReadOnlyList<int> y);
}

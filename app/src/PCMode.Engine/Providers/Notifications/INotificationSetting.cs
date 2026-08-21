namespace PCMode.Engine;

/// <summary>
/// The Windows setting that decides whether a notification is shown.
/// </summary>
/// <remarks>
/// WHY THIS IS BEHIND AN INTERFACE, AND WHY IT MATTERS MORE HERE THAN ELSEWHERE
///
/// Because the implementation writes a registry value Microsoft never documented
/// and restarts a service, and a test that exercised either would be changing the
/// machine it was running on. Every rule worth having about Do Not Disturb -- that
/// it is off unless somebody asked for it, that On means suppressing toasts and
/// not the reverse, that a value nobody has written yet means the Windows default
/// -- is a rule about a decision, and this is what lets those be checked without a
/// registry in sight.
///
/// TOASTS ALLOWED, NOT 'DO NOT DISTURB'
///
/// The interface speaks in the setting's own terms, because that is what is
/// actually there: a flag saying whether Windows may show a toast. Do Not Disturb
/// is the opposite of it, and the inversion happens exactly once -- in
/// <see cref="DoNotDisturbProvider"/>, which is the thing that owns what On and
/// Off mean. Somewhere that stored the inverted sense as well would give the
/// system two places to get it backwards.
/// </remarks>
public interface INotificationSetting
{
    /// <summary>
    /// Whether Windows is currently allowed to show notifications, or null when
    /// nothing could be read.
    /// </summary>
    /// <remarks>
    /// An implementation is expected to answer true where nothing has ever been
    /// written: that is the Windows default, and it is a real answer rather than
    /// an absence. Null is for a lookup that failed.
    /// </remarks>
    bool? ToastsAllowed();

    /// <summary>Allows or suppresses notifications.</summary>
    void AllowToasts(bool allowed);

    /// <summary>
    /// Nudges Windows so that the change is noticed now rather than eventually.
    /// </summary>
    /// <remarks>
    /// Best effort by nature, and separate from the write for that reason. The
    /// setting has already been changed by the time this is called; a failure to
    /// make it take effect promptly is not a failure to make it.
    /// </remarks>
    void Refresh();
}

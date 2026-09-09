namespace PCMode.Tray.Platform.Picker;

/// <summary>
/// Which display the picker appears on.
/// </summary>
/// <remarks>
/// Ported ahead of the picker itself, because <c>ApplicationSettings</c>
/// persists it. Legacy defines this alongside <c>MonitorLayout</c>'s screen
/// geometry in its own <c>Picker/</c> folder; there is nothing to geometrically
/// resolve here yet, so this stays a plain enum next to the settings that read
/// and write it until the picker page gives it a real home.
/// </remarks>
public enum PickerMonitorChoice
{
    Primary,
    UnderPointer,
    WithForegroundWindow,
}

namespace PCMode.Tray.Elements;

/// <summary>
/// How a row wants its status read: nothing to say, fine, worth attention, or
/// wrong.
/// </summary>
/// <remarks>
/// A view-model concept, not a visual one. It lived nested inside the
/// <c>StatusDot</c> control, which meant every view model reporting a status
/// had to reference a WPF control to name one of four values.
/// </remarks>
public enum StatusLevel
{
    Neutral,
    Ok,
    Warning,
    Error,
}

namespace PCMode.Tray.UI.Components.Visualizations;

/// <summary>Where one monitor was let go of, in the arrangement surface's own coordinates.</summary>
/// <param name="Monitor">The item that was dragged, passed straight back untouched.</param>
/// <remarks>
/// The shape of a completed drag, and nothing more. It lives with the feature
/// rather than with the surface that raises it because the command handling it
/// is feature state: the surface is one way to produce a drop, not the only
/// conceivable one, and the feature must not depend on it.
/// </remarks>
public sealed record MonitorDrop(object Monitor, double Left, double Top);

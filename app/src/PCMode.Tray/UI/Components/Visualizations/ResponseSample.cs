namespace PCMode.Tray.UI.Components.Visualizations;

/// <summary>
/// One point on a computed equaliser response: how much a filter changes the
/// signal at one frequency.
/// </summary>
/// <param name="FrequencyHz">Where on the spectrum this was measured.</param>
/// <param name="MagnitudeDb">
/// The change there, in decibels. Unclamped and unscaled -- this is what the
/// filter math produced, not what fits in a plot.
/// </param>
/// <remarks>
/// The whole of what the Audio feature says about a response curve. How wide
/// the graph is, how many decibels fit on it, and where this lands in pixels
/// are all rendering decisions, and they belong to whatever draws the curve --
/// see <c>Elements/Audio/ResponseGraph</c>.
/// </remarks>
public readonly record struct ResponseSample(double FrequencyHz, double MagnitudeDb);

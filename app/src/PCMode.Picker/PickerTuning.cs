namespace PCMode.Picker;

/// <summary>
/// Every number that decides how the picker looks and moves.
/// </summary>
/// <remarks>
/// Sizes are fractions of the wheel diameter (or of the monitor's short side,
/// for the wheel itself), so
/// the picker is the same proportion of a 1080p laptop panel and a 4K
/// television without a second set of numbers for either.
///
/// Opacity, not hue, is what distinguishes a selected wedge from an inactive
/// one: the colour identifies the mode and never changes meaning.
/// </remarks>
public sealed record PickerTuning
{
    public static PickerTuning Default { get; } = new();

    // ---- size and geometry, as fractions -----------------------------------

    /// <summary>Wheel diameter as a fraction of the monitor's short side.</summary>
    public double ScreenFraction { get; init; } = 0.58;

    /// <summary>How far the selected wedge protrudes past the others.</summary>
    public double SelectedExtensionFraction { get; init; } = 0.06;

    public double HubDiameterFraction { get; init; } = 0.34;

    public double SeparatorFraction { get; init; } = 0.014;

    /// <summary>Breathing room so anti-aliasing is not clipped by the window edge.</summary>
    public double OuterMarginFraction { get; init; } = 0.009;

    // ---- animation ---------------------------------------------------------

    /// <summary>How long one wedge hands emphasis to the next.</summary>
    public TimeSpan SelectionDuration { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan OpeningDuration { get; init; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan ClosingDuration { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Roughly 100 frames a second, as in the original.</summary>
    public TimeSpan FrameInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    // ---- opacity -----------------------------------------------------------

    public double InactiveWedgeOpacity { get; init; } = 0.75;

    public double SelectedWedgeInnerOpacity { get; init; } = 0.50;

    public double SelectedWedgeOuterOpacity { get; init; } = 1.00;

    public double HubOpacity { get; init; } = 1.00;

    /// <summary>
    /// Zero by default, which means the separators are not visible at all.
    /// </summary>
    /// <remarks>
    /// That is the shipped appearance, not an oversight. Separators are drawn
    /// as an ordinary stroke rather than a cut-out, so raising this gives a thin
    /// dark line over the wedges rather than a transparent gap through them.
    /// </remarks>
    public double SeparatorOpacity { get; init; } = 0.00;

    // ---- labels and icons --------------------------------------------------

    /// <summary>How far out from the hub each wedge's icon and label sit.</summary>
    public double ContentRadiusFraction { get; init; } = 0.65;

    public double IconSizeFraction { get; init; } = 0.12;

    public double IconStrokeFraction { get; init; } = 0.004;

    public double SelectedIconScale { get; init; } = 1.3;

    public double SelectedIconStrokeScale { get; init; } = 1.5;

    public double LabelGapFraction { get; init; } = 0.00;

    public double LabelWidthFraction { get; init; } = 0.25;

    public double LabelHeightFraction { get; init; } = 0.065;

    public double LabelFontFraction { get; init; } = 0.03;

    public double SelectedLabelScale { get; init; } = 1.4;

    public double HubLabelGapFraction { get; init; } = 0.00;

    /// <summary>Nudge for the hub icon, as a fraction of wheel diameter.</summary>
    public PickerPoint HubIconOffset { get; init; } = new(0.000, -0.010);

    public PickerPoint HubLabelOffset { get; init; } = new(0.000, -0.020);

    // ---- colours -----------------------------------------------------------

    public PickerColor SeparatorColor { get; init; } = new(0x0F, 0x0F, 0x0F);

    public PickerColor DimLabelColor { get; init; } = new(0xC8, 0xC8, 0xC8);

    public PickerColor SelectedLabelColor { get; init; } = new(0xFF, 0xFF, 0xFF);

    // ---- derived hub sizes -------------------------------------------------
    // The hub shows the mode that is actually active, using the same
    // icon-over-name hierarchy a selected wedge does and a shade larger.

    public double HubIconSizeFraction => 1.1 * IconSizeFraction * SelectedIconScale;

    public double HubIconStrokeFraction => 1.1 * IconStrokeFraction * SelectedIconStrokeScale;

    public double HubFontFraction => 1.1 * LabelFontFraction * SelectedLabelScale;
}

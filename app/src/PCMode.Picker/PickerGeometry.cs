namespace PCMode.Picker;

/// <summary>A rectangle in physical device pixels.</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

/// <summary>A rectangle in the picker's own drawing units.</summary>
public readonly record struct PickerRect(double X, double Y, double Width, double Height);

/// <summary>What part of the picker a point landed on.</summary>
public enum PickerHitKind
{
    /// <summary>Outside the wheel. Nothing there.</summary>
    None,

    /// <summary>
    /// The centre circle, which shows the mode that is already active. Not a
    /// wedge: hovering it changes no selection, and clicking it re-applies the
    /// current mode.
    /// </summary>
    Hub,

    Wedge,
}

/// <summary>Where a point landed, and on which wedge if it was one.</summary>
public readonly record struct PickerHit(PickerHitKind Kind, PickerDirection Direction)
{
    public static PickerHit None => new(PickerHitKind.None, default);

    public static PickerHit Hub => new(PickerHitKind.Hub, default);
}

/// <summary>
/// Where every part of the picker goes, for one monitor at one scale factor.
/// </summary>
/// <remarks>
/// TWO COORDINATE SPACES, AND WHY BOTH ARE HERE
///
/// <see cref="WindowBounds"/> is in physical device pixels, because that is the
/// only space in which a window can be placed correctly on a machine with
/// mixed DPI. A window positioned in WPF's own units lands in the wrong place
/// whenever the target monitor scales differently from the primary one, and
/// PCMode exists to move a desktop between a monitor and a television, which
/// almost never share a scale factor. That case is the normal one here, not
/// the exotic one.
///
/// Everything else is in device-independent units, because that is the space
/// WPF lays out and reports mouse positions in. Keeping hit testing in the
/// same units as the pointer removes the entire class of bug where the picture
/// and the click target disagree by the scale factor.
///
/// The pixel arithmetic is done first, so a wheel is a whole number of pixels
/// across on any monitor, and the drawing units are derived from it.
/// </remarks>
public sealed class PickerGeometry
{
    private readonly Dictionary<PickerDirection, PickerPoint> _iconCenters;
    private readonly Dictionary<PickerDirection, PickerRect> _labelBoxes;
    private readonly PickerTuning _tuning;

    private PickerGeometry(
        PixelRect windowBounds,
        double scale,
        PickerTuning tuning,
        double canvasSize,
        double wheelDiameter,
        double selectedExtension,
        double hubRadius,
        double separatorWidth,
        Dictionary<PickerDirection, PickerPoint> iconCenters,
        Dictionary<PickerDirection, PickerRect> labelBoxes)
    {
        WindowBounds = windowBounds;
        Scale = scale;
        _tuning = tuning;
        CanvasSize = canvasSize;
        WheelDiameter = wheelDiameter;
        SelectedExtension = selectedExtension;
        HubRadius = hubRadius;
        SeparatorWidth = separatorWidth;
        _iconCenters = iconCenters;
        _labelBoxes = labelBoxes;
    }

    /// <summary>Where to put the window, in physical pixels.</summary>
    public PixelRect WindowBounds { get; }

    /// <summary>Physical pixels per drawing unit on the target monitor.</summary>
    public double Scale { get; }

    /// <summary>The canvas is square; this is its side, in drawing units.</summary>
    public double CanvasSize { get; }

    /// <summary>The centre of the canvas, on both axes.</summary>
    public double Center => CanvasSize / 2.0;

    /// <summary>Diameter of an unselected wedge's outer edge.</summary>
    public double WheelDiameter { get; }

    /// <summary>How far past that a selected wedge reaches.</summary>
    public double SelectedExtension { get; }

    /// <summary>Diameter of the selected wedge's outer edge.</summary>
    public double SelectedOuterDiameter => WheelDiameter + (2 * SelectedExtension);

    public double HubRadius { get; }

    public double SeparatorWidth { get; }

    public double LabelFontSize { get; private init; }

    public double HubFontSize { get; private init; }

    public double IconSize { get; private init; }

    public double IconStrokeWidth { get; private init; }

    public double HubIconSize { get; private init; }

    public double HubIconStrokeWidth { get; private init; }

    /// <summary>The start angle of a wedge, in degrees, clockwise from east.</summary>
    /// <remarks>
    /// Each wedge sweeps 90 degrees from here, so Up spans the quarter centred
    /// on straight up. Drawing and hit testing have to agree about the compass,
    /// which they only do when it is stated once, here.
    /// </remarks>
    public static double StartAngle(PickerDirection direction) => direction switch
    {
        PickerDirection.Up => -135,
        PickerDirection.Right => -45,
        PickerDirection.Down => 45,
        PickerDirection.Left => 135,
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    public const double WedgeSweep = 90;

    /// <summary>
    /// Works out the layout for a monitor.
    /// </summary>
    /// <param name="workArea">
    /// The monitor's work area in physical pixels. The work area rather than
    /// the full bounds, matching the original, so the picker is centred in the
    /// space a user actually sees rather than behind the taskbar.
    /// </param>
    /// <param name="scale">Physical pixels per drawing unit, for example 1.5 at 150%.</param>
    public static PickerGeometry Create(
        PixelRect workArea,
        double scale,
        FavoriteSet favorites,
        PickerTuning? tuning = null)
    {
        ArgumentNullException.ThrowIfNull(favorites);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(scale, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(workArea.Width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(workArea.Height, 0);

        PickerTuning settings = tuning ?? PickerTuning.Default;

        int shortSide = Math.Min(workArea.Width, workArea.Height);

        // Even, so that the centre falls on a pixel boundary rather than half
        // way through one, on every derived circle.
        int wheelPixels = MakeEven((int)Math.Round(shortSide * settings.ScreenFraction));

        int extensionPixels = Math.Max(0, (int)Math.Round(wheelPixels * settings.SelectedExtensionFraction));
        int marginPixels = Math.Max(3, (int)Math.Round(wheelPixels * settings.OuterMarginFraction));

        // The canvas is bigger than the wheel by exactly enough for the
        // selected wedge to protrude and still anti-alias cleanly.
        int canvasPixels = MakeEven(wheelPixels + (2 * (extensionPixels + marginPixels)));

        var window = new PixelRect(
            workArea.Left + ((workArea.Width - canvasPixels) / 2),
            workArea.Top + ((workArea.Height - canvasPixels) / 2),
            canvasPixels,
            canvasPixels);

        int hubPixels = MakeEven((int)Math.Round(wheelPixels * settings.HubDiameterFraction));

        double Units(double pixels) => pixels / scale;

        double canvas = Units(canvasPixels);
        double wheel = Units(wheelPixels);
        double center = canvas / 2.0;

        // Minimums are applied in pixels, as in the original: they exist so
        // that text and strokes stay legible on a small display, which is a
        // statement about physical size.
        double labelFont = Units(Math.Max(12, (int)Math.Round(wheelPixels * settings.LabelFontFraction)));
        double hubFont = Units(Math.Max(12, (int)Math.Round(wheelPixels * settings.HubFontFraction)));
        double iconSize = Units(Math.Max(24, (int)Math.Round(wheelPixels * settings.IconSizeFraction)));
        double iconStroke = Units(Math.Max(2, (int)Math.Round(wheelPixels * settings.IconStrokeFraction)));
        double hubIconSize = Units(Math.Max(24, (int)Math.Round(wheelPixels * settings.HubIconSizeFraction)));
        double hubIconStroke = Units(Math.Max(2, (int)Math.Round(wheelPixels * settings.HubIconStrokeFraction)));

        (Dictionary<PickerDirection, PickerPoint> icons, Dictionary<PickerDirection, PickerRect> labels) =
            BuildContent(favorites, settings, center, wheel, iconSize);

        return new PickerGeometry(
            window,
            scale,
            settings,
            canvas,
            wheel,
            Units(extensionPixels),
            Units(hubPixels) / 2.0,
            Units(Math.Max(3, (int)Math.Round(wheelPixels * settings.SeparatorFraction))),
            icons,
            labels)
        {
            LabelFontSize = labelFont,
            HubFontSize = hubFont,
            IconSize = iconSize,
            IconStrokeWidth = iconStroke,
            HubIconSize = hubIconSize,
            HubIconStrokeWidth = hubIconStroke,
        };
    }

    /// <summary>Where a wedge's icon is centred.</summary>
    public PickerPoint IconCenter(PickerDirection direction) => _iconCenters[direction];

    /// <summary>The box a wedge's label is laid out in.</summary>
    public PickerRect LabelBox(PickerDirection direction) => _labelBoxes[direction];

    /// <summary>
    /// The outer radius of one wedge, which depends on whether it is the
    /// selected one.
    /// </summary>
    public double OuterRadius(PickerDirection direction, PickerDirection selected) =>
        (direction == selected ? SelectedOuterDiameter : WheelDiameter) / 2.0;

    /// <summary>Whether a point is inside the hub.</summary>
    public bool IsInHub(PickerPoint point)
    {
        double dx = point.X - Center;
        double dy = point.Y - Center;

        return (dx * dx) + (dy * dy) <= HubRadius * HubRadius;
    }

    /// <summary>
    /// What a point landed on.
    /// </summary>
    /// <remarks>
    /// The quadrant is decided by comparing |dx| with |dy| rather than by
    /// computing an angle. That is the original's rule and it is worth keeping
    /// literally: it puts the boundaries exactly on the diagonals, it has no
    /// branch where an angle wraps, and it agrees with the drawn wedges because
    /// those are the same four quarter-circles.
    ///
    /// The selected wedge is physically bigger, so its hit region is too. A
    /// point in the protruding ring belongs to the selected wedge and to
    /// nothing else, which is what keeps a click landing where the user aimed
    /// it rather than falling through the gap beside it.
    /// </remarks>
    public PickerHit HitTest(PickerPoint point, PickerDirection selected)
    {
        if (IsInHub(point))
        {
            return PickerHit.Hub;
        }

        double dx = point.X - Center;
        double dy = point.Y - Center;

        PickerDirection direction = Math.Abs(dx) > Math.Abs(dy)
            ? (dx > 0 ? PickerDirection.Right : PickerDirection.Left)
            : (dy > 0 ? PickerDirection.Down : PickerDirection.Up);

        double radius = OuterRadius(direction, selected);

        return (dx * dx) + (dy * dy) > radius * radius
            ? PickerHit.None
            : new PickerHit(PickerHitKind.Wedge, direction);
    }

    /// <summary>
    /// The diameter of a wedge at a given selection amount, so the extension
    /// animates rather than snapping.
    /// </summary>
    public double WedgeDiameter(double amount) =>
        WheelDiameter + (2 * SelectedExtension * Math.Clamp(amount, 0.0, 1.0));

    private static (Dictionary<PickerDirection, PickerPoint> Icons, Dictionary<PickerDirection, PickerRect> Labels)
        BuildContent(FavoriteSet favorites, PickerTuning tuning, double center, double wheel, double iconSize)
    {
        double contentRadius = (wheel / 2.0) * tuning.ContentRadiusFraction;
        double labelGap = wheel * tuning.LabelGapFraction;
        double labelWidth = wheel * tuning.LabelWidthFraction;
        double labelHeight = wheel * tuning.LabelHeightFraction;

        Dictionary<PickerDirection, PickerPoint> icons = [];
        Dictionary<PickerDirection, PickerRect> labels = [];

        foreach (ModeFavorite favorite in favorites.All)
        {
            // Derived from the compass point, never listed per favorite. The
            // original once kept a second table naming all four modes again,
            // and a mode renamed in one and not the other lost its icon and
            // label silently while its wedge still drew.
            PickerPoint anchor = favorite.Direction switch
            {
                PickerDirection.Up => new PickerPoint(center, center - contentRadius),
                PickerDirection.Right => new PickerPoint(center + contentRadius, center),
                PickerDirection.Down => new PickerPoint(center, center + contentRadius),
                PickerDirection.Left => new PickerPoint(center - contentRadius, center),
                _ => throw new ArgumentOutOfRangeException(nameof(favorites)),
            };

            var iconCenter = new PickerPoint(
                anchor.X + (wheel * favorite.IconOffset.X),
                anchor.Y + (wheel * favorite.IconOffset.Y));

            icons[favorite.Direction] = iconCenter;

            // The label sits under its own icon and then takes its own nudge,
            // so moving an icon does not silently leave its text behind.
            labels[favorite.Direction] = new PickerRect(
                iconCenter.X - (labelWidth / 2.0) + (wheel * favorite.LabelOffset.X),
                iconCenter.Y + (iconSize / 2.0) + labelGap + (wheel * favorite.LabelOffset.Y),
                labelWidth,
                labelHeight);
        }

        return (icons, labels);
    }

    private static int MakeEven(int value) => (value % 2 == 0) ? value : value + 1;

    /// <summary>The tuning this layout was built from.</summary>
    public PickerTuning Tuning => _tuning;
}

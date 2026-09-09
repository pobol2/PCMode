using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using PCMode.Picker;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace PCMode.Tray.UI.Components.Picker;

/// <summary>
/// Paints the wheel.
/// </summary>
/// <remarks>
/// Reads state and draws it, and does nothing else -- it never changes a
/// selection, starts a timer, or decides anything. That division is what lets
/// every rule about behaviour be tested in a project (<c>PCMode.Picker</c>)
/// that cannot reference WPF at all.
///
/// One element rendering the whole wheel rather than a tree of shapes: the
/// picture changes every frame during a transition, and rebuilding a visual
/// tree a hundred times a second to move four wedges is work with nothing to
/// show for it.
/// </remarks>
public sealed class PickerCanvas : FrameworkElement
{
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface BoldFace = new(
        new FontFamily("Segoe UI"),
        FontStyles.Normal,
        FontWeights.Bold,
        FontStretches.Normal
    );

    private readonly PickerCoordinator _picker;

    public PickerCanvas(PickerCoordinator picker)
    {
        ArgumentNullException.ThrowIfNull(picker);

        _picker = picker;

        // The wheel is drawn edge to edge; nothing here should catch a click
        // that the window has already worked out the meaning of.
        IsHitTestVisible = false;
    }

    /// <summary>
    /// What a screen reader is told this is.
    /// </summary>
    /// <remarks>
    /// The wheel is a single rendered element, so there is nothing for
    /// automation to walk. Rather than leave it as an unnamed rectangle, the
    /// name carries what somebody who cannot see it needs: what this is, what
    /// is selected now, and what the machine is currently in.
    /// </remarks>
    public string AccessibleName =>
        $"PCMode mode picker. {_picker.Favorites[_picker.Selected].Label} selected. "
        + $"Currently {_picker.Current.Label}. "
        + "Use the arrow keys to choose, Enter to apply, Escape to close.";

    protected override AutomationPeer OnCreateAutomationPeer() => new PickerCanvasAutomationPeer(this);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        base.OnRender(drawingContext);

        PickerGeometry? geometry = _picker.Geometry;

        if (geometry is null)
        {
            return;
        }

        PickerTuning tuning = geometry.Tuning;
        var center = new Point(geometry.Center, geometry.Center);

        DrawWedges(drawingContext, geometry, tuning, center);
        DrawHub(drawingContext, geometry, tuning, center);
        DrawSeparators(drawingContext, geometry, tuning, center);
        DrawContent(drawingContext, geometry, tuning);
        DrawHubContent(drawingContext, geometry, tuning, center);
    }

    private void DrawWedges(DrawingContext context, PickerGeometry geometry, PickerTuning tuning, Point center)
    {
        foreach (ModeFavorite favorite in _picker.Favorites.All)
        {
            double amount = _picker.Amount(favorite.Direction);
            double diameter = geometry.WedgeDiameter(amount);
            double radius = diameter / 2;

            Geometry wedge = Pie(center, radius, PickerGeometry.StartAngle(favorite.Direction));

            if (amount <= 0.0001)
            {
                // No gradient needed for a wedge that is not selected at all,
                // and a flat fill is markedly cheaper per frame.
                context.DrawGeometry(Solid(favorite.Color, tuning.InactiveWedgeOpacity), null, wedge);
                continue;
            }

            // Selection reads as a radial brightening from the hub outwards.
            // Colour identifies the mode and never changes; only opacity says
            // which wedge is chosen.
            var brush = new RadialGradientBrush(
                Argb(
                    favorite.Color,
                    PickerEasing.Lerp(tuning.InactiveWedgeOpacity, tuning.SelectedWedgeInnerOpacity, amount)
                ),
                Argb(
                    favorite.Color,
                    PickerEasing.Lerp(tuning.InactiveWedgeOpacity, tuning.SelectedWedgeOuterOpacity, amount)
                )
            )
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = center,
                GradientOrigin = center,
                RadiusX = radius,
                RadiusY = radius,
            };

            brush.Freeze();

            context.DrawGeometry(brush, null, wedge);
        }
    }

    private void DrawHub(DrawingContext context, PickerGeometry geometry, PickerTuning tuning, Point center) =>
        context.DrawEllipse(
            Solid(_picker.Current.Color, tuning.HubOpacity),
            null,
            center,
            geometry.HubRadius,
            geometry.HubRadius
        );

    private static void DrawSeparators(
        DrawingContext context,
        PickerGeometry geometry,
        PickerTuning tuning,
        Point center
    )
    {
        if (tuning.SeparatorOpacity <= 0.0001)
        {
            // The shipped appearance. See PickerTuning.SeparatorOpacity for why
            // this is a deliberate zero rather than a forgotten one.
            return;
        }

        var pen = new Pen(Solid(tuning.SeparatorColor, tuning.SeparatorOpacity), geometry.SeparatorWidth);
        pen.Freeze();

        // The four boundaries are the diagonals, which is also where the hit
        // test divides the wheel.
        const double Diagonal = 0.70710678118654752;

        double inner = geometry.HubRadius * Diagonal;
        double outer = (geometry.SelectedOuterDiameter / 2) * Diagonal;

        foreach ((int sx, int sy) in new[] { (1, -1), (1, 1), (-1, 1), (-1, -1) })
        {
            context.DrawLine(
                pen,
                new Point(center.X + (sx * inner), center.Y + (sy * inner)),
                new Point(center.X + (sx * outer), center.Y + (sy * outer))
            );
        }

        context.DrawEllipse(null, pen, center, geometry.HubRadius, geometry.HubRadius);
    }

    private void DrawContent(DrawingContext context, PickerGeometry geometry, PickerTuning tuning)
    {
        foreach (ModeFavorite favorite in _picker.Favorites.All)
        {
            double amount = _picker.Amount(favorite.Direction);
            bool isSelected = favorite.Direction == _picker.Selected;

            Brush ink = Solid(isSelected ? tuning.SelectedLabelColor : tuning.DimLabelColor, 1.0);

            PickerPoint iconCenter = geometry.IconCenter(favorite.Direction);

            PickerIcons.Draw(
                context,
                favorite.Icon,
                new Point(iconCenter.X, iconCenter.Y),
                Math.Max(8, geometry.IconSize * PickerEasing.Lerp(1.0, tuning.SelectedIconScale, amount)),
                Math.Max(1, geometry.IconStrokeWidth * PickerEasing.Lerp(1.0, tuning.SelectedIconStrokeScale, amount)),
                ink
            );

            PickerRect box = geometry.LabelBox(favorite.Direction);

            DrawCenteredText(
                context,
                favorite.Label,
                new Rect(box.X, box.Y, box.Width, box.Height),
                Math.Max(1, geometry.LabelFontSize * PickerEasing.Lerp(1.0, tuning.SelectedLabelScale, amount)),
                ink,
                isSelected
            );
        }
    }

    private void DrawHubContent(DrawingContext context, PickerGeometry geometry, PickerTuning tuning, Point center)
    {
        Brush white = Solid(new PickerColor(0xFF, 0xFF, 0xFF), 1.0);

        // Icon over name, the same hierarchy a selected wedge uses, with the
        // pair centred as a group rather than each half centred on its own.
        double gap = geometry.WheelDiameter * tuning.HubLabelGapFraction;
        double groupHeight = geometry.HubIconSize + gap + (geometry.HubFontSize * 1.55);

        double iconCenterY = center.Y - (groupHeight / 2) + (geometry.HubIconSize / 2);

        PickerIcons.Draw(
            context,
            _picker.Current.Icon,
            new Point(
                center.X + (geometry.WheelDiameter * tuning.HubIconOffset.X),
                iconCenterY + (geometry.WheelDiameter * tuning.HubIconOffset.Y)
            ),
            geometry.HubIconSize,
            geometry.HubIconStrokeWidth,
            white
        );

        double textY = iconCenterY + (geometry.HubIconSize / 2) + gap;

        DrawCenteredText(
            context,
            _picker.Current.Label,
            new Rect(
                center.X - geometry.HubRadius + (geometry.WheelDiameter * tuning.HubLabelOffset.X),
                textY + (geometry.WheelDiameter * tuning.HubLabelOffset.Y),
                geometry.HubRadius * 2,
                geometry.HubFontSize * 1.65
            ),
            geometry.HubFontSize,
            white,
            bold: true
        );
    }

    private void DrawCenteredText(
        DrawingContext context,
        string text,
        Rect box,
        double fontSize,
        Brush brush,
        bool bold
    )
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0)
        {
            return;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            // Qualified: FrameworkElement has a property of the same name, and
            // an unqualified reference finds that instead of the enum.
            System.Windows.FlowDirection.LeftToRight,
            bold ? BoldFace : Face,
            fontSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        )
        {
            // Centred horizontally and unclipped vertically, matching the
            // original's string format. A long label overflows its box rather
            // than being cut in half.
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = Math.Max(1, box.Width),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        context.DrawText(formatted, new Point(box.X, box.Y));
    }

    /// <summary>A quarter-circle wedge starting at <paramref name="startAngle"/>.</summary>
    private static Geometry Pie(Point center, double radius, double startAngle)
    {
        Point from = OnCircle(center, radius, startAngle);
        Point to = OnCircle(center, radius, startAngle + PickerGeometry.WedgeSweep);

        var geometry = new StreamGeometry();

        using (StreamGeometryContext figure = geometry.Open())
        {
            figure.BeginFigure(center, isFilled: true, isClosed: true);
            figure.LineTo(from, isStroked: false, isSmoothJoin: false);
            figure.ArcTo(
                to,
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: false,
                SweepDirection.Clockwise,
                isStroked: false,
                isSmoothJoin: false
            );
        }

        geometry.Freeze();

        return geometry;
    }

    private static Point OnCircle(Point center, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180.0;

        return new Point(center.X + (radius * Math.Cos(radians)), center.Y + (radius * Math.Sin(radians)));
    }

    private static Color Argb(PickerColor color, double opacity) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0.0, 1.0) * 255), color.Red, color.Green, color.Blue);

    private static Brush Solid(PickerColor color, double opacity)
    {
        var brush = new SolidColorBrush(Argb(color, opacity));
        brush.Freeze();

        return brush;
    }

    private sealed class PickerCanvasAutomationPeer(PickerCanvas owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => ((PickerCanvas)Owner).AccessibleName;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

        protected override string GetClassNameCore() => nameof(PickerCanvas);
    }
}

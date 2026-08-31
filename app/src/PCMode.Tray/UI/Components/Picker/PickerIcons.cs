using System.Windows;
using System.Windows.Media;
using PCMode.Picker;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace PCMode.Tray.UI.Components.Picker;

/// <summary>
/// The wedge icons, drawn from numbers rather than loaded from files.
/// </summary>
/// <remarks>
/// Vector, and deliberately so: the wheel is sized as a fraction of the
/// monitor, so an icon has to be sharp at any size on any scale factor, and a
/// bitmap set covering that range is several files per icon that somebody has
/// to keep in step.
///
/// Chosen by <see cref="FavoriteIcon"/>, never by mode name. In the original
/// this switch was on the display name, so renaming a mode in the definitions
/// file silently cost it its icon here -- the wedge still drew, just empty, and
/// nothing reported anything.
///
/// The proportions are the original's, so the wheel looks like the one people
/// already use.
/// </remarks>
public static class PickerIcons
{
    /// <summary>Draws one icon centred on <paramref name="center"/>.</summary>
    public static void Draw(
        DrawingContext context,
        FavoriteIcon icon,
        Point center,
        double size,
        double strokeWidth,
        Brush brush)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(brush);

        if (icon == FavoriteIcon.None || size <= 0)
        {
            return;
        }

        var pen = new Pen(brush, strokeWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        pen.Freeze();

        double x = center.X - (size / 2);
        double y = center.Y - (size / 2);

        switch (icon)
        {
            case FavoriteIcon.Monitor:
                DrawMonitor(context, pen, center, x, y, size);
                break;

            case FavoriteIcon.PlayTile:
                DrawPlayTile(context, pen, brush, center, size);
                break;

            case FavoriteIcon.Gamepad:
                DrawGamepad(context, pen, brush, center, x, y, size);
                break;

            case FavoriteIcon.FilmStrip:
                DrawFilmStrip(context, pen, x, y, size);
                break;
        }
    }

    private static void DrawMonitor(DrawingContext context, Pen pen, Point center, double x, double y, double size)
    {
        double screenX = x + (size * 0.09);
        double screenY = y + (size * 0.12);
        double screenWidth = size * 0.82;
        double screenHeight = size * 0.56;

        context.DrawRectangle(null, pen, new Rect(screenX, screenY, screenWidth, screenHeight));

        // Stand: a stem down from the panel, then a foot.
        context.DrawLine(
            pen,
            new Point(center.X, screenY + screenHeight),
            new Point(center.X, y + (size * 0.82)));

        context.DrawLine(
            pen,
            new Point(x + (size * 0.31), y + (size * 0.83)),
            new Point(x + (size * 0.69), y + (size * 0.83)));
    }

    private static void DrawPlayTile(DrawingContext context, Pen pen, Brush brush, Point center, double size)
    {
        double width = size * 0.84;
        double height = size * 0.58;
        double radius = size * 0.18;

        context.DrawRoundedRectangle(
            null,
            pen,
            new Rect(center.X - (width / 2), center.Y - (height / 2), width, height),
            radius,
            radius);

        // The triangle is nudged right of centre so it LOOKS centred: a
        // triangle balanced on its bounding box reads as sitting too far left.
        var play = new StreamGeometry();

        using (StreamGeometryContext figure = play.Open())
        {
            figure.BeginFigure(new Point(center.X - (size * 0.08), center.Y - (size * 0.13)), isFilled: true, isClosed: true);
            figure.LineTo(new Point(center.X - (size * 0.08), center.Y + (size * 0.13)), isStroked: false, isSmoothJoin: false);
            figure.LineTo(new Point(center.X + (size * 0.13), center.Y), isStroked: false, isSmoothJoin: false);
        }

        play.Freeze();

        context.DrawGeometry(brush, null, play);
    }

    private static void DrawGamepad(
        DrawingContext context,
        Pen pen,
        Brush brush,
        Point center,
        double x,
        double y,
        double size)
    {
        double bodyWidth = size * 0.90;
        double bodyHeight = size * 0.54;

        context.DrawEllipse(
            null,
            pen,
            new Point(x + (size * 0.05) + (bodyWidth / 2), y + (size * 0.23) + (bodyHeight / 2)),
            bodyWidth / 2,
            bodyHeight / 2);

        double crossX = x + (size * 0.31);
        double arm = size * 0.10;

        context.DrawLine(pen, new Point(crossX - arm, center.Y), new Point(crossX + arm, center.Y));
        context.DrawLine(pen, new Point(crossX, center.Y - arm), new Point(crossX, center.Y + arm));

        double buttonRadius = size * 0.045;

        context.DrawEllipse(
            brush,
            null,
            new Point(x + (size * 0.68), center.Y - (size * 0.07)),
            buttonRadius,
            buttonRadius);

        context.DrawEllipse(
            brush,
            null,
            new Point(x + (size * 0.79), center.Y + (size * 0.06)),
            buttonRadius,
            buttonRadius);
    }

    private static void DrawFilmStrip(DrawingContext context, Pen pen, double x, double y, double size)
    {
        double boxX = x + (size * 0.10);
        double boxY = y + (size * 0.15);
        double boxWidth = size * 0.80;
        double boxHeight = size * 0.70;

        context.DrawRectangle(null, pen, new Rect(boxX, boxY, boxWidth, boxHeight));

        double top = boxY + (size * 0.15);
        double bottom = boxY + boxHeight - (size * 0.15);

        context.DrawLine(pen, new Point(boxX, top), new Point(boxX + boxWidth, top));
        context.DrawLine(pen, new Point(boxX, bottom), new Point(boxX + boxWidth, bottom));

        // Three sprocket ticks along each edge.
        for (int index = 1; index <= 3; index++)
        {
            double tick = boxX + (boxWidth * (index / 4.0));

            context.DrawLine(pen, new Point(tick, boxY), new Point(tick, top));
            context.DrawLine(pen, new Point(tick, bottom), new Point(tick, boxY + boxHeight));
        }
    }
}

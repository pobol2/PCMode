using System.Windows;
using System.Windows.Media;
using System.Windows.Forms;
using DrawingColor = System.Drawing.Color;

namespace PCMode.Tray.Themes;

/// <summary>Bridges the application's semantic WPF palette to its native WinForms tray menu.</summary>
public static class TrayMenuTheme
{
    public static void Apply(ContextMenuStrip menu, ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(resources);

        var colors = new TrayMenuColors(
            Background: Read(resources, "SurfaceBrush"),
            Text: Read(resources, "ForegroundBrush"),
            DisabledText: Read(resources, "DisabledTextBrush"),
            Border: Read(resources, "BorderBrush"),
            Hover: Read(resources, "SurfaceHoverBrush"),
            Pressed: Read(resources, "SurfaceHoverBrush"),
            Selected: Read(resources, "SurfaceHoverBrush"),
            Accent: Read(resources, "AccentBrush"),
            AccentText: Read(resources, "AccentTextBrush"));

        menu.BackColor = colors.Background;
        menu.ForeColor = colors.Text;
        menu.Renderer = new TrayMenuRenderer(colors);
        menu.Invalidate();
    }

    private static DrawingColor Read(ResourceDictionary resources, string key)
    {
        if (resources[key] is not SolidColorBrush brush)
        {
            throw new InvalidOperationException($"The theme resource '{key}' is not a solid color brush.");
        }

        System.Windows.Media.Color color = brush.Color;
        return DrawingColor.FromArgb(color.A, color.R, color.G, color.B);
    }

    private sealed record TrayMenuColors(
        DrawingColor Background,
        DrawingColor Text,
        DrawingColor DisabledText,
        DrawingColor Border,
        DrawingColor Hover,
        DrawingColor Pressed,
        DrawingColor Selected,
        DrawingColor Accent,
        DrawingColor AccentText);

    private sealed class TrayMenuRenderer(TrayMenuColors colors)
        : ToolStripProfessionalRenderer(new TrayMenuColorTable(colors))
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? colors.Text : colors.DisabledText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var checkBounds = e.ImageRectangle;
            checkBounds.Inflate(-2, -2);

            using var background = new System.Drawing.SolidBrush(colors.Accent);
            e.Graphics.FillRectangle(background, checkBounds);
            ControlPaint.DrawMenuGlyph(
                e.Graphics,
                e.ImageRectangle,
                MenuGlyph.Checkmark,
                colors.AccentText,
                DrawingColor.Transparent);
        }
    }

    private sealed class TrayMenuColorTable(TrayMenuColors colors) : ProfessionalColorTable
    {
        public override DrawingColor ToolStripDropDownBackground => colors.Background;
        public override DrawingColor MenuBorder => colors.Border;
        public override DrawingColor MenuItemBorder => colors.Border;
        public override DrawingColor MenuItemSelected => colors.Hover;
        public override DrawingColor MenuItemPressedGradientBegin => colors.Pressed;
        public override DrawingColor MenuItemPressedGradientMiddle => colors.Pressed;
        public override DrawingColor MenuItemPressedGradientEnd => colors.Pressed;
        public override DrawingColor CheckBackground => colors.Selected;
        public override DrawingColor CheckSelectedBackground => colors.Hover;
        public override DrawingColor CheckPressedBackground => colors.Pressed;
        public override DrawingColor ImageMarginGradientBegin => colors.Background;
        public override DrawingColor ImageMarginGradientMiddle => colors.Background;
        public override DrawingColor ImageMarginGradientEnd => colors.Background;
        public override DrawingColor SeparatorDark => colors.Border;
        public override DrawingColor SeparatorLight => colors.Background;
    }
}

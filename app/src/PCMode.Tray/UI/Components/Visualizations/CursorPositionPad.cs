using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

// The tray enables WinForms for the notify icon, whose implicit usings
// bring System.Drawing's Point and System.Windows.Forms' Control into
// scope alongside WPF's own.
using Control = System.Windows.Controls.Control;
using Point = System.Windows.Point;

namespace PCMode.Tray.UI.Components.Visualizations;

/// <summary>
/// The cursor-position surface: a stand-in for the primary display, with a
/// marker that can be dragged to where the pointer should be placed.
/// </summary>
/// <remarks>
/// A final UI component for the same reason <see cref="MonitorArrangementPad"/>
/// is one -- a drag is pointer state, which neither a lookless block nor a
/// <see cref="ControlTemplate"/> can hold. It owns only the translation
/// between a point on this surface and the four values a cursor position is
/// actually stored as; what those values mean, and where they are saved, stay
/// with <see cref="CursorSectionViewModel"/>.
///
/// <see cref="TargetWidth"/>/<see cref="TargetHeight"/> matter only in the
/// from-right/from-bottom modes, where the stored value is a pixel distance
/// and the surface has to know how many pixels the display it stands for
/// actually has.
/// </remarks>
[TemplatePart(Name = SurfacePart, Type = typeof(Canvas))]
[TemplatePart(Name = MarkerPart, Type = typeof(FrameworkElement))]
public sealed class CursorPositionPad : Control
{
    public const string SurfacePart = "PART_Surface";
    public const string MarkerPart = "PART_Marker";
    private const string PercentageMode = "Percentage";

    static CursorPositionPad()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(CursorPositionPad),
            new FrameworkPropertyMetadata(typeof(CursorPositionPad)));
    }

    public static readonly DependencyProperty XModeProperty =
        DependencyProperty.Register(
            nameof(XMode), typeof(string), typeof(CursorPositionPad),
            new FrameworkPropertyMetadata(
                PercentageMode,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnPositionChanged));

    public static readonly DependencyProperty YModeProperty =
        DependencyProperty.Register(
            nameof(YMode), typeof(string), typeof(CursorPositionPad),
            new FrameworkPropertyMetadata(
                PercentageMode,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnPositionChanged));

    public static readonly DependencyProperty XValueProperty =
        DependencyProperty.Register(
            nameof(XValue), typeof(double), typeof(CursorPositionPad),
            new FrameworkPropertyMetadata(50d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPositionChanged));

    public static readonly DependencyProperty YValueProperty =
        DependencyProperty.Register(
            nameof(YValue), typeof(double), typeof(CursorPositionPad),
            new FrameworkPropertyMetadata(50d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPositionChanged));

    /// <summary>The real pixel width of the display this surface stands for.</summary>
    public static readonly DependencyProperty TargetWidthProperty =
        DependencyProperty.Register(
            nameof(TargetWidth), typeof(double), typeof(CursorPositionPad),
            new FrameworkPropertyMetadata(1920d, OnPositionChanged));

    public static readonly DependencyProperty TargetHeightProperty =
        DependencyProperty.Register(
            nameof(TargetHeight), typeof(double), typeof(CursorPositionPad),
            new FrameworkPropertyMetadata(1080d, OnPositionChanged));

    /// <summary>Whether there is a linked position to place at all; the marker is hidden and dragging refused when there is not.</summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive), typeof(bool), typeof(CursorPositionPad),
            new FrameworkPropertyMetadata(false, OnPositionChanged));

    public string XMode
    {
        get => (string)GetValue(XModeProperty);
        set => SetValue(XModeProperty, value);
    }

    public string YMode
    {
        get => (string)GetValue(YModeProperty);
        set => SetValue(YModeProperty, value);
    }

    public double XValue
    {
        get => (double)GetValue(XValueProperty);
        set => SetValue(XValueProperty, value);
    }

    public double YValue
    {
        get => (double)GetValue(YValueProperty);
        set => SetValue(YValueProperty, value);
    }

    public double TargetWidth
    {
        get => (double)GetValue(TargetWidthProperty);
        set => SetValue(TargetWidthProperty, value);
    }

    public double TargetHeight
    {
        get => (double)GetValue(TargetHeightProperty);
        set => SetValue(TargetHeightProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private Canvas? _surface;
    private FrameworkElement? _marker;
    private bool _dragging;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_surface is not null)
        {
            _surface.PreviewMouseLeftButtonDown -= OnSurfaceMouseDown;
            _surface.PreviewMouseMove -= OnSurfaceMouseMove;
            _surface.PreviewMouseLeftButtonUp -= OnSurfaceMouseUp;
            _surface.LostMouseCapture -= OnSurfaceLostCapture;
            _surface.SizeChanged -= OnSurfaceSizeChanged;
        }

        _surface = GetTemplateChild(SurfacePart) as Canvas;
        _marker = GetTemplateChild(MarkerPart) as FrameworkElement;

        if (_surface is not null)
        {
            _surface.PreviewMouseLeftButtonDown += OnSurfaceMouseDown;
            _surface.PreviewMouseMove += OnSurfaceMouseMove;
            _surface.PreviewMouseLeftButtonUp += OnSurfaceMouseUp;
            _surface.LostMouseCapture += OnSurfaceLostCapture;
            _surface.SizeChanged += OnSurfaceSizeChanged;
        }

        UpdateMarker();
    }

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CursorPositionPad)d).UpdateMarker();

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => UpdateMarker();

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsActive || _surface is null)
        {
            return;
        }

        _dragging = true;
        _surface.CaptureMouse();
        ApplyPoint(e.GetPosition(_surface));
        e.Handled = true;
    }

    private void OnSurfaceMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed || _surface is null)
        {
            return;
        }

        ApplyPoint(e.GetPosition(_surface));
        e.Handled = true;
    }

    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || _surface is null)
        {
            return;
        }

        ApplyPoint(e.GetPosition(_surface));
        _dragging = false;
        _surface.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnSurfaceLostCapture(object sender, System.Windows.Input.MouseEventArgs e) => _dragging = false;

    private void ApplyPoint(Point point)
    {
        if (_surface is null)
        {
            return;
        }

        double width = Math.Max(1, _surface.ActualWidth);
        double height = Math.Max(1, _surface.ActualHeight);

        double xFraction = Math.Clamp(point.X / width, 0, 1);
        double yFraction = Math.Clamp(point.Y / height, 0, 1);

        XValue = XMode == PercentageMode
            ? Math.Round(xFraction * 100, 1, MidpointRounding.AwayFromZero)
            : Math.Round((1 - xFraction) * Math.Max(1, TargetWidth), MidpointRounding.AwayFromZero);

        YValue = YMode == PercentageMode
            ? Math.Round(yFraction * 100, 1, MidpointRounding.AwayFromZero)
            : Math.Round((1 - yFraction) * Math.Max(1, TargetHeight), MidpointRounding.AwayFromZero);
    }

    private void UpdateMarker()
    {
        if (_surface is null || _marker is null)
        {
            return;
        }

        _marker.Visibility = IsActive ? Visibility.Visible : Visibility.Collapsed;

        double width = _surface.ActualWidth;
        double height = _surface.ActualHeight;

        if (width <= 0 || height <= 0 || !IsActive)
        {
            return;
        }

        double availableWidth = Math.Max(0, width - _marker.Width);
        double availableHeight = Math.Max(0, height - _marker.Height);

        double xFraction = XMode == PercentageMode
            ? Math.Clamp(XValue / 100, 0, 1)
            : Math.Clamp((Math.Max(1, TargetWidth) - XValue) / Math.Max(1, TargetWidth), 0, 1);

        double yFraction = YMode == PercentageMode
            ? Math.Clamp(YValue / 100, 0, 1)
            : Math.Clamp((Math.Max(1, TargetHeight) - YValue) / Math.Max(1, TargetHeight), 0, 1);

        Canvas.SetLeft(_marker, xFraction * availableWidth);
        Canvas.SetTop(_marker, yFraction * availableHeight);
    }
}

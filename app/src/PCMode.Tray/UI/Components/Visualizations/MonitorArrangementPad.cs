using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

// The tray enables WinForms for the notify icon, whose implicit usings
// bring System.Drawing's own Point into scope alongside WPF's.
using MouseEventHandler = System.Windows.Input.MouseEventHandler;
using Point = System.Windows.Point;

namespace PCMode.Tray.UI.Components.Visualizations;

/// <summary>
/// The monitor arrangement surface: monitors laid out on a canvas, picked by
/// clicking and re-placed by dragging one beside another.
/// </summary>
/// <remarks>
/// A final UI component, not a Functional Block. A block is lookless and a
/// Layout is a <see cref="ControlTemplate"/>; neither can own the pointer
/// state a drag needs -- where the gesture started, whether it has passed the
/// drag threshold, which element is being moved. So the gesture lives here,
/// in a control of its own that a region ends in, and the block above stays
/// free of it.
///
/// It knows nothing about display layouts. Items are plain objects it never
/// inspects: their position comes from the <c>ItemContainerStyle</c> a Layout
/// supplies (Canvas.Left/Top), and what a click or a drop means is entirely
/// up to <see cref="SelectCommand"/> and <see cref="SnapCommand"/>.
/// </remarks>
public sealed class MonitorArrangementPad : ItemsControl
{
    static MonitorArrangementPad()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(MonitorArrangementPad),
            new FrameworkPropertyMetadata(typeof(MonitorArrangementPad)));
    }

    public MonitorArrangementPad()
    {
        AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPreviewMouseLeftButtonDown), handledEventsToo: true);
        AddHandler(PreviewMouseMoveEvent, new MouseEventHandler(OnPreviewMouseMove), handledEventsToo: true);
        AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnPreviewMouseLeftButtonUp), handledEventsToo: true);
        AddHandler(LostMouseCaptureEvent, new MouseEventHandler((_, _) => ResetDrag()), handledEventsToo: true);
    }

    /// <summary>Run with the clicked item as its parameter, on press rather than on click -- a drag selects what it is about to move.</summary>
    public static readonly DependencyProperty SelectCommandProperty =
        DependencyProperty.Register(
            nameof(SelectCommand),
            typeof(System.Windows.Input.ICommand),
            typeof(MonitorArrangementPad));

    /// <summary>Run with a <see cref="MonitorDrop"/> once a real drag ends. A press that never moved is a selection, not a drop.</summary>
    public static readonly DependencyProperty SnapCommandProperty =
        DependencyProperty.Register(
            nameof(SnapCommand),
            typeof(System.Windows.Input.ICommand),
            typeof(MonitorArrangementPad));

    public System.Windows.Input.ICommand? SelectCommand
    {
        get => (System.Windows.Input.ICommand?)GetValue(SelectCommandProperty);
        set => SetValue(SelectCommandProperty, value);
    }

    public System.Windows.Input.ICommand? SnapCommand
    {
        get => (System.Windows.Input.ICommand?)GetValue(SnapCommandProperty);
        set => SetValue(SnapCommandProperty, value);
    }

    private FrameworkElement? _container;
    private object? _item;
    private Point _start;
    private bool _dragging;

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ContainerFromElement(this, source) is not FrameworkElement container)
        {
            return;
        }

        object item = ItemContainerGenerator.ItemFromContainer(container);

        if (item is null || item == DependencyProperty.UnsetValue)
        {
            return;
        }

        _container = container;
        _item = item;
        _start = e.GetPosition(this);
        _dragging = false;
        container.RenderTransform = new TranslateTransform();

        if (SelectCommand is { } select && select.CanExecute(item))
        {
            select.Execute(item);
        }
    }

    private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            _container is not { RenderTransform: TranslateTransform transform })
        {
            return;
        }

        Vector movement = e.GetPosition(this) - _start;

        if (!_dragging &&
            Math.Abs(movement.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(movement.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (!_dragging)
        {
            // Captured only once the gesture is genuinely a drag, so a plain
            // click still reaches the item the ordinary way.
            _dragging = true;
            CaptureMouse();
        }

        transform.X = movement.X;
        transform.Y = movement.Y;
        e.Handled = true;
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_container is not { } container || _item is not { } item)
        {
            return;
        }

        Vector movement = e.GetPosition(this) - _start;
        bool dropped = _dragging;
        double left = Canvas.GetLeft(container);
        double top = Canvas.GetTop(container);

        ResetDrag();

        if (!dropped)
        {
            return;
        }

        var drop = new MonitorDrop(
            item,
            (double.IsNaN(left) ? 0 : left) + movement.X,
            (double.IsNaN(top) ? 0 : top) + movement.Y);

        if (SnapCommand is { } snap && snap.CanExecute(drop))
        {
            snap.Execute(drop);
        }

        e.Handled = true;
    }

    private void ResetDrag()
    {
        if (_container is not null)
        {
            _container.RenderTransform = Transform.Identity;
        }

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        _container = null;
        _item = null;
        _dragging = false;
    }
}

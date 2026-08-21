using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCMode.Tray.Controls.PageShells;

namespace PCMode.Tray.Features.Layouts;

/// <summary>
/// Live status ticks while this page is open -- the one page whose canvas
/// reflects which monitor Windows currently has active, not just what was
/// last loaded from disk. Every other page refreshes only on
/// activation; this one also opts into <see cref="ConfigurationPageShell.EnableLiveRefresh"/>.
/// </summary>
public partial class LayoutsPage : ConfigurationPageShell
{
    private static readonly TimeSpan LiveStatusInterval = TimeSpan.FromSeconds(3);

    private System.Windows.Controls.Button? _dragButton;
    private LayoutMonitorRow? _dragMonitor;
    private System.Windows.Point _dragStart;
    private bool _dragging;

    public LayoutsPage(LayoutsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        DataContext = viewModel;

        EnableLiveRefresh(LiveStatusInterval, viewModel.RefreshLiveStatusAsync);
    }

    private void OnNewLayoutClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LayoutsViewModel viewModel)
        {
            return;
        }

        PromptAndApply(
            "Create display layout",
            "Name the new layout. It starts with no monitors selected.",
            string.Empty,
            viewModel.StartNewLayout);
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LayoutsViewModel viewModel || viewModel.SelectedLayout is not { } current)
        {
            return;
        }

        PromptAndApply(
            "Rename display layout",
            $"Rename '{current}'. Any mode that uses it is updated to match.",
            current,
            viewModel.RenameSelected);
    }

    private void OnDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LayoutsViewModel viewModel || viewModel.SelectedLayout is not { } current)
        {
            return;
        }

        PromptAndApply(
            "Duplicate display layout",
            $"Name the copy of '{current}'. Its settings open as a draft; change the arrangement, then Save.",
            $"{current} copy",
            name => viewModel.DuplicateSelected(name));
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LayoutsViewModel viewModel || viewModel.SelectedLayout is not { } name)
        {
            return;
        }

        ConfirmAndApply(
            $"Delete '{name}'? This cannot be undone.",
            () => viewModel.DeleteSelected());
    }

    private void OnActivateClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is LayoutsViewModel viewModel)
        {
            _ = viewModel.ActivateSelectedAsync();
        }
    }

    private void OnMonitorClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: LayoutMonitorRow monitor } && DataContext is LayoutsViewModel viewModel)
        {
            viewModel.SelectMonitor(monitor);
        }
    }

    private void OnMonitorDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            sender is not System.Windows.Controls.Button { DataContext: LayoutMonitorRow { Included: true } monitor } button ||
            DataContext is not LayoutsViewModel viewModel)
        {
            return;
        }

        viewModel.SelectMonitor(monitor);
        _dragButton = button;
        _dragMonitor = monitor;
        _dragStart = e.GetPosition(MonitorArrangement);
        _dragging = false;
        button.RenderTransform = new TranslateTransform();
    }

    private void OnMonitorDragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            sender is not System.Windows.Controls.Button button ||
            button != _dragButton ||
            _dragMonitor is null ||
            button.RenderTransform is not TranslateTransform transform)
        {
            return;
        }

        System.Windows.Point point = e.GetPosition(MonitorArrangement);
        Vector movement = point - _dragStart;

        if (!_dragging &&
            Math.Abs(movement.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(movement.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragging = true;
        transform.X = movement.X;
        transform.Y = movement.Y;
        e.Handled = true;
    }

    private void OnMonitorDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button != _dragButton || _dragMonitor is not { } monitor)
        {
            return;
        }

        System.Windows.Point point = e.GetPosition(MonitorArrangement);
        Vector movement = point - _dragStart;
        bool snapped = _dragging;
        ResetDragVisual();

        if (snapped && DataContext is LayoutsViewModel viewModel)
        {
            if (button.IsMouseCaptured)
            {
                button.ReleaseMouseCapture();
            }

            viewModel.TrySnapMonitor(
                monitor,
                monitor.PreviewLeft + movement.X,
                monitor.PreviewTop + movement.Y);
            e.Handled = true;
        }
    }

    private void OnMonitorLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e) => ResetDragVisual();

    private void ResetDragVisual()
    {
        if (_dragButton is not null)
        {
            _dragButton.RenderTransform = Transform.Identity;
        }

        _dragButton = null;
        _dragMonitor = null;
        _dragging = false;
    }
}

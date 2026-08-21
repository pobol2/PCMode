using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCMode.Engine;
using PCMode.Tray.Controls.PageShells;

namespace PCMode.Tray.Features.Cursor;

public partial class CursorPage : ConfigurationPageShell
{
    private DisplayRect _primaryDisplay;
    private bool _draggingPreview;

    public CursorPage(CursorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        DataContext = viewModel;
        _primaryDisplay = ReadPrimaryDisplay();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnPageLoaded;
        UpdateMarker(viewModel);
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _primaryDisplay = ReadPrimaryDisplay();

        if (DataContext is CursorViewModel viewModel)
        {
            UpdateMarker(viewModel);
        }
    }

    private static DisplayRect ReadPrimaryDisplay()
    {
        try
        {
            return new WindowsPrimaryDisplay().PrimaryBounds();
        }
        catch (Exception)
        {
            // The editor remains usable in a disconnected or test session. The
            // fallback affects only the preview of pixel offsets, never what is
            // stored or how the cursor provider resolves the saved position.
            return new DisplayRect(0, 0, 1920, 1080);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not CursorViewModel viewModel)
        {
            return;
        }

        if (e.PropertyName is nameof(CursorViewModel.XMode) or
            nameof(CursorViewModel.XValue) or
            nameof(CursorViewModel.YMode) or
            nameof(CursorViewModel.YValue) or
            nameof(CursorViewModel.SelectedPosition))
        {
            UpdateMarker(viewModel);
        }
    }

    private void OnNewPositionClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CursorViewModel viewModel)
        {
            return;
        }

        PromptAndApply(
            "Create cursor position",
            "Name the cursor position. It is created at the display's center; adjust it, then Save.",
            string.Empty,
            viewModel.CreatePosition);
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CursorViewModel viewModel || viewModel.SelectedPosition is not { } current)
        {
            return;
        }

        PromptAndApply(
            "Rename cursor position",
            $"Rename '{current}'. Any mode that uses it is updated to match.",
            current,
            viewModel.RenameSelected);
    }

    private void OnDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CursorViewModel viewModel || viewModel.SelectedPosition is not { } current)
        {
            return;
        }

        PromptAndApply(
            "Duplicate cursor position",
            $"Name the copy of '{current}'. Adjust it if needed, then Save.",
            $"{current} copy",
            name => viewModel.DuplicatePosition(name, current));
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CursorViewModel viewModel || viewModel.SelectedPosition is not { } name)
        {
            return;
        }

        ConfirmAndApply(
            $"Delete '{name}'? This cannot be undone.",
            () => viewModel.DeleteSelected());
    }

    private void OnActivateClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is CursorViewModel viewModel && ConfirmDiscardIfDirty())
        {
            viewModel.ActivateSelected();
        }
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not CursorViewModel { HasSelectedPosition: true } viewModel)
        {
            return;
        }

        _draggingPreview = true;
        CursorPreview.CaptureMouse();
        UpdateFromPreviewPoint(viewModel, e.GetPosition(CursorPreview));
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_draggingPreview || e.LeftButton != MouseButtonState.Pressed ||
            DataContext is not CursorViewModel viewModel)
        {
            return;
        }

        UpdateFromPreviewPoint(viewModel, e.GetPosition(CursorPreview));
        e.Handled = true;
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingPreview)
        {
            return;
        }

        if (DataContext is CursorViewModel viewModel)
        {
            UpdateFromPreviewPoint(viewModel, e.GetPosition(CursorPreview));
        }

        _draggingPreview = false;
        CursorPreview.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnPreviewLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e) => _draggingPreview = false;

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is CursorViewModel viewModel)
        {
            UpdateMarker(viewModel);
        }
    }

    private void UpdateFromPreviewPoint(CursorViewModel viewModel, System.Windows.Point point)
    {
        double width = Math.Max(1, CursorPreview.ActualWidth);
        double height = Math.Max(1, CursorPreview.ActualHeight);
        viewModel.SetFromPreview(point.X / width, point.Y / height, _primaryDisplay.Width, _primaryDisplay.Height);
    }

    private void UpdateMarker(CursorViewModel viewModel)
    {
        double width = CursorPreview.ActualWidth;
        double height = CursorPreview.ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        double availableWidth = Math.Max(0, width - CursorMarker.Width);
        double availableHeight = Math.Max(0, height - CursorMarker.Height);
        Canvas.SetLeft(CursorMarker, viewModel.PreviewXFraction(_primaryDisplay.Width) * availableWidth);
        Canvas.SetTop(CursorMarker, viewModel.PreviewYFraction(_primaryDisplay.Height) * availableHeight);
    }
}

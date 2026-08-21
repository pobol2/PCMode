using System.Windows;
using System.Windows.Controls;
using PCMode.Tray.Dialogs;
using PCMode.Tray.Controls.PageShells;
namespace PCMode.Tray.Features.Applications;

public partial class ApplicationsPage : ConfigurationPageShell
{
    private static readonly TimeSpan LiveStatusInterval = TimeSpan.FromSeconds(3);

    public ApplicationsPage(ApplicationsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        DataContext = viewModel;

        EnableLiveRefresh(LiveStatusInterval, viewModel.RefreshRunningStatesAsync);
    }

    private void OnNewApplicationClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ApplicationsViewModel viewModel || !ConfirmDiscardIfDirty())
        {
            return;
        }

        var dialog = new NewApplicationWindow(
            (name, launchOnly, process, start) => Task.FromResult(viewModel.CreateApplication(name, launchOnly, process, start)))
        {
            Owner = Window.GetWindow(this),
        };

        dialog.ShowDialog();
    }
    private async void OnActivateClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ApplicationsViewModel viewModel)
        {
            await viewModel.ActivateSelectedAsync().ConfigureAwait(true);
        }
    }

    private void OnDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ApplicationsViewModel viewModel || viewModel.SelectedApplication is not { } source)
        {
            return;
        }

        PromptAndApply(
            "Duplicate application",
            $"Name the copy of '{source}'. Everything -- including dependencies and the readiness action -- is copied.",
            $"{source} copy",
            name => viewModel.DuplicateApplication(name, source));
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ApplicationsViewModel viewModel || viewModel.SelectedApplication is not { } current)
        {
            return;
        }

        PromptAndApply(
            "Rename application",
            $"Rename '{current}'. Every mode that has an opinion about it -- Running or Stopped -- is updated to match.",
            current,
            viewModel.RenameSelected);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ApplicationsViewModel viewModel || viewModel.SelectedApplication is not { } name)
        {
            return;
        }

        ConfirmAndApply(
            $"Remove '{name}' from PCMode? The application itself is not touched -- this only removes it from the configuration.",
            () => viewModel.DeleteSelected());
    }

    private void OnAddRequirementClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ApplicationsViewModel viewModel)
        {
            viewModel.AddRequirement();
        }
    }

    private void OnRemoveRequirementClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ApplicationsViewModel viewModel ||
            sender is not FrameworkElement { DataContext: RequirementRowViewModel row })
        {
            return;
        }

        viewModel.RemoveRequirement(row);
    }

    /// <summary>
    /// Browses for the executable behind the Start or Stop path box, chosen by
    /// the clicked button's Tag.
    /// </summary>
    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ApplicationsViewModel viewModel || sender is not FrameworkElement { Tag: string which })
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the program",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        if (which == "Start")
        {
            viewModel.StartPath = dialog.FileName;
        }
        else
        {
            viewModel.StopPath = dialog.FileName;
        }
    }
}

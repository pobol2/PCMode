using System.Windows;
using PCMode.Tray.Controls.PageShells;

namespace PCMode.Tray.Features.Rgb;

public partial class RgbPage : ConfigurationPageShell
{
    public RgbPage(RgbViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        DataContext = viewModel;
    }

    private void OnNewSceneClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RgbViewModel viewModel)
        {
            return;
        }

        PromptAndApply(
            "Create lighting scene",
            "Name the new scene. It starts with every configured OpenRGB controller.",
            string.Empty,
            viewModel.CreateScene);
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RgbViewModel viewModel || viewModel.SelectedScene is not { } selected)
        {
            return;
        }

        PromptAndApply(
            "Rename lighting scene",
            $"Rename '{selected.Name}'. Space targets that use it are updated when you save.",
            selected.Name,
            viewModel.RenameSelected);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RgbViewModel viewModel || viewModel.SelectedScene is not { } selected)
        {
            return;
        }

        ConfirmAndApply(
            $"Delete lighting scene '{selected.Name}'? If a Space still uses it, Save will refuse the change until that reference is removed.",
            () => viewModel.DeleteSelected());
    }

    private async void OnActivateClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is RgbViewModel viewModel)
        {
            await viewModel.ActivateSelectedAsync().ConfigureAwait(true);
        }
    }
}

using System.Windows;
using PCMode.Tray.Controls.PageShells;
using PCMode.Tray.Dialogs;
using PCMode.Tray.Features.Hardware;

namespace PCMode.Tray.Features.Devices;

public partial class DevicesPage : ListPageShell
{
    private static readonly TimeSpan LiveStatusInterval = TimeSpan.FromSeconds(3);

    public DevicesPage(HardwareViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        DataContext = viewModel;

        EnableLiveRefresh(LiveStatusInterval, RefreshAsync);
    }

    public override async void OnActivated()
    {
        base.OnActivated();

        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        if (DataContext is HardwareViewModel viewModel)
        {
            await viewModel.RefreshAsync().ConfigureAwait(true);
        }
    }

    private async void OnRescanClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is HardwareViewModel viewModel)
        {
            await viewModel.RefreshAsync().ConfigureAwait(true);
        }
    }

    private void OnAliasActionClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not HardwareViewModel viewModel ||
            (sender as FrameworkElement)?.DataContext is not HardwareItemRow item)
        {
            return;
        }

        var dialog = new AliasPromptWindow(
            item.IsConfigured ? "Edit alias" : "Add to PCMode",
            $"Give '{item.Name}' a PCMode alias.",
            item.Alias ?? string.Empty,
            alias => viewModel.SetAliasAsync(item, alias))
        {
            Owner = Window.GetWindow(this),
        };

        dialog.ShowDialog();
    }
}

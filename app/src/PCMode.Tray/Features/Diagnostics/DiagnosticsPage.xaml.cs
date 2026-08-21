using System.Windows;
using PCMode.Tray.Controls.PageShells;

namespace PCMode.Tray.Features.Diagnostics;

public partial class DiagnosticsPage : ListPageShell
{
    private static readonly TimeSpan LiveStatusInterval = TimeSpan.FromSeconds(3);

    public DiagnosticsPage(DiagnosticsViewModel viewModel)
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
        if (DataContext is DiagnosticsViewModel viewModel)
        {
            await viewModel.RefreshAsync().ConfigureAwait(true);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiagnosticsViewModel viewModel)
        {
            await viewModel.RefreshAsync().ConfigureAwait(true);
        }
    }

    private async void OnDryRunClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiagnosticsViewModel viewModel)
        {
            await viewModel.DryRunAsync().ConfigureAwait(true);
        }
    }

    private void OnOpenLogFolderClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DiagnosticsViewModel viewModel)
        {
            viewModel.OpenLogFolder();
        }
    }
}

using System.Windows;
using PCMode.Tray.Controls.PageShells;

namespace PCMode.Tray.Features.Home;

public partial class HomePage : ListPageShell
{
    private static readonly TimeSpan LiveStatusInterval = TimeSpan.FromSeconds(3);

    public HomePage(HomeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        DataContext = viewModel;

        EnableLiveRefresh(LiveStatusInterval, RefreshAsync);
    }

    public override void OnActivated()
    {
        base.OnActivated();

        if (DataContext is HomeViewModel viewModel)
        {
            viewModel.Refresh();
        }
    }

    private Task RefreshAsync()
    {
        if (DataContext is HomeViewModel viewModel)
        {
            viewModel.Refresh();
        }

        return Task.CompletedTask;
    }

    private async void OnTargetClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is HomeViewModel viewModel &&
            sender is System.Windows.Controls.Button { DataContext: HomeTargetCard { Enabled: true } card })
        {
            await viewModel.ApplyAsync(card.Key).ConfigureAwait(true);
        }
    }
}

using System.Windows;
using PCMode.Tray.Controls.PageShells;

namespace PCMode.Tray.Features.Theme;

public partial class ThemePage : ConfigurationPageShell
{
    public ThemePage(ThemeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        DataContext = viewModel;
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ThemeViewModel viewModel)
        {
            await viewModel.ApplySelectedAsync().ConfigureAwait(true);
        }
    }
}

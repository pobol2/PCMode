using System.Windows;
using PCMode.Tray.Controls.PageShells;

namespace PCMode.Tray.Features.Settings;

public partial class SettingsPage : ListPageShell
{
    private readonly Action? _openSetup;

    public SettingsPage(SettingsViewModel viewModel, Action? openSetup = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _openSetup = openSetup;

        InitializeComponent();

        DataContext = viewModel;
        GuidedSetupSection.Visibility = _openSetup is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public override void OnActivated()
    {
        base.OnActivated();

        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.Refresh();
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.Save();
        }
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.Refresh();
        }
    }

    private void OnGuidedSetupClick(object sender, RoutedEventArgs e) => _openSetup?.Invoke();
}

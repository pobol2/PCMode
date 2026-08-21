using System.Windows;
using PCMode.Tray.Controls.PageShells;

namespace PCMode.Tray.Features.Television;

/// <summary>
/// Live power status ticks while this page is open, the same way
/// <c>LayoutsPage</c> ticks its own live monitor status -- the two
/// pages whose canvas reflects Windows/network state that can change while
/// the user is looking at it, not just on activation. Both opt into
/// <see cref="ConfigurationPageShell.EnableLiveRefresh"/> instead of owning a
/// timer themselves.
/// </summary>
public partial class TelevisionPage : ConfigurationPageShell
{
    private static readonly TimeSpan LiveStatusInterval = TimeSpan.FromSeconds(3);

    public TelevisionPage(TelevisionViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        DataContext = viewModel;

        EnableLiveRefresh(LiveStatusInterval, viewModel.RefreshLivePowerStatusAsync);
    }

    private void OnCreateTelevisionClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelevisionViewModel viewModel)
        {
            return;
        }

        PromptAndApply(
            "Create television",
            "Give this television a name. You can configure its address, inputs, and picture profiles after creating it.",
            string.Empty,
            viewModel.CreateTelevision);
    }

    private void OnActionsMenuOpened(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelevisionViewModel viewModel)
        {
            return;
        }

        EnableTelevisionMenuItem.Visibility =
            viewModel.Enabled ? Visibility.Collapsed : Visibility.Visible;
        DisableTelevisionMenuItem.Visibility =
            viewModel.Enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelevisionViewModel viewModel || viewModel.SelectedTelevision is not { } source)
        {
            return;
        }

        PromptAndApply(
            "Duplicate television",
            $"Name the copy of '{source}'. Connection settings, inputs, and picture profiles are copied. Pairing credentials stay tied to the physical host.",
            $"{source} copy",
            name => viewModel.DuplicateSelected(name));
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelevisionViewModel viewModel || viewModel.SelectedTelevision is not { } current)
        {
            return;
        }

        PromptAndApply(
            "Rename television",
            $"Rename '{current}'. PCMode will refuse the change if it would leave the current configuration invalid.",
            current,
            viewModel.RenameSelected);
    }

    private void OnSetDefaultClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TelevisionViewModel viewModel && ConfirmDiscardIfDirty())
        {
            viewModel.SetSelectedAsDefault();
        }
    }

    private void OnToggleEnabledClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TelevisionViewModel viewModel && ConfirmDiscardIfDirty())
        {
            viewModel.ToggleSelectedEnabled();
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelevisionViewModel viewModel || viewModel.SelectedTelevision is not { } name)
        {
            return;
        }

        ConfirmAndApply(
            $"Remove '{name}' from PCMode? The television itself and its pairing credentials are not changed.",
            () => viewModel.DeleteSelected());
    }

    private async void OnPairClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TelevisionViewModel viewModel)
        {
            await viewModel.PairAsync().ConfigureAwait(true);
        }
    }

    private void OnAddInputClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TelevisionViewModel viewModel)
        {
            viewModel.AddInput();
        }
    }

    private void OnRemoveInputClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelevisionViewModel viewModel ||
            sender is not FrameworkElement { DataContext: TelevisionInputRow row })
        {
            return;
        }

        viewModel.RemoveInput(row);
    }

    private void OnAddProfileClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TelevisionViewModel viewModel)
        {
            viewModel.AddProfile();
        }
    }

    private void OnRemoveProfileClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TelevisionViewModel viewModel ||
            sender is not FrameworkElement { DataContext: TelevisionProfileRow row })
        {
            return;
        }

        viewModel.RemoveProfile(row);
    }

    private void OnAddSignalClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TelevisionProfileRow profile })
        {
            profile.AddSignal();
        }
    }

    private void OnRemoveSignalClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TelevisionSettingRow row })
        {
            return;
        }

        TelevisionProfileRow? profile = FindProfile(sender as FrameworkElement);
        profile?.RemoveSignal(row);
    }

    private void OnAddSettingClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TelevisionProfileRow profile })
        {
            profile.AddSetting();
        }
    }

    private void OnRemoveSettingClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TelevisionSettingRow row })
        {
            return;
        }

        TelevisionProfileRow? profile = FindProfile(sender as FrameworkElement);
        profile?.RemoveSetting(row);
    }

    /// <summary>
    /// Walks up the visual tree from a setting row's Remove button to the
    /// profile it belongs to, since a nested ItemsControl's DataContext is the
    /// row, not the profile that owns the list it came from.
    /// </summary>
    private static TelevisionProfileRow? FindProfile(FrameworkElement? element)
    {
        for (FrameworkElement? current = element;
             current is not null;
             current = System.Windows.Media.VisualTreeHelper.GetParent(current) as FrameworkElement)
        {
            if (current.DataContext is TelevisionProfileRow profile)
            {
                return profile;
            }
        }

        return null;
    }
}

using System.Windows;
using PCMode.Engine;
using PCMode.Tray.Dialogs;
using PCMode.Tray.Controls.PageShells;
using PCMode.Tray.Presentation;

namespace PCMode.Tray.Features.Spaces;

/// <summary>
/// Where Spaces, Activities and Sources are created, edited, deleted and
/// reordered.
/// </summary>
public partial class SpacesPage : ConfigurationPageShell
{
    public SpacesPage(SpacesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        DataContext = viewModel;
    }

    /// <summary>
    /// Refreshes which row is the current target on every activation after
    /// the first, without reloading the configuration or disturbing unsaved
    /// edits -- the engine's idea of "current" can change while this page is
    /// open elsewhere (the tray menu, a global hotkey).
    /// </summary>
    protected override void OnReactivated(ConfigurationPageViewModelBase viewModel)
    {
        ((SpacesViewModel)viewModel).RefreshCurrentTarget();
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SpacesViewModel viewModel ||
            FindResource("CreateMenu") is not System.Windows.Controls.ContextMenu menu)
        {
            return;
        }

        menu.DataContext = viewModel;

        if (Template.FindName("PART_CreateButton", this) is System.Windows.Controls.Button createButton)
        {
            menu.PlacementTarget = createButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }
        else
        {
            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        }

        menu.IsOpen = true;
    }

    private void OnNewSpaceClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SpacesViewModel viewModel)
        {
            return;
        }

        PromptAndApply(
            "New space",
            "Name the new space -- where this PC is being used, such as Desk or TV. It starts empty.",
            string.Empty,
            viewModel.CreateSpace);
    }

    private void OnNewActivityClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SpacesViewModel { Selected: { } selected } viewModel)
        {
            return;
        }

        PromptAndApply(
            "New activity",
            $"Name the new activity of '{selected.Address.SpaceId}' -- what you are doing, such as Work or Gaming. " +
            "It starts with everything inherited from its space.",
            string.Empty,
            viewModel.CreateActivity);
    }

    private void OnNewSourceClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SpacesViewModel { Selected.Address.ActivityId: not null } viewModel ||
            !ConfirmDiscardIfDirty())
        {
            return;
        }

        var dialog = new NewSourceWindow { Owner = Window.GetWindow(this) };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        viewModel.CreateSource(dialog.SourceName, dialog.Kind);
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SpacesViewModel { Selected: { } selected } viewModel)
        {
            return;
        }

        PromptAndApply(
            "Rename",
            $"Rename '{selected.Name}'.",
            selected.Name,
            viewModel.RenameSelected);
    }

    private void OnDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SpacesViewModel { Selected: { } selected } viewModel)
        {
            return;
        }

        string message = selected.Level switch
        {
            NodeLevel.Space =>
                "Name the copy. Its own resources and every activity and source under it are copied.",
            NodeLevel.Activity =>
                "Name the copy. Its own resources and every source under it are copied; anything it inherits " +
                "goes on being inherited.",
            _ =>
                "Name the copy. Its kind and own resources are copied; anything it inherits goes on being inherited.",
        };

        PromptAndApply(
            $"Duplicate {selected.Level.ToString().ToLowerInvariant()}",
            message,
            $"{selected.Name} copy",
            viewModel.DuplicateSelected);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SpacesViewModel { Selected: { } selected } viewModel)
        {
            return;
        }

        string what = selected.Level == NodeLevel.Space
            ? $"Delete '{selected.Name}' and every activity and source in it?"
            : selected.Level == NodeLevel.Activity
                ? $"Delete '{selected.DisplayPath}' and every source under it?"
                : $"Delete '{selected.DisplayPath}'?";

        ConfirmAndApply(
            $"{what} This cannot be undone.",
            () =>
            {
                TargetReferences references = viewModel.DescribeReferences(selected);
                IReadOnlyDictionary<string, string>? favoriteReplacements = null;
                string? startupReplacement = null;

                if (references.Any)
                {
                    var referencesDialog = new TargetReferencesWindow(
                        selected.DisplayPath,
                        references.FavoriteDirections,
                        references.IsStartupTarget,
                        viewModel.AlternativesTo(selected))
                    {
                        Owner = Window.GetWindow(this),
                    };

                    if (referencesDialog.ShowDialog() != true)
                    {
                        return;
                    }

                    favoriteReplacements = referencesDialog.FavoriteReplacements;
                    startupReplacement = referencesDialog.StartupReplacement;
                }

                viewModel.DeleteSelected(favoriteReplacements, startupReplacement);
            });
    }

    private void OnToggleExpandClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SpacesViewModel viewModel ||
            sender is not FrameworkElement { DataContext: HierarchyNodeViewModel node })
        {
            return;
        }

        if (node.IsExpanded && viewModel.Selected is { } selected &&
            SpacesViewModel.IsDescendantOf(selected, node) && !ConfirmDiscardIfDirty())
        {
            return;
        }

        viewModel.ToggleExpanded(node);
    }

    private void OnEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox { IsChecked: bool enabled } toggle ||
            DataContext is not SpacesViewModel { Selected: { } node } viewModel)
        {
            return;
        }

        if (!ConfirmDiscardIfDirty())
        {
            toggle.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();
            return;
        }

        if (viewModel.SetTargetEnabled(node, enabled) is not null)
        {
            toggle.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();
        }
    }

    private void OnAddSettingClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EditableResourceViewModel resource } &&
            DataContext is SpacesViewModel viewModel)
        {
            viewModel.AddSetting(resource);
        }
    }

    private void OnOverrideSettingClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EditableResourceViewModel resource } &&
            DataContext is SpacesViewModel viewModel)
        {
            viewModel.OverrideSetting(resource);
        }
    }

    private void OnRemoveSettingClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EditableResourceViewModel resource } &&
            DataContext is SpacesViewModel viewModel)
        {
            viewModel.RemoveSetting(resource);
        }
    }

    private void OnExcludeSettingClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EditableResourceViewModel resource } &&
            DataContext is SpacesViewModel viewModel)
        {
            viewModel.ExcludeInheritedSetting(resource);
        }
    }

    private async void OnActivateClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SpacesViewModel viewModel)
        {
            await viewModel.ActivateSelectedAsync().ConfigureAwait(true);
        }
    }
}

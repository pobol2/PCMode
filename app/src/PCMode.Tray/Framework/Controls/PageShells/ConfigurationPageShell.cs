using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PCMode.Tray.Dialogs;
using PCMode.Tray.Navigation;
using PCMode.Tray.Presentation;

namespace PCMode.Tray.Controls.PageShells;

[TemplatePart(Name = "PART_CreateButton", Type = typeof(System.Windows.Controls.Button))]
[TemplatePart(Name = "PART_SelectorList", Type = typeof(System.Windows.Controls.ListBox))]
[TemplatePart(Name = "PART_ActionsButton", Type = typeof(System.Windows.Controls.Button))]
[TemplatePart(Name = "PART_PrimaryActionButton", Type = typeof(System.Windows.Controls.Button))]
[TemplatePart(Name = "PART_DiscardButton", Type = typeof(System.Windows.Controls.Button))]
[TemplatePart(Name = "PART_SaveButton", Type = typeof(System.Windows.Controls.Button))]
public class ConfigurationPageShell : CommonPageShell, IPageLifecycle
{
    private System.Windows.Controls.Button? _createButton;
    private System.Windows.Controls.ListBox? _selectorList;
    private System.Windows.Controls.Button? _actionsButton;
    private System.Windows.Controls.Button? _primaryActionButton;
    private System.Windows.Controls.Button? _discardButton;
    private System.Windows.Controls.Button? _saveButton;

    private bool _initialized;
    private bool _syncingSelectorSelection;
    private LiveRefreshTimer? _liveRefreshTimer;

    public ConfigurationPageShell()
    {
        SetResourceReference(
            StyleProperty,
            typeof(ConfigurationPageShell));

        DataContextChanged += OnDataContextChanged;
    }

    // ------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------

    /// <summary>
    /// Loads the page's configuration the first time it becomes active, then
    /// hands subsequent activations to <see cref="OnReactivated"/> so a page
    /// can refresh ambient/runtime state without reloading the editor and
    /// discarding unsaved work. Also starts the live-refresh timer, if the
    /// page opted into one via <see cref="EnableLiveRefresh"/>.
    /// </summary>
    public virtual void OnActivated()
    {
        if (DataContext is ConfigurationPageViewModelBase viewModel)
        {
            if (!_initialized)
            {
                _initialized = true;
                viewModel.Refresh();
            }
            else
            {
                OnReactivated(viewModel);
            }
        }

        _liveRefreshTimer?.Start();
    }

    public virtual void OnDeactivated()
    {
        _liveRefreshTimer?.Stop();
    }

    /// <summary>Called on every activation after the first. The base implementation does nothing.</summary>
    protected virtual void OnReactivated(ConfigurationPageViewModelBase viewModel)
    {
    }

    /// <summary>
    /// Opts this page into a tick that runs only while it is the active page --
    /// started in <see cref="OnActivated"/>, stopped in <see cref="OnDeactivated"/>.
    /// For a page whose content reflects state that can change while it is open
    /// (Layouts' live monitor state, Television's live power state), not just
    /// between visits. Call once, from the page's constructor.
    /// </summary>
    protected void EnableLiveRefresh(TimeSpan interval, Func<Task> tick)
    {
        _liveRefreshTimer = new LiveRefreshTimer(interval, tick);
    }

    // ------------------------------------------------------------
    // Unsaved-selection protection
    // ------------------------------------------------------------

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ConfigurationPageViewModelBase oldViewModel)
        {
            oldViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is ConfigurationPageViewModelBase newViewModel)
        {
            newViewModel.PropertyChanged += OnViewModelPropertyChanged;
            SyncSelectorSelection(newViewModel);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfigurationPageViewModelBase.SelectedKey) &&
            DataContext is ConfigurationPageViewModelBase viewModel)
        {
            SyncSelectorSelection(viewModel);
        }
    }

    private void SyncSelectorSelection(ConfigurationPageViewModelBase viewModel)
    {
        _syncingSelectorSelection = true;

        try
        {
            SelectorSelectedItem = FindSelectorItem(viewModel.SelectedKey);
        }
        finally
        {
            _syncingSelectorSelection = false;
        }
    }

    private object? FindSelectorItem(object? key)
    {
        if (key is null || SelectorItemsSource is null)
        {
            return null;
        }

        foreach (object item in SelectorItemsSource)
        {
            if (item is IConfigurationSelectorItem selectorItem && Equals(selectorItem.Key, key))
            {
                return item;
            }
        }

        return null;
    }

    // ------------------------------------------------------------
    // Selector
    // ------------------------------------------------------------

    public static readonly DependencyProperty ShowCreateButtonProperty =
        DependencyProperty.Register(
            nameof(ShowCreateButton),
            typeof(bool),
            typeof(ConfigurationPageShell),
            new PropertyMetadata(true));

    public bool ShowCreateButton
    {
        get => (bool)GetValue(ShowCreateButtonProperty);
        set => SetValue(ShowCreateButtonProperty, value);
    }

    public static readonly DependencyProperty SelectorTitleProperty =
        DependencyProperty.Register(
            nameof(SelectorTitle),
            typeof(string),
            typeof(ConfigurationPageShell),
            new PropertyMetadata(string.Empty));

    public string SelectorTitle
    {
        get => (string)GetValue(SelectorTitleProperty);
        set => SetValue(SelectorTitleProperty, value);
    }

    public static readonly DependencyProperty SelectorDescriptionProperty =
        DependencyProperty.Register(
            nameof(SelectorDescription),
            typeof(string),
            typeof(ConfigurationPageShell),
            new PropertyMetadata(string.Empty));

    public string SelectorDescription
    {
        get => (string)GetValue(SelectorDescriptionProperty);
        set => SetValue(SelectorDescriptionProperty, value);
    }

    public static readonly DependencyProperty SelectorItemsSourceProperty =
        DependencyProperty.Register(
            nameof(SelectorItemsSource),
            typeof(IEnumerable),
            typeof(ConfigurationPageShell));

    public IEnumerable? SelectorItemsSource
    {
        get => (IEnumerable?)GetValue(SelectorItemsSourceProperty);
        set => SetValue(SelectorItemsSourceProperty, value);
    }

    public static readonly DependencyProperty SelectorItemTemplateProperty =
        DependencyProperty.Register(
            nameof(SelectorItemTemplate),
            typeof(DataTemplate),
            typeof(ConfigurationPageShell));

    public DataTemplate? SelectorItemTemplate
    {
        get => (DataTemplate?)GetValue(SelectorItemTemplateProperty);
        set => SetValue(SelectorItemTemplateProperty, value);
    }

    public static readonly DependencyProperty SelectorSelectedItemProperty =
        DependencyProperty.Register(
            nameof(SelectorSelectedItem),
            typeof(object),
            typeof(ConfigurationPageShell),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public object? SelectorSelectedItem
    {
        get => GetValue(SelectorSelectedItemProperty);
        set => SetValue(SelectorSelectedItemProperty, value);
    }

    // ------------------------------------------------------------
    // Header / status
    // ------------------------------------------------------------

    public static readonly DependencyProperty HeaderSummaryTextProperty =
        DependencyProperty.Register(
            nameof(HeaderSummaryText),
            typeof(string),
            typeof(ConfigurationPageShell),
            new PropertyMetadata(string.Empty));

    public string HeaderSummaryText
    {
        get => (string)GetValue(HeaderSummaryTextProperty);
        set => SetValue(HeaderSummaryTextProperty, value);
    }

    protected bool ConfirmDiscardIfDirty()
    {
        if (DataContext is not ConfigurationPageViewModelBase viewModel ||
            !viewModel.HasUnsavedChanges)
        {
            return true;
        }

        MessageBoxResult choice = System.Windows.MessageBox.Show(
            "This configuration has unsaved changes. Save them first?",
            "PCMode",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        switch (choice)
        {
            case MessageBoxResult.Yes:
                return viewModel.TrySaveChanges();

            case MessageBoxResult.No:
                viewModel.DiscardChanges();
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Guards against discarding unsaved work, then asks for a name and hands
    /// it to <paramref name="apply"/>. Covers the create/rename/duplicate
    /// dialog every configuration page uses -- the feature supplies only the
    /// wording and what a name means to it.
    /// </summary>
    protected void PromptAndApply(
        string title,
        string message,
        string initialValue,
        Func<string, string?> apply)
    {
        if (!ConfirmDiscardIfDirty())
        {
            return;
        }

        var dialog = new AliasPromptWindow(
            title,
            message,
            initialValue,
            name => Task.FromResult(apply(name)))
        {
            Owner = Window.GetWindow(this),
        };

        dialog.ShowDialog();
    }

    /// <summary>
    /// Guards against discarding unsaved work, then asks for a Yes/No
    /// confirmation before running <paramref name="apply"/>. Covers the
    /// delete confirmation every configuration page uses.
    /// </summary>
    protected bool ConfirmAndApply(string message, Action apply)
    {
        if (!ConfirmDiscardIfDirty())
        {
            return false;
        }

        MessageBoxResult confirmed = System.Windows.MessageBox.Show(
            message,
            "PCMode",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmed != MessageBoxResult.Yes)
        {
            return false;
        }

        apply();
        return true;
    }

    // ------------------------------------------------------------
    // Editor
    // ------------------------------------------------------------

    public static readonly DependencyProperty EditorEyebrowProperty =
        DependencyProperty.Register(
            nameof(EditorEyebrow),
            typeof(string),
            typeof(ConfigurationPageShell),
            new PropertyMetadata(string.Empty));

    public string EditorEyebrow
    {
        get => (string)GetValue(EditorEyebrowProperty);
        set => SetValue(EditorEyebrowProperty, value);
    }

    public static readonly DependencyProperty EditorTitleProperty =
        DependencyProperty.Register(
            nameof(EditorTitle),
            typeof(string),
            typeof(ConfigurationPageShell),
            new PropertyMetadata(string.Empty));

    public string EditorTitle
    {
        get => (string)GetValue(EditorTitleProperty);
        set => SetValue(EditorTitleProperty, value);
    }

    public static readonly DependencyProperty EditorContentProperty =
        DependencyProperty.Register(
            nameof(EditorContent),
            typeof(object),
            typeof(ConfigurationPageShell));

    public object? EditorContent
    {
        get => GetValue(EditorContentProperty);
        set => SetValue(EditorContentProperty, value);
    }

    public static readonly DependencyProperty SecondaryEditorContentProperty =
        DependencyProperty.Register(
            nameof(SecondaryEditorContent),
            typeof(object),
            typeof(ConfigurationPageShell));

    public object? SecondaryEditorContent
    {
        get => GetValue(SecondaryEditorContentProperty);
        set => SetValue(SecondaryEditorContentProperty, value);
    }

    // ------------------------------------------------------------
    // Actions
    // ------------------------------------------------------------

    public static readonly DependencyProperty ActionsMenuProperty =
        DependencyProperty.Register(
            nameof(ActionsMenu),
            typeof(System.Windows.Controls.ContextMenu),
            typeof(ConfigurationPageShell));

    public System.Windows.Controls.ContextMenu? ActionsMenu
    {
        get => (System.Windows.Controls.ContextMenu?)GetValue(ActionsMenuProperty);
        set => SetValue(ActionsMenuProperty, value);
    }

    public static readonly DependencyProperty CanUseActionsProperty =
        DependencyProperty.Register(
            nameof(CanUseActions),
            typeof(bool),
            typeof(ConfigurationPageShell),
            new PropertyMetadata(true));

    public bool CanUseActions
    {
        get => (bool)GetValue(CanUseActionsProperty);
        set => SetValue(CanUseActionsProperty, value);
    }

    public static readonly DependencyProperty PrimaryActionLabelProperty =
        DependencyProperty.Register(
            nameof(PrimaryActionLabel),
            typeof(string),
            typeof(ConfigurationPageShell),
            new PropertyMetadata("Activate"));

    public string PrimaryActionLabel
    {
        get => (string)GetValue(PrimaryActionLabelProperty);
        set => SetValue(PrimaryActionLabelProperty, value);
    }

    public static readonly DependencyProperty CanUsePrimaryActionProperty =
        DependencyProperty.Register(
            nameof(CanUsePrimaryAction),
            typeof(bool),
            typeof(ConfigurationPageShell),
            new PropertyMetadata(true));

    public bool CanUsePrimaryAction
    {
        get => (bool)GetValue(CanUsePrimaryActionProperty);
        set => SetValue(CanUsePrimaryActionProperty, value);
    }

    // ------------------------------------------------------------
    // Footer
    // ------------------------------------------------------------

    public static readonly DependencyProperty ShowFooterProperty =
        DependencyProperty.Register(
            nameof(ShowFooter),
            typeof(bool),
            typeof(ConfigurationPageShell),
            new PropertyMetadata(true));

    public bool ShowFooter
    {
        get => (bool)GetValue(ShowFooterProperty);
        set => SetValue(ShowFooterProperty, value);
    }

    // ------------------------------------------------------------
    // Routed events
    // ------------------------------------------------------------

    public static readonly RoutedEvent CreateRequestedEvent =
        EventManager.RegisterRoutedEvent(
            nameof(CreateRequested),
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(ConfigurationPageShell));

    public event RoutedEventHandler CreateRequested
    {
        add => AddHandler(CreateRequestedEvent, value);
        remove => RemoveHandler(CreateRequestedEvent, value);
    }

    public static readonly RoutedEvent PrimaryActionRequestedEvent =
        EventManager.RegisterRoutedEvent(
            nameof(PrimaryActionRequested),
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(ConfigurationPageShell));

    public event RoutedEventHandler PrimaryActionRequested
    {
        add => AddHandler(PrimaryActionRequestedEvent, value);
        remove => RemoveHandler(PrimaryActionRequestedEvent, value);
    }

    // ------------------------------------------------------------
    // Template wiring
    // ------------------------------------------------------------

    public override void OnApplyTemplate()
    {
        DetachTemplateHandlers();

        base.OnApplyTemplate();

        _createButton =
            GetTemplateChild("PART_CreateButton") as System.Windows.Controls.Button;

        _selectorList =
            GetTemplateChild("PART_SelectorList") as System.Windows.Controls.ListBox;

        _actionsButton =
            GetTemplateChild("PART_ActionsButton") as System.Windows.Controls.Button;

        _primaryActionButton =
            GetTemplateChild("PART_PrimaryActionButton") as System.Windows.Controls.Button;

        _discardButton =
            GetTemplateChild("PART_DiscardButton") as System.Windows.Controls.Button;

        _saveButton =
            GetTemplateChild("PART_SaveButton") as System.Windows.Controls.Button;

        AttachTemplateHandlers();
    }

    private void AttachTemplateHandlers()
    {
        if (_createButton is not null)
            _createButton.Click += OnCreateClick;

        if (_selectorList is not null)
            _selectorList.SelectionChanged += OnSelectorSelectionChanged;

        if (_actionsButton is not null)
            _actionsButton.Click += OnActionsClick;

        if (_primaryActionButton is not null)
            _primaryActionButton.Click += OnPrimaryActionClick;

        if (_discardButton is not null)
            _discardButton.Click += OnDiscardClick;

        if (_saveButton is not null)
            _saveButton.Click += OnSaveClick;
    }

    private void DetachTemplateHandlers()
    {
        if (_createButton is not null)
            _createButton.Click -= OnCreateClick;

        if (_selectorList is not null)
            _selectorList.SelectionChanged -= OnSelectorSelectionChanged;

        if (_actionsButton is not null)
            _actionsButton.Click -= OnActionsClick;

        if (_primaryActionButton is not null)
            _primaryActionButton.Click -= OnPrimaryActionClick;

        if (_discardButton is not null)
            _discardButton.Click -= OnDiscardClick;

        if (_saveButton is not null)
            _saveButton.Click -= OnSaveClick;
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(
            CreateRequestedEvent,
            this));
    }

    private void OnSelectorSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingSelectorSelection || DataContext is not ConfigurationPageViewModelBase viewModel)
        {
            return;
        }

        object? nextKey = (SelectorSelectedItem as IConfigurationSelectorItem)?.Key;

        if (nextKey is null || Equals(nextKey, viewModel.SelectedKey))
        {
            return;
        }

        if (!ConfirmDiscardIfDirty())
        {
            SyncSelectorSelection(viewModel);
            return;
        }

        viewModel.SelectKey(nextKey);
    }

    private void OnActionsClick(object sender, RoutedEventArgs e)
    {
        if (_actionsButton?.ContextMenu is not { } menu)
            return;

        menu.PlacementTarget = _actionsButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnPrimaryActionClick(object sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(
            PrimaryActionRequestedEvent,
            this));
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConfigurationPageViewModelBase viewModel)
        {
            viewModel.DiscardChanges();
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConfigurationPageViewModelBase viewModel)
        {
            viewModel.TrySaveChanges();
        }
    }
}
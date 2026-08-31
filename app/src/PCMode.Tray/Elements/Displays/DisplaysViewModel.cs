using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using PCMode.Engine;
using PCMode.Tray.Elements.Configuration;
using PCMode.Tray.Elements.Displays.Sections;

namespace PCMode.Tray.Elements.Displays;

/// <summary>
/// Edits display layouts graphically -- which monitors, arranged how, at what
/// rate -- and, for whichever layout is selected, the one saved cursor
/// position linked to it, if any.
/// </summary>
/// <remarks>
/// THE PAGE OWNS THE SELECTED LAYOUT; SECTIONS OWN A FACET EACH
///
/// This ViewModel is deliberately thin: it owns the shared selector
/// (<see cref="SelectorItems"/>/<see cref="SelectedLayout"/>), via the same
/// <see cref="ConfigurationEditingSession{TKey, TEntity}"/> lifecycle Audio's
/// targets use -- Create/Duplicate/Rename open an unsaved draft that stays
/// selected until Save writes it -- and which layout exists at all (create,
/// rename, duplicate, delete, activate), exactly the concerns every other
/// configuration page's own ViewModel already owns for its collection. What
/// editing one layout actually involves is split into
/// <see cref="LayoutSection"/> (monitor arrangement, plus which cursor
/// position is linked) and <see cref="CursorSection"/> (that position's X/Y),
/// each an <see cref="IConfigurationSection"/> this ViewModel composes rather
/// than a second copy of their fields. Neither section knows the other
/// exists; both are handed the same selected layout name by this ViewModel,
/// and read whatever else they need fresh from <see cref="IConfigurationWorkspace"/>
/// themselves -- the same "no second source of truth" pattern every feature
/// here already follows.
///
/// SAVE IS BESPOKE, NOT A GENERIC SECTION FAN-OUT
///
/// A layout's monitor arrangement, its cursor-position link, and that
/// position's own X/Y are three separate writes, in that order, because each
/// depends on the last existing first: the link names a Space/Activity/Source
/// resource for a layout name that has to exist before it can be pointed at
/// (<see cref="LayoutConfigurationStore.Save"/>), and the position's geometry
/// is saved under a name the link has to have written first. So
/// <see cref="LayoutSection"/>'s own <c>IConfigurationSection.Save</c> is a
/// no-op -- this ViewModel calls <see cref="LayoutSectionViewModel.BuildEdit"/>
/// and writes the layout itself, then <see cref="LayoutSectionViewModel.ApplyCursorLink"/>,
/// then <see cref="CursorSection"/>'s own independent save (a separate
/// 'providers.cursor.positions' entry).
/// </remarks>
public sealed class DisplaysViewModel : ConfigurationPageViewModelBase, IPageLifecycle
{
    private const string Category = "Displays";

    /// <summary>
    /// One selectable layout, as the shared editing-session's pool entity --
    /// just enough to key and title the selector. Everything the editor
    /// actually shows (the monitor arrangement, the cursor link) is read fresh
    /// through <see cref="LayoutSectionViewModel"/>/<see cref="CursorSectionViewModel"/>
    /// by name, not carried on this record, unlike Audio's <c>AudioTargetStatus</c>.
    /// </summary>
    private sealed record DisplayLayoutEntry(string Name, bool IsCurrent);

    private readonly ConfigurationEditingSession<string, DisplayLayoutEntry> _session;

    private string? _editingLayoutName;
    private string? _currentLayout;
    private bool _isMonitorControlRescanRunning;
    private readonly Func<string?, TvPowerProvider?>? _livePowerProvider;

    private static readonly TimeSpan LiveStatusInterval = TimeSpan.FromSeconds(3);

    private readonly LiveRefreshTimer _liveRefresh;
    private bool _loaded;

    /// <param name="livePowerProvider">
    /// The running engine's own television, by name, handed straight to
    /// each monitor's television section so pairing and power go through
    /// the connection every transition shares. Null when nothing is running.
    /// </param>
    /// <param name="prompts">
    /// How this ViewModel, and the sections it composes, ask for a name,
    /// confirm a deletion, and run the unsaved-changes guard. A Functional
    /// Block is lookless and cannot open a window, so the whole flow lives
    /// here instead of in a page's click handlers. Left null outside a hosted
    /// process (tests), where <see cref="NoConfigurationPrompts"/> refuses
    /// every prompt rather than silently discarding an edit nobody could be
    /// asked about.
    /// </param>
    public DisplaysViewModel(
        IConfigurationWorkspace workspace,
        Func<string?, TvPowerProvider?>? livePowerProvider = null,
        IConfigurationPrompts? prompts = null)
        : base(workspace, prompts)
    {
        _liveRefresh = new LiveRefreshTimer(LiveStatusInterval, RefreshLiveStatusAsync);

        _session = new ConfigurationEditingSession<string, DisplayLayoutEntry>(
            keyOf: entry => entry.Name,
            titleOf: entry => entry.Name,
            summaryOf: entry => entry.IsCurrent ? "Current" : null);

        _livePowerProvider = livePowerProvider;
        LayoutSection = new LayoutSectionViewModel(workspace, OnSectionChanged, OnCursorLinkChanged, OnSelectedMonitorChanged);

        // The cursor section owns the prompts and confirmations for its four
        // actions but not the writes: a position's link lives on a
        // Space/Activity/Source and its geometry in its own store, and only
        // this ViewModel sees both.
        CursorSection = new CursorSectionViewModel(
            workspace,
            OnSectionChanged,
            Prompts,
            new CursorPositionWrites(
                LayoutSection.CreateCursorPosition,
                RenameCursorPosition,
                DuplicateCursorPosition,
                DeleteCursorPosition));

        DisplayRegistrationSection = new DisplayRegistrationSectionViewModel(
            workspace, Refresh, Prompts, GuardUnsavedChanges);

        SaveCommand = new RelayCommand(() => TrySaveChanges(), () => CanSave);
        DiscardCommand = new RelayCommand(DiscardChanges, () => CanDiscard);
        CreateLayoutCommand = new RelayCommand(PromptCreateLayout);
        RenameLayoutCommand = new RelayCommand(PromptRenameLayout, () => HasSelectedLayout);
        DuplicateLayoutCommand = new RelayCommand(PromptDuplicateLayout, () => HasSelectedLayout);
        DeleteLayoutCommand = new RelayCommand(ConfirmDeleteLayout, () => HasSelectedLayout);
        ActivateLayoutCommand = new AsyncRelayCommand(ActivateSelectedForShellAsync, () => CanActivateSelected);
        AddMonitorCommand = new RelayCommand(LayoutSection.AddMonitor, () => HasSelectedLayout && LayoutSection.CanAddMonitor);
        RemoveMonitorCommand = new RelayCommand<MonitorConfigurationSectionRow>(
            section => LayoutSection.RemoveMonitor(section.Monitor),
            _ => LayoutSection.CanRemoveMonitor);
        SelectMonitorCommand = new RelayCommand<LayoutMonitorRow>(LayoutSection.SelectMonitor);
        RescanMonitorControlsCommand = new AsyncRelayCommand(RescanMonitorControlsAsync, () => CanRescanMonitorControls);

        TreePresentation = new ConfigurationPagePresentation(
            "Displays",
            new ConfigurationSelectorPresentation("Layouts", SelectorItems, CreateLayoutCommand, SelectCommand: SelectItemCommand),
            ConfigurationEditorPresentation.Create(
                essential:
                [
                    new("Layout", new TerminalElementContent("Monitor arrangement and layout membership", LayoutSection)),
                ],
                advanced:
                [
                    new("Cursor", new TerminalElementContent("Cursor positions and layout links", CursorSection), IsCollapsible: true),
                    new("Monitor controls", new TerminalElementContent("Per-monitor DDC and television settings", MonitorSections), IsCollapsible: true),
                ],
                hardware:
                [
                    new("Current hardware", new TerminalElementContent("Display registration", DisplayRegistrationSection), IsCollapsible: true),
                ]));

        PropertyChanged += OnSelfPropertyChanged;
    }

    public ICommand SaveCommand { get; }

    public ICommand DiscardCommand { get; }

    public ICommand CreateLayoutCommand { get; }

    public ICommand RenameLayoutCommand { get; }

    public ICommand DuplicateLayoutCommand { get; }

    public ICommand DeleteLayoutCommand { get; }

    public ICommand ActivateLayoutCommand { get; }

    public ICommand AddMonitorCommand { get; }

    public ICommand RemoveMonitorCommand { get; }

    /// <summary>Makes one monitor the selected one, from a click on the arrangement surface.</summary>
    public ICommand SelectMonitorCommand { get; }

    public ICommand RescanMonitorControlsCommand { get; }

    // The layout actions keep the wording, and the guard behaviour, the page
    // shell used before these became commands: Create asks the unsaved-changes
    // question, Rename/Duplicate/Delete do not, because they act on the very
    // edit in progress.

    private void PromptCreateLayout()
    {
        if (!GuardUnsavedChanges())
        {
            return;
        }

        Prompts.PromptForName(
            "Create layout",
            "Name the new layout.",
            string.Empty,
            StartNewLayout);
    }

    private void PromptRenameLayout()
    {
        if (EditingLayoutName is not string current)
        {
            return;
        }

        Prompts.PromptForName("Rename layout", $"Rename '{current}'.", current, RenameSelected);
    }

    private void PromptDuplicateLayout()
    {
        if (EditingLayoutName is not string current)
        {
            return;
        }

        Prompts.PromptForName("Duplicate layout", $"Name the copy of '{current}'.", $"{current} copy", DuplicateSelected);
    }

    private void ConfirmDeleteLayout()
    {
        if (EditingLayoutName is string current &&
            Prompts.Confirm($"Delete '{current}'? This cannot be undone."))
        {
            DeleteSelected();
        }
    }

    private void OnSelfPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SelectedKey)
            or nameof(CanSave)
            or nameof(CanDiscard)
            or nameof(CanActivateSelected)
            or nameof(CanRescanMonitorControls))
        {
            RefreshCommands();
        }
    }

    private void RefreshCommands()
    {
        (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DiscardCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RenameLayoutCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DuplicateLayoutCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteLayoutCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ActivateLayoutCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AddMonitorCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveMonitorCommand as RelayCommand<MonitorConfigurationSectionRow>)?.RaiseCanExecuteChanged();
        (RescanMonitorControlsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public LayoutSectionViewModel LayoutSection { get; }

    public CursorSectionViewModel CursorSection { get; }

    /// <summary>All configured displays and their monitor-scoped editors.</summary>
    public ObservableCollection<MonitorConfigurationSectionRow> MonitorSections { get; } = [];

    /// <summary>
    /// Which monitors this machine has and what PCMode calls them -- about
    /// the machine rather than about the selected layout, so unlike every
    /// other section here it is never loaded per selection.
    /// </summary>
    public DisplayRegistrationSectionViewModel DisplayRegistrationSection { get; }

    /// <summary>The shared framework's selector projection -- see <see cref="AudioViewModel.SelectorItems"/>'s counterpart for Audio targets.</summary>
    public ObservableCollection<ConfigurationEntityListItem<string>> SelectorItems => _session.SelectorItems;

    public string? SelectedLayout
    {
        get => (string?)SelectedKey;
        set
        {
            if (value is null || Equals(SelectedKey, value))
            {
                return;
            }

            SelectKey(value);
        }
    }

    public override void SelectKey(object key)
    {
        _session.Select((string)key);
        SelectedKey = _session.SelectedKey;

        OnPropertyChanged(nameof(SelectedLayout));
        OnPropertyChanged(nameof(HasSelectedLayout));
        OnPropertyChanged(nameof(CanActivateSelected));

        Load(_session.SelectedKey!);
    }

    public bool HasSelectedLayout => _session.HasSelection;

    /// <summary>The layout Windows currently reports, or null while it is settling or unknown.</summary>
    public string? CurrentLayout
    {
        get => _currentLayout;
        private set
        {
            if (SetProperty(ref _currentLayout, value))
            {
                // The "Current" summary text is derived from the pool entries
                // themselves, so a live tick rebuilds the pool rather than
                // patching one selector item in place -- the same shape
                // Audio's own live tick rebuilds SelectorItems through
                // _session.Load in LoadResources.
                RebuildSelectorEntries();
            }
        }
    }

    /// <summary>The existing or draft name shown in the editor and used by Save.</summary>
    public string? EditingLayoutName
    {
        get => _editingLayoutName;
        private set => SetProperty(ref _editingLayoutName, value);
    }

    public bool CanActivateSelected => HasSelectedLayout && !HasUnsavedChanges && !IsBusy;

    public bool CanRescanMonitorControls =>
        !_isMonitorControlRescanRunning &&
        !IsBusy &&
        MonitorSections.Any(
            section =>
                section.ShowDdc &&
                section.Ddc.CanProbe);

    public string? InstructionMessage => !string.IsNullOrEmpty(StatusMessage)
        ? null
        : LayoutSection.Rows.Count == 0
            ? "Add at least one monitor."
            : null;

    /// <summary>
    /// The first thing any section says is wrong. The layout's own rules are
    /// asked only while a layout is actually selected: monitors are
    /// selectable -- and so their DDC/CI and television sections are
    /// editable -- on a machine with no layouts saved yet, and "include at
    /// least one monitor" is not a reason to refuse a television edit.
    /// </summary>
    public override string? ValidationProblem =>
        (HasSelectedLayout ? LayoutSection.ValidationProblem : null) ??
        (HasSelectedLayout ? CursorSection.ValidationProblem : null) ??
        MonitorSections
            .Select(section => section.Television.ValidationProblem)
            .FirstOrDefault(problem => problem is not null);

    public override bool CanSave =>
        (HasSelectedLayout || MonitorSections.Any(section => section.Television.HasAssociation)) &&
        HasUnsavedChanges &&
        !IsBusy &&
        ValidationProblem is null;

    /// <summary>
    /// Refreshes only Windows-observed display state for the already-loaded
    /// editor -- the selector's "Current" badge here, each section's own
    /// ambient state through itself. Never reloads configuration or rebuilds
    /// the editor.
    /// </summary>
    public async Task RefreshLiveStatusAsync()
    {
        if (IsBusy)
        {
            return;
        }

        (DisplayProvider? display, _) = ReadProvider<DisplayProvider>();

        if (display is null)
        {
            return;
        }

        try
        {
            DisplaySnapshot snapshot = await Task.Run(display.Discover).ConfigureAwait(true);

            CurrentLayout = snapshot.CurrentLayout is { } current &&
                _session.Items.Any(entry => string.Equals(entry.Name, current, StringComparison.OrdinalIgnoreCase))
                ? current
                : null;
        }
        catch
        {
            // Best-effort ambient observation. Keep the last good live state
            // and try again on the next tick.
        }

        await LayoutSection.RefreshLiveStatusAsync().ConfigureAwait(true);

        // A monitor can go from undetected to detected between ticks -- a
        // television that was switched off when the page opened, above all --
        // so the monitor-scoped sections re-resolve every tick. ReResolve
        // reloads only when the answer actually changed, so a quiet tick
        // never discards live readings or an edit in progress.
        RunWithoutDirtyTracking(() =>
        {
            EnsureMonitorSections();

            foreach (MonitorConfigurationSectionRow section in MonitorSections)
            {
                section.Ddc.ReResolve(section.Monitor);
                section.Television.ReResolve(section.Monitor);
            }
        });

        // Snapshotted: each await below yields back to the UI thread, where a
        // Rescan, Save, or layout switch can run EnsureMonitorSections and
        // Clear/rebuild MonitorSections before this loop resumes. Enumerating
        // the live collection across an await throws "Collection was
        // modified"; a stale row here is otherwise harmless -- it is simply
        // no longer displayed.
        foreach (MonitorConfigurationSectionRow section in MonitorSections.ToArray())
        {
            await section.Ddc.RefreshLiveStatusAsync().ConfigureAwait(true);
            await section.Television.RefreshLiveStatusAsync().ConfigureAwait(true);
        }

        UpdateCursorTargetBounds();
    }

    public async Task RescanMonitorControlsAsync()
    {
        if (!CanRescanMonitorControls)
        {
            return;
        }

        _isMonitorControlRescanRunning = true;
        OnPropertyChanged(nameof(CanRescanMonitorControls));

        try
        {
            MonitorConfigurationSectionRow[] sections =
            [
                .. MonitorSections.Where(
                    section =>
                        section.ShowDdc &&
                        section.Ddc.CanProbe)
            ];

            foreach (MonitorConfigurationSectionRow section in sections)
            {
                await section.Ddc.ProbeCapabilityAsync().ConfigureAwait(true);
                await section.Ddc.RefreshLiveStatusAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _isMonitorControlRescanRunning = false;
            OnPropertyChanged(nameof(CanRescanMonitorControls));
        }
    }

    /// <summary>
    /// Loads the configuration the first time this page is shown, then only
    /// refreshes live machine state on later visits -- reloading the editor
    /// would discard an edit left in progress.
    /// </summary>
    public void OnActivated()
    {
        if (!_loaded)
        {
            _loaded = true;
            Refresh();

            // The monitor inventory is what names every monitor the rest of
            // this page arranges, so it is scanned once on open rather than
            // waiting for somebody to press Rescan.
            _ = DisplayRegistrationSection.Inventory.ScanAsync();
        }
        else
        {
            _ = RefreshLiveStatusAsync();
        }

        _liveRefresh.Start();
    }

    public void OnDeactivated() => _liveRefresh.Stop();

    public override void Refresh()
    {
        (DisplayProvider? display, string? problem) = ReadProvider<DisplayProvider>();

        if (display is null)
        {
            _session.Clear();
            SelectedKey = null;
            CurrentLayout = null;
            EditingLayoutName = null;
            ClearDirtyState();
            SetError(problem ?? "The configuration could not be read.");
            NotifyEditingState();
            return;
        }

        List<DisplayLayoutEntry> entries =
        [
            .. display.LayoutNames.Select(name =>
                new DisplayLayoutEntry(name, string.Equals(name, _currentLayout, StringComparison.OrdinalIgnoreCase)))
        ];

        _session.Load(entries, keep: SelectedKey as string);
        SelectedKey = _session.SelectedKey;
        OnPropertyChanged(nameof(SelectedLayout));
        OnPropertyChanged(nameof(HasSelectedLayout));

        if (SelectedLayout is { } selected)
        {
            Load(selected);
        }
        else
        {
            EditingLayoutName = null;
            RunWithoutDirtyTracking(() =>
            {
                LayoutSection.Load(null);
                CursorSection.Load(null);
            });
            ClearDirtyState();
            ClearError();
            NotifyEditingState();
        }
    }

    private void Load(string name)
    {
        EditingLayoutName = name;

        RunWithoutDirtyTracking(() =>
        {
            LayoutSection.Load(name);
            CursorSection.Load(name);
        });

        ClearDirtyState();
        ClearError();
        NotifyEditingState();
    }

    /// <summary>Starts a named, unsaved layout with no monitors selected yet -- an editing-session draft, exists only in the selector until Save writes it.</summary>
    public string? StartNewLayout(string name)
    {
        if (LayoutNameProblem(name) is string problem)
        {
            return problem;
        }

        string trimmed = name.Trim();

        _session.BeginCreate(trimmed, trimmed);
        SelectedKey = _session.SelectedKey;
        OnPropertyChanged(nameof(SelectedLayout));
        OnPropertyChanged(nameof(HasSelectedLayout));

        EditingLayoutName = trimmed;

        RunWithoutDirtyTracking(() =>
        {
            LayoutSection.Load(trimmed);
            CursorSection.Load(trimmed);
        });

        MarkDirty();
        ClearError();
        NotifyEditingState();
        return null;
    }

    /// <summary>Starts a new draft with an available suggested name.</summary>
    public void StartNewLayout()
    {
        const string basis = "New display layout";
        string proposal = basis;
        int suffix = 2;

        while (_session.Items.Any(entry => string.Equals(entry.Name, proposal, StringComparison.OrdinalIgnoreCase)))
        {
            proposal = $"{basis} {suffix++}";
        }

        _ = StartNewLayout(proposal);
    }

    private async Task ActivateSelectedForShellAsync() => await ActivateSelectedAsync().ConfigureAwait(true);

    /// <summary>
    /// Applies the selected saved layout through a freshly read provider,
    /// then re-reads what Windows actually settled on.
    /// </summary>
    public async Task<string?> ActivateSelectedAsync(CancellationToken cancellationToken = default)
    {
        ClearError();

        if (SelectedLayout is not { } name)
        {
            return SetError("Choose a display layout first.");
        }

        if (HasUnsavedChanges)
        {
            return SetError("Save or discard changes before activating a layout.");
        }

        (DisplayProvider? provider, string? problem) = ReadProvider<DisplayProvider>();

        if (provider is null)
        {
            StatusMessage = problem ?? "The configuration could not be read.";
            return SetError(StatusMessage);
        }

        IsBusy = true;
        StatusMessage = $"Activating '{name}'...";

        try
        {
            await Task.Run(() => provider.Apply(name, cancellationToken), cancellationToken).ConfigureAwait(true);
            DisplaySnapshot snapshot = await Task.Run(provider.Discover, cancellationToken).ConfigureAwait(true);
            CurrentLayout = snapshot.CurrentLayout;

            StatusMessage = string.Equals(CurrentLayout, name, StringComparison.OrdinalIgnoreCase)
                ? Workspace.Apply($"'{name}' activated.")
                : $"'{name}' requested. Windows is still settling.";
            return null;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Display layout activation was cancelled.";
            return SetError(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not activate '{name}'. {ex.Message}";
            Workspace.Fault(Category, StatusMessage, ex);
            return SetError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected override void DiscardChangesCore()
    {
        if (_session.HasOverlay)
        {
            _session.Discard();
        }

        Refresh();
        StatusMessage = "Changes discarded.";
    }

    protected override string? SaveChangesCore()
    {
        string trimmed = EditingLayoutName?.Trim() ?? string.Empty;

        if (ValidationProblem is string validation)
        {
            StatusMessage = validation;
            return validation;
        }

        IsBusy = true;

        try
        {
            // A machine with no layouts saved yet can still have a monitor
            // selected, and so a television to edit. Only the layout half is
            // skipped in that case; the sections that own their own stores
            // still save.
            if (trimmed.Length > 0)
            {
                // A pending rename's origin is still what is on disk:
                // LayoutConfigurationStore.Rename has to run first, so every
                // Space/Activity/Source 'display' resource naming it is
                // rewritten, before the current in-memory arrangement below
                // overwrites what that rename just copied under the new name.
                if (!_session.IsDraft &&
                    _session.Selected?.Name is string oldName &&
                    !string.Equals(oldName, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    ModeSaveOutcome renameOutcome = LayoutConfigurationStore.Rename(Workspace.Root, oldName, trimmed);

                    if (!renameOutcome.Success)
                    {
                        StatusMessage = renameOutcome.Problem ?? "That could not be saved.";
                        Workspace.Warn(Category, StatusMessage);
                        return renameOutcome.Problem;
                    }
                }

                (IReadOnlyList<LayoutMonitorEdit> monitors, IReadOnlyDictionary<string, string> requires) =
                    LayoutSection.BuildEdit();

                ModeSaveOutcome outcome = LayoutConfigurationStore.Save(Workspace.Root, trimmed, monitors, requires);

                if (!outcome.Success)
                {
                    StatusMessage = outcome.Problem ?? "That could not be saved.";
                    Workspace.Warn(Category, StatusMessage);
                    return outcome.Problem;
                }

                // The layout itself is now on disk, and so is its own name --
                // only now can the cursor link name it, and only now does the
                // linked position's own geometry (an independent store) get
                // its turn.
                if (LayoutSection.ApplyCursorLink(trimmed) is string linkProblem)
                {
                    return ReportSectionFailure(linkProblem, trimmed);
                }

                if (CursorSection.Save() is string cursorProblem)
                {
                    return ReportSectionFailure(cursorProblem, trimmed);
                }
            }

            foreach (MonitorConfigurationSectionRow section in MonitorSections)
            {
                if (section.Television.Save() is string televisionProblem)
                {
                    return ReportSectionFailure(televisionProblem, trimmed);
                }
            }

            _session.Commit();
            Refresh();

            StatusMessage = Workspace.Apply(trimmed.Length > 0 ? $"'{trimmed}' saved." : "Saved.");
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Reports a section's refusal after the layout itself already reached disk.</summary>
    private string ReportSectionFailure(string problem, string layoutName)
    {
        StatusMessage = problem;
        Workspace.Warn(Category, problem);

        // The layout itself already committed to disk under its final name by
        // the time any of these sections gets a turn, so the session's own
        // overlay bookkeeping is done regardless of what failed next.
        _session.Commit();
        Refresh();

        if (layoutName.Length > 0)
        {
            SelectedLayout = layoutName;
        }

        return problem;
    }

    /// <summary>
    /// Copies the layout currently open in the editor -- saved or not -- into
    /// a new, unsaved draft named <paramref name="newName"/>. Nothing is
    /// written until Save. The display provider deliberately rejects two
    /// names for an identical arrangement because Windows readback could not
    /// distinguish them, so the user changes the copied arrangement and saves
    /// it through the normal path.
    /// </summary>
    public string? DuplicateSelected(string newName)
    {
        if (SelectedLayout is null)
        {
            return "Choose a display layout first.";
        }

        if (LayoutNameProblem(newName) is string problem)
        {
            return problem;
        }

        string trimmed = newName.Trim();
        string source = SelectedLayout;

        _session.BeginDuplicate(trimmed, trimmed);
        SelectedKey = _session.SelectedKey;
        OnPropertyChanged(nameof(SelectedLayout));
        OnPropertyChanged(nameof(HasSelectedLayout));

        EditingLayoutName = trimmed;
        MarkDirty();
        NotifyEditingState();
        StatusMessage =
            $"Copy of '{source}' ready as '{trimmed}'. Change at least one monitor setting, then Save.";
        return null;
    }

    /// <summary>
    /// Retitles the selected layout's working copy -- a draft, or an already
    /// saved layout -- without writing anything; the page's own Save is what
    /// persists it, under whichever name is current at that point.
    /// </summary>
    public string? RenameSelected(string newName)
    {
        if (SelectedLayout is null)
        {
            return "Choose a display layout first.";
        }

        string trimmed = newName?.Trim() ?? string.Empty;

        if (string.Equals(SelectedLayout, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (LayoutNameProblem(newName, _session.Selected?.Name) is string problem)
        {
            return problem;
        }

        bool wasDraft = _session.IsDraft;
        string oldName = SelectedLayout;

        _session.RenameCurrent(trimmed, trimmed);
        SelectedKey = _session.SelectedKey;
        OnPropertyChanged(nameof(SelectedLayout));
        OnPropertyChanged(nameof(HasSelectedLayout));

        EditingLayoutName = trimmed;
        MarkDirty();
        NotifyEditingState();

        StatusMessage = wasDraft
            ? $"New layout will be saved as '{trimmed}'."
            : $"'{oldName}' will be saved as '{trimmed}'.";

        return null;
    }

    public string? DeleteSelected()
    {
        ClearError();

        if (SelectedLayout is null)
        {
            return SetError("Choose a display layout first.");
        }

        if (_session.HasOverlay)
        {
            // An unsaved draft, or a pending rename of an existing layout:
            // either way there is nothing on disk yet that reflects the
            // current selector entry, so abandoning it is enough.
            _session.Discard();
            Refresh();
            return null;
        }

        string name = _session.Selected!.Name;
        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = LayoutConfigurationStore.Delete(Workspace.Root, name);

            if (!outcome.Success)
            {
                StatusMessage = outcome.Problem ?? "That could not be deleted.";
                Workspace.Warn(Category, StatusMessage);
                return SetError(StatusMessage);
            }

            Refresh();
            return ReportSuccess($"'{name}' deleted.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Renames the linked cursor position and the layout's own link to match, in one step.</summary>
    public string? RenameCursorPosition(string newName)
    {
        if (CursorSection.LinkedPositionName is not { } oldName)
        {
            return "Choose a cursor position first.";
        }

        string trimmed = newName?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return "Give the cursor position a name.";
        }

        if (string.Equals(oldName, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // CursorPositionConfigurationStore.Rename already calls
        // LayoutConfigurationStore.RenameModeReferences, which updates every
        // Space/Activity/Source 'cursor' resource naming the old position --
        // including whichever one this layout is linked through -- so nothing
        // further needs saving.
        ModeSaveOutcome outcome = CursorPositionConfigurationStore.Rename(Workspace.Root, oldName, trimmed);

        if (!outcome.Success)
        {
            StatusMessage = outcome.Problem ?? "That could not be renamed.";
            Workspace.Warn(Category, StatusMessage);
            return StatusMessage;
        }

        LayoutSection.RefreshCursorChoices();
        LayoutSection.SetLinkedCursorPositionAfterRename(trimmed);
        CursorSection.LoadLinkedPosition(trimmed);
        return ReportSuccess($"'{oldName}' renamed to '{trimmed}'.");
    }

    /// <summary>Saves the linked position's current X/Y under a new name, and links this layout to the copy.</summary>
    public string? DuplicateCursorPosition(string newName)
    {
        if (CursorSection.LinkedPositionName is not { } source)
        {
            return "Choose a cursor position first.";
        }

        string trimmed = newName?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return "Give the cursor position a name.";
        }

        if (LayoutSection.CursorPositionChoices.Any(existing =>
            !string.Equals(existing, LayoutSectionViewModel.NoCursorPosition, StringComparison.Ordinal) &&
            string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{trimmed}' is already a configured cursor position.";
        }

        if (CursorSection.SaveAs(trimmed) is string problem)
        {
            return problem;
        }

        LayoutSection.RefreshCursorChoices();
        LayoutSection.CursorPositionName = trimmed;
        return ReportSuccess($"'{trimmed}' created from '{source}'.");
    }

    /// <summary>
    /// Unlinks the cursor position from this layout, then deletes it outright
    /// if nothing else still needs it.
    /// </summary>
    /// <remarks>
    /// UNLINKS FIRST, ALWAYS
    ///
    /// The store's own delete is refused while any Space, Activity or Source
    /// still sets 'cursor' to this position -- including the one THIS layout
    /// is linked through, whose 'cursor' resource does not change on disk
    /// until <see cref="LayoutSectionViewModel.ApplyCursorLink"/> runs, which
    /// only happens on the next save. Rather than special-case "the only
    /// thing blocking this is the layout being edited right now," the unlink
    /// always happens, and the delete is simply attempted afterward: it
    /// succeeds immediately for a position nothing else uses, and leaves an
    /// explanatory status (not an error -- the unlink itself is a real,
    /// successful edit) the rest of the time, telling the user to save this
    /// layout and delete it again.
    /// </remarks>
    public string? DeleteCursorPosition()
    {
        if (CursorSection.LinkedPositionName is not { } name)
        {
            return "Choose a cursor position first.";
        }

        LayoutSection.CursorPositionName = LayoutSectionViewModel.NoCursorPosition;

        ModeSaveOutcome outcome = CursorPositionConfigurationStore.Delete(Workspace.Root, name);

        LayoutSection.RefreshCursorChoices();

        if (!outcome.Success)
        {
            StatusMessage =
                $"'{name}' unlinked from this layout, but not deleted yet -- {outcome.Problem} " +
                $"Save this layout, then delete '{name}' again.";
            Workspace.Warn(Category, StatusMessage);
            return null;
        }

        return ReportSuccess($"'{name}' deleted.");
    }

    private void OnSectionChanged()
    {
        MarkDirty();
        NotifyEditingState();
    }

    /// <summary>Keeps <see cref="CursorSection"/> in sync with <see cref="LayoutSectionViewModel.CursorPositionName"/> the moment it changes, in memory -- see that property's setter and <c>CursorSectionViewModel.Load</c>'s remarks for why a disk re-read would not work here.</summary>
    private void OnCursorLinkChanged()
    {
        string name = LayoutSection.CursorPositionName;

        CursorSection.LoadLinkedPosition(
            string.Equals(name, LayoutSectionViewModel.NoCursorPosition, StringComparison.Ordinal) ? null : name);
    }

    /// <summary>Rebuilds the all-display sections when a layout load replaces the monitor row objects.</summary>
    private void OnSelectedMonitorChanged(LayoutMonitorRow? monitor)
    {
        EnsureMonitorSections();
        NotifyEditingState();
    }

    /// <summary>
    /// Reloads the monitor-scoped sections after one of them wrote something
    /// that changes what the others would resolve, and clears the dirty state
    /// those immediate writes already committed.
    /// </summary>
    public void ReloadMonitorSections()
    {
        RunWithoutDirtyTracking(() =>
        {
            EnsureMonitorSections();

            foreach (MonitorConfigurationSectionRow section in MonitorSections)
            {
                section.Ddc.ReResolve(section.Monitor);
                section.Television.ReResolve(section.Monitor);
            }
        });

        NotifyEditingState();
    }

    private void EnsureMonitorSections()
    {
        if (MonitorSections.Count == LayoutSection.Rows.Count &&
            MonitorSections.Select(section => section.Monitor)
                .SequenceEqual(LayoutSection.Rows, ReferenceEqualityComparer.Instance))
        {
            return;
        }

        foreach (MonitorConfigurationSectionRow section in MonitorSections)
        {
            section.PropertyChanged -= OnMonitorControlSectionChanged;
            section.Ddc.PropertyChanged -= OnMonitorDdcStateChanged;
        }

        MonitorSections.Clear();

        foreach (LayoutMonitorRow monitor in LayoutSection.Rows)
        {
            var ddc = new MonitorDdcSectionViewModel(
                Workspace, Prompts, ReloadMonitorSections, GuardUnsavedChanges);

            var television = new MonitorTelevisionSectionViewModel(
                Workspace, OnSectionChanged, _livePowerProvider, Prompts, ReloadMonitorSections, GuardUnsavedChanges);

            ddc.Load(monitor);
            television.Load(monitor);

            var section = new MonitorConfigurationSectionRow(monitor, ddc, television);

            section.PropertyChanged += OnMonitorControlSectionChanged;
            ddc.PropertyChanged += OnMonitorDdcStateChanged;

            MonitorSections.Add(section);
        }

        OnPropertyChanged(nameof(CanRescanMonitorControls));
    }

    /// <summary>
    /// Keeps the cursor pad measuring against this layout's OWN primary
    /// monitor rather than whichever display Windows currently calls primary.
    /// The two coincide once the layout is applied -- a layout's primary IS
    /// the monitor at the virtual desktop's origin -- but while editing a
    /// layout that is not the active one they can differ, and the pad has to
    /// show the layout being edited.
    /// </summary>
    private void UpdateCursorTargetBounds()
    {
        LayoutMonitorRow? primary = LayoutSection.Rows.FirstOrDefault(row => row.Primary);

        CursorSection.SetTargetBounds(
            primary?.LayoutWidth ?? 1920,
            primary?.LayoutHeight ?? 1080);
    }

    private void OnMonitorControlSectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitorConfigurationSectionRow.ShowDdc))
        {
            OnPropertyChanged(nameof(CanRescanMonitorControls));
        }
    }

    private void OnMonitorDdcStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MonitorDdcSectionViewModel.CanProbe)
            or nameof(MonitorDdcSectionViewModel.HasMonitor))
        {
            OnPropertyChanged(nameof(CanRescanMonitorControls));
        }
    }

    /// <summary>Validates a candidate layout name for Create/Duplicate/Rename alike, the way <c>AudioViewModel.TargetNameProblem</c> validates a target name.</summary>
    /// <param name="ignoredName">The name currently being renamed, excluded from the collision check against itself.</param>
    private string? LayoutNameProblem(string? name, string? ignoredName = null)
    {
        string trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return "Give the layout a name.";
        }

        if (_session.Items.Any(entry =>
                !string.Equals(entry.Name, ignoredName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{trimmed}' is already a configured display layout.";
        }

        return null;
    }

    /// <summary>Re-projects the session's pool with a freshly observed "Current" flag, for <see cref="CurrentLayout"/>'s setter -- never discards an active Create/Duplicate/Rename overlay, per <see cref="ConfigurationEditingSession{TKey, TEntity}.Load"/>.</summary>
    private void RebuildSelectorEntries()
    {
        List<DisplayLayoutEntry> entries =
        [
            .. _session.Items.Select(entry =>
                entry with { IsCurrent = string.Equals(entry.Name, _currentLayout, StringComparison.OrdinalIgnoreCase) })
        ];

        _session.Load(entries, keep: SelectedKey as string);
    }

    private void NotifyEditingState()
    {
        UpdateCursorTargetBounds();
        OnPropertyChanged(nameof(InstructionMessage));
        NotifyValidationChanged(nameof(ValidationProblem), nameof(CanSave));
        OnPropertyChanged(nameof(CanActivateSelected));
    }

    private string? ReportSuccess(string message)
    {
        StatusMessage = Workspace.Apply(message);
        return null;
    }

    protected override void OnBusyStateChanged()
    {
        base.OnBusyStateChanged();
        OnPropertyChanged(nameof(CanActivateSelected));
        OnPropertyChanged(nameof(CanRescanMonitorControls));
    }

    protected override void OnDirtyStateChanged()
    {
        base.OnDirtyStateChanged();
        OnPropertyChanged(nameof(CanActivateSelected));
    }
}

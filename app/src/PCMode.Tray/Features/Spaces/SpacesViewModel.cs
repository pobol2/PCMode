using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PCMode.Engine;
using PCMode.Picker;
using PCMode.Tray.Configuration;
using PCMode.Tray.Presentation;
using PCMode.Tray.Settings;

namespace PCMode.Tray.Features.Spaces;

/// <summary>One stored resource choice and the label the Spaces page shows for it.</summary>
public sealed record ResourceRequirementOption(string Value, string Label);

/// <summary>What one level of the hierarchy says about one resource.</summary>
/// <remarks>
/// Four things a level can say, and they are genuinely four. At an inheriting
/// level, "Inherit" and "Do not control" mean opposite things: the first takes
/// whatever is above, while the second excludes the resource from the target.
/// Their stored values remain distinct even though a Space also uses "Do not
/// control" for its no-parent equivalent.
/// </remarks>
public static class ResourceRequirementChoice
{
    public const string Inherited = "Inherited";

    public const string Unset = "Not set";

    public const string Required = "Required";

    public const string Optional = "Optional";

    public const string Cleared = "Not set here";

    public static IReadOnlyList<ResourceRequirementOption> ForSpace { get; } =
    [
        new(Unset, "Not configured"),
        new(Required, "Always apply"),
        new(Optional, "Best effort"),
    ];

    public static IReadOnlyList<ResourceRequirementOption> ForInheritingLevel { get; } =
    [
        new(Inherited, "Use parent setting"),
        new(Required, "Always apply"),
        new(Optional, "Best effort"),
        new(Cleared, "Leave uncontrolled"),
    ];
}

/// <summary>One choice offered for the selected target's picker position.</summary>
public sealed record PickerPositionOption(string Name, PickerDirection? Direction, bool IsMultiple = false);

/// <summary>
/// What else in the configuration names a target, gathered so the UI can ask for
/// a replacement instead of a delete guessing one.
/// </summary>
public sealed record TargetReferences(IReadOnlyList<string> FavoriteDirections, bool IsStartupTarget)
{
    public bool Any => FavoriteDirections.Count > 0 || IsStartupTarget;
}

/// <summary>
/// One resource a level can control, edited as a free-form value: the engine's
/// own validation -- run through <see cref="HierarchyStore.SaveResources"/> on
/// save -- is what accepts or refuses it, the same way every other level's
/// resources are accepted or refused.
/// </summary>
public sealed class EditableResourceViewModel : INotifyPropertyChanged
{
    private string _selected;
    private string _requirement;

    public EditableResourceViewModel(
        string key,
        string displayName,
        string current,
        string requirement,
        IReadOnlyList<ResourceRequirementOption> requirementChoices,
        IReadOnlyList<string>? choices = null,
        string? availabilityMessage = null,
        string? inheritedFrom = null,
        string? inheritedValue = null)
    {
        Key = key;
        DisplayName = displayName;
        RequirementChoices = requirementChoices;
        Choices = choices ?? [];
        AvailabilityMessage = availabilityMessage;
        InheritedFrom = inheritedFrom;
        InheritedValue = inheritedValue;
        _selected = current;
        _requirement = requirement;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }

    public string DisplayName { get; }

    public IReadOnlyList<ResourceRequirementOption> RequirementChoices { get; }

    /// <summary>
    /// The bounded set of values known to be legal for this resource, or empty
    /// when any string could be. Populated only for the handful of resource
    /// kinds the engine's own providers can enumerate (cursor positions, audio
    /// outputs, RGB scenes, application run states); every other resource
    /// stays free text, same as before this existed.
    /// </summary>
    public IReadOnlyList<string> Choices { get; }

    public string? AvailabilityMessage { get; }

    public bool HasAvailabilityMessage => !string.IsNullOrWhiteSpace(AvailabilityMessage);

    /// <summary>Which parent supplies the inherited value, when there is one.</summary>
    public string? InheritedFrom { get; }

    public string? InheritedValue { get; }

    public string? InheritedMessage => HasInheritedMessage
        ? $"Inherited from {InheritedFrom}: {InheritedValue}"
        : null;

    public bool HasInheritedMessage =>
        !string.IsNullOrWhiteSpace(InheritedFrom) && !string.IsNullOrWhiteSpace(InheritedValue);

    public string Selected
    {
        get => _selected;
        set
        {
            if (string.Equals(_selected, value, StringComparison.Ordinal))
            {
                return;
            }

            _selected = value;
            RaiseChanged();
        }
    }

    public string Requirement
    {
        get => _requirement;
        set
        {
            if (string.Equals(_requirement, value, StringComparison.Ordinal))
            {
                return;
            }

            _requirement = value;
            RaiseChanged();
            RaiseChanged(nameof(IsConfigured));
            RaiseChanged(nameof(IsInherited));
            RaiseChanged(nameof(IsExcluded));
            RaiseChanged(nameof(UseBestEffort));
            RaiseChanged(nameof(StateSummary));
        }
    }

    /// <summary>Whether this level sets a value of its own, so the value box means something.</summary>
    public bool IsConfigured =>
        string.Equals(Requirement, ResourceRequirementChoice.Required, StringComparison.Ordinal) ||
        string.Equals(Requirement, ResourceRequirementChoice.Optional, StringComparison.Ordinal);

    public bool IsOptional => string.Equals(Requirement, ResourceRequirementChoice.Optional, StringComparison.Ordinal);

    public bool IsInherited =>
        string.Equals(Requirement, ResourceRequirementChoice.Inherited, StringComparison.Ordinal) &&
        HasInheritedMessage;

    public bool IsExcluded => string.Equals(Requirement, ResourceRequirementChoice.Cleared, StringComparison.Ordinal);

    public bool UseBestEffort
    {
        get => IsOptional;
        set
        {
            if (!IsConfigured)
            {
                return;
            }

            Requirement = value ? ResourceRequirementChoice.Optional : ResourceRequirementChoice.Required;
        }
    }

    public string StateSummary => IsOptional
        ? "Controlled here · Best effort"
        : IsConfigured
            ? "Controlled here"
            : IsInherited
                ? InheritedMessage!
                : IsExcluded
                    ? "Not controlled for this target"
                    : "Not added";

    public void ControlHere() => Requirement = ResourceRequirementChoice.Required;

    public void UseParentOrRemove(bool isSpace) =>
        Requirement = isSpace ? ResourceRequirementChoice.Unset : ResourceRequirementChoice.Inherited;

    public void ExcludeInherited() => Requirement = ResourceRequirementChoice.Cleared;

    /// <summary>What this level's choice is, as the store understands it.</summary>
    public ResourceEdit Edit => Requirement switch
    {
        ResourceRequirementChoice.Required => ResourceEdit.Of(Selected.Trim(), optional: false),
        ResourceRequirementChoice.Optional => ResourceEdit.Of(Selected.Trim(), optional: true),
        ResourceRequirementChoice.Cleared => ResourceEdit.Cleared,
        _ => ResourceEdit.Inherited,
    };

    private void RaiseChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>One row of the tree: a Space, an Activity or a Source.</summary>
/// <remarks>
/// The stored hierarchy remains flat and ordered, while this row carries only
/// the small amount of presentation state needed to reveal or hide descendants.
/// Selection and editing still use the same stable <see cref="NodeAddress"/>.
/// </remarks>
public sealed class HierarchyNodeViewModel : INotifyPropertyChanged, IConfigurationSelectorItem
{
    private bool _isExpanded;
    private bool _isCurrent;
    private bool _hasCurrentDescendant;

    public HierarchyNodeViewModel(
        NodeAddress address,
        NodeLevel level,
        string name,
        string displayPath,
        string? targetKey,
        SourceKind? kind,
        bool targetEnabled,
        bool hasChildren = false,
        bool isExpanded = true)
    {
        Address = address;
        Level = level;
        Name = name;
        DisplayPath = displayPath;
        TargetKey = targetKey;
        Kind = kind;
        TargetEnabled = targetEnabled;
        HasChildren = hasChildren;
        _isExpanded = hasChildren && isExpanded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public NodeAddress Address { get; }

    public NodeLevel Level { get; }

    public string Name { get; }

    public string DisplayPath { get; }

    public string? TargetKey { get; }

    public SourceKind? Kind { get; }

    public bool TargetEnabled { get; }

    /// <summary>Whether this row owns at least one row at the next level.</summary>
    public bool HasChildren { get; }

    /// <summary>Whether the row's descendants are currently shown.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            bool expanded = HasChildren && value;

            if (_isExpanded == expanded)
            {
                return;
            }

            _isExpanded = expanded;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpansionGlyph)));
        }
    }

    public string ExpansionGlyph => IsExpanded ? "▾" : "▸";

    /// <summary>Left margin, in pixels, for this row's depth.</summary>
    public double Indent => (int)Level * 18d;

    /// <summary>What the row says beside the name.</summary>
    public string Description => Level switch
    {
        NodeLevel.Space => "Space",
        NodeLevel.Activity => "Activity",
        _ => Kind switch
        {
            SourceKind.ExternalDevice => "Source — external device",
            SourceKind.TvApplication => "Source — TV app",
            _ => "Source — PC application",
        },
    };

    /// <summary>Whether this row represents a target rather than a Space.</summary>
    public bool IsActivatable => TargetKey is not null;

    /// <summary>Whether to show the "Disabled" marker beside this row.</summary>
    public bool ShowsDisabled => IsActivatable && !TargetEnabled;

    /// <summary>Whether this row is the target the engine currently records as active.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set
        {
            if (_isCurrent == value)
            {
                return;
            }

            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    /// <summary>Whether the current target is nested beneath this row.</summary>
    public bool HasCurrentDescendant
    {
        get => _hasCurrentDescendant;
        internal set
        {
            if (_hasCurrentDescendant == value)
            {
                return;
            }

            _hasCurrentDescendant = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCurrentDescendant)));
        }
    }

    object IConfigurationSelectorItem.Key => Address;
}

/// <summary>
/// The Spaces page's state: one level of the hierarchy at a time, built fresh
/// from the real configuration file every time it is opened or saved.
/// </summary>
/// <remarks>
/// THE SAME DOCUMENT, THE SAME VALIDATION
///
/// Nothing here invents a second configuration model. The tree comes from
/// <see cref="ModeConfiguration.Spaces"/>, and every change goes through
/// <see cref="HierarchyStore"/>, which validates through the same production
/// pipeline a real startup uses before writing anything.
///
/// WHAT THIS PAGE EDITS IS ONE LEVEL, NOT ONE TARGET
///
/// The resources shown belong to the selected Space, Activity or Source alone.
/// What the level above already decided is shown beside them, so that setting a
/// value here is visibly a decision to override something rather than an
/// unexplained duplicate of it.
/// </remarks>
public sealed class SpacesViewModel : ConfigurationPageViewModelBase
{
    private const string Category = "Spaces";

    private readonly PartyAudioHost? _party;
    private readonly EngineRuntime? _runtime;
    private readonly ApplicationSettingsStore? _settingsStore;
    private readonly Action<ApplicationSettings>? _onApplicationSettingsChanged;
    private readonly List<EditableResourceViewModel> _editable = [];
    private Dictionary<string, (string Value, string Requirement)> _loadedSnapshot =
        new(StringComparer.OrdinalIgnoreCase);

    private HierarchyNodeViewModel? _selected;
    private bool _canMoveUp;
    private bool _canMoveDown;
    private TargetCatalog _targets = new([]);
    private ApplicationSettings? _loadedApplicationSettings;
    private Dictionary<PickerDirection, ModeFavorite> _favoriteDraft = [];
    private string _directShortcutKeys = string.Empty;
    private string _loadedDirectShortcutKeys = string.Empty;
    private HashSet<PickerDirection> _loadedFavoriteDirections = [];
    private PickerPositionOption _selectedPickerPosition;
    private IReadOnlyList<PickerPositionOption> _pickerPositionOptions;

    /// <param name="party">
    /// The process's one shared <see cref="PartyAudioHost"/>, passed through
    /// to the fresh <see cref="ModeEngine"/> <see cref="ActivateSelectedAsync"/>
    /// builds, so a target that sets audio to the Party group starts the same
    /// mirror the Audio page itself would report on. Null outside a hosted
    /// process.
    /// </param>
    /// <param name="runtime">
    /// The process's one live <see cref="EngineRuntime"/>, read fresh on every
    /// activation to show which row is the current target -- the same
    /// snapshot-not-subscription pattern <c>HomeViewModel</c> uses, since
    /// Target has no shell-wide status view model to subscribe to instead.
    /// Null outside a hosted process: no row is ever marked current.
    /// </param>
    /// <param name="settingsStore">
    /// Read and written here for the selected target's picker favorite and
    /// direct shortcut, and read to describe what a delete would still leave
    /// dangling. Null outside a hosted process: quick access is hidden and a
    /// delete never needs a replacement.
    /// </param>
    /// <param name="onApplicationSettingsChanged">
    /// Called after a save or delete changes picker favorites, shortcuts, or
    /// startup, so the composition root can restart the picker and rebuild
    /// global hotkeys with what is now on disk.
    /// </param>
    public SpacesViewModel(
        IConfigurationWorkspace workspace,
        PartyAudioHost? party = null,
        EngineRuntime? runtime = null,
        ApplicationSettingsStore? settingsStore = null,
        Action<ApplicationSettings>? onApplicationSettingsChanged = null) : base(workspace)
    {
        _party = party;
        _runtime = runtime;
        _settingsStore = settingsStore;
        _onApplicationSettingsChanged = onApplicationSettingsChanged;
        _pickerPositionOptions = StandardPickerPositions();
        _selectedPickerPosition = _pickerPositionOptions[0];
    }

    /// <summary>Every Space, Activity and Source, in configuration order.</summary>
    public ObservableCollection<HierarchyNodeViewModel> Nodes { get; } = [];

    /// <summary>The currently expanded projection of <see cref="Nodes"/>.</summary>
    public ObservableCollection<HierarchyNodeViewModel> VisibleNodes { get; } = [];

    /// <summary>Settings this level actively applies.</summary>
    public ObservableCollection<EditableResourceViewModel> ConfiguredSettings { get; } = [];

    /// <summary>Settings supplied by a parent and not overridden here.</summary>
    public ObservableCollection<EditableResourceViewModel> InheritedSettings { get; } = [];

    /// <summary>Settings known to this machine but not yet controlled here.</summary>
    public ObservableCollection<EditableResourceViewModel> AddableSettings { get; } = [];

    public bool HasConfiguredSettings => ConfiguredSettings.Count > 0;

    public bool HasInheritedSettings => InheritedSettings.Count > 0;

    public bool HasAddableSettings => AddableSettings.Count > 0;

    public HierarchyNodeViewModel? Selected
    {
        get => _selected;
        private set
        {
            if (ReferenceEquals(_selected, value))
            {
                return;
            }

            _selected = value;
            SelectedKey = value?.Address;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DuplicateLabel));
            OnPropertyChanged(nameof(CanDuplicate));
            OnPropertyChanged(nameof(CanManage));
            OnPropertyChanged(nameof(CanAddActivity));
            OnPropertyChanged(nameof(CanAddSource));
            OnPropertyChanged(nameof(AddActivityLabel));
            OnPropertyChanged(nameof(AddSourceLabel));
            OnPropertyChanged(nameof(CanActivateSelected));
            OnPropertyChanged(nameof(ShowsPickerConfiguration));

            if (value is not null)
            {
                Load(value);
            }
        }
    }

    public override void SelectKey(object key)
    {
        if (key is NodeAddress address)
        {
            Selected = Nodes.FirstOrDefault(node => node.Address == address);
        }
    }

    /// <summary>Whether the selected node can move earlier among its siblings.</summary>
    public bool CanMoveUp
    {
        get => _canMoveUp;
        private set => SetProperty(ref _canMoveUp, value);
    }

    /// <summary>Whether the selected node can move later among its siblings.</summary>
    public bool CanMoveDown
    {
        get => _canMoveDown;
        private set => SetProperty(ref _canMoveDown, value);
    }

    /// <summary>The duplicate action in the words of the selected hierarchy level.</summary>
    public string DuplicateLabel => Selected?.Level switch
    {
        NodeLevel.Space => "Duplicate space",
        NodeLevel.Activity => "Duplicate activity",
        NodeLevel.Source => "Duplicate source",
        _ => "Duplicate",
    };

    public bool CanDuplicate => Selected is not null;

    public bool CanManage => Selected is not null;

    public bool CanAddActivity => Selected is not null;

    public bool CanAddSource => Selected?.Address.ActivityId is not null;

    public string AddActivityLabel => Selected is null
        ? "Activity"
        : $"Activity in {SpaceNameOf(Selected)}";

    public string AddSourceLabel => Selected?.Address.ActivityId is null
        ? "Source"
        : $"Source in {ActivityNameOf(Selected)}";

    /// <summary>Picker assignments exist only for activatable Activities and Sources.</summary>
    public bool ShowsPickerConfiguration => _settingsStore is not null && Selected is { IsActivatable: true };

    public IReadOnlyList<PickerPositionOption> PickerPositionOptions => _pickerPositionOptions;

    public PickerPositionOption SelectedPickerPosition
    {
        get => _selectedPickerPosition;
        set
        {
            if (value is null || value.IsMultiple || ReferenceEquals(_selectedPickerPosition, value))
            {
                return;
            }

            _selectedPickerPosition = value;
            OnPropertyChanged();
            SetPickerPosition(value.Direction);
        }
    }

    /// <summary>Comma-separated direct Ctrl+Alt letters for the selected target.</summary>
    public string DirectShortcutKeys
    {
        get => _directShortcutKeys;
        set => SetDirtyProperty(ref _directShortcutKeys, value ?? string.Empty);
    }

    public override bool CanSave =>
        Selected is not null &&
        HasUnsavedChanges &&
        !IsBusy;

    /// <summary>
    /// Whether the selected node is an enabled target (an Activity or a
    /// Source, not a bare Space) that can actually be transitioned to.
    /// </summary>
    public bool CanActivateSelected =>
        Selected is { IsActivatable: true, TargetEnabled: true } &&
        !HasUnsavedChanges &&
        !IsBusy;

    public void AddSetting(EditableResourceViewModel resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        resource.ControlHere();
    }

    public void OverrideSetting(EditableResourceViewModel resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        resource.ControlHere();
    }

    public void RemoveSetting(EditableResourceViewModel resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        resource.UseParentOrRemove(Selected?.Level == NodeLevel.Space);
    }

    public void ExcludeInheritedSetting(EditableResourceViewModel resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        resource.ExcludeInherited();
    }

    /// <summary>Shows or hides a row's descendants without changing the stored hierarchy.</summary>
    public void ToggleExpanded(HierarchyNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!node.HasChildren)
        {
            return;
        }

        if (node.IsExpanded && _selected is not null && IsDescendantOf(_selected, node))
        {
            Selected = node;
        }

        node.IsExpanded = !node.IsExpanded;
        RebuildVisibleNodes();
    }

    /// <summary>Whether <paramref name="candidate"/> is nested below <paramref name="ancestor"/>.</summary>
    public static bool IsDescendantOf(HierarchyNodeViewModel candidate, HierarchyNodeViewModel ancestor)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(ancestor);

        if ((int)candidate.Level <= (int)ancestor.Level ||
            !string.Equals(candidate.Address.SpaceId, ancestor.Address.SpaceId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ancestor.Level == NodeLevel.Space ||
            string.Equals(candidate.Address.ActivityId, ancestor.Address.ActivityId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>(Re)reads the configuration file and the hierarchy from it.</summary>
    public override void Refresh()
    {
        NodeAddress? keep = _selected?.Address;
        bool startCollapsed = Nodes.Count == 0;
        HashSet<NodeAddress> collapsed =
        [
            .. Nodes.Where(node => node.HasChildren && !node.IsExpanded).Select(node => node.Address),
        ];

        (ModeConfiguration? configuration, _, string? readProblem) = ReadConfiguration();

        if (configuration is null)
        {
            Nodes.Clear();
            VisibleNodes.Clear();
            ClearForm();
            ClearSettingGroups();
            StatusMessage = readProblem;
            return;
        }

        IReadOnlyList<ConfiguredSpace> spaces = configuration.Spaces;
        _targets = configuration.Targets;

        Nodes.Clear();

        foreach (HierarchyNodeViewModel node in Flatten(spaces))
        {
            if (startCollapsed || collapsed.Contains(node.Address))
            {
                node.IsExpanded = false;
            }

            Nodes.Add(node);
        }

        RebuildVisibleNodes();
        RefreshCurrentTarget();

        StatusMessage = $"{spaces.Count} space(s).";

        _selected = keep is null
            ? Nodes.FirstOrDefault()
            : Nodes.FirstOrDefault(node => node.Address == keep) ?? Nodes.FirstOrDefault();

        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(DuplicateLabel));
        OnPropertyChanged(nameof(CanDuplicate));
        OnPropertyChanged(nameof(CanManage));
        OnPropertyChanged(nameof(CanAddActivity));
        OnPropertyChanged(nameof(CanAddSource));
        OnPropertyChanged(nameof(AddActivityLabel));
        OnPropertyChanged(nameof(AddSourceLabel));
        OnPropertyChanged(nameof(ShowsPickerConfiguration));

        if (_selected is not null)
        {
            SelectedKey = _selected.Address;
            Load(_selected);
        }
        else
        {
            SelectedKey = null;
            ClearForm();
            ClearSettingGroups();
            ClearPickerConfiguration();
            CanMoveUp = false;
            CanMoveDown = false;
        }
    }

    /// <summary>Refreshes which row is marked as the engine's current target, without reloading anything else.</summary>
    public void RefreshCurrentTarget()
    {
        string? currentKey = _runtime?.Engine.ReadCurrentTargetKey(out _);
        HierarchyNodeViewModel? current = currentKey is null
            ? null
            : Nodes.FirstOrDefault(node =>
                node.TargetKey is not null &&
                string.Equals(node.TargetKey, currentKey, StringComparison.OrdinalIgnoreCase));

        foreach (HierarchyNodeViewModel node in Nodes)
        {
            node.IsCurrent = ReferenceEquals(node, current);
            node.HasCurrentDescendant = current is not null && IsDescendantOf(current, node);
        }
    }

    /// <summary>Adds a Space with no activities in it yet.</summary>
    public string? CreateSpace(string name) =>
        Perform(() => HierarchyStore.CreateSpace(Workspace.Root, name), $"'{name.Trim()}' created.", name);

    /// <summary>Adds an Activity to the selected Space.</summary>
    public string? CreateActivity(string name)
    {
        if (_selected is null)
        {
            return "Choose a space first.";
        }

        return Perform(
            () => HierarchyStore.CreateActivity(Workspace.Root, _selected.Address.SpaceId, name),
            $"'{name.Trim()}' created.",
            name);
    }

    /// <summary>Adds a Source to the selected Activity, or to the selected Source's Activity.</summary>
    public string? CreateSource(string name, SourceKind kind)
    {
        if (_selected?.Address.ActivityId is not string activityId)
        {
            return "Choose an activity first.";
        }

        return Perform(
            () => HierarchyStore.CreateSource(Workspace.Root, _selected.Address.SpaceId, activityId, name, kind),
            $"'{name.Trim()}' created.",
            name);
    }

    /// <summary>Copies the selected hierarchy level beside the original.</summary>
    public string? DuplicateSelected(string name)
    {
        if (_selected is null)
        {
            return "Choose a space, activity or source first.";
        }

        return Perform(
            () => HierarchyStore.Duplicate(Workspace.Root, _selected.Address, name),
            $"'{name.Trim()}' duplicated.",
            name);
    }

    /// <summary>Renames the selected node.</summary>
    public string? RenameSelected(string newName)
    {
        if (_selected is null)
        {
            return "Choose a space, activity or source first.";
        }

        string previous = _selected.Name;

        return Perform(
            () => HierarchyStore.Rename(Workspace.Root, _selected.Address, newName),
            $"'{previous}' renamed to '{newName.Trim()}'.",
            newName);
    }

    /// <summary>
    /// Enables or disables one target while keeping the target and all of its
    /// configuration in place.
    /// </summary>
    public string? SetTargetEnabled(HierarchyNodeViewModel node, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!node.IsActivatable)
        {
            return "A space is not a target and cannot be enabled or disabled.";
        }

        return Perform(
            () => HierarchyStore.SetEnabled(Workspace.Root, node.Address, enabled),
            $"'{node.DisplayPath}' {(enabled ? "enabled" : "disabled")}.");
    }

    /// <summary>
    /// Deletes the selected node and everything under it.
    /// <paramref name="favoriteReplacements"/> (direction name to replacement
    /// target key) must cover every picker direction currently favoring a
    /// target being removed, and <paramref name="startupReplacement"/> is the
    /// target to switch startup to if it was one; null turns startup
    /// behaviour off instead. Refused, leaving nothing changed, if a
    /// reference is left unresolved.
    /// </summary>
    public string? DeleteSelected(
        IReadOnlyDictionary<string, string>? favoriteReplacements = null, string? startupReplacement = null)
    {
        if (_selected is null)
        {
            return "Choose a space, activity or source first.";
        }

        string name = _selected.DisplayPath;
        bool touchesApplicationSettings = favoriteReplacements is not null || startupReplacement is not null;

        string? problem = Perform(
            () => HierarchyStore.Delete(Workspace.Root, _selected.Address, favoriteReplacements, startupReplacement),
            $"'{name}' deleted.");

        if (problem is null && touchesApplicationSettings && _settingsStore is not null)
        {
            try
            {
                _onApplicationSettingsChanged?.Invoke(_settingsStore.Load(_targets));
            }
            catch (ModeConfigurationException)
            {
                // The delete itself already succeeded; a picker/hotkey restart is
                // a convenience on top of it, not something to fail the delete over.
            }
        }

        return problem;
    }

    /// <summary>Every enabled target other than the ones deleting <paramref name="node"/> would remove.</summary>
    public IReadOnlyList<ConfiguredTarget> AlternativesTo(HierarchyNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);

        IReadOnlyList<string> removed = KeysUnder(node);

        return
        [
            .. _targets.Targets.Where(target =>
                target.Enabled && !removed.Contains(target.Key, StringComparer.OrdinalIgnoreCase))
        ];
    }

    /// <summary>
    /// Which picker favorites and startup behaviour currently name a target
    /// that deleting <paramref name="node"/> would remove, so a delete can
    /// ask for a replacement for each one instead of guessing.
    /// </summary>
    public TargetReferences DescribeReferences(HierarchyNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (_settingsStore is null)
        {
            return new TargetReferences([], false);
        }

        IReadOnlyList<string> removed = KeysUnder(node);

        try
        {
            ApplicationSettings settings = _settingsStore.Load(_targets);

            bool Removed(string? reference) =>
                _targets.Resolve(reference) is ConfiguredTarget target &&
                removed.Contains(target.Key, StringComparer.OrdinalIgnoreCase);

            List<string> directions =
            [
                .. settings.Favorites.All
                    .Where(favorite => Removed(favorite.ModeName))
                    .Select(favorite => favorite.Direction.ToString())
            ];

            bool startup = settings.LaunchBehavior == LaunchBehavior.SwitchToMode && Removed(settings.StartupTarget);

            return new TargetReferences(directions, startup);
        }
        catch (ModeConfigurationException)
        {
            return new TargetReferences([], false);
        }
    }

    /// <summary>Every target key under (or equal to) <paramref name="node"/>, for reference lookups.</summary>
    private IReadOnlyList<string> KeysUnder(HierarchyNodeViewModel node)
    {
        string prefix = node.Level switch
        {
            NodeLevel.Space => node.Address.SpaceId + TargetIdentity.Separator,
            _ => node.TargetKey!,
        };

        return
        [
            .. Nodes
                .Where(candidate => candidate.TargetKey is not null)
                .Select(candidate => candidate.TargetKey!)
                .Where(key =>
                    node.Level == NodeLevel.Space
                        ? key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(key, prefix, StringComparison.OrdinalIgnoreCase) ||
                          key.StartsWith(prefix + TargetIdentity.Separator, StringComparison.OrdinalIgnoreCase))
        ];
    }

    /// <summary>Moves the selected node one place earlier or later among its siblings.</summary>
    public string? MoveSelected(bool up)
    {
        if (_selected is null)
        {
            return "Choose a space, activity or source first.";
        }

        return Perform(
            () => HierarchyStore.Move(Workspace.Root, _selected.Address, up),
            $"'{_selected.Name}' moved.");
    }

    /// <summary>
    /// Saves whatever the editable resources currently hold for the selected
    /// level. Returns null on success, or the reason it was refused.
    /// </summary>
    protected override string? SaveChangesCore()
    {
        if (_selected is null)
        {
            return "Choose a space, activity or source first.";
        }

        var edits = new Dictionary<string, ResourceEdit>(StringComparer.OrdinalIgnoreCase);

        foreach (EditableResourceViewModel resource in _editable)
        {
            if (!_loadedSnapshot.TryGetValue(resource.Key, out (string Value, string Requirement) original) ||
                !string.Equals(original.Value, resource.Selected, StringComparison.Ordinal) ||
                !string.Equals(original.Requirement, resource.Requirement, StringComparison.Ordinal))
            {
                edits[resource.Key] = resource.Edit;
            }
        }

        ApplicationSettings? applicationCandidate = null;
        bool applicationChanged = PickerConfigurationChanged();

        if (applicationChanged)
        {
            string? shortcutProblem = BuildApplicationCandidate(out applicationCandidate);

            if (shortcutProblem is not null)
            {
                StatusMessage = shortcutProblem;
                return shortcutProblem;
            }
        }

        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = HierarchyStore.SaveResources(Workspace.Root, _selected.Address, edits);

            if (!outcome.Success)
            {
                StatusMessage = outcome.Problem ?? "That could not be saved.";
                Workspace.Warn(Category, StatusMessage);
                return StatusMessage;
            }

            if (applicationCandidate is not null && _settingsStore is not null)
            {
                SettingsSaveOutcome application = _settingsStore.Save(applicationCandidate, _targets);

                if (!application.Success)
                {
                    StatusMessage = application.Problem ?? "The picker and shortcut settings could not be saved.";
                    Workspace.Warn(Category, StatusMessage);
                    return StatusMessage;
                }
            }

            string displayPath = _selected.DisplayPath;
            Refresh();
            StatusMessage = Workspace.Apply($"'{displayPath}' saved.");

            if (applicationCandidate is not null)
            {
                _onApplicationSettingsChanged?.Invoke(applicationCandidate);
            }

            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected override void DiscardChangesCore() => Refresh();

    /// <summary>
    /// Runs a full target transition through a freshly built engine -- the
    /// same production pipeline a real startup uses, built fresh for this one
    /// call the same way every other page's Activate button builds a fresh
    /// provider. No state persists between activations here; the resulting
    /// mode is recorded to disk by <see cref="ModeEngine"/> itself, same as a
    /// real run.
    /// </summary>
    public async Task<string?> ActivateSelectedAsync(CancellationToken cancellationToken = default)
    {
        ClearError();

        if (_selected is not { } selected)
        {
            return SetError("Choose a space, activity or source first.");
        }

        if (selected.TargetKey is not string target)
        {
            return SetError("A space is not a target and cannot be activated.");
        }

        if (!selected.TargetEnabled)
        {
            return SetError($"'{selected.DisplayPath}' is disabled and cannot be activated.");
        }

        if (HasUnsavedChanges)
        {
            return SetError("Save or discard changes before activating a target.");
        }

        IsBusy = true;
        StatusMessage = $"Activating '{selected.DisplayPath}'...";

        try
        {
            ModeEngine engine = EngineBootstrap.Create(Workspace.Root, _party);
            TransitionOutcome outcome = await engine.ApplyAsync(target, cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            StatusMessage = outcome.Message;

            if (outcome.Succeeded)
            {
                Workspace.Note(Category, outcome.Message);
                return null;
            }

            Workspace.Warn(Category, outcome.Message);
            return SetError(outcome.Message);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Activation was cancelled.";
            return SetError(StatusMessage);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not activate '{selected.DisplayPath}'. {exception.Message}";
            Workspace.Fault(Category, $"Could not activate '{selected.DisplayPath}'.", exception);
            return SetError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Runs one store operation: refuse, or save and refresh.
    /// </summary>
    private string? Perform(Func<ModeSaveOutcome> operation, string success, string? nameToCheck = null)
    {
        if (nameToCheck is not null && string.IsNullOrWhiteSpace(nameToCheck))
        {
            return "Give it a name.";
        }

        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = operation();

            if (!outcome.Success)
            {
                StatusMessage = outcome.Problem ?? "That could not be saved.";
                Workspace.Warn(Category, StatusMessage);
                return outcome.Problem;
            }

            Refresh();
            StatusMessage = Workspace.Apply(success);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The whole tree, in the order a person reads it.</summary>
    private static IEnumerable<HierarchyNodeViewModel> Flatten(IReadOnlyList<ConfiguredSpace> spaces)
    {
        foreach (ConfiguredSpace space in spaces)
        {
            yield return new HierarchyNodeViewModel(
                new NodeAddress(space.Id), NodeLevel.Space, space.Name, space.Name, null, null, false,
                hasChildren: space.Activities.Count > 0);

            foreach (ConfiguredActivity activity in space.Activities)
            {
                ConfiguredTarget target = ConfiguredTarget.For(space, activity, null);

                yield return new HierarchyNodeViewModel(
                    new NodeAddress(space.Id, activity.Id),
                    NodeLevel.Activity,
                    activity.Name,
                    target.DisplayPath,
                    target.Key,
                    null,
                    target.Enabled,
                    hasChildren: activity.Sources.Count > 0);

                foreach (ConfiguredSource source in activity.Sources)
                {
                    ConfiguredTarget sourceTarget = ConfiguredTarget.For(space, activity, source);

                    yield return new HierarchyNodeViewModel(
                        new NodeAddress(space.Id, activity.Id, source.Id),
                        NodeLevel.Source,
                        source.Name,
                        sourceTarget.DisplayPath,
                        sourceTarget.Key,
                        source.Kind,
                        sourceTarget.Enabled);
                }
            }
        }
    }

    /// <summary>Rebuilds the list projection according to the two possible ancestor levels.</summary>
    private void RebuildVisibleNodes()
    {
        VisibleNodes.Clear();

        HierarchyNodeViewModel? currentSpace = null;
        HierarchyNodeViewModel? currentActivity = null;

        foreach (HierarchyNodeViewModel node in Nodes)
        {
            switch (node.Level)
            {
                case NodeLevel.Space:
                    currentSpace = node;
                    currentActivity = null;
                    VisibleNodes.Add(node);
                    break;

                case NodeLevel.Activity:
                    currentActivity = node;

                    if (currentSpace?.IsExpanded == true)
                    {
                        VisibleNodes.Add(node);
                    }

                    break;

                case NodeLevel.Source:
                    if (currentSpace?.IsExpanded == true && currentActivity?.IsExpanded == true)
                    {
                        VisibleNodes.Add(node);
                    }

                    break;
            }
        }
    }

    private string SpaceNameOf(HierarchyNodeViewModel node) =>
        Nodes.FirstOrDefault(candidate =>
            candidate.Level == NodeLevel.Space &&
            string.Equals(candidate.Address.SpaceId, node.Address.SpaceId, StringComparison.OrdinalIgnoreCase))?.Name
        ?? node.Address.SpaceId;

    private string ActivityNameOf(HierarchyNodeViewModel node) =>
        Nodes.FirstOrDefault(candidate =>
            candidate.Level == NodeLevel.Activity &&
            string.Equals(candidate.Address.SpaceId, node.Address.SpaceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Address.ActivityId, node.Address.ActivityId, StringComparison.OrdinalIgnoreCase))?.Name
        ?? node.Address.ActivityId
        ?? "activity";

    /// <summary>Reloads the selected level's own resources without touching the file.</summary>
    private void Load(HierarchyNodeViewModel node)
    {
        ClearForm();
        ClearSettingGroups();
        LoadPickerConfiguration(node);
        _loadedSnapshot = new Dictionary<string, (string Value, string Requirement)>(StringComparer.OrdinalIgnoreCase);

        int index = Nodes.IndexOf(node);
        HierarchyNodeViewModel? previous = index > 0 ? Nodes[index - 1] : null;
        HierarchyNodeViewModel? next = index >= 0 && index < Nodes.Count - 1 ? Nodes[index + 1] : null;

        // Only among siblings: moving an Activity "up" past its Space would be
        // a reparent, and reparenting is a different operation.
        CanMoveUp = previous is not null && IsSibling(previous, node);
        CanMoveDown = next is not null && IsSibling(next, node);

        (ModeConfiguration? configuration, IReadOnlyList<IModeResourceProvider> providers, string? readProblem) = ReadConfiguration();

        if (configuration is null)
        {
            StatusMessage = readProblem;
            return;
        }

        if (Locate(configuration.Spaces, node.Address) is not (ConfiguredSpace space, _, _))
        {
            return;
        }

        IReadOnlyList<ResourceAssignment> own = OwnResourcesOf(configuration.Spaces, node.Address);
        Dictionary<string, ResourceAssignment> current =
            own.ToDictionary(resource => resource.Key, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, (ConfiguredResource Resource, string From)> inherited =
            InheritedAt(configuration.Spaces, node.Address, space);

        IReadOnlyList<ResourceRequirementOption> requirementChoices = node.Level == NodeLevel.Space
            ? ResourceRequirementChoice.ForSpace
            : ResourceRequirementChoice.ForInheritingLevel;

        string Label(string key) =>
            providers.FirstOrDefault(provider =>
                string.Equals(provider.Key, key, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? key;

        string? Availability(string key, string value)
        {
            IModeResourceProvider? provider = providers.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));

            if (provider is not IAvailableProvider available)
            {
                return null;
            }

            ResourceAvailability availability = available.AvailabilityOf(value);
            return availability.State == AvailabilityState.Available ? null : availability.Explain();
        }

        IReadOnlyList<string> ChoicesFor(string key)
        {
            IModeResourceProvider? provider = providers.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));

            return provider switch
            {
                CursorProvider cursor => cursor.PositionNames,
                AudioProvider audio => audio.SelectableNames,
                CaseRgbProvider caseRgb =>
                    [.. caseRgb.Scenes.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)],
                ApplicationProvider application => application.Definition.CanStop
                    ? [ApplicationProvider.Running, ApplicationProvider.Stopped]
                    : [ApplicationProvider.Running],
                _ => [],
            };
        }

        // What this level itself controls.
        foreach ((string key, ResourceAssignment assignment) in current)
        {
            inherited.TryGetValue(key, out (ConfiguredResource Resource, string From) above);

            _editable.Add(new EditableResourceViewModel(
                key,
                Label(key),
                assignment.Value ?? above.Resource?.Value ?? string.Empty,
                assignment.Clears
                    ? ResourceRequirementChoice.Cleared
                    : assignment.Optional
                        ? ResourceRequirementChoice.Optional
                        : ResourceRequirementChoice.Required,
                requirementChoices,
                choices: ChoicesFor(key),
                availabilityMessage: assignment.Value is null ? null : Availability(key, assignment.Value),
                inheritedFrom: above.Resource is null ? null : above.From,
                inheritedValue: above.Resource?.Value));
        }

        // What a parent already decided, and this level has not overridden.
        foreach ((string key, (ConfiguredResource resource, string from)) in inherited)
        {
            if (current.ContainsKey(key))
            {
                continue;
            }

            _editable.Add(new EditableResourceViewModel(
                key,
                Label(key),
                resource.Value,
                ResourceRequirementChoice.Inherited,
                requirementChoices,
                choices: ChoicesFor(key),
                inheritedFrom: from,
                inheritedValue: resource.Value + (resource.Optional ? " (best effort)" : string.Empty)));
        }

        // Every resource this machine knows about, offered as addable when this
        // level neither controls it nor inherits it.
        foreach (IModeResourceProvider provider in providers)
        {
            if (current.ContainsKey(provider.Key) || inherited.ContainsKey(provider.Key))
            {
                continue;
            }

            _editable.Add(new EditableResourceViewModel(
                provider.Key,
                provider.DisplayName,
                string.Empty,
                node.Level == NodeLevel.Space ? ResourceRequirementChoice.Unset : ResourceRequirementChoice.Inherited,
                requirementChoices,
                choices: ChoicesFor(provider.Key)));
        }

        foreach (EditableResourceViewModel resource in _editable)
        {
            resource.PropertyChanged += OnResourcePropertyChanged;
        }

        RebuildSettingGroups();

        _loadedSnapshot = _editable.ToDictionary(
            resource => resource.Key,
            resource => (resource.Selected, resource.Requirement),
            StringComparer.OrdinalIgnoreCase);

        ClearDirtyState();
    }

    private void ClearForm()
    {
        foreach (EditableResourceViewModel resource in _editable)
        {
            resource.PropertyChanged -= OnResourcePropertyChanged;
        }

        _editable.Clear();
    }

    private void OnResourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkDirty();

        if (e.PropertyName == nameof(EditableResourceViewModel.Requirement))
        {
            RebuildSettingGroups();
        }

        OnPropertyChanged(nameof(CanSave));
    }

    private void RebuildSettingGroups()
    {
        ConfiguredSettings.Clear();
        InheritedSettings.Clear();
        AddableSettings.Clear();

        foreach (EditableResourceViewModel resource in _editable)
        {
            if (resource.IsConfigured)
            {
                ConfiguredSettings.Add(resource);
            }
            else if (resource.IsInherited)
            {
                InheritedSettings.Add(resource);
            }
            else
            {
                AddableSettings.Add(resource);
            }
        }

        OnPropertyChanged(nameof(HasConfiguredSettings));
        OnPropertyChanged(nameof(HasInheritedSettings));
        OnPropertyChanged(nameof(HasAddableSettings));
    }

    private void ClearSettingGroups()
    {
        ConfiguredSettings.Clear();
        InheritedSettings.Clear();
        AddableSettings.Clear();

        OnPropertyChanged(nameof(HasConfiguredSettings));
        OnPropertyChanged(nameof(HasInheritedSettings));
        OnPropertyChanged(nameof(HasAddableSettings));
    }

    private void LoadPickerConfiguration(HierarchyNodeViewModel node)
    {
        ClearPickerConfiguration();

        if (_settingsStore is null || node.TargetKey is not string target)
        {
            return;
        }

        try
        {
            _loadedApplicationSettings = _settingsStore.Load(_targets);
            _favoriteDraft = _loadedApplicationSettings.Favorites.All.ToDictionary(favorite => favorite.Direction);
            _loadedFavoriteDirections =
            [
                .. _favoriteDraft.Values
                    .Where(favorite => SameTarget(favorite.ModeName, target))
                    .Select(favorite => favorite.Direction),
            ];
            SetPickerPositionSelection(_loadedFavoriteDirections);
            _directShortcutKeys = FormatShortcutKeys(
                _loadedApplicationSettings.DirectShortcuts
                    .Where(shortcut => SameTarget(shortcut.Target, target))
                    .Select(shortcut => shortcut.Key));
            _loadedDirectShortcutKeys = _directShortcutKeys;
            OnPropertyChanged(nameof(DirectShortcutKeys));
        }
        catch (ModeConfigurationException exception)
        {
            StatusMessage = $"The picker and shortcut settings could not be read. {exception.Message}";
        }
    }

    private void ClearPickerConfiguration()
    {
        _loadedApplicationSettings = null;
        _favoriteDraft = [];
        _loadedFavoriteDirections = [];
        _pickerPositionOptions = StandardPickerPositions();
        _selectedPickerPosition = _pickerPositionOptions[0];
        _directShortcutKeys = string.Empty;
        _loadedDirectShortcutKeys = string.Empty;
        OnPropertyChanged(nameof(DirectShortcutKeys));
        OnPropertyChanged(nameof(PickerPositionOptions));
        OnPropertyChanged(nameof(SelectedPickerPosition));
    }

    private void SetPickerPositionSelection(IReadOnlyCollection<PickerDirection> directions)
    {
        List<PickerPositionOption> options = [.. StandardPickerPositions()];

        if (directions.Count > 1)
        {
            string names = string.Join(", ", directions.Select(DisplayDirection));
            options.Insert(0, new PickerPositionOption($"Multiple positions ({names})", null, IsMultiple: true));
        }

        _pickerPositionOptions = options;
        _selectedPickerPosition = directions.Count switch
        {
            0 => options.Single(option => option.Direction is null && !option.IsMultiple),
            1 => options.Single(option => option.Direction == directions.First()),
            _ => options[0],
        };

        OnPropertyChanged(nameof(PickerPositionOptions));
        OnPropertyChanged(nameof(SelectedPickerPosition));
    }

    private static IReadOnlyList<PickerPositionOption> StandardPickerPositions() =>
    [
        new("None", null),
        new("Top", PickerDirection.Up),
        new("Right", PickerDirection.Right),
        new("Bottom", PickerDirection.Down),
        new("Left", PickerDirection.Left),
    ];

    private static string DisplayDirection(PickerDirection direction) => direction switch
    {
        PickerDirection.Up => "Top",
        PickerDirection.Down => "Bottom",
        _ => direction.ToString(),
    };

    /// <summary>Assigns the selected target to one picker position, unassigning it from every other.</summary>
    private void SetPickerPosition(PickerDirection? direction)
    {
        if (_selected?.TargetKey is not string target || _loadedApplicationSettings is null)
        {
            return;
        }

        foreach (PickerDirection assigned in Enum.GetValues<PickerDirection>().Where(IsPickerDirection).ToArray())
        {
            _favoriteDraft[assigned] = UnassignedFavorite(_favoriteDraft[assigned]);
        }

        if (direction is PickerDirection selectedDirection)
        {
            ModeFavorite existing = _favoriteDraft[selectedDirection];
            ModeFavorite appearance = FavoriteSet.Default[selectedDirection];
            _favoriteDraft[selectedDirection] = existing with
            {
                Id = $"{selectedDirection.ToString().ToLowerInvariant()}-{target.ToLowerInvariant()}",
                ModeName = target,
                Label = _selected.Name,
                Icon = appearance.Icon,
                IconOffset = appearance.IconOffset,
                LabelOffset = appearance.LabelOffset,
                Hotkey = null,
            };
        }

        MarkDirty();
        OnPropertyChanged(nameof(CanSave));
    }

    private static ModeFavorite UnassignedFavorite(ModeFavorite appearance) => appearance with
    {
        Id = $"{appearance.Direction.ToString().ToLowerInvariant()}-none",
        ModeName = string.Empty,
        Label = "Not assigned",
        Icon = FavoriteIcon.None,
        Hotkey = null,
    };

    private bool IsPickerDirection(PickerDirection direction) =>
        _selected?.TargetKey is string target &&
        _favoriteDraft.TryGetValue(direction, out ModeFavorite? favorite) &&
        SameTarget(favorite.ModeName, target);

    private bool PickerConfigurationChanged()
    {
        if (!ShowsPickerConfiguration || _loadedApplicationSettings is null)
        {
            return false;
        }

        HashSet<PickerDirection> current = [.. Enum.GetValues<PickerDirection>().Where(IsPickerDirection)];

        return !_loadedFavoriteDirections.SetEquals(current) ||
            !string.Equals(_loadedDirectShortcutKeys, DirectShortcutKeys.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private string? BuildApplicationCandidate(out ApplicationSettings? candidate)
    {
        candidate = null;

        if (_loadedApplicationSettings is null || _selected?.TargetKey is not string target)
        {
            return "The picker and shortcut settings are not available for this selection.";
        }

        if (!TryParseShortcutKeys(DirectShortcutKeys, out IReadOnlyList<char> keys, out string? problem))
        {
            return problem;
        }

        List<DirectShortcut> shortcuts =
        [
            .. _loadedApplicationSettings.DirectShortcuts.Where(shortcut => !SameTarget(shortcut.Target, target)),
        ];

        shortcuts.AddRange(keys.Select(key => new DirectShortcut(target, key, _selected.Name)));

        candidate = _loadedApplicationSettings with
        {
            Favorites = FavoriteSet.Create(_favoriteDraft.Values),
            DirectShortcuts = shortcuts,
        };

        return candidate.Validate(_targets);
    }

    private static bool TryParseShortcutKeys(string text, out IReadOnlyList<char> keys, out string? problem)
    {
        string[] parts = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<char>();

        foreach (string part in parts)
        {
            if (part.Length != 1 || !char.IsAsciiLetter(part[0]))
            {
                keys = [];
                problem = "Direct shortcuts must be single letters A to Z, separated by commas.";
                return false;
            }

            char key = char.ToLowerInvariant(part[0]);

            if (!parsed.Contains(key))
            {
                parsed.Add(key);
            }
        }

        keys = parsed;
        problem = null;
        return true;
    }

    private static string FormatShortcutKeys(IEnumerable<char> keys) =>
        string.Join(", ", keys.Select(char.ToUpperInvariant));

    private bool SameTarget(string reference, string target) =>
        string.Equals(_targets.Resolve(reference)?.Key ?? reference, target, StringComparison.OrdinalIgnoreCase);

    private static bool IsSibling(HierarchyNodeViewModel one, HierarchyNodeViewModel other) =>
        one.Level == other.Level &&
        string.Equals(one.Address.SpaceId, other.Address.SpaceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(one.Address.ActivityId, other.Address.ActivityId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Space, Activity and Source an address names, as far as it goes.</summary>
    private static (ConfiguredSpace Space, ConfiguredActivity? Activity, ConfiguredSource? Source)? Locate(
        IReadOnlyList<ConfiguredSpace> spaces, NodeAddress address)
    {
        ConfiguredSpace? space = spaces.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, address.SpaceId, StringComparison.OrdinalIgnoreCase));

        if (space is null)
        {
            return null;
        }

        ConfiguredActivity? activity = address.ActivityId is null
            ? null
            : space.Activities.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, address.ActivityId, StringComparison.OrdinalIgnoreCase));

        if (address.ActivityId is not null && activity is null)
        {
            return null;
        }

        ConfiguredSource? source = address.SourceId is null || activity is null
            ? null
            : activity.Sources.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, address.SourceId, StringComparison.OrdinalIgnoreCase));

        return address.SourceId is not null && source is null ? null : (space, activity, source);
    }

    /// <summary>What the addressed level itself says, and nothing it inherits.</summary>
    private static IReadOnlyList<ResourceAssignment> OwnResourcesOf(
        IReadOnlyList<ConfiguredSpace> spaces, NodeAddress address) =>
        Locate(spaces, address) switch
        {
            (_, _, ConfiguredSource source) => source.Resources,
            (_, ConfiguredActivity activity, _) => activity.Resources,
            (ConfiguredSpace space, _, _) => space.Resources,
            _ => [],
        };

    /// <summary>
    /// What the levels above the addressed one have already decided, and which
    /// of them decided it.
    /// </summary>
    private static Dictionary<string, (ConfiguredResource Resource, string From)> InheritedAt(
        IReadOnlyList<ConfiguredSpace> spaces, NodeAddress address, ConfiguredSpace space)
    {
        var inherited = new Dictionary<string, (ConfiguredResource, string)>(StringComparer.OrdinalIgnoreCase);

        if (address.ActivityId is null)
        {
            return inherited;
        }

        void Apply(IReadOnlyList<ResourceAssignment> assignments, string from)
        {
            foreach (ResourceAssignment assignment in assignments)
            {
                if (assignment.Clears)
                {
                    inherited.Remove(assignment.Key);
                    continue;
                }

                inherited[assignment.Key] =
                    (new ConfiguredResource(assignment.Key, assignment.Value!, assignment.Optional), from);
            }
        }

        Apply(space.Resources, space.Name);

        if (address.SourceId is not null &&
            Locate(spaces, address) is (_, ConfiguredActivity activity, _))
        {
            Apply(activity.Resources, activity.Name);
        }

        return inherited;
    }

    protected override void OnDirtyStateChanged()
    {
        base.OnDirtyStateChanged();
        OnPropertyChanged(nameof(CanActivateSelected));
    }

    protected override void OnBusyStateChanged()
    {
        base.OnBusyStateChanged();
        OnPropertyChanged(nameof(CanActivateSelected));
    }
}

using System.Collections.ObjectModel;
using System.Windows.Input;
using PCMode.Engine;
using PCMode.Tray.Elements;
using PCMode.Tray.Elements.Configuration;

namespace PCMode.Tray.Elements.Displays.Sections;

/// <summary>
/// The whole television feature, scoped to whichever physical monitor is
/// selected in <see cref="LayoutSectionViewModel"/>: which configured
/// television is connected as that display, its connection settings, its
/// named HDMI inputs and picture profiles, pairing, and live power.
/// </summary>
/// <remarks>
/// ONE TELEVISION AT A TIME, CHOSEN BY WHICH SCREEN IT IS
///
/// This replaces the standalone Television page, which was a collection
/// editor with its own selector over television names. The selector is gone
/// because the Displays canvas already answers the question it asked: a
/// television is not an abstract entry in a list, it is one of the screens
/// on this desk, and <see cref="LgTvSettings.DisplayMonitor"/> is what says
/// which. Everything the standalone page could do survives, addressed
/// through that link instead of through a name picked from a sidebar.
///
/// A television configured but not yet linked to any display is still
/// reachable: <see cref="LinkableTelevisions"/> offers them, so a document
/// written before this link existed is adopted rather than stranded.
///
/// TWO KINDS OF WRITE, DELIBERATELY DIFFERENT
///
/// The editor fields (host, MAC, inputs, profiles) are ordinary dirty-tracked
/// state, saved with the page's Save button through <see cref="Save"/> --
/// which is why this is an <see cref="IConfigurationSection"/> and not a bag
/// of live controls. Everything that changes WHICH television is being
/// edited, or how it is filed -- create, link, unlink, rename, duplicate,
/// delete, make default, enable/disable -- writes immediately and reloads,
/// exactly as the standalone page did, and the page guards those behind its
/// unsaved-changes prompt for the same reason it always did.
///
/// EVERY NETWORK OPERATION GOES THROUGH THE RUNNING ENGINE'S OWN PROVIDER
///
/// Pairing, status reads and power changes all ask <see cref="_livePowerProvider"/>
/// for the television rather than building a second one from configuration.
/// Two providers for one television would be two connections to one address,
/// and what serializes webOS operations is that a television's resources
/// share one. Reading configuration is the fallback for a page open before
/// the engine exists.
/// </remarks>
public sealed class MonitorTelevisionSectionViewModel : ObservableObject, IConfigurationSection
{
    private const string Category = "Television";

    private readonly IConfigurationWorkspace _workspace;
    private readonly Action _onChanged;
    private readonly Func<string?, TvPowerProvider?>? _livePowerProvider;

    /// <summary>
    /// Suppresses <see cref="_onChanged"/> while this section reloads itself
    /// after one of its own immediate writes. The page wraps its own calls to
    /// <see cref="Load"/> in <c>RunWithoutDirtyTracking</c>, but a reload that
    /// this section starts happens outside that, and re-populating the form
    /// from disk is not an edit.
    /// </summary>
    private bool _suppressChanges;

    private string? _devicePath;
    private string? _monitorName;
    private string? _televisionName;
    private string? _defaultTelevision;

    private bool _enabled;
    private string _host = string.Empty;
    private string _macAddress = string.Empty;
    private string _safeDisplayTarget = string.Empty;

    private TvPowerProvider? _observedProvider;
    private string? _powerActionText;
    private bool _isBusy;
    private string? _statusMessage;

    /// <param name="livePowerProvider">
    /// The running engine's own television, by name. Null when nothing is
    /// running -- a test, or a page built before the runtime exists.
    /// </param>
    /// <param name="prompts">
    /// How this section asks for a television's name and confirms unlinking or
    /// deleting one. A block is lookless and a Layout cannot open a dialog, so
    /// the section that owns the write owns the whole flow. Left null outside
    /// a hosted process (tests), where <see cref="NoConfigurationPrompts"/>
    /// refuses every prompt.
    /// </param>
    /// <param name="onLinkChanged">
    /// Called after a write that changes which television this display is, or
    /// how it is filed, so the sections beside this one can re-resolve.
    /// </param>
    public MonitorTelevisionSectionViewModel(
        IConfigurationWorkspace workspace,
        Action onChanged,
        Func<string?, TvPowerProvider?>? livePowerProvider = null,
        IConfigurationPrompts? prompts = null,
        Action? onLinkChanged = null,
        Func<bool>? guardUnsavedChanges = null
    )
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(onChanged);

        _workspace = workspace;
        _onChanged = onChanged;
        _livePowerProvider = livePowerProvider;
        _prompts = prompts ?? NoConfigurationPrompts.Instance;
        _onLinkChanged = onLinkChanged;
        _guardUnsavedChanges = guardUnsavedChanges;

        CreateCommand = new RelayCommand(PromptCreate, () => HasMonitor && !HasAssociation);
        LinkCommand = new RelayCommand(PromptLink, () => HasLinkableTelevisions && !HasAssociation);
        RenameCommand = new RelayCommand(PromptRename, () => HasAssociation);
        DuplicateCommand = new RelayCommand(PromptDuplicate, () => HasAssociation);
        UnlinkCommand = new RelayCommand(ConfirmUnlink, () => HasAssociation);
        DeleteCommand = new RelayCommand(ConfirmDelete, () => HasAssociation);
        ToggleEnabledCommand = new RelayCommand(() => Write(ToggleEnabled), () => HasAssociation);
        MakeDefaultCommand = new RelayCommand(() => Write(SetAsDefault), () => CanMakeDefault);
        PairCommand = new AsyncRelayCommand(async () => await PairAsync().ConfigureAwait(true), () => CanPair);
        PowerOnCommand = new AsyncRelayCommand(
            async () => await SetPowerAsync(TvPowerProvider.On).ConfigureAwait(true),
            () => CanUsePowerControl
        );
        PowerOffCommand = new AsyncRelayCommand(
            async () => await SetPowerAsync(TvPowerProvider.Off).ConfigureAwait(true),
            () => CanUsePowerControl
        );
        AddInputCommand = new RelayCommand(AddInput, () => HasAssociation);
        RemoveInputCommand = new RelayCommand<TelevisionInputRow>(RemoveInput);
        AddProfileCommand = new RelayCommand(AddProfile, () => HasAssociation);
        RemoveProfileCommand = new RelayCommand<TelevisionProfileRow>(RemoveProfile);

        PropertyChanged += (_, e) =>
        {
            if (
                e.PropertyName
                is nameof(HasAssociation)
                    or nameof(HasMonitor)
                    or nameof(HasLinkableTelevisions)
                    or nameof(CanPair)
                    or nameof(CanUsePowerControl)
                    or nameof(CanMakeDefault)
                    or nameof(IsBusy)
            )
            {
                RefreshCommands();
            }
        };
    }

    private readonly IConfigurationPrompts _prompts;
    private readonly Action? _onLinkChanged;

    /// <summary>
    /// The page's own "you have unsaved changes" guard, run before a write
    /// that changes which television this display is -- the same guard the
    /// page shell ran before these became commands. False means the action
    /// must not proceed.
    /// </summary>
    private readonly Func<bool>? _guardUnsavedChanges;

    /// <summary>Names a new television and links this display to it.</summary>
    public ICommand CreateCommand { get; }

    /// <summary>Links this display to a television PCMode already knows.</summary>
    public ICommand LinkCommand { get; }

    public ICommand RenameCommand { get; }

    public ICommand DuplicateCommand { get; }

    public ICommand UnlinkCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand ToggleEnabledCommand { get; }

    public ICommand MakeDefaultCommand { get; }

    public ICommand PairCommand { get; }

    public ICommand PowerOnCommand { get; }

    public ICommand PowerOffCommand { get; }

    public ICommand AddInputCommand { get; }

    public ICommand RemoveInputCommand { get; }

    public ICommand AddProfileCommand { get; }

    public ICommand RemoveProfileCommand { get; }

    private void PromptCreate()
    {
        if (!Guard())
        {
            return;
        }

        _prompts.PromptForName(
            "Add television",
            "Name the television this display is. Its address, inputs and picture profiles are configured here afterward.",
            SuggestedTelevisionName,
            name => Write(() => CreateTelevision(name))
        );
    }

    private void PromptLink()
    {
        if (LinkableTelevisions.Count == 0 || !Guard())
        {
            return;
        }

        _prompts.PromptForName(
            "Link existing television",
            "Name the configured television this display is: " + string.Join(", ", LinkableTelevisions) + ".",
            LinkableTelevisions[0],
            name => Write(() => LinkTelevision(name))
        );
    }

    private void PromptRename()
    {
        if (_televisionName is not string current || !Guard())
        {
            return;
        }

        _prompts.PromptForName(
            "Rename television",
            $"Rename '{current}'. PCMode refuses the change if it would leave the configuration invalid.",
            current,
            name => Write(() => RenameTelevision(name))
        );
    }

    private void PromptDuplicate()
    {
        if (_televisionName is not string current || !Guard())
        {
            return;
        }

        _prompts.PromptForName(
            "Duplicate television",
            $"Name the copy of '{current}'. Connection settings, inputs and picture profiles are copied; "
                + "the copy starts unlinked, so link it to whichever display it actually is.",
            $"{current} copy",
            name => Write(() => DuplicateTelevision(name))
        );
    }

    private void ConfirmUnlink()
    {
        if (!Guard())
        {
            return;
        }

        if (
            _televisionName is string current
            && _prompts.Confirm($"Unlink '{current}' from this display? It stays configured and can be linked again.")
        )
        {
            Write(UnlinkTelevision);
        }
    }

    private void ConfirmDelete()
    {
        if (!Guard())
        {
            return;
        }

        if (
            _televisionName is string current
            && _prompts.Confirm(
                $"Remove '{current}' from PCMode? The television itself and its pairing credentials are not changed."
            )
        )
        {
            Write(DeleteTelevision);
        }
    }

    /// <summary>Runs a write that changes which television this display is, or how it is filed, and lets the page pick it up.</summary>
    private string? Write(Func<string?> write)
    {
        string? problem = write();

        if (problem is null)
        {
            _onLinkChanged?.Invoke();
        }

        return problem;
    }

    private bool Guard() => _guardUnsavedChanges?.Invoke() ?? true;

    private void RefreshCommands()
    {
        (CreateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (LinkCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RenameCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DuplicateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UnlinkCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleEnabledCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MakeDefaultCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PairCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PowerOnCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PowerOffCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AddInputCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AddProfileCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public ObservableCollection<TelevisionInputRow> Inputs { get; } = [];

    public ObservableCollection<TelevisionProfileRow> Profiles { get; } = [];

    /// <summary>Configured televisions that no display claims yet, offered for adoption.</summary>
    public ObservableCollection<string> LinkableTelevisions { get; } = [];

    /// <summary>Whether there is anything for "Link existing" to offer.</summary>
    public bool HasLinkableTelevisions => LinkableTelevisions.Count > 0;

    /// <summary>Whether a monitor is selected at all and Windows currently reports a device path for it.</summary>
    public bool HasMonitor => _devicePath is not null;

    /// <summary>Whether a configured television names the selected monitor's device path.</summary>
    public bool HasAssociation => _televisionName is not null;

    public string? TelevisionName => _televisionName;

    /// <summary>A suggested name for a television created for this monitor: the monitor's own name.</summary>
    public string SuggestedTelevisionName => _monitorName ?? string.Empty;

    public bool IsDefaultTelevision =>
        _televisionName is not null
        && _defaultTelevision is not null
        && string.Equals(_televisionName, _defaultTelevision, StringComparison.OrdinalIgnoreCase);

    public bool CanMakeDefault => HasAssociation && !IsDefaultTelevision && !IsBusy;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(PowerStateText));
                ReportChanged();
            }
        }
    }

    public string Host
    {
        get => _host;
        set
        {
            if (SetProperty(ref _host, value ?? string.Empty))
            {
                ReportChanged();
            }
        }
    }

    public string MacAddress
    {
        get => _macAddress;
        set
        {
            if (SetProperty(ref _macAddress, value ?? string.Empty))
            {
                ReportChanged();
            }
        }
    }

    /// <summary>The display layout to return to before this television is switched off.</summary>
    public string SafeDisplayTarget
    {
        get => _safeDisplayTarget;
        set
        {
            if (SetProperty(ref _safeDisplayTarget, value ?? string.Empty))
            {
                ReportChanged();
            }
        }
    }

    public bool HasMultipleInputs => Inputs.Count > 1;

    public string PowerStateText =>
        _powerActionText
        ?? DescribePowerState(
            _enabled
                ? _observedProvider?.ObservedState.Power ?? LgTvObservation.NotObserved
                : new LgTvObservation(LgTvObservationStatus.Disabled)
        );

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanUsePowerControl));
                OnPropertyChanged(nameof(CanPair));
                OnPropertyChanged(nameof(CanMakeDefault));
            }
        }
    }

    public bool CanUsePowerControl => HasAssociation && _enabled && !IsBusy;

    public bool CanPair => HasAssociation && _enabled && !IsBusy;

    /// <summary>
    /// The same rules the standalone page enforced, unchanged: what a
    /// television that says it is switched on must supply, and what makes an
    /// input or profile well formed.
    /// </summary>
    public string? ValidationProblem
    {
        get
        {
            if (!HasAssociation || !_enabled)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(_host))
            {
                return "Give the television's address on your network, or turn it off.";
            }

            if (string.IsNullOrWhiteSpace(_macAddress))
            {
                return "Give the television's MAC address -- switching it on needs it -- or turn it off.";
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (TelevisionInputRow row in Inputs)
            {
                if (string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Input))
                {
                    return "Every input needs both a name and a socket. Remove an empty row instead of leaving it blank.";
                }

                if (!names.Add(row.Name.Trim()))
                {
                    return $"'{row.Name}' is used for more than one input.";
                }
            }

            var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (TelevisionProfileRow profile in Profiles)
            {
                if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.PictureMode))
                {
                    return "Every profile needs both a name and a picture mode.";
                }

                if (!profileNames.Add(profile.Name.Trim()))
                {
                    return $"'{profile.Name}' is used for more than one profile.";
                }

                // Only a profile that actually sets something per HDMI socket
                // needs to say which input it is for.
                bool needsInput = profile.Signal.Any(setting => LgTvProfile.SignalUsesHdmi(setting.Key));

                if (needsInput)
                {
                    if (Inputs.Count == 0)
                    {
                        return $"'{profile.Name}' needs an input before using HDMI-specific advanced options.";
                    }

                    if (Inputs.Count > 1)
                    {
                        if (string.IsNullOrWhiteSpace(profile.Input))
                        {
                            return $"Choose which input '{profile.Name}' targets for its HDMI-specific advanced options.";
                        }

                        if (
                            !Inputs.Any(input =>
                                string.Equals(
                                    input.Name.Trim(),
                                    profile.Input.Trim(),
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                        )
                        {
                            return $"'{profile.Name}' targets '{profile.Input}', which is not one of the configured inputs.";
                        }
                    }
                }

                if (
                    profile
                        .Signal.Concat(profile.Settings)
                        .Any(row => string.IsNullOrWhiteSpace(row.Key) || string.IsNullOrWhiteSpace(row.Value))
                )
                {
                    return $"'{profile.Name}' has an advanced setting with no name or value. Remove it instead of leaving it blank.";
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Resolves which television (if any) names the selected monitor's device
    /// path and loads its saved body, replacing whatever was in the editor.
    /// Always reloads -- which is what makes the page's Discard reach the
    /// television editor too. See <see cref="ReResolve"/> for the
    /// ambient-tick version that does not.
    /// </summary>
    public void Load(object? entityKey)
    {
        LayoutMonitorRow? monitor = entityKey as LayoutMonitorRow;

        _devicePath = monitor?.DevicePath;
        _monitorName = monitor?.MonitorName;

        LgTvCatalog catalog = ReadCatalog();
        _televisionName = ResolveTelevision(catalog, _devicePath);

        LoadFrom(catalog);
        SelectObservedProvider();
        _powerActionText = null;
        OnPropertyChanged(nameof(PowerStateText));
        StatusMessage = null;
    }

    /// <summary>
    /// Re-resolves the association on an ambient tick, reloading only if the
    /// answer actually changed.
    /// </summary>
    /// <remarks>
    /// A television that was switched off when the page opened appears as a
    /// detected monitor once it wakes, so the resolution genuinely has to be
    /// redone -- but a tick that resolves the same television must not
    /// overwrite an edit somebody is part way through typing, which a full
    /// <see cref="Load"/> would.
    /// </remarks>
    public void ReResolve(LayoutMonitorRow? monitor)
    {
        string? devicePath = monitor?.DevicePath;
        LgTvCatalog catalog = ReadCatalog();

        if (
            string.Equals(devicePath, _devicePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                ResolveTelevision(catalog, devicePath),
                _televisionName,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            RefreshCatalogState(catalog);
            return;
        }

        Load(monitor);
    }

    /// <summary>Writes the edited body of the linked television, if there is one.</summary>
    public string? Save()
    {
        if (_televisionName is not { } name)
        {
            return null;
        }

        if (ValidationProblem is string problem)
        {
            return problem;
        }

        ModeSaveOutcome outcome = LgTvConfigurationStore.Save(_workspace.Root, name, BuildEdit());

        return outcome.Success ? null : outcome.Problem ?? "The television could not be saved.";
    }

    // ------------------------------------------------------------
    // Which television this display is
    // ------------------------------------------------------------

    /// <summary>Creates a disabled blank television already linked to this display.</summary>
    public string? CreateTelevision(string name)
    {
        if (_devicePath is not { } devicePath)
        {
            return "Windows is not currently reporting a device path for this monitor.";
        }

        string trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return "Give the television a name.";
        }

        var edit = new LgTvEdit(
            Enabled: false,
            Host: string.Empty,
            MacAddress: string.Empty,
            SafeDisplayTarget: null,
            Inputs: [],
            Profiles: [],
            DisplayMonitor: devicePath
        );

        LgTvCatalog before = ReadCatalog();

        ModeSaveOutcome outcome = LgTvConfigurationStore.Create(
            _workspace.Root,
            trimmed,
            edit,
            makeDefault: before.Names.Count == 0
        );

        if (!outcome.Success)
        {
            return Warn(outcome.Problem ?? "That television could not be created.");
        }

        return Adopt(trimmed, $"'{trimmed}' created for this display.");
    }

    /// <summary>Points an already-configured television at this display.</summary>
    public string? LinkTelevision(string name)
    {
        if (_devicePath is not { } devicePath)
        {
            return "Windows is not currently reporting a device path for this monitor.";
        }

        string trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return "Choose a television to link.";
        }

        LgTvCatalog catalog = ReadCatalog();

        if (catalog.SettingsFor(trimmed) is not { } settings)
        {
            return Warn($"'{trimmed}' is not a configured television.");
        }

        ModeSaveOutcome outcome = LgTvConfigurationStore.Save(
            _workspace.Root,
            trimmed,
            EditFrom(settings) with
            {
                DisplayMonitor = devicePath,
            }
        );

        if (!outcome.Success)
        {
            return Warn(outcome.Problem ?? "That television could not be linked.");
        }

        return Adopt(trimmed, $"'{trimmed}' linked to this display.");
    }

    /// <summary>
    /// Stops this display claiming the television, without deleting it. It
    /// stays configured and can be linked to another display, or to this one
    /// again.
    /// </summary>
    public string? UnlinkTelevision()
    {
        if (_televisionName is not { } name)
        {
            return "This display is not linked to a television.";
        }

        LgTvCatalog catalog = ReadCatalog();

        if (catalog.SettingsFor(name) is not { } settings)
        {
            return Warn($"'{name}' is not a configured television.");
        }

        ModeSaveOutcome outcome = LgTvConfigurationStore.Save(
            _workspace.Root,
            name,
            EditFrom(settings) with
            {
                DisplayMonitor = null,
            }
        );

        if (!outcome.Success)
        {
            return Warn(outcome.Problem ?? "That television could not be unlinked.");
        }

        return Adopt(null, $"'{name}' unlinked. It is still configured.");
    }

    public string? RenameTelevision(string newName)
    {
        if (_televisionName is not { } oldName)
        {
            return "This display is not linked to a television.";
        }

        string trimmed = newName?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return "Give the television a name.";
        }

        if (string.Equals(oldName, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ModeSaveOutcome outcome = LgTvConfigurationStore.Rename(_workspace.Root, oldName, trimmed);

        if (!outcome.Success)
        {
            return Warn(outcome.Problem ?? "That television could not be renamed.");
        }

        return Adopt(trimmed, $"'{oldName}' renamed to '{trimmed}'.");
    }

    /// <summary>
    /// Copies this television under a new name, leaving the copy unlinked.
    /// </summary>
    /// <remarks>
    /// Unlinked deliberately: two televisions both claiming one display would
    /// make <see cref="ResolveTelevision"/> pick whichever the document
    /// happened to list first, which is not an answer anybody chose. The copy
    /// is adopted by whichever display it is linked to next.
    /// </remarks>
    public string? DuplicateTelevision(string newName)
    {
        if (_televisionName is not { } source)
        {
            return "This display is not linked to a television.";
        }

        string trimmed = newName?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return "Give the television a name.";
        }

        ModeSaveOutcome duplicated = LgTvConfigurationStore.Duplicate(_workspace.Root, source, trimmed);

        if (!duplicated.Success)
        {
            return Warn(duplicated.Problem ?? "That television could not be duplicated.");
        }

        LgTvCatalog catalog = ReadCatalog();

        if (catalog.SettingsFor(trimmed) is { } copy)
        {
            ModeSaveOutcome unlinked = LgTvConfigurationStore.Save(
                _workspace.Root,
                trimmed,
                EditFrom(copy) with
                {
                    DisplayMonitor = null,
                }
            );

            if (!unlinked.Success)
            {
                return Warn(unlinked.Problem ?? "The copy could not be unlinked from this display.");
            }
        }

        return Adopt(source, $"'{trimmed}' created from '{source}'. Link it to a display to configure it.");
    }

    public string? DeleteTelevision()
    {
        if (_televisionName is not { } name)
        {
            return "This display is not linked to a television.";
        }

        ModeSaveOutcome outcome = LgTvConfigurationStore.Delete(_workspace.Root, name);

        if (!outcome.Success)
        {
            return Warn(outcome.Problem ?? "That television could not be removed.");
        }

        return Adopt(null, $"'{name}' removed.");
    }

    /// <summary>Makes this television the one the historical tvPower/tvInput/... keys mean.</summary>
    public string? SetAsDefault()
    {
        if (_televisionName is not { } name)
        {
            return "This display is not linked to a television.";
        }

        if (IsDefaultTelevision)
        {
            return null;
        }

        ModeSaveOutcome outcome = LgTvConfigurationStore.SetDefault(_workspace.Root, name);

        if (!outcome.Success)
        {
            return Warn(outcome.Problem ?? "The default television could not be changed.");
        }

        return Adopt(name, $"'{name}' is now the default television.");
    }

    /// <summary>
    /// Enables or disables this television, saving whatever else is currently
    /// in the editor along with it -- the same commit-and-reload the
    /// standalone page's quick action did.
    /// </summary>
    public string? ToggleEnabled()
    {
        if (_televisionName is not { } name)
        {
            return "This display is not linked to a television.";
        }

        bool enabled = !_enabled;

        ModeSaveOutcome outcome = LgTvConfigurationStore.Save(_workspace.Root, name, BuildEdit(enabled));

        if (!outcome.Success)
        {
            return Warn(
                outcome.Problem
                    ?? (enabled ? "That television could not be enabled." : "That television could not be disabled.")
            );
        }

        return Adopt(name, enabled ? $"'{name}' enabled." : $"'{name}' disabled.");
    }

    // ------------------------------------------------------------
    // Inputs and profiles
    // ------------------------------------------------------------

    public void AddInput()
    {
        Inputs.Add(new TelevisionInputRow(string.Empty, string.Empty, OnRowChanged));
        OnPropertyChanged(nameof(HasMultipleInputs));
        OnRowChanged();
    }

    public void RemoveInput(TelevisionInputRow row)
    {
        if (Inputs.Remove(row))
        {
            OnPropertyChanged(nameof(HasMultipleInputs));
            OnRowChanged();
        }
    }

    public void AddProfile()
    {
        Profiles.Add(new TelevisionProfileRow(string.Empty, "standard", string.Empty, CurrentInputNames, OnRowChanged));
        OnRowChanged();
    }

    public void RemoveProfile(TelevisionProfileRow row)
    {
        if (Profiles.Remove(row))
        {
            OnRowChanged();
        }
    }

    // ------------------------------------------------------------
    // The network
    // ------------------------------------------------------------

    /// <summary>
    /// Opens a pairing prompt on this television and waits for somebody to
    /// accept it there.
    /// </summary>
    /// <remarks>
    /// Off the calling thread and awaited, because the wait is a person
    /// finding a remote control and is bounded by a minute. Done inline it
    /// would freeze the window for that minute -- including the very message
    /// telling them to go and accept the prompt.
    /// </remarks>
    public async Task<string?> PairAsync()
    {
        if (_televisionName is not { } name)
        {
            return "This display is not linked to a television.";
        }

        (TvPowerProvider? provider, string? problem) = LoadProvider(name);

        if (provider is null)
        {
            return Warn(problem ?? "The configuration could not be read.");
        }

        if (!provider.Settings.Enabled)
        {
            return Warn("Save this television's address and MAC address first.");
        }

        LgTvConnection connection = provider.Connection;

        IsBusy = true;
        StatusMessage = $"Waiting for the pairing prompt to be accepted on '{name}'...";

        try
        {
            (bool paired, string? pairingProblem) = await Task.Run(() =>
                {
                    bool ok = connection.TryPair(out string? failure);
                    return (ok, failure);
                })
                .ConfigureAwait(true);

            if (!paired)
            {
                return Warn(pairingProblem ?? "Pairing failed.");
            }

            StatusMessage = $"'{name}' paired.";
            _workspace.Note(Category, $"Paired with '{name}'.");
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Reads live power state for the linked television, if any.</summary>
    public async Task RefreshLiveStatusAsync()
    {
        if (_televisionName is not { } name)
        {
            return;
        }

        if (!_enabled)
        {
            _observedProvider = null;
            OnPropertyChanged(nameof(PowerStateText));
            return;
        }

        (TvPowerProvider? provider, _) = LoadProvider(name);

        if (provider is null)
        {
            _observedProvider = null;
            _powerActionText = "This television is not active in the current configuration.";
            OnPropertyChanged(nameof(PowerStateText));
            return;
        }

        _observedProvider = provider;
        _powerActionText = null;

        try
        {
            _ = await Task.Run(provider.Read).ConfigureAwait(true);
        }
        catch
        {
            // The provider records the error in its shared observation snapshot.
        }

        if (!string.Equals(_televisionName, name, StringComparison.OrdinalIgnoreCase))
        {
            // The selection moved on while this was in flight.
            return;
        }

        OnPropertyChanged(nameof(PowerStateText));
    }

    /// <summary>Switches the linked television on or off now.</summary>
    public async Task<string?> SetPowerAsync(string value)
    {
        if (_televisionName is not { } name)
        {
            return "This display is not linked to a television.";
        }

        (TvPowerProvider? provider, string? problem) = LoadProvider(name);

        if (provider is null)
        {
            return Warn(problem ?? "This television is not active in the current configuration.");
        }

        IsBusy = true;
        _powerActionText = string.Equals(value, TvPowerProvider.On, StringComparison.OrdinalIgnoreCase)
            ? "Switching on..."
            : "Switching off...";
        OnPropertyChanged(nameof(PowerStateText));

        try
        {
            await Task.Run(() => provider.Apply(value, CancellationToken.None)).ConfigureAwait(true);
            await RefreshLiveStatusAsync().ConfigureAwait(true);
            _workspace.Note(Category, $"'{name}' switched {value.ToLowerInvariant()}.");
            return null;
        }
        catch (Exception exception)
        {
            _powerActionText = $"Could not change power state. {exception.Message}";
            OnPropertyChanged(nameof(PowerStateText));
            return Warn(_powerActionText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------
    // Loading
    // ------------------------------------------------------------

    /// <summary>Reloads after one of this section's own immediate writes, and reports the result.</summary>
    private string? Adopt(string? televisionName, string message)
    {
        _televisionName = televisionName;
        LoadFrom(ReadCatalog());
        StatusMessage = _workspace.Apply(message);
        SelectObservedProvider();
        _powerActionText = null;
        OnPropertyChanged(nameof(PowerStateText));
        return null;
    }

    private void LoadFrom(LgTvCatalog catalog)
    {
        _defaultTelevision = catalog.DefaultName;

        LgTvSettings? settings = _televisionName is null ? null : catalog.SettingsFor(_televisionName);

        _suppressChanges = true;

        try
        {
            Enabled = settings?.Enabled ?? false;
            Host = settings?.Host ?? string.Empty;
            MacAddress = settings?.MacAddress ?? string.Empty;
            SafeDisplayTarget = settings?.SafeDisplayTarget ?? string.Empty;

            Inputs.Clear();

            foreach (string inputName in settings?.InputNames ?? [])
            {
                Inputs.Add(
                    new TelevisionInputRow(inputName, settings!.InputIdFor(inputName) ?? string.Empty, OnRowChanged)
                );
            }

            Profiles.Clear();

            foreach (string profileName in settings?.ProfileNames ?? [])
            {
                LgTvProfile profile = settings!.ProfileFor(profileName)!;

                // Shown only with more than one input: on the ordinary machine
                // the resolved input is implied and would be a field nobody
                // needed to look at.
                string input = Inputs.Count > 1 ? profile.InputName ?? string.Empty : string.Empty;

                var row = new TelevisionProfileRow(
                    profileName,
                    profile.PictureMode,
                    input,
                    CurrentInputNames,
                    OnRowChanged
                );

                foreach (LgTvSetting setting in profile.Signal)
                {
                    string editableKey = LgTvProfile.WrittenSignalName(setting.Key) ?? setting.Key;
                    row.Signal.Add(new TelevisionSettingRow(editableKey, setting.Value, OnRowChanged));
                }

                foreach (LgTvSetting setting in profile.Settings)
                {
                    row.Settings.Add(new TelevisionSettingRow(setting.Key, setting.Value, OnRowChanged));
                }

                Profiles.Add(row);
            }

            RebuildLinkableTelevisions(catalog);
        }
        finally
        {
            _suppressChanges = false;
        }

        RaiseAssociationState();
    }

    /// <summary>
    /// Refreshes catalog-wide metadata without replacing fields that may be
    /// mid-edit in this card.
    /// </summary>
    private void RefreshCatalogState(LgTvCatalog catalog)
    {
        _defaultTelevision = catalog.DefaultName;
        RebuildLinkableTelevisions(catalog);
        OnPropertyChanged(nameof(IsDefaultTelevision));
        OnPropertyChanged(nameof(CanMakeDefault));
        OnPropertyChanged(nameof(HasLinkableTelevisions));
    }

    private void RebuildLinkableTelevisions(LgTvCatalog catalog)
    {
        LinkableTelevisions.Clear();

        foreach (string name in catalog.Names)
        {
            if (string.Equals(name, _televisionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Only televisions no display has claimed: linking one that
            // another screen already owns would silently move it.
            if (catalog.SettingsFor(name)?.DisplayMonitor is null)
            {
                LinkableTelevisions.Add(name);
            }
        }
    }

    private LgTvCatalog ReadCatalog()
    {
        try
        {
            return LgTvConfigurationStore.Read(_workspace.Root);
        }
        catch (ModeConfigurationException exception)
        {
            _workspace.Fault(Category, "The televisions could not be read.", exception);
            return LgTvCatalog.Empty;
        }
    }

    private static string? ResolveTelevision(LgTvCatalog catalog, string? devicePath)
    {
        if (devicePath is null)
        {
            return null;
        }

        foreach (string name in catalog.Names)
        {
            if (
                catalog.SettingsFor(name)?.DisplayMonitor is { } bound
                && string.Equals(bound, devicePath, StringComparison.OrdinalIgnoreCase)
            )
            {
                return name;
            }
        }

        return null;
    }

    private LgTvEdit BuildEdit(bool? enabledOverride = null) =>
        new(
            enabledOverride ?? Enabled,
            Host.Trim(),
            MacAddress.Trim(),
            string.IsNullOrWhiteSpace(SafeDisplayTarget) ? null : SafeDisplayTarget.Trim(),
            [.. Inputs.Select(row => new LgTvInputEdit(row.Name.Trim(), row.Input.Trim()))],
            [
                .. Profiles.Select(profile => new LgTvProfileEdit(
                    profile.Name.Trim(),
                    profile.PictureMode.Trim(),
                    string.IsNullOrWhiteSpace(profile.Input) ? null : profile.Input.Trim(),
                    [.. profile.Signal.Select(row => new LgTvSettingEdit(row.Key.Trim(), row.Value.Trim()))],
                    [.. profile.Settings.Select(row => new LgTvSettingEdit(row.Key.Trim(), row.Value.Trim()))]
                )),
            ],
            _devicePath
        );

    /// <summary>Round-trips a saved television's own body, for edits that change only its filing.</summary>
    private static LgTvEdit EditFrom(LgTvSettings settings) =>
        new(
            settings.Enabled,
            settings.Host,
            settings.MacAddress,
            settings.SafeDisplayTarget,
            [.. settings.InputNames.Select(name => new LgTvInputEdit(name, settings.InputIdFor(name) ?? string.Empty))],
            [
                .. settings.ProfileNames.Select(name =>
                {
                    LgTvProfile profile = settings.ProfileFor(name)!;

                    return new LgTvProfileEdit(
                        name,
                        profile.PictureMode,
                        profile.InputName,
                        [
                            .. profile.Signal.Select(setting => new LgTvSettingEdit(
                                LgTvProfile.WrittenSignalName(setting.Key) ?? setting.Key,
                                setting.Value
                            )),
                        ],
                        [.. profile.Settings.Select(setting => new LgTvSettingEdit(setting.Key, setting.Value))]
                    );
                }),
            ],
            settings.DisplayMonitor
        );

    /// <summary>The television's currently configured input names, live.</summary>
    private IReadOnlyList<string> CurrentInputNames() => [.. Inputs.Select(row => row.Name)];

    /// <summary>
    /// The provider that owns this television's connection: the running
    /// engine's whenever there is one, so everything done over the network
    /// shares the gate every transition shares.
    /// </summary>
    private (TvPowerProvider? Provider, string? Problem) LoadProvider(string televisionName)
    {
        if (_livePowerProvider?.Invoke(televisionName) is TvPowerProvider live)
        {
            return (live, null);
        }

        (IReadOnlyList<TvPowerProvider> providers, string? problem) = _workspace.ReadProviders<TvPowerProvider>();

        if (problem is not null)
        {
            return (null, problem);
        }

        TvPowerProvider? named = providers.FirstOrDefault(provider =>
            string.Equals(provider.TelevisionName, televisionName, StringComparison.OrdinalIgnoreCase)
        );

        return named is not null
            ? (named, null)
            : (null, $"Television '{televisionName}' is not active in the current configuration.");
    }

    private void SelectObservedProvider()
    {
        _observedProvider = _televisionName is { } name && _enabled ? LoadProvider(name).Provider : null;
    }

    internal static string DescribePowerState(LgTvObservation observation) =>
        observation.Status switch
        {
            LgTvObservationStatus.Observed
                when string.Equals(observation.Value, TvPowerProvider.On, StringComparison.OrdinalIgnoreCase) => "On.",
            LgTvObservationStatus.Observed
                when string.Equals(observation.Value, TvPowerProvider.Off, StringComparison.OrdinalIgnoreCase) =>
                "Off.",
            LgTvObservationStatus.Disabled => "This television is disabled.",
            LgTvObservationStatus.Unpaired => "Not paired. Use Pair.",
            LgTvObservationStatus.Unreachable => WithProblem("Unreachable.", observation.Problem),
            LgTvObservationStatus.Unknown => WithProblem("Power state unknown.", observation.Problem),
            LgTvObservationStatus.Error => WithProblem("Could not read power state.", observation.Problem),
            _ => "Not checked yet.",
        };

    private static string WithProblem(string message, string? problem) =>
        string.IsNullOrWhiteSpace(problem) ? message : $"{message} {problem}";

    private void OnRowChanged()
    {
        // Broad rather than targeted: an input's name can change from any row
        // here, and every profile's input picker needs the current list.
        foreach (TelevisionProfileRow profile in Profiles)
        {
            profile.NotifyInputsChanged();
        }

        ReportChanged();
    }

    private void ReportChanged()
    {
        if (_suppressChanges)
        {
            return;
        }

        _onChanged();
    }

    private void RaiseAssociationState()
    {
        OnPropertyChanged(nameof(HasMonitor));
        OnPropertyChanged(nameof(HasAssociation));
        OnPropertyChanged(nameof(TelevisionName));
        OnPropertyChanged(nameof(SuggestedTelevisionName));
        OnPropertyChanged(nameof(IsDefaultTelevision));
        OnPropertyChanged(nameof(CanMakeDefault));
        OnPropertyChanged(nameof(CanUsePowerControl));
        OnPropertyChanged(nameof(CanPair));
        OnPropertyChanged(nameof(HasMultipleInputs));
        OnPropertyChanged(nameof(HasLinkableTelevisions));
    }

    private string Warn(string message)
    {
        StatusMessage = message;
        _workspace.Warn(Category, message);
        return message;
    }
}

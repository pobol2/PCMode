using System.Collections.ObjectModel;
using PCMode.Engine;
using PCMode.Tray.Elements.Configuration;
using PCMode.Tray.Elements;

namespace PCMode.Tray.Elements.Rgb;

/// <summary>One addressable LED inside a scene controller row.</summary>
public sealed class CaseRgbLedRowViewModel : ObservableEditableRow
{
    private string _color;

    public CaseRgbLedRowViewModel(int index, string name, string color, Action onChanged)
        : base(onChanged)
    {
        Index = index;
        Name = name;
        _color = color;
    }

    public int Index { get; }
    public string Name { get; }

    public string Color
    {
        get => _color;
        set
        {
            if (SetProperty(ref _color, (value ?? string.Empty).ToUpperInvariant()))
            {
                NotifyChanged();
            }
        }
    }
}

/// <summary>One configured OpenRGB controller as edited inside one named scene.</summary>
public sealed class CaseRgbSceneDeviceRowViewModel : ObservableEditableRow
{
    private readonly CaseRgbDeviceState _resolutionState;
    private readonly string _resolutionDetail;
    private readonly IReadOnlyList<string> _availableModes;
    private readonly IReadOnlySet<string> _perLedModes;
    private string _mode;
    private string _color;
    private bool _usePerLed;

    public CaseRgbSceneDeviceRowViewModel(
        int index,
        string controllerName,
        CaseRgbDeviceState resolutionState,
        string resolutionDetail,
        IReadOnlyList<string> availableModes,
        IReadOnlyList<OpenRgbLedInfo> availableLeds,
        IReadOnlySet<string> perLedModes,
        string mode,
        string color,
        IReadOnlyList<string>? ledColors,
        Action onChanged)
        : base(onChanged)
    {
        Index = index;
        ControllerName = controllerName;
        _resolutionState = resolutionState;
        _resolutionDetail = resolutionDetail;
        _availableModes = availableModes;
        _perLedModes = perLedModes;
        Modes = MergeMode(availableModes, mode);
        _mode = mode;
        _color = color;
        _usePerLed = ledColors is { Count: > 0 };

        // A controller that cannot be queried right now (SDK offline, machine
        // without the device connected) still needs one row per saved LED so a
        // scene that already has per-LED colours does not lose them here.
        IReadOnlyList<OpenRgbLedInfo> leds = availableLeds.Count > 0
            ? availableLeds
            : ledColors is { Count: > 0 }
                ? [.. Enumerable.Range(0, ledColors.Count).Select(i => new OpenRgbLedInfo(i, $"LED {i + 1}"))]
                : [];

        Leds = new ObservableCollection<CaseRgbLedRowViewModel>(
            leds.Select((led, position) => new CaseRgbLedRowViewModel(
                led.Index,
                led.Name,
                ledColors is { Count: > 0 } && position < ledColors.Count ? ledColors[position] : color,
                OnLedChanged)));
    }

    public int Index { get; }
    public string ControllerName { get; }
    public IReadOnlyList<string> Modes { get; }
    public ObservableCollection<CaseRgbLedRowViewModel> Leds { get; }

    public bool HasLeds => Leds.Count > 0;

    public bool SupportsPerLed => HasLeds && _perLedModes.Contains(Mode.Trim());

    public bool CanConfigurePerLed => HasLeds && (_resolutionState != CaseRgbDeviceState.Ready || SupportsPerLed);

    /// <summary>Whether this controller's colour is set LED-by-LED instead of as one colour.</summary>
    public bool UsePerLed
    {
        get => _usePerLed;
        set
        {
            if (SetProperty(ref _usePerLed, value))
            {
                OnPropertyChanged(nameof(PerLedHint));
                NotifyChanged();
            }
        }
    }

    /// <summary>The per-LED colours to save, or null when this controller uses one colour.</summary>
    public IReadOnlyList<string>? LedColors =>
        UsePerLed ? [.. Leds.Select(led => led.Color.Trim().ToUpperInvariant())] : null;

    public string PerLedHint
    {
        get
        {
            if (!HasLeds)
            {
                return "OpenRGB exposes no LEDs for this controller.";
            }

            if (_resolutionState == CaseRgbDeviceState.Ready && !SupportsPerLed)
            {
                return $"'{Mode.Trim()}' does not expose individual LED colours. Choose a per-LED mode such as Direct when available.";
            }

            return $"{Leds.Count} LED(s) available. LED order follows OpenRGB.";
        }
    }

    public string Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsReady));
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateDetail));
                OnPropertyChanged(nameof(SupportsPerLed));
                OnPropertyChanged(nameof(CanConfigurePerLed));
                OnPropertyChanged(nameof(PerLedHint));

                if (_resolutionState == CaseRgbDeviceState.Ready && _usePerLed && !SupportsPerLed)
                {
                    UsePerLed = false;
                }

                NotifyChanged();
            }
        }
    }

    public string Color
    {
        get => _color;
        set
        {
            if (SetProperty(ref _color, (value ?? string.Empty).ToUpperInvariant()))
            {
                NotifyChanged();
            }
        }
    }

    public bool IsReady =>
        _resolutionState == CaseRgbDeviceState.Ready &&
        _availableModes.Any(candidate => string.Equals(candidate, Mode.Trim(), StringComparison.OrdinalIgnoreCase));

    public string StateText
    {
        get
        {
            if (_resolutionState != CaseRgbDeviceState.Ready)
            {
                return _resolutionState.ToString();
            }

            return IsReady ? "Ready" : "Mode unavailable";
        }
    }

    public string StateDetail
    {
        get
        {
            if (_resolutionState != CaseRgbDeviceState.Ready)
            {
                return _resolutionDetail;
            }

            if (!IsReady)
            {
                return $"'{Mode.Trim()}' is not exposed by this OpenRGB controller.";
            }

            return _resolutionDetail;
        }
    }

    private void OnLedChanged()
    {
        OnPropertyChanged(nameof(LedColors));
        NotifyChanged();
    }

    private static IReadOnlyList<string> MergeMode(IReadOnlyList<string> modes, string selected)
    {
        var merged = new List<string>();

        foreach (string mode in modes)
        {
            if (!merged.Contains(mode, StringComparer.OrdinalIgnoreCase))
            {
                merged.Add(mode);
            }
        }

        if (!merged.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            merged.Add(selected);
        }

        return merged;
    }
}

/// <summary>One named lighting scene being edited.</summary>
public sealed class CaseRgbSceneEditorViewModel : ObservableEditableRow
{
    private string _name;

    public CaseRgbSceneEditorViewModel(
        string name,
        IEnumerable<CaseRgbSceneDeviceRowViewModel> devices,
        Action onChanged)
        : base(onChanged)
    {
        _name = name;
        Devices = new ObservableCollection<CaseRgbSceneDeviceRowViewModel>(devices);
    }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value ?? string.Empty);
    }

    public ObservableCollection<CaseRgbSceneDeviceRowViewModel> Devices { get; }
}

/// <summary>
/// Edits the named OpenRGB scenes understood by <see cref="CaseRgbProvider"/>.
/// Controller identities stay part of the engine configuration; only mode and
/// colour per scene are edited here.
/// </summary>
public sealed class RgbViewModel : ConfigurationPageViewModelBase
{
    private const string Category = "Rgb";

    private readonly ConfigurationEditingSession<string, CaseRgbScene> _session;
    private readonly Dictionary<int, ControllerSnapshot> _controllers = [];
    private IReadOnlyList<CaseRgbConfiguredDevice> _configuredDevices = [];
    private string _legacyExecutable = string.Empty;
    private int _withinMs = 4000;
    private string _currentLightingText = "Current lighting: unknown";
    private string? _activeSceneName;
    private CaseRgbSceneEditorViewModel? _selectedScene;
    private readonly ObservableCollection<CaseRgbSceneDeviceRowViewModel> _emptySceneDevices = [];

    public RgbViewModel(
        IConfigurationWorkspace workspace,
        IConfigurationPrompts? prompts = null)
        : base(workspace, prompts)
    {
        _session = new ConfigurationEditingSession<string, CaseRgbScene>(
            keyOf: scene => scene.Name,
            titleOf: scene => scene.Name,
            summaryOf: scene => string.Equals(scene.Name, _activeSceneName, StringComparison.OrdinalIgnoreCase)
                ? "Current"
                : null);

        ControllerInventory = new HardwareInventoryList<DiscoveredRgbControllerRow>(ScanControllersAsync);

        TreePresentation = new ConfigurationPagePresentation(
            "Case lighting",
            new ConfigurationSelectorPresentation("Lighting scenes", SelectorItems, Commands.Create, Commands.ShowCreate, SelectItemCommand),
            ConfigurationEditorPresentation.Create(
                essential:
                [
                    new("Scene", new TerminalElementContent("Lighting scene and controller colors", this)),
                ],
                advanced:
                [
                    new("OpenRGB", new TerminalElementContent("OpenRGB connection settings", this), IsCollapsible: true),
                ],
                hardware:
                [
                    new("Current hardware", new TerminalElementContent("Discovered RGB controllers", ControllerInventory), IsCollapsible: true),
                ]));
    }

    public ObservableCollection<ConfigurationEntityListItem<string>> SelectorItems => _session.SelectorItems;

    /// <summary>The Current hardware section: the OpenRGB controllers behind these scenes.</summary>
    public HardwareInventoryList<DiscoveredRgbControllerRow> ControllerInventory { get; }

    /// <summary>What the shell's standard Rescan button calls.</summary>
    public Func<Task> RescanControllers => ControllerInventory.ScanAsync;

    public string? SelectedSceneName
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
        LoadEditor();
    }

    public CaseRgbSceneEditorViewModel? SelectedScene
    {
        get => _selectedScene;
        private set
        {
            if (SetProperty(ref _selectedScene, value))
            {
                OnPropertyChanged(nameof(SceneDevices));
            }
        }
    }

    public ObservableCollection<CaseRgbSceneDeviceRowViewModel> SceneDevices =>
        SelectedScene?.Devices ?? _emptySceneDevices;

    public bool HasSelectedScene => _session.HasSelection && SelectedScene is not null;

    public int WithinMs
    {
        get => _withinMs;
        set
        {
            if (SetDirtyProperty(ref _withinMs, value))
            {
                RaiseValidation();
            }
        }
    }

    public string CurrentLightingText
    {
        get => _currentLightingText;
        private set => SetProperty(ref _currentLightingText, value);
    }

    public override bool CanSave => HasSelectedScene && HasUnsavedChanges && !IsBusy && ValidationProblem is null;

    public bool CanActivate =>
        SelectedScene is { } selected &&
        selected.Devices.All(device => device.IsReady) &&
        !HasUnsavedChanges &&
        !IsBusy &&
        ValidationProblem is null;

    public override EntityActions Actions => new()
    {
        EntityNoun = "lighting scene",
        CurrentDisplayName = SelectedScene?.Name,
        Create = CreateScene,
        Rename = HasSelectedScene ? RenameSelected : null,
        Duplicate = HasSelectedScene ? DuplicateSelected : null,
        Delete = HasSelectedScene ? () => DeleteSelected() : null,
        Activate = HasSelectedScene ? ActivateSelectedForShellAsync : null,
        ActivateLabel = "Activate",
        CanActivateNow = CanActivate,
    };

    /// <summary>Why what is on screen could not be saved, or null when it could.</summary>
    public override string? ValidationProblem
    {
        get
        {
            if (_withinMs < 500 || _withinMs > 60_000)
            {
                return "The OpenRGB SDK timeout must be between 500 and 60000 milliseconds.";
            }

            if (_configuredDevices.Count == 0)
            {
                return "No RGB controllers are configured.";
            }

            IReadOnlyList<CaseRgbSceneEdit> scenes = BuildSceneEdits();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var signatures = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (CaseRgbSceneEdit scene in scenes)
            {
                string name = scene.Name.Trim();

                if (name.Length == 0)
                {
                    return "Every lighting scene needs a name.";
                }

                if (IsRgb(name))
                {
                    return $"Scene name '{name}' looks like a direct RRGGBB target. Choose another name.";
                }

                if (!names.Add(name))
                {
                    return $"Scene '{name}' is listed more than once.";
                }

                if (!scene.Devices.Select(device => device.Index).ToHashSet()
                    .SetEquals(_configuredDevices.Select(device => device.Index)))
                {
                    return $"Scene '{name}' must define every configured RGB controller.";
                }

                foreach (CaseRgbSceneDeviceEdit device in scene.Devices)
                {
                    if (string.IsNullOrWhiteSpace(device.Mode))
                    {
                        return $"Scene '{name}' needs a mode for controller {device.Index}.";
                    }

                    if (!IsRgb(device.Color.Trim()))
                    {
                        return $"Scene '{name}' needs a six-digit RGB colour for controller {device.Index}.";
                    }

                    if (device.LedColors is { } ledColors &&
                        (ledColors.Count == 0 || ledColors.Any(color => !IsRgb(color.Trim()))))
                    {
                        return $"Scene '{name}' has an invalid per-LED colour for controller {device.Index}.";
                    }
                }

                string signature = SceneSignature(scene);

                if (signatures.TryGetValue(signature, out string? duplicate))
                {
                    return
                        $"Scenes '{duplicate}' and '{name}' describe the same controller state. " +
                        "Change a mode or colour so readback can tell them apart.";
                }

                signatures.Add(signature, name);
            }

            return null;
        }
    }

    public override void Refresh()
    {
        string? previousSelection = SelectedKey as string;
        (CaseRgbProvider? provider, string? problem) = ReadProvider<CaseRgbProvider>();

        if (provider is null)
        {
            _session.Clear();
            SelectedKey = null;
            SelectedScene = null;
            _configuredDevices = [];
            ClearDirtyState();
            SetError(problem ?? "The configuration could not be read.");
            StatusMessage = ErrorMessage;
            ControllerInventory.Apply([], ErrorMessage);
            NotifyEditingState();
            return;
        }

        bool preserveOverlayEditor = _session.HasOverlay && SelectedScene is not null;
        string? current = SafeRead(provider);

        RunWithoutDirtyTracking(() =>
        {
            WithinMs = provider.WithinMs;
            _legacyExecutable = provider.Executable ?? string.Empty;
            _configuredDevices = [.. provider.ConfiguredDevices.OrderBy(device => device.Index)];

            LoadControllerSnapshots(provider);
            _activeSceneName = current is not null && !IsRgb(current) ? current : null;
            _session.Load(
                provider.Scenes.Values.OrderBy(scene => scene.Name, StringComparer.OrdinalIgnoreCase),
                keep: previousSelection);
            SelectedKey = _session.SelectedKey;
        });

        if (!preserveOverlayEditor)
        {
            LoadEditor();
        }
        else
        {
            NotifyEditingState();
        }

        PublishControllerInventory(problem: null);

        int ready = _controllers.Values.Count(controller => controller.State == CaseRgbDeviceState.Ready);
        SetCurrentLighting(current);

        StatusMessage = current is not null && !IsRgb(current)
            ? $"Active scene: '{current}'. {SelectorItems.Count} scene(s). {ready} of {_configuredDevices.Count} OpenRGB controller(s) ready."
            : current is not null && IsRgb(current)
                ? $"Current lighting: #{current} (direct RGB). {SelectorItems.Count} scene(s). {ready} of {_configuredDevices.Count} OpenRGB controller(s) ready."
                : $"Current lighting state could not be verified. {SelectorItems.Count} scene(s). {ready} of {_configuredDevices.Count} OpenRGB controller(s) ready.";

        RaiseValidation();
    }

    public string? CreateScene(string name)
    {
        if (_configuredDevices.Count == 0)
        {
            return "Configure at least one OpenRGB controller before creating a scene.";
        }

        if (SceneNameProblem(name) is string problem)
        {
            return problem;
        }

        string trimmed = name.Trim();
        _session.BeginCreate(trimmed, trimmed);
        SelectedKey = _session.SelectedKey;
        SelectedScene = BuildScene(
            trimmed,
            _configuredDevices.Select(device => new CaseRgbSceneDevice(device.Index, device.Mode, "000000")));

        MarkDirty();
        NotifyEditingState();
        StatusMessage = $"New scene '{trimmed}' created. Set its controller colours and modes, then Save.";
        return null;
    }

    public string? RenameSelected(string name)
    {
        if (SelectedScene is not { } selected)
        {
            return "Choose a lighting scene first.";
        }

        string trimmed = (name ?? string.Empty).Trim();

        if (string.Equals(selected.Name, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (SceneNameProblem(trimmed, _session.Selected?.Name) is string problem)
        {
            return problem;
        }

        string oldName = selected.Name;
        _session.RenameCurrent(trimmed, trimmed);
        SelectedKey = _session.SelectedKey;

        RunWithoutDirtyTracking(() => selected.Name = trimmed);
        MarkDirty();
        NotifyEditingState();
        StatusMessage = $"'{oldName}' renamed to '{trimmed}'. Save to keep the change.";
        return null;
    }

    public string? DuplicateSelected(string name)
    {
        if (SelectedScene is not { } selected)
        {
            return "Choose a lighting scene first.";
        }

        if (SceneNameProblem(name) is string problem)
        {
            return problem;
        }

        string trimmed = name.Trim();
        string source = selected.Name;
        CaseRgbScene copy = ToScene(selected, trimmed);

        _session.BeginDuplicate(trimmed, trimmed);
        SelectedKey = _session.SelectedKey;
        SelectedScene = BuildScene(copy.Name, copy.Devices);
        MarkDirty();
        NotifyEditingState();
        StatusMessage =
            $"Copy of '{source}' ready as '{trimmed}'. Change a mode or colour so readback can distinguish it, then Save.";
        return null;
    }

    public string? DeleteSelected()
    {
        if (SelectedScene is not { } selected)
        {
            return "Choose a lighting scene first.";
        }

        ClearError();

        if (_session.HasOverlay)
        {
            _session.Discard();
            Refresh();
            return null;
        }

        (CaseRgbProvider? provider, string? readProblem) = ReadProvider<CaseRgbProvider>();

        if (provider is null)
        {
            return SetError(readProblem ?? "The configuration could not be read.");
        }

        string name = selected.Name;
        CaseRgbEdit edit = BuildDocumentEdit(
            provider,
            provider.Scenes.Values
                .Where(scene => !string.Equals(scene.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(ToEdit)
                .ToArray(),
            renames: null);

        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = CaseRgbConfigurationStore.Save(Workspace.Root, edit);

            if (!outcome.Success)
            {
                StatusMessage = outcome.Problem ?? "That could not be deleted.";
                Workspace.Warn(Category, StatusMessage);
                return SetError(StatusMessage);
            }

            Refresh();
            StatusMessage = Workspace.Apply($"'{name}' deleted.");
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected override string? SaveChangesCore()
    {
        if (SelectedScene is null)
        {
            return "Choose a lighting scene first.";
        }

        if (ValidationProblem is string problem)
        {
            StatusMessage = problem;
            return problem;
        }

        IReadOnlyDictionary<string, string>? renames =
            !_session.IsDraft &&
            _session.Selected is { } origin &&
            !string.Equals(origin.Name, SelectedScene.Name, StringComparison.Ordinal)
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [origin.Name] = SelectedScene.Name.Trim(),
                }
                : null;

        CaseRgbEdit edit = BuildDocumentEdit(BuildSceneEdits(), renames);
        string selectedName = SelectedScene.Name.Trim();
        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = CaseRgbConfigurationStore.Save(Workspace.Root, edit);

            if (!outcome.Success)
            {
                StatusMessage = outcome.Problem ?? "That could not be saved.";
                Workspace.Warn(Category, StatusMessage);
                return outcome.Problem;
            }

            _session.Commit();
            Refresh();

            if (SelectorItems.Any(item => string.Equals(item.Title, selectedName, StringComparison.OrdinalIgnoreCase)))
            {
                SelectKey(selectedName);
            }

            StatusMessage = Workspace.Apply("Lighting scenes saved.");
            return null;
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

    private Task ActivateSelectedForShellAsync() => ActivateSelectedAsync();

    /// <summary>Applies the selected saved scene through a freshly built provider.</summary>
    public async Task<string?> ActivateSelectedAsync(CancellationToken cancellationToken = default)
    {
        ClearError();

        if (SelectedScene is not { } scene)
        {
            return SetError("Choose a lighting scene first.");
        }

        if (HasUnsavedChanges)
        {
            return SetError("Save or discard changes before activating.");
        }

        if (!scene.Devices.All(device => device.IsReady))
        {
            return SetError("One or more OpenRGB controllers are not ready for this scene.");
        }

        string name = scene.Name;

        await RunActivationAsync(
            Category,
            $"Activating '{name}'...",
            async token =>
            {
                (CaseRgbProvider? provider, string? problem) = ReadProvider<CaseRgbProvider>();

                if (provider is null)
                {
                    return new ActivationOutcome(false, problem ?? "The configuration could not be read.");
                }

                await Task.Run(() => provider.Apply(name, token), token).ConfigureAwait(true);

                string? current = SafeRead(provider);
                SetCurrentLighting(current);

                return string.Equals(current, name, StringComparison.OrdinalIgnoreCase)
                    ? new ActivationOutcome(true, $"'{name}' activated and verified.")
                    : new ActivationOutcome(
                        false,
                        current is null
                            ? $"'{name}' was applied, but OpenRGB could not verify the complete scene state."
                            : $"'{name}' was applied, but OpenRGB currently reads back '{current}'.");
            },
            cancellationToken).ConfigureAwait(true);

        return ErrorMessage;
    }

    private void SetCurrentLighting(string? current)
    {
        _activeSceneName = current is not null && !IsRgb(current) ? current : null;
        RefreshSelectorProjection();

        CurrentLightingText = current switch
        {
            null => "Current lighting: unknown",
            _ when IsRgb(current) => $"Current lighting: #{current} (direct RGB)",
            _ => $"Active scene: {current}",
        };
    }

    private CaseRgbSceneEditorViewModel BuildScene(
        string name,
        IEnumerable<CaseRgbSceneDevice> devices)
    {
        var rows = new List<CaseRgbSceneDeviceRowViewModel>();

        foreach (CaseRgbSceneDevice device in devices.OrderBy(device => device.Index))
        {
            ControllerSnapshot snapshot = _controllers.GetValueOrDefault(
                device.Index,
                ControllerSnapshot.Unknown(device.Index));

            rows.Add(new CaseRgbSceneDeviceRowViewModel(
                device.Index,
                snapshot.Name,
                snapshot.State,
                snapshot.Detail,
                snapshot.Modes,
                snapshot.Leds,
                snapshot.PerLedModes,
                device.Mode,
                device.Color,
                device.LedColors,
                OnSceneChanged));
        }

        return new CaseRgbSceneEditorViewModel(name, rows, OnSceneChanged);
    }

    /// <summary>
    /// Re-reads the OpenRGB controllers behind the Current hardware section
    /// without rebuilding the scenes being edited, so Rescan never discards
    /// unsaved work -- the same thing Rescan means on Audio and Displays.
    /// </summary>
    public Task RescanControllersAsync() => ControllerInventory.ScanAsync();

    private async Task<(IReadOnlyList<DiscoveredRgbControllerRow> Rows, string? Problem)> ScanControllersAsync()
    {
        (CaseRgbProvider? provider, string? problem) = ReadProvider<CaseRgbProvider>();

        if (provider is null)
        {
            return ([], problem ?? "The configuration could not be read.");
        }

        await Task.Run(() => LoadControllerSnapshots(provider)).ConfigureAwait(true);
        return (BuildControllerInventoryRows(), null);
    }

    private void PublishControllerInventory(string? problem) =>
        ControllerInventory.Apply(BuildControllerInventoryRows(), problem);

    private IReadOnlyList<DiscoveredRgbControllerRow> BuildControllerInventoryRows() =>
        [
            .. _configuredDevices.Select(device =>
            {
                ControllerSnapshot snapshot = _controllers.GetValueOrDefault(
                    device.Index,
                    ControllerSnapshot.Unknown(device.Index));

                return new DiscoveredRgbControllerRow(snapshot.Name, snapshot.State, snapshot.Detail);
            }),
        ];

    private void LoadControllerSnapshots(CaseRgbProvider provider)
    {
        _controllers.Clear();

        foreach (CaseRgbConfiguredDevice device in _configuredDevices)
        {
            _controllers[device.Index] = ControllerSnapshot.Unknown(device.Index);
        }

        try
        {
            foreach (CaseRgbDeviceResolution resolution in provider.ResolveConfiguredDevices())
            {
                _controllers[resolution.Configured.Index] = resolution.Controller is { } controller
                    ? new ControllerSnapshot(
                        controller.Name, CaseRgbDeviceState.Ready, resolution.Detail, controller.Modes,
                        controller.Leds, controller.PerLedModes)
                    : new ControllerSnapshot(
                        $"OpenRGB controller {resolution.Configured.Index}",
                        resolution.State,
                        resolution.Detail,
                        [resolution.Configured.Mode]);
            }
        }
        catch (Exception exception)
        {
            foreach (CaseRgbConfiguredDevice device in _configuredDevices)
            {
                _controllers[device.Index] = new ControllerSnapshot(
                    $"OpenRGB controller {device.Index}",
                    CaseRgbDeviceState.Missing,
                    $"OpenRGB SDK server unavailable. {exception.Message}",
                    [device.Mode]);
            }
        }
    }

    private void LoadEditor()
    {
        RunWithoutDirtyTracking(() =>
        {
            SelectedScene = _session.Selected is { } scene
                ? BuildScene(scene.Name, scene.Devices)
                : null;
        });

        ClearDirtyState();
        ClearError();
        NotifyEditingState();
    }

    private void RefreshSelectorProjection()
    {
        CaseRgbScene[] scenes = [.. _session.Items];
        _session.Load(scenes, keep: _session.SelectedKey);
        SelectedKey = _session.SelectedKey;
        OnPropertyChanged(nameof(SelectorItems));
    }

    /// <summary>
    /// Builds the complete scenes document. The selected editor replaces its
    /// persisted origin, or is appended for Create/Duplicate; every untouched
    /// scene remains exactly as the provider parsed it. This is the RGB-specific
    /// atomic persistence boundary underneath the standard one-entity editor.
    /// </summary>
    private IReadOnlyList<CaseRgbSceneEdit> BuildSceneEdits()
    {
        var scenes = new List<CaseRgbSceneEdit>();

        foreach (CaseRgbScene scene in _session.Items)
        {
            if (!_session.IsDraft &&
                ReferenceEquals(scene, _session.Selected) &&
                SelectedScene is { } selected)
            {
                scenes.Add(ToEdit(selected));
            }
            else
            {
                scenes.Add(ToEdit(scene));
            }
        }

        if (_session.IsDraft && SelectedScene is { } draft)
        {
            scenes.Add(ToEdit(draft));
        }

        return scenes;
    }

    private CaseRgbEdit BuildDocumentEdit(
        IReadOnlyList<CaseRgbSceneEdit> scenes,
        IReadOnlyDictionary<string, string>? renames) =>
        new(
            true,
            _legacyExecutable,
            [
                .. _configuredDevices.Select(device =>
                    new CaseRgbDeviceEdit(device.Index, device.Mode)
                    {
                        Identity = device.Identity,
                    }),
            ],
            WithinMs)
        {
            Scenes = scenes,
            SceneRenames = renames,
        };

    private static CaseRgbEdit BuildDocumentEdit(
        CaseRgbProvider provider,
        IReadOnlyList<CaseRgbSceneEdit> scenes,
        IReadOnlyDictionary<string, string>? renames) =>
        new(
            provider.Enabled,
            provider.Executable ?? string.Empty,
            [
                .. provider.ConfiguredDevices.Select(device =>
                    new CaseRgbDeviceEdit(device.Index, device.Mode)
                    {
                        Identity = device.Identity,
                    }),
            ],
            provider.WithinMs)
        {
            Scenes = scenes,
            SceneRenames = renames,
        };

    private static CaseRgbSceneEdit ToEdit(CaseRgbScene scene) =>
        new(
            scene.Name,
            [
                .. scene.Devices.Select(device =>
                    new CaseRgbSceneDeviceEdit(device.Index, device.Mode, device.Color)
                    {
                        LedColors = device.LedColors,
                    }),
            ]);

    private static CaseRgbSceneEdit ToEdit(CaseRgbSceneEditorViewModel scene) =>
        new(
            scene.Name.Trim(),
            [
                .. scene.Devices.Select(device =>
                    new CaseRgbSceneDeviceEdit(
                        device.Index,
                        device.Mode.Trim(),
                        device.Color.Trim().ToUpperInvariant())
                    {
                        LedColors = device.LedColors,
                    }),
            ]);

    private static CaseRgbScene ToScene(CaseRgbSceneEditorViewModel scene, string name) =>
        new(
            name,
            [
                .. scene.Devices.Select(device =>
                    new CaseRgbSceneDevice(device.Index, device.Mode, device.Color)
                    {
                        LedColors = device.LedColors,
                    }),
            ]);

    private string? SceneNameProblem(string? name, string? allowedExisting = null)
    {
        string trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return "Give the scene a name.";
        }

        if (IsRgb(trimmed))
        {
            return "A scene name cannot be six hexadecimal digits because that is already a direct RGB target.";
        }

        if (SelectorItems.Any(item =>
                !string.Equals(item.Title, allowedExisting, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Title, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return $"A lighting scene named '{trimmed}' already exists.";
        }

        return null;
    }

    private void NotifyEditingState()
    {
        OnPropertyChanged(nameof(SelectedSceneName));
        OnPropertyChanged(nameof(SelectedScene));
        OnPropertyChanged(nameof(HasSelectedScene));
        RaiseValidation();
    }

    private void OnSceneChanged()
    {
        MarkDirty();
        RaiseValidation();
    }

    private void RaiseValidation()
    {
        NotifyValidationChanged(
            nameof(ValidationProblem),
            nameof(CanSave),
            nameof(CanActivate));
    }

    protected override void OnDirtyStateChanged()
    {
        base.OnDirtyStateChanged();
        OnPropertyChanged(nameof(CanActivate));
    }

    protected override void OnBusyStateChanged()
    {
        base.OnBusyStateChanged();
        OnPropertyChanged(nameof(CanActivate));
    }

    private static string SceneSignature(CaseRgbSceneEdit scene) =>
        string.Join(
            "|",
            scene.Devices
                .OrderBy(device => device.Index)
                .Select(device =>
                    $"{device.Index}:{device.Mode.Trim().ToUpperInvariant()}:{device.Color.Trim().ToUpperInvariant()}:" +
                    (device.LedColors is { Count: > 0 } ledColors
                        ? string.Join(",", ledColors.Select(color => color.Trim().ToUpperInvariant()))
                        : "-")));

    private static bool IsRgb(string value) =>
        value.Length == 6 && value.All(char.IsAsciiHexDigit);

    private sealed record ControllerSnapshot(
        string Name,
        CaseRgbDeviceState State,
        string Detail,
        IReadOnlyList<string> Modes,
        IReadOnlyList<OpenRgbLedInfo>? Leds = null,
        IReadOnlySet<string>? PerLedModes = null)
    {
        public IReadOnlyList<OpenRgbLedInfo> Leds { get; } = Leds ?? [];

        public IReadOnlySet<string> PerLedModes { get; } =
            PerLedModes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static ControllerSnapshot Unknown(int index) =>
            new(
                $"OpenRGB controller {index}",
                CaseRgbDeviceState.Missing,
                "This controller could not be resolved through the OpenRGB SDK server.",
                []);
    }
}

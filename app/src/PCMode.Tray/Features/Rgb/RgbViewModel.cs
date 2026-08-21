using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using PCMode.Engine;
using PCMode.Tray.Configuration;
using PCMode.Tray.Presentation;

namespace PCMode.Tray.Features.Rgb;

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
                OnPropertyChanged(nameof(ColorBrush));
                NotifyChanged();
            }
        }
    }

    public System.Windows.Media.Brush ColorBrush => ColorBrushFor(Color);

    internal static System.Windows.Media.Brush ColorBrushFor(string value)
    {
        string color = value.Trim();

        if (!IsRgbColor(color))
        {
            return System.Windows.Media.Brushes.Transparent;
        }

        return new SolidColorBrush(System.Windows.Media.Color.FromRgb(
            byte.Parse(color.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(color.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(color.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
    }

    internal static bool IsRgbColor(string value) =>
        value.Length == 6 && value.All(char.IsAsciiHexDigit);
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
                OnPropertyChanged(nameof(ColorBrush));
                NotifyChanged();
            }
        }
    }

    public System.Windows.Media.Brush ColorBrush => CaseRgbLedRowViewModel.ColorBrushFor(Color);

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
public sealed class CaseRgbSceneEditorViewModel : ObservableEditableRow, IConfigurationSelectorItem
{
    private string _name;
    private bool _isActive;

    public CaseRgbSceneEditorViewModel(
        string name,
        string? originalName,
        IEnumerable<CaseRgbSceneDeviceRowViewModel> devices,
        Action onChanged)
        : base(onChanged)
    {
        _name = name;
        OriginalName = originalName;
        Devices = new ObservableCollection<CaseRgbSceneDeviceRowViewModel>(devices);
    }

    public string? OriginalName { get; }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value ?? string.Empty);
    }

    public ObservableCollection<CaseRgbSceneDeviceRowViewModel> Devices { get; }

    public bool IsActive => _isActive;

    public string Subtitle => IsActive
        ? $"Active • {Devices.Count} controller(s)"
        : $"{Devices.Count} controller(s)";

    object IConfigurationSelectorItem.Key => Name;

    public void SetActive(bool active)
    {
        if (_isActive == active)
        {
            return;
        }

        _isActive = active;
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(Subtitle));
    }
}

/// <summary>
/// Edits the named OpenRGB scenes understood by <see cref="CaseRgbProvider"/>.
/// Controller identities stay part of the engine configuration; only mode and
/// colour per scene are edited here.
/// </summary>
public sealed class RgbViewModel : ConfigurationPageViewModelBase
{
    private const string Category = "Rgb";

    private readonly Dictionary<int, ControllerSnapshot> _controllers = [];
    private IReadOnlyList<CaseRgbConfiguredDevice> _configuredDevices = [];
    private string _legacyExecutable = string.Empty;
    private int _withinMs = 4000;
    private string _currentLightingText = "Current lighting: unknown";

    public RgbViewModel(IConfigurationWorkspace workspace) : base(workspace)
    {
    }

    public ObservableCollection<CaseRgbSceneEditorViewModel> Scenes { get; } = [];

    public string? SelectedSceneName
    {
        get => (string?)SelectedKey;
        set
        {
            if (Equals(SelectedKey, value))
            {
                return;
            }

            SelectedKey = value;
            OnPropertyChanged(nameof(SelectedScene));
            OnPropertyChanged(nameof(HasSelectedScene));
            OnPropertyChanged(nameof(CanActivate));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public override void SelectKey(object key) => SelectedSceneName = key as string;

    public CaseRgbSceneEditorViewModel? SelectedScene =>
        Scenes.FirstOrDefault(scene => string.Equals(scene.Name, SelectedSceneName, StringComparison.OrdinalIgnoreCase));

    public bool HasSelectedScene => SelectedScene is not null;

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

    public string SceneCountText => $"{Scenes.Count} profile(s)";

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

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var signatures = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (CaseRgbSceneEditorViewModel scene in Scenes)
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

                if (scene.Devices.Count != _configuredDevices.Count ||
                    !scene.Devices.Select(device => device.Index).ToHashSet()
                        .SetEquals(_configuredDevices.Select(device => device.Index)))
                {
                    return $"Scene '{name}' must define every configured RGB controller.";
                }

                foreach (CaseRgbSceneDeviceRowViewModel device in scene.Devices)
                {
                    if (string.IsNullOrWhiteSpace(device.Mode))
                    {
                        return $"Scene '{name}' needs a mode for '{device.ControllerName}'.";
                    }

                    if (!IsRgb(device.Color.Trim()))
                    {
                        return $"Scene '{name}' needs a six-digit RGB colour for '{device.ControllerName}'.";
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
        string? previousSelection = SelectedSceneName;
        (CaseRgbProvider? provider, string? problem) = ReadProvider<CaseRgbProvider>();

        if (provider is null)
        {
            Scenes.Clear();
            StatusMessage = problem ?? "The configuration could not be read.";
            return;
        }

        RunWithoutDirtyTracking(() =>
        {
            WithinMs = provider.WithinMs;
            _legacyExecutable = provider.Executable ?? string.Empty;
            _configuredDevices = [.. provider.ConfiguredDevices.OrderBy(device => device.Index)];

            LoadControllerSnapshots(provider);

            Scenes.Clear();

            foreach (CaseRgbScene scene in provider.Scenes.Values.OrderBy(scene => scene.Name, StringComparer.OrdinalIgnoreCase))
            {
                Scenes.Add(BuildScene(scene.Name, scene.Name, scene.Devices));
            }
        });

        ClearDirtyState();

        SelectedSceneName =
            Scenes.FirstOrDefault(scene => string.Equals(scene.Name, previousSelection, StringComparison.OrdinalIgnoreCase))?.Name ??
            Scenes.FirstOrDefault()?.Name;

        OnPropertyChanged(nameof(SelectedScene));
        OnPropertyChanged(nameof(HasSelectedScene));
        OnPropertyChanged(nameof(CanDiscard));
        OnPropertyChanged(nameof(SceneCountText));

        int ready = _controllers.Values.Count(controller => controller.State == CaseRgbDeviceState.Ready);
        string? current = SafeRead(provider);
        SetCurrentLighting(current);

        StatusMessage = current is not null && !IsRgb(current)
            ? $"Active scene: '{current}'. {Scenes.Count} scene(s). {ready} of {_configuredDevices.Count} OpenRGB controller(s) ready."
            : current is not null && IsRgb(current)
                ? $"Current lighting: #{current} (direct RGB). {Scenes.Count} scene(s). {ready} of {_configuredDevices.Count} OpenRGB controller(s) ready."
                : $"Current lighting state could not be verified. {Scenes.Count} scene(s). {ready} of {_configuredDevices.Count} OpenRGB controller(s) ready.";

        RaiseValidation();
    }

    public string? CreateScene(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();

        if (_configuredDevices.Count == 0)
        {
            return "Configure at least one OpenRGB controller before creating a scene.";
        }

        if (trimmed.Length == 0)
        {
            return "Give the scene a name.";
        }

        if (IsRgb(trimmed))
        {
            return "A scene name cannot be six hexadecimal digits because that is already a direct RGB target.";
        }

        if (Scenes.Any(scene => string.Equals(scene.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return $"A lighting scene named '{trimmed}' already exists.";
        }

        CaseRgbSceneEditorViewModel scene = BuildScene(
            trimmed,
            originalName: null,
            _configuredDevices.Select(device => new CaseRgbSceneDevice(device.Index, device.Mode, "000000")));

        Scenes.Add(scene);
        SelectedSceneName = scene.Name;
        MarkDirty();
        RaiseValidation();
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

        if (trimmed.Length == 0)
        {
            return "Give the scene a name.";
        }

        if (IsRgb(trimmed))
        {
            return "A scene name cannot be six hexadecimal digits because that is already a direct RGB target.";
        }

        if (Scenes.Any(scene =>
                !ReferenceEquals(scene, selected) &&
                string.Equals(scene.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return $"A lighting scene named '{trimmed}' already exists.";
        }

        string oldName = selected.Name;
        selected.Name = trimmed;
        SelectedSceneName = trimmed;
        MarkDirty();
        RaiseValidation();
        StatusMessage = $"'{oldName}' renamed to '{trimmed}'. Save to keep the change.";
        return null;
    }

    public string? DeleteSelected()
    {
        if (SelectedScene is not { } selected)
        {
            return "Choose a lighting scene first.";
        }

        int index = Scenes.IndexOf(selected);
        string name = selected.Name;
        Scenes.Remove(selected);

        SelectedSceneName = Scenes.Count == 0
            ? null
            : Scenes[Math.Clamp(index, 0, Scenes.Count - 1)].Name;

        MarkDirty();
        RaiseValidation();
        StatusMessage = $"'{name}' removed. Save to keep the change.";
        return null;
    }

    protected override string? SaveChangesCore()
    {
        if (ValidationProblem is string problem)
        {
            StatusMessage = problem;
            return problem;
        }

        var edit = new CaseRgbEdit(
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
            Scenes =
            [
                .. Scenes.Select(scene =>
                    new CaseRgbSceneEdit(
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
                        ])),
            ],
            SceneRenames = BuildRenameMap(),
        };

        string? selectedName = SelectedSceneName;
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

            Refresh();

            if (selectedName is not null)
            {
                SelectedSceneName = Scenes.FirstOrDefault(scene =>
                    string.Equals(scene.Name, selectedName, StringComparison.OrdinalIgnoreCase))?.Name;
            }

            StatusMessage = Workspace.Apply("Lighting scenes saved.");
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected override void DiscardChangesCore() => Refresh();

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
            return SetError("Save or discard changes before activating a scene.");
        }

        if (!scene.Devices.All(device => device.IsReady))
        {
            return SetError("One or more OpenRGB controllers are not ready for this scene.");
        }

        (CaseRgbProvider? provider, string? problem) = ReadProvider<CaseRgbProvider>();

        if (provider is null)
        {
            StatusMessage = problem ?? "The configuration could not be read.";
            return SetError(StatusMessage);
        }

        string name = scene.Name;
        IsBusy = true;
        StatusMessage = $"Activating '{name}'...";

        try
        {
            await Task.Run(
                () => provider.Apply(name, cancellationToken),
                cancellationToken).ConfigureAwait(true);

            string? current = SafeRead(provider);
            SetCurrentLighting(current);

            if (string.Equals(current, name, StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = $"'{name}' activated and verified.";
                Workspace.Note(Category, StatusMessage);
                return null;
            }

            StatusMessage = current is null
                ? $"'{name}' was applied, but OpenRGB could not verify the complete scene state."
                : $"'{name}' was applied, but OpenRGB currently reads back '{current}'.";
            Workspace.Warn(Category, StatusMessage);
            return SetError(StatusMessage);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Lighting scene activation was cancelled.";
            return SetError(StatusMessage);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not activate '{name}'. {exception.Message}";
            Workspace.Fault(Category, $"Could not activate '{name}'.", exception);
            return SetError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetCurrentLighting(string? current)
    {
        foreach (CaseRgbSceneEditorViewModel scene in Scenes)
        {
            scene.SetActive(
                current is not null &&
                !IsRgb(current) &&
                string.Equals(scene.Name, current, StringComparison.OrdinalIgnoreCase));
        }

        CurrentLightingText = current switch
        {
            null => "Current lighting: unknown",
            _ when IsRgb(current) => $"Current lighting: #{current} (direct RGB)",
            _ => $"Active scene: {current}",
        };
    }

    private CaseRgbSceneEditorViewModel BuildScene(
        string name,
        string? originalName,
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

        return new CaseRgbSceneEditorViewModel(name, originalName, rows, OnSceneChanged);
    }

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

    private IReadOnlyDictionary<string, string> BuildRenameMap()
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (CaseRgbSceneEditorViewModel scene in Scenes)
        {
            if (scene.OriginalName is { } original &&
                !string.Equals(original, scene.Name, StringComparison.Ordinal))
            {
                renames[original] = scene.Name.Trim();
            }
        }

        return renames;
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

    private static string SceneSignature(CaseRgbSceneEditorViewModel scene) =>
        string.Join(
            "|",
            scene.Devices
                .OrderBy(device => device.Index)
                .Select(device =>
                    $"{device.Index}:{device.Mode.Trim().ToUpperInvariant()}:{device.Color.Trim().ToUpperInvariant()}"));

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

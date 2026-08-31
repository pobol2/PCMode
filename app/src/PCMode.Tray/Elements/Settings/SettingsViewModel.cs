using System.Collections.ObjectModel;
using PCMode.Engine;
using PCMode.Tray.Elements.Configuration;

namespace PCMode.Tray.Elements.Settings;

public sealed record PickerMonitorOption(string Name, PickerMonitorChoice Value);

public sealed record LaunchBehaviorOption(string Name, LaunchBehavior Value);

public sealed record ApplicationThemeOption(string Name, ApplicationTheme Value);

/// <summary>
/// Configures PCMode itself -- appearance, which display the picker opens on,
/// Windows startup, and controller input. Editing these values never applies a
/// mode.
/// </summary>
/// <remarks>
/// Picker favorites and direct shortcuts are part of <see cref="ApplicationSettings"/>
/// and round-trip through every save untouched -- this page has no UI for them,
/// matching legacy's own Settings page, which points at Spaces instead (a page
/// Target does not yet expose the same picker-authoring surface on). Losing them
/// silently on a save from here would be a real regression for a data root
/// legacy PCMode.Tray still reads, so <see cref="Save"/> always starts from
/// what was last loaded and only overwrites the fields this page actually edits.
///
/// <see cref="ApplicationSettings.Theme"/> takes effect immediately: a
/// successful <see cref="Save"/> invokes <c>onSaved</c>, which the composition
/// root uses to re-apply <c>Themes/ApplicationThemeService</c>. The "Guided
/// setup" section legacy shows here is omitted entirely rather than shown
/// disabled, since Target has no setup wizard yet for it to open.
/// </remarks>
public sealed class SettingsViewModel : ObservableObject, IPageLifecycle
{
    private readonly IConfigurationWorkspace _workspace;
    private readonly ApplicationSettingsStore? _store;
    private readonly Action<ApplicationSettings>? _onSaved;

    private TargetCatalog _targets = new([]);
    private ApplicationSettings _loaded = new();

    private PickerMonitorOption _selectedMonitor;
    private ApplicationThemeOption _selectedTheme;
    private LaunchBehaviorOption _selectedLaunchBehavior;
    private string? _startupTarget;
    private bool _controllerEnabled;
    private bool _startWithWindows;
    private bool _startMinimizedToTray;
    private string? _statusMessage;
    private bool _statusIsError;

    private static readonly TimeSpan LiveStatusInterval = TimeSpan.FromSeconds(3);

    private readonly Action? _openGuidedSetup;
    private readonly LiveRefreshTimer _liveRefresh;

    public SettingsViewModel(
        IConfigurationWorkspace workspace,
        ApplicationSettingsStore? store,
        Action<ApplicationSettings>? onSaved = null,
        IGamepadCapabilitiesSource? controllers = null,
        Action? openGuidedSetup = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        _workspace = workspace;
        _store = store;
        _onSaved = onSaved;
        _openGuidedSetup = openGuidedSetup;
        ControllerDiscovery = new ControllerDiscoveryViewModel(workspace, controllers);
        _liveRefresh = new LiveRefreshTimer(
            LiveStatusInterval,
            () => ControllerDiscovery.ScanAsync());

        MonitorOptions =
        [
            new("Primary display", PickerMonitorChoice.Primary),
            new("Display under the pointer", PickerMonitorChoice.UnderPointer),
            new("Display with the foreground window", PickerMonitorChoice.WithForegroundWindow),
        ];

        LaunchBehaviorOptions =
        [
            new("Do nothing", LaunchBehavior.DoNothing),
            new("Restore previous target", LaunchBehavior.RestorePreviousMode),
            new("Switch to selected target", LaunchBehavior.SwitchToMode),
        ];

        ThemeOptions =
        [
            new("System", ApplicationTheme.System),
            new("Light", ApplicationTheme.Light),
            new("Dark", ApplicationTheme.Dark),
        ];

        _selectedMonitor = MonitorOptions[0];
        _selectedTheme = ThemeOptions[0];
        _selectedLaunchBehavior = LaunchBehaviorOptions[0];
    }

    public IReadOnlyList<PickerMonitorOption> MonitorOptions { get; }

    public IReadOnlyList<LaunchBehaviorOption> LaunchBehaviorOptions { get; }

    public IReadOnlyList<ApplicationThemeOption> ThemeOptions { get; }

    public ControllerDiscoveryViewModel ControllerDiscovery { get; }

    public ObservableCollection<ConfiguredTarget> Targets { get; } = [];

    public bool CanEdit => _store is not null;

    public PickerMonitorOption SelectedMonitor
    {
        get => _selectedMonitor;
        set => SetProperty(ref _selectedMonitor, value);
    }

    public ApplicationThemeOption SelectedTheme
    {
        get => _selectedTheme;
        set => SetProperty(ref _selectedTheme, value);
    }

    public bool ControllerEnabled
    {
        get => _controllerEnabled;
        set => SetProperty(ref _controllerEnabled, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool StartMinimizedToTray
    {
        get => _startMinimizedToTray;
        set => SetProperty(ref _startMinimizedToTray, value);
    }

    public LaunchBehaviorOption SelectedLaunchBehavior
    {
        get => _selectedLaunchBehavior;
        set => SetProperty(ref _selectedLaunchBehavior, value);
    }

    /// <summary>The key of the target startup switches to.</summary>
    public string? StartupTarget
    {
        get => _startupTarget;
        set => SetProperty(ref _startupTarget, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    /// <summary>Reloads targets and the persisted source, discarding any edit.</summary>
    /// <summary>
    /// Whether the guided-setup entry point is offered at all. Nothing outside
    /// a hosted process has a window to open it in, so it is hidden rather
    /// than shown disabled.
    /// </summary>
    public bool ShowGuidedSetup => _openGuidedSetup is not null;

    public void OpenGuidedSetup() => _openGuidedSetup?.Invoke();

    public async void OnActivated()
    {
        Refresh();
        _liveRefresh.Start();
        await ControllerDiscovery.ScanAsync().ConfigureAwait(true);
    }

    public void OnDeactivated() => _liveRefresh.Stop();

    public void Refresh()
    {
        _targets = _workspace.Read().Configuration.Targets;

        Targets.Clear();
        foreach (ConfiguredTarget target in _targets.Targets)
        {
            Targets.Add(target);
        }

        if (_store is null)
        {
            StatusIsError = true;
            StatusMessage = "Settings are unavailable in this session.";
            return;
        }

        try
        {
            _loaded = _store.Load(_targets);
        }
        catch (ModeConfigurationException exception)
        {
            StatusIsError = true;
            StatusMessage = $"PCMode's settings could not be read. {exception.Message}";
            return;
        }

        SelectedTheme = ThemeOptions.Single(option => option.Value == _loaded.Theme);
        SelectedMonitor = MonitorOptions.Single(option => option.Value == _loaded.PickerMonitor);
        ControllerEnabled = _loaded.ControllerEnabled;
        StartWithWindows = _loaded.StartWithWindows;
        StartMinimizedToTray = _loaded.StartMinimizedToTray;
        SelectedLaunchBehavior = LaunchBehaviorOptions.Single(option => option.Value == _loaded.LaunchBehavior);
        StartupTarget = Key(_loaded.StartupTarget) ?? Targets.FirstOrDefault()?.Key;
        StatusIsError = false;
        StatusMessage = null;
    }

    public void Save()
    {
        if (_store is null)
        {
            StatusIsError = true;
            StatusMessage = "Settings are unavailable in this session.";
            return;
        }

        var candidate = _loaded with
        {
            Theme = SelectedTheme.Value,
            PickerMonitor = SelectedMonitor.Value,
            ControllerEnabled = ControllerEnabled,
            StartWithWindows = StartWithWindows,
            StartMinimizedToTray = StartMinimizedToTray,
            LaunchBehavior = SelectedLaunchBehavior.Value,
            StartupTarget = StartupTarget,
        };

        SettingsSaveOutcome outcome = _store.Save(candidate, _targets);

        if (!outcome.Success)
        {
            StatusIsError = true;
            StatusMessage = outcome.Problem ?? "PCMode could not save these settings.";
            _workspace.Warn("Settings", StatusMessage);
            return;
        }

        _loaded = candidate;
        StatusIsError = false;
        StatusMessage = _workspace.Apply("Settings saved.");
        _onSaved?.Invoke(candidate);
    }

    /// <summary>The key a saved reference means, or null when it means none.</summary>
    private string? Key(string? reference) => _targets.Resolve(reference)?.Key;
}

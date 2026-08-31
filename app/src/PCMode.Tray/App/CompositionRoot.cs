using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using PCMode.Engine;
using PCMode.Tray.Platform.Startup;
using PCMode.Tray.Platform.Themes;
using PCMode.Tray.Platform.Logging;
using PCMode.Tray.Elements.Settings;
using PCMode.Tray.Picker;
using PCMode.Tray.Platform.TrayIcon;
using PCMode.Tray.Elements.Navigation;
using PCMode.Tray.Elements.Applications;
using PCMode.Tray.Elements.Audio;
using PCMode.Tray.Elements.Configuration;
using PCMode.Tray.Elements.Diagnostics;
using PCMode.Tray.Elements.Displays;
using PCMode.Tray.Elements.Home;
using PCMode.Tray.Elements.Rgb;
using PCMode.Tray.Elements.Spaces;

namespace PCMode.Tray;

/// <summary>What could not be recovered from before any window exists.</summary>
public sealed class TrayStartupException(string message) : Exception(message);

/// <summary>
/// Builds the real engine, hosts it for the process's life, and wires a saved
/// change on any page back into it.
/// </summary>
/// <remarks>
/// This is what makes Target the actual running instance instead of an editor
/// of a file something else reads: the picker, global hotkeys, the controller
/// and app chrome theming are all wired here, the same way legacy
/// PCMode.Tray's <c>App.xaml.cs</c> wired them, just against
/// <see cref="EngineRuntime"/> directly rather than a <c>ShellViewModel</c>
/// (Target has none -- see <c>ARCHITECTURE.md</c>'s "Permanently excluded").
/// One <see cref="EngineRuntime"/> for the process's life, single-instance
/// ownership, a tray icon that can reach every configured target, and a
/// shutdown that will not walk away from a transition mid-flight.
/// </remarks>
public sealed class CompositionRoot : IDisposable
{
    private readonly LocalLog _log;
    private readonly ResourceDictionary _resources;
    private readonly Action<int> _shutdown;
    private readonly bool _inputEnabled;

    private readonly AudioHostLink _audio;
    private readonly ApplicationSettingsStore _settingsStore;
    private readonly EngineRuntime _runtime;
    private readonly ApplicationThemeService _theme;
    private readonly ExitCoordinator _exit;
    private readonly Icon _applicationIcon;
    private readonly NotifyIcon _trayIcon;

    private PickerHost? _picker;

    /// <param name="options">The parsed, valid command line.</param>
    /// <param name="firstRun">Whether no configuration file existed yet at startup.</param>
    /// <param name="log">The process's one shared log.</param>
    /// <param name="resources">The application's resource dictionary, for theming.</param>
    /// <param name="shutdown">Ends the process with the given exit code.</param>
    private CompositionRoot(
        TrayOptions options,
        bool firstRun,
        LocalLog log,
        ResourceDictionary resources,
        Action<int> shutdown)
    {
        _log = log;
        _resources = resources;
        _shutdown = shutdown;
        _inputEnabled = !options.NoInput;

        ModeEngine engine;
        ApplicationSettings persistedSettings;

        try
        {
            _audio = new AudioHostLink();
            engine = EngineBootstrap.Create(options.DataRoot, _audio);

            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Windows did not report the PCMode executable path.");

            _settingsStore = new ApplicationSettingsStore(
                options.DataRoot,
                new WindowsAutoStartRegistration(),
                AutoStartCommand.Build(executable, options.DataRoot));

            persistedSettings = _settingsStore.Load(engine.Catalog);
        }
        catch (ModeConfigurationException exception)
        {
            log.Error("Configuration", "PCMode could not read its configuration.", exception);
            throw new TrayStartupException($"PCMode could not read its configuration.\n\n{exception.Message}");
        }

        ApplicationSettings? runtimeSettings = options.Resolve(persistedSettings, out string? overrideProblem);
        if (runtimeSettings is null)
        {
            log.Warning("Command line", overrideProblem ?? "Command-line overrides could not be applied.");
            throw new TrayStartupException($"PCMode could not apply its command-line overrides.\n\n{overrideProblem}");
        }

        _runtime = new EngineRuntime(engine, options.DataRoot, audio: _audio);

        _theme = new ApplicationThemeService(resources, new WindowsSystemThemePreference());
        _theme.ThemeChanged += OnApplicationThemeChanged;
        _theme.Apply(runtimeSettings.Theme);
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));

        IConfigurationWorkspace workspace = ConfigurationWorkspace.For(
            options.DataRoot,
            log,
            _audio,
            ActivateRuntime);

        // Modal prompt UI is connected with the dialog surface in migration
        // step 6. Until then the safe unattended implementation refuses any
        // operation that would require an answer.
        IConfigurationPrompts prompts = NoConfigurationPrompts.Instance;

        var elements = new ElementCatalog(
        [
            KeyValuePair.Create<PageId, object>(
                PageId.Home,
                new HomeViewModel(_runtime, _settingsStore)),
            KeyValuePair.Create<PageId, object>(
                PageId.Applications,
                new ApplicationsViewModel(workspace, prompts)),
            KeyValuePair.Create<PageId, object>(
                PageId.Audio,
                new AudioViewModel(workspace, prompts)),
            KeyValuePair.Create<PageId, object>(
                PageId.Rgb,
                new RgbViewModel(workspace, prompts)),
            KeyValuePair.Create<PageId, object>(
                PageId.Displays,
                new DisplaysViewModel(
                    workspace,
                    name => _runtime.Engine.Providers
                        .OfType<TvPowerProvider>()
                        .FirstOrDefault(provider =>
                            string.Equals(
                                provider.TelevisionName,
                                name,
                                StringComparison.OrdinalIgnoreCase)),
                    prompts)),
            KeyValuePair.Create<PageId, object>(
                PageId.Spaces,
                new SpacesViewModel(
                    workspace,
                    _runtime,
                    _settingsStore,
                    OnApplicationSettingsChanged,
                    prompts)),
            KeyValuePair.Create<PageId, object>(
                PageId.Settings,
                new SettingsViewModel(
                    workspace,
                    _settingsStore,
                    OnApplicationSettingsChanged)),
            KeyValuePair.Create<PageId, object>(
                PageId.Diagnostics,
                new DiagnosticsViewModel(workspace, _runtime, log)),
        ]);

        elements.Validate(NavigationRegistry.Items);

        var navigation = new NavigationState();

        var appViewModel = new AppViewModel(
            navigation,
            elements);

        Window = new MainWindow(appViewModel);
        _theme.ApplyTo(Window);

        _exit = new ExitCoordinator(() => !_runtime.Engine.IsBusy, AskAboutExit, ShutdownForReal);

        if (_inputEnabled)
        {
            StartPicker(runtimeSettings);
        }

        _applicationIcon = PCModeIcon.LoadTrayIcon();
        _trayIcon = new NotifyIcon
        {
            Icon = _applicationIcon,
            Visible = true,
            Text = "PCMode",
            ContextMenuStrip = new ContextMenuStrip
            {
                ShowImageMargin = false,
                ShowCheckMargin = true,
                AccessibleName = "PCMode quick actions",
            },
        };

        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                Window.Dispatcher.BeginInvoke(() => ShowWindow());
            }
        };

        TrayMenuTheme.Apply(_trayIcon.ContextMenuStrip, resources);
        RebuildTrayMenu();

        if (!options.Background || firstRun)
        {
            ShowWindow();
        }

        string? startupRegistrationProblem = _settingsStore.SynchronizeAutoStart(persistedSettings);
        if (startupRegistrationProblem is not null)
        {
            log.Warning("Startup registration", startupRegistrationProblem);
        }

        log.Information("Startup", $"PCMode started with {engine.TargetKeys.Count} configured target(s).");
    }

    public MainWindow Window { get; }

    public static CompositionRoot Build(
        TrayOptions options,
        bool firstRun,
        LocalLog log,
        ResourceDictionary resources,
        Action<int> shutdown) =>
        new(options, firstRun, log, resources, shutdown);

    /// <summary>Brings the window to the foreground, restoring it first if minimized or hidden.</summary>
    public void ShowWindow() => Window.ShowAndActivate();

    public void Dispose()
    {
        _picker?.Dispose();
        _picker = null;

        _theme.ThemeChanged -= OnApplicationThemeChanged;
        _theme.Dispose();

        _runtime.Dispose();

        // Also here, because the paths that fail before the runtime is built --
        // an unreadable configuration, a rejected command line -- have already
        // created it. Stopping a mirror twice is a no-op; leaving one running
        // after the process that owns it has gone is not.
        _audio.Dispose();

        // Without this the icon lingers in the notification area until
        // something makes Windows repaint it.
        _trayIcon.Visible = false;

        // NotifyIcon.Dispose does not dispose the strip it was given, and the
        // strip owns a window handle of its own.
        ContextMenuStrip? menu = _trayIcon.ContextMenuStrip;
        _trayIcon.ContextMenuStrip = null;
        menu?.Dispose();

        _trayIcon.Dispose();
        _applicationIcon.Dispose();
    }

     /// <summary>Puts the exit question to the user, in the terms that matter.</summary>
    private static ExitChoice AskAboutExit()
    {
        MessageBoxResult answer = System.Windows.MessageBox.Show(
            "PCMode is applying a target.\n\n" +
            "Exiting now leaves PCMode running: it will finish on its own, but this window will not " +
            "be here to show how it turned out.\n\n" +
            "Yes\twait for it to finish, then exit\n" +
            "No\texit now and leave it running\n" +
            "Cancel\tstay open",
            "PCMode is still working",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        return answer switch
        {
            MessageBoxResult.Yes => ExitChoice.WaitThenExit,
            MessageBoxResult.No => ExitChoice.ExitAndLeaveItRunning,
            _ => ExitChoice.Stay,
        };
    }

    /// <summary>
    /// Really goes, which the window otherwise refuses.
    /// </summary>
    /// <remarks>
    /// MainWindow cancels its own closing so that the red cross hides it
    /// instead of ending the process -- the tray icon is the application.
    /// Reached only once the exit coordinator has decided, so a user who chose
    /// to stay does not silently end up with a closeable window.
    /// </remarks>
    private void ShutdownForReal()
    {
        Window.AllowClose = true;
        _shutdown(0);
    }


    private void RebuildTrayMenu()
    {
        if (_trayIcon.ContextMenuStrip is null)
        {
            return;
        }

        ModeEngine engine = _runtime.Engine;
        string? currentKey = engine.ReadCurrentTargetKey(out _);
        string currentLabel = engine.ResolveTarget(currentKey)?.DisplayPath ?? "Unknown";

        TrayMenu.Rebuild(
            _trayIcon.ContextMenuStrip,
            new TrayMenuState(engine.Targets, currentKey, currentLabel, IsIdle: !engine.IsBusy, CanShowPicker: _picker is not null),
            new TrayMenuActions(ApplyTargetFromTray, () => _picker?.ShowPicker(), ShowWindow, RequestExitAsync));

        _trayIcon.Text = TrayMenu.StatusText(currentLabel);
    }

    /// <summary>
    /// Starts the picker and the hotkeys, or explains why it could not.
    /// </summary>
    /// <remarks>
    /// A refusal is not a startup failure. The shell is perfectly useful
    /// without the wheel -- it lists modes and applies them from the tray --
    /// and the reason is nearly always one key held by another program, which
    /// is something the user can act on once they are told which.
    /// </remarks>
    private void StartPicker(ApplicationSettings settings)
    {
        var actions = new PickerHostActions(
            () => _runtime.Engine.ReadCurrentTargetKey(out _),
            ApplyTargetFromTray);

        _picker = PickerHost.TryStart(
            actions,
            settings.Favorites,
            settings.DirectShortcuts,
            settings.PickerMonitor,
            settings.ControllerEnabled,
            out string? refusal);

        if (_picker is null)
        {
            _log.Warning("Input", refusal ?? "The picker and hotkeys could not start.");
        }
        else
        {
            _log.Information(
                "Input",
                $"Picker started with {_picker.Hotkeys.Count} hotkey(s); controller " +
                $"{(settings.ControllerEnabled ? _picker.ControllerLibrary ?? "not available" : "disabled")}.");
        }
    }

    private void OnApplicationThemeChanged(object? sender, EventArgs e)
    {
        // _theme.Apply below can raise this synchronously, before _trayIcon
        // is constructed later in this same constructor -- it is legitimately
        // null the first time this runs.
        if (_trayIcon?.ContextMenuStrip is { } menu)
        {
            TrayMenuTheme.Apply(menu, _resources);
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            _theme.ApplyTo(window);
        }
    }

    /// <summary>
    /// Makes a saved configuration the process's live configuration and
    /// refreshes App-owned tray state only after activation succeeds.
    /// </summary>
    private RuntimeActivationOutcome ActivateRuntime()
    {
        RuntimeActivationOutcome outcome = _runtime.Reload();

        if (outcome.Activated)
        {
            RebuildTrayMenu();
        }
        else
        {
            _log.Warning("Configuration", outcome.Message);
        }

        return outcome;
    }

    /// <summary>
    /// Reacts to persisted application settings whose live ownership belongs
    /// to App rather than to the Element that edited them.
    /// </summary>
    private void OnApplicationSettingsChanged(ApplicationSettings settings)
    {
        _theme.Apply(settings.Theme);

        if (!_inputEnabled)
        {
            return;
        }

        _picker?.Dispose();
        _picker = null;
        StartPicker(settings);
        RebuildTrayMenu();
    }

    private async Task ApplyTargetFromTray(string targetKey)
    {
        try
        {
            TransitionOutcome outcome = await _runtime.Engine.ApplyAsync(targetKey).ConfigureAwait(true);
            _log.Information("Targets", outcome.Message);
        }
        catch (Exception exception)
        {
            _log.Error("Targets", $"Could not apply '{targetKey}'.", exception);
        }
        finally
        {
            RebuildTrayMenu();
        }
    }

    /// <summary>
    /// Ends the shell, having first dealt with anything still in flight.
    /// </summary>
    private Task RequestExitAsync() => _exit.RequestExitAsync();
}

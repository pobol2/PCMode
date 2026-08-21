using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using PCMode.Engine;
using PCMode.Picker;
using PCMode.Tray.Configuration;
using PCMode.Tray.Diagnostics;
using PCMode.Tray.Input;
using PCMode.Tray.Navigation;
using PCMode.Tray.Picker;
using PCMode.Tray.Runtime;
using PCMode.Tray.Settings;
using PCMode.Tray.Setup;
using PCMode.Tray.Shell;
using PCMode.Tray.Themes;

namespace PCMode.Tray;

/// <summary>
/// The composition root. Builds the real engine, hosts it for the process's
/// life, and wires a saved change on any page back into it.
/// </summary>
/// <remarks>
/// This is what makes Target the actual running instance instead of an
/// editor of a file something else reads: the picker, global hotkeys, the
/// controller and app chrome theming are all wired here, the same way legacy
/// PCMode.Tray's <c>App.xaml.cs</c> wires them, just against
/// <see cref="EngineRuntime"/> directly rather than a <c>ShellViewModel</c>
/// (Target has none -- see <c>ARCHITECTURE.md</c>'s "Permanently
/// excluded"). One <see cref="EngineRuntime"/> for the process's life,
/// single-instance ownership, a tray icon that can reach every configured
/// target, and a shutdown that will not walk away from a transition
/// mid-flight.
/// </remarks>
public partial class App : System.Windows.Application
{
    private NotifyIcon? _trayIcon;
    private Icon? _applicationIcon;
    private MainWindow? _window;
    private SingleInstance? _instance;
    private ExitCoordinator? _exit;
    private EngineRuntime? _runtime;
    private ApplicationThemeService? _theme;
    private PickerHost? _picker;
    private TrayOptions? _options;

    /// <summary>
    /// The Party mirror's owner, created once for the life of the process.
    /// Handed to every engine <see cref="EngineRuntime"/> builds, including
    /// across a reload, and stopped when the shell exits.
    /// </summary>
    private PartyAudioHost? _party;

    private LocalLog? _log;

    private ApplicationSettingsStore? _settingsStore;

    private PageCatalog? _pageCatalog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
            _log?.Error("Unhandled", "An unhandled dispatcher exception terminated an operation.", args.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _log?.Error(
                "Unhandled",
                "An unhandled application exception occurred.",
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));

        TaskScheduler.UnobservedTaskException += (_, args) =>
            _log?.Error("Unhandled", "An unobserved task exception occurred.", args.Exception);

        try
        {
            Start(e);
        }
        catch (Exception exception)
        {
            _log?.Error("Startup", "PCMode could not start.", exception);
            System.Windows.MessageBox.Show(
                $"PCMode could not start.\n\n{Describe(exception)}",
                "PCMode",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    private void Start(StartupEventArgs e)
    {
        TrayOptions options = TrayOptions.Parse(e.Args);
        _options = options;

        if (!options.IsValid)
        {
            System.Windows.MessageBox.Show(
                $"PCMode could not read its command line.\n\n{options.Error}",
                "PCMode",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            Shutdown(1);
            return;
        }

        // Before anything else acquires a resource. A second launch must not
        // create a second tray icon.
        _instance = SingleInstance.Acquire();

        if (!_instance.IsOwner)
        {
            if (!options.Background)
            {
                _instance.RequestActivation();
            }

            _instance.Dispose();
            _instance = null;

            Shutdown(0);
            return;
        }

        _instance.ActivationRequested += (_, _) => Dispatcher.BeginInvoke(() => ShowWindow());

        string resolvedRoot = options.DataRoot ?? PCModePaths.DefaultRoot;

        // Whether the setup wizard is offered is tracked separately, by
        // ApplicationSettings.SetupCompleted, so it can be dismissed without
        // reappearing even though the configuration file now exists. This
        // only decides whether the main window is force-shown so the wizard
        // it is about to open (below) is not opened over a hidden window.
        bool firstRun = !System.IO.File.Exists(PCModePaths.ConfigurationFile(resolvedRoot));

        _log = new LocalLog(resolvedRoot);
        _log.Information("Startup", $"PCMode starting. Data root: {resolvedRoot}");

        ModeEngine engine;
        ApplicationSettingsStore settingsStore;
        ApplicationSettings persistedSettings;

        try
        {
            _party = new PartyAudioHost();
            engine = EngineBootstrap.Create(options.DataRoot, _party);

            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Windows did not report the PCMode executable path.");

            settingsStore = new ApplicationSettingsStore(
                options.DataRoot,
                new WindowsAutoStartRegistration(),
                AutoStartCommand.Build(executable, options.DataRoot));

            persistedSettings = settingsStore.Load(engine.Catalog);
        }
        catch (ModeConfigurationException exception)
        {
            _log.Error("Configuration", "PCMode could not read its configuration.", exception);
            System.Windows.MessageBox.Show(
                $"PCMode could not read its configuration.\n\n{exception.Message}",
                "PCMode",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            Shutdown(1);
            return;
        }

        ApplicationSettings? runtimeSettings = options.Resolve(persistedSettings, out string? overrideProblem);
        if (runtimeSettings is null)
        {
            _log.Warning("Command line", overrideProblem ?? "Command-line overrides could not be applied.");
            System.Windows.MessageBox.Show(
                $"PCMode could not apply its command-line overrides.\n\n{overrideProblem}",
                "PCMode",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            Shutdown(1);
            return;
        }

        _settingsStore = settingsStore;
        _runtime = new EngineRuntime(engine, options.DataRoot, party: _party);

        _theme = new ApplicationThemeService(Resources, new WindowsSystemThemePreference());
        _theme.ThemeChanged += OnApplicationThemeChanged;
        _theme.Apply(runtimeSettings.Theme);
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));

        IConfigurationWorkspace workspace = ConfigurationWorkspace.For(
            options.DataRoot,
            _log,
            _party,
            activate: ActivateRuntime);

        var pageCatalog = new PageCatalog(
            workspace,
            _party,
            _runtime,
            _log,
            _settingsStore,
            openSetupWizard: () => ShowSetupWizard(),
            onSettingsSaved: OnSettingsSaved);
        pageCatalog.Validate(NavigationRegistry.Items);
        _pageCatalog = pageCatalog;

        var shellPages = new ShellPages(pageCatalog);
        _window = new MainWindow(shellPages);
        MainWindow = _window;
        _theme.ApplyTo(_window);

        _exit = new ExitCoordinator(() => !_runtime.Engine.IsBusy, AskAboutExit, ShutdownForReal);

        if (!options.NoInput)
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
                Dispatcher.BeginInvoke(() => ShowWindow());
            }
        };

        TrayMenuTheme.Apply(_trayIcon.ContextMenuStrip, Resources);
        RebuildTrayMenu();

        if (!options.Background || firstRun)
        {
            ShowWindow();
        }

        string? startupRegistrationProblem = settingsStore.SynchronizeAutoStart(persistedSettings);
        if (startupRegistrationProblem is not null)
        {
            _log.Warning("Startup registration", startupRegistrationProblem);
        }

        _log.Information("Startup", $"PCMode started with {engine.TargetKeys.Count} configured target(s).");

        if (!persistedSettings.SetupCompleted)
        {
            _log.Information("First run", "Guided setup has not been completed or dismissed; offering it now.");
            Dispatcher.BeginInvoke(() => ShowSetupWizard(SetupWizardWindow.WelcomeStep));
        }
    }

    /// <param name="startStep">
    /// Pass <see cref="SetupWizardWindow.WelcomeStep"/> to always start at the
    /// beginning (used for the automatic first-run offer). Left null, the
    /// step is inferred from what is already configured -- Hardware while no
    /// display layout exists yet, then Spaces while no target exists yet,
    /// otherwise the summary -- the same three-way heuristic legacy uses, so
    /// reopening after an interruption resumes rather than restarts.
    /// </param>
    private void ShowSetupWizard(int? startStep = null)
    {
        if (_runtime is null || _settingsStore is null || _pageCatalog is null)
        {
            return;
        }

        int layoutCount = _runtime.Engine.Providers.OfType<DisplayProvider>().FirstOrDefault()?.LayoutNames.Count ?? 0;
        int targetCount = _runtime.Engine.TargetKeys.Count;

        int resolvedStart = startStep ?? (layoutCount == 0
            ? SetupWizardWindow.HardwareStep
            : targetCount == 0
                ? SetupWizardWindow.SpacesStep
                : SetupWizardWindow.FinishStep);

        var wizard = new SetupWizardWindow(
            _pageCatalog.Create(PageId.Devices),
            _pageCatalog.Create(PageId.Spaces),
            _pageCatalog.Create(PageId.Settings),
            () => new SetupSummary(_runtime.HardwareDiscoverers.Count, layoutCount, _runtime.Engine.TargetKeys.Count),
            MarkSetupCompleted,
            resolvedStart)
        {
            Owner = _window,
        };

        wizard.ShowDialog();
    }

    private void MarkSetupCompleted()
    {
        if (_runtime is null || _settingsStore is null)
        {
            return;
        }

        SettingsSaveOutcome outcome = _settingsStore.MarkSetupCompleted(_runtime.Engine.Catalog);
        if (!outcome.Success)
        {
            _log?.Warning("First run", $"Could not record that Guided setup finished. {outcome.Problem}");
            return;
        }

        _log?.Information("First run", "Guided setup finished or was dismissed; it will not be offered automatically again.");
    }

    /// <summary>Puts the exit question to the user, in the terms that matter.</summary>
    private ExitChoice AskAboutExit()
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
        if (_window is not null)
        {
            _window.AllowClose = true;
        }

        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Information("Shutdown", $"PCMode exiting with code {e.ApplicationExitCode}.");

        _picker?.Dispose();
        _picker = null;

        if (_theme is not null)
        {
            _theme.ThemeChanged -= OnApplicationThemeChanged;
            _theme.Dispose();
            _theme = null;
        }

        _runtime?.Dispose();
        _runtime = null;

        // Also here, because the paths that fail before the runtime is built --
        // an unreadable configuration, a rejected command line -- have already
        // created it. Stopping a mirror twice is a no-op; leaving one running
        // after the process that owns it has gone is not.
        _party?.Dispose();
        _party = null;

        _instance?.Dispose();
        _instance = null;

        if (_trayIcon is not null)
        {
            // Without this the icon lingers in the notification area until
            // something makes Windows repaint it.
            _trayIcon.Visible = false;

            // NotifyIcon.Dispose does not dispose the strip it was given, and
            // the strip owns a window handle of its own.
            ContextMenuStrip? menu = _trayIcon.ContextMenuStrip;
            _trayIcon.ContextMenuStrip = null;
            menu?.Dispose();

            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _applicationIcon?.Dispose();
        _applicationIcon = null;

        base.OnExit(e);
    }

    private void ShowWindow() => _window?.ShowAndActivate();

    /// <summary>
    /// Rebuilds, validates and activates the configuration on disk, and -- only
    /// when that succeeds -- puts the result in front of the tray. This is the
    /// callback every configuration page's save reaches through
    /// <see cref="IConfigurationWorkspace.Apply"/>. Safe to call only from the
    /// UI thread: unlike <see cref="EngineRuntime.Reload"/> itself, this also
    /// touches WinForms tray state.
    /// </summary>
    private RuntimeActivationOutcome ActivateRuntime()
    {
        if (_runtime is null)
        {
            return RuntimeActivationOutcome.Failed("PCMode has not finished starting.");
        }

        RuntimeActivationOutcome outcome = _runtime.Reload();

        if (outcome.Activated)
        {
            RebuildTrayMenu();
        }
        else
        {
            _log?.Warning("Configuration", outcome.Message);
        }

        return outcome;
    }

    private void RebuildTrayMenu()
    {
        if (_trayIcon?.ContextMenuStrip is null || _runtime is null)
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
        if (_runtime is null)
        {
            return;
        }

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
            _log?.Warning("Input", refusal ?? "The picker and hotkeys could not start.");
        }
        else
        {
            _log?.Information(
                "Input",
                $"Picker started with {_picker.Hotkeys.Count} hotkey(s); controller " +
                $"{(settings.ControllerEnabled ? _picker.ControllerLibrary ?? "not available" : "disabled")}.");
        }
    }

    private void OnApplicationThemeChanged(object? sender, EventArgs e)
    {
        if (_trayIcon?.ContextMenuStrip is { } menu)
        {
            TrayMenuTheme.Apply(menu, Resources);
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            _theme?.ApplyTo(window);
        }
    }

    /// <summary>
    /// Reacts to a Settings save with the parts only the composition root
    /// owns: applying the new theme, and restarting the picker so a changed
    /// picker monitor or controller toggle takes effect immediately rather
    /// than at the next launch.
    /// </summary>
    private void OnSettingsSaved(ApplicationSettings settings)
    {
        _theme?.Apply(settings.Theme);

        if (_options is null || _options.NoInput)
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
        if (_runtime is null)
        {
            return;
        }

        try
        {
            TransitionOutcome outcome = await _runtime.Engine.ApplyAsync(targetKey).ConfigureAwait(true);
            _log?.Information("Targets", outcome.Message);
        }
        catch (Exception exception)
        {
            _log?.Error("Targets", $"Could not apply '{targetKey}'.", exception);
        }
        finally
        {
            RebuildTrayMenu();
        }
    }

    /// <summary>
    /// Ends the shell, having first dealt with anything still in flight.
    /// </summary>
    private async Task RequestExitAsync()
    {
        if (_exit is null)
        {
            Shutdown();
            return;
        }

        await _exit.RequestExitAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// The whole exception chain, outermost first.
    /// </summary>
    private static string Describe(Exception exception)
    {
        var lines = new List<string>();

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            lines.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(Environment.NewLine + Environment.NewLine, lines);
    }
}

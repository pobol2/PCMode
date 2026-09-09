using System.Windows;
using PCMode.Tray.Platform.Logging;
using PCMode.Tray.Platform.Startup;

namespace PCMode.Tray;

/// <summary>
/// The application's WPF entry point. Owns process-wide exception handlers and
/// the top-level startup/shutdown sequence; every other piece of the running
/// application -- the engine, the services, the shell -- is built and owned by
/// <see cref="CompositionRoot"/>.
/// </summary>
public partial class App : System.Windows.Application
{
    private SingleInstance? _instance;
    private LocalLog? _log;
    private CompositionRoot? _root;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
            _log?.Error("Unhandled", "An unhandled dispatcher exception terminated an operation.", args.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _log?.Error(
                "Unhandled",
                "An unhandled application exception occurred.",
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString())
            );

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
                MessageBoxImage.Error
            );

            Shutdown(1);
        }
    }

    private void Start(StartupEventArgs e)
    {
        TrayOptions options = TrayOptions.Parse(e.Args);

        if (!options.IsValid)
        {
            System.Windows.MessageBox.Show(
                $"PCMode could not read its command line.\n\n{options.Error}",
                "PCMode",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );

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

        _instance.ActivationRequested += (_, _) => Dispatcher.BeginInvoke(() => _root?.ShowWindow());

        string resolvedRoot = options.DataRoot ?? PCMode.Engine.PCModePaths.DefaultRoot;

        // Whether the setup wizard is offered is tracked separately, by
        // ApplicationSettings.SetupCompleted, so it can be dismissed without
        // reappearing even though the configuration file now exists. This
        // only decides whether the main window is force-shown so the wizard
        // it is about to open (below) is not opened over a hidden window.
        bool firstRun = !System.IO.File.Exists(PCMode.Engine.PCModePaths.ConfigurationFile(resolvedRoot));

        _log = new LocalLog(resolvedRoot);
        _log.Information("Startup", $"PCMode starting. Data root: {resolvedRoot}");

        try
        {
            _root = CompositionRoot.Build(options, firstRun, _log, Resources, Shutdown);
        }
        catch (TrayStartupException exception)
        {
            System.Windows.MessageBox.Show(exception.Message, "PCMode", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        MainWindow = _root.Window;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Information("Shutdown", $"PCMode exiting with code {e.ApplicationExitCode}.");

        _root?.Dispose();
        _root = null;

        _instance?.Dispose();
        _instance = null;

        base.OnExit(e);
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

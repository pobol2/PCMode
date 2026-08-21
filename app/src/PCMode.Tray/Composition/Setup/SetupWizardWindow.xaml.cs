using System.Windows;
using PCMode.Tray.Navigation;

namespace PCMode.Tray.Setup;

/// <summary>Counts shown on the finish step; all read from already-loaded engine state.</summary>
public sealed record SetupSummary(int HardwareCategories, int Layouts, int Targets);

/// <summary>
/// A short, resumable first-run wizard built entirely out of the same pages
/// and view models Hardware, Spaces and Settings already use. It saves
/// nothing of its own except, on Finish or Skip, the one
/// 'application.setup.completed' flag -- every other change goes through the
/// embedded pages' own existing save paths, exactly as if they had been
/// opened from the main window.
/// </summary>
/// <remarks>
/// Mirrors legacy's three-way resume heuristic (Hardware while no display
/// layout is configured yet, then Spaces while no target is configured yet,
/// otherwise straight to the summary) -- see <see cref="App.ShowSetupWizard"/>.
///
/// The embedded pages are fresh instances from <c>PageCatalog</c>, not the
/// ones cached for the main window -- a page cannot have two visual parents
/// at once. Because they bypass <see cref="ShellPages"/>, this window calls
/// <see cref="IPageLifecycle.OnActivated"/>/<see cref="IPageLifecycle.OnDeactivated"/>
/// on each step change itself, the same contract <see cref="ShellPages.Activate"/>
/// enforces for ordinary navigation.
/// </remarks>
public partial class SetupWizardWindow : Window
{
    internal const int WelcomeStep = 0;
    internal const int HardwareStep = 1;
    internal const int SpacesStep = 2;
    internal const int SettingsStep = 3;
    internal const int FinishStep = 4;

    private static readonly string[] StepNames = ["Welcome", "Devices", "Spaces", "Settings", "Finish"];

    private readonly FrameworkElement _hardwarePage;
    private readonly FrameworkElement _spacesPage;
    private readonly FrameworkElement? _settingsPage;
    private readonly Func<SetupSummary> _summarize;
    private readonly Action _markCompleted;
    private int _step;
    private FrameworkElement? _activeEmbeddedPage;

    public SetupWizardWindow(
        FrameworkElement hardwarePage,
        FrameworkElement spacesPage,
        FrameworkElement? settingsPage,
        Func<SetupSummary> summarize,
        Action markCompleted,
        int startStep = WelcomeStep)
    {
        ArgumentNullException.ThrowIfNull(hardwarePage);
        ArgumentNullException.ThrowIfNull(spacesPage);
        ArgumentNullException.ThrowIfNull(summarize);
        ArgumentNullException.ThrowIfNull(markCompleted);

        _hardwarePage = hardwarePage;
        _spacesPage = spacesPage;
        _settingsPage = settingsPage;
        _summarize = summarize;
        _markCompleted = markCompleted;
        _step = Math.Clamp(startStep, WelcomeStep, FinishStep);

        InitializeComponent();
        ShowStep();
    }

    private void ShowStep()
    {
        if (_step == SettingsStep && _settingsPage is null)
        {
            // Settings could not be built (e.g. runtime not ready); nothing
            // meaningful to skip past it for.
            _step = FinishStep;
        }

        StepTitle.Text = $"Guided setup - {StepNames[_step]}";
        BackButton.IsEnabled = _step > WelcomeStep;
        SkipSetupButton.Visibility = _step == FinishStep ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = _step == FinishStep ? "Finish" : "Next";

        WelcomeContent.Visibility = _step == WelcomeStep ? Visibility.Visible : Visibility.Collapsed;
        FinishContent.Visibility = _step == FinishStep ? Visibility.Visible : Visibility.Collapsed;
        PageHost.Visibility = _step is HardwareStep or SpacesStep or SettingsStep ? Visibility.Visible : Visibility.Collapsed;

        FrameworkElement? nextEmbedded = _step switch
        {
            HardwareStep => _hardwarePage,
            SpacesStep => _spacesPage,
            SettingsStep => _settingsPage,
            _ => null,
        };

        ActivateEmbedded(nextEmbedded);
        PageHost.Content = nextEmbedded;

        if (_step == FinishStep)
        {
            SetupSummary summary = _summarize();
            SummaryText.Text =
                $"Hardware categories detected: {summary.HardwareCategories}\n" +
                $"Display layouts configured: {summary.Layouts}\n" +
                $"Targets configured: {summary.Targets}" +
                (summary.Targets == 0 ? "\n\nNothing is configured yet -- reopen Guided setup from Settings when ready." : string.Empty);
        }
    }

    /// <summary>Mirrors <see cref="ShellPages.Activate"/> for the embedded pages this window owns directly.</summary>
    private void ActivateEmbedded(FrameworkElement? next)
    {
        if (ReferenceEquals(_activeEmbeddedPage, next))
        {
            return;
        }

        if (_activeEmbeddedPage is IPageLifecycle previous)
        {
            previous.OnDeactivated();
        }

        _activeEmbeddedPage = next;

        if (next is IPageLifecycle current)
        {
            current.OnActivated();
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_step > WelcomeStep)
        {
            _step--;
            ShowStep();
        }
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_step == FinishStep)
        {
            _markCompleted();
            Close();
            return;
        }

        _step++;
        ShowStep();
    }

    private void OnSkipSetupClick(object sender, RoutedEventArgs e)
    {
        _markCompleted();
        Close();
    }
}

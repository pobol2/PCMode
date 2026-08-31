using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

// WinForms is enabled for the tray's notify icon, so these arrive ambiguous.
using Application = System.Windows.Application;
using Size = System.Windows.Size;

namespace PCMode.Tray.Tests;

/// <summary>
/// The one WPF host these tests use, for the few of them that genuinely need
/// one.
/// </summary>
/// <remarks>
/// Most of this suite is ordinary unit tests, like every other project here.
/// WPF is needed only where the thing under test is WPF: applying a template,
/// resolving a resource, running a layout pass. Those cases have two hard
/// constraints -- WPF requires STA, and <see cref="Application"/> refuses to be
/// constructed twice in one process -- so there is exactly one STA thread and
/// one Application for the life of the test run, and <see cref="Run"/>
/// marshals onto it.
///
/// The Application is a plain one holding the tray's dictionaries, not
/// <c>PCMode.Tray.App</c> itself: nothing here wants the application's startup
/// behaviour, only its resources.
///
/// Every caller belongs to <see cref="WpfCollection"/>, which stops these
/// interleaving with each other; the shared thread does not need protecting
/// from itself, since its message loop runs one body at a time.
/// </remarks>
internal static class Wpf
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly Lazy<Dispatcher> Host = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Runs <paramref name="body"/> on the WPF thread and rethrows whatever it threw.</summary>
    public static void Run(Action body, TimeSpan? timeout = null)
    {
        Dispatcher dispatcher = Host.Value;
        ExceptionDispatchInfo? failure = null;

        DispatcherOperation operation = dispatcher.BeginInvoke(() =>
        {
            try
            {
                body();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        });

        TimeSpan limit = timeout ?? DefaultTimeout;
        DispatcherOperationStatus status = operation.Wait(limit);

        Assert.True(
            status == DispatcherOperationStatus.Completed,
            $"The test body was still running on the WPF thread after {limit.TotalSeconds:0} seconds.");

        failure?.Throw();
    }

    /// <summary>
    /// Builds <paramref name="element"/> far enough that templates are applied
    /// and data templates have resolved, without showing a window.
    /// </summary>
    public static void Realize(FrameworkElement element)
    {
        element.Measure(new Size(1000, 2000));
        element.Arrange(new Rect(0, 0, 1000, 2000));
        element.UpdateLayout();
    }

    /// <summary>Every element of type <typeparamref name="T"/> beneath <paramref name="root"/>.</summary>
    public static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);

            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim(initialState: false);

        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;

            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            foreach (string source in Dictionaries)
            {
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(source, UriKind.Relative),
                });
            }

            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "PCMode.Tray.Tests WPF host",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(ready.Wait(DefaultTimeout), "The WPF host thread never finished starting.");
        return dispatcher!;
    }

    /// <summary>
    /// What App.xaml merges, plus the fixture templates. Kept in step with
    /// App.xaml by <c>ApplicationResourcesTests</c>, which fails if the two
    /// lists diverge.
    /// </summary>
    private static readonly string[] Dictionaries =
    [
        "/PCMode.Tray;component/UI/Components/Theme/Theme.xaml",
        "/PCMode.Tray;component/UI/Framework/Framework.xaml",
        "/PCMode.Tray;component/UI/Tree/Application/MainWindowRegion/AppShell/MainContentRegion/PageLayoutResources.xaml",
        "/PCMode.Tray;component/UI/Components/Components.xaml",
        "/PCMode.Tray;component/UI/Components/Migration/MigrationComponents.xaml",
        "/PCMode.Tray;component/UI/Tree/Application/ApplicationLayout.xaml",
        "/PCMode.Tray;component/UI/Tree/Application/MainWindowRegion/AppShell/AppShellLayout.xaml",
        "/PCMode.Tray;component/UI/Tree/Application/MainWindowRegion/AppShell/NavigationRegion/PageNavigation/PageNavigationLayout.xaml",
        "/PCMode.Tray;component/UI/Tree/Application/MainWindowRegion/AppShell/MainContentRegion/ListPage/ListPageLayout.xaml",
        "/PCMode.Tray;component/UI/Tree/Application/MainWindowRegion/AppShell/MainContentRegion/ConfigurationPage/ConfigurationPageLayout.xaml",
        "/PCMode.Tray;component/UI/Tree/Application/MainWindowRegion/AppShell/MainContentRegion/ConfigurationPage/SelectorRegion/ConfigurationSelectorLayout.xaml",
        "/PCMode.Tray;component/UI/Tree/Application/MainWindowRegion/AppShell/MainContentRegion/ConfigurationPage/EditorRegion/ConfigurationEditorLayout.xaml",
        "/PCMode.Tray;component/UI/Tree/Application/MainWindowRegion/AppShell/MainContentRegion/ConfigurationPage/EditorRegion/EditorSectionLayout.xaml",
        "/PCMode.Tray;component/UI/Tree/Application/ApplicationBindings.xaml",
        "/PCMode.Tray;component/UI/Tree/Application/MainWindowRegion/AppShell/NavigationRegion/PageNavigation/PageNavigationBindings.xaml",
        "/PCMode.Tray;component/UI/Tree/Application/MainWindowRegion/AppShell/MainContentRegion/ListPage/ListPageBindings.xaml",
        "/PCMode.Tray;component/UI/Tree/Application/MainWindowRegion/AppShell/MainContentRegion/ConfigurationPage/ConfigurationPageBindings.xaml",
        "/PCMode.Tray;component/UI/Components/Theme/Light.xaml",
        "/PCMode.Tray;component/UI/Components/Theme/Dark.xaml",

        "/PCMode.Tray.Tests;component/UI/Construction/Fixtures/BlockTemplates.xaml",
    ];

    /// <summary>The application dictionaries, for the test that keeps this list honest.</summary>
    public static IReadOnlyList<string> ApplicationDictionaries =>
        [.. Dictionaries.Where(source => source.StartsWith("/PCMode.Tray;", StringComparison.Ordinal))];
}

/// <summary>
/// Every test that touches WPF, serialized against every other one.
/// See <see cref="Wpf"/> for why.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfCollection
{
    public const string Name = "WPF";
}

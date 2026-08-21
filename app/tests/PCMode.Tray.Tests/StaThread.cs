using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

namespace PCMode.Tray.Tests;

/// <summary>
/// Runs WPF page construction on one persistent STA thread, with a real
/// <see cref="System.Windows.Application"/> and Target's own merged resource
/// dictionaries loaded -- exactly what <c>App.xaml</c> loads at startup.
/// </summary>
/// <remarks>
/// Two constraints collide here. WPF requires STA, and xunit gives no
/// guarantee about the apartment of the thread a test runs on, so page
/// construction has to happen on a dedicated thread. But
/// <see cref="System.Windows.Application"/> refuses to be constructed twice in
/// one process even after a prior instance has shut down, and without a real
/// one, StaticResource lookups that reach application scope -- which every
/// page-family shell's control template does, for example
/// <c>EditorContextMenuStyle</c> from <c>ConfigurationPageLayout.xaml</c> --
/// throw instead of the page silently looking wrong.
///
/// So there is exactly one STA thread and one Application for the life of the
/// test process, built the first time <see cref="Run"/> is called, and every
/// call after that marshals its body onto the same thread's Dispatcher rather
/// than spinning up a new one. That also keeps every constructed page's
/// resource lookups on the same thread that owns them -- WPF's dispatcher
/// objects have thread affinity, so building a page on a second STA thread
/// while the Application lives on the first would fail a different way.
///
/// Every caller also belongs to <see cref="WpfWindowConstructionCollection"/>,
/// which keeps this project's WPF-resource tests from interleaving with each
/// other; the shared thread here does not need protecting from itself, since
/// its own message loop already runs one body at a time.
/// </remarks>
internal static class StaThread
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly Lazy<Dispatcher> Host = new(StartHost, LazyThreadSafetyMode.ExecutionAndPublication);

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
        System.Windows.Threading.DispatcherOperationStatus status = operation.Wait(limit);

        Assert.True(
            status == System.Windows.Threading.DispatcherOperationStatus.Completed,
            $"The test body was still running on the WPF thread after {limit.TotalSeconds:0} seconds.");
        failure?.Throw();
    }

    private static Dispatcher StartHost()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim(initialState: false);

        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;

            // The real application object, so Styles.xaml and the shared
            // page-shell/layout dictionaries resolve the way they do in the
            // shipped app. Without one, a StaticResource lookup that reaches
            // application scope finds nothing and construction throws.
            var application = new System.Windows.Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };

            foreach (string dictionary in MergedDictionaries)
            {
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"/PCMode.Tray;component/{dictionary}", UriKind.Relative),
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

    private static readonly string[] MergedDictionaries =
    [
        "Framework/Layout/Styles.xaml",
        "Framework/Layout/PageShellLayout.xaml",
        "Framework/Layout/ComponentLayout.xaml",
        "Framework/Layout/ConfigurationPageLayout.xaml",
    ];
}

/// <summary>
/// Every test class that builds a real WPF page or loads its resources,
/// serialized against every other one. See <see cref="StaThread"/> for why.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfWindowConstructionCollection
{
    public const string Name = "WPF window construction";
}

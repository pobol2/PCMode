using System.Windows.Threading;
using PCMode.Tray.Controls.PageShells;

namespace PCMode.Tray.Tests;

/// <summary>
/// <see cref="ConfigurationPageShell.EnableLiveRefresh"/>'s contract -- the
/// shared opt-in timer <c>LayoutsPage</c> and <c>TelevisionPage</c> both rely
/// on instead of each owning a <see cref="DispatcherTimer"/> directly.
/// </summary>
[Collection(WpfWindowConstructionCollection.Name)]
public sealed class ConfigurationPageShellLiveRefreshTests
{
    [Fact]
    public void Live_refresh_ticks_only_while_the_page_is_activated()
    {
        StaThread.Run(() =>
        {
            int ticks = 0;
            var page = new LiveRefreshTestPage(TimeSpan.FromMilliseconds(20), () => ticks++);

            page.OnActivated();
            PumpFor(TimeSpan.FromMilliseconds(200));
            Assert.True(ticks > 0, "Expected at least one tick while activated.");

            int afterActivation = ticks;
            page.OnDeactivated();
            PumpFor(TimeSpan.FromMilliseconds(200));
            Assert.Equal(afterActivation, ticks);
        });
    }

    [Fact]
    public void A_page_that_never_enables_live_refresh_activates_and_deactivates_without_error()
    {
        StaThread.Run(() =>
        {
            var page = new NoLiveRefreshTestPage();

            page.OnActivated();
            page.OnDeactivated();
        });
    }

    /// <summary>Pumps the dispatcher's own message loop for <paramref name="duration"/>.</summary>
    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };

        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class LiveRefreshTestPage : ConfigurationPageShell
    {
        public LiveRefreshTestPage(TimeSpan interval, Action onTick)
        {
            EnableLiveRefresh(interval, () =>
            {
                onTick();
                return Task.CompletedTask;
            });
        }
    }

    private sealed class NoLiveRefreshTestPage : ConfigurationPageShell
    {
    }
}

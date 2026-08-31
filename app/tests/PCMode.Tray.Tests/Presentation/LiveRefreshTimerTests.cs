using System.Windows.Threading;
using PCMode.Tray.Elements;

namespace PCMode.Tray.Tests.Presentation;

/// <summary>
/// <see cref="LiveRefreshTimer"/>'s contract: the shared opt-in tick a page's
/// view model runs only while its page is on screen.
/// </summary>
/// <remarks>
/// Start and Stop are driven by <see cref="IPageLifecycle"/>, so what matters
/// is that a stopped timer is genuinely silent and that a slow tick is never
/// overlapped by the next one -- a poll of live machine state that re-entered
/// itself would queue work faster than it completed.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class LiveRefreshTimerTests
{
    [Fact]
    public void It_ticks_only_between_start_and_stop()
    {
        Wpf.Run(() =>
        {
            int ticks = 0;
            var timer = new LiveRefreshTimer(TimeSpan.FromMilliseconds(20), () =>
            {
                ticks++;
                return Task.CompletedTask;
            });

            timer.Start();
            PumpFor(TimeSpan.FromMilliseconds(200));
            Assert.True(ticks > 0, "Expected at least one tick while started.");

            int afterStart = ticks;
            timer.Stop();
            PumpFor(TimeSpan.FromMilliseconds(200));
            Assert.Equal(afterStart, ticks);
        });
    }

    [Fact]
    public void Stopping_a_timer_that_never_started_is_harmless()
    {
        Wpf.Run(() =>
        {
            var timer = new LiveRefreshTimer(TimeSpan.FromMilliseconds(20), () => Task.CompletedTask);

            timer.Stop();
            timer.Start();
            timer.Stop();
        });
    }

    [Fact]
    public void A_slow_tick_is_never_overlapped_by_the_next_one()
    {
        Wpf.Run(() =>
        {
            int active = 0;
            int maximumActive = 0;
            int calls = 0;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var timer = new LiveRefreshTimer(TimeSpan.FromMilliseconds(20), async () =>
            {
                calls++;
                int nowActive = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, nowActive);
                entered.TrySetResult();

                try
                {
                    await release.Task;
                }
                finally
                {
                    _ = Interlocked.Decrement(ref active);
                }
            });

            timer.Start();
            PumpFor(TimeSpan.FromMilliseconds(150));

            Assert.True(entered.Task.IsCompleted);
            Assert.Equal(1, calls);
            Assert.Equal(1, maximumActive);

            timer.Stop();
            release.SetResult();
            PumpFor(TimeSpan.FromMilliseconds(50));

            Assert.Equal(1, calls);
            Assert.Equal(0, active);
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
}

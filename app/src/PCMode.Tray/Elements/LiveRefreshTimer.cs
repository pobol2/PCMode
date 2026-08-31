using System.Windows.Threading;

namespace PCMode.Tray.Elements;

/// <summary>
/// A <see cref="DispatcherTimer"/> wrapper for a view model that refreshes live
/// machine state while its page is on screen. Start/Stop are driven by the
/// view model's <see cref="IPageLifecycle"/>, so it only ever ticks while that
/// page is the one being shown.
/// </summary>
public sealed class LiveRefreshTimer
{
    private readonly DispatcherTimer _timer;
    private readonly Func<Task> _tick;
    private bool _isTicking;

    public LiveRefreshTimer(TimeSpan interval, Func<Task> tick)
    {
        _tick = tick;
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += OnTick;
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_isTicking)
        {
            return;
        }

        _isTicking = true;

        try
        {
            await _tick().ConfigureAwait(true);
        }
        finally
        {
            _isTicking = false;
        }
    }
}

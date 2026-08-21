using System.Windows.Threading;

namespace PCMode.Tray.Controls.PageShells;

/// <summary>
/// A <see cref="DispatcherTimer"/> wrapper shared by <see cref="ConfigurationPageShell"/>
/// and <see cref="ListPageShell"/> so neither shell owns raw timer plumbing directly.
/// Start/Stop are driven by the owning shell's <c>OnActivated</c>/<c>OnDeactivated</c>,
/// so it only ever ticks while its page is the one on screen.
/// </summary>
internal sealed class LiveRefreshTimer
{
    private readonly DispatcherTimer _timer;
    private readonly Func<Task> _tick;

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
        await _tick().ConfigureAwait(true);
    }
}

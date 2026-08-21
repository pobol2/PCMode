using System.Windows;
using PCMode.Tray.Navigation;

namespace PCMode.Tray.Controls.PageShells;

/// <summary>Standard scrolling page family.</summary>
public class ListPageShell : CommonPageShell, IPageLifecycle
{
    private LiveRefreshTimer? _liveRefreshTimer;

    public ListPageShell()
    {
        SetResourceReference(
            StyleProperty,
            typeof(ListPageShell));
    }

    public virtual void OnActivated()
    {
        _liveRefreshTimer?.Start();
    }

    public virtual void OnDeactivated()
    {
        _liveRefreshTimer?.Stop();
    }

    /// <summary>
    /// Opts this page into a tick that runs only while it is the active page --
    /// started in <see cref="OnActivated"/>, stopped in <see cref="OnDeactivated"/>.
    /// Call once, from the page's constructor. Mirrors
    /// <see cref="ConfigurationPageShell.EnableLiveRefresh"/> for pages with no
    /// unsaved-edit state to protect.
    /// </summary>
    protected void EnableLiveRefresh(TimeSpan interval, Func<Task> tick)
    {
        _liveRefreshTimer = new LiveRefreshTimer(interval, tick);
    }
}

using System.Windows;

namespace PCMode.Tray.Navigation;

/// <summary>
/// Owns the lifetime of navigable pages. Pages are created lazily and reused.
/// </summary>
public sealed class ShellPages
{
    private readonly PageCatalog _catalog;
    private readonly Dictionary<PageId, FrameworkElement> _cache = [];
    private FrameworkElement? _activePage;

    public ShellPages(PageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    public FrameworkElement Get(PageId page)
    {
        if (_cache.TryGetValue(page, out var existing))
            return existing;

        var created = _catalog.Create(page);
        _cache.Add(page, created);
        return created;
    }

    public FrameworkElement Activate(PageId page)
    {
        FrameworkElement next = Get(page);

        if (ReferenceEquals(_activePage, next))
        {
            return next;
        }

        if (_activePage is IPageLifecycle previousLifecycle)
        {
            previousLifecycle.OnDeactivated();
        }

        _activePage = next;

        if (next is IPageLifecycle nextLifecycle)
        {
            nextLifecycle.OnActivated();
        }

        return next;
    }
}

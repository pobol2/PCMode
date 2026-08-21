using PCMode.Tray.Presentation;

namespace PCMode.Tray.Navigation;

/// <summary>
/// One row in the navigation list's flat <see cref="System.Windows.Controls.ItemsControl.ItemsSource"/>,
/// wrapping a <see cref="NavigationItem"/> with the mutable expand/collapse
/// state <see cref="NavigationRegistry"/>'s immutable records can't carry.
/// </summary>
/// <remarks>
/// A child's <see cref="IsVisible"/> tracks its parent entry's
/// <see cref="IsExpanded"/> live, so collapsing a parent hides its children
/// without rebuilding the list.
/// </remarks>
public sealed class NavigationEntry : ObservableObject
{
    private readonly NavigationEntry? _parent;
    private bool _isExpanded;

    public NavigationEntry(NavigationItem item, NavigationEntry? parent, bool hasChildren)
    {
        ArgumentNullException.ThrowIfNull(item);

        Item = item;
        _parent = parent;
        HasChildren = hasChildren;

        if (_parent is not null)
        {
            _parent.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(IsExpanded))
                {
                    OnPropertyChanged(nameof(IsVisible));
                }
            };
        }
    }

    public NavigationItem Item { get; }

    public PageId Page => Item.Page;

    public string Label => Item.Label;

    public bool IsChild => Item.IsChild;

    /// <summary>Whether this entry is a parent with an expand/collapse toggle.</summary>
    public bool HasChildren { get; }

    /// <summary>Only meaningful when <see cref="HasChildren"/> is true.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>False for a child entry whose parent is collapsed; true otherwise.</summary>
    public bool IsVisible => _parent is null || _parent.IsExpanded;

    /// <summary>Builds one flat, ordered entry per <paramref name="items"/>, wiring each child to its parent entry.</summary>
    public static IReadOnlyList<NavigationEntry> Build(IEnumerable<NavigationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        NavigationItem[] all = items.ToArray();
        var entries = new List<NavigationEntry>(all.Length);
        var byPage = new Dictionary<PageId, NavigationEntry>();

        foreach (NavigationItem item in all)
        {
            NavigationEntry? parent = item.Parent is PageId parentPage && byPage.TryGetValue(parentPage, out NavigationEntry? parentEntry)
                ? parentEntry
                : null;

            bool hasChildren = all.Any(other => other.Parent == item.Page);

            var entry = new NavigationEntry(item, parent, hasChildren);
            entries.Add(entry);
            byPage[item.Page] = entry;
        }

        return entries;
    }
}

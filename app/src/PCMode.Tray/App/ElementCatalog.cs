using PCMode.Tray.Elements.Navigation;

namespace PCMode.Tray;

/// <summary>
/// App-owned composition of the application Elements, keyed by the navigation
/// identity that will activate them.
/// </summary>
/// <remarks>
/// The catalog owns no UI structure. It keeps construction and coverage in
/// App while the values remain application-specific state and behaviour from
/// Elements. UI/Tree never depends on this type or on any Element type.
/// </remarks>
public sealed class ElementCatalog
{
    private readonly IReadOnlyDictionary<PageId, object> _elements;

    public ElementCatalog(IEnumerable<KeyValuePair<PageId, object>> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);

        var composed = new Dictionary<PageId, object>();

        foreach ((PageId page, object? element) in elements)
        {
            ArgumentNullException.ThrowIfNull(element);

            if (!composed.TryAdd(page, element))
            {
                throw new InvalidOperationException(
                    $"Application composition contains more than one Element for '{page}'."
                );
            }
        }

        _elements = composed;
    }

    public IReadOnlyDictionary<PageId, object> Elements => _elements;

    public object Get(PageId page) =>
        _elements.TryGetValue(page, out object? element)
            ? element
            : throw new InvalidOperationException($"No application Element is composed for '{page}'.");

    /// <summary>
    /// Ensures navigation and application composition describe the same set
    /// of pages exactly once.
    /// </summary>
    public void Validate(IEnumerable<NavigationItem> navigationItems)
    {
        ArgumentNullException.ThrowIfNull(navigationItems);

        NavigationItem[] items = navigationItems.ToArray();
        NavigationRegistry.Validate(items);

        HashSet<PageId> navigable = items.Select(item => item.Page).ToHashSet();
        var errors = new List<string>();

        foreach (PageId missing in navigable.Except(_elements.Keys).Order())
        {
            errors.Add($"Page '{missing}' is navigable but has no composed Element.");
        }

        foreach (PageId unreachable in _elements.Keys.Except(navigable).Order())
        {
            errors.Add($"Element '{unreachable}' is composed but is not navigable.");
        }

        NavigationValidation.Report(errors);
    }
}

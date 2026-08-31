using PCMode.Tray.Elements;

namespace PCMode.Tray.Elements.Navigation;

public enum PageId
{
    Home,
    Applications,
    Audio,
    Rgb,
    Displays,
    Spaces,
    Settings,
    Diagnostics
}

public sealed record NavigationItem(
    PageId Page,
    string Label,
    PageId? Parent = null)
{
    public bool IsChild => Parent is not null;
}

public static class NavigationRegistry
{
    public static IReadOnlyList<NavigationItem> Items { get; } =
    [
        new(PageId.Home, "Home"),
        new(PageId.Spaces, "Spaces"),
        new(PageId.Displays, "Displays", PageId.Spaces),
        new(PageId.Audio, "Audio output", PageId.Spaces),
        new(PageId.Rgb, "Case lighting", PageId.Spaces),
        new(PageId.Applications, "Applications", PageId.Spaces),
        new(PageId.Settings, "Settings"),
        new(PageId.Diagnostics, "Diagnostics")
    ];

    /// <summary>
    /// Checks that a navigation list is a well-formed hierarchy: each page
    /// appears once, every parent named exists, and nothing is nested beneath
    /// itself.
    /// </summary>
    /// <remarks>
    /// This is the shell's own invariant and nothing outside the shell can
    /// decide it, so it is checked here rather than by whoever happens to
    /// supply the pages.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The list is not a valid hierarchy.</exception>
    public static void Validate(IEnumerable<NavigationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        NavigationItem[] all = [.. items];
        var errors = new List<string>();

        ValidateDuplicatePages(all, errors);
        ValidateParents(all, errors);
        ValidateCycles(all, errors);

        NavigationValidation.Report(errors);
    }

    private static void ValidateDuplicatePages(
        IReadOnlyCollection<NavigationItem> items,
        ICollection<string> errors)
    {
        PageId[] duplicates = items
            .GroupBy(item => item.Page)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(page => page)
            .ToArray();

        foreach (PageId page in duplicates)
        {
            errors.Add($"Page '{page}' appears more than once in navigation.");
        }
    }

    private static void ValidateParents(
        IReadOnlyCollection<NavigationItem> items,
        ICollection<string> errors)
    {
        HashSet<PageId> registeredPages = items.Select(item => item.Page).ToHashSet();

        foreach (NavigationItem item in items)
        {
            if (item.Parent is not PageId parent)
            {
                continue;
            }

            if (parent == item.Page)
            {
                errors.Add($"Page '{item.Page}' cannot be its own parent.");
                continue;
            }

            if (!registeredPages.Contains(parent))
            {
                errors.Add($"Page '{item.Page}' references missing parent '{parent}'.");
            }
        }
    }

    private static void ValidateCycles(
        IReadOnlyCollection<NavigationItem> items,
        ICollection<string> errors)
    {
        Dictionary<PageId, PageId?> parentByPage =
            items
                .GroupBy(item => item.Page)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().Parent);

        var visited = new HashSet<PageId>();

        foreach (PageId page in parentByPage.Keys)
        {
            if (visited.Contains(page))
            {
                continue;
            }

            var path = new List<PageId>();
            var pathIndexes = new Dictionary<PageId, int>();

            PageId current = page;

            while (true)
            {
                if (pathIndexes.TryGetValue(current, out int cycleStartIndex))
                {
                    PageId[] cycle = path
                        .Skip(cycleStartIndex)
                        .Append(current)
                        .ToArray();

                    errors.Add(
                        "Navigation hierarchy contains a cycle: "
                        + string.Join(" -> ", cycle)
                        + ".");

                    break;
                }

                if (visited.Contains(current))
                {
                    break;
                }

                pathIndexes[current] = path.Count;
                path.Add(current);

                if (!parentByPage.TryGetValue(current, out PageId? parent)
                    || parent is null)
                {
                    break;
                }

                if (parent.Value == current)
                {
                    // Already reported by ValidateParents().
                    break;
                }

                if (!parentByPage.ContainsKey(parent.Value))
                {
                    // Already reported as a missing parent.
                    break;
                }

                current = parent.Value;
            }

            foreach (PageId pathPage in path)
            {
                visited.Add(pathPage);
            }
        }
    }
}

/// <summary>How a navigation problem is reported, wherever it was found.</summary>
/// <remarks>
/// Shared so that the shell's hierarchy check and the composition root's
/// page-coverage check fail the same way, with the same shape of message, and
/// a caller only has to recognise one.
/// </remarks>
public static class NavigationValidation
{
    /// <summary>Throws with every problem listed, or returns when there are none.</summary>
    public static void Report(IReadOnlyCollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (errors.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Navigation configuration is invalid:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                errors.Select(error => $" - {error}")));
    }
}

public sealed class NavigationState : ObservableObject
{
    private NavigationEntry? _selectedEntry;

    public NavigationState()
    {
        Items = NavigationEntry.Build(NavigationRegistry.Items);
    }

    public IReadOnlyList<NavigationEntry> Items { get; }

    public NavigationEntry? SelectedEntry
    {
        get => _selectedEntry;
        set => SetProperty(ref _selectedEntry, value);
    }
}

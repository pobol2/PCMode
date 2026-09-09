namespace PCMode.Engine;

/// <summary>What is known about one discovered item.</summary>
/// <remarks>
/// THREE STATES, NOT TWO
///
/// "Not there" and "could not be checked" call for different things from
/// whoever reads a hardware report: a controller with nothing plugged into its
/// slot is an ordinary machine, and Core Audio refusing to answer is a problem
/// worth mentioning. Folding them into one "unavailable" would hide exactly the
/// distinction this window exists to show.
/// </remarks>
public enum HardwareItemStatus
{
    /// <summary>Detected, and as far as this could tell, working.</summary>
    Ok,

    /// <summary>
    /// Ordinarily absent: nothing plugged in, nothing paired, no such endpoint
    /// right now. Not a fault.
    /// </summary>
    NotDetected,

    /// <summary>Something went wrong finding out.</summary>
    Error,
}

/// <summary>One line of technical detail, kept out of the main view.</summary>
public sealed record HardwareDetail(string Label, string Value);

/// <summary>One discovered device or application, in terms a person reads first.</summary>
/// <param name="Name">The friendly name. Never a raw Windows identifier.</param>
/// <param name="Summary">One line describing status, shown next to the name.</param>
/// <param name="Details">
/// Technical identifiers and secondary facts -- a device path, an endpoint ID, a
/// process name -- kept apart from <paramref name="Name"/> and
/// <paramref name="Summary"/> so a UI can show them only when asked.
/// </param>
/// <param name="Identifier">
/// The stable value PCMode's own configuration would key an alias on -- a
/// display's device path, an audio endpoint's name -- or null for a category
/// that has no alias of its own to give, such as a controller slot or a
/// configured application. This is what a caller passes back to
/// <see cref="HardwareConfigurationStore"/>, never something derived from
/// <paramref name="Name"/> or a detail string.
/// </param>
/// <param name="Alias">
/// The PCMode alias already configured for this item, or null when there is
/// none. Null with a non-null <paramref name="Identifier"/> is what "not yet
/// added to PCMode" looks like.
/// </param>
/// <param name="Category">
/// The category this item came from -- <see cref="DisplayDiscoverer.CategoryName"/>
/// or <see cref="AudioOutputDiscoverer.CategoryName"/> for anything with an
/// <paramref name="Identifier"/>, blank otherwise. What a caller adding or
/// renaming an alias uses to know which of <see cref="HardwareConfigurationStore"/>'s
/// two methods applies, without guessing from the item's name.
/// </param>
public sealed record HardwareItem(
    string Name,
    HardwareItemStatus Status,
    string Summary,
    IReadOnlyList<HardwareDetail> Details,
    string? Identifier = null,
    string? Alias = null,
    string Category = ""
)
{
    /// <summary>Whether this item is something PCMode's configuration can name at all.</summary>
    public bool SupportsAlias => Identifier is not null;

    /// <summary>Whether it already has been -- shown vs. offered "Add to PCMode".</summary>
    public bool IsConfigured => Alias is not null;
}

/// <summary>What one category of hardware discovery found, or why it could not look.</summary>
/// <param name="Problem">
/// Set when the whole category could not be inspected -- Core Audio unreachable,
/// the display API refusing a query. Null is the ordinary case, including a
/// category that genuinely found nothing.
/// </param>
public sealed record HardwareCategoryResult(string Category, IReadOnlyList<HardwareItem> Items, string? Problem);

/// <summary>One category of hardware this build knows how to look for.</summary>
/// <remarks>
/// Presentation-neutral, like <see cref="ModeEngine"/>: nothing here knows about
/// a window or a tray icon, which is what lets the same discoverers back a setup
/// wizard, a diagnostics view or a CLI later. A discoverer is free to throw --
/// <see cref="HardwareInspector"/> is what keeps one category's failure from
/// taking the others down with it.
/// </remarks>
public interface IHardwareDiscoverer
{
    /// <summary>What to call this category in a report.</summary>
    string Category { get; }

    /// <summary>Looks for this category's devices. May throw.</summary>
    IReadOnlyList<HardwareItem> Discover();
}

/// <summary>
/// Runs every discoverer, keeping one's failure from being every category's
/// failure.
/// </summary>
public static class HardwareInspector
{
    /// <summary>
    /// Runs each discoverer in turn, in the order given, and reports what each
    /// one found or why it could not look.
    /// </summary>
    public static IReadOnlyList<HardwareCategoryResult> Discover(IReadOnlyList<IHardwareDiscoverer> discoverers)
    {
        ArgumentNullException.ThrowIfNull(discoverers);

        var results = new List<HardwareCategoryResult>(discoverers.Count);

        foreach (IHardwareDiscoverer discoverer in discoverers)
        {
            try
            {
                results.Add(new HardwareCategoryResult(discoverer.Category, discoverer.Discover(), Problem: null));
            }
            catch (Exception exception)
            {
                // A category that could not be inspected is reported as itself,
                // not folded into the categories that could: a broken audio
                // stack must never take the display list down with it.
                results.Add(new HardwareCategoryResult(discoverer.Category, [], exception.Message));
            }
        }

        return results;
    }
}

/// <summary>
/// Tells apart two devices Windows happens to have given the same name.
/// </summary>
/// <remarks>
/// A pair of identical monitors, or two "Speakers" outputs from the same
/// chipset, share a friendly name because that is genuinely what their EDID or
/// driver reports. The technical identifier that actually distinguishes them
/// belongs in a device's details, not its headline, so this only numbers the
/// names that collide -- everything else is left exactly as it was.
/// </remarks>
internal static class HardwareNaming
{
    public static IReadOnlyList<HardwareItem> Disambiguate(IReadOnlyList<HardwareItem> items)
    {
        Dictionary<string, int> counts = items
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<HardwareItem>(items.Count);

        foreach (HardwareItem item in items)
        {
            if (counts[item.Name] <= 1)
            {
                result.Add(item);
                continue;
            }

            int index = seen.TryGetValue(item.Name, out int last) ? last + 1 : 1;
            seen[item.Name] = index;

            result.Add(item with { Name = $"{item.Name} (#{index})" });
        }

        return result;
    }
}

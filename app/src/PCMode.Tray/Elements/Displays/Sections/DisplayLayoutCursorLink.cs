using PCMode.Engine;

namespace PCMode.Tray.Elements.Displays.Sections;

/// <summary>
/// Finds which Space, Activity or Source level(s) currently pair a display
/// layout with a cursor position -- the read half of the "linked cursor
/// position" the Displays page shows alongside a layout.
/// </summary>
/// <remarks>
/// NOT A LAYOUT PROPERTY
///
/// A display layout cannot require a cursor position: <see cref="CursorProvider"/>
/// always runs after the display (<c>CursorProvider.RunsAfter</c>), so a
/// layout requiring the cursor would need it ordered both before and after
/// itself, which <c>TransitionPlan.TryOrder</c> can never satisfy. See
/// <see cref="DisplayLayout"/>'s 'requires' parsing, which refuses 'cursor' by
/// name.
///
/// The association a "linked" cursor position expresses -- this layout, this
/// pointer position -- still exists; it is simply owned by whichever
/// Space/Activity/Source sets 'display' to this layout, exactly the place a
/// mode already sets 'cursor' directly. This class only reads that; writing
/// it back is <see cref="LayoutSectionViewModel.ApplyCursorLink"/>, since only
/// the section that owns the picker needs to write it.
/// </remarks>
internal static class DisplayLayoutCursorLink
{
    /// <summary>
    /// The cursor position named by the first Space/Activity/Source level (in
    /// configuration order) whose 'display' resource is <paramref name="layoutName"/>
    /// and which also sets 'cursor' -- or null when no such level exists.
    /// </summary>
    internal static string? Find(IReadOnlyList<ConfiguredSpace> spaces, string layoutName)
    {
        foreach (IReadOnlyList<ResourceAssignment> resources in Levels(spaces))
        {
            if (!Sets(resources, DisplayProvider.ResourceKey, layoutName))
            {
                continue;
            }

            if (Value(resources, CursorProvider.ResourceKey) is string cursor)
            {
                return cursor;
            }
        }

        return null;
    }

    /// <summary>
    /// Every level whose 'display' resource is <paramref name="layoutName"/>,
    /// as the address <c>HierarchyStore.SaveResources</c> takes -- what
    /// <see cref="LayoutSectionViewModel.ApplyCursorLink"/> writes the linked
    /// position onto.
    /// </summary>
    internal static IReadOnlyList<NodeAddress> AddressesFor(IReadOnlyList<ConfiguredSpace> spaces, string layoutName)
    {
        var found = new List<NodeAddress>();

        foreach (ConfiguredSpace space in spaces)
        {
            if (Sets(space.Resources, DisplayProvider.ResourceKey, layoutName))
            {
                found.Add(new NodeAddress(space.Id));
            }

            foreach (ConfiguredActivity activity in space.Activities)
            {
                if (Sets(activity.Resources, DisplayProvider.ResourceKey, layoutName))
                {
                    found.Add(new NodeAddress(space.Id, activity.Id));
                }

                foreach (ConfiguredSource source in activity.Sources)
                {
                    if (Sets(source.Resources, DisplayProvider.ResourceKey, layoutName))
                    {
                        found.Add(new NodeAddress(space.Id, activity.Id, source.Id));
                    }
                }
            }
        }

        return found;
    }

    private static IEnumerable<IReadOnlyList<ResourceAssignment>> Levels(IReadOnlyList<ConfiguredSpace> spaces)
    {
        foreach (ConfiguredSpace space in spaces)
        {
            yield return space.Resources;

            foreach (ConfiguredActivity activity in space.Activities)
            {
                yield return activity.Resources;

                foreach (ConfiguredSource source in activity.Sources)
                {
                    yield return source.Resources;
                }
            }
        }
    }

    private static bool Sets(IReadOnlyList<ResourceAssignment> resources, string key, string value) =>
        Value(resources, key) is string current && string.Equals(current, value, StringComparison.OrdinalIgnoreCase);

    private static string? Value(IReadOnlyList<ResourceAssignment> resources, string key) =>
        resources.FirstOrDefault(assignment =>
            string.Equals(assignment.Key, key, StringComparison.OrdinalIgnoreCase)) is { Clears: false } found
            ? found.Value
            : null;
}

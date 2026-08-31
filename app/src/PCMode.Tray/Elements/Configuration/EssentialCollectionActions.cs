using PCMode.Tray.Elements;

namespace PCMode.Tray.Elements.Configuration;

/// <summary>
/// The Add/Remove surface for a configuration page's repeated "Essential
/// settings" cards (one card per configured device/route/target). Mirrors
/// <see cref="EntityActions"/> at the collection-item level: the shell owns
/// the Add button's placement and the Remove button already built into
/// <c>EssentialDeviceCard</c>, a page supplies only what adding and removing
/// mean to it.
/// </summary>
public sealed class EssentialCollectionActions
{
    /// <summary>The lowercase singular noun used in the generated Add button label -- "output", "fallback", "controller".</summary>
    public required string ItemNoun { get; init; }

    /// <summary>Adds a new item to the collection. Null hides the Add button entirely.</summary>
    public Action? Add { get; init; }

    /// <summary>Whether <see cref="Add"/> can be used right now.</summary>
    public bool CanAdd { get; init; }

    /// <summary>Whether an existing item can be removed right now.</summary>
    public bool CanRemove { get; init; } = true;

    /// <summary>
    /// Removes the item behind a card's Remove click. The argument is that
    /// card's own <c>DataContext</c> -- the item itself, not a key -- since the
    /// shell only ever sees the row a person clicked Remove on.
    /// </summary>
    public Action<object>? Remove { get; init; }
}

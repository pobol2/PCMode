namespace PCMode.Tray.Elements;

/// <summary>
/// One row in a "Current hardware" inventory collection -- what
/// <c>HardwareInventoryItemTemplate</c> (<c>ComponentLayout.xaml</c>) binds
/// against. A hardware page's discovered-device row implements this so its
/// Current hardware collection can use the one shared card shape instead of
/// re-declaring it.
/// </summary>
public interface IHardwareInventoryRow
{
    /// <summary>The PCMode alias, or the raw underlying device name when not yet named.</summary>
    string FriendlyName { get; }

    /// <summary>The raw underlying device name, shown as a subtitle once it differs from <see cref="FriendlyName"/>.</summary>
    string Name { get; }

    /// <summary>Whether the underlying device name is worth its own line -- only once it has diverged from <see cref="FriendlyName"/>.</summary>
    bool ShowUnderlyingDevice { get; }

    string? BadgeText { get; }

    bool HasBadge { get; }

    /// <summary>"Rename" once named, or the page's own "Add to PCMode" wording otherwise.</summary>
    string ActionLabel { get; }

    /// <summary>Whether the alias action (the card's Actions menu) applies to this row at all.</summary>
    bool CanUseActions { get; }

    /// <summary>Whether this hardware kind supports an Activate affordance at all -- false hides the button entirely, not just disables it.</summary>
    bool ShowActivate { get; }

    /// <summary>Whether Activate can be used right now, when <see cref="ShowActivate"/> is true.</summary>
    bool CanActivate { get; }
}

using PCMode.Tray.Elements;

namespace PCMode.Tray.Elements.Configuration;

/// <summary>
/// A selector-list item whose <see cref="Key"/> can be compared against
/// <see cref="ConfigurationPageViewModelBase.SelectedKey"/> to keep the
/// selector list and the editor in sync.
/// </summary>
public interface IConfigurationSelectorItem
{
    object Key { get; }
}

/// <summary>
/// Optional tree metadata for a configuration selector item. The selector
/// still receives one flat, ordered item source; parent keys and expansion
/// state let the shared shell project the visible rows without knowing the
/// feature represented by those rows.
/// </summary>
public interface IHierarchicalConfigurationSelectorItem : IConfigurationSelectorItem
{
    object? ParentKey { get; }

    double Indent { get; }

    bool HasChildren { get; }

    bool IsExpanded { get; set; }

    string Title { get; }

    string? Summary { get; }

    string? Status { get; }

    bool IsCurrent { get; }

    bool HasCurrentDescendant { get; }
}

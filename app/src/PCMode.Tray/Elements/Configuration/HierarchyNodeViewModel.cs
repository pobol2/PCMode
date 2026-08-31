using System.ComponentModel;
using PCMode.Engine;

namespace PCMode.Tray.Elements.Configuration;

/// <summary>One row of the tree: a Space, an Activity or a Source.</summary>
/// <remarks>
/// The stored hierarchy remains flat and ordered, while this row carries only
/// the small amount of presentation state needed to reveal or hide descendants.
/// Selection and editing still use the same stable <see cref="NodeAddress"/>.
/// </remarks>
public sealed class HierarchyNodeViewModel : INotifyPropertyChanged, IHierarchicalConfigurationSelectorItem
{
    private bool _isExpanded;
    private bool _isCurrent;
    private bool _hasCurrentDescendant;

    public HierarchyNodeViewModel(
        NodeAddress address,
        NodeLevel level,
        string name,
        string displayPath,
        string? targetKey,
        SourceKind? kind,
        bool targetEnabled,
        bool hasChildren = false,
        bool isExpanded = true)
    {
        Address = address;
        Level = level;
        Name = name;
        DisplayPath = displayPath;
        TargetKey = targetKey;
        Kind = kind;
        TargetEnabled = targetEnabled;
        HasChildren = hasChildren;
        _isExpanded = hasChildren && isExpanded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public NodeAddress Address { get; }

    public NodeLevel Level { get; }

    public string Name { get; }

    public string Title => Name;

    public string DisplayPath { get; }

    public string? TargetKey { get; }

    public SourceKind? Kind { get; }

    public bool TargetEnabled { get; }

    /// <summary>Whether this row owns at least one row at the next level.</summary>
    public bool HasChildren { get; }

    /// <summary>Whether the row's descendants are currently shown.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            bool expanded = HasChildren && value;

            if (_isExpanded == expanded)
            {
                return;
            }

            _isExpanded = expanded;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    /// <summary>Left margin, in pixels, for this row's depth.</summary>
    public double Indent => (int)Level * 18d;

    public object? ParentKey => Level switch
    {
        NodeLevel.Activity => new NodeAddress(Address.SpaceId),
        NodeLevel.Source => new NodeAddress(Address.SpaceId, Address.ActivityId),
        _ => null,
    };

    /// <summary>What the row says beside the name.</summary>
    public string Description => Level switch
    {
        NodeLevel.Space => "Space",
        NodeLevel.Activity => "Activity",
        _ => Kind switch
        {
            SourceKind.ExternalDevice => "Source â€” external device",
            SourceKind.TvApplication => "Source â€” TV app",
            _ => "Source â€” PC application",
        },
    };

    public string Summary => Description;

    /// <summary>Whether this row represents a target rather than a Space.</summary>
    public bool IsActivatable => TargetKey is not null;

    /// <summary>Whether to show the "Disabled" marker beside this row.</summary>
    public bool ShowsDisabled => IsActivatable && !TargetEnabled;

    public string? Status => ShowsDisabled ? "Disabled" : null;

    /// <summary>Whether this row is the target the engine currently records as active.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set
        {
            if (_isCurrent == value)
            {
                return;
            }

            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    /// <summary>Whether the current target is nested beneath this row.</summary>
    public bool HasCurrentDescendant
    {
        get => _hasCurrentDescendant;
        internal set
        {
            if (_hasCurrentDescendant == value)
            {
                return;
            }

            _hasCurrentDescendant = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCurrentDescendant)));
        }
    }

    object IConfigurationSelectorItem.Key => Address;

    /// <summary>Whether <paramref name="candidate"/> is nested below <paramref name="ancestor"/>.</summary>
    public static bool IsDescendantOf(HierarchyNodeViewModel candidate, HierarchyNodeViewModel ancestor)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(ancestor);

        if ((int)candidate.Level <= (int)ancestor.Level ||
            !string.Equals(candidate.Address.SpaceId, ancestor.Address.SpaceId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ancestor.Level == NodeLevel.Space ||
            string.Equals(candidate.Address.ActivityId, ancestor.Address.ActivityId, StringComparison.OrdinalIgnoreCase);
    }
}

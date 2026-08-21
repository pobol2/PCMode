using PCMode.Engine;
using PCMode.Tray.Controls.Components;

namespace PCMode.Tray.Features.Hardware;

/// <summary>One discovered device or application, in terms the page can bind to directly.</summary>
public sealed class HardwareItemRow(HardwareItem item)
{
    public string Name => item.Name;

    public string Category => item.Category;

    public string? Identifier => item.Identifier;

    public string? Alias => item.Alias;

    public string Summary => item.Summary;

    public IReadOnlyList<HardwareDetail> Details => item.Details;

    public bool HasDetails => Details.Count > 0;

    public bool IsConfigured => item.IsConfigured;

    public bool SupportsAlias => item.SupportsAlias;

    public string AliasActionLabel => IsConfigured ? "Edit alias" : "Add to PCMode";

    public string DisplayName => IsConfigured ? $"{item.Alias} ({item.Name})" : item.Name;

    public StatusDot.StatusDotState StatusState => item.Status switch
    {
        HardwareItemStatus.Ok => StatusDot.StatusDotState.Ok,
        HardwareItemStatus.Error => StatusDot.StatusDotState.Error,
        _ => StatusDot.StatusDotState.Neutral,
    };
}

/// <summary>What one category of hardware discovery found, in terms the page can bind to directly.</summary>
public sealed class HardwareCategoryRow(HardwareCategoryResult result)
{
    public string Category => result.Category;

    public string? Problem => result.Problem;

    public bool HasProblem => Problem is not null;

    public IReadOnlyList<HardwareItemRow> Items { get; } = [.. result.Items.Select(static i => new HardwareItemRow(i))];
}

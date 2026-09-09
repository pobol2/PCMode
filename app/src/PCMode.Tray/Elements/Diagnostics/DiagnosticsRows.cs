using PCMode.Engine;
using PCMode.Tray.Elements;

namespace PCMode.Tray.Elements.Diagnostics;

/// <summary>One target the Dry run picker can test against.</summary>
public sealed record DryRunTargetRow(string Key, string DisplayPath);

/// <summary>One resource's standing from the last dry run, in terms the page can bind to directly.</summary>
public sealed class ResourceHealthRow(ResourceHealth health)
{
    public string ResourceName => health.ResourceName;

    public string Target => health.Target;

    public string? Current => health.Current;

    public string? Detail => health.Detail;

    public bool Optional => health.Optional;

    public StatusLevel StatusState =>
        health.State switch
        {
            ResourceHealthState.Correct => StatusLevel.Ok,
            ResourceHealthState.Degraded => StatusLevel.Warning,
            ResourceHealthState.Failed => StatusLevel.Error,
            _ => StatusLevel.Neutral,
        };
}

/// <summary>The last dry run's overall result, in terms the page can bind to directly.</summary>
public sealed class ModeHealthRow(ModeHealth health)
{
    public string DisplayName => health.DisplayName;

    public string Summary => health.Summary;

    public StatusLevel StatusState =>
        health.Overall switch
        {
            ResourceHealthState.Correct => StatusLevel.Ok,
            ResourceHealthState.Degraded => StatusLevel.Warning,
            ResourceHealthState.Failed => StatusLevel.Error,
            _ => StatusLevel.Neutral,
        };

    public IReadOnlyList<ResourceHealthRow> Resources { get; } =
    [.. health.Resources.Select(static resource => new ResourceHealthRow(resource))];
}

/// <summary>One loaded provider, in terms the page can bind to directly.</summary>
public sealed record ProviderRow(string DisplayName, string Key)
{
    public string Display => $"{DisplayName} ({Key})";
}

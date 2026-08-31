using PCMode.Engine;
using PCMode.Tray.Elements;

namespace PCMode.Tray.Elements.Rgb;

/// <summary>
/// One OpenRGB controller as the SDK server currently reports it, rendered by
/// the shared <c>HardwareInventoryItemTemplate</c>.
/// </summary>
/// <remarks>
/// Read-only, unlike Audio's and Displays' inventory rows: a controller's
/// identity lives in the engine configuration rather than being named here, so
/// this row has no alias action and nothing to activate.
/// </remarks>
public sealed class DiscoveredRgbControllerRow(
    string name,
    CaseRgbDeviceState state,
    string detail) : IHardwareInventoryRow
{
    public string FriendlyName => name;

    public string Name => detail;

    public bool ShowUnderlyingDevice => !string.IsNullOrWhiteSpace(detail);

    public string? BadgeText => state == CaseRgbDeviceState.Ready ? "Ready" : state.ToString();

    public bool HasBadge => true;

    public string ActionLabel => string.Empty;

    public bool CanUseActions => false;

    public bool ShowActivate => false;

    public bool CanActivate => false;
}

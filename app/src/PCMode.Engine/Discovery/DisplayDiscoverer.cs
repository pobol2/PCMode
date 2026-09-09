using System.Globalization;

namespace PCMode.Engine;

/// <summary>
/// Every monitor Windows has a slot for, active or not, in terms someone
/// inspecting their setup would read.
/// </summary>
/// <remarks>
/// Reuses <see cref="DisplayProvider.Discover"/> rather than <see cref="IDisplayDevices"/>
/// directly, which is what <see cref="DisplayProvider"/> itself already computes
/// -- including which configured alias, if any, a monitor resolves to. A second
/// implementation of that matching rule here would eventually disagree with the
/// one a transition actually uses.
///
/// A monitor with no picture right now is not an error. Windows keeps a slot for
/// every target it has ever detected, including a television that is currently
/// switched off, and that is reported as "not detected" rather than left out or
/// shown as a fault.
/// </remarks>
public sealed class DisplayDiscoverer(DisplayProvider provider) : IHardwareDiscoverer
{
    /// <summary>The category name, shared with the alias-writing side.</summary>
    public const string CategoryName = "Displays";

    private readonly DisplayProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public string Category => CategoryName;

    public IReadOnlyList<HardwareItem> Discover()
    {
        DisplaySnapshot snapshot = _provider.Discover();

        if (!snapshot.Available)
        {
            // Reported as a category failure by HardwareInspector, the same as
            // any other discoverer that could not look at all.
            throw new InvalidOperationException(snapshot.Problem ?? "The displays could not be read.");
        }

        var items = new List<HardwareItem>(snapshot.Monitors.Count);
        int unnamed = 0;

        foreach (DisplayMonitorInfo monitor in snapshot.Monitors)
        {
            string name = string.IsNullOrWhiteSpace(monitor.FriendlyName)
                ? $"Display {++unnamed}"
                : monitor.FriendlyName;

            List<HardwareDetail> details = [new("Device path", monitor.DevicePath)];

            HardwareItemStatus status;
            string summary;

            if (monitor.Active)
            {
                details.Add(new("Resolution", $"{monitor.Width} x {monitor.Height}"));
                details.Add(new("Refresh rate", FormatHz(monitor.RefreshHz)));
                details.Add(new("Position", $"({monitor.X}, {monitor.Y})"));
                details.Add(new("Primary", monitor.Primary ? "Yes" : "No"));

                status = HardwareItemStatus.Ok;
                summary =
                    $"{monitor.Width} x {monitor.Height} @ {FormatHz(monitor.RefreshHz)}"
                    + (monitor.Primary ? ", primary" : string.Empty);
            }
            else
            {
                status = HardwareItemStatus.NotDetected;
                summary = "Not currently showing a picture.";
            }

            items.Add(
                new HardwareItem(name, status, summary, details, monitor.DevicePath, monitor.LogicalName, CategoryName)
            );
        }

        return HardwareNaming.Disambiguate(items);
    }

    private static string FormatHz(double refreshHz) =>
        $"{refreshHz.ToString("0.##", CultureInfo.InvariantCulture)} Hz";
}

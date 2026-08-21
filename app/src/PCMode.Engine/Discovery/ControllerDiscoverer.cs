namespace PCMode.Engine;

/// <summary>What XInput reports about a connected controller.</summary>
public sealed record GamepadCapabilities(string SubType, bool Wireless);

/// <summary>
/// XInput, behind a seam so tests never depend on a real controller being
/// plugged in.
/// </summary>
/// <remarks>
/// The same reason every other native seam in this application exists: a test
/// suite cannot arrange for a controller to be present, absent or wireless on
/// whichever machine happens to be running it, and those are exactly the cases
/// worth exercising.
/// </remarks>
public interface IGamepadCapabilitiesSource
{
    /// <summary>Whether XInput itself was found on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>What is in that slot, or null when nothing answered.</summary>
    GamepadCapabilities? Read(int slot);
}

/// <summary>
/// XInput's four controller slots, in terms someone inspecting their setup
/// would read.
/// </summary>
/// <remarks>
/// Every slot is reported, connected or not, because XInput's four slots are
/// the whole of what a machine can plug a controller into: an empty slot is
/// exactly as informative as a full one to somebody working out why their pad
/// is not being seen. Missing XInput entirely is reported as a single line
/// rather than four, since no slot could then possibly answer.
/// </remarks>
public sealed class ControllerDiscoverer(IGamepadCapabilitiesSource source) : IHardwareDiscoverer
{
    /// <summary>XInput has exactly four slots.</summary>
    public const int SlotCount = 4;

    private readonly IGamepadCapabilitiesSource _source = source ?? throw new ArgumentNullException(nameof(source));

    public string Category => "Controllers";

    public IReadOnlyList<HardwareItem> Discover()
    {
        if (!_source.IsAvailable)
        {
            return
            [
                new HardwareItem(
                    "XInput controllers",
                    HardwareItemStatus.NotDetected,
                    "XInput is not available on this machine.",
                    []),
            ];
        }

        var items = new List<HardwareItem>(SlotCount);

        for (int slot = 0; slot < SlotCount; slot++)
        {
            string name = $"Controller {slot + 1}";
            GamepadCapabilities? capabilities = _source.Read(slot);

            items.Add(capabilities is null
                ? new HardwareItem(
                    name,
                    HardwareItemStatus.NotDetected,
                    "Not connected.",
                    [new("Slot", slot.ToString())])
                : new HardwareItem(
                    name,
                    HardwareItemStatus.Ok,
                    capabilities.Wireless ? $"{capabilities.SubType}, wireless." : $"{capabilities.SubType}.",
                    [
                        new("Slot", slot.ToString()),
                        new("Type", capabilities.SubType),
                        new("Wireless", capabilities.Wireless ? "Yes" : "No"),
                    ]));
        }

        return items;
    }
}

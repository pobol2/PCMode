namespace PCMode.Engine.Tests;

/// <summary>A machine's controllers, made up.</summary>
/// <remarks>
/// The reason <see cref="IGamepadCapabilitiesSource"/> is an interface: no test
/// can arrange for a real Xbox controller to be plugged into slot 2 and nothing
/// else, and that is exactly the kind of case worth exercising.
/// </remarks>
internal sealed class FakeGamepadCapabilitiesSource : IGamepadCapabilitiesSource
{
    private readonly Dictionary<int, GamepadCapabilities> _slots = [];

    public bool IsAvailable { get; set; } = true;

    public FakeGamepadCapabilitiesSource Plug(int slot, string subType, bool wireless = false)
    {
        _slots[slot] = new GamepadCapabilities(subType, wireless);
        return this;
    }

    public GamepadCapabilities? Read(int slot) => _slots.GetValueOrDefault(slot);
}

/// <summary>What the Controllers category reports.</summary>
public sealed class ControllerDiscovererTests
{
    [Fact]
    public void Reports_all_four_slots_whether_or_not_anything_is_plugged_in()
    {
        var source = new FakeGamepadCapabilitiesSource().Plug(0, "Gamepad");

        IReadOnlyList<HardwareItem> items = new ControllerDiscoverer(source).Discover();

        Assert.Equal(4, items.Count);
        Assert.Equal(HardwareItemStatus.Ok, items[0].Status);
        Assert.Equal(HardwareItemStatus.NotDetected, items[1].Status);
        Assert.Equal(HardwareItemStatus.NotDetected, items[2].Status);
        Assert.Equal(HardwareItemStatus.NotDetected, items[3].Status);
    }

    [Fact]
    public void Names_controllers_by_slot_rather_than_a_raw_index()
    {
        var source = new FakeGamepadCapabilitiesSource().Plug(2, "Wheel");

        IReadOnlyList<HardwareItem> items = new ControllerDiscoverer(source).Discover();

        Assert.Equal("Controller 3", items[2].Name);
        Assert.Equal("Wheel.", items[2].Summary);
    }

    [Fact]
    public void Mentions_a_wireless_pad_in_the_summary()
    {
        var source = new FakeGamepadCapabilitiesSource().Plug(0, "Gamepad", wireless: true);

        IReadOnlyList<HardwareItem> items = new ControllerDiscoverer(source).Discover();

        Assert.Equal("Gamepad, wireless.", items[0].Summary);
        Assert.Equal("Yes", items[0].Details.Single(detail => detail.Label == "Wireless").Value);
    }

    [Fact]
    public void An_empty_slot_reads_as_not_connected_rather_than_an_error()
    {
        var source = new FakeGamepadCapabilitiesSource();

        IReadOnlyList<HardwareItem> items = new ControllerDiscoverer(source).Discover();

        Assert.All(items, item => Assert.Equal(HardwareItemStatus.NotDetected, item.Status));
        Assert.All(items, item => Assert.DoesNotContain("error", item.Summary, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void XInput_missing_entirely_is_one_line_rather_than_four()
    {
        var source = new FakeGamepadCapabilitiesSource { IsAvailable = false };

        IReadOnlyList<HardwareItem> items = new ControllerDiscoverer(source).Discover();

        HardwareItem item = Assert.Single(items);
        Assert.Equal(HardwareItemStatus.NotDetected, item.Status);
    }
}

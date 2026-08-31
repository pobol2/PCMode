using PCMode.Engine;
using PCMode.Tray.Elements.Settings;

namespace PCMode.Tray.Tests.Features.Settings;

/// <summary>
/// Settings' controller section: every XInput slot is reported, connected or
/// not, with the detail rows the page shows beside each.
/// </summary>
public sealed class ControllerDiscoveryTests
{
    [Fact]
    public async Task Controller_section_reports_all_xinput_slots_and_connection_details()
    {
        var source = new FakeControllers
        {
            Connected = new GamepadCapabilities("Gamepad", Wireless: true),
        };
        var section = new ControllerDiscoveryViewModel(new FakeWorkspace(), source);

        await section.ScanAsync();

        Assert.Equal(ControllerDiscoverer.SlotCount, section.Controllers.Count);
        ControllerStatusRow connected = section.Controllers[0];
        Assert.Equal("Gamepad, wireless.", connected.Summary);
        Assert.Contains(connected.Details, detail => detail.Label == "Wireless" && detail.Value == "Yes");
        Assert.Equal("1 controller(s) connected.", section.StatusMessage);
    }

    private sealed class FakeControllers : IGamepadCapabilitiesSource
    {
        public bool IsAvailable => true;

        public GamepadCapabilities? Connected { get; init; }

        public GamepadCapabilities? Read(int slot) => slot == 0 ? Connected : null;
    }
}

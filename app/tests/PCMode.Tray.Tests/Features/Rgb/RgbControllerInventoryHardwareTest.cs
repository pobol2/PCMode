using PCMode.Engine;
using PCMode.Tray.Elements.Rgb;
using PCMode.Tray.Tests;

namespace PCMode.Tray.Tests.Features.Rgb;

public sealed class RgbControllerInventoryHardwareFactAttribute : FactAttribute
{
    public RgbControllerInventoryHardwareFactAttribute()
    {
        if (!RgbControllerInventoryHardwareTest.Requested)
        {
            Skip =
                "Hardware check. Set PCMODE_TRAY_RGB_HARDWARE_TEST=1 to run it with the OpenRGB SDK server " +
                "listening on localhost:6742 and at least one controller available.";
        }
    }
}

/// <summary>
/// Exercises the RGB page's shared Current hardware/Rescan path against a real
/// OpenRGB SDK server. It is read-only: no scene is activated and no lighting
/// state is changed.
///
/// Exact command:
///
///   $env:PCMODE_TRAY_RGB_HARDWARE_TEST="1"; dotnet test app/tests/PCMode.Tray.Tests --filter "FullyQualifiedName~RgbControllerInventoryHardwareTest" --logger "console;verbosity=detailed"
/// </summary>
public sealed class RgbControllerInventoryHardwareTest
{
    private const string Switch = "PCMODE_TRAY_RGB_HARDWARE_TEST";

    public static bool Requested =>
        string.Equals(Environment.GetEnvironmentVariable(Switch), "1", StringComparison.Ordinal);

    [RgbControllerInventoryHardwareFact]
    public async Task Rescan_reports_the_first_real_OpenRGB_controller_through_shared_inventory_rows()
    {
        using var root = new TemporaryConfigurationRoot("""
            {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
              {"id":"desktop","name":"Desktop","resources":{}}]}]}
            """);

        ModeSaveOutcome outcome = CaseRgbConfigurationStore.Save(
            root.Path,
            new CaseRgbEdit(true, string.Empty, [new CaseRgbDeviceEdit(0, "Direct")], 4000));
        Assert.True(outcome.Success, outcome.Problem);

        var viewModel = new RgbViewModel(Workspaces.At(root.Path));
        viewModel.Refresh();

        await viewModel.ControllerInventory.ScanAsync();

        Assert.False(viewModel.ControllerInventory.HasProblem, viewModel.ControllerInventory.Problem);
        Assert.Contains(viewModel.ControllerInventory.Rows, row => row.BadgeText == "Ready");
        Assert.Contains(viewModel.ControllerInventory.Rows, row => !string.IsNullOrWhiteSpace(row.FriendlyName));
    }
}

using System.Globalization;

namespace PCMode.Engine.Tests;

/// <summary>
/// A test that is skipped unless the machine was explicitly volunteered.
/// </summary>
/// <remarks>
/// Decided when tests are discovered, before anything in the class is
/// constructed, so an ordinary run never enters it -- see
/// <see cref="HardwareFactAttribute"/> in <c>AudioTargetHardwareTest.cs</c>, which
/// this follows exactly.
/// </remarks>
public sealed class DdcHardwareFactAttribute : FactAttribute
{
    public DdcHardwareFactAttribute()
    {
        if (!DdcHardwareTest.Requested)
        {
            Skip =
                "Hardware check. Set PCMODE_DDC_HARDWARE_TEST=1 to run it: it sends real DDC/CI commands " +
                "to a real monitor and briefly changes its brightness.";
        }
    }
}

/// <summary>
/// The one test that talks DDC/CI to a real monitor.
/// </summary>
/// <remarks>
/// NOT PART OF THE TEST SUITE, AND CANNOT BECOME PART OF IT BY ACCIDENT
///
/// It skips itself unless PCMODE_DDC_HARDWARE_TEST is exactly "1". Every other
/// test in this file fakes <see cref="IDdcMonitors"/>; this one drives
/// <see cref="WindowsDdcMonitors"/> against whatever monitors are actually
/// connected, which nudges a real monitor's brightness visibly.
///
/// Run it with:
///
///   $env:PCMODE_DDC_HARDWARE_TEST="1"; dotnet test app/tests/PCMode.Engine.Tests --filter "FullyQualifiedName~DdcHardwareTest" --logger "console;verbosity=detailed"
///
/// It needs no configuration document: DDC/CI monitors are discovered
/// directly, unlike the audio check, which needs a configured target. It picks
/// the first monitor that answers a capability probe and reports DDC/CI
/// brightness support.
///
/// WHAT IT COVERS, AND WHY BOTH HALVES
///
/// Brightness and input are the two controls the Displays page's DDC/CI
/// section drives against a real screen -- see
/// <c>Features.Displays.Sections.MonitorDdcSectionViewModel</c>. Brightness is
/// checked by writing and reading back; input is only READ, never written,
/// because switching a monitor to an input with nothing on it takes the screen
/// away from whoever is running the test, and there is no reliable way to ask
/// it to come back.
///
/// WHAT IT PUTS BACK
///
/// The monitor's brightness is read before anything is changed and restored
/// afterwards, including when the test fails.
/// </remarks>
public sealed class DdcHardwareTest(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Switch = "PCMODE_DDC_HARDWARE_TEST";

    private readonly Xunit.Abstractions.ITestOutputHelper _output = output;

    /// <summary>
    /// Whether the one environment variable that lets this run is set. Checked
    /// by xunit before the test is constructed -- see
    /// <see cref="DdcHardwareFactAttribute"/>.
    /// </summary>
    public static bool Requested =>
        string.Equals(Environment.GetEnvironmentVariable(Switch), "1", StringComparison.Ordinal);

    [DdcHardwareFact]
    public void Reads_changes_and_restores_brightness_on_a_real_monitor()
    {
        var monitors = new WindowsDdcMonitors();
        TimeSpan probe = TimeSpan.FromSeconds(3);
        TimeSpan command = TimeSpan.FromSeconds(5);

        IReadOnlyList<string> devicePaths = monitors.KnownDevicePaths(probe);
        Say($"Windows enumerated {devicePaths.Count} monitor(s) over DDC/CI.");

        if (devicePaths.Count == 0)
        {
            Say("Diagnosing why: per-monitor handle, stage by stage, with Win32 error codes.");

            foreach (string line in WindowsDdcMonitors.DiagnoseEnumeration())
            {
                Say("  " + line);
            }
        }

        Assert.NotEmpty(devicePaths);

        string? chosen = null;
        uint originalValue = 0;

        foreach (string devicePath in devicePaths)
        {
            DdcCapabilities? capabilities = monitors.ReadCapabilities(devicePath, probe);
            Say($"{devicePath}: capabilities {(capabilities is null ? "did not answer" : Describe(capabilities))}");

            if (capabilities is null || !capabilities.Supports(DdcVcpCode.Brightness))
            {
                continue;
            }

            DdcVcpValue? current = monitors.GetVcpFeature(devicePath, DdcVcpCode.Brightness, probe);

            if (current is null)
            {
                Say($"{devicePath}: reports brightness support but did not answer a read. Trying the next monitor.");
                continue;
            }

            chosen = devicePath;
            originalValue = current.Current;
            break;
        }

        if (chosen is null)
        {
            Assert.Fail(
                "No connected monitor answered both a capability probe and a brightness read over DDC/CI. " +
                "DDC/CI may be disabled in the monitor's own menu, or no monitor here supports it.");
            return;
        }

        Say($"Using {chosen}. Current brightness: {originalValue}.");

        uint probeValue = originalValue >= 50 ? originalValue - 20 : originalValue + 20;

        try
        {
            Say($"Setting brightness to {probeValue}. The panel should visibly change.");
            monitors.SetVcpFeature(chosen, DdcVcpCode.Brightness, probeValue, command);

            DdcVcpValue? after = monitors.GetVcpFeature(chosen, DdcVcpCode.Brightness, probe);

            Say($"Read back: {after?.Current.ToString(CultureInfo.InvariantCulture) ?? "(no answer)"}");

            Assert.NotNull(after);
            Assert.Equal(probeValue, after!.Current);
        }
        finally
        {
            Say($"Restoring brightness to {originalValue}.");
            monitors.SetVcpFeature(chosen, DdcVcpCode.Brightness, originalValue, command);

            DdcVcpValue? restored = monitors.GetVcpFeature(chosen, DdcVcpCode.Brightness, probe);
            Say($"Restored to: {restored?.Current.ToString(CultureInfo.InvariantCulture) ?? "(no answer)"}");
        }

        ReportInputSupport(monitors, chosen, probe);
    }

    /// <summary>
    /// Reads -- and never writes -- the monitor's current input, the other
    /// half of what the Displays page's DDC/CI section offers.
    /// </summary>
    /// <remarks>
    /// Read-only on purpose. Writing an input is exactly as real as writing
    /// brightness, but a wrong guess puts the screen on a socket with nothing
    /// plugged into it, and a monitor showing "no signal" cannot be asked to
    /// come back over the channel that just moved it. Brightness restores
    /// itself; an input does not.
    /// </remarks>
    private void ReportInputSupport(WindowsDdcMonitors monitors, string devicePath, TimeSpan probe)
    {
        DdcCapabilities? capabilities = monitors.ReadCapabilities(devicePath, probe);

        if (capabilities is null || !capabilities.Supports(DdcVcpCode.InputSource))
        {
            Say("This monitor does not report DDC/CI input switching. Nothing to read.");
            return;
        }

        DdcVcpValue? input = monitors.GetVcpFeature(devicePath, DdcVcpCode.InputSource, probe);

        if (input is null)
        {
            Say("This monitor reports input switching but did not answer a read.");
            return;
        }

        string named = DdcInputProvider.KnownInputNames
            .FirstOrDefault(pair => pair.Value == input.Current)
            .Key ?? "not one this build names";

        Say($"Current input: 0x{input.Current:X2} ({named}). Not changed -- see this method's remarks.");
    }

    private static string Describe(DdcCapabilities capabilities) =>
        capabilities.Features.Count == 0
            ? "none of the controls this build knows about"
            : string.Join(
                ", ",
                capabilities.Features.Values.Select(feature =>
                    $"0x{(byte)feature.Code:X2}" +
                    (feature.SupportedValues.Count > 0
                        ? $" ({string.Join(" ", feature.SupportedValues.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)))})"
                        : string.Empty)));

    private void Say(string line)
    {
        _output.WriteLine(line);
        Console.WriteLine(line);
    }
}

using System.Windows.Controls;
using PCMode.Tray.Elements.Audio;
using PCMode.Tray.Tests;
using PCMode.Tray.UI.Components.Migration;
using PCMode.Tray.UI.Framework;
using PCMode.Tray.UI.Tree;
using Button = System.Windows.Controls.Button;

namespace PCMode.Tray.Tests.Features.Audio;

/// <summary>
/// A test that is skipped unless the machine was explicitly volunteered.
/// </summary>
/// <remarks>
/// Decided when tests are discovered, before anything in the class is
/// constructed, so an ordinary run never enters it -- see
/// <c>AudioTargetHardwareTest.HardwareFactAttribute</c> in
/// PCMode.Engine.Tests, which this mirrors for the Tray layer.
/// </remarks>
public sealed class AudioDeviceDiscoveryHardwareFactAttribute : FactAttribute
{
    public AudioDeviceDiscoveryHardwareFactAttribute()
    {
        if (!AudioDeviceDiscoveryHardwareTest.Requested)
        {
            Skip =
                "Hardware check. Set PCMODE_TRAY_AUDIO_HARDWARE_TEST=1 to run it: it reads this "
                + "machine's real Windows audio playback endpoints (no window is opened, nothing is "
                + "clicked, and nothing is changed).";
        }
    }
}

/// <summary>
/// <see cref="AudioDeviceDiscoveryViewModel.Inventory"/> feeds Audio's
/// Current hardware block -- against this machine's real Windows audio
/// endpoints, not the fabricated <c>HardwareCategoryResult</c> every other
/// Audio test substitutes.
/// </summary>
/// <remarks>
/// Read-only hardware enumeration only: it calls the same
/// <c>AudioOutputDiscoverer</c>/Core Audio path the real Audio page's Rescan
/// uses, but never opens a window, sends input, or changes any device or
/// PCMode setting.
///
/// Run it with:
///
///   $env:PCMODE_TRAY_AUDIO_HARDWARE_TEST="1"; dotnet test app/tests/PCMode.Tray.Tests --filter "FullyQualifiedName~AudioDeviceDiscoveryHardwareTest" --logger "console;verbosity=detailed"
/// </remarks>
public sealed class AudioDeviceDiscoveryHardwareTest
{
    private const string Switch = "PCMODE_TRAY_AUDIO_HARDWARE_TEST";

    /// <summary>Whether the one environment variable that lets this run is set.</summary>
    public static bool Requested =>
        string.Equals(Environment.GetEnvironmentVariable(Switch), "1", StringComparison.Ordinal);

    private const string Configuration = """
        {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
          {"id":"desktop","name":"Desktop","resources":{}}]}]}
        """;

    /// <summary>
    /// The same file with one audio target, so the page has something to open
    /// in its editor. The configured alias is deliberately arbitrary: what is
    /// under test is the Current hardware block, whose rows come from a real
    /// Core Audio enumeration and not from anything named here.
    /// </summary>
    private const string ConfigurationWithTarget = """
        {"schemaVersion":1,"providers":{"audio":{
          "devices":{"Configured":"Some configured endpoint"},
          "targets":{"Probe":{"outputs":[
            {"output":"Configured","delayMs":0,"gain":1,"volume":0.5}]}}
        }},"spaces":[{"id":"desk","name":"Desk","activities":[
          {"id":"desktop","name":"Desktop","resources":{}}]}]}
        """;

    [AudioDeviceDiscoveryHardwareFact]
    public async Task Scanning_reports_this_machine_s_real_playback_endpoints_as_rows()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        var viewModel = new AudioDeviceDiscoveryViewModel(Workspaces.At(root.Path), onAliasSaved: () => { });

        await viewModel.Inventory.ScanAsync();

        Assert.False(viewModel.Inventory.HasProblem, viewModel.Inventory.Problem);
        Assert.NotEmpty(viewModel.Inventory.Rows);
        Assert.Contains(viewModel.Inventory.Rows, row => !string.IsNullOrWhiteSpace(row.FriendlyName));
    }

    [AudioDeviceDiscoveryHardwareFact]
    public async Task Restored_audio_page_realizes_with_this_machine_s_playback_endpoints()
    {
        using var root = new TemporaryConfigurationRoot(ConfigurationWithTarget);
        var viewModel = new AudioViewModel(Workspaces.At(root.Path));

        viewModel.Refresh();
        await viewModel.DeviceDiscovery.Inventory.ScanAsync();

        Wpf.Run(() =>
        {
            var host = new ContentControl { Content = viewModel };

            Wpf.Realize(host);

            ConfigurationPageBlock page = Assert.Single(Wpf.Descendants<ConfigurationPageBlock>(host));
            Assert.NotEmpty(Wpf.Descendants<FinalUiComponent>(page));
            Assert.Empty(Wpf.Descendants<MigrationContent>(page));
            Assert.NotEmpty(Wpf.Descendants<Button>(page));
        });
    }
}

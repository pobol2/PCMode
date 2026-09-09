using PCMode.Engine;
using PCMode.Tray.Elements.Audio;

namespace PCMode.Tray.Tests.Features.Audio;

/// <summary>
/// Audio's Current hardware section: what a scan turns real Windows playback
/// endpoints into, and what naming one writes back to the configuration.
/// </summary>
public sealed class AudioDeviceDiscoveryTests
{
    private const string BareConfiguration = """
        {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
          {"id":"desktop","name":"Desktop","resources":{}}]}]}
        """;

    [Fact]
    public async Task Audio_section_scans_outputs_and_saves_their_aliases()
    {
        using var root = new TemporaryConfigurationRoot(BareConfiguration);
        int reloads = 0;

        HardwareItem endpoint = new(
            "Speakers",
            HardwareItemStatus.Ok,
            "Default playback device.",
            [new HardwareDetail("Default", "Yes")],
            Identifier: "Windows Speakers",
            Category: AudioOutputDiscoverer.CategoryName
        );

        var section = new AudioDeviceDiscoveryViewModel(
            Workspaces.At(root.Path),
            () => reloads++,
            () => new HardwareCategoryResult(AudioOutputDiscoverer.CategoryName, [endpoint], null)
        );

        await section.Inventory.ScanAsync();

        DiscoveredAudioOutputRow row = Assert.Single(section.Inventory.Rows);
        Assert.True(row.SupportsAlias);
        Assert.Equal("Add to PCMode", row.ActionLabel);

        Assert.Null(await section.SetAliasAsync(row, "Desk speakers"));
        Assert.Equal(1, reloads);

        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(root.Path, json);
        _ = ModeConfiguration.Parse(json, providers);
        AudioProvider provider = providers.OfType<AudioProvider>().Single();

        Assert.Contains("Desk speakers", provider.DeviceNames);
    }

    [Fact]
    public void Discovered_output_rows_offer_switch_only_for_a_configured_output_that_is_not_already_current()
    {
        // The Audio outputs section's own direct-switching controls -- Switch
        // needs an alias to hand AudioProvider.Apply, and there is nothing to
        // switch to once Windows is already playing into that output.
        var notCurrent = new DiscoveredAudioOutputRow(
            new HardwareItem(
                "Speakers (Realtek Audio)",
                HardwareItemStatus.Ok,
                "Available.",
                [new HardwareDetail("Default", "No")],
                Identifier: "Speakers (Realtek Audio)",
                Alias: "Speakers"
            )
        );

        Assert.Equal("Speakers", notCurrent.FriendlyName);
        Assert.True(notCurrent.ShowUnderlyingDevice);
        Assert.True(notCurrent.CanSwitch);
        Assert.False(notCurrent.ShowCurrentState);

        var current = new DiscoveredAudioOutputRow(
            new HardwareItem(
                "Realtek HD Audio 2nd output",
                HardwareItemStatus.Ok,
                "Default Windows output.",
                [new HardwareDetail("Default", "Yes")],
                Identifier: "Realtek HD Audio 2nd output",
                Alias: "Headphones"
            )
        );

        Assert.False(current.CanSwitch);
        Assert.True(current.ShowCurrentState);

        var unconfigured = new DiscoveredAudioOutputRow(
            new HardwareItem(
                "LG Soundbar (Intel Display Audio)",
                HardwareItemStatus.Ok,
                "Available.",
                [new HardwareDetail("Default", "No")],
                Identifier: "LG Soundbar (Intel Display Audio)"
            )
        );

        Assert.Equal("LG Soundbar (Intel Display Audio)", unconfigured.FriendlyName);
        Assert.False(unconfigured.ShowUnderlyingDevice);
        Assert.False(unconfigured.CanSwitch);
        Assert.False(unconfigured.ShowCurrentState);
        Assert.Equal("Add to PCMode", unconfigured.ActionLabel);

        // The PCMode virtual endpoint is never aliasable, so it never offers
        // Switch, even while it happens to be the current default (an audio
        // target is playing).
        var virtualEndpoint = new DiscoveredAudioOutputRow(
            new HardwareItem(
                "PCMode Virtual Audio Endpoint",
                HardwareItemStatus.Ok,
                "PCMode's own audio endpoint. Audio targets play into this one.",
                [new HardwareDetail("Default", "Yes"), new HardwareDetail("PCMode virtual endpoint", "Yes")]
            )
        );

        Assert.False(virtualEndpoint.SupportsAlias);
        Assert.False(virtualEndpoint.CanSwitch);
        Assert.False(virtualEndpoint.ShowCurrentState);
    }
}

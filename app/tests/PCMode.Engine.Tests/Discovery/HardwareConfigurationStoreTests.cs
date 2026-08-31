namespace PCMode.Engine.Tests;

/// <summary>
/// What writing an alias to the real configuration file does and refuses to do.
/// </summary>
public sealed class HardwareConfigurationStoreTests
{
    private const string Document = """
        {
          "schemaVersion": 1,
          "spaces": [ { "id": "space", "name": "Space", "activities": [
            { "id": "desktop", "name": "Desktop", "resources": {} }
          ] } ]
        }
        """;

    [Fact]
    public void Adds_a_display_alias_that_DisplayProvider_then_reads_back()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        HardwareAliasOutcome outcome = HardwareConfigurationStore.SetDisplayAlias(root.Path, @"\\?\DISPLAY#1", "Desk Left");

        Assert.True(outcome.Success, outcome.Problem);

        var provider = new DisplayProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration.Parse(json, [provider]);

        Assert.Equal(["Desk Left"], provider.MonitorNames);
    }

    [Fact]
    public void Adds_an_audio_alias_that_AudioProvider_then_reads_back()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        HardwareAliasOutcome outcome =
            HardwareConfigurationStore.SetAudioAlias(root.Path, "Speakers (Realtek)", "Headphones");

        Assert.True(outcome.Success, outcome.Problem);

        var provider = new AudioProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration.Parse(json, [provider]);

        Assert.Equal(["Headphones"], provider.DeviceNames);
    }

    [Fact]
    public void Preserves_existing_modes_and_other_provider_settings()
    {
        using var root = new TemporaryRoot();
        root.Write("""
            {
              "schemaVersion": 1,
              "providers": { "audio": { "devices": { "TV": "Digital Audio (HDMI)" } } },
              "spaces": [ { "id": "space", "name": "Space", "activities": [
                { "id": "movie", "name": "Movie", "resources": { "audio": "TV" } }
              ] } ]
            }
            """);

        HardwareAliasOutcome outcome = HardwareConfigurationStore.SetDisplayAlias(root.Path, @"\\?\DISPLAY#1", "Desk Left");
        Assert.True(outcome.Success, outcome.Problem);

        var display = new DisplayProvider();
        var audio = new AudioProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration configuration = ModeConfiguration.Parse(json, [display, audio]);

        Assert.Equal(["Desk Left"], display.MonitorNames);
        Assert.Equal(["TV"], audio.DeviceNames);
        Assert.NotNull(configuration.Find("Movie"));
    }

    [Fact]
    public void Preserves_a_property_this_build_does_not_recognize()
    {
        using var root = new TemporaryRoot();
        // A save only ever mutates the JSON graph at the paths it owns; a field
        // this build has never heard of -- a future one, or one nobody has
        // written a reader for yet -- is not something an alias write is
        // entitled to drop.
        root.Write("""
            {
              "schemaVersion": 1,
              "notes": "do not delete",
              "spaces": [ { "id": "space", "name": "Space", "notes": "space note", "activities": [
                { "id": "desktop", "name": "Desktop", "resources": {} }
              ] } ]
            }
            """);

        HardwareAliasOutcome outcome = HardwareConfigurationStore.SetDisplayAlias(root.Path, @"\\?\DISPLAY#1", "Desk Left");
        Assert.True(outcome.Success, outcome.Problem);

        string written = File.ReadAllText(root.ConfigurationFile);

        Assert.Contains("\"do not delete\"", written, StringComparison.Ordinal);
        Assert.Contains("\"space note\"", written, StringComparison.Ordinal);

        // And it is still a document this build itself can read back.
        var provider = new DisplayProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration.Parse(json, [provider]);
        Assert.Equal(["Desk Left"], provider.MonitorNames);
    }

    [Fact]
    public void Renaming_an_alias_moves_it_rather_than_adding_a_second_one()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        Assert.True(HardwareConfigurationStore.SetDisplayAlias(root.Path, @"\\?\DISPLAY#1", "Desk Left").Success);
        Assert.True(HardwareConfigurationStore.SetDisplayAlias(root.Path, @"\\?\DISPLAY#1", "Left Monitor").Success);

        var provider = new DisplayProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration.Parse(json, [provider]);

        Assert.Equal(["Left Monitor"], provider.MonitorNames);
    }

    [Fact]
    public void Renaming_a_monitor_used_by_a_layout_updates_the_layout_too()
    {
        using var root = new TemporaryRoot();
        root.Write("""
            {
              "schemaVersion": 1,
              "providers": {
                "display": {
                  "monitors": { "Desk1": "\\\\?\\DISPLAY#1", "Desk2": "\\\\?\\DISPLAY#2" },
                  "layouts": {
                    "Desktop": {
                      "monitors": [ "Desk1", "Desk2" ],
                      "primary": "Desk1",
                      "placement": [ "Desk2 LeftOf Desk1" ],
                      "refresh": [ "Desk1 144", "Desk2 144" ]
                    }
                  }
                }
              },
              "spaces": [ { "id": "space", "name": "Space", "activities": [
                { "id": "desktop", "name": "Desktop", "resources": { "display": "Desktop" } }
              ] } ]
            }
            """);

        HardwareAliasOutcome outcome = HardwareConfigurationStore.SetDisplayAlias(root.Path, @"\\?\DISPLAY#1", "Desk Left");
        Assert.True(outcome.Success, outcome.Problem);

        var provider = new DisplayProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration configuration = ModeConfiguration.Parse(json, [provider]);

        Assert.Contains("Desk Left", provider.MonitorNames);
        Assert.NotNull(configuration.Find("Desktop"));
    }

    [Fact]
    public void Renaming_an_audio_device_used_by_a_mode_updates_the_mode_too()
    {
        using var root = new TemporaryRoot();
        root.Write("""
            {
              "schemaVersion": 1,
              "providers": { "audio": { "devices": { "TV": "Digital Audio (HDMI)" } } },
              "spaces": [ { "id": "space", "name": "Space", "activities": [
                { "id": "movie", "name": "Movie", "resources": { "audio": "TV" } }
              ] } ]
            }
            """);

        HardwareAliasOutcome outcome =
            HardwareConfigurationStore.SetAudioAlias(root.Path, "Digital Audio (HDMI)", "Living Room TV");
        Assert.True(outcome.Success, outcome.Problem);

        var provider = new AudioProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration configuration = ModeConfiguration.Parse(json, [provider]);

        Assert.Equal(["Living Room TV"], provider.DeviceNames);
        Assert.NotNull(configuration.Find("Movie"));
    }

    [Fact]
    public void Refuses_an_alias_already_used_for_a_different_device()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        Assert.True(HardwareConfigurationStore.SetDisplayAlias(root.Path, @"\\?\DISPLAY#1", "Desk Left").Success);

        HardwareAliasOutcome outcome =
            HardwareConfigurationStore.SetDisplayAlias(root.Path, @"\\?\DISPLAY#2", "Desk Left");

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Problem);

        // Unchanged: the second monitor did not silently take the name.
        var provider = new DisplayProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration.Parse(json, [provider]);
        Assert.Equal(["Desk Left"], provider.MonitorNames);
    }

    [Fact]
    public void Refuses_a_blank_alias()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        HardwareAliasOutcome outcome = HardwareConfigurationStore.SetDisplayAlias(root.Path, @"\\?\DISPLAY#1", "  ");

        Assert.False(outcome.Success);
        Assert.False(File.Exists(root.ConfigurationFile) && File.ReadAllText(root.ConfigurationFile).Contains("monitors"));
    }

    [Fact]
    public void Setting_the_same_alias_for_the_same_device_twice_is_a_no_op()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        Assert.True(HardwareConfigurationStore.SetDisplayAlias(root.Path, @"\\?\DISPLAY#1", "Desk Left").Success);
        Assert.True(HardwareConfigurationStore.SetDisplayAlias(root.Path, @"\\?\DISPLAY#1", "Desk Left").Success);

        var provider = new DisplayProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration.Parse(json, [provider]);

        Assert.Equal(["Desk Left"], provider.MonitorNames);
    }
}

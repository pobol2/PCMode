using System.IO;

namespace PCMode.Engine.Tests;

/// <summary>What the LG TV settings screen does with 'providers.lgTv'.</summary>
public sealed class LgTvConfigurationStoreTests
{
    [Fact]
    public void Saves_and_reads_back_a_configured_television_with_an_input_and_a_profile()
    {
        using var root = new TemporaryRoot("""{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{}}]}]}""");

        ModeSaveOutcome outcome = LgTvConfigurationStore.Save(
            root.Path,
            new LgTvEdit(
                true,
                "192.0.2.31",
                "00:00:5E:00:53:31",
                SafeDisplayTarget: null,
                Inputs: [new LgTvInputEdit("PC", "HDMI_4")],
                Profiles: [new LgTvProfileEdit(
                    "Gaming",
                    "hdrGame",
                    Input: null,
                    Signal: [new LgTvSettingEdit("gameoptimization", "on")],
                    Settings: [])]));

        Assert.True(outcome.Success, outcome.Problem);

        TvPowerProvider power = Load(root);
        LgTvSettings settings = power.Settings;

        Assert.True(settings.Enabled);
        Assert.Equal("192.0.2.31", settings.Host);
        Assert.Equal("00:00:5E:00:53:31", settings.MacAddress);
        Assert.Equal("HDMI_4", settings.InputIdFor("PC"));

        LgTvProfile profile = settings.ProfileFor("Gaming")!;
        Assert.Equal("hdrGame", profile.PictureMode);
        Assert.Equal("on", Assert.Single(profile.Signal).Value);
    }

    [Fact]
    public void Saves_a_profile_naming_its_own_input_and_resolves_the_right_socket()
    {
        // Two inputs, two profiles, each naming its own -- the replacement for
        // the single television-wide 'profileInput'.
        using var root = new TemporaryRoot("""{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{}}]}]}""");

        ModeSaveOutcome outcome = LgTvConfigurationStore.Save(
            root.Path,
            new LgTvEdit(
                true,
                "192.0.2.31",
                "00:00:5E:00:53:31",
                SafeDisplayTarget: null,
                Inputs: [new LgTvInputEdit("PC", "HDMI_4"), new LgTvInputEdit("Console", "HDMI_2")],
                Profiles:
                [
                    new LgTvProfileEdit(
                        "PC Gaming", "hdrGame", Input: "PC",
                        Signal: [new LgTvSettingEdit("gamemode_hdmi{n}", "on")], Settings: []),
                    new LgTvProfileEdit(
                        "Console Gaming", "game", Input: "Console",
                        Signal: [new LgTvSettingEdit("gamemode_hdmi{n}", "on")], Settings: []),
                ]));

        Assert.True(outcome.Success, outcome.Problem);

        TvPowerProvider power = Load(root);
        LgTvSettings settings = power.Settings;

        LgTvProfile pc = settings.ProfileFor("PC Gaming")!;
        LgTvProfile console = settings.ProfileFor("Console Gaming")!;

        Assert.Equal("PC", pc.InputName);
        Assert.Equal("gameMode_hdmi4", Assert.Single(pc.Signal).Key);

        Assert.Equal("Console", console.InputName);
        Assert.Equal("gameMode_hdmi2", Assert.Single(console.Signal).Key);
    }

    [Fact]
    public void Refuses_a_profile_naming_an_input_that_does_not_exist()
    {
        using var root = new TemporaryRoot("""{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{}}]}]}""");

        ModeSaveOutcome outcome = LgTvConfigurationStore.Save(
            root.Path,
            new LgTvEdit(
                true, "192.0.2.31", "00:00:5E:00:53:31", null,
                Inputs: [new LgTvInputEdit("PC", "HDMI_4")],
                Profiles: [new LgTvProfileEdit("Gaming", "hdrGame", Input: "Console", Signal: [], Settings: [])]));

        Assert.False(outcome.Success);
        Assert.Contains("Console", outcome.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_switching_it_on_with_no_host_and_leaves_the_file_untouched()
    {
        using var root = new TemporaryRoot("""{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{}}]}]}""");
        string before = File.ReadAllText(PCModePaths.ConfigurationFile(root.Path));

        ModeSaveOutcome outcome = LgTvConfigurationStore.Save(
            root.Path,
            new LgTvEdit(true, string.Empty, "00:00:5E:00:53:31", null, [], []));

        Assert.False(outcome.Success);
        Assert.Contains("address", outcome.Problem, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(PCModePaths.ConfigurationFile(root.Path)));
    }

    [Fact]
    public void Refuses_switching_it_on_with_no_mac_address()
    {
        using var root = new TemporaryRoot("""{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{}}]}]}""");

        ModeSaveOutcome outcome = LgTvConfigurationStore.Save(
            root.Path,
            new LgTvEdit(true, "192.0.2.31", string.Empty, null, [], []));

        Assert.False(outcome.Success);
        Assert.Contains("MAC address", outcome.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_two_inputs_with_the_same_name()
    {
        using var root = new TemporaryRoot("""{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{}}]}]}""");

        ModeSaveOutcome outcome = LgTvConfigurationStore.Save(
            root.Path,
            new LgTvEdit(
                true, "192.0.2.31", "00:00:5E:00:53:31", null,
                [new LgTvInputEdit("PC", "HDMI_4"), new LgTvInputEdit("PC", "HDMI_2")],
                []));

        Assert.False(outcome.Success);
        Assert.Contains("more than one input", outcome.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_two_profiles_with_the_same_name()
    {
        using var root = new TemporaryRoot("""{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{}}]}]}""");

        ModeSaveOutcome outcome = LgTvConfigurationStore.Save(
            root.Path,
            new LgTvEdit(
                true, "192.0.2.31", "00:00:5E:00:53:31", null, [],
                [new LgTvProfileEdit("Gaming", "hdrGame", null, [], []), new LgTvProfileEdit("Gaming", "filmMaker", null, [], [])]));

        Assert.False(outcome.Success);
        Assert.Contains("more than one profile", outcome.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognised_signal_key_is_refused_by_the_same_pipeline_a_real_startup_uses()
    {
        // The store makes no separate claim about which signal keys exist --
        // that stays LgTvProfile's alone, exercised here through the shared
        // validation path so the two can never disagree.
        using var root = new TemporaryRoot("""{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{}}]}]}""");

        ModeSaveOutcome outcome = LgTvConfigurationStore.Save(
            root.Path,
            new LgTvEdit(
                true, "192.0.2.31", "00:00:5E:00:53:31", null, [],
                [new LgTvProfileEdit("Gaming", "hdrGame", null, [new LgTvSettingEdit("notARealSetting", "on")], [])]));

        Assert.False(outcome.Success);
        Assert.Contains("notARealSetting", outcome.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_it_off_keeps_the_inputs_and_profiles()
    {
        using var root = new TemporaryRoot("""{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{}}]}]}""");

        Assert.True(LgTvConfigurationStore.Save(
            root.Path,
            new LgTvEdit(false, "192.0.2.31", "00:00:5E:00:53:31", null, [new LgTvInputEdit("PC", "HDMI_4")], [])).Success);

        TvPowerProvider power = Load(root);
        Assert.False(power.Settings.Enabled);
        Assert.Equal("HDMI_4", power.Settings.InputIdFor("PC"));
    }

    private static TvPowerProvider Load(TemporaryRoot root)
    {
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(root.Path, json);
        ModeConfiguration.Parse(json, providers);

        return providers.OfType<TvPowerProvider>().Single();
    }
}

using System.Text.Json;

namespace PCMode.Engine.Tests;

/// <summary>
/// What writing Audio Targets and audio outputs to the real configuration file
/// does, and refuses to do.
/// </summary>
public sealed class AudioConfigurationStoreTests
{
    private const string Document = """
        {
          "schemaVersion": 1,
          "providers": {
            "audio": {
              "devices": {
                "Speakers": "Speakers (Realtek(R) Audio)",
                "Soundbar": "Digital Audio (HDMI)",
                "Headphones": "Headset Earphone"
              }
            }
          },
          "spaces": [ { "id": "space", "name": "Space", "activities": [
            { "id": "desktop", "name": "Desktop", "resources": { "audio": "Speakers" } }
          ] } ]
        }
        """;

    /// <summary>
    /// The same document with its one mode already asking for 'Party'.
    /// </summary>
    /// <remarks>
    /// Not loadable on its own -- no target has that name yet -- which is
    /// exactly right: only the candidate a write produces is validated, so
    /// saving the target is what makes it valid, and that is the state the
    /// rename and removal rules are actually about.
    /// </remarks>
    private const string DocumentNamingParty = """
        {
          "schemaVersion": 1,
          "providers": {
            "audio": {
              "devices": {
                "Speakers": "Speakers (Realtek(R) Audio)",
                "Soundbar": "Digital Audio (HDMI)",
                "Headphones": "Headset Earphone"
              }
            }
          },
          "spaces": [ { "id": "space", "name": "Space", "activities": [
            { "id": "desktop", "name": "Desktop", "resources": { "audio": "Party" } }
          ] } ]
        }
        """;

    [Fact]
    public void Saves_a_target_that_AudioProvider_then_reads_back_route_for_route()
    {
        using var root = new TemporaryRoot(Document);

        ModeSaveOutcome outcome = AudioConfigurationStore.SaveTarget(
            root.Path,
            new AudioTargetEdit("Party", [
                new AudioRouteEdit("Soundbar", Volume: 0.4),
                new AudioRouteEdit("Speakers", DelayMs: 68, Gain: 0.8),
            ]));

        Assert.True(outcome.Success, outcome.Problem);

        AudioTargetSettings target = Assert.Single(ReadProvider(root).Targets);

        Assert.Equal("Party", target.Name);
        Assert.Equal(["Soundbar", "Speakers"], target.Outputs);

        Assert.Equal(0, target.Routes[0].DelayMs);
        Assert.Equal(0.4, target.Routes[0].Volume);

        Assert.Equal(68, target.Routes[1].DelayMs);
        Assert.Equal(0.8, target.Routes[1].Gain);
        Assert.Null(target.Routes[1].Volume);
    }

    [Fact]
    public void Keeps_more_than_one_target_side_by_side()
    {
        using var root = new TemporaryRoot(Document);

        Assert.True(AudioConfigurationStore.SaveTarget(
            root.Path, new AudioTargetEdit("Party", [new AudioRouteEdit("Soundbar"), new AudioRouteEdit("Speakers")]))
            .Success);

        Assert.True(AudioConfigurationStore.SaveTarget(
            root.Path, new AudioTargetEdit("Desk", [new AudioRouteEdit("Speakers")]))
            .Success);

        Assert.Equal(["Desk", "Party"], ReadProvider(root).Targets.Select(target => target.Name));
    }

    [Fact]
    public void Takes_every_mode_that_named_a_target_along_when_it_is_renamed()
    {
        // A target's name is what a mode's 'audio' resource holds, not a copy of
        // it. Left alone, every mode that asked for it would now name nothing --
        // and the validation the writer runs would refuse the whole edit.
        using var root = new TemporaryRoot(DocumentNamingParty);

        Assert.True(AudioConfigurationStore.SaveTarget(
            root.Path, new AudioTargetEdit("Party", [new AudioRouteEdit("Soundbar"), new AudioRouteEdit("Speakers")]))
            .Success);

        ModeSaveOutcome renamed = AudioConfigurationStore.SaveTarget(
            root.Path,
            new AudioTargetEdit("Everywhere", [new AudioRouteEdit("Soundbar"), new AudioRouteEdit("Speakers")]),
            previousName: "Party");

        Assert.True(renamed.Success, renamed.Problem);

        (ModeConfiguration configuration, _) = ReadConfiguration(root);

        Assert.Equal(
            "Everywhere",
            configuration.Find("Desktop")!.Resources.Single(r => r.Key == AudioProvider.ResourceKey).Value);

        Assert.Equal("Everywhere", Assert.Single(ReadProvider(root).Targets).Name);
    }

    [Fact]
    public void Refuses_to_remove_a_target_a_mode_still_asks_for()
    {
        using var root = new TemporaryRoot(DocumentNamingParty);

        Assert.True(AudioConfigurationStore.SaveTarget(
            root.Path, new AudioTargetEdit("Party", [new AudioRouteEdit("Soundbar"), new AudioRouteEdit("Speakers")]))
            .Success);

        ModeSaveOutcome removed = AudioConfigurationStore.RemoveTarget(root.Path, "Party");

        Assert.False(removed.Success);
        Assert.Contains("Desktop", removed.Problem!, StringComparison.Ordinal);

        // And nothing changed.
        Assert.Single(ReadProvider(root).Targets);
    }

    [Fact]
    public void Removes_a_target_nothing_names()
    {
        using var root = new TemporaryRoot(Document);

        Assert.True(AudioConfigurationStore.SaveTarget(
            root.Path, new AudioTargetEdit("Party", [new AudioRouteEdit("Soundbar"), new AudioRouteEdit("Speakers")]))
            .Success);

        Assert.True(AudioConfigurationStore.RemoveTarget(root.Path, "party").Success);
        Assert.Empty(ReadProvider(root).Targets);
    }

    [Fact]
    public void Refuses_to_remove_an_output_a_target_is_built_from()
    {
        using var root = new TemporaryRoot(Document);

        Assert.True(AudioConfigurationStore.SaveTarget(
            root.Path, new AudioTargetEdit("Party", [new AudioRouteEdit("Soundbar"), new AudioRouteEdit("Headphones")]))
            .Success);

        ModeSaveOutcome outcome = AudioConfigurationStore.RemoveOutput(root.Path, "Soundbar");

        Assert.False(outcome.Success);
        Assert.Contains("Party", outcome.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_to_remove_an_output_a_mode_still_names()
    {
        using var root = new TemporaryRoot(Document);

        ModeSaveOutcome outcome = AudioConfigurationStore.RemoveOutput(root.Path, "Speakers");

        Assert.False(outcome.Success);
        Assert.Contains("Desktop", outcome.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void Removes_an_output_nothing_uses()
    {
        using var root = new TemporaryRoot(Document);

        Assert.True(AudioConfigurationStore.RemoveOutput(root.Path, "headphones").Success);
        Assert.Equal(["Soundbar", "Speakers"], ReadProvider(root).DeviceNames);
    }

    [Theory]
    [InlineData("", "Speakers", 0, 1.0, "name")]
    [InlineData("Party", "", 0, 1.0, "outputs")]
    [InlineData("Party", "Speakers", 9000, 1.0, "delay")]
    [InlineData("Party", "Speakers", 0, 99.0, "gain")]
    public void Refuses_an_edit_a_person_could_not_have_meant(
        string name, string output, int delayMs, double gain, string mentions)
    {
        using var root = new TemporaryRoot(Document);

        ModeSaveOutcome outcome = AudioConfigurationStore.SaveTarget(
            root.Path,
            new AudioTargetEdit(name, [new AudioRouteEdit(output, delayMs, gain)]));

        Assert.False(outcome.Success);
        Assert.Contains(mentions, outcome.Problem!, StringComparison.OrdinalIgnoreCase);

        // The document is untouched: a refused edit writes nothing.
        Assert.Empty(ReadProvider(root).Targets);
    }

    [Fact]
    public void Refuses_one_output_used_twice_in_the_same_target()
    {
        using var root = new TemporaryRoot(Document);

        ModeSaveOutcome outcome = AudioConfigurationStore.SaveTarget(
            root.Path,
            new AudioTargetEdit("Party", [new AudioRouteEdit("Speakers"), new AudioRouteEdit("speakers")]));

        Assert.False(outcome.Success);
        Assert.Contains("different outputs", outcome.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_target_whose_every_output_is_optional()
    {
        using var root = new TemporaryRoot(Document);
        // Nothing could fail it, and a target that cannot fail is one whose
        // failure nobody is told about.

        ModeSaveOutcome outcome = AudioConfigurationStore.SaveTarget(
            root.Path,
            new AudioTargetEdit("Party", [
                new AudioRouteEdit("Speakers", Required: false),
                new AudioRouteEdit("Soundbar", Required: false),
            ]));

        Assert.False(outcome.Success);
        Assert.Contains("required", outcome.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refuses_a_target_named_after_one_of_the_outputs()
    {
        using var root = new TemporaryRoot(Document);
        // A mode names one value for the audio resource, and it would mean two
        // different things. Refused by the same parser that reads the document,
        // which is why nothing has to restate the rule here.

        ModeSaveOutcome outcome = AudioConfigurationStore.SaveTarget(
            root.Path,
            new AudioTargetEdit("Speakers", [new AudioRouteEdit("Soundbar")]));

        Assert.False(outcome.Success);
        Assert.Contains("Speakers", outcome.Problem!, StringComparison.Ordinal);
    }

    private static AudioProvider ReadProvider(TemporaryRoot root)
    {
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);

        var provider = new AudioProvider(new FakeAudioEndpoints());

        using JsonDocument document = JsonDocument.Parse(json);

        JsonElement audio = document.RootElement
            .GetProperty("providers")
            .GetProperty("audio");

        Assert.True(provider.TryConfigure(audio, out string? problem), problem);

        return provider;
    }

    private static (ModeConfiguration Configuration, IReadOnlyList<IModeResourceProvider> Providers)
        ReadConfiguration(TemporaryRoot root)
    {
        (string json, string path) = ModeConfiguration.ReadOrCreate(root.Path);

        IReadOnlyList<IModeResourceProvider> providers =
            EngineBootstrap.ProductionProviders(root.Path, json);

        return (ModeConfiguration.Parse(json, providers, path), providers);
    }
}

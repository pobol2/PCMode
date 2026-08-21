using System.IO;

namespace PCMode.Engine.Tests;

/// <summary>
/// What writing the Party group and audio outputs to the real configuration
/// file does, and refuses to do.
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

    [Fact]
    public void Saves_a_group_that_AudioProvider_then_reads_back_as_the_same_pair()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ModeSaveOutcome outcome = AudioConfigurationStore.SaveParty(
            root.Path,
            new PartyGroupEdit("Party", "Soundbar", "Speakers", DelayMs: 68, Gain: 0.8, ReferenceVolume: 0.4));

        Assert.True(outcome.Success, outcome.Problem);

        PartySettings party = ReadParty(root);

        Assert.Equal("Party", party.Name);
        Assert.Equal("Soundbar", party.Reference);
        Assert.Equal("Speakers", party.Mirror);
        Assert.Equal(68, party.DelayMs);
        Assert.Equal(0.8, party.Gain);
        Assert.Equal(0.4, party.ReferenceVolume);
        Assert.Null(party.MirrorVolume);
    }

    /// <summary>
    /// The two written forms mean the same thing, so editing one produces the
    /// other without changing which endpoint is which. A document a person
    /// hand-wrote in the older form has to be editable on screen.
    /// </summary>
    [Fact]
    public void Reads_the_older_reference_and_mirror_form_and_saves_it_as_the_current_one()
    {
        using var root = new TemporaryRoot();
        root.Write("""
            {
              "schemaVersion": 1,
              "providers": {
                "audio": {
                  "devices": { "Speakers": "Speakers (Realtek(R) Audio)", "Soundbar": "Digital Audio (HDMI)" },
                  "party": { "reference": "Soundbar", "mirror": "Speakers", "delayMs": 40, "gain": 1 }
                }
              },
              "spaces": [ { "id": "space", "name": "Space", "activities": [
                { "id": "desktop", "name": "Desktop", "resources": { "audio": "Speakers" } }
              ] } ]
            }
            """);

        PartySettings before = ReadParty(root);
        Assert.Equal("Soundbar", before.Reference);
        Assert.Equal(40, before.DelayMs);

        ModeSaveOutcome outcome = AudioConfigurationStore.SaveParty(
            root.Path,
            new PartyGroupEdit(before.Name, before.Reference, before.Mirror, DelayMs: 55, Gain: before.Gain));

        Assert.True(outcome.Success, outcome.Problem);

        string saved = File.ReadAllText(PCModePaths.ConfigurationFile(root.Path));
        Assert.Contains("\"outputs\"", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("\"delayMs\"", saved, StringComparison.Ordinal);

        PartySettings after = ReadParty(root);
        Assert.Equal("Soundbar", after.Reference);
        Assert.Equal("Speakers", after.Mirror);
        Assert.Equal(55, after.DelayMs);
    }

    [Fact]
    public void Renaming_the_group_takes_every_mode_that_asked_for_it_along()
    {
        using var root = new TemporaryRoot();
        root.Write(Document.Replace("\"audio\": \"Speakers\"", "\"audio\": \"Party\"", StringComparison.Ordinal));

        Assert.True(AudioConfigurationStore.SaveParty(
            root.Path,
            new PartyGroupEdit("Party", "Soundbar", "Speakers", 0, 1)).Success);

        ModeSaveOutcome renamed = AudioConfigurationStore.SaveParty(
            root.Path,
            new PartyGroupEdit("Whole room", "Soundbar", "Speakers", 0, 1));

        Assert.True(renamed.Success, renamed.Problem);

        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        var provider = new AudioProvider(new FakeAudioEndpoints());
        ModeConfiguration configuration = ModeConfiguration.Parse(json, [provider]);

        Assert.Equal("Whole room", provider.Party!.Name);
        Assert.Equal(
            "Whole room",
            configuration.Find("Desktop")!.Resources.Single(resource => resource.Key == AudioProvider.ResourceKey).Value);
    }

    [Theory]
    // Two of one endpoint is a feedback loop, not a group.
    [InlineData("Party", "Speakers", "Speakers", 0, 1.0, "two different outputs")]
    // Beyond what the runtime will buffer.
    [InlineData("Party", "Soundbar", "Speakers", 9000, 1.0, "between 0 and 2000")]
    // Above unity by more than the runtime's clipping headroom allows.
    [InlineData("Party", "Soundbar", "Speakers", 0, 8.0, "between 0 and 2")]
    // A group named after a device would make one mode value mean two things.
    [InlineData("Speakers", "Soundbar", "Speakers", 0, 1.0, "Give the Party group a name of its own")]
    // A name nothing defines would validate here and fail when it was asked for.
    [InlineData("Party", "Kitchen", "Speakers", 0, 1.0, "does not define")]
    public void Refuses_a_malformed_group_and_leaves_the_file_byte_for_byte_unchanged(
        string name, string reference, string mirrored, int delayMs, double gain, string expected)
    {
        using var root = new TemporaryRoot();
        root.Write(Document);
        string before = File.ReadAllText(root.ConfigurationFile);

        ModeSaveOutcome outcome = AudioConfigurationStore.SaveParty(
            root.Path,
            new PartyGroupEdit(name, reference, mirrored, delayMs, gain));

        Assert.False(outcome.Success);
        Assert.Contains(expected, outcome.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(root.ConfigurationFile));
    }

    [Fact]
    public void Removes_a_group_no_mode_asks_for()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        Assert.True(AudioConfigurationStore.SaveParty(
            root.Path,
            new PartyGroupEdit("Party", "Soundbar", "Speakers", 0, 1)).Success);

        ModeSaveOutcome removed = AudioConfigurationStore.RemoveParty(root.Path);

        Assert.True(removed.Success, removed.Problem);
        Assert.Null(ReadProvider(root).Party);
    }

    [Fact]
    public void Refuses_to_remove_a_group_a_mode_still_asks_for()
    {
        using var root = new TemporaryRoot();
        root.Write(Document.Replace("\"audio\": \"Speakers\"", "\"audio\": \"Party\"", StringComparison.Ordinal));

        Assert.True(AudioConfigurationStore.SaveParty(
            root.Path,
            new PartyGroupEdit("Party", "Soundbar", "Speakers", 0, 1)).Success);

        ModeSaveOutcome removed = AudioConfigurationStore.RemoveParty(root.Path);

        Assert.False(removed.Success);
        Assert.Contains("'Space — Desktop'", removed.Problem, StringComparison.Ordinal);
        Assert.NotNull(ReadProvider(root).Party);
    }

    [Fact]
    public void Removes_an_output_nothing_names()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ModeSaveOutcome outcome = AudioConfigurationStore.RemoveOutput(root.Path, "headphones");

        Assert.True(outcome.Success, outcome.Problem);
        Assert.Equal(["Soundbar", "Speakers"], ReadProvider(root).DeviceNames);
    }

    [Fact]
    public void Refuses_to_remove_an_output_the_group_is_built_from()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        Assert.True(AudioConfigurationStore.SaveParty(
            root.Path,
            new PartyGroupEdit("Party", "Soundbar", "Speakers", 0, 1)).Success);

        ModeSaveOutcome outcome = AudioConfigurationStore.RemoveOutput(root.Path, "Soundbar");

        Assert.False(outcome.Success);
        Assert.Contains("Party group", outcome.Problem, StringComparison.Ordinal);
        Assert.Contains("Soundbar", ReadProvider(root).DeviceNames);
    }

    [Fact]
    public void Refuses_to_remove_an_output_a_mode_still_asks_for()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ModeSaveOutcome outcome = AudioConfigurationStore.RemoveOutput(root.Path, "Speakers");

        Assert.False(outcome.Success);
        Assert.Contains("'Space — Desktop'", outcome.Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Everything the document already said still says it. A settings screen
    /// that quietly dropped a device alias or a mode while saving a Party group
    /// would be worse than one that could not save at all.
    /// </summary>
    [Fact]
    public void Leaves_the_rest_of_the_document_alone()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        Assert.True(AudioConfigurationStore.SaveParty(
            root.Path,
            new PartyGroupEdit("Party", "Soundbar", "Speakers", 12, 1)).Success);

        AudioProvider provider = ReadProvider(root);

        Assert.Equal(["Headphones", "Soundbar", "Speakers"], provider.DeviceNames);
        Assert.Equal("Speakers (Realtek(R) Audio)", provider.EndpointNameFor("Speakers"));
        Assert.Equal("Headset Earphone", provider.EndpointNameFor("Headphones"));
    }

    private static AudioProvider ReadProvider(TemporaryRoot root)
    {
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        var provider = new AudioProvider(new FakeAudioEndpoints());
        ModeConfiguration.Parse(json, [provider]);
        return provider;
    }

    private static PartySettings ReadParty(TemporaryRoot root) =>
        ReadProvider(root).Party ?? throw new InvalidOperationException("No Party group was configured.");
}

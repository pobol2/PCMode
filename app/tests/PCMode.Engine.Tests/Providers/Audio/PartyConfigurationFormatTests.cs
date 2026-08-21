using System.Text.Json;

namespace PCMode.Engine.Tests;

/// <summary>
/// The "outputs" / "delay" / "gain" configuration form, its equivalence with
/// the older "reference"/"mirror" form, and where the two must not be mixed.
/// </summary>
/// <remarks>
/// Both forms are read down to exactly the same <see cref="PartySettings"/>, so
/// what these tests exist to prove is the translation: that listing outputs in
/// either order lands on the same routing, that only "delay.output" -- never
/// list position -- decides which endpoint is the mirror, and that the older
/// technical field names go on working unchanged. Runtime behaviour (starting,
/// stopping, rollback) is covered once, in <see cref="PartyAudioTests"/>,
/// against whichever form is convenient there.
/// </remarks>
public sealed class PartyConfigurationFormatTests
{
    private const string SpeakerEndpoint = "Desk speakers (Realtek(R) Audio)";
    private const string SoundbarEndpoint = "LG Soundbar (Intel Display Audio)";

    [Fact]
    public void The_delayed_output_becomes_the_mirror_whichever_order_outputs_are_listed_in()
    {
        PartySettings forward = ParsePartySettings(
            """{"outputs":["Speakers","Soundbar"],"delay":{"output":"Speakers","ms":68}}""");

        PartySettings reversed = ParsePartySettings(
            """{"outputs":["Soundbar","Speakers"],"delay":{"output":"Speakers","ms":68}}""");

        foreach (PartySettings party in (PartySettings[])[forward, reversed])
        {
            Assert.Equal("Soundbar", party.Reference);
            Assert.Equal("Speakers", party.Mirror);
            Assert.Equal(68, party.DelayMs);
        }
    }

    [Fact]
    public void Reads_gain_and_volumes_from_the_new_form()
    {
        PartySettings party = ParsePartySettings(
            """
            {
              "outputs": ["Speakers", "Soundbar"],
              "delay": { "output": "Speakers", "ms": 68 },
              "gain": { "output": "Speakers", "value": 0.75 },
              "volumes": { "Speakers": 0.35, "Soundbar": 0.55 }
            }
            """);

        Assert.Equal(0.75, party.Gain);
        Assert.Equal(0.35, party.MirrorVolume);
        Assert.Equal(0.55, party.ReferenceVolume);
    }

    [Fact]
    public void The_new_form_and_the_older_form_agree_on_the_same_group()
    {
        PartySettings viaOutputs = ParsePartySettings(
            """
            {
              "outputs": ["Speakers", "Soundbar"],
              "delay": { "output": "Speakers", "ms": 68 },
              "gain": { "output": "Speakers", "value": 0.75 },
              "volumes": { "Speakers": 0.35, "Soundbar": 0.55 }
            }
            """);

        PartySettings viaReferenceMirror = ParsePartySettings(
            """
            {
              "reference": "Soundbar", "mirror": "Speakers",
              "delayMs": 68, "gain": 0.75,
              "referenceVolume": 0.55, "mirrorVolume": 0.35
            }
            """);

        Assert.Equal(viaOutputs with { Name = "Party" }, viaReferenceMirror with { Name = "Party" });
    }

    [Fact]
    public void A_delay_of_zero_still_needs_to_say_which_output_it_would_affect()
    {
        // Direction is the whole point of "delay.output"; leaving it out is not
        // the same as "no delay is wanted", because there would still be no way
        // to say which endpoint becomes the Windows default.
        string? problem = Configure(
            """{"outputs":["Speakers","Soundbar"]}""");

        Assert.NotNull(problem);
        Assert.Contains("delay", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        """{"outputs":["Speakers","Soundbar"],"reference":"Soundbar","mirror":"Speakers"}""",
        "mixes")]
    [InlineData(
        """{"outputs":["Speakers","Soundbar"],"delay":{"output":"Speakers"},"referenceVolume":0.5}""",
        "volumes")]
    [InlineData(
        """{"reference":"Soundbar","mirror":"Speakers","delay":{"output":"Speakers","ms":10}}""",
        "delayMs")]
    [InlineData(
        """{"reference":"Soundbar","mirror":"Speakers","volumes":{"Speakers":0.5}}""",
        "referenceVolume")]
    public void Refuses_to_mix_the_two_forms(string party, string mentions)
    {
        string? problem = Configure(party);

        Assert.NotNull(problem);
        Assert.Contains(mentions, problem, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"outputs":["Speakers","Soundbar"],"delay":{"output":"Kitchen","ms":10}}""", "Kitchen")]
    [InlineData("""{"outputs":["Speakers","Soundbar"],"delay":{"output":"Speakers","ms":10},"gain":{"output":"Soundbar","value":1}}""", "Speakers")]
    [InlineData("""{"outputs":["Speakers","Soundbar"],"delay":{"output":"Speakers","ms":10},"volumes":{"Kitchen":0.5}}""", "Kitchen")]
    [InlineData("""{"outputs":["Speakers","Speakers"],"delay":{"output":"Speakers","ms":10}}""", "twice")]
    [InlineData("""{"outputs":["Speakers"],"delay":{"output":"Speakers","ms":10}}""", "two")]
    [InlineData("""{"outputs":["Speakers","Soundbar"],"delay":{"output":"Speakers","ms":9000}}""", "delay.ms")]
    [InlineData("""{"outputs":["Speakers","Soundbar"],"delay":{"output":"Speakers"},"gain":{"output":"Speakers","value":11}}""", "gain.value")]
    public void Refuses_a_new_form_group_that_could_not_work(string party, string mentions)
    {
        string? problem = Configure(party);

        Assert.NotNull(problem);
        Assert.Contains(mentions, problem, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ------------------------------------------------------------

    private static PartySettings ParsePartySettings(string party)
    {
        AudioProvider provider = new(new FakeAudioEndpoints(), new PartyAudioHost(new FakeAudioMirrorFactory()));

        Assert.Null(Configure(provider, party));

        return provider.Party ?? throw new InvalidOperationException("Expected the party group to parse.");
    }

    private static string? Configure(string party) =>
        Configure(new AudioProvider(new FakeAudioEndpoints(), new PartyAudioHost(new FakeAudioMirrorFactory())), party);

    private static string? Configure(AudioProvider provider, string party)
    {
        string document =
            $$"""
            {"devices":{"Speakers":"{{SpeakerEndpoint}}","Soundbar":"{{SoundbarEndpoint}}"},"party":{{party}}}
            """;

        using JsonDocument parsed = JsonDocument.Parse(document);

        bool accepted = provider.TryConfigure(parsed.RootElement, out string? problem);

        Assert.Equal(accepted, problem is null);

        return problem;
    }
}

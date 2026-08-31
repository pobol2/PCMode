namespace PCMode.Engine.Tests;

/// <summary>
/// What happens to a document that still describes a Party group.
/// </summary>
/// <remarks>
/// Both written forms, the settings that have to survive, the one that cannot,
/// and the rule that a document nothing needs doing to is left exactly as it
/// was. The conversion is a pure function of the text, which is what lets all of
/// that be checked without a machine.
/// </remarks>
public sealed class AudioMigrationTests
{
    private const string Devices =
        """
        "devices": {
          "Speakers": "Speakers (Realtek(R) Audio)",
          "Soundbar": "Digital Audio (HDMI)"
        }
        """;

    [Fact]
    public void Converts_the_outputs_and_delay_form_into_a_target_with_two_routes()
    {
        // The reference output becomes the route with no delay and the mirrored
        // one becomes the delayed route: that is what the two roles meant. The
        // reference was the slower path in the room, and the other was held back
        // to meet it.
        AudioTargetSettings target = Convert(
            """
            "party": {
              "name": "Party",
              "outputs": ["Soundbar", "Speakers"],
              "delay": { "output": "Speakers", "ms": 68 },
              "gain": { "output": "Speakers", "value": 0.8 },
              "volumes": { "Soundbar": 0.4, "Speakers": 0.6 }
            }
            """);

        Assert.Equal("Party", target.Name);
        Assert.Equal(["Soundbar", "Speakers"], target.Outputs);

        Assert.Equal(0, target.Routes[0].DelayMs);
        Assert.Equal(1.0, target.Routes[0].Gain);
        Assert.Equal(0.4, target.Routes[0].Volume);
        Assert.True(target.Routes[0].Required);

        Assert.Equal(68, target.Routes[1].DelayMs);
        Assert.Equal(0.8, target.Routes[1].Gain);
        Assert.Equal(0.6, target.Routes[1].Volume);

        // Both required, because a Party group could not start unless both
        // endpoints were there.
        Assert.True(target.Routes[1].Required);
    }

    [Fact]
    public void Converts_the_older_reference_and_mirror_form_to_exactly_the_same_thing()
    {
        AudioTargetSettings target = Convert(
            """
            "party": {
              "reference": "Soundbar",
              "mirror": "Speakers",
              "delayMs": 68,
              "gain": 0.8,
              "referenceVolume": 0.4,
              "mirrorVolume": 0.6
            }
            """);

        // Including the name, which an unnamed group has always had.
        Assert.Equal("Party", target.Name);
        Assert.Equal(["Soundbar", "Speakers"], target.Outputs);
        Assert.Equal(0, target.Routes[0].DelayMs);
        Assert.Equal(0.4, target.Routes[0].Volume);
        Assert.Equal(68, target.Routes[1].DelayMs);
        Assert.Equal(0.8, target.Routes[1].Gain);
        Assert.Equal(0.6, target.Routes[1].Volume);
    }

    [Fact]
    public void Keeps_the_name_a_group_was_given_so_every_mode_that_asks_for_it_still_works()
    {
        // The group's name is what a mode's 'audio' resource holds. A conversion
        // that renamed it would leave every mode naming nothing.
        AudioTargetSettings target = Convert(
            """
            "party": {
              "name": "Everywhere",
              "outputs": ["Soundbar", "Speakers"],
              "delay": { "output": "Speakers", "ms": 0 }
            }
            """,
            asks: "Everywhere");

        Assert.Equal("Everywhere", target.Name);
    }

    [Fact]
    public void Clamps_a_delay_the_audio_engine_cannot_hold_and_says_so()
    {
        // The old runtime allowed two seconds and a route allows one. A delay
        // that large was a mistake in a file, and refusing to start over it
        // would strand the machine.
        Assert.True(AudioMigration.TryConvert(
            Document(
                """
                "party": {
                  "outputs": ["Soundbar", "Speakers"],
                  "delay": { "output": "Speakers", "ms": 1800 }
                }
                """),
            out string migrated,
            out string? summary));

        Assert.Contains("reduced", summary!, StringComparison.Ordinal);
        Assert.Equal(AudioTargetSettings.MaxDelayMs, Read(migrated).Routes[1].DelayMs);
    }

    [Fact]
    public void Leaves_a_document_with_no_party_group_byte_for_byte_alone()
    {
        // Almost every start is this one, and it has to cost nothing and change
        // nothing.
        string document = Document(
            """
            "targets": { "T": { "outputs": [ { "output": "Speakers" } ] } }
            """);

        Assert.False(AudioMigration.TryConvert(document, out string migrated, out string? summary));

        Assert.Same(document, migrated);
        Assert.Null(summary);
    }

    [Fact]
    public void Converting_an_already_converted_document_does_nothing()
    {
        Assert.True(AudioMigration.TryConvert(Document(Party), out string once, out _));
        Assert.False(AudioMigration.TryConvert(once, out string twice, out _));

        Assert.Equal(once, twice, StringComparer.Ordinal);
    }

    [Fact]
    public void Converts_the_same_document_to_the_same_text_every_time()
    {
        // Deterministic, which is what makes it safe to run on every start.
        Assert.True(AudioMigration.TryConvert(Document(Party), out string first, out _));
        Assert.True(AudioMigration.TryConvert(Document(Party), out string second, out _));

        Assert.Equal(first, second, StringComparer.Ordinal);
    }

    [Fact]
    public void Leaves_a_group_it_cannot_make_sense_of_where_it_is()
    {
        // A half-conversion would be far worse than a refusal. The loader then
        // says what is wrong with the document, in its own words.
        Assert.False(AudioMigration.TryConvert(
            Document("""
                "party": { "outputs": ["Soundbar", "Speakers"] }
                """),
            out _,
            out _));

        Assert.False(AudioMigration.TryConvert(
            Document("""
                "party": { "reference": "Soundbar" }
                """),
            out _,
            out _));
    }

    [Fact]
    public void Leaves_a_group_alone_when_a_target_already_has_its_name()
    {
        // Converting on top of it would overwrite something somebody wrote
        // deliberately.
        Assert.False(AudioMigration.TryConvert(
            Document(
                """
                "party": { "reference": "Soundbar", "mirror": "Speakers", "delayMs": 10 },
                "targets": { "Party": { "outputs": [ { "output": "Speakers" } ] } }
                """),
            out _,
            out _));
    }

    // ---- what a real start does with one -----------------------------------

    [Fact]
    public void A_real_start_converts_the_file_keeps_a_copy_and_loads_the_result()
    {
        using var root = new TemporaryRoot(Document(Party));

        ModeEngine engine = EngineBootstrap.Create(root.Path);

        // The mode that named the group still resolves, through the converted
        // target rather than through anything left behind.
        AudioProvider provider = Assert.Single(engine.Providers.OfType<AudioProvider>());

        Assert.Equal("Party", Assert.Single(provider.Targets).Name);
        Assert.NotNull(engine.ResolveTarget("Desktop"));

        // The file on disk was rewritten, and the original is beside it.
        string written = File.ReadAllText(root.ConfigurationFile);

        Assert.DoesNotContain("\"party\"", written, StringComparison.Ordinal);
        Assert.Contains("\"targets\"", written, StringComparison.Ordinal);

        string backup = Path.Combine(root.Path, AudioMigration.BackupFileName);

        Assert.True(File.Exists(backup));
        Assert.Contains("\"party\"", File.ReadAllText(backup), StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_start_finds_nothing_left_to_convert()
    {
        using var root = new TemporaryRoot(Document(Party));

        _ = EngineBootstrap.Create(root.Path);

        string afterFirst = File.ReadAllText(root.ConfigurationFile);

        _ = EngineBootstrap.Create(root.Path);

        Assert.Equal(afterFirst, File.ReadAllText(root.ConfigurationFile), StringComparer.Ordinal);
    }

    // ---- helpers ------------------------------------------------------------

    private const string Party =
        """
        "party": {
          "outputs": ["Soundbar", "Speakers"],
          "delay": { "output": "Speakers", "ms": 68 },
          "gain": { "output": "Speakers", "value": 0.8 }
        }
        """;

    /// <summary>A whole document with that audio section, and a mode naming the group.</summary>
    /// <param name="asks">
    /// What the one mode's audio resource holds. Named separately because the
    /// whole point of keeping the group's name is that this string does not have
    /// to change, so a test that converts a group called something else has to
    /// be able to say so.
    /// </param>
    private static string Document(string audioSection, string asks = "Party") =>
        $$"""
        {
          "schemaVersion": 1,
          "providers": { "audio": { {{Devices}}, {{audioSection}} } },
          "spaces": [ { "id": "space", "name": "Space", "activities": [
            { "id": "desktop", "name": "Desktop", "resources": { "audio": "{{asks}}" } }
          ] } ]
        }
        """;

    private static AudioTargetSettings Convert(string party, string asks = "Party")
    {
        Assert.True(AudioMigration.TryConvert(Document(party, asks), out string migrated, out string? summary));
        Assert.NotNull(summary);

        return Read(migrated);
    }

    /// <summary>The one target a converted document now has.</summary>
    private static AudioTargetSettings Read(string document)
    {
        using var root = new TemporaryRoot(document);

        IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(root.Path, document);

        // Parsed through the real loader, so a conversion that produced
        // something this build cannot read fails here rather than passing.
        _ = ModeConfiguration.Parse(document, providers);

        return Assert.Single(providers.OfType<AudioProvider>().Single().Targets);
    }
}

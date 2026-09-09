namespace PCMode.Engine.Tests;

/// <summary>
/// Space -> Activity -> Source, composed into the one desired state the engine
/// applies.
/// </summary>
/// <remarks>
/// The rules here are the whole reason common settings can be written once. If
/// inheritance, overriding and clearing are not exactly predictable then storing
/// something at the Space is a gamble rather than a saving, and the only honest
/// alternative would be to repeat every value on every Source.
///
/// Everything is checked through <see cref="ModeConfiguration.Parse"/> rather
/// than by calling the composer directly, because what has to be true is that a
/// document composes this way -- not that a function does.
/// </remarks>
public sealed class CompositionTests
{
    private static IReadOnlyList<IModeResourceProvider> Providers =>
        [new FakeProvider("theme"), new FakeProvider("cursor"), new FakeProvider("audio"), new FakeProvider("tvPower")];

    private const string Hierarchy = """
        {
          "schemaVersion": 1,
          "spaces": [
            {
              "id": "desk",
              "name": "Desk",
              "resources": { "theme": "Light", "cursor": "Center", "audio": "Headphones" },
              "activities": [
                {
                  "id": "work",
                  "name": "Work",
                  "resources": { "audio": "Speakers" }
                },
                {
                  "id": "gaming",
                  "name": "Gaming",
                  "resources": { "theme": "Dark", "tvPower": { "value": "On", "optional": true } },
                  "sources": [
                    { "id": "steam", "name": "Steam", "resources": { "audio": "Soundbar" } },
                    { "id": "switch", "name": "Switch", "kind": "externalDevice",
                      "resources": { "cursor": null, "theme": "Dark" } }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static ModeConfiguration Read(string json) => ModeConfiguration.Parse(json, Providers);

    private static IReadOnlyDictionary<string, ConfiguredResource> ResourcesOf(
        ModeConfiguration configuration,
        string key
    )
    {
        ConfiguredMode mode =
            configuration.Find(key) ?? throw new InvalidOperationException($"'{key}' is not a configured target.");

        return mode.Resources.ToDictionary(resource => resource.Key, StringComparer.OrdinalIgnoreCase);
    }

    // ---- what a target is ---------------------------------------------------

    [Fact]
    public void Every_activity_is_a_target_and_every_source_is_another_beside_it()
    {
        ModeConfiguration configuration = Read(Hierarchy);

        Assert.Equal(
            ["desk/work", "desk/gaming", "desk/gaming/steam", "desk/gaming/switch"],
            configuration.Targets.Keys
        );

        Assert.Equal(
            ["Desk — Work", "Desk — Gaming", "Desk — Gaming — Steam", "Desk — Gaming — Switch"],
            configuration.Targets.Targets.Select(target => target.DisplayPath)
        );
    }

    /// <summary>
    /// An Activity with Sources is still activatable in its own right, and one
    /// without them needs nothing added.
    /// </summary>
    [Fact]
    public void Activities_work_both_with_and_without_sources()
    {
        ModeConfiguration configuration = Read(Hierarchy);

        Assert.Equal("Speakers", ResourcesOf(configuration, "desk/work")["audio"].Value);
        Assert.Equal("Headphones", ResourcesOf(configuration, "desk/gaming")["audio"].Value);
        Assert.Equal("Soundbar", ResourcesOf(configuration, "desk/gaming/steam")["audio"].Value);
    }

    // ---- the four composition rules ----------------------------------------

    [Fact]
    public void An_omitted_value_is_inherited_from_the_level_above()
    {
        IReadOnlyDictionary<string, ConfiguredResource> resources = ResourcesOf(Read(Hierarchy), "desk/gaming/steam");

        // Neither the Source nor the Activity mentions the cursor.
        Assert.Equal("Center", resources["cursor"].Value);
    }

    [Fact]
    public void An_activity_overrides_its_space_and_a_source_overrides_its_activity()
    {
        ModeConfiguration configuration = Read(Hierarchy);

        Assert.Equal("Light", ResourcesOf(configuration, "desk/work")["theme"].Value);
        Assert.Equal("Dark", ResourcesOf(configuration, "desk/gaming")["theme"].Value);
        Assert.Equal("Soundbar", ResourcesOf(configuration, "desk/gaming/steam")["audio"].Value);
    }

    [Fact]
    public void An_explicit_clear_removes_the_inherited_value_rather_than_overriding_it()
    {
        IReadOnlyDictionary<string, ConfiguredResource> resources = ResourcesOf(Read(Hierarchy), "desk/gaming/switch");

        // Not "cursor is something else" -- cursor is not part of this target at
        // all, so the transition never touches it.
        Assert.False(resources.ContainsKey("cursor"));
        Assert.Equal("Dark", resources["theme"].Value);
    }

    /// <summary>
    /// Off is an ordinary value and needs no special handling: it overrides an
    /// inherited value exactly like any other, and is emphatically not the same
    /// statement as clearing.
    /// </summary>
    [Fact]
    public void An_off_value_overrides_inherited_state_and_stays_in_the_target()
    {
        ModeConfiguration configuration = Read(
            """
            {"schemaVersion":1,"spaces":[{"id":"tv","name":"TV",
              "resources":{"tvPower":"On"},
              "activities":[
                {"id":"away","name":"Away","resources":{"tvPower":"Off"}},
                {"id":"quiet","name":"Quiet","resources":{"tvPower":null}}]}]}
            """
        );

        Assert.Equal("Off", ResourcesOf(configuration, "tv/away")["tvPower"].Value);
        Assert.False(ResourcesOf(configuration, "tv/quiet").ContainsKey("tvPower"));
    }

    [Fact]
    public void Optionality_travels_with_the_value_that_was_written()
    {
        IReadOnlyDictionary<string, ConfiguredResource> resources = ResourcesOf(Read(Hierarchy), "desk/gaming");

        Assert.True(resources["tvPower"].Optional);
        Assert.False(resources["theme"].Optional);
    }

    /// <summary>
    /// A resource keeps the position it was first declared at, whatever
    /// overrides it later, so the same document composes identically every time.
    /// </summary>
    [Fact]
    public void Composition_order_is_deterministic()
    {
        ModeConfiguration first = Read(Hierarchy);
        ModeConfiguration second = Read(Hierarchy);

        // Space order first, with an overridden resource keeping its original
        // position and a resource a lower level introduces appended.
        Assert.Equal(
            ["theme", "cursor", "audio", "tvPower"],
            first.Find("desk/gaming/steam")!.Resources.Select(resource => resource.Key)
        );

        Assert.Equal(first.Find("desk/gaming/steam")!.Resources, second.Find("desk/gaming/steam")!.Resources);
    }

    // ---- storing common state once -----------------------------------------

    [Fact]
    public void Common_state_written_once_at_the_space_reaches_every_target_under_it()
    {
        ModeConfiguration configuration = Read(Hierarchy);

        // The cursor is set once, on the Desk, and every target under it gets
        // it -- except the one that explicitly says it does not want it.
        Assert.All(
            (string[])["desk/work", "desk/gaming", "desk/gaming/steam"],
            key => Assert.Equal("Center", ResourcesOf(configuration, key)["cursor"].Value)
        );

        Assert.False(ResourcesOf(configuration, "desk/gaming/switch").ContainsKey("cursor"));

        // And it is written down exactly once.
        Assert.Equal(
            ["theme", "cursor", "audio"],
            configuration.Spaces.Single().Resources.Select(resource => resource.Key)
        );

        Assert.DoesNotContain(
            configuration.Spaces.Single().Activities.SelectMany(activity => activity.Resources),
            resource => resource.Key == "cursor"
        );
    }

    // ---- sources that are not programs on this PC ---------------------------

    [Fact]
    public void A_source_can_be_an_external_device_without_any_application_data()
    {
        ConfiguredSource external = Read(Hierarchy)
            .Spaces.Single()
            .Activities.Single(activity => activity.Id == "gaming")
            .Sources.Single(source => source.Id == "switch");

        Assert.Equal(SourceKind.ExternalDevice, external.Kind);
        Assert.Equal(
            SourceKind.PcApplication,
            Read(Hierarchy)
                .Spaces.Single()
                .Activities.Single(activity => activity.Id == "gaming")
                .Sources.Single(source => source.Id == "steam")
                .Kind
        );
    }

    // ---- identities and relationships --------------------------------------

    [Theory]
    [InlineData("""{"schemaVersion":1,"spaces":[{"name":"Desk","activities":[]}]}""", "has no id")]
    [InlineData("""{"schemaVersion":1,"spaces":[{"id":"desk","activities":[]}]}""", "has no name")]
    [InlineData("""{"schemaVersion":1,"spaces":[{"id":"my desk","name":"Desk","activities":[]}]}""", "letters, digits")]
    [InlineData("""{"schemaVersion":1,"spaces":[{"id":"a/b","name":"Desk","activities":[]}]}""", "letters, digits")]
    [InlineData(
        """
            {"schemaVersion":1,"spaces":[
              {"id":"desk","name":"Desk","activities":[]},
              {"id":"desk","name":"Other","activities":[]}]}
            """,
        "id of more than one space"
    )]
    [InlineData(
        """
            {"schemaVersion":1,"spaces":[
              {"id":"desk","name":"Desk","activities":[]},
              {"id":"desk2","name":"Desk","activities":[]}]}
            """,
        "name of more than one space"
    )]
    [InlineData(
        """
            {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
              {"id":"work","name":"Work"},{"id":"work","name":"Other"}]}]}
            """,
        "more than one activity with the id"
    )]
    [InlineData(
        """
            {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
              {"id":"work","name":"Work","sources":[
                {"id":"a","name":"Steam"},{"id":"b","name":"Steam"}]}]}]}
            """,
        "more than one source called"
    )]
    [InlineData(
        """
            {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
              {"id":"work","name":"Work","sources":[{"id":"a","name":"Steam","kind":"console"}]}]}]}
            """,
        "pcApplication, externalDevice or tvApplication"
    )]
    public void Invalid_identities_and_relationships_are_refused_before_anything_can_be_activated(
        string document,
        string expected
    )
    {
        ModeConfigurationException exception = Assert.Throws<ModeConfigurationException>(() => Read(document));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        """
            {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
              {"id":"work","name":"Work","resources":{"nonsense":"Yes"}}]}]}
            """,
        "which nothing here knows how to apply"
    )]
    [InlineData(
        """
            {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
              {"id":"work","name":"Work","resources":{"theme":""}}]}]}
            """,
        "needs a value"
    )]
    [InlineData(
        """
            {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","resources":{"theme":"Light","THEME":"Dark"},
              "activities":[]}]}
            """,
        "more than once"
    )]
    public void Composition_conflicts_are_refused_by_name(string document, string expected)
    {
        ModeConfigurationException exception = Assert.Throws<ModeConfigurationException>(() => Read(document));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A Space with nothing in it yet is somebody half way through building
    /// one, not a broken document: the GUI creates the Space before the first
    /// Activity exists, and that intermediate state has to save.
    /// </summary>
    [Fact]
    public void A_space_with_no_activities_is_allowed_and_offers_no_targets()
    {
        ModeConfiguration configuration = Read("""{"schemaVersion":1,"spaces":[{"id":"tv","name":"TV"}]}""");

        Assert.Empty(configuration.Targets.Targets);
        Assert.Equal("TV", configuration.Spaces.Single().Name);
    }

    // ---- the two document shapes never mix ----------------------------------

    [Theory]
    [InlineData(
        """{"schemaVersion":1,"modes":[{"name":"Desktop","resources":{"theme":"Light"}}]}""",
        "defines 'modes'"
    )]
    [InlineData("""{"schemaVersion":1}""", "defines no spaces")]
    [InlineData("""{"schemaVersion":3,"spaces":[]}""", "version 1")]
    [InlineData("""{"schemaVersion":3,"modes":[{"name":"Desktop","resources":{"theme":"Light"}}]}""", "version 1")]
    public void A_document_that_mixes_or_misnames_its_shape_is_refused(string document, string expected)
    {
        ModeConfigurationException exception = Assert.Throws<ModeConfigurationException>(() => Read(document));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- resolving a reference ---------------------------------------------

    [Fact]
    public void A_target_is_found_by_key_by_path_or_by_an_unambiguous_name()
    {
        ModeConfiguration configuration = Read(Hierarchy);

        Assert.Equal("desk/gaming/steam", configuration.Find("desk/gaming/steam")!.Name);
        Assert.Equal("desk/gaming/steam", configuration.Find("Desk — Gaming — Steam")!.Name);
        Assert.Equal("desk/gaming/steam", configuration.Find("steam")!.Name);
        Assert.Equal("desk/work", configuration.Find("WORK")!.Name);
    }

    [Fact]
    public void A_name_that_two_targets_share_is_refused_rather_than_guessed()
    {
        ModeConfiguration configuration = Read(
            """
            {"schemaVersion":1,"spaces":[
              {"id":"desk","name":"Desk","activities":[{"id":"gaming","name":"Gaming"}]},
              {"id":"tv","name":"TV","activities":[{"id":"gaming","name":"Gaming"}]}]}
            """
        );

        Assert.False(configuration.Targets.TryResolve("Gaming", out ConfiguredTarget? target, out string? problem));
        Assert.Null(target);
        Assert.Contains("names 2 targets", problem!, StringComparison.Ordinal);

        // Both are still perfectly reachable by what they actually are.
        Assert.NotNull(configuration.Find("desk/gaming"));
        Assert.NotNull(configuration.Find("tv/gaming"));
    }
}

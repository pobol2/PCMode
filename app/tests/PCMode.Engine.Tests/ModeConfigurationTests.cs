namespace PCMode.Engine.Tests;

/// <summary>
/// What the configuration accepts, and what it refuses before anything runs.
/// </summary>
public sealed class ModeConfigurationTests
{
    private static readonly IReadOnlyList<IModeResourceProvider> Providers =
    [
        new FakeProvider("theme") { Allowed = ["Light", "Dark"] },
    ];

    private static ModeConfiguration Parse(string json) => ModeConfiguration.Parse(json, Providers);

    [Fact]
    public void Reads_modes_in_the_order_they_were_written()
    {
        // The order is the order resources are applied in, so it is not a
        // presentation detail.
        ModeConfiguration configuration = Parse(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
              {"id":"movie","name":"Movie","resources":{"theme":"Dark"}},
              {"id":"desktop","name":"Desktop","resources":{"theme":"Light"}}]}]}
            """);

        Assert.Equal(["space/movie", "space/desktop"], configuration.Modes.Select(mode => mode.Name));
    }

    [Fact]
    public void Finds_a_mode_however_it_was_typed()
    {
        // The seeded document, so it is read by the providers that wrote it.
        ModeConfiguration configuration = ModeConfiguration.Parse(
            ModeConfiguration.DefaultDocument(),
            EngineBootstrap.ProductionProviders());

        Assert.NotNull(configuration.Find("desktop"));
        Assert.NotNull(configuration.Find("  Desktop "));
        Assert.Null(configuration.Find("Nothing"));
    }

    [Fact]
    public void Refuses_a_schema_version_it_does_not_speak()
    {
        // The alternative is reading a newer document leniently, which gives
        // somebody a configuration they believe they have and do not.
        var refusal = Assert.Throws<ModeConfigurationException>(
            () => Parse("""{"schemaVersion":99,"spaces":[]}"""));

        Assert.Contains("version 1", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_resource_nothing_can_apply()
    {
        // And says what it does know, because the answer is a short closed list
        // and making somebody find it in source is unkind.
        var refusal = Assert.Throws<ModeConfigurationException>(
            () => Parse(
                """{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{"volume":"40"}}]}]}"""));

        Assert.Contains("volume", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("theme", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_value_the_provider_will_not_take()
    {
        var refusal = Assert.Throws<ModeConfigurationException>(
            () => Parse(
                """{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{"theme":"Purple"}}]}]}"""));

        Assert.Contains("Purple", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_two_modes_of_the_same_name()
    {
        // One of them could never be selected, and nothing would say which.
        Assert.Throws<ModeConfigurationException>(() => Parse(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
              {"id":"desktop","name":"Desktop","resources":{"theme":"Light"}},
              {"id":"desktop-2","name":"desktop","resources":{"theme":"Dark"}}]}]}
            """));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("""{"schemaVersion":1,"modes":[]}""")]
    [InlineData(
        """{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"blank","name":"  ","resources":{}}]}]}""")]
    public void Refuses_a_document_it_cannot_use(string json) =>
        Assert.Throws<ModeConfigurationException>(() => Parse(json));

    [Fact]
    public void Forgives_comments_and_a_trailing_comma()
    {
        // A file a person edits by hand. Refusing it over a comma teaches
        // nothing, and every rule about CONTENT is still enforced.
        ModeConfiguration configuration = Parse(
            """
            {
              // the one I use
              "schemaVersion": 1,
              "spaces": [ { "id": "space", "name": "Space", "activities": [
                { "id": "desktop", "name": "Desktop", "resources": { "theme": "Light" } }, ] } ]
            }
            """);

        Assert.Single(configuration.Modes);
    }

    [Fact]
    public void Writes_working_defaults_the_first_time_it_is_asked()
    {
        // First run is the ordinary case. A fresh machine should get modes that
        // work, not an empty list and an explanation.
        using var root = new TemporaryRoot();

        // The shipped providers, because what is being checked is that this
        // build can read back what this build writes. Nothing is applied, so no
        // setting on the machine running the test is touched.
        ModeConfiguration configuration = ModeConfiguration.LoadOrCreate(
            root.Path,
            EngineBootstrap.ProductionProviders());

        Assert.True(File.Exists(root.ConfigurationFile));
        Assert.Equal(
            ["desk/desktop", "desk/youtube", "desk/gaming", "desk/movie"],
            configuration.Modes.Select(mode => mode.Name));

        Assert.Equal(
            ["Desk — Desktop", "Desk — YouTube", "Desk — Gaming", "Desk — Movie"],
            configuration.Targets.Targets.Select(target => target.DisplayPath));
    }

    [Fact]
    public void Leaves_a_configuration_that_already_exists_alone()
    {
        using var root = new TemporaryRoot();
        root.Write(
            """{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"work","name":"Work","resources":{"theme":"Light"}}]}]}""");

        ModeConfiguration configuration = ModeConfiguration.LoadOrCreate(root.Path, Providers);

        Assert.Equal(["space/work"], configuration.Modes.Select(mode => mode.Name));
    }

    [Fact]
    public void Ships_defaults_that_the_real_providers_accept()
    {
        // The seeded document is written by this build and read back by it, so
        // a default naming a resource the shipped providers do not have would
        // stop the application from starting on a machine that had never run
        // it -- the one case nobody tests by hand.
        ModeConfiguration configuration = ModeConfiguration.Parse(
            ModeConfiguration.DefaultDocument(),
            EngineBootstrap.ProductionProviders());

        Assert.Equal(4, configuration.Modes.Count);
        Assert.All(configuration.Modes, mode => Assert.NotEmpty(mode.Resources));
    }

    [Theory]
    [InlineData("Desktop", "Light", "Center")]
    [InlineData("YouTube", "Dark", "Corner")]
    [InlineData("Gaming", "Dark", "Corner")]
    [InlineData("Movie", "Dark", "Corner")]
    public void Ships_defaults_that_set_a_theme_and_park_the_pointer(string mode, string theme, string cursor)
    {
        // Both, in every mode. The cursor was added to modes that already had a
        // theme, and a seeded document that quietly dropped one of them would
        // leave a mode doing half of what it says it does.
        ModeConfiguration configuration = ModeConfiguration.Parse(
            ModeConfiguration.DefaultDocument(),
            EngineBootstrap.ProductionProviders());

        ConfiguredMode configured = Assert.IsType<ConfiguredMode>(configuration.Find(mode));

        Assert.Equal(theme, Assert.Single(configured.Resources, r => r.Key == "theme").Value);
        Assert.Equal(cursor, Assert.Single(configured.Resources, r => r.Key == "cursor").Value);
    }

    [Fact]
    public void Refuses_settings_for_something_that_is_not_a_provider()
    {
        // The same rule as an unknown resource key, for the same reason: a
        // mistyped section is not one anybody notices is missing. It is simply
        // ignored, and the positions the user carefully wrote never exist.
        ModeConfigurationException failure = Assert.Throws<ModeConfigurationException>(() => Parse(
            """
            {"schemaVersion":1,
             "providers":{"cursour":{"positions":{}}},
             "spaces":[{"id":"space","name":"Space","activities":[
               {"id":"work","name":"Work","resources":{"theme":"Dark"}}]}]}
            """));

        Assert.Contains("cursour", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_settings_for_a_provider_that_takes_none()
    {
        // Accepting them would mean a section that reads as configuration and
        // has no effect whatsoever.
        ModeConfigurationException failure = Assert.Throws<ModeConfigurationException>(() =>
            ModeConfiguration.Parse(
                """
                {"schemaVersion":1,
                 "providers":{"theme":{"accent":"blue"}},
                 "spaces":[{"id":"space","name":"Space","activities":[
                   {"id":"work","name":"Work","resources":{"theme":"Dark"}}]}]}
                """,
                EngineBootstrap.ProductionProviders()));

        Assert.Contains("takes none", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Settles_the_providers_before_it_checks_a_single_mode()
    {
        // The ordering rule the pattern depends on. Whether "Corner" is a usable
        // value is decided by the settings, so a document checked in the other
        // order would refuse every mode for a reason that was about to stop
        // being true.
        ModeConfiguration configuration = ModeConfiguration.Parse(
            """
            {"schemaVersion":1,
             "providers":{"cursor":{"positions":{"Corner":{"xFromRight":2,"yFromBottom":200}}}},
             "spaces":[{"id":"space","name":"Space","activities":[
               {"id":"movie","name":"Movie","resources":{"cursor":"Corner"}}]}]}
            """,
            EngineBootstrap.ProductionProviders());

        Assert.Equal("Corner", Assert.Single(Assert.IsType<ConfiguredMode>(configuration.Find("Movie")).Resources).Value);
    }

    // ---- optional resources, without a new document version -----------------

    [Fact]
    public void Reads_a_resource_written_the_way_every_existing_document_writes_one()
    {
        // The whole reason this is still schema version 1. Adding a version for
        // optionality would have made every configuration on every machine need
        // rewriting to express something almost none of them want to say.
        ConfiguredResource resource = Only(Parse(
            """{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"movie","name":"Movie","resources":{"theme":"Dark"}}]}]}"""));

        Assert.Equal("Dark", resource.Value);
        Assert.False(resource.Optional);
    }

    [Fact]
    public void Reads_a_resource_the_mode_says_it_can_manage_without()
    {
        ConfiguredResource resource = Only(Parse(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
              {"id":"movie","name":"Movie","resources":{"theme":{"value":"Dark","optional":true}}}]}]}
            """));

        Assert.Equal("Dark", resource.Value);
        Assert.True(resource.Optional);
    }

    [Fact]
    public void Reads_the_long_form_without_optional_as_required()
    {
        Assert.False(Only(Parse(
            """{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"movie","name":"Movie","resources":{"theme":{"value":"Dark"}}}]}]}""")).Optional);
    }

    [Theory]
    [InlineData("""{"optional":true}""", "not what it is")]
    [InlineData("""{"value":"Dark","optional":"yes"}""", "true or false")]
    [InlineData("""{"value":42}""", "in quotes")]
    [InlineData("""{"value":"Dark","required":false}""", "does not understand")]
    [InlineData("""["Dark"]""", "neither a value nor")]
    [InlineData("42", "neither a value nor")]
    public void Refuses_an_optional_resource_it_cannot_read(string written, string expected)
    {
        ModeConfigurationException failure = Assert.Throws<ModeConfigurationException>(() => Parse(
            $$$"""{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"movie","name":"Movie","resources":{"theme":{{{written}}}}}]}]}"""));

        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_value_it_cannot_apply_even_when_the_mode_could_do_without_it()
    {
        // Optional is about what this machine can do, never about what the
        // document means. A misspelt theme is a mistake in the file on every
        // machine there is.
        ModeConfigurationException failure = Assert.Throws<ModeConfigurationException>(() => Parse(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
              {"id":"movie","name":"Movie","resources":{"theme":{"value":"Purple","optional":true}}}]}]}
            """));

        Assert.Contains("Purple", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_resource_nothing_can_apply_even_when_it_is_optional()
    {
        ModeConfigurationException failure = Assert.Throws<ModeConfigurationException>(() => Parse(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
              {"id":"movie","name":"Movie","resources":{"volume":{"value":"40","optional":true}}}]}]}
            """));

        Assert.Contains("volume", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_mode_that_sets_one_resource_twice()
    {
        // A dictionary would have kept the last one, and a mode where a line
        // silently does nothing is worse than one refused by name. Matched the
        // way a resource is matched to its provider, so a difference of case is
        // still the same resource.
        ModeConfigurationException failure = Assert.Throws<ModeConfigurationException>(() => Parse(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
              {"id":"movie","name":"Movie","resources":{"theme":"Dark","Theme":"Light"}}]}]}
            """));

        Assert.Contains("more than once", failure.Message, StringComparison.Ordinal);
    }

    private static ConfiguredResource Only(ModeConfiguration configuration) =>
        Assert.Single(Assert.IsType<ConfiguredMode>(configuration.Find("Movie")).Resources);
}

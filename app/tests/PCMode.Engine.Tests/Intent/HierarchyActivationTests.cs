namespace PCMode.Engine.Tests;

/// <summary>
/// Activating a composed target through the existing engine.
/// </summary>
/// <remarks>
/// The point of every one of these is that nothing new executes anything. The
/// hierarchy composes into the same <see cref="ConfiguredMode"/> the engine
/// always applied, so planning, convergence, recording, retry and repair are the
/// mechanisms they already were -- what changes is only which resources a
/// request turns out to consist of, and what the result is able to say about
/// itself.
/// </remarks>
public sealed class HierarchyActivationTests
{
    private const string Document = """
        {
          "schemaVersion": 1,
          "spaces": [
            {
              "id": "desk",
              "name": "Desk",
              "resources": { "display": "Desk", "theme": "Light" },
              "activities": [
                {
                  "id": "gaming",
                  "name": "Gaming",
                  "resources": { "theme": "Dark" },
                  "sources": [
                    { "id": "steam", "name": "Steam", "resources": { "audio": "Speakers" } },
                    { "id": "switch", "name": "Switch", "kind": "externalDevice",
                      "resources": { "audio": "Soundbar" } }
                  ]
                },
                {
                  "id": "work",
                  "name": "Work",
                  "resources": {
                    "audio": "Headphones",
                    "lighting": { "value": "Rainbow", "optional": true }
                  }
                }
              ]
            },
            {
              "id": "tv",
              "name": "TV",
              "resources": { "display": "TVOnly" },
              "activities": [
                { "id": "movies", "name": "Movies", "resources": { "theme": "Dark", "audio": "TV" } }
              ]
            }
          ]
        }
        """;

    private sealed class World : IDisposable
    {
        public World(string document = Document)
        {
            Root = new TemporaryRoot();
            Root.Write(document);

            Display = new FakeProvider("display") { DisplayName = "Displays" };
            Theme = new FakeProvider("theme") { DisplayName = "Windows theme" };
            Audio = new FakeProvider("audio") { DisplayName = "Audio output" };
            Lighting = new FakeProvider("lighting")
            {
                DisplayName = "Case lighting",
                Unavailable = ResourceAvailability.Unavailable("OpenRGB is not installed."),
            };

            IReadOnlyList<IModeResourceProvider> providers = [Display, Theme, Audio, Lighting];

            Engine = new ModeEngine(
                ModeConfiguration.LoadOrCreate(Root.Path, providers),
                new ModeStateStore(Root.StateFile),
                providers
            );
        }

        public TemporaryRoot Root { get; }

        public FakeProvider Display { get; }

        public FakeProvider Theme { get; }

        public FakeProvider Audio { get; }

        public FakeProvider Lighting { get; }

        public ModeEngine Engine { get; }

        public void Dispose() => Root.Dispose();
    }

    [Fact]
    public async Task The_engine_receives_one_composed_desired_state_for_a_source()
    {
        using var world = new World();

        TransitionOutcome outcome = await world.Engine.ApplyAsync("desk/gaming/steam");

        Assert.True(outcome.Succeeded, outcome.Message);

        // Space, Activity and Source, in one transition, with the Activity's
        // theme winning over the Space's.
        Assert.Equal(["Desk"], world.Display.Applied);
        Assert.Equal(["Dark"], world.Theme.Applied);
        Assert.Equal(["Speakers"], world.Audio.Applied);
    }

    [Fact]
    public async Task An_activity_with_no_source_of_its_own_applies_just_as_well()
    {
        using var world = new World();

        TransitionOutcome outcome = await world.Engine.ApplyAsync("desk/work");

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(["Headphones"], world.Audio.Applied);
        Assert.Equal("desk/work", world.Engine.ReadCurrentTargetKey(out _));
    }

    /// <summary>
    /// The acceptance criterion the hierarchy exists for: changing Source inside
    /// one Space and Activity must not disturb what they already had right.
    /// </summary>
    [Fact]
    public async Task Switching_source_preserves_the_space_and_activity_resources_that_are_already_correct()
    {
        using var world = new World();

        Assert.True((await world.Engine.ApplyAsync("desk/gaming/steam")).Succeeded);

        TransitionOutcome outcome = await world.Engine.ApplyAsync("desk/gaming/switch");

        Assert.True(outcome.Succeeded, outcome.Message);

        // Nothing was applied a second time to the display or the theme...
        Assert.Equal(["Desk"], world.Display.Applied);
        Assert.Equal(["Dark"], world.Theme.Applied);

        // ...and they are reported as already correct rather than silently
        // dropped, so a person can see why the transition was short.
        Assert.Equal(
            TransitionStepStatus.AlreadyCorrect,
            outcome.Steps.Single(step => step.Resource == "display").Status
        );

        Assert.Equal(TransitionStepStatus.Applied, outcome.Steps.Single(step => step.Resource == "audio").Status);

        Assert.Equal(["Speakers", "Soundbar"], world.Audio.Applied);
    }

    [Fact]
    public async Task The_outcome_health_and_recorded_state_all_identify_the_hierarchy()
    {
        using var world = new World();

        TransitionOutcome outcome = await world.Engine.ApplyAsync("desk/gaming/switch");

        Assert.Equal("desk/gaming/switch", outcome.Mode);
        Assert.Equal("Desk", outcome.Target!.SpaceName);
        Assert.Equal("Gaming", outcome.Target.ActivityName);
        Assert.Equal("Switch", outcome.Target.SourceName);
        Assert.Equal(SourceKind.ExternalDevice, outcome.Target.SourceKind);
        Assert.Equal("Desk — Gaming — Switch", outcome.Target.DisplayPath);

        // The message a person reads names the hierarchy, and the key -- what is
        // recorded and retried -- stays the identity.
        Assert.Contains("'Desk — Gaming — Switch'", outcome.Message, StringComparison.Ordinal);
        Assert.Equal("desk/gaming/switch", world.Engine.ReadCurrentTargetKey(out _));

        ModeHealth health = world.Engine.LastHealth!;

        Assert.Equal("desk/gaming/switch", health.Mode);
        Assert.Equal("Desk — Gaming — Switch", health.DisplayName);
        Assert.Equal(ResourceHealthState.Correct, health.Overall);
    }

    [Fact]
    public async Task A_repair_re_applies_the_recorded_target_and_touches_only_what_drifted()
    {
        using var world = new World();

        Assert.True((await world.Engine.ApplyAsync("desk/gaming/steam")).Succeeded);

        world.Audio.Current = "Something else";

        TransitionOutcome outcome = (await world.Engine.RepairAsync())!;

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("Desk — Gaming — Steam", outcome.Target!.DisplayPath);
        Assert.Equal(["Speakers", "Speakers"], world.Audio.Applied);
        Assert.Equal(["Desk"], world.Display.Applied);
    }

    [Fact]
    public async Task A_request_naming_nothing_is_refused_with_the_targets_that_do_exist()
    {
        using var world = new World();

        TransitionOutcome outcome = await world.Engine.ApplyAsync("desk/lounge");

        Assert.Equal(TransitionStatus.Refused, outcome.Status);
        Assert.Null(outcome.Target);
        Assert.Contains("desk/gaming/steam", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(world.Theme.Applied);
    }

    [Fact]
    public async Task A_disabled_target_stays_configured_but_is_refused_before_any_provider_is_touched()
    {
        string document = Document.Replace(
            "{ \"id\": \"movies\", \"name\": \"Movies\"",
            "{ \"id\": \"movies\", \"name\": \"Movies\", \"enabled\": false",
            StringComparison.Ordinal
        );

        using var world = new World(document);

        ConfiguredTarget target = world.Engine.Targets.Single(candidate => candidate.Key == "tv/movies");
        Assert.False(target.Enabled);

        TransitionOutcome outcome = await world.Engine.ApplyAsync(target.Key);

        Assert.Equal(TransitionStatus.Refused, outcome.Status);
        Assert.Same(target, outcome.Target);
        Assert.Contains("disabled", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(world.Display.Applied);
        Assert.Empty(world.Theme.Applied);
        Assert.Empty(world.Audio.Applied);
        Assert.Null(world.Engine.ReadCurrentTargetKey(out _));
    }

    [Fact]
    public async Task A_saved_reference_that_still_uses_a_name_activates_the_target_it_means()
    {
        using var world = new World();

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Work");

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("desk/work", outcome.Mode);
    }

    /// <summary>
    /// The acceptance criterion that a missing optional integration must not
    /// block the rest of a composed target, proven through the hierarchy rather
    /// than a flat <see cref="ConfiguredMode"/>.
    /// </summary>
    [Fact]
    public async Task A_missing_optional_integration_is_left_out_without_blocking_the_rest_of_the_target()
    {
        using var world = new World();

        TransitionOutcome outcome = await world.Engine.ApplyAsync("desk/work");

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(["Headphones"], world.Audio.Applied);
        Assert.Empty(world.Lighting.Applied);

        TransitionStep left = outcome.Steps.Single(step => step.Status == TransitionStepStatus.Omitted);
        Assert.Equal("lighting", left.Resource);
    }

    /// <summary>
    /// The acceptance criterion that representative Desk and TV targets can both
    /// be activated and switched between, and that switching Space does not
    /// disturb a resource the destination leaves unmentioned.
    /// </summary>
    [Fact]
    public async Task Switching_from_desk_to_tv_and_back_applies_only_what_each_space_actually_states()
    {
        using var world = new World();

        Assert.True((await world.Engine.ApplyAsync("desk/gaming/steam")).Succeeded);
        Assert.Equal(["Desk"], world.Display.Applied);
        Assert.Equal(["Dark"], world.Theme.Applied);

        TransitionOutcome toTv = await world.Engine.ApplyAsync("tv/movies");

        Assert.True(toTv.Succeeded, toTv.Message);
        Assert.Equal("TV — Movies", toTv.Target!.DisplayPath);
        Assert.Equal(["Desk", "TVOnly"], world.Display.Applied);
        // The TV space's Activity restates the same theme value the Desk space
        // left it on, so it is reported as already correct rather than reapplied.
        Assert.Equal(["Dark"], world.Theme.Applied);
        Assert.Equal(["Speakers", "TV"], world.Audio.Applied);

        TransitionOutcome backToDesk = await world.Engine.ApplyAsync("desk/gaming/steam");

        Assert.True(backToDesk.Succeeded, backToDesk.Message);
        Assert.Equal(["Desk", "TVOnly", "Desk"], world.Display.Applied);
        Assert.Equal(["Speakers", "TV", "Speakers"], world.Audio.Applied);
    }
}

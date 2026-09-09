namespace PCMode.Engine.Tests;

/// <summary>
/// What a transition consists of, and in what order, decided before any of it
/// happens.
/// </summary>
/// <remarks>
/// Everything here is settled with the machine untouched. That is the point of
/// having a plan at all: the rule that a mode with one unusable resource changes
/// nothing is only as good as the amount that is known before the first write.
/// </remarks>
public sealed class TransitionPlanTests
{
    // ---- order --------------------------------------------------------------

    [Fact]
    public void Keeps_the_order_the_configuration_was_written_in()
    {
        // Nothing here depends on anything, so there is nothing to reorder --
        // and a planner that shuffled independent resources anyway would make
        // the file's order a lie.
        TransitionPlan plan = Plan(
            Mode("Desk", ("theme", "Light"), ("cursor", "Center"), ("audio", "Speakers")),
            new FakeProvider("theme"),
            new FakeProvider("cursor"),
            new FakeProvider("audio")
        );

        Assert.Equal(["theme", "cursor", "audio"], Keys(plan));
    }

    [Fact]
    public void Runs_a_resource_after_the_one_it_declares_it_follows()
    {
        // The display is written last and applied first. The whole reason the
        // relationship is declared by the provider rather than implied by the
        // file is that a user should not have to know this to get it right.
        TransitionPlan plan = Plan(
            Mode("Film", ("cursor", "Corner"), ("display", "TVOnly")),
            new FakeProvider("cursor") { RunsAfter = ["display"] },
            new FakeProvider("display")
        );

        Assert.Equal(["display", "cursor"], Keys(plan));
    }

    [Fact]
    public void Leaves_a_resource_where_it_is_when_what_it_follows_is_absent()
    {
        // THE rule that keeps an ordering preference from becoming a demand. A
        // mode that only moves the pointer must not start rearranging monitors.
        TransitionPlan plan = Plan(
            Mode("Quiet", ("theme", "Dark"), ("cursor", "Corner")),
            new FakeProvider("theme"),
            new FakeProvider("cursor") { RunsAfter = ["display"] },
            new FakeProvider("display")
        );

        Assert.Equal(["theme", "cursor"], Keys(plan));
        Assert.DoesNotContain(plan.Steps, step => step.Key == "display");
    }

    [Fact]
    public void Breaks_a_tie_by_configuration_order_and_not_by_luck()
    {
        // Two resources that both follow the display and have no opinion about
        // each other. Either order works; only one of them is reproducible.
        var mode = Mode("Film", ("audio", "TV"), ("cursor", "Corner"), ("display", "TVOnly"));

        IModeResourceProvider[] providers =
        [
            new FakeProvider("audio") { RunsAfter = ["display"] },
            new FakeProvider("cursor") { RunsAfter = ["display"] },
            new FakeProvider("display"),
        ];

        Assert.Equal(["display", "audio", "cursor"], Keys(Plan(mode, providers)));

        // And the same answer when the providers are handed over in another
        // order, because the configuration is what decides, not the wiring.
        Assert.Equal(["display", "audio", "cursor"], Keys(Plan(mode, [.. providers.Reverse()])));
    }

    // ---- requirements -------------------------------------------------------

    [Fact]
    public void Adds_a_resource_the_mode_never_mentioned_when_something_needs_it()
    {
        // A mode asking for sound over a television it did not switch on is
        // asking for something that cannot happen. Adding the display is the
        // only reading of the request that is not a failure.
        TransitionPlan plan = Plan(
            Mode("Film", ("audio", "TV")),
            new FakeProvider("audio")
            {
                Requires = new Dictionary<string, ResourceRequirement[]>
                {
                    ["TV"] = [new ResourceRequirement("display", "TVOnly")],
                },
            },
            new FakeProvider("display")
        );

        Assert.Equal(["display", "audio"], Keys(plan));

        PlannedResource added = Assert.Single(plan.Added);

        Assert.Equal("display", added.Key);
        Assert.Equal("TVOnly", added.Target);
        Assert.Equal("audio", added.RequiredBy);
    }

    [Fact]
    public void Requires_nothing_for_a_target_that_needs_nothing()
    {
        // Requirements belong to a value, not to a provider. Declaring them once
        // for every target would drag the television into every mode.
        TransitionPlan plan = Plan(
            Mode("Desk", ("audio", "Speakers")),
            new FakeProvider("audio")
            {
                Requires = new Dictionary<string, ResourceRequirement[]>
                {
                    ["TV"] = [new ResourceRequirement("display", "TVOnly")],
                },
            },
            new FakeProvider("display")
        );

        Assert.Equal(["audio"], Keys(plan));
        Assert.Empty(plan.Added);
    }

    [Fact]
    public void Adds_what_an_added_resource_needs_in_turn()
    {
        // The chain is resolved whole, not one link deep.
        TransitionPlan plan = Plan(
            Mode("Film", ("audio", "TV")),
            new FakeProvider("audio")
            {
                Requires = new Dictionary<string, ResourceRequirement[]>
                {
                    ["TV"] = [new ResourceRequirement("display", "TVOnly")],
                },
            },
            new FakeProvider("display")
            {
                Requires = new Dictionary<string, ResourceRequirement[]>
                {
                    ["TVOnly"] = [new ResourceRequirement("theme", "Dark")],
                },
            },
            new FakeProvider("theme")
        );

        Assert.Equal(["theme", "display", "audio"], Keys(plan));
        Assert.Equal(["display", "theme"], plan.Added.Select(step => step.Key).Order());
    }

    [Fact]
    public void Leaves_a_requirement_the_mode_already_states_exactly_where_it_was()
    {
        // Agreement is not a reason to move anything, and above all not a reason
        // to add a second copy of it.
        TransitionPlan plan = Plan(
            Mode("Film", ("display", "TVOnly"), ("audio", "TV")),
            new FakeProvider("display"),
            new FakeProvider("audio")
            {
                Requires = new Dictionary<string, ResourceRequirement[]>
                {
                    ["TV"] = [new ResourceRequirement("display", "TVOnly")],
                },
            }
        );

        Assert.Equal(["display", "audio"], Keys(plan));
        Assert.Empty(plan.Added);
        Assert.All(plan.Steps, step => Assert.True(step.WasRequested));
    }

    [Fact]
    public void Puts_a_requirement_before_the_resource_that_needed_it_even_when_written_after()
    {
        TransitionPlan plan = Plan(
            Mode("Film", ("audio", "TV"), ("display", "TVOnly")),
            new FakeProvider("audio")
            {
                Requires = new Dictionary<string, ResourceRequirement[]>
                {
                    ["TV"] = [new ResourceRequirement("display", "TVOnly")],
                },
            },
            new FakeProvider("display")
        );

        Assert.Equal(["display", "audio"], Keys(plan));
    }

    // ---- conflicts ----------------------------------------------------------

    [Fact]
    public void Refuses_a_mode_that_contradicts_something_it_needs()
    {
        // Both readings are wrong. Applying the mode's value breaks the resource
        // that needs the other one; applying the requirement quietly overrides
        // what the user actually wrote.
        string problem = Rejected(
            Mode("Muddle", ("display", "Desktop"), ("audio", "TV")),
            new FakeProvider("display"),
            new FakeProvider("audio")
            {
                Requires = new Dictionary<string, ResourceRequirement[]>
                {
                    ["TV"] = [new ResourceRequirement("display", "TVOnly")],
                },
            }
        );

        Assert.Contains("Desktop", problem, StringComparison.Ordinal);
        Assert.Contains("TVOnly", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_two_resources_that_need_the_same_thing_set_two_ways()
    {
        // Neither asked for the display, and between them they have made it
        // impossible. Picking a winner would mean one of the two resources
        // silently not working.
        string problem = Rejected(
            Mode("Muddle", ("audio", "TV"), ("theme", "Dark")),
            new FakeProvider("audio")
            {
                Requires = new Dictionary<string, ResourceRequirement[]>
                {
                    ["TV"] = [new ResourceRequirement("display", "TVOnly")],
                },
            },
            new FakeProvider("theme")
            {
                Requires = new Dictionary<string, ResourceRequirement[]>
                {
                    ["Dark"] = [new ResourceRequirement("display", "Desktop")],
                },
            },
            new FakeProvider("display")
        );

        Assert.Contains("TVOnly", problem, StringComparison.Ordinal);
        Assert.Contains("Desktop", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_requirement_nothing_here_can_apply()
    {
        string problem = Rejected(
            Mode("Film", ("audio", "TV")),
            new FakeProvider("audio")
            {
                Requires = new Dictionary<string, ResourceRequirement[]>
                {
                    ["TV"] = [new ResourceRequirement("television", "On")],
                },
            }
        );

        Assert.Contains("television", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_requirement_the_provider_it_names_will_not_accept()
    {
        // The added resource is validated exactly as a written one is. A plan
        // containing a step that is going to be refused is not a plan.
        string problem = Rejected(
            Mode("Film", ("audio", "TV")),
            new FakeProvider("audio")
            {
                Requires = new Dictionary<string, ResourceRequirement[]>
                {
                    ["TV"] = [new ResourceRequirement("display", "Nonsense")],
                },
            },
            new FakeProvider("display") { Allowed = ["Desktop", "TVOnly"] }
        );

        Assert.Contains("Nonsense", problem, StringComparison.Ordinal);
    }

    // ---- cycles -------------------------------------------------------------

    [Fact]
    public void Refuses_two_resources_that_each_have_to_go_first()
    {
        string problem = Rejected(
            Mode("Loop", ("audio", "TV"), ("display", "TVOnly")),
            new FakeProvider("audio") { RunsAfter = ["display"] },
            new FakeProvider("display") { RunsAfter = ["audio"] }
        );

        Assert.Contains("audio", problem, StringComparison.Ordinal);
        Assert.Contains("display", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_cycle_that_only_appears_once_requirements_are_resolved()
    {
        // Nothing in the mode is circular. The display is added because the
        // sound needs it, and only then does it turn out that the display has
        // to follow the sound. This is the case an order-as-written engine
        // cannot see at all.
        string problem = Rejected(
            Mode("Loop", ("audio", "TV")),
            new FakeProvider("audio")
            {
                Requires = new Dictionary<string, ResourceRequirement[]>
                {
                    ["TV"] = [new ResourceRequirement("display", "TVOnly")],
                },
            },
            new FakeProvider("display") { RunsAfter = ["audio"] }
        );

        Assert.Contains("audio", problem, StringComparison.Ordinal);
        Assert.Contains("display", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_cycle_of_three()
    {
        string problem = Rejected(
            Mode("Loop", ("a", "1"), ("b", "1"), ("c", "1")),
            new FakeProvider("a") { RunsAfter = ["c"] },
            new FakeProvider("b") { RunsAfter = ["a"] },
            new FakeProvider("c") { RunsAfter = ["b"] }
        );

        Assert.Contains("cannot be ordered", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Does_not_call_a_resource_that_follows_itself_a_cycle()
    {
        // A programming slip in a provider, not a configuration a user can
        // write. Refusing every transition over it would be a poor trade.
        TransitionPlan plan = Plan(Mode("Odd", ("theme", "Dark")), new FakeProvider("theme") { RunsAfter = ["theme"] });

        Assert.Equal(["theme"], Keys(plan));
    }

    // ---- the rest of the request --------------------------------------------

    [Fact]
    public void Refuses_a_resource_nothing_here_can_apply()
    {
        string problem = Rejected(Mode("Broken", ("sound", "Quiet")), new FakeProvider("theme"));

        Assert.Contains("sound", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_value_the_provider_will_not_accept()
    {
        string problem = Rejected(
            Mode("Broken", ("theme", "Purple")),
            new FakeProvider("theme") { Allowed = ["Light", "Dark"] }
        );

        Assert.Contains("Purple", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_mode_that_sets_nothing()
    {
        Assert.Contains("sets nothing", Rejected(Mode("Empty"), new FakeProvider("theme")), StringComparison.Ordinal);
    }

    // ---- the providers this application actually ships -----------------------

    [Fact]
    public void Puts_the_display_before_the_cursor_and_the_sound()
    {
        // The real declarations, not stand-ins. A corner is a different pixel
        // once a monitor has come or gone, and an endpoint carried over a
        // display cable does not exist until the display does -- so both of them
        // wait, and the theme, which cares about none of this, keeps the place
        // the file gave it.
        (ConfiguredMode mode, IModeResourceProvider[] providers) = Real("Film");

        TransitionPlan plan = Plan(mode, providers);

        Assert.Equal(["theme", "display", "cursor", "audio"], Keys(plan));
        Assert.Empty(plan.Added);
    }

    [Fact]
    public void Leaves_the_displays_alone_in_a_mode_that_does_not_mention_them()
    {
        // The other half, and the one that matters more: wanting to run after
        // the displays is not a reason to start changing them. This mode moves
        // the pointer and the sound on the desktop it already has.
        (ConfiguredMode mode, IModeResourceProvider[] providers) = Real("Quiet");

        TransitionPlan plan = Plan(mode, providers);

        Assert.Equal(["theme", "cursor", "audio"], Keys(plan));
        Assert.DoesNotContain(plan.Steps, step => step.Key == "display");
    }

    /// <summary>
    /// A configuration in the shape a machine would really have, read by the
    /// real providers.
    /// </summary>
    private const string RealDocument = """
        {
          "schemaVersion": 1,
          "providers": {
            "cursor":  { "positions": { "Corner": { "xFromRight": 2, "yFromBottom": 200 } } },
            "audio":   { "devices": { "TV": "LG TV (NVIDIA High Definition Audio)" } },
            "display": {
              "monitors": { "TV": "\\\\?\\DISPLAY#TV" },
              "layouts":  { "TVOnly": { "monitors": ["TV"] } }
            }
          },
          "spaces": [
            { "id": "space", "name": "Space", "activities": [
              { "id": "film",  "name": "Film",  "resources": { "cursor": "Corner", "audio": "TV", "theme": "Dark", "display": "TVOnly" } },
              { "id": "quiet", "name": "Quiet", "resources": { "theme": "Dark", "cursor": "Corner", "audio": "TV" } }
            ] }
          ]
        }
        """;

    private static IModeResourceProvider[] RealProviders() =>
        [
            new FakeProvider("theme") { Allowed = ["Light", "Dark"] },
            new CursorProvider(new FakeCursor(), new FakeDisplay(new DisplayRect(0, 0, 1920, 1080))),
            new AudioProvider(new FakeAudioEndpoints()),
            new DisplayProvider(new FakeDisplayDevices()),
        ];

    /// <summary>
    /// A mode, and the very providers that were configured from the document it
    /// came out of.
    /// </summary>
    /// <remarks>
    /// Parsed rather than hand-built, because configuring the providers is half
    /// of what makes their targets valid at all -- and they have to be the same
    /// instances, since a second set would have been told nothing.
    /// </remarks>
    private static (ConfiguredMode Mode, IModeResourceProvider[] Providers) Real(string mode)
    {
        IModeResourceProvider[] providers = RealProviders();

        ConfiguredMode? configured = ModeConfiguration.Parse(RealDocument, providers).Find(mode);

        return (Assert.IsType<ConfiguredMode>(configured), providers);
    }

    // ---- plumbing -----------------------------------------------------------

    private static ConfiguredMode Mode(string name, params (string Key, string Value)[] resources) =>
        new(
            name,
            [.. resources.Select(resource => new ConfiguredResource(resource.Key, resource.Value))],
            new ConfiguredTarget
            {
                Key = name,
                DisplayName = name,
                DisplayPath = name,
                Identity = new TargetIdentity("space", name.ToLowerInvariant(), null),
            }
        );

    private static IReadOnlyList<string> Keys(TransitionPlan plan) => [.. plan.Steps.Select(step => step.Key)];

    private static TransitionPlan Plan(ConfiguredMode mode, params IModeResourceProvider[] providers)
    {
        bool made = TransitionPlan.TryCreate(mode, Index(providers), out TransitionPlan? plan, out string? problem);

        Assert.True(made, problem);

        return plan!;
    }

    private static string Rejected(ConfiguredMode mode, params IModeResourceProvider[] providers)
    {
        bool made = TransitionPlan.TryCreate(mode, Index(providers), out TransitionPlan? plan, out string? problem);

        Assert.False(made, "The plan was accepted.");
        Assert.Null(plan);
        Assert.False(string.IsNullOrWhiteSpace(problem), "A refusal has to say what is wrong.");

        return problem!;
    }

    private static Dictionary<string, IModeResourceProvider> Index(IModeResourceProvider[] providers) =>
        providers.ToDictionary(provider => provider.Key, StringComparer.OrdinalIgnoreCase);
}

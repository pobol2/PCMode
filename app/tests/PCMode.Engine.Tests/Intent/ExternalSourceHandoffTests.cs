namespace PCMode.Engine.Tests;

/// <summary>
/// An External Source -- a console on the television's HDMI input -- reached
/// through Space -&gt; Activity -&gt; Source and driven by the real television
/// providers, not a fake standing in for them.
/// </summary>
/// <remarks>
/// Everything the other hierarchy tests check is checked here too, against the
/// one resource an External Source actually exists for: nothing new was built to
/// support it, so what has to be proven is that the existing television
/// dependency chain -- power before input -- and the existing composition rules
/// -- a Source's own resources, and nothing a sibling Source set -- reach it
/// exactly the way they reach anything else. See <see cref="TargetHierarchy"/>
/// and <see cref="LgTvTests"/>, which this reuses the harness of.
/// </remarks>
public sealed class ExternalSourceHandoffTests
{
    private const string Document =
        """
        {
          "schemaVersion": 1,
          "providers": { "lgTv": { "host": "192.0.2.31", "macAddress": "00:00:5E:00:53:31",
            "inputs": { "PC": "HDMI_4" } } },
          "spaces": [
            {
              "id": "living-room",
              "name": "Living Room",
              "resources": {},
              "activities": [
                {
                  "id": "gaming",
                  "name": "Gaming",
                  "resources": {},
                  "sources": [
                    { "id": "steam", "name": "Steam", "resources": { "theme": "Dark" } },
                    { "id": "switch", "name": "Switch", "kind": "externalDevice",
                      "resources": { "tvInput": "PC" } }
                  ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// A console Source pulls in the television's power dependency exactly the
    /// way a hand-written flat mode naming <c>tvInput</c> always has --
    /// composition through the hierarchy is not a second implementation of that
    /// rule. See <c>LgTvTests.A_mode_naming_only_the_input_still_requires_power</c>.
    /// </summary>
    [Fact]
    public void The_external_sources_own_input_requires_the_televisions_power_and_nothing_else()
    {
        using var world = new World();

        ConfiguredMode mode = Find(world, "living-room/gaming/switch");

        Assert.Equal(["tvInput"], mode.Resources.Select(resource => resource.Key));

        Assert.True(TransitionPlan.TryCreate(
            mode,
            world.Engine.Providers.ToDictionary(provider => provider.Key, StringComparer.OrdinalIgnoreCase),
            out TransitionPlan? plan,
            out string? problem));

        Assert.Equal(["tvPower", "tvInput"], plan!.Steps.Select(step => step.Key));
        Assert.Equal("On", plan.Steps[0].Target);
        Assert.Equal("tvInput", plan.Steps[0].RequiredBy);
    }

    /// <summary>A PC Source beside it is untouched: no television key, kind unchanged.</summary>
    [Fact]
    public void A_pc_source_beside_it_carries_no_television_resource_of_its_own()
    {
        using var world = new World();

        ConfiguredMode mode = Find(world, "living-room/gaming/steam");

        Assert.Equal(["theme"], mode.Resources.Select(resource => resource.Key));
    }

    /// <summary>
    /// The acceptance criterion this exists for: switching to the console and
    /// back both actually plan correctly through the real engine, over the real
    /// dependency chain, and neither leaves anything belonging to the other
    /// Source in the plan by mistake.
    /// </summary>
    /// <remarks>
    /// A dry run, like <c>LgTvTests.A_dry_run_wakes_nothing_and_prompts_nobody</c>:
    /// what is being proven is composition and ordering through
    /// <see cref="ModeEngine"/>, not the television's own pairing and readback
    /// handshake, which <see cref="LgTvProviderSetTests"/> and
    /// <c>LgTvTests</c> already cover on their own.
    /// </remarks>
    [Fact]
    public async Task Switching_from_a_pc_source_to_the_console_and_back_both_plan_correctly()
    {
        using var world = new World();

        TransitionOutcome toSteam = await world.Engine.ApplyAsync("living-room/gaming/steam", dryRun: true);
        Assert.Equal(TransitionStatus.Succeeded, toSteam.Status);
        Assert.DoesNotContain(toSteam.Steps, step => step.Resource is "tvPower" or "tvInput");

        TransitionOutcome toSwitch = await world.Engine.ApplyAsync("living-room/gaming/switch", dryRun: true);
        Assert.Equal(TransitionStatus.Succeeded, toSwitch.Status);

        // Power before input, in that order -- the chain the whole feature
        // exists to produce -- and nothing else.
        Assert.Equal(["tvPower", "tvInput"], toSwitch.Steps.Select(step => step.Resource));

        // Going to the console did not also plan the theme Steam owns -- an
        // External Source is not carrying PC Source state around with it.
        Assert.DoesNotContain(toSwitch.Steps, step => step.Resource == "theme");

        TransitionOutcome backToSteam = await world.Engine.ApplyAsync("living-room/gaming/steam", dryRun: true);
        Assert.Equal(TransitionStatus.Succeeded, backToSteam.Status);

        // And the return trip does not re-touch the television either: Steam
        // never asked for it.
        Assert.DoesNotContain(backToSteam.Steps, step => step.Resource is "tvPower" or "tvInput");

        // None of the above is allowed to have touched the television at all --
        // a dry run's entire promise, exactly as it is everywhere else.
        Assert.Empty(world.Set.Woken);
        Assert.Equal(0, world.Set.Prompts);
        Assert.Equal(0, world.Set.Connections);
    }

    private static ConfiguredMode Find(World world, string key) =>
        world.Configuration.Find(key) ?? throw new InvalidOperationException($"'{key}' is not a configured target.");

    /// <summary>The real television providers, a real engine, nothing faked but the network.</summary>
    private sealed class World : IDisposable
    {
        public World()
        {
            Root = new TemporaryRoot();
            Root.Write(Document);
            Set = new FakeTelevision();

            var connection = new LgTvConnection(Set, new FakeCredentials());

            IReadOnlyList<IModeResourceProvider> providers =
            [
                new TvPowerProvider(connection),
                new TvInputProvider(connection),
                new TvSignalProvider(connection),
                new TvProfileProvider(connection),
                new DisplayProvider(new FakeDisplayDevices()),
                new FakeProvider("theme"),
            ];

            Configuration = ModeConfiguration.LoadOrCreate(Root.Path, providers);

            Engine = new ModeEngine(
                Configuration,
                new ModeStateStore(Root.StateFile),
                providers,
                new FakeTime());
        }

        public TemporaryRoot Root { get; }

        public FakeTelevision Set { get; }

        public ModeConfiguration Configuration { get; }

        public ModeEngine Engine { get; }

        public void Dispose() => Root.Dispose();
    }
}

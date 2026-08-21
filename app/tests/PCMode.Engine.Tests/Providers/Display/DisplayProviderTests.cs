using System.Text.Json;

namespace PCMode.Engine.Tests;

/// <summary>
/// What the display provider will and will not do, with a made-up machine.
/// </summary>
public sealed class DisplayProviderTests
{
    // Opaque to the provider, which never interprets one: it is matched against
    // whatever Windows reports. Two of these monitors share a friendly name on
    // purpose -- that is precisely why the path is the identity and the name is
    // not.
    private const string Desk1 = "DISPLAY#DEL4321#5&1a2b3c&0&UID4353";
    private const string Desk2 = "DISPLAY#DEL4321#5&1a2b3c&0&UID4354";
    private const string Tv = "DISPLAY#GSM7700#5&1a2b3c&0&UID4355";

    // ---- configuration ------------------------------------------------------

    [Fact]
    public void Stays_out_of_the_way_when_nothing_asks_for_a_layout()
    {
        // A machine with one monitor has no reason to configure this, and a
        // provider that made itself mandatory would stop such a machine starting.
        ModeConfiguration configuration = Parse(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
              {"id":"film","name":"Film","resources":{"theme":"Dark"}}]}]}
            """,
            new DisplayProvider(Machine()));

        Assert.NotNull(configuration.Find("Film"));
    }

    [Fact]
    public void Refuses_a_mode_that_asks_for_a_layout_when_none_is_configured()
    {
        // The other half of the rule above. Unconfigured is fine until a mode
        // depends on it, and then it is refused by name rather than at the moment
        // the screens were supposed to change.
        ModeConfigurationException failure = Assert.Throws<ModeConfigurationException>(() => Parse(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
              {"id":"film","name":"Film","resources":{"display":"TVOnly"}}]}]}
            """,
            new DisplayProvider(Machine())));

        Assert.Contains(DisplayProvider.LayoutsPath, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"desktop\"")]
    [InlineData("""{"monitor":{"A":"a"}}""")]
    [InlineData("""{"monitors":[]}""")]
    [InlineData("""{"layouts":[]}""")]
    public void Refuses_settings_it_does_not_understand(string settings) =>
        Assert.NotNull(Configure(new DisplayProvider(Machine()), settings));

    [Theory]
    [InlineData("""{"monitors":{"TV":""}}""")]
    [InlineData("""{"monitors":{"TV":"   "}}""")]
    [InlineData("""{"monitors":{"TV":42}}""")]
    public void Refuses_a_monitor_that_maps_to_nothing_usable(string settings)
    {
        // An entry that exists but says nothing is worse than a missing one:
        // every layout using the name still validates, and the failure waits
        // until a transition is already under way.
        string? problem = Configure(new DisplayProvider(Machine()), settings);

        Assert.NotNull(problem);
        Assert.Contains("TV", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_two_names_for_the_same_monitor()
    {
        // Two layouts built on them would be indistinguishable, and the readback
        // could not say which of them the machine was in.
        string? problem = Configure(
            new DisplayProvider(Machine()),
            $$"""{ "monitors": { "TV": "{{Tv}}", "Cinema": "{{Tv}}" } }""");

        Assert.NotNull(problem);
        Assert.Contains("TV", problem, StringComparison.Ordinal);
        Assert.Contains("Cinema", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_layout_that_names_a_monitor_nothing_defines()
    {
        string? problem = Configure(
            new DisplayProvider(Machine()),
            $$"""
            {
              "monitors": { "Desk1": "{{Desk1}}" },
              "layouts": {
                "Desktop": {
                  "monitors": ["Desk1", "Projector"],
                  "placement": ["Projector RightOf Desk1"]
                }
              }
            }
            """);

        Assert.NotNull(problem);
        Assert.Contains("Projector", problem, StringComparison.Ordinal);
        Assert.Contains(DisplayProvider.MonitorsPath, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"Desktop\"", "must be an object")]
    [InlineData("""{"monitors":[]}""", "lists no monitors")]
    [InlineData("""{"monitors":["A","A"]}""", "more than once")]
    [InlineData("""{"monitors":["A"],"primary":"B"}""", "primary")]
    [InlineData("""{"monitors":["A","B"]}""", "no placement")]
    [InlineData("""{"monitors":["A","B"],"placement":["B A"]}""", "RightOf")]
    [InlineData("""{"monitors":["A","B"],"placement":["B RightOf B"]}""", "relative to itself")]
    [InlineData("""{"monitors":["A","B"],"placement":["B RightOf Nowhere"]}""", "not one of its monitors")]
    [InlineData("""{"monitors":["A","B"],"placement":["A RightOf B","B RightOf A"]}""", "sits at the origin")]
    [InlineData("""{"monitors":["A","B","C"],"placement":["B RightOf C","C RightOf B"]}""", "loop")]
    [InlineData("""{"monitors":["A"],"refresh":["A fast"]}""", "positive number of Hz")]
    [InlineData("""{"monitors":["A"],"refresh":["B 60"]}""", "not one of its monitors")]
    [InlineData("""{"monitors":["A"],"refresh":["A 60","A 120"]}""", "more than one refresh rate")]
    [InlineData("""{"monitors":["A"],"tilt":["A 5"]}""", "does not understand")]
    public void Refuses_a_layout_it_could_not_arrange(string layout, string expected)
    {
        // Every one of these is a fact about the document, so it is refused when
        // the configuration is read rather than when a mode is chosen. A layout
        // that cannot be laid out has no moment at which it becomes applicable.
        string? problem = Configure(
            new DisplayProvider(Machine()),
            $$"""{ "monitors": { "A": "a", "B": "b", "C": "c" }, "layouts": { "L": {{layout}} } }""");

        Assert.NotNull(problem);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_two_layouts_that_ask_for_the_same_thing()
    {
        // Whichever the readback picked, the other could never report as current
        // no matter what the machine did.
        string? problem = Configure(
            new DisplayProvider(Machine()),
            $$"""
            {
              "monitors": { "TV": "{{Tv}}" },
              "layouts": {
                "TVOnly": { "monitors": ["TV"] },
                "Cinema": { "monitors": ["TV"] }
              }
            }
            """);

        Assert.NotNull(problem);
        Assert.Contains("TVOnly", problem, StringComparison.Ordinal);
        Assert.Contains("Cinema", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_the_layouts_it_has_when_a_mode_asks_for_another()
    {
        DisplayProvider provider = Provider(Machine());

        Assert.False(provider.IsValidValue("Projector", out string? problem));

        Assert.NotNull(problem);
        Assert.Contains("Desktop", problem, StringComparison.Ordinal);
        Assert.Contains("TVOnly", problem, StringComparison.Ordinal);
        Assert.Equal(["Desktop", "TVOnly"], provider.LayoutNames);
    }

    // ---- discovery ----------------------------------------------------------

    [Fact]
    public void Describes_every_monitor_it_knows_about()
    {
        FakeDisplayDevices machine = InDesktopLayout();

        DisplaySnapshot snapshot = Provider(machine).Discover();

        Assert.True(snapshot.Available);
        Assert.True(snapshot.Configured);
        Assert.Equal("Desktop", snapshot.CurrentLayout);

        DisplayMonitorInfo desk1 = Monitor(snapshot, Desk1);

        Assert.True(desk1.Active);
        Assert.True(desk1.Primary);
        Assert.Equal("Desk1", desk1.LogicalName);
        Assert.Equal((2560, 1440), (desk1.Width, desk1.Height));
        Assert.Equal(144, desk1.RefreshHz);
        Assert.Equal([60, 144], desk1.SupportedRefreshHz);

        DisplayMonitorInfo desk2 = Monitor(snapshot, Desk2);

        Assert.False(desk2.Primary);
        Assert.Equal((2560, 0), (desk2.X, desk2.Y));

        // The television has never been on this session, which is the ordinary
        // state of a television rather than a fault. Windows still has a slot for
        // it -- unlike an audio endpoint, which vanishes entirely -- and that is
        // what lets a layout switch to it.
        DisplayMonitorInfo television = Monitor(snapshot, Tv);

        Assert.False(television.Active);
        Assert.Equal("TV", television.LogicalName);
        Assert.Empty(television.SupportedRefreshHz);
    }

    [Fact]
    public void Says_which_configured_monitors_are_actually_there()
    {
        FakeDisplayDevices machine = Machine();

        DisplayProvider provider = new(machine);

        Assert.Null(Configure(
            provider,
            $$"""{ "monitors": { "Desk1": "{{Desk1}}", "Projector": "one that was sold" } }"""));

        DisplaySnapshot snapshot = provider.Discover();

        Assert.Equal(DisplayMappingState.Ready, Mapping(snapshot, "Desk1").State);
        Assert.Equal(DisplayMappingState.Missing, Mapping(snapshot, "Projector").State);
    }

    [Fact]
    public void Reports_displays_as_unreadable_rather_than_as_monitors_that_are_missing()
    {
        // "Windows did not answer" and "that monitor is not plugged in" call for
        // different things from a user, and reporting the first as the second
        // would be a claim about the machine that nothing checked.
        FakeDisplayDevices machine = InDesktopLayout();
        machine.Fails = new InvalidOperationException("Windows could not count the monitors (error 87).");

        DisplaySnapshot snapshot = Provider(machine).Discover();

        Assert.False(snapshot.Available);
        Assert.Contains("error 87", snapshot.Problem!, StringComparison.Ordinal);
        Assert.Empty(snapshot.Monitors);
        Assert.Null(snapshot.CurrentLayout);
        Assert.All(snapshot.Mappings, mapping => Assert.Equal(DisplayMappingState.Unknown, mapping.State));
    }

    // ---- readback -----------------------------------------------------------

    [Fact]
    public void Reads_the_layout_the_machine_is_in() =>
        Assert.Equal("Desktop", Provider(InDesktopLayout()).Read());

    [Fact]
    public void Reads_nothing_when_a_monitor_nobody_asked_for_is_also_on()
    {
        // A layout is not what the machine is in while an extra screen is lit:
        // reporting it as current would make a half-applied desktop look settled.
        FakeDisplayDevices machine = InDesktopLayout();
        machine[Tv].Active = true;
        machine[Tv].Y = -2160;

        Assert.Null(Provider(machine).Read());
    }

    [Fact]
    public void Reads_nothing_when_a_monitor_is_in_the_wrong_place()
    {
        FakeDisplayDevices machine = InDesktopLayout();
        machine[Desk2].X = -1920;

        Assert.Null(Provider(machine).Read());
    }

    [Fact]
    public void Reads_nothing_when_a_rate_the_layout_asks_for_is_not_being_run()
    {
        // A drifted rate is not a cosmetic difference: it is the thing the layout
        // was chosen for, so the machine is not in that layout.
        FakeDisplayDevices machine = InDesktopLayout();
        machine[Desk1].Hz = 60;

        Assert.Null(Provider(machine).Read());
    }

    [Fact]
    public void Counts_a_rate_windows_reports_as_a_fraction_as_the_one_that_was_asked_for()
    {
        // Windows commonly reports NTSC-derived rates: 143.856 for 144, 59.94 for
        // 60. Comparing those for equality would leave a machine permanently one
        // step away from a layout it is already in.
        FakeDisplayDevices machine = InDesktopLayout();
        machine[Desk1].Hz = 143.856;

        Assert.Equal("Desktop", Provider(machine).Read());
    }

    [Fact]
    public void Reads_nothing_when_the_monitors_cannot_be_read_at_all()
    {
        // Null means "apply it anyway", which is the right answer here: a
        // transition can still go ahead, and must not be stopped by a readback.
        FakeDisplayDevices machine = InDesktopLayout();
        machine.Fails = new InvalidOperationException("no");

        Assert.Null(Provider(machine).Read());
    }

    // ---- applying -----------------------------------------------------------

    [Fact]
    public async Task Switches_the_monitors_on_and_then_puts_them_where_the_layout_says()
    {
        using var root = new TemporaryRoot();

        FakeDisplayDevices machine = InTelevisionLayout();

        TransitionOutcome outcome = await Engine(root, machine).ApplyAsync("Work");

        Assert.True(outcome.Succeeded);

        // One topology change, asking for exactly the layout's monitors and the
        // one rate it names. Zero is "no opinion" -- the layout says nothing about
        // the second monitor's rate, so nothing is imposed on it.
        ActivationCall activation = Assert.Single(machine.Activations);
        Assert.Equal([Desk1, Desk2], activation.Paths);
        Assert.Equal([144, 0], activation.RefreshHz);

        // Then, and only then, the arrangement: the second monitor's position is
        // the first one's width, which is why no layout has to know a resolution.
        ArrangementCall arrangement = Assert.Single(machine.Arrangements);
        Assert.Equal([0, 2560], arrangement.X);
        Assert.Equal([0, 0], arrangement.Y);

        TransitionStep step = Assert.Single(
            outcome.Steps,
            reported => reported.Resource == DisplayProvider.ResourceKey);

        Assert.Equal(TransitionStepStatus.Applied, step.Status);
        Assert.Equal("Desktop", step.Target);
        Assert.Equal("TVOnly", step.Current);
        Assert.Equal("Display layout", step.ResourceName);

        Assert.True(outcome.StateRecorded);
    }

    [Fact]
    public async Task Leaves_the_screens_alone_when_the_layout_is_already_current()
    {
        // Re-applying a mode must be a genuine no-op. Blanking the screens to
        // arrive where the machine already was is the most visible way a
        // transition can be wrong while reporting success.
        using var root = new TemporaryRoot();

        FakeDisplayDevices machine = InDesktopLayout();

        TransitionOutcome outcome = await Engine(root, machine).ApplyAsync("Work");

        Assert.True(outcome.Succeeded);
        Assert.True(machine.Untouched);

        Assert.Equal(
            TransitionStepStatus.AlreadyCorrect,
            Assert.Single(outcome.Steps, step => step.Resource == DisplayProvider.ResourceKey).Status);
    }

    [Fact]
    public void Moves_the_monitors_without_switching_anything_off_to_do_it()
    {
        // The right monitors are on and at the right rate; only the arrangement
        // is wrong. Re-running the topology phase would black the screens out on
        // the way to a change that needed nothing of the kind.
        FakeDisplayDevices machine = InDesktopLayout();
        machine[Desk2].X = -1920;

        Provider(machine).Apply("Desktop", CancellationToken.None);

        Assert.Empty(machine.Activations);
        Assert.Single(machine.Arrangements);
        Assert.Equal(2560, machine[Desk2].X);
    }

    [Fact]
    public async Task Fails_the_transition_when_a_monitor_is_not_there()
    {
        // The television is off. Validation could not have caught this: it is a
        // fact about the room, not about the file.
        using var root = new TemporaryRoot();

        FakeDisplayDevices machine = InDesktopLayout();
        machine[Tv].Available = false;

        TransitionOutcome outcome = await Engine(root, machine).ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Empty(machine.Arrangements);

        // Nothing was recorded, so nothing claims the machine is in that mode.
        Assert.False(outcome.StateRecorded);
        Assert.False(File.Exists(root.StateFile));
    }

    [Fact]
    public async Task Asks_for_a_slow_television_once_and_then_waits_for_it()
    {
        // THE thing a topology change must not do. Windows accepts the request
        // and returns; the picture arrives seconds later, and until it does the
        // configuration reads as though nothing happened. A provider that called
        // that a failure would have the whole topology issued again -- another
        // round of black screens -- for no better reason than that a television
        // takes longer to wake than an API call takes to return.
        using var root = new TemporaryRoot();

        FakeDisplayDevices machine = new FakeDisplayDevices()
            .Add(new FakeMonitor { Path = Desk1, Name = "DELL U2720Q", Width = 2560, Height = 1440, Supported = [60, 144] })
            .Add(new FakeMonitor { Path = Desk2, Name = "DELL U2720Q", Width = 1920, Height = 1080, Supported = [60] })
            .Add(new FakeMonitor
            {
                Path = Tv,
                Name = "LG TV SSCR2",
                Width = 3840,
                Height = 2160,
                Supported = [60, 120],
                WakesAfter = 4,
            });

        machine[Desk1].Active = true;
        machine[Desk1].Hz = 144;
        machine[Desk2].Active = true;
        machine[Desk2].X = 2560;

        TransitionOutcome outcome = await Engine(root, machine).ApplyAsync("Film");

        Assert.True(outcome.Succeeded);

        // One. Not one per readback that had not caught up yet.
        Assert.Single(machine.Activations);

        TransitionStep step = Assert.Single(
            outcome.Steps,
            reported => reported.Resource == DisplayProvider.ResourceKey);

        Assert.Equal(TransitionStepStatus.Applied, step.Status);
        Assert.Equal(1, step.Attempts);
        Assert.Equal("TVOnly", step.Observed);
    }

    [Fact]
    public void Refuses_to_call_it_done_when_windows_gives_it_a_different_rate()
    {
        // Windows does not refuse an unsupported rate for a topology change --
        // strict validation is not available for one -- it accepts the request
        // and delivers something else. Reading back is the only way to know, and
        // nothing above this retries, so a rate not checked here would be
        // reported as a success that did not happen.
        FakeDisplayDevices machine = Machine();

        var provider = new DisplayProvider(machine);

        Assert.Null(Configure(
            provider,
            $$"""
            {
              "monitors": { "TV": "{{Tv}}" },
              "layouts": { "Cinema": { "monitors": ["TV"], "refresh": ["TV 240"] } }
            }
            """));

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => provider.Apply("Cinema", CancellationToken.None));

        Assert.Contains("240", failure.Message, StringComparison.Ordinal);
        Assert.Contains("60", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_to_call_it_done_when_the_monitors_do_not_actually_move()
    {
        // The same rule for the second phase: the arrangement is requested, and
        // then confirmed to have happened before this returns.
        FakeDisplayDevices machine = InDesktopLayout();
        machine[Desk2].X = 0;
        machine.IgnoresArrange = true;

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => Provider(machine).Apply("Desktop", CancellationToken.None));

        Assert.Single(machine.Arrangements);
        Assert.Contains("Desk2", failure.Message, StringComparison.Ordinal);
    }

    // ---- helpers ------------------------------------------------------------

    private static readonly string Settings =
        $$"""
        {
          "monitors": { "Desk1": "{{Desk1}}", "Desk2": "{{Desk2}}", "TV": "{{Tv}}" },
          "layouts": {
            "Desktop": {
              "monitors": ["Desk1", "Desk2"],
              "primary": "Desk1",
              "placement": ["Desk2 RightOf Desk1"],
              "refresh": ["Desk1 144"]
            },
            "TVOnly": { "monitors": ["TV"], "refresh": ["TV 60"] }
          }
        }
        """;

    private static readonly string Document =
        $$"""
        {
          "schemaVersion": 1,
          "providers": { "display": {{Settings}} },
          "spaces": [
            { "id": "space", "name": "Space", "activities": [
              { "id": "work", "name": "Work", "resources": { "theme": "Light", "display": "Desktop" } },
              { "id": "film", "name": "Film", "resources": { "theme": "Dark", "display": "TVOnly" } }
            ] }
          ]
        }
        """;

    /// <summary>Three monitors, two of which report the identical name.</summary>
    private static FakeDisplayDevices Machine() =>
        new FakeDisplayDevices()
            .Add(new FakeMonitor { Path = Desk1, Name = "DELL U2720Q", Width = 2560, Height = 1440, Supported = [60, 144] })
            .Add(new FakeMonitor { Path = Desk2, Name = "DELL U2720Q", Width = 1920, Height = 1080, Supported = [60] })
            .Add(new FakeMonitor { Path = Tv, Name = "LG TV SSCR2", Width = 3840, Height = 2160, Supported = [60, 120] });

    private static FakeDisplayDevices InDesktopLayout()
    {
        FakeDisplayDevices machine = Machine();

        machine[Desk1].Active = true;
        machine[Desk1].Hz = 144;

        machine[Desk2].Active = true;
        machine[Desk2].X = 2560;

        return machine;
    }

    private static FakeDisplayDevices InTelevisionLayout()
    {
        FakeDisplayDevices machine = Machine();

        machine[Tv].Active = true;

        return machine;
    }

    private static DisplayMonitorInfo Monitor(DisplaySnapshot snapshot, string path) =>
        Assert.Single(snapshot.Monitors, monitor => monitor.DevicePath == path);

    private static DisplayMonitorMapping Mapping(DisplaySnapshot snapshot, string logical) =>
        Assert.Single(snapshot.Mappings, mapping => mapping.LogicalName == logical);

    private static ModeEngine Engine(TemporaryRoot root, FakeDisplayDevices machine)
    {
        root.Write(Document);

        IReadOnlyList<IModeResourceProvider> providers =
            [new FakeProvider("theme", "Dark"), new DisplayProvider(machine)];

        // A fake clock, because this provider's convergence policy settles for
        // well over a second and these tests are about what it does, not how
        // long it is prepared to wait to do it.
        return new ModeEngine(
            ModeConfiguration.LoadOrCreate(root.Path, providers),
            new ModeStateStore(root.StateFile),
            providers,
            new FakeTime());
    }

    private static ModeConfiguration Parse(string json, DisplayProvider display) =>
        ModeConfiguration.Parse(json, [new FakeProvider("theme") { Allowed = ["Light", "Dark"] }, display]);

    private static DisplayProvider Provider(FakeDisplayDevices machine)
    {
        var provider = new DisplayProvider(machine);

        Assert.Null(Configure(provider, Settings));

        return provider;
    }

    /// <summary>The refusal, or null when the settings were accepted.</summary>
    private static string? Configure(DisplayProvider provider, string settings)
    {
        using JsonDocument document = JsonDocument.Parse(settings);

        bool accepted = provider.TryConfigure(document.RootElement, out string? problem);

        Assert.Equal(accepted, problem is null);

        return problem;
    }
}

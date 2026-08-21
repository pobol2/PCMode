namespace PCMode.Engine.Tests;

/// <summary>
/// The rules a transition has to keep, whoever asked for it.
/// </summary>
public sealed class ModeEngineTests
{
    // ---- lookup and validation, before anything is dispatched --------------

    [Fact]
    public async Task Refuses_a_mode_it_does_not_have()
    {
        using var world = new World();

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Nonsense");

        Assert.Equal(TransitionStatus.Refused, outcome.Status);
        Assert.Empty(world.Theme.Applied);
        Assert.Contains("space/desktop", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changes_nothing_when_one_resource_of_a_mode_is_unusable()
    {
        // THE refusal-order rule. A mode with one good resource and one bad one
        // must not apply the good one first and then discover the problem: that
        // leaves the machine somewhere nobody asked for.
        //
        // Reachable even though the configuration was validated when it was
        // loaded, because the two checks are not the same one. Here the second
        // provider is present at load and gone at run -- which is what
        // "validate again before dispatching" is actually for.
        using var root = new TemporaryRoot();

        root.Write(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"broken","name":"Broken","resources":{"theme":"Dark","sound":"Quiet"}}]}]}
            """);

        var theme = new FakeProvider("theme", "Light");
        var sound = new FakeProvider("sound");

        ModeConfiguration configuration = ModeConfiguration.LoadOrCreate(root.Path, [theme, sound]);

        var engine = new ModeEngine(configuration, new ModeStateStore(root.StateFile), [theme]);

        TransitionOutcome outcome = await engine.ApplyAsync("Broken");

        Assert.Equal(TransitionStatus.Refused, outcome.Status);
        Assert.Contains("sound", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(theme.Applied);
        Assert.False(File.Exists(root.StateFile));
    }

    [Fact]
    public async Task Refuses_a_mode_that_sets_nothing()
    {
        // An empty plan is not a successful transition, and above all must not
        // record a mode: nothing about the machine would have changed.
        using var world = new World("""{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"empty","name":"Empty","resources":{}}]}]}""");

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Empty");

        Assert.Equal(TransitionStatus.Refused, outcome.Status);
        Assert.False(File.Exists(world.Root.StateFile));
    }

    [Fact]
    public async Task Changes_nothing_when_a_mode_cannot_be_ordered()
    {
        // The refusal-order rule, now that resources can depend on each other.
        // A cycle is not visible from any one resource, so an engine that walked
        // the list would apply the first, the second, and then discover there is
        // no order -- with the machine already half moved.
        var order = new List<string>();
        var first = new RecordingProvider("first", order) { After = ["second"] };
        var second = new RecordingProvider("second", order) { After = ["first"] };

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"both","name":"Both","resources":{"first":"a","second":"b"}}]}]}
            """,
            providers: [first, second]);

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Both");

        Assert.Equal(TransitionStatus.Refused, outcome.Status);
        Assert.Empty(order);
        Assert.False(File.Exists(world.Root.StateFile));
    }

    [Fact]
    public async Task Changes_nothing_when_a_mode_contradicts_what_it_needs()
    {
        // Refused with the machine untouched, like every other unresolvable
        // request -- and before either provider is asked to do anything.
        var order = new List<string>();

        var sound = new RecordingProvider("sound", order)
        {
            Requires = new Dictionary<string, ResourceRequirement[]>
            {
                ["TV"] = [new ResourceRequirement("screen", "TVOnly")],
            },
        };

        var screen = new RecordingProvider("screen", order);

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"muddle","name":"Muddle","resources":{"screen":"Desktop","sound":"TV"}}]}]}
            """,
            providers: [sound, screen]);

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Muddle");

        Assert.Equal(TransitionStatus.Refused, outcome.Status);
        Assert.Empty(order);
        Assert.False(File.Exists(world.Root.StateFile));
    }

    // ---- dependencies ------------------------------------------------------

    [Fact]
    public async Task Applies_a_resource_the_mode_never_named_because_something_needed_it()
    {
        var order = new List<string>();

        var sound = new RecordingProvider("sound", order)
        {
            Requires = new Dictionary<string, ResourceRequirement[]>
            {
                ["TV"] = [new ResourceRequirement("screen", "TVOnly")],
            },
        };

        var screen = new RecordingProvider("screen", order);

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"film","name":"Film","resources":{"sound":"TV"}}]}]}
            """,
            providers: [sound, screen]);

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.Equal(["screen", "sound"], order);
        Assert.Equal(["TVOnly"], screen.Applied);

        // And it says whose idea the extra step was. A transition report listing
        // a change the mode never mentions, with nothing to account for it, is
        // how a user learns to distrust the report.
        TransitionStep added = Assert.Single(outcome.Steps, step => step.Resource == "screen");

        Assert.Equal("sound", added.RequiredBy);
        Assert.Null(Assert.Single(outcome.Steps, step => step.Resource == "sound").RequiredBy);
    }

    [Fact]
    public async Task Records_the_mode_only_once_an_added_resource_has_worked_too()
    {
        // A required resource is a real step, so failing it fails the mode. The
        // sound is never touched, and nothing is recorded: a mode recorded here
        // would name a state the machine is not in.
        var order = new List<string>();

        var sound = new RecordingProvider("sound", order)
        {
            Requires = new Dictionary<string, ResourceRequirement[]>
            {
                ["TV"] = [new ResourceRequirement("screen", "TVOnly")],
            },
        };

        var screen = new RecordingProvider("screen", order) { Fails = new InvalidOperationException("no picture") };

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"film","name":"Film","resources":{"sound":"TV"}}]}]}
            """,
            providers: [sound, screen]);

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Empty(sound.Applied);
        Assert.False(outcome.StateRecorded);
        Assert.Null(world.Engine.ReadCurrentTargetKey(out _));
    }

    [Fact]
    public async Task Walks_an_added_resource_without_touching_it()
    {
        // A dry run has to show the whole transition, including the parts the
        // mode did not ask for -- that is precisely what somebody runs one to
        // find out -- and still change nothing.
        var order = new List<string>();

        var sound = new RecordingProvider("sound", order)
        {
            Requires = new Dictionary<string, ResourceRequirement[]>
            {
                ["TV"] = [new ResourceRequirement("screen", "TVOnly")],
            },
        };

        var screen = new RecordingProvider("screen", order);

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"film","name":"Film","resources":{"sound":"TV"}}]}]}
            """,
            providers: [sound, screen]);

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film", dryRun: true);

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.Equal(2, outcome.Steps.Count);
        Assert.All(outcome.Steps, step => Assert.Equal(TransitionStepStatus.Planned, step.Status));

        Assert.Empty(order);
        Assert.False(File.Exists(world.Root.StateFile));
    }

    [Fact]
    public async Task Does_not_change_a_required_resource_that_is_already_right()
    {
        // Adding a requirement to the plan was never a decision to change it.
        var order = new List<string>();

        var sound = new RecordingProvider("sound", order)
        {
            Requires = new Dictionary<string, ResourceRequirement[]>
            {
                ["TV"] = [new ResourceRequirement("screen", "TVOnly")],
            },
        };

        var screen = new RecordingProvider("screen", order) { Current = "TVOnly" };

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"film","name":"Film","resources":{"sound":"TV"}}]}]}
            """,
            providers: [sound, screen]);

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.Equal(["sound"], order);

        Assert.Equal(
            TransitionStepStatus.AlreadyCorrect,
            Assert.Single(outcome.Steps, step => step.Resource == "screen").Status);
    }

    // ---- execution ---------------------------------------------------------

    [Fact]
    public async Task Applies_a_mode_and_records_it()
    {
        using var world = new World();

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Movie");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.Equal(["Dark"], world.Theme.Applied);
        Assert.True(outcome.StateRecorded);
        Assert.Equal("space/movie", world.Engine.ReadCurrentTargetKey(out _));
    }

    [Fact]
    public async Task Applies_resources_in_configuration_order()
    {
        var order = new List<string>();

        var first = new RecordingProvider("first", order);
        var second = new RecordingProvider("second", order);

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"both","name":"Both","resources":{"first":"a","second":"b"}}]}]}
            """,
            providers: [first, second]);

        await world.Engine.ApplyAsync("Both");

        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public async Task Does_not_dispatch_a_resource_that_is_already_where_it_should_be()
    {
        using var world = new World();
        world.Theme.Current = "Dark";

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Movie");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.Empty(world.Theme.Applied);
        Assert.Equal(TransitionStepStatus.AlreadyCorrect, Assert.Single(outcome.Steps).Status);

        // Still a real transition, so the mode is still recorded. Otherwise
        // switching to the mode you are already in would leave the recorded
        // name behind.
        Assert.True(outcome.StateRecorded);
    }

    [Fact]
    public async Task Applies_a_resource_that_cannot_say_where_it_is()
    {
        // Null from Read means "apply it anyway". A provider that cannot report
        // itself is not a broken one.
        using var world = new World();
        world.Theme.Current = null;

        await world.Engine.ApplyAsync("Movie");

        Assert.Equal(["Dark"], world.Theme.Applied);
    }

    [Fact]
    public async Task Does_not_let_a_failed_read_stop_a_transition()
    {
        using var world = new World(providers:
        [
            new FakeProvider("theme") { ReadThrows = true, Allowed = ["Light", "Dark"] },
        ]);

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Movie");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
    }

    // ---- failure -----------------------------------------------------------

    [Fact]
    public async Task Stops_at_the_first_failure_and_records_nothing()
    {
        var order = new List<string>();
        var first = new RecordingProvider("first", order) { Fails = new InvalidOperationException("no") };
        var second = new RecordingProvider("second", order);

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"both","name":"Both","resources":{"first":"a","second":"b"}}]}]}
            """,
            providers: [first, second]);

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Both");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);

        // Carrying on would apply the rest of a mode that is already not going
        // to be reached, spreading the mess rather than containing it.
        Assert.Empty(second.Applied);
        Assert.False(outcome.StateRecorded);
        Assert.Null(world.Engine.ReadCurrentTargetKey(out _));

        Assert.Contains(outcome.Steps, step => step.Status == TransitionStepStatus.NotReached);
    }

    [Fact]
    public async Task Reports_a_converged_transition_it_could_not_record_as_uncertain()
    {
        // The machine really did change, so this is not a failure -- but what a
        // UI would display as current is no longer trustworthy, which is a
        // different thing and has to be shown as one.
        using var world = new World();

        Directory.CreateDirectory(world.Root.StateFile);

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Movie");

        Assert.Equal(["Dark"], world.Theme.Applied);
        Assert.Equal(TransitionStatus.Uncertain, outcome.Status);
        Assert.False(outcome.StateRecorded);
        Assert.NotNull(outcome.StateProblem);
    }

    // ---- dry run -----------------------------------------------------------

    [Fact]
    public async Task Walks_a_mode_without_touching_anything()
    {
        using var world = new World();

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Movie", dryRun: true);

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.True(outcome.DryRun);
        Assert.Empty(world.Theme.Applied);
        Assert.False(outcome.StateRecorded);
        Assert.False(File.Exists(world.Root.StateFile));
        Assert.Equal(TransitionStepStatus.Planned, Assert.Single(outcome.Steps).Status);
    }

    // ---- one at a time, newest wins -----------------------------------------

    [Fact]
    public async Task Supersedes_a_running_transition_with_a_newer_one()
    {
        // Not queued behind, and not simply refused either: the newer request
        // cancels the older one at its next safe boundary -- between resources,
        // never mid-command -- and takes the slot once it yields.
        var order = new List<string>();
        ModeEngine? engine = null;
        Task<TransitionOutcome>? newerRun = null;
        bool superseded1 = false;

        // Fired synchronously from inside "first"'s Apply, which is itself
        // running on the transition's own worker thread. By the time "first"
        // returns, the older transition's token has already been cancelled --
        // proving the cancellation reached it before "second" was ever
        // dispatched, not merely by the time the test got around to checking.
        //
        // "first" is also the resource "Desktop" sets, so this must fire only
        // once -- otherwise the superseding request's own dispatch of "first"
        // would trigger another one, forever.
        var first = new RecordingProvider("first", order)
        {
            OnApply = () =>
            {
                if (!superseded1)
                {
                    superseded1 = true;
                    newerRun = engine!.ApplyAsync("Desktop");
                }
            },
        };
        var second = new RecordingProvider("second", order);

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
              {"id":"both","name":"Both","resources":{"first":"a","second":"b"}},
              {"id":"desktop","name":"Desktop","resources":{"first":"c"}}]}]}
            """,
            providers: [first, second]);

        engine = world.Engine;

        TransitionOutcome superseded = await world.Engine.ApplyAsync("Both");
        TransitionOutcome newer = await newerRun!;

        Assert.Equal(TransitionStatus.Uncertain, superseded.Status);
        Assert.Contains("superseded", superseded.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(superseded.StateRecorded);

        // "second" was never reached by either run: the cancellation landed
        // between resources, exactly where it is allowed to.
        Assert.DoesNotContain("second", order);

        Assert.True(newer.Succeeded);
        Assert.Equal(["a", "c"], first.Applied);

        // The mode recorded is the newer request's, not a mix of both.
        Assert.Equal("space/desktop", world.Engine.ReadCurrentTargetKey(out _));
    }

    [Fact]
    public async Task Abandons_a_superseded_request_that_never_started()
    {
        // A third request arrives while a second is still waiting for a first
        // to yield. Only the newest of the three may ever dispatch anything --
        // the middle one is abandoned before it touches the machine.
        var order = new List<string>();
        ModeEngine? engine = null;
        Task<TransitionOutcome>? queued = null;
        Task<TransitionOutcome>? newest = null;
        bool superseded1 = false;

        // "first" is also the resource every other mode here sets, so this
        // must fire only once -- see the equivalent note above.
        var first = new RecordingProvider("first", order)
        {
            OnApply = () =>
            {
                if (!superseded1)
                {
                    superseded1 = true;
                    queued = engine!.ApplyAsync("Desktop");
                    newest = engine!.ApplyAsync("Movie");
                }
            },
        };
        var second = new RecordingProvider("second", order);

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
              {"id":"both","name":"Both","resources":{"first":"a","second":"b"}},
              {"id":"desktop","name":"Desktop","resources":{"first":"c"}},
              {"id":"movie","name":"Movie","resources":{"first":"d"}}]}]}
            """,
            providers: [first, second]);

        engine = world.Engine;

        await world.Engine.ApplyAsync("Both");

        TransitionOutcome abandoned = await queued!;
        TransitionOutcome winner = await newest!;

        Assert.Equal(TransitionStatus.Refused, abandoned.Status);
        Assert.Contains("superseded", abandoned.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(winner.Succeeded);
        Assert.Equal(["a", "d"], first.Applied);
        Assert.Equal("space/movie", world.Engine.ReadCurrentTargetKey(out _));
    }

    [Fact]
    public async Task Is_ready_again_once_a_transition_finishes()
    {
        using var world = new World();

        await world.Engine.ApplyAsync("Movie");

        Assert.False(world.Engine.IsBusy);
        Assert.True((await world.Engine.ApplyAsync("Desktop")).Succeeded);
    }

    [Fact]
    public async Task Does_not_look_busy_because_of_a_request_that_never_ran()
    {
        using var world = new World();

        await world.Engine.ApplyAsync("Nonsense");

        Assert.False(world.Engine.IsBusy);
    }

    // ---- cancellation ------------------------------------------------------

    [Fact]
    public async Task Leaves_the_machine_untouched_when_it_is_cancelled_before_it_starts()
    {
        using var world = new World();
        using var cancellation = new CancellationTokenSource();

        await cancellation.CancelAsync();

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Movie", cancellationToken: cancellation.Token);

        Assert.Equal(TransitionStatus.Refused, outcome.Status);
        Assert.Empty(world.Theme.Applied);
    }

    [Fact]
    public async Task Reports_a_transition_cancelled_part_way_as_uncertain()
    {
        // The rule that makes cancellation survivable: what was already applied
        // stands, the rest is not reached, and nothing claims to know the
        // result. Recording a mode here would be a straight lie.
        var order = new List<string>();
        using var cancellation = new CancellationTokenSource();

        var first = new RecordingProvider("first", order) { OnApply = cancellation.Cancel };
        var second = new RecordingProvider("second", order);

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"both","name":"Both","resources":{"first":"a","second":"b"}}]}]}
            """,
            providers: [first, second]);

        TransitionOutcome outcome = await world.Engine.ApplyAsync(
            "Both",
            cancellationToken: cancellation.Token);

        Assert.Equal(TransitionStatus.Uncertain, outcome.Status);
        Assert.Equal(["a"], first.Applied);
        Assert.Empty(second.Applied);
        Assert.False(outcome.StateRecorded);
        Assert.Null(world.Engine.ReadCurrentTargetKey(out _));
    }

    // ---- progress ----------------------------------------------------------

    [Fact]
    public async Task Says_what_it_is_doing_while_it_does_it()
    {
        using var world = new World();
        var seen = new List<TransitionStep>();

        await world.Engine.ApplyAsync(
            "Movie",
            progress: new SynchronousProgress(seen.Add));

        TransitionStep step = Assert.Single(seen);

        Assert.Equal("theme", step.Resource);
        Assert.Equal("Dark", step.Target);
        Assert.Equal(TransitionStepStatus.Applied, step.Status);
    }

    // ---- retry ---------------------------------------------------------------

    [Fact]
    public async Task Has_nothing_to_retry_before_anything_has_been_applied()
    {
        using var world = new World();

        Assert.Null(await world.Engine.RetryAsync());
    }

    [Fact]
    public async Task Retries_the_mode_that_failed_and_only_touches_what_is_still_wrong()
    {
        var first = new FakeProvider("first", "a") { Allowed = ["a"] };
        var second = new FakeProvider("second") { Allowed = ["b"], Fails = new InvalidOperationException("no") };

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"both","name":"Both","resources":{"first":"a","second":"b"}}]}]}
            """,
            providers: [first, second]);

        TransitionOutcome failed = await world.Engine.ApplyAsync("Both");
        Assert.Equal(TransitionStatus.Failed, failed.Status);
        Assert.Empty(first.Applied); // already "a"; never dispatched in the first place

        // The fault is fixed, as it would be by whatever a user did in response
        // to seeing it -- Retry does not know or care what changed.
        second.Fails = null;

        TransitionOutcome retried = await world.Engine.RetryAsync() ?? throw new InvalidOperationException();

        Assert.True(retried.Succeeded);

        // "first" already read as its target before, during and after the
        // retry -- only what was actually wrong is touched again.
        Assert.Empty(first.Applied);
        Assert.Equal(["b"], second.Applied);
        Assert.Equal("space/both", world.Engine.ReadCurrentTargetKey(out _));
    }

    [Fact]
    public async Task Retry_targets_the_last_request_even_when_a_different_mode_is_recorded()
    {
        using var world = new World();

        await world.Engine.ApplyAsync("Desktop");

        world.Theme.Fails = new InvalidOperationException("no");

        TransitionOutcome failed = await world.Engine.ApplyAsync("Movie");
        Assert.Equal(TransitionStatus.Failed, failed.Status);

        // The failed attempt mutated the machine (it tried to apply the
        // theme), so the recorded mode was forgotten rather than left naming
        // "Desktop" -- see ModeEngine's "forgotten first, written last" rule.
        Assert.Null(world.Engine.ReadCurrentTargetKey(out _));

        world.Theme.Fails = null;

        TransitionOutcome retried = await world.Engine.RetryAsync() ?? throw new InvalidOperationException();

        Assert.True(retried.Succeeded);
        Assert.Equal("space/movie", retried.Mode);
    }

    // ---- repair ----------------------------------------------------------

    [Fact]
    public async Task Has_nothing_to_repair_when_no_mode_is_recorded()
    {
        using var world = new World();

        Assert.Null(await world.Engine.RepairAsync());
    }

    [Fact]
    public async Task Repairs_only_the_resource_that_drifted()
    {
        var first = new FakeProvider("first", "a") { Allowed = ["a"] };
        var second = new FakeProvider("second", "b") { Allowed = ["b"] };

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"both","name":"Both","resources":{"first":"a","second":"b"}}]}]}
            """,
            providers: [first, second]);

        TransitionOutcome applied = await world.Engine.ApplyAsync("Both");
        Assert.True(applied.Succeeded);
        Assert.Empty(first.Applied);
        Assert.Empty(second.Applied); // both already read as their targets

        // Something outside PCMode changed "second" back.
        second.Current = "elsewhere";

        TransitionOutcome repaired = await world.Engine.RepairAsync() ?? throw new InvalidOperationException();

        Assert.True(repaired.Succeeded);
        Assert.Equal(["b"], second.Applied);
        Assert.Empty(first.Applied);
    }

    [Fact]
    public async Task Repair_is_available_even_when_nothing_has_recently_failed()
    {
        // A repair chases whatever is currently recorded, whether or not any
        // transition has recently run -- unlike a retry, which has nothing to
        // aim at until something has actually been requested and gone wrong.
        using var world = new World();

        await world.Engine.ApplyAsync("Movie");
        world.Theme.Current = "Light"; // drifted after the fact

        TransitionOutcome repaired = await world.Engine.RepairAsync() ?? throw new InvalidOperationException();

        Assert.True(repaired.Succeeded);
        Assert.Equal("space/movie", repaired.Mode);

        // Applied once for the original request and again to correct the drift.
        Assert.Equal(["Dark", "Dark"], world.Theme.Applied);
    }

    [Fact]
    public async Task Repairs_every_resource_that_drifted_in_one_pass()
    {
        var first = new FakeProvider("first", "a") { Allowed = ["a"] };
        var second = new FakeProvider("second", "b") { Allowed = ["b"] };

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"both","name":"Both","resources":{"first":"a","second":"b"}}]}]}
            """,
            providers: [first, second]);

        Assert.True((await world.Engine.ApplyAsync("Both")).Succeeded);

        // Something outside PCMode changed both back.
        first.Current = "elsewhere";
        second.Current = "elsewhere";

        TransitionOutcome repaired = await world.Engine.RepairAsync() ?? throw new InvalidOperationException();

        Assert.True(repaired.Succeeded);
        Assert.Equal(["a"], first.Applied);
        Assert.Equal(["b"], second.Applied);
    }

    /// <summary>
    /// A repair that cannot land follows the same "forgotten first, written
    /// last" rule as a failed Retry: it mutated the machine before the failure,
    /// so the mode it was chasing is no longer recorded as current either.
    /// </summary>
    [Fact]
    public async Task A_failing_repair_reports_failure_and_forgets_the_recorded_mode()
    {
        var first = new FakeProvider("first", "a") { Allowed = ["a"] };
        var second = new FakeProvider("second", "b") { Allowed = ["b"] };

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"both","name":"Both","resources":{"first":"a","second":"b"}}]}]}
            """,
            providers: [first, second]);

        Assert.True((await world.Engine.ApplyAsync("Both")).Succeeded);
        Assert.Equal("space/both", world.Engine.ReadCurrentTargetKey(out _));

        // Something outside PCMode changed "second" back, and fixing it is now
        // going to fail.
        second.Current = "elsewhere";
        second.Fails = new InvalidOperationException("no");

        TransitionOutcome repaired = await world.Engine.RepairAsync() ?? throw new InvalidOperationException();

        Assert.Equal(TransitionStatus.Failed, repaired.Status);
        Assert.Empty(first.Applied); // already correct; never touched

        Assert.Null(world.Engine.ReadCurrentTargetKey(out _));
    }

    // ---- intentionally unobservable resources -------------------------------

    [Fact]
    public async Task Applies_an_unobservable_resource_but_reports_it_unverified()
    {
        var launcher = new FakeProvider("launcher") { Observable = false, Allowed = ["Running"] };

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"go","name":"Go","resources":{"launcher":"Running"}}]}]}
            """,
            providers: [launcher]);

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Go");

        Assert.True(outcome.Succeeded);
        Assert.Equal(["Running"], launcher.Applied);
        Assert.Equal(TransitionStepStatus.AppliedUnverified, Assert.Single(outcome.Steps).Status);
    }

    [Fact]
    public async Task Dry_run_reports_an_unobservable_resource_without_dispatching_it()
    {
        var launcher = new FakeProvider("launcher") { Observable = false, Allowed = ["Running"] };

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"go","name":"Go","resources":{"launcher":"Running"}}]}]}
            """,
            providers: [launcher]);

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Go", dryRun: true);

        Assert.Empty(launcher.Applied);
        Assert.Equal(TransitionStepStatus.Unverified, Assert.Single(outcome.Steps).Status);
    }

    [Fact]
    public async Task Repair_does_not_relaunch_an_unobservable_resource_that_was_already_dispatched()
    {
        var launcher = new FakeProvider("launcher") { Observable = false, Allowed = ["Running"] };

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"go","name":"Go","resources":{"launcher":"Running"}}]}]}
            """,
            providers: [launcher]);

        Assert.True((await world.Engine.ApplyAsync("Go")).Succeeded);
        Assert.Equal(["Running"], launcher.Applied);

        TransitionOutcome repaired = await world.Engine.RepairAsync() ?? throw new InvalidOperationException();

        Assert.True(repaired.Succeeded);
        Assert.Equal(["Running"], launcher.Applied); // still just the one dispatch
        Assert.Equal(TransitionStepStatus.Unverified, Assert.Single(repaired.Steps).Status);
    }

    [Fact]
    public async Task Retry_does_not_replay_an_unobservable_resource_already_dispatched_by_the_previous_attempt()
    {
        var launcher = new FakeProvider("launcher") { Observable = false, Allowed = ["Running"] };
        var failing = new FakeProvider("failing") { Allowed = ["b"], Fails = new InvalidOperationException("no") };

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"both","name":"Both","resources":{"launcher":"Running","failing":"b"}}]}]}
            """,
            providers: [launcher, failing]);

        TransitionOutcome failed = await world.Engine.ApplyAsync("Both");
        Assert.Equal(TransitionStatus.Failed, failed.Status);
        Assert.Equal(["Running"], launcher.Applied); // dispatched successfully before the second resource failed

        failing.Fails = null;

        TransitionOutcome retried = await world.Engine.RetryAsync() ?? throw new InvalidOperationException();

        Assert.True(retried.Succeeded);
        Assert.Equal(["Running"], launcher.Applied); // not relaunched
        Assert.Equal(["b"], failing.Applied);
    }

    [Fact]
    public async Task Retry_still_dispatches_an_unobservable_resource_that_was_never_reached()
    {
        var failing = new FakeProvider("failing") { Allowed = ["a"], Fails = new InvalidOperationException("no") };
        var launcher = new FakeProvider("launcher") { Observable = false, Allowed = ["Running"] };

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"both","name":"Both","resources":{"failing":"a","launcher":"Running"}}]}]}
            """,
            providers: [failing, launcher]);

        TransitionOutcome failed = await world.Engine.ApplyAsync("Both");
        Assert.Equal(TransitionStatus.Failed, failed.Status);
        Assert.Empty(launcher.Applied); // never reached; "failing" failed first

        failing.Fails = null;

        TransitionOutcome retried = await world.Engine.RetryAsync() ?? throw new InvalidOperationException();

        Assert.True(retried.Succeeded);
        Assert.Equal(["Running"], launcher.Applied); // dispatched now that it was actually reached
    }

    [Fact]
    public async Task A_new_activation_may_dispatch_an_unobservable_resource_again()
    {
        var launcher = new FakeProvider("launcher") { Observable = false, Allowed = ["Running"] };

        using var world = new World(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
              {"id":"go","name":"Go","resources":{"launcher":"Running"}}]}]}
            """,
            providers: [launcher]);

        Assert.True((await world.Engine.ApplyAsync("Go")).Succeeded);
        Assert.True((await world.Engine.ApplyAsync("Go")).Succeeded);

        Assert.Equal(["Running", "Running"], launcher.Applied);
    }

    // ---- plumbing ----------------------------------------------------------

    /// <summary>
    /// A provider that notes the order it was called in, alongside a hook for
    /// making something happen mid-transition.
    /// </summary>
    private sealed class RecordingProvider(string key, List<string> order) : IDependentProvider
    {
        private readonly List<string> _applied = [];

        public string Key { get; } = key;

        public string DisplayName => Key;

        public Exception? Fails { get; init; }

        public Action? OnApply { get; init; }

        /// <summary>What Read reports. Null stands for "cannot tell".</summary>
        public string? Current { get; init; }

        /// <summary>Resource keys this runs after when they are in the mode too.</summary>
        public IReadOnlyList<string> After { get; init; } = [];

        /// <summary>What each target needs, keyed by target.</summary>
        public IReadOnlyDictionary<string, ResourceRequirement[]>? Requires { get; init; }

        public IReadOnlyList<string> RunsAfter => After;

        public IReadOnlyList<string> Applied => _applied;

        public IReadOnlyList<ResourceRequirement> RequirementsFor(string target) =>
            Requires is not null && Requires.TryGetValue(target, out ResourceRequirement[]? required)
                ? required
                : [];

        public bool IsValidValue(string value, out string? problem)
        {
            problem = null;
            return true;
        }

        public string? Read() => Current;

        public void Apply(string value, CancellationToken cancellationToken)
        {
            order.Add(Key);
            OnApply?.Invoke();

            if (Fails is not null)
            {
                throw Fails;
            }

            _applied.Add(value);
        }
    }

    /// <summary>
    /// Reports on the calling thread.
    /// </summary>
    /// <remarks>
    /// Progress&lt;T&gt; posts to the captured context, and a test has none, so
    /// its callbacks land on the thread pool and may not have arrived by the
    /// time the transition returns. That would make this test flaky about
    /// something it is not testing.
    /// </remarks>
    private sealed class SynchronousProgress(Action<TransitionStep> report) : IProgress<TransitionStep>
    {
        public void Report(TransitionStep value) => report(value);
    }

    /// <summary>A configuration, a state file and an engine, all disposable.</summary>
    private sealed class World : IDisposable
    {
        private const string DefaultDocument =
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
              {"id":"desktop","name":"Desktop","resources":{"theme":"Light"}},
              {"id":"movie","name":"Movie","resources":{"theme":"Dark"}}]}]}
            """;

        public World(string? document = null, IReadOnlyList<IModeResourceProvider>? providers = null)
        {
            Root = new TemporaryRoot();
            Root.Write(document ?? DefaultDocument);

            List<IModeResourceProvider> all = providers is null
                ? [Theme]
                : [.. providers];

            if (providers is not null)
            {
                Theme = all.OfType<FakeProvider>().FirstOrDefault(provider => provider.Key == "theme") ?? Theme;
            }

            ModeConfiguration configuration = ModeConfiguration.LoadOrCreate(Root.Path, all);

            Engine = new ModeEngine(configuration, new ModeStateStore(Root.StateFile), all);
        }

        public TemporaryRoot Root { get; }

        public FakeProvider Theme { get; } = new("theme", "Light") { Allowed = ["Light", "Dark"] };

        public ModeEngine Engine { get; }

        public void Dispose() => Root.Dispose();
    }
}

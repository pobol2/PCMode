namespace PCMode.Engine.Tests;

/// <summary>
/// What a transition does about the parts of itself this machine cannot do.
/// </summary>
/// <remarks>
/// Two rules run through all of it. Only a definite no may remove anything --
/// unknown is planned and attempted, because a television that has not answered
/// yet is exactly what convergence is for. And nothing is removed until the whole
/// graph has been resolved, so a contradiction or a loop is refused on the days
/// the television is unplugged as well as the days it is not.
/// </remarks>
public sealed class AvailabilityTests
{
    // ---- what counts as a reason to leave something out ---------------------

    [Fact]
    public void Leaves_out_an_optional_resource_this_machine_definitely_cannot_do()
    {
        TransitionPlan plan = Plan(
            Mode("Film", ("theme", "Dark", false), ("audio", "TV", true)),
            new FakeProvider("theme"),
            new Fussy("audio", ResourceAvailability.Unavailable("there is no such sound card."))
        );

        Assert.Equal(["theme"], Keys(plan));

        OmittedResource omitted = Assert.Single(plan.Omissions);

        Assert.Equal("audio", omitted.Key);
        Assert.Equal("TV", omitted.Target);
        Assert.Contains("sound card", omitted.Reason, StringComparison.Ordinal);
        Assert.Equal(AvailabilityState.Unavailable, omitted.Availability.State);
    }

    [Fact]
    public void Leaves_out_an_optional_resource_that_is_switched_off_rather_than_absent()
    {
        // Kept apart from missing because they ask different things of whoever
        // reads the report: one is a machine that cannot, the other one that has
        // been told not to.
        TransitionPlan plan = Plan(
            Mode("Film", ("theme", "Dark", false), ("audio", "TV", true)),
            new FakeProvider("theme"),
            new Fussy("audio", ResourceAvailability.Disabled("sound is muted by policy."))
        );

        Assert.Equal(AvailabilityState.Disabled, Assert.Single(plan.Omissions).Availability.State);
    }

    [Fact]
    public void Keeps_an_optional_resource_nobody_can_currently_vouch_for()
    {
        // THE rule that stops this feature from silently shrinking modes. A
        // missing HDMI endpoint, a sleeping monitor and a discovery query that
        // failed are all states this machine passes through on the way to
        // working, and dropping them would drop the resources most worth waiting
        // for.
        TransitionPlan plan = Plan(
            Mode("Film", ("audio", "TV", true)),
            new Fussy("audio", ResourceAvailability.Unknown("the television has not answered yet."))
        );

        Assert.Equal(["audio"], Keys(plan));
        Assert.Empty(plan.Omissions);
        Assert.Equal(AvailabilityState.Unknown, Assert.Single(plan.Steps).Availability.State);
    }

    [Fact]
    public void Keeps_a_resource_whose_provider_could_not_answer_at_all()
    {
        // A provider that throws has not said no, it has said nothing -- and the
        // exception must reach neither the caller nor a silent omission. It
        // becomes "nobody knows", with the words, and the resource is attempted.
        TransitionPlan plan = Plan(
            Mode("Film", ("audio", "TV", true)),
            new Fussy("audio") { Throws = new InvalidOperationException("Core Audio did not answer.") }
        );

        Assert.Equal(["audio"], Keys(plan));

        ResourceAvailability availability = Assert.Single(plan.Steps).Availability;

        Assert.Equal(AvailabilityState.Unknown, availability.State);
        Assert.Contains("Core Audio did not answer", availability.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_a_provider_that_says_nothing_to_available()
    {
        Assert.Equal(
            AvailabilityState.Available,
            Assert
                .Single(Plan(Mode("Film", ("theme", "Dark", false)), new FakeProvider("theme")).Steps)
                .Availability.State
        );
    }

    // ---- required means required --------------------------------------------

    [Fact]
    public void Refuses_a_mode_whose_required_resource_this_machine_cannot_do()
    {
        string problem = Rejected(
            Mode("Film", ("audio", "TV", false)),
            new Fussy("audio", ResourceAvailability.Unavailable("there is no such sound card."))
        );

        Assert.Contains("sound card", problem, StringComparison.Ordinal);
        Assert.Contains("'Film' sets it", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Says_which_chain_of_needs_a_missing_resource_was_holding_up()
    {
        // "The television is off" is not an explanation of why a mode about
        // sound was refused. "The television is off, and the sound you asked for
        // only exists on it" is.
        string problem = Rejected(
            Mode("Film", ("audio", "TV", false)),
            new FakeProvider("audio")
            {
                Requires = new Dictionary<string, ResourceRequirement[]>
                {
                    ["TV"] = [new ResourceRequirement("display", "TVOnly")],
                },
            },
            new Fussy("display", ResourceAvailability.Unavailable("that monitor is not plugged in."))
        );

        Assert.Contains("not plugged in", problem, StringComparison.Ordinal);
        Assert.Contains("needed by audio 'TV'", problem, StringComparison.Ordinal);
        Assert.Contains("which 'Film' sets", problem, StringComparison.Ordinal);
    }

    // ---- requiredness spreads through what is needed ------------------------

    [Fact]
    public void Refuses_a_mode_whose_optional_resource_something_required_needs()
    {
        // The display is written as optional and the sound is not. Leaving the
        // display out would send sound to a television nobody switched on, so
        // the requirement wins: this is the only reading under which the mode
        // still means what it says.
        string problem = Rejected(
            Mode("Film", ("audio", "TV", false), ("display", "TVOnly", true)),
            new FakeProvider("audio")
            {
                Requires = new Dictionary<string, ResourceRequirement[]>
                {
                    ["TV"] = [new ResourceRequirement("display", "TVOnly")],
                },
            },
            new Fussy("display", ResourceAvailability.Unavailable("that monitor is not plugged in."))
        );

        Assert.Contains("not plugged in", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_resource_added_for_something_required_and_then_found_missing()
    {
        // Nobody wrote the display down at all. It is here because the sound
        // needs it, and it inherits the sound's standing rather than being
        // treated as an extra that can be dropped.
        string problem = Rejected(
            Mode("Film", ("audio", "TV", false)),
            new FakeProvider("audio")
            {
                Requires = new Dictionary<string, ResourceRequirement[]>
                {
                    ["TV"] = [new ResourceRequirement("display", "TVOnly")],
                },
            },
            new Fussy("display", ResourceAvailability.Unavailable("that monitor is not plugged in."))
        );

        Assert.Contains("display", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_a_shared_resource_when_only_one_of_the_branches_needing_it_is_optional()
    {
        // The theme is needed by an optional branch that is leaving and by a
        // required one that is staying. It stays, because a dependency is
        // removed only when nothing left needs it.
        TransitionPlan plan = Plan(
            Mode("Film", ("audio", "TV", true), ("display", "TVOnly", false)),
            new Fussy("audio", ResourceAvailability.Unavailable("no such device."))
            {
                Needs = [new ResourceRequirement("theme", "Dark")],
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

        Assert.Equal(["theme", "display"], Keys(plan));
        Assert.Equal("audio", Assert.Single(plan.Omissions).Key);
    }

    [Fact]
    public void Removes_a_resource_that_was_only_ever_there_for_a_branch_that_left()
    {
        // The theme was added for the sound and for nothing else. Leaving it
        // behind would mean a mode that changes the desktop's colours as a
        // side effect of a device it could not find.
        TransitionPlan plan = Plan(
            Mode("Film", ("audio", "TV", true), ("cursor", "Corner", false)),
            new Fussy("audio", ResourceAvailability.Unavailable("no such device."))
            {
                Needs = [new ResourceRequirement("theme", "Dark")],
            },
            new FakeProvider("cursor"),
            new FakeProvider("theme")
        );

        Assert.Equal(["cursor"], Keys(plan));

        Assert.Equal(["audio", "theme"], plan.Omissions.Select(omitted => omitted.Key).Order(StringComparer.Ordinal));

        Assert.Contains(
            "nothing left",
            Assert.Single(plan.Omissions, omitted => omitted.Key == "theme").Reason,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Takes_an_optional_branch_out_whole_when_what_it_needs_has_gone()
    {
        // The sound is available; the display it needs is not. Applying the
        // sound anyway would be applying the half of the branch that cannot work
        // without the half that is missing.
        TransitionPlan plan = Plan(
            Mode("Film", ("theme", "Dark", false), ("audio", "TV", true)),
            new FakeProvider("theme"),
            new FakeProvider("audio")
            {
                Requires = new Dictionary<string, ResourceRequirement[]>
                {
                    ["TV"] = [new ResourceRequirement("display", "TVOnly")],
                },
            },
            new Fussy("display", ResourceAvailability.Unavailable("that monitor is not plugged in."))
        );

        Assert.Equal(["theme"], Keys(plan));

        Assert.Contains(
            "which was left out",
            Assert.Single(plan.Omissions, omitted => omitted.Key == "audio").Reason,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Does_not_let_an_ordering_preference_make_anything_required()
    {
        // Wanting to run after the displays says nothing about needing them.
        // That is the entire difference between the two relationships, and it
        // has to survive being asked a question about availability.
        TransitionPlan plan = Plan(
            Mode("Film", ("audio", "TV", false), ("display", "TVOnly", true)),
            new FakeProvider("audio") { RunsAfter = ["display"] },
            new Fussy("display", ResourceAvailability.Unavailable("that monitor is not plugged in."))
        );

        Assert.Equal(["audio"], Keys(plan));
        Assert.Equal("display", Assert.Single(plan.Omissions).Key);
    }

    // ---- nothing may be hidden by dropping a branch -------------------------

    [Fact]
    public void Refuses_a_contradiction_that_an_absent_device_would_have_covered_up()
    {
        // The sound is unavailable and optional, so it is about to be dropped.
        // It is still a contradiction, and a mode that was refused on the days
        // the television was on and accepted on the days it was off would be the
        // least useful possible way to find that out.
        string problem = Rejected(
            Mode("Muddle", ("display", "Desktop", false), ("audio", "TV", true)),
            new FakeProvider("display"),
            new Fussy("audio", ResourceAvailability.Unavailable("no such device."))
            {
                Needs = [new ResourceRequirement("display", "TVOnly")],
            }
        );

        Assert.Contains("Desktop", problem, StringComparison.Ordinal);
        Assert.Contains("TVOnly", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_loop_that_an_absent_device_would_have_covered_up()
    {
        string problem = Rejected(
            Mode("Loop", ("audio", "TV", true), ("display", "TVOnly", true)),
            new Fussy("audio", ResourceAvailability.Unavailable("no such device.")) { After = ["display"] },
            new FakeProvider("display") { RunsAfter = ["audio"] }
        );

        Assert.Contains("cannot be ordered", problem, StringComparison.Ordinal);
    }

    // ---- when there is nothing left to do -----------------------------------

    [Fact]
    public async Task Changes_nothing_and_records_nothing_when_every_resource_is_unavailable()
    {
        using var root = new TemporaryRoot();

        root.Write(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"name":"Film","id":"film","resources":{
              "audio":{"value":"TV","optional":true},
              "display":{"value":"TVOnly","optional":true}}}]}]}
            """
        );

        var audio = new Fussy("audio", ResourceAvailability.Unavailable("no such device."));
        var display = new Fussy("display", ResourceAvailability.Unavailable("not plugged in."));

        IReadOnlyList<IModeResourceProvider> providers = [audio, display];

        var engine = new ModeEngine(
            ModeConfiguration.LoadOrCreate(root.Path, providers),
            new ModeStateStore(root.StateFile),
            providers,
            new FakeTime()
        );

        // A mode was recorded before this, and it is still the mode the machine
        // is in: nothing was changed, so nothing may be forgotten either.
        Assert.True(new ModeStateStore(root.StateFile).TryWrite("Desktop", DateTimeOffset.UnixEpoch, out _));

        TransitionOutcome outcome = await engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Refused, outcome.Status);
        Assert.False(outcome.Mutated);
        Assert.False(outcome.StateRecorded);
        Assert.Equal("Desktop", engine.ReadCurrentTargetKey(out _));

        Assert.Empty(audio.Applied);
        Assert.Empty(display.Applied);

        Assert.Equal(2, outcome.Omitted.Count);
        Assert.All(outcome.Steps, step => Assert.Equal(TransitionStepStatus.Omitted, step.Status));
    }

    // ---- what a transition says about what it left out ----------------------

    [Fact]
    public async Task Reports_what_it_left_out_and_still_applies_the_rest()
    {
        using var root = new TemporaryRoot();

        root.Write(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"name":"Film","id":"film","resources":{
              "theme":"Dark",
              "audio":{"value":"TV","optional":true}}}]}]}
            """
        );

        var theme = new FakeProvider("theme", "Light");
        var audio = new Fussy("audio", ResourceAvailability.Unavailable("no active device is named 'TV'."));

        IReadOnlyList<IModeResourceProvider> providers = [theme, audio];

        var engine = new ModeEngine(
            ModeConfiguration.LoadOrCreate(root.Path, providers),
            new ModeStateStore(root.StateFile),
            providers,
            new FakeTime()
        );

        TransitionOutcome outcome = await engine.ApplyAsync("Film");

        // A mode that did everything it could is a mode that worked, and it is
        // recorded -- but it says what it went without.
        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.Equal(["Dark"], theme.Applied);
        Assert.True(outcome.StateRecorded);
        Assert.Contains("without", outcome.Message, StringComparison.Ordinal);

        TransitionStep omitted = Assert.Single(outcome.Omitted);

        Assert.Equal("audio", omitted.Resource);
        Assert.True(omitted.Optional);
        Assert.Equal(0, omitted.Attempts);
        Assert.Contains("named 'TV'", omitted.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Walks_a_mode_that_would_leave_something_out_without_touching_anything()
    {
        using var root = new TemporaryRoot();

        root.Write(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"name":"Film","id":"film","resources":{
              "theme":"Dark",
              "audio":{"value":"TV","optional":true}}}]}]}
            """
        );

        var theme = new FakeProvider("theme", "Light");
        var audio = new Fussy("audio", ResourceAvailability.Unavailable("no active device is named 'TV'."));

        IReadOnlyList<IModeResourceProvider> providers = [theme, audio];

        var engine = new ModeEngine(
            ModeConfiguration.LoadOrCreate(root.Path, providers),
            new ModeStateStore(root.StateFile),
            providers,
            new FakeTime()
        );

        TransitionOutcome outcome = await engine.ApplyAsync("Film", dryRun: true);

        // Which is exactly what somebody runs a dry run to find out.
        Assert.True(outcome.DryRun);
        Assert.Single(outcome.Omitted);
        Assert.Empty(theme.Applied);
        Assert.False(File.Exists(root.StateFile));
    }

    [Fact]
    public async Task Still_fails_a_mode_whose_optional_resource_was_there_and_broke()
    {
        using var root = new TemporaryRoot();
        // Optional is about availability and nothing else. A resource that was
        // present enough to be attempted is a resource whose failure is a
        // failure.

        root.Write(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"name":"Film","id":"film","resources":{
              "audio":{"value":"TV","optional":true}}}]}]}
            """
        );

        var audio = new Fussy("audio") { Fails = new InvalidOperationException("the amplifier is off.") };

        IReadOnlyList<IModeResourceProvider> providers = [audio];

        var engine = new ModeEngine(
            ModeConfiguration.LoadOrCreate(root.Path, providers),
            new ModeStateStore(root.StateFile),
            providers,
            new FakeTime()
        );

        TransitionOutcome outcome = await engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Contains("amplifier", outcome.Message, StringComparison.Ordinal);
        Assert.False(outcome.StateRecorded);
    }

    // ---- plumbing -----------------------------------------------------------

    /// <summary>A resource with an opinion about whether it can be done at all.</summary>
    private sealed class Fussy(string key, ResourceAvailability? availability = null)
        : IAvailableProvider,
            IDependentProvider
    {
        private readonly List<string> _applied = [];

        public string Key { get; } = key;

        public string DisplayName => Key;

        /// <summary>Thrown instead of answering, when set.</summary>
        public Exception? Throws { get; init; }

        /// <summary>Thrown from Apply, when set.</summary>
        public Exception? Fails { get; init; }

        public IReadOnlyList<ResourceRequirement> Needs { get; init; } = [];

        public IReadOnlyList<string> After { get; init; } = [];

        public IReadOnlyList<string> Applied => _applied;

        public IReadOnlyList<string> RunsAfter => After;

        public IReadOnlyList<ResourceRequirement> RequirementsFor(string target) => Needs;

        public ResourceAvailability AvailabilityOf(string target) =>
            Throws is not null ? throw Throws : availability ?? ResourceAvailability.Available;

        public bool IsValidValue(string value, out string? problem)
        {
            problem = null;
            return true;
        }

        public string? Read() => null;

        public void Apply(string value, CancellationToken cancellationToken)
        {
            if (Fails is not null)
            {
                throw Fails;
            }

            _applied.Add(value);
        }
    }

    private static ConfiguredMode Mode(string name, params (string Key, string Value, bool Optional)[] resources) =>
        new(
            name,
            [.. resources.Select(r => new ConfiguredResource(r.Key, r.Value, r.Optional))],
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

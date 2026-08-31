namespace PCMode.Engine.Tests;

/// <summary>
/// What it takes for the engine to believe a resource got where it was sent.
/// </summary>
/// <remarks>
/// Every test here runs on <see cref="FakeTime"/>, so the policies exercised are
/// the real ones -- seconds of settling, windows of eight seconds, deadlines of
/// ninety -- and the suite still finishes in milliseconds. Nothing waits, and
/// nothing touches a machine.
/// </remarks>
public sealed class ConvergenceTests
{
    // ---- verification -------------------------------------------------------

    [Fact]
    public async Task Does_not_believe_a_command_the_machine_did_not_follow()
    {
        // THE rule this whole step exists for. Apply returned without complaint
        // and the machine is still where it was, which is precisely the failure
        // an engine that trusted the call could never report.
        var screen = new Machine("screen")
        {
            Policy = Verifying(window: 4, poll: 1),
            Reads = ["Desktop"],
        };

        using var world = new World(screen);

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Single(screen.Commands);

        TransitionStep step = Assert.Single(outcome.Steps);

        Assert.Equal(TransitionStepStatus.Failed, step.Status);
        Assert.Equal("Desktop", step.Observed);
        Assert.Equal(1, step.Attempts);
        Assert.Contains("Desktop", step.Problem!, StringComparison.Ordinal);

        Assert.False(outcome.StateRecorded);
        Assert.Null(world.Engine.ReadCurrentTargetKey(out _));
    }

    [Fact]
    public async Task Believes_it_once_the_readback_agrees()
    {
        // The readback arrives a poll late, which is the ordinary case: Windows
        // returns from the call before it has finished doing what was asked.
        var screen = new Machine("screen")
        {
            Policy = Verifying(window: 4, poll: 1),
            Reads = ["Desktop", "Desktop", "TVOnly"],
        };
        using var world = new World(screen);


        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.Single(screen.Commands);

        TransitionStep step = Assert.Single(outcome.Steps);

        Assert.Equal(TransitionStepStatus.Applied, step.Status);

        // Where it came from and where it ended up are both reported, and they
        // are not the same question.
        Assert.Equal("Desktop", step.Current);
        Assert.Equal("TVOnly", step.Observed);

        // It polled rather than guessing: one interval passed between the read
        // that disagreed and the one that did not.
        Assert.Equal(TimeSpan.FromSeconds(1), world.Clock.Elapsed);
    }

    [Fact]
    public async Task Does_not_wait_for_a_resource_that_is_already_right_and_has_nothing_to_settle()
    {
        // Nothing is dispatched and nothing is waited for. A resource whose
        // readback is a single fact -- the theme is a pair of registry values --
        // is either right or it is not, and there is no period over which that
        // could become more true.
        var screen = new Machine("screen")
        {
            Policy = Verifying(window: 8, poll: 1),
            Reads = ["TVOnly"],
        };
        using var world = new World(screen);


        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.Empty(screen.Commands);
        Assert.Equal(TransitionStepStatus.AlreadyCorrect, Assert.Single(outcome.Steps).Status);
        Assert.Equal(TimeSpan.Zero, world.Clock.Elapsed);
    }

    [Fact]
    public async Task Keeps_watching_a_resource_that_is_already_right_until_it_has_held_still()
    {
        // A settle period exists because this resource's readback is known to
        // wobble. One agreeing sample is exactly the evidence it exists to
        // reject, and a sample taken before anything was dispatched is no better
        // than one taken after.
        var screen = new Machine("screen")
        {
            Policy = Verifying(window: 8, poll: 1, settle: 2),
            Reads = ["TVOnly"],
        };
        using var world = new World(screen);


        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);

        // Watched, and still never touched: it was right all along.
        Assert.Empty(screen.Commands);

        TransitionStep step = Assert.Single(outcome.Steps);

        Assert.Equal(TransitionStepStatus.AlreadyCorrect, step.Status);
        Assert.Equal(0, step.Attempts);
        Assert.Equal(TimeSpan.FromSeconds(2), world.Clock.Elapsed);
    }

    [Fact]
    public async Task Applies_a_resource_that_only_looked_right_when_it_was_first_read()
    {
        // The case the watching is for. The machine reported the layout and then
        // reported something else, so it was passing through rather than sitting
        // there -- and a transition that had believed the first read would have
        // reported a mode it never applied.
        var screen = new Machine("screen")
        {
            Policy = Verifying(window: 8, poll: 1, settle: 2),
            Reads = ["TVOnly", "Desktop", "TVOnly", "TVOnly", "TVOnly"],
        };
        using var world = new World(screen);


        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);

        TransitionStep step = Assert.Single(outcome.Steps);

        Assert.Equal(TransitionStepStatus.Applied, step.Status);
        Assert.Equal(1, step.Attempts);
        Assert.Single(screen.Commands);
    }

    // ---- settling -----------------------------------------------------------

    [Fact]
    public async Task Does_not_call_it_done_until_the_readback_has_held_still()
    {
        // The display reports the layout, then reports something else as the
        // monitors finish negotiating. It passed through the layout rather than
        // reaching it, and a settle period that reset on anything less than
        // continuous agreement is the only thing that can tell the difference.
        var screen = new Machine("screen")
        {
            Policy = Verifying(window: 8, poll: 1, settle: 2),
            Reads = ["Desktop", "TVOnly", "Desktop", "TVOnly", "TVOnly", "TVOnly"],
        };
        using var world = new World(screen);


        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.Single(screen.Commands);

        // Four seconds, not two: the first agreement was interrupted at one
        // second, so the settle period started again from the second one.
        Assert.Equal(TimeSpan.FromSeconds(4), world.Clock.Elapsed);
    }

    [Fact]
    public async Task Does_not_start_a_dependent_resource_until_the_one_before_it_has_settled()
    {
        // The whole reason the pointer is ordered after the displays. A desktop
        // that is still changing shape is one the pointer would be placed on
        // wrongly, so "after" has to mean after it stopped moving, not after the
        // call that started it moving returned.
        var clock = new FakeTime();

        var screen = new Machine("screen", clock)
        {
            Policy = Verifying(window: 8, poll: 1, settle: 2),
            Reads = ["Desktop", "TVOnly"],
        };

        var pointer = new Machine("pointer", clock) { After = ["screen"] };

        using var world = new World(clock, screen, pointer, Document(
            """{"screen":"TVOnly","pointer":"Corner"}"""));

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);

        // Settled at two seconds, and not touched a moment before that.
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(pointer.Commands).At);
    }

    // ---- retrying -----------------------------------------------------------

    [Fact]
    public async Task Commands_it_again_when_the_first_attempt_did_not_take()
    {
        var screen = new Machine("screen")
        {
            Policy = Verifying(attempts: 2, window: 2, poll: 1),
            Reads = ["Desktop", "Desktop", "Desktop", "Desktop", "Desktop", "TVOnly"],
        };
        using var world = new World(screen);


        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.Equal(2, screen.Commands.Count);
        Assert.Equal(2, Assert.Single(outcome.Steps).Attempts);
    }

    [Fact]
    public async Task Stops_commanding_it_after_the_attempts_its_policy_allows()
    {
        // Bounded, because a resource that has not taken twice is not going to
        // take on the ninetieth try, and a transition that never gives up is a
        // shell that never comes back.
        var screen = new Machine("screen")
        {
            Policy = Verifying(attempts: 3, window: 2, poll: 1),
            Reads = ["Desktop"],
        };
        using var world = new World(screen);


        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Equal(3, screen.Commands.Count);
        Assert.Equal(3, Assert.Single(outcome.Steps).Attempts);
        Assert.False(outcome.StateRecorded);
    }

    [Fact]
    public async Task Retries_a_command_the_machine_refused_until_it_stops_refusing()
    {
        // The audio case exactly: the television's endpoint does not exist until
        // some seconds after the display that woke the television up, so the
        // command genuinely fails and then genuinely starts working.
        var sound = new Machine("sound")
        {
            Policy = Verifying(attempts: 4, window: 2, poll: 1),
            Fails = new InvalidOperationException("No active playback device is named 'TV'."),
            FailedAttempts = 2,
            Reads = ["Speakers", "Speakers", "TV"],
        };

        using var world = new World(sound, Document("""{"sound":"TV"}"""));

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.Equal(3, sound.Commands.Count);

        // It paused between the refusals rather than hammering: two intervals of
        // two seconds, and a device that appears in between is one it finds.
        Assert.True(world.Clock.Elapsed >= TimeSpan.FromSeconds(4), $"Waited only {world.Clock.Elapsed}.");
    }

    [Fact]
    public async Task Gives_up_on_a_command_the_machine_keeps_refusing()
    {
        var sound = new Machine("sound")
        {
            Policy = Verifying(attempts: 3, window: 2, poll: 1),
            Fails = new InvalidOperationException("No active playback device is named 'TV'."),
            FailedAttempts = int.MaxValue,
        };
        using var world = new World(sound, Document("""{"sound":"TV"}"""));


        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Equal(3, sound.Commands.Count);

        // The provider's own words survive the retrying. The list of what IS
        // plugged in is how somebody tells a typo from a switched-off device.
        Assert.Contains("No active playback device", outcome.Message, StringComparison.Ordinal);
        Assert.False(outcome.StateRecorded);
    }

    // ---- dispatch-only ------------------------------------------------------

    [Fact]
    public async Task Does_not_retry_a_resource_that_has_nothing_to_read_back()
    {
        // The trap this avoids: "it never confirms, so try again" is exactly
        // backwards. Nothing will ever confirm it, so retrying means retrying
        // forever -- and every retry is another grab at a pointer whose owner
        // has since moved it where they wanted it.
        var pointer = new Machine("pointer") { Reads = [null] };

        using var world = new World(pointer, Document("""{"pointer":"Corner"}"""));

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.Single(pointer.Commands);
        Assert.Equal(TimeSpan.Zero, world.Clock.Elapsed);
    }

    [Fact]
    public async Task Still_fails_a_dispatch_only_resource_whose_command_failed()
    {
        // Not verifying is not the same as not caring. A move that could not
        // happen is a real failure, reported on the first attempt.
        var pointer = new Machine("pointer")
        {
            Fails = new InvalidOperationException("Windows refused to move the pointer."),
            FailedAttempts = int.MaxValue,
        };
        using var world = new World(pointer, Document("""{"pointer":"Corner"}"""));


        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Single(pointer.Commands);
        Assert.False(outcome.StateRecorded);
    }

    [Fact]
    public async Task Does_not_treat_a_readback_it_never_asked_for_as_a_confirmation()
    {
        // This machine would answer with the target if anything asked it. Nothing
        // does, and the report says so: a dispatch-only resource reports no
        // observed value at all, because reusing the read taken before the
        // command would put a confirmation in the report that nobody made.
        var pointer = new Machine("pointer");
        using var world = new World(pointer, Document("""{"pointer":"Corner"}"""));


        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);

        TransitionStep step = Assert.Single(outcome.Steps);

        Assert.Equal(TransitionStepStatus.Applied, step.Status);
        Assert.Null(step.Observed);

        // One read, taken before dispatching, and none afterwards.
        Assert.Equal(1, pointer.ReadCount);
    }

    [Fact]
    public async Task Lets_a_command_it_cannot_watch_spread_before_the_next_resource_starts()
    {
        // The honest version of settling for a resource with nothing to read.
        // It proves nothing and does not pretend to: it is a stated pause, and
        // the thing ordered after it waits it out.
        var clock = new FakeTime();

        var announcer = new Machine("screen", clock)
        {
            Policy = new ConvergencePolicy { PostDispatchDelay = TimeSpan.FromSeconds(3) },
        };

        var pointer = new Machine("pointer", clock) { After = ["screen"] };

        using var world = new World(clock, announcer, pointer, Document(
            """{"screen":"TVOnly","pointer":"Corner"}"""));

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(pointer.Commands).At);

        // And it is still not a confirmation.
        Assert.Null(Assert.Single(outcome.Steps, step => step.Resource == "screen").Observed);
    }

    [Fact]
    public void Ships_a_cursor_that_dispatches_once_and_does_not_pause_afterwards()
    {
        // The pointer is the user's again the moment it has been moved. A delay
        // after moving it would be the transition sitting still for a reason
        // nobody could name.
        ConvergencePolicy policy =
            new CursorProvider(new FakeCursor(), new FakeDisplay(new DisplayRect(0, 0, 1920, 1080)))
                .PolicyFor("Corner");

        Assert.False(policy.Verify);
        Assert.Equal(1, policy.MaxAttempts);
        Assert.Equal(TimeSpan.Zero, policy.PostDispatchDelay);
    }

    [Fact]
    public void Ships_a_dispatch_only_cursor_and_verifies_everything_else()
    {
        // The shipped stance, asserted on the real providers rather than on
        // stand-ins, because it is the providers the decision is about. Built
        // over their seams, so asking them what they intend cannot become a
        // reason for a test run to touch the machine it is running on.
        var pointer = new CursorProvider(new FakeCursor(), new FakeDisplay(new DisplayRect(0, 0, 1920, 1080)));
        var sound = new AudioProvider(new FakeAudioEndpoints());

        Assert.False(pointer.PolicyFor("Corner").Verify);

        Assert.True(new WindowsThemeProvider(new UnreadableStore()).PolicyFor("Dark").Verify);
        Assert.True(new DisplayProvider(new FakeDisplayDevices()).PolicyFor("TVOnly").Verify);
        Assert.True(sound.PolicyFor("TV").Verify);

        // And the one that has to outlast a television waking up retries most.
        Assert.True(sound.PolicyFor("TV").MaxAttempts > 1);
    }

    // ---- the transition's own deadline --------------------------------------

    [Fact]
    public async Task Gives_up_on_a_transition_that_has_run_out_of_time()
    {
        // The resource's own policy would spend twenty-four seconds on this.
        // The transition's deadline is what stops one stubborn resource from
        // spending the whole shell's afternoon on it.
        var screen = new Machine("screen")
        {
            Policy = Verifying(attempts: 3, window: 8, poll: 1),
            Reads = ["Desktop"],
        };

        using var world = new World(screen, timeout: TimeSpan.FromSeconds(5));

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Contains("ran out of time", outcome.Message, StringComparison.Ordinal);

        // Exactly the deadline: not cut short, and not overrun by a poll that
        // was already in flight when it arrived.
        Assert.Equal(TimeSpan.FromSeconds(5), world.Clock.Elapsed);

        Assert.False(outcome.StateRecorded);
        Assert.Null(world.Engine.ReadCurrentTargetKey(out _));
    }

    [Fact]
    public async Task Leaves_everything_after_a_timed_out_resource_alone()
    {
        var clock = new FakeTime();

        var screen = new Machine("screen", clock)
        {
            Policy = Verifying(attempts: 3, window: 8, poll: 1),
            Reads = ["Desktop"],
        };

        var pointer = new Machine("pointer", clock) { After = ["screen"] };

        using var world = new World(
            clock,
            [screen, pointer],
            Document("""{"screen":"TVOnly","pointer":"Corner"}"""),
            TimeSpan.FromSeconds(5));

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Empty(pointer.Commands);

        Assert.Equal(
            TransitionStepStatus.NotReached,
            Assert.Single(outcome.Steps, step => step.Resource == "pointer").Status);
    }

    [Fact]
    public async Task Starts_the_next_attempt_the_moment_the_last_one_is_over()
    {
        // A poll interval says how often to look, and nothing else. Sleeping
        // through the end of an attempt and noticing afterwards would make it
        // the resolution of every boundary in the system, so the last wait of an
        // attempt is clipped to the boundary rather than overrunning it.
        var clock = new FakeTime();

        var screen = new Machine("screen", clock)
        {
            Policy = Verifying(attempts: 2, window: 5, poll: 2),
            Reads = ["Desktop"],
        };

        using var world = new World(clock, [screen], document: null, timeout: null);

        await world.Engine.ApplyAsync("Film");

        Assert.Equal(2, screen.Commands.Count);

        // Five seconds exactly. Polls fall at two and four, and the wait that
        // would have reached six stops where the attempt does.
        Assert.Equal(TimeSpan.FromSeconds(5), screen.Commands[1].At);
    }

    // ---- what the machine is left in ----------------------------------------

    [Fact]
    public async Task Forgets_the_recorded_mode_before_it_changes_anything()
    {
        // The recorded name stops being true when the first resource moves, not
        // when the transition finishes going wrong -- and a transition that is
        // killed along with the machine never gets to a moment afterwards at
        // all. So it is cleared first, and written again only on success.
        var screen = new Machine("screen")
        {
            Policy = Verifying(attempts: 1, window: 2, poll: 1),
            Reads = ["Desktop"],
        };
        using var world = new World(screen);


        world.Record("Desktop");

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.True(outcome.Mutated);

        // Not "Desktop", which is a mode the machine was dragged out of, and not
        // "Film", which it never reached.
        Assert.Null(world.Engine.ReadCurrentTargetKey(out _));
        Assert.False(File.Exists(world.Root.StateFile));
    }

    [Fact]
    public async Task Keeps_the_recorded_mode_when_it_never_got_as_far_as_changing_anything()
    {
        // The other half, and the reason the clearing is placed where it is
        // rather than at the top of the transition. Nothing moved, so the old
        // answer is exactly as good as it was.
        var screen = new Machine("screen")
        {
            Policy = Verifying(window: 2, poll: 1),
            Reads = ["TVOnly"],
        };
        using var world = new World(screen);


        world.Record("Desktop");

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film", dryRun: true);

        Assert.True(outcome.DryRun);
        Assert.False(outcome.Mutated);
        Assert.Equal("Desktop", world.Engine.ReadCurrentTargetKey(out _));
    }

    [Fact]
    public async Task Forgets_the_recorded_mode_when_a_command_failed_half_way_through()
    {
        // A command that threw may still have done half of what it was asked, so
        // it counts as having changed the machine. Working out whether anything
        // moved by counting completed attempts would call this one untouched.
        var screen = new Machine("screen")
        {
            Fails = new InvalidOperationException("the television did not answer"),
            FailedAttempts = int.MaxValue,
        };
        using var world = new World(screen);


        world.Record("Desktop");

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.True(outcome.Mutated);
        Assert.Null(world.Engine.ReadCurrentTargetKey(out _));
    }

    // ---- cancellation -------------------------------------------------------

    [Fact]
    public async Task Reports_a_transition_cancelled_while_it_was_waiting_as_uncertain()
    {
        // Cancelling during a wait is the likeliest moment to cancel, because
        // waiting is where a slow transition spends its time. What was already
        // commanded stands, and nothing claims to know the result.
        var clock = new FakeTime();
        using var cancellation = new CancellationTokenSource();

        // On the second read, which is the first one taken while polling. The
        // first is the read every step begins with, and cancelling there would
        // be testing a request that never started.
        int reads = 0;

        var screen = new Machine("screen", clock)
        {
            Policy = Verifying(window: 8, poll: 1),
            Reads = ["Desktop"],
            OnRead = () =>
            {
                if (++reads == 2)
                {
                    cancellation.Cancel();
                }
            },
        };

        var pointer = new Machine("pointer", clock) { After = ["screen"] };

        using var world = new World(clock, screen, pointer, Document(
            """{"screen":"TVOnly","pointer":"Corner"}"""));

        TransitionOutcome outcome = await world.Engine.ApplyAsync(
            "Film",
            cancellationToken: cancellation.Token);

        Assert.Equal(TransitionStatus.Uncertain, outcome.Status);
        Assert.Single(screen.Commands);
        Assert.Empty(pointer.Commands);

        Assert.False(outcome.StateRecorded);
        Assert.Null(world.Engine.ReadCurrentTargetKey(out _));
    }

    // ---- dry run ------------------------------------------------------------

    [Fact]
    public async Task Walks_a_slow_resource_without_waiting_a_moment_for_it()
    {
        // A dry run reads once and stops. It must not dispatch, must not retry,
        // must not settle and must not spend the transition's time -- otherwise
        // asking what a mode would do would itself take as long as doing it.
        var screen = new Machine("screen")
        {
            Policy = Verifying(attempts: 3, window: 8, poll: 1, settle: 2),
            Reads = ["Desktop"],
        };
        using var world = new World(screen);


        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film", dryRun: true);

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.True(outcome.DryRun);

        Assert.Empty(screen.Commands);
        Assert.Equal(1, screen.ReadCount);
        Assert.Equal(TimeSpan.Zero, world.Clock.Elapsed);

        Assert.Equal(TransitionStepStatus.Planned, Assert.Single(outcome.Steps).Status);
        Assert.False(outcome.StateRecorded);
        Assert.False(File.Exists(world.Root.StateFile));
    }

    // ---- refused before anything moves --------------------------------------

    [Fact]
    public async Task Refuses_a_mode_whose_policy_could_never_converge()
    {
        // Resolved while planning, so it is refused with the machine untouched.
        // Discovered at dispatch it would be a transition that polls without
        // ever reading, which looks exactly like one that is nearly finished.
        var screen = new Machine("screen")
        {
            Policy = new ConvergencePolicy { Verify = true, PollInterval = TimeSpan.Zero },
        };
        using var world = new World(screen);


        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Refused, outcome.Status);
        Assert.Empty(screen.Commands);
        Assert.False(File.Exists(world.Root.StateFile));
    }

    [Fact]
    public async Task Refuses_a_mode_whose_provider_cannot_say_what_reaching_it_takes()
    {
        // A provider that throws while being planned is a defect in that
        // provider, and its caller should be handed a refusal naming the
        // resource -- not an exception out of a method whose whole contract is
        // that it decides before it changes anything.
        var screen = new Machine("screen") { PolicyThrows = new InvalidOperationException("no idea") };
        using var world = new World(screen);


        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Refused, outcome.Status);
        Assert.Contains("no idea", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(screen.Commands);
    }

    [Fact]
    public async Task Refuses_a_mode_whose_provider_cannot_say_what_it_needs()
    {
        var screen = new Machine("screen") { RequirementsThrow = new InvalidOperationException("cannot say") };
        using var world = new World(screen);


        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Refused, outcome.Status);
        Assert.Contains("cannot say", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(screen.Commands);
    }

    // ---- the policy's own rules ---------------------------------------------

    [Fact]
    public void Accepts_the_policy_a_provider_gets_for_saying_nothing()
    {
        Assert.True(ConvergencePolicy.DispatchOnce.IsUsable(out string? problem));
        Assert.Null(problem);
    }

    [Theory]
    [InlineData(0, false, 0, 0, 0)]                 // no attempt at all
    [InlineData(3, false, 0, 0, 0)]                 // retries with no pause between them
    [InlineData(1, false, 0, 0, 2)]                 // settles without ever reading
    [InlineData(1, true, 4, 0, 0)]                  // verifies but never reads
    [InlineData(1, true, 0, 1, 0)]                  // verifies with no time to do it in
    [InlineData(1, true, 1, 4, 0)]                  // gives up before it would read once
    [InlineData(1, true, 4, 1, -1)]                 // waits for a negative length of time
    public void Refuses_a_policy_that_could_not_converge(
        int attempts,
        bool verify,
        int window,
        int poll,
        int settle)
    {
        var policy = new ConvergencePolicy
        {
            Verify = verify,
            MaxAttempts = attempts,
            RetryInterval = TimeSpan.FromSeconds(window),
            PollInterval = TimeSpan.FromSeconds(poll),
            Settle = TimeSpan.FromSeconds(settle),
        };

        Assert.False(policy.IsUsable(out string? problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void Refuses_a_fixed_pause_on_something_it_is_going_to_read_back_anyway()
    {
        // Waiting a fixed time and then measuring is not two pieces of evidence,
        // it is one measurement that started late. The knob exists for resources
        // that have nothing to measure, and letting a verifying one have it would
        // make a blind pause indistinguishable from a settled readback.
        var policy = new ConvergencePolicy
        {
            Verify = true,
            RetryInterval = TimeSpan.FromSeconds(4),
            PollInterval = TimeSpan.FromSeconds(1),
            PostDispatchDelay = TimeSpan.FromSeconds(1),
        };

        Assert.False(policy.IsUsable(out string? problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void Accepts_a_pause_on_something_that_has_nothing_to_read_back()
    {
        var policy = new ConvergencePolicy { PostDispatchDelay = TimeSpan.FromSeconds(1) };

        Assert.True(policy.IsUsable(out string? problem));
        Assert.Null(problem);
    }

    [Fact]
    public void Refuses_a_pause_of_a_negative_length()
    {
        Assert.False(
            new ConvergencePolicy { PostDispatchDelay = TimeSpan.FromSeconds(-1) }.IsUsable(out _));
    }

    // ---- dispatching even when it already looks right -----------------------

    [Fact]
    public async Task Commands_a_resource_that_insists_on_it_even_when_the_readback_agrees()
    {
        // For a resource whose readback answers a narrower question than the
        // command asks. A television reports which input it is showing, and
        // reports it correctly, while what belongs to that input has been changed
        // by something else -- so agreement is not a reason to skip the command.
        var screen = new Machine("screen")
        {
            Policy = Verifying(window: 4, poll: 1) with { DispatchAtLeastOnce = true },
            Current = "TVOnly",
        };
        using var world = new World(screen);


        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);

        TransitionStep step = Assert.Single(outcome.Steps);

        // Applied, not AlreadyCorrect: something was done, and the report says so.
        Assert.Equal(TransitionStepStatus.Applied, step.Status);
        Assert.Equal("TVOnly", Assert.Single(screen.Commands).Value);
    }

    [Fact]
    public async Task Owes_exactly_one_command_to_insisting_and_no_more()
    {
        // The flag suppresses the shortcut once. It must not turn into a resource
        // that is commanded again on every attempt for the same reason -- the
        // first readback confirms it, and that is the end of it.
        var screen = new Machine("screen")
        {
            Policy = Verifying(attempts: 3, window: 4, poll: 1) with { DispatchAtLeastOnce = true },
            Current = "TVOnly",
        };
        using var world = new World(screen);


        Assert.Equal(TransitionStatus.Succeeded, (await world.Engine.ApplyAsync("Film")).Status);
        Assert.Single(screen.Commands);
    }

    // ---- what a failed command means depends on what else happened ----------

    [Fact]
    public async Task Calls_a_command_that_failed_after_a_cancellation_cancelled()
    {
        // The provider threw something ordinary, but the transition had just been
        // called off. Reporting its message as the reason the mode failed would
        // send somebody looking for a fault in a device that was working.
        using var stopping = new CancellationTokenSource();

        var screen = new Machine("screen")
        {
            Policy = Verifying(attempts: 3, window: 4, poll: 1),
            OnApply = stopping.Cancel,
            FailedAttempts = 3,
            Fails = new InvalidOperationException("the socket went away."),
        };
        using var world = new World(screen);


        world.Record("Desktop");

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film", cancellationToken: stopping.Token);

        Assert.Equal(TransitionStatus.Uncertain, outcome.Status);
        Assert.Single(screen.Commands);

        // And it is not retried: no new command is issued after a cancellation,
        // exactly as none is issued after the deadline.
        Assert.DoesNotContain("socket", Assert.Single(outcome.Steps).Problem!, StringComparison.Ordinal);

        // Something may have moved, so the recorded mode is gone.
        Assert.True(outcome.Mutated);
        Assert.Null(world.Engine.ReadCurrentTargetKey(out _));
    }

    [Fact]
    public async Task Calls_a_command_that_failed_after_the_deadline_a_timeout()
    {
        // The same rule for time. A call that outlasted the whole transition is a
        // timeout whatever it eventually complained about, and above all it must
        // not be retried into a transition that has no time left.
        var clock = new FakeTime();

        var screen = new Machine("screen", clock)
        {
            Policy = Verifying(attempts: 3, window: 4, poll: 1),
            OnApply = () => clock.Advance(TimeSpan.FromSeconds(60)),
            FailedAttempts = 3,
            Fails = new InvalidOperationException("the device is not ready."),
        };

        using var world = new World(clock, [screen], document: null, timeout: TimeSpan.FromSeconds(30));

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Single(screen.Commands);
        Assert.Contains("ran out of time", Assert.Single(outcome.Steps).Problem!, StringComparison.Ordinal);

        // The provider's own words are kept: a timeout is not a reason to throw
        // away the only diagnosis anybody had.
        Assert.Contains("not ready", Assert.Single(outcome.Steps).Problem!, StringComparison.Ordinal);
    }

    // ---- the old mode has to be forgettable before anything moves -----------

    [Fact]
    public async Task Refuses_to_touch_anything_when_it_cannot_forget_where_it_was()
    {
        // Clearing the recorded mode is not housekeeping that can be skipped when
        // it is inconvenient. It is the whole of what makes a half-applied machine
        // admit to being one, so a transition that cannot do it does not go.
        var screen = new Machine("screen") { Current = "Desktop" };
        using var world = new World(screen);


        world.Record("Desktop");

        // Held open with no sharing, which is what stops the delete.
        using (File.Open(world.Root.StateFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

            Assert.Equal(TransitionStatus.Refused, outcome.Status);

            // Nothing was dispatched, so nothing was changed.
            Assert.Empty(screen.Commands);
            Assert.False(outcome.Mutated);
            Assert.Contains("nothing was changed", outcome.Message, StringComparison.Ordinal);
        }

        // And once the file can be read again it still says where the machine is,
        // which is the point: the recorded mode survived because it is still true.
        Assert.Equal("Desktop", world.Engine.ReadCurrentTargetKey(out _));
    }

    [Fact]
    public async Task Still_refuses_when_the_first_resources_had_nothing_to_do()
    {
        // The veto belongs to the first resource that would MOVE something, not
        // to the first resource in the plan. One that was already correct
        // dispatches nothing and therefore never asks.
        var clock = new FakeTime();
        var settled = new Machine("first", clock) { Current = "TVOnly" };
        var moving = new Machine("second", clock) { Current = "Desktop" };

        using var world = new World(
            clock,
            [settled, moving],
            Document("""{"first":"TVOnly","second":"TVOnly"}"""),
            timeout: null);

        world.Record("Desktop");

        using (File.Open(world.Root.StateFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            TransitionOutcome outcome = await world.Engine.ApplyAsync("Film");

            Assert.Equal(TransitionStatus.Refused, outcome.Status);

            // The one that was already correct is reported as such; the one that
            // would have moved was never reached.
            Assert.Empty(moving.Commands);
            Assert.Empty(settled.Commands);
        }

        Assert.Equal("Desktop", world.Engine.ReadCurrentTargetKey(out _));
    }

    // ---- plumbing -----------------------------------------------------------

    private static ConvergencePolicy Verifying(int attempts = 1, int window = 4, int poll = 1, int settle = 0) =>
        new()
        {
            Verify = true,
            MaxAttempts = attempts,
            RetryInterval = TimeSpan.FromSeconds(window),
            PollInterval = TimeSpan.FromSeconds(poll),
            Settle = TimeSpan.FromSeconds(settle),
        };

    private static string Document(string resources = """{"screen":"TVOnly"}""") =>
        $$"""{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"film","name":"Film","resources":{{resources}}}]}]}""";

    /// <summary>
    /// A resource whose behaviour is written out in advance.
    /// </summary>
    /// <remarks>
    /// <see cref="Reads"/> is the sequence of answers the readback gives, one
    /// per call, repeating the last one once the script runs out. The first is
    /// always the read the engine takes before it dispatches anything, so a
    /// script says in one line what a machine does over several seconds --
    /// which is the only way a test of settling can state what it is testing.
    /// </remarks>
    private sealed class Machine(string key, FakeTime? clock = null) : IConvergentProvider, IDependentProvider
    {
        private readonly List<Command> _commands = [];

        private int _reads;

        public string Key { get; } = key;

        public string DisplayName => Key;

        /// <summary>Scripted readback. Null falls back to <see cref="Current"/>.</summary>
        public IReadOnlyList<string?>? Reads { get; init; }

        /// <summary>What Read answers when there is no script. Apply sets it.</summary>
        public string? Current { get; set; }

        public ConvergencePolicy Policy { get; init; } = ConvergencePolicy.DispatchOnce;

        /// <summary>Resource keys this runs after when they are in the mode too.</summary>
        public IReadOnlyList<string> After { get; init; } = [];

        /// <summary>What a failing Apply throws.</summary>
        public Exception? Fails { get; init; }

        /// <summary>How many Apply calls throw before the machine cooperates.</summary>
        public int FailedAttempts { get; init; }

        public Exception? PolicyThrows { get; init; }

        public Exception? RequirementsThrow { get; init; }

        public Action? OnRead { get; init; }

        /// <summary>
        /// Run inside Apply, after the call has been recorded.
        /// </summary>
        /// <remarks>
        /// So that a test can make the world change while a command is in
        /// progress -- a cancellation arriving, a deadline passing -- which is
        /// exactly the situation where what a subsequent failure MEANS is
        /// different from what it says.
        /// </remarks>
        public Action? OnApply { get; init; }

        /// <summary>Every Apply call, whether or not it worked, and when.</summary>
        public IReadOnlyList<Command> Commands => _commands;

        public int ReadCount => _reads;

        public IReadOnlyList<string> RunsAfter => After;

        public IReadOnlyList<ResourceRequirement> RequirementsFor(string target) =>
            RequirementsThrow is not null ? throw RequirementsThrow : [];

        public ConvergencePolicy PolicyFor(string target) =>
            PolicyThrows is not null ? throw PolicyThrows : Policy;

        public bool IsValidValue(string value, out string? problem)
        {
            problem = null;
            return true;
        }

        public string? Read()
        {
            string? answer = Reads is null
                ? Current
                : Reads[Math.Min(_reads, Reads.Count - 1)];

            _reads++;
            OnRead?.Invoke();

            return answer;
        }

        public void Apply(string value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _commands.Add(new Command(value, clock?.Elapsed ?? TimeSpan.Zero));

            OnApply?.Invoke();

            if (_commands.Count <= FailedAttempts)
            {
                throw Fails ?? new InvalidOperationException($"{Key} refused.");
            }

            if (Reads is null)
            {
                Current = value;
            }
        }
    }

    /// <summary>One Apply call: what it was told, and how far in.</summary>
    private sealed record Command(string Value, TimeSpan At);

    /// <summary>
    /// A theme store that is never asked anything.
    /// </summary>
    /// <remarks>
    /// Only so that the shipped theme provider can be built without reaching
    /// for the registry of the machine running the tests. Throwing rather than
    /// answering, because a test that started depending on either answer would
    /// be a test that had quietly stopped being about a policy.
    /// </remarks>
    private sealed class UnreadableStore : IThemeStore
    {
        public bool? ReadUsesLightTheme() => throw new NotSupportedException();

        public void WriteUsesLightTheme(bool light) => throw new NotSupportedException();
    }

    /// <summary>A configuration, a state file, a clock and an engine.</summary>
    private sealed class World : IDisposable
    {
        public World(Machine machine, string? document = null, TimeSpan? timeout = null)
            : this(new FakeTime(), [machine], document, timeout)
        {
        }

        public World(FakeTime clock, Machine first, Machine second, string? document = null)
            : this(clock, [first, second], document, timeout: null)
        {
        }

        public World(
            FakeTime clock,
            IReadOnlyList<Machine> machines,
            string? document,
            TimeSpan? timeout)
        {
            Clock = clock;
            Root = new TemporaryRoot();
            Root.Write(document ?? Document());

            IReadOnlyList<IModeResourceProvider> providers = [.. machines];

            Engine = new ModeEngine(
                ModeConfiguration.LoadOrCreate(Root.Path, providers),
                new ModeStateStore(Root.StateFile),
                providers,
                clock,
                timeout);
        }

        public FakeTime Clock { get; }

        public TemporaryRoot Root { get; }

        public ModeEngine Engine { get; }

        /// <summary>Puts a mode in the state file, as a finished transition would.</summary>
        public void Record(string mode)
        {
            Assert.True(new ModeStateStore(Root.StateFile).TryWrite(mode, Clock.GetUtcNow(), out string? problem));
            Assert.Null(problem);
            Assert.Equal(mode, Engine.ReadCurrentTargetKey(out _));
        }

        public void Dispose() => Root.Dispose();
    }
}

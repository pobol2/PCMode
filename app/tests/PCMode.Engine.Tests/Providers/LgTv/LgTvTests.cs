namespace PCMode.Engine.Tests;

/// <summary>
/// The television: what it is configured to be, when it may be touched, and what
/// it drags into a transition with it.
/// </summary>
/// <remarks>
/// Nothing here opens a socket, sends a packet or writes a credential to the
/// machine running the tests. The strongest claims are the negative ones -- a
/// plan that woke nothing, a dry run that prompted nobody -- and
/// <see cref="FakeTelevision"/> counts precisely the things that must not happen.
/// </remarks>
public sealed class LgTvTests
{
    private const string Host = "192.0.2.31";
    private const string Mac = "00:00:5E:00:53:31";

    // ---- one section, two resources -----------------------------------------

    [Fact]
    public void Both_television_resources_read_the_same_section()
    {
        // The point of the shared settings key. The address is written once, and
        // an input resource that never saw the section could not resolve a name.
        (TvPowerProvider power, TvInputProvider input, _, _) = Television(Configured());

        Assert.True(power.IsValidValue("On", out _));
        Assert.True(input.IsValidValue("PC", out _));
        Assert.False(input.IsValidValue("Console", out string? problem));
        Assert.Contains("PC", problem!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"lgTv":{"enabled":true,"macAddress":"00:00:5E:00:53:31"}}""", "host")]
    [InlineData("""{"lgTv":{"enabled":true,"host":"192.0.2.31"}}""", "macAddress")]
    [InlineData("""{"lgTv":{"enabled":true,"host":"not a host","macAddress":"00:00:5E:00:53:31"}}""", "host")]
    [InlineData("""{"lgTv":{"enabled":true,"host":"192.0.2.31","macAddress":"nonsense"}}""", "macAddress")]
    [InlineData(
        """{"lgTv":{"enabled":true,"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31","colour":"red"}}""",
        "colour"
    )]
    [InlineData("""{"lgTv":{"enabled":"yes","host":"192.0.2.31","macAddress":"00:00:5E:00:53:31"}}""", "enabled")]
    public void Refuses_a_television_that_says_it_is_enabled_and_is_not_reachable_as_written(
        string providers,
        string mentions
    )
    {
        // Enabled and incomplete is a mistake somebody made, and finding it when a
        // mode is applied instead of when the file is read means finding it in the
        // dark, half way through a transition.
        Assert.Contains(mentions, Refused(providers), StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_two_names_for_one_input()
    {
        // The readback would have to pick one, and whichever it picked would make
        // the other mode report the wrong input forever.
        string problem = Refused(
            """
            {"lgTv":{"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31",
             "inputs":{"PC":"HDMI_4","Desk":"hdmi4"}}}
            """
        );

        Assert.Contains("HDMI_4", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_an_input_however_it_was_written()
    {
        // "HDMI_4", "hdmi4" and "4" are the same socket and all get typed. The
        // television only answers to one of them.
        (_, TvInputProvider input, _, _) = Television(
            """
            {"lgTv":{"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31",
             "inputs":{"One":"HDMI_1","Two":"hdmi 2","Three":"3"}}}
            """
        );

        Assert.True(input.IsValidValue("Two", out _));
        Assert.True(input.IsValidValue("Three", out _));
    }

    [Fact]
    public void A_disabled_television_keeps_its_settings_and_is_still_a_resource()
    {
        // Switching it off must not mean re-supplying everything to switch it back
        // on, and the resource still exists so a mode can name it and be told why
        // it was left out rather than that nothing knows how to apply it.
        (TvPowerProvider power, _, _, _) = Television(
            """{"lgTv":{"enabled":false,"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31"}}"""
        );

        Assert.True(power.IsValidValue("On", out _));
        Assert.Equal(AvailabilityState.Disabled, power.AvailabilityOf("On").State);
    }

    // ---- availability asks the configuration, never the network -------------

    [Fact]
    public void Says_disabled_without_a_television_and_unknown_with_one()
    {
        // Two answers and no third. Disabled is settled, and is the one case where
        // a mode may leave the television out. A configured television is unknown,
        // because whether it is awake is a question about the room -- and never
        // unavailable, which would drop it at the moment it was about to work.
        (TvPowerProvider absent, _, _, _) = Television("""{}""");
        (TvPowerProvider present, TvInputProvider input, FakeTelevision set, _) = Television(Configured());

        Assert.Equal(AvailabilityState.Disabled, absent.AvailabilityOf("On").State);
        Assert.Equal(AvailabilityState.Unknown, present.AvailabilityOf("On").State);
        Assert.Equal(AvailabilityState.Unknown, input.AvailabilityOf("PC").State);

        // And asking cost nothing: no socket, no packet.
        Assert.Equal(0, set.Connections);
        Assert.Empty(set.Woken);
    }

    [Fact]
    public void Reports_a_television_that_is_asleep_as_unknown_rather_than_missing()
    {
        // The rule convergence exists for. A television that is off answers
        // nothing at all, and that must not read as "there is no television".
        (TvPowerProvider power, _, FakeTelevision set, _) = Television(Configured());

        set.Reachable = false;

        Assert.Equal(AvailabilityState.Unknown, power.AvailabilityOf("On").State);
        Assert.False(power.AvailabilityOf("On").IsDefinitelyOut);
    }

    // ---- nothing looks at a television without permission -------------------

    [Fact]
    public void Never_opens_a_pairing_prompt_in_order_to_read()
    {
        // A registration without a key IS the prompt, so an unpaired television
        // cannot be looked at without asking its owner to accept something. The
        // answer is "not known", and the screen stays as it was.
        (TvPowerProvider power, TvInputProvider input, FakeTelevision set, FakeCredentials keys) = Television(
            Configured()
        );

        Assert.Null(power.Read());
        Assert.Null(input.Read());

        Assert.Equal(LgTvObservationStatus.Unpaired, power.ObservedState.Power.Status);
        Assert.Equal(LgTvObservationStatus.Unpaired, power.ObservedState.Input.Status);

        Assert.Equal(0, set.Connections);
        Assert.Equal(0, set.Prompts);
        Assert.Equal(0, keys.Writes);
    }

    [Fact]
    public void Reads_a_paired_television_without_prompting_or_rewriting_anything()
    {
        (TvPowerProvider power, TvInputProvider input, FakeTelevision set, FakeCredentials keys) = Television(
            Configured(),
            paired: true
        );

        Assert.Equal("On", power.Read());
        Assert.Equal("PC", input.Read());

        Assert.Equal(LgTvObservationStatus.Observed, power.ObservedState.Power.Status);
        Assert.Equal("On", power.ObservedState.Power.Value);
        Assert.Equal(LgTvObservationStatus.Observed, power.ObservedState.Input.Status);
        Assert.Equal("PC", power.ObservedState.Input.Value);

        Assert.Equal(0, set.Prompts);
        Assert.Equal(0, keys.Writes);
    }

    [Fact]
    public async Task A_dry_run_wakes_nothing_and_prompts_nobody()
    {
        // The whole promise of a dry run, and the television is the resource where
        // breaking it would be most obvious: somebody's screen lighting up to ask
        // a question about a transition that was never going to happen.
        using var world = new TelevisionWorld("""{"tvPower":"On","tvInput":"PC"}""", Configured());

        TransitionOutcome outcome = await world.Engine.ApplyAsync("Film", dryRun: true);

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);

        Assert.Empty(world.Set.Woken);
        Assert.Equal(0, world.Set.Prompts);
        Assert.Equal(0, world.Set.Connections);
        Assert.Equal(0, world.Keys.Writes);
    }

    [Fact]
    public void Pairs_and_stores_the_key_only_when_something_was_actually_asked_for()
    {
        // The other half of the rule. A real transition may pair, and when it does
        // the key is kept -- with the certificate that was presented at the moment
        // somebody approved it, which is what makes pinning it honest.
        (_, TvInputProvider input, FakeTelevision set, FakeCredentials keys) = Television(Configured());

        input.Apply("PC", CancellationToken.None);

        Assert.Equal(1, set.Prompts);
        Assert.Equal(1, keys.Writes);
        Assert.Equal("HDMI_4", set.SwitchedTo);

        LgTvCredential stored = keys.Read(Host)!;

        Assert.Equal(set.IssuedKey, stored.ClientKey);
        Assert.Equal(set.Fingerprint, stored.CertificateFingerprint);
    }

    [Fact]
    public void Refuses_a_television_presenting_a_certificate_it_did_not_pair_with()
    {
        // Something else is answering on the television's address. It is refused
        // rather than handed the client key.
        (_, TvInputProvider input, FakeTelevision set, _) = Television(Configured(), paired: true);

        set.Fingerprint = new string('B', 64);

        Assert.Throws<IOException>(() => input.Apply("PC", CancellationToken.None));
        Assert.Null(set.SwitchedTo);
    }

    // ---- what each target actually does -------------------------------------

    [Fact]
    public void Switching_on_sends_a_packet_and_nothing_else()
    {
        // A television that is off is not listening on its socket to be asked
        // politely, so a magic packet is the whole of switching one on. Whether it
        // worked is answered afterwards by reading it back.
        (TvPowerProvider power, _, FakeTelevision set, _) = Television(Configured(), paired: true);

        power.Apply("On", CancellationToken.None);

        Assert.Equal(Mac, Assert.Single(set.Woken));
        Assert.Equal(0, set.Connections);
    }

    [Fact]
    public void Switching_off_a_television_that_is_already_off_is_not_a_failure()
    {
        // Otherwise every desktop mode fails whenever the television happens to be
        // off already, which is most of the time. Silence means "nothing to do"
        // here -- and, because the policy verifies nothing, it is never mistaken
        // for evidence that something was done.
        (TvPowerProvider power, _, FakeTelevision set, _) = Television(Configured(), paired: true);

        set.Reachable = false;

        power.Apply("Off", CancellationToken.None);

        Assert.False(power.PolicyFor("Off").Verify);
        Assert.True(power.PolicyFor("Off").PostDispatchDelay > TimeSpan.Zero);
    }

    [Fact]
    public void Switching_off_a_television_in_network_standby_is_not_a_failure_either()
    {
        // The case that is not the same as unreachable, and the one that was
        // getting this wrong. A television in standby keeps its network interface
        // awake -- that is what makes it wakeable at all -- so it ACCEPTS the
        // socket and then never finishes the registration, because webOS is not
        // running behind it. Treating that as a fault made every return to the
        // desktop fail on the ordinary evening when the television was off
        // already.
        (TvPowerProvider power, _, FakeTelevision set, _) = Television(
            Configured(),
            paired: true,
            clock: new FakeTime()
        );

        set.GoesQuiet = true;

        power.Apply("Off", CancellationToken.None);

        Assert.Equal(1, set.Connections);
    }

    [Fact]
    public void A_television_that_answers_and_says_no_still_fails()
    {
        // The other side of the line above. Silence is a device that is not there;
        // a refusal is a device that is there and would not do it, and absorbing
        // that as "nothing to do" would be inventing a success.
        (TvSignalProvider signal, _, FakeTelevision set, _) = Picture(WithProfiles());

        set.RefusesSettings = true;

        _ = Assert.Throws<InvalidOperationException>(() => signal.Apply("Gaming", CancellationToken.None));
    }

    [Fact]
    public void Does_not_call_a_television_that_is_still_waking_up_awake()
    {
        // It reports where it is going as well as where it is. Answering with the
        // destination would report the transition finished at the moment there is
        // most reason to think it is not.
        (TvPowerProvider power, _, FakeTelevision set, _) = Television(Configured(), paired: true);

        set.Processing = "Request Power On";

        Assert.Null(power.Read());
        Assert.Equal(LgTvObservationStatus.Unknown, power.ObservedState.Power.Status);

        set.Processing = null;

        Assert.Equal("On", power.Read());
    }

    [Fact]
    public void Reports_no_input_when_the_television_is_showing_something_else()
    {
        // A television on its own menu is showing no input at all. Not an error,
        // and not one of the answers a mode can ask for.
        (_, TvInputProvider input, FakeTelevision set, _) = Television(Configured(), paired: true);

        set.ForegroundApp = "com.webos.app.livetv";

        Assert.Null(input.Read());
    }

    [Fact]
    public void Fails_a_switch_the_television_refused()
    {
        (_, TvInputProvider input, FakeTelevision set, _) = Television(Configured(), paired: true);

        set.RefusesInput = true;

        Assert.Contains(
            "HDMI_4",
            Assert.Throws<InvalidOperationException>(() => input.Apply("PC", CancellationToken.None)).Message,
            StringComparison.Ordinal
        );
    }

    // ---- giving up, and being called off ------------------------------------

    [Fact]
    public void Keeps_an_unreachable_television_distinct_from_off()
    {
        // It accepted the socket and then went quiet -- network standby, which is
        // the commonest state a television is in. The read gives up on its own
        // deadline rather than waiting for the transition's. Silence is not an
        // observed Off answer: the status snapshot retains why there is no value.
        (TvPowerProvider power, _, FakeTelevision set, _) = Television(
            Configured(),
            paired: true,
            clock: new FakeTime()
        );

        set.GoesQuiet = true;

        Assert.Null(power.Read());
        Assert.Equal(LgTvObservationStatus.Unreachable, power.ObservedState.Power.Status);
        Assert.Contains(Host, power.ObservedState.Power.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_an_explicit_power_read_error_distinct_from_unreachable()
    {
        (TvPowerProvider power, _, FakeTelevision set, _) = Television(Configured(), paired: true);

        set.RefusesPower = true;

        Assert.Null(power.Read());
        Assert.Equal(LgTvObservationStatus.Error, power.ObservedState.Power.Status);
        Assert.Contains("refused", power.ObservedState.Power.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_television_that_was_never_reached_for_is_not_reported_as_off()
    {
        // The other half of the same rule, and the reason the connection reports
        // silence rather than concluding from it. Both of these produce no answer
        // from the television -- but one of them was asked and one of them was
        // not, and only the television that was asked and stayed quiet is
        // evidence of anything.
        (TvPowerProvider unpaired, _, FakeTelevision never, _) = Television(Configured());

        Assert.Null(unpaired.Read());
        Assert.Equal(0, never.Connections);

        (TvPowerProvider waking, _, FakeTelevision starting, _) = Television(Configured(), paired: true);

        starting.Processing = "Request Power On";

        // It answered, and what it said was "ask me again in a moment".
        Assert.Null(waking.Read());
    }

    [Fact]
    public void A_television_that_went_quiet_is_a_failure_and_not_a_cancellation()
    {
        // The distinction matters to the engine: a cancellation stops the whole
        // transition and reports it uncertain, where a device that went quiet is
        // one step that failed and can be retried.
        (_, TvInputProvider input, FakeTelevision set, _) = Television(
            Configured(),
            paired: true,
            clock: new FakeTime()
        );

        set.GoesQuiet = true;

        Exception thrown = Record.Exception(() => input.Apply("PC", CancellationToken.None))!;

        Assert.IsNotType<OperationCanceledException>(thrown);
        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains(Host, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Stops_when_the_transition_is_called_off()
    {
        (_, TvInputProvider input, FakeTelevision set, _) = Television(Configured(), paired: true);

        set.GoesQuiet = true;

        using var stopping = new CancellationTokenSource();

        stopping.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => input.Apply("PC", stopping.Token));
    }

    // ---- what the television drags into a transition ------------------------

    [Fact]
    public void An_input_brings_the_television_on_and_goes_after_it()
    {
        // Asking a television that is asleep to change input is asking nothing of
        // anybody, so this is a requirement rather than a preference: a mode that
        // names only the input gets the television switched on as part of getting
        // what it asked for.
        TransitionPlan plan = Plan("""{"tvInput":"PC"}""", Configured());

        Assert.Equal(["tvPower", "tvInput"], plan.Steps.Select(step => step.Key));
        Assert.Equal("On", plan.Steps[0].Target);
        Assert.Equal("tvInput", plan.Steps[0].RequiredBy);
    }

    [Fact]
    public void A_layout_that_needs_the_television_orders_the_whole_chain()
    {
        // The order the whole feature exists to produce: switch it on, put it on
        // the right input, and only then send it a display topology.
        TransitionPlan plan = Plan(
            """{"display":"TVOnly"}""",
            """
            {"lgTv":{"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31","inputs":{"PC":"HDMI_4"}},
             "display":{"monitors":{"TV":"\\\\?\\TV"},
              "layouts":{"TVOnly":{"monitors":["TV"],"requires":{"tvInput":"PC"}}}}}
            """
        );

        Assert.Equal(["tvPower", "tvInput", "display"], plan.Steps.Select(step => step.Key));
    }

    [Fact]
    public void Switching_off_puts_the_desktop_somewhere_first()
    {
        // Switching off a television that is showing the desktop leaves Windows
        // with nowhere to draw and whoever did it with nothing to look at. The
        // safe layout is required, which both brings it in and orders it first.
        TransitionPlan plan = Plan(
            """{"tvPower":"Off"}""",
            """
            {"lgTv":{"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31","safeDisplayTarget":"DeskOnly"},
             "display":{"monitors":{"Desk":"\\\\?\\Desk"},
              "layouts":{"DeskOnly":{"monitors":["Desk"]}}}}
            """
        );

        Assert.Equal(["display", "tvPower"], plan.Steps.Select(step => step.Key));
        Assert.Equal("DeskOnly", plan.Steps[0].Target);
        Assert.Equal("tvPower", plan.Steps[0].RequiredBy);
    }

    [Fact]
    public void Switching_on_requires_nothing_of_its_own()
    {
        // The thing everything else about the television depends on. Giving it a
        // requirement is how a dependency graph acquires a loop.
        (TvPowerProvider power, _, _, _) = Television(
            """{"lgTv":{"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31","safeDisplayTarget":"DeskOnly"}}"""
        );

        Assert.Empty(power.RequirementsFor("On"));
        Assert.Single(power.RequirementsFor("Off"));
    }

    [Fact]
    public void A_mode_with_no_television_leaves_the_television_out_and_says_so()
    {
        // Not a failure and not a success: nothing was attempted, and the report
        // says which resource and why. A mode that quietly did less on this
        // machine than on another is how somebody spends an evening looking for a
        // fault in their configuration.
        TransitionPlan plan = Plan("""{"theme":"Dark","tvPower":{"value":"On","optional":true}}""", "{}");

        Assert.Equal(["theme"], plan.Steps.Select(step => step.Key));

        OmittedResource omitted = Assert.Single(plan.Omissions);

        Assert.Equal("tvPower", omitted.Key);
        Assert.Equal(AvailabilityState.Disabled, omitted.Availability.State);
        Assert.Contains(LgTvSettings.SettingsPath, omitted.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_required_television_that_is_not_there_refuses_before_anything_moves()
    {
        // And explains itself through the chain, so somebody can see that the
        // input they asked for is why the television was needed.
        bool made = TransitionPlan.TryCreate(
            new ConfiguredMode(
                "Film",
                [new ConfiguredResource("tvInput", "PC")],
                new ConfiguredTarget
                {
                    Key = "film",
                    DisplayName = "Film",
                    DisplayPath = "Film",
                    Identity = new TargetIdentity("space", "film", null),
                }
            ),
            Providers("""{"lgTv":{"enabled":false,"inputs":{"PC":"HDMI_4"}}}"""),
            out TransitionPlan? plan,
            out string? problem
        );

        Assert.False(made);
        Assert.Null(plan);
        Assert.Contains("Television power", problem!, StringComparison.Ordinal);
        Assert.Contains("Television input", problem!, StringComparison.Ordinal);
    }

    // ---- picture profiles ---------------------------------------------------

    [Fact]
    public void Writes_a_signal_profile_under_the_names_the_television_answers_to()
    {
        // The whole reason the translation table exists. The file says
        // "uhddeepcolor_hdmi{n}" because that is how somebody writes it; webOS
        // answers to "uhdDeepColorHDMI4" and silently ignores anything else, so a
        // name that made it to the wire unchanged would be a setting that never
        // applied and never complained.
        (TvSignalProvider signal, _, FakeTelevision set, _) = Picture(WithProfiles());

        signal.Apply("Gaming", CancellationToken.None);

        Assert.Equal("on", set.Setting("other", "uhdDeepColorHDMI4"));
        Assert.Equal("on", set.Setting("other", "hdmiPcMode_hdmi4"));
        Assert.Equal("on", set.Setting("other", "gameMode_hdmi4"));
        Assert.Equal("on", set.Setting("other", "gameOptimizationHDMI4"));
        Assert.Equal("on", set.Setting("other", "gameOptimization"));
        Assert.Equal("on", set.Setting("other", "freesyncOLEDHDMI4"));
        Assert.Equal("on", set.Setting("other", "freesync"));
        Assert.Equal("boost", set.Setting("other", "inputOptimization"));
    }

    [Fact]
    public void Writes_nothing_until_the_alert_carrying_it_is_closed()
    {
        // How webOS actually applies these: an alert is created holding the
        // settings, and closing it is what runs them. A write that stopped after
        // creating the alert would look like it had worked and would have changed
        // nothing at all.
        (TvSignalProvider signal, _, FakeTelevision set, _) = Picture(WithProfiles());

        signal.Apply("Movie", CancellationToken.None);

        Assert.Contains("ssap://system.notifications/createAlert", set.Requests, StringComparer.Ordinal);
        Assert.Contains("ssap://system.notifications/closeAlert", set.Requests, StringComparer.Ordinal);
        Assert.Equal("off", set.Setting("other", "gameMode_hdmi4"));
    }

    [Fact]
    public void A_television_that_refuses_a_profile_fails_rather_than_reporting_success()
    {
        (TvSignalProvider signal, _, FakeTelevision set, _) = Picture(WithProfiles());

        set.RefusesSettings = true;

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
            signal.Apply("Gaming", CancellationToken.None)
        );

        Assert.Contains("uhdDeepColorHDMI4", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_picture_mode_is_written_by_itself_and_first()
    {
        // Selecting a picture mode loads that mode's own stored values, so
        // anything sent with it in the same breath is overwritten by the act of
        // selecting it.
        (_, TvProfileProvider picture, FakeTelevision set, _) = Picture(
            """
            {"lgTv":{"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31","inputs":{"PC":"HDMI_4"},
             "profiles":{"Movie":{"pictureMode":"hdrFilmMaker","settings":{"brightness":"50"}}}}}
            """
        );

        picture.Apply("Movie", CancellationToken.None);

        Assert.Equal(["pictureMode", "brightness"], set.Writes);
        Assert.Equal("hdrFilmMaker", set.PictureMode);
    }

    [Fact]
    public void Reads_back_the_profile_a_picture_mode_belongs_to()
    {
        (_, TvProfileProvider picture, FakeTelevision set, _) = Picture(WithProfiles());

        set.PictureMode = "hdrGame";
        Assert.Equal("Gaming", picture.Read());

        // A picture mode somebody chose with the remote control is not an error
        // and simply is not one of the answers a mode can ask for.
        set.PictureMode = "vivid";
        Assert.Null(picture.Read());
    }

    [Fact]
    public void Writes_the_picture_even_when_the_readback_already_agrees()
    {
        // The picture mode is the only part of a profile that can be read back, so
        // a readback that agrees has confirmed one setting out of nine. A
        // transition interrupted part way through leaves exactly that -- and this
        // is the flag that repairs it.
        (_, TvProfileProvider picture, _, _) = Picture(WithProfiles());

        Assert.True(picture.PolicyFor("Gaming").DispatchAtLeastOnce);
        Assert.False(picture.PolicyFor("Gaming").PostDispatchDelay > TimeSpan.Zero);
        Assert.True(picture.PolicyFor("Gaming").Verify);
    }

    [Fact]
    public void Refuses_two_profiles_that_the_television_would_report_identically()
    {
        // The picture mode is how a profile is recognised. Two profiles sharing
        // one would make the readback a coin toss, and whichever lost would read
        // as wrong forever.
        string problem = Refused(
            """
            {"lgTv":{"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31","inputs":{"PC":"HDMI_4"},
             "profiles":{"Movie":{"pictureMode":"hdrFilmMaker"},"YouTube":{"pictureMode":"hdrFilmMaker"}}}}
            """
        );

        Assert.Contains("hdrFilmMaker", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"Gaming":{"pictureMode":"hdrGame","signal":{"turbo":"on"}}}""", "turbo")]
    [InlineData("""{"Gaming":{"pictureMode":"hdrGame","signal":{"freesync":"maybe"}}}""", "maybe")]
    [InlineData("""{"Gaming":{"signal":{"freesync":"on"}}}""", "pictureMode")]
    [InlineData("""{"Gaming":{"pictureMode":"hdrGame","colour":"red"}}""", "colour")]
    [InlineData("""{"Gaming":{"pictureMode":"hdrGame","settings":{"pictureMode":"game"}}}""", "pictureMode")]
    public void Refuses_a_profile_that_would_quietly_do_nothing(string profiles, string mentions)
    {
        // The television reports none of these as a mistake. It accepts an
        // unknown key, ignores it, and leaves the picture as it was -- so the
        // check happens here or it does not happen at all.
        // Concatenated rather than interpolated: a raw string holding JSON is
        // mostly closing braces, and interpolating into one is a counting exercise
        // that changes every time the JSON does.
        string problem = Refused(
            """{"lgTv":{"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31","inputs":{"PC":"HDMI_4"},"profiles":"""
                + profiles
                + "}}"
        );

        Assert.Contains(mentions, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_per_socket_setting_when_nothing_says_which_socket()
    {
        // One input needs no choosing. Two do, and a profile that silently picked
        // one would configure the wrong HDMI port.
        string problem = Refused(
            """
            {"lgTv":{"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31",
             "inputs":{"PC":"HDMI_4","Console":"HDMI_2"},
             "profiles":{"Gaming":{"pictureMode":"hdrGame","signal":{"freesyncoled_hdmi{n}":"on"}}}}}
            """
        );

        Assert.Contains(".input", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_profile_names_its_own_input_and_resolves_its_own_HDMI_socket()
    {
        // The replacement for the television-wide 'profileInput': two profiles,
        // each naming a different one of two inputs, and each resolving the
        // socket that input actually is.
        (IReadOnlyList<IModeResourceProvider> all, _, _) = Built(
            """
            {"lgTv":{"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31",
             "inputs":{"PC":"HDMI_4","Console":"HDMI_2"},
             "profiles":{
               "PC Gaming":{"pictureMode":"hdrGame","input":"PC","signal":{"freesyncoled_hdmi{n}":"on"}},
               "Console Gaming":{"pictureMode":"game","input":"Console","signal":{"freesyncoled_hdmi{n}":"on"}}}}}
            """
        );

        LgTvSettings settings = all.OfType<TvPowerProvider>().Single().Settings;

        LgTvProfile pc = settings.ProfileFor("PC Gaming")!;
        LgTvProfile console = settings.ProfileFor("Console Gaming")!;

        Assert.Equal("PC", pc.InputName);
        Assert.Equal("freesyncOLEDHDMI4", Assert.Single(pc.Signal).Key);

        Assert.Equal("Console", console.InputName);
        Assert.Equal("freesyncOLEDHDMI2", Assert.Single(console.Signal).Key);
    }

    [Fact]
    public void Refuses_a_profile_whose_own_input_is_not_one_of_the_televisions()
    {
        string problem = Refused(
            """
            {"lgTv":{"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31",
             "inputs":{"PC":"HDMI_4"},
             "profiles":{"Gaming":{"pictureMode":"hdrGame","input":"Console"}}}}
            """
        );

        Assert.Contains("Console", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_written_before_profiles_could_name_their_own_input_still_reads()
    {
        // The old television-wide 'profileInput' is still honoured as the
        // default for a profile that does not name its own.
        (TvSignalProvider signal, _, _, _) = Picture(
            """
            {"lgTv":{"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31",
             "inputs":{"PC":"HDMI_4","Console":"HDMI_2"},"profileInput":"Console",
             "profiles":{"Gaming":{"pictureMode":"hdrGame","signal":{"freesyncoled_hdmi{n}":"on"}}}}}
            """
        );

        Assert.Equal(["tvPower", "tvInput"], signal.RequirementsFor("Gaming").Select(requirement => requirement.Key));

        ResourceRequirement inputRequirement = signal
            .RequirementsFor("Gaming")
            .Single(requirement => requirement.Key == "tvInput");

        Assert.Equal("Console", inputRequirement.Target);
    }

    [Fact]
    public void Reads_and_dry_runs_never_touch_an_unpaired_television()
    {
        // The same promise the rest of the television keeps, extended to the two
        // resources that were added last. A registration without a key IS the
        // pairing prompt.
        (TvSignalProvider signal, TvProfileProvider picture, FakeTelevision set, FakeCredentials keys) = Picture(
            WithProfiles(),
            paired: false
        );

        Assert.Null(signal.Read());
        Assert.Null(picture.Read());

        Assert.Equal(0, set.Connections);
        Assert.Equal(0, set.Prompts);
        Assert.Equal(0, keys.Writes);
    }

    // ---- the order the whole feature exists to produce ----------------------

    [Fact]
    public void A_picture_orders_the_television_around_the_display_switch()
    {
        // The sequence the two halves of a profile were split to produce. The
        // signal settings renegotiate the HDMI link, so they go before the desktop
        // moves onto the television; the picture mode can only be selected against
        // a live signal, so it goes after.
        TransitionPlan plan = Plan(
            """{"tvProfile":"Gaming","display":"TVOnly"}""",
            """
            {"lgTv":{"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31","inputs":{"PC":"HDMI_4"},
             "profiles":{"Gaming":{"pictureMode":"hdrGame","signal":{"gamemode_hdmi{n}":"on"}}}},
             "display":{"monitors":{"TV":"\\\\?\\TV"},
              "layouts":{"TVOnly":{"monitors":["TV"],"requires":{"tvInput":"PC"}}}}}
            """
        );

        Assert.Equal(["tvPower", "tvInput", "tvSignal", "display", "tvProfile"], plan.Steps.Select(step => step.Key));
    }

    [Fact]
    public void A_picture_on_its_own_brings_the_whole_television_with_it()
    {
        // And leaves the monitors alone. Naming a picture pulls in its signal
        // half, which pulls in the input, which pulls in the power -- but the
        // display is an ordering preference, so a mode that did not ask for a
        // layout does not get one invented for it.
        TransitionPlan plan = Plan("""{"tvProfile":"Gaming"}""", WithProfiles());

        Assert.Equal(["tvPower", "tvInput", "tvSignal", "tvProfile"], plan.Steps.Select(step => step.Key));
        Assert.Equal("tvProfile", plan.Steps[2].RequiredBy);
    }

    [Fact]
    public void The_sound_is_chosen_after_everything_that_disturbs_the_cable()
    {
        // The whole order, end to end, as a real television mode produces it. The
        // sound goes last not because it depends on the picture, but because
        // everything before it has been renegotiating the link the endpoint
        // arrives over -- and an endpoint selected while that is happening is one
        // that can vanish from under the selection.
        var set = new FakeTelevision();

        IReadOnlyList<IModeResourceProvider> all =
        [
            .. All(new LgTvConnection(set, new FakeCredentials())),
            new AudioProvider(new FakeAudioEndpoints()),
        ];

        ModeConfiguration configuration = ModeConfiguration.Parse(
            Document(
                """{"tvProfile":"Movie","display":"TVOnly","audio":"TV"}""",
                """
                {"lgTv":{"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31","inputs":{"PC":"HDMI_4"},
                 "profiles":{"Movie":{"pictureMode":"hdrFilmMaker","signal":{"gamemode_hdmi{n}":"off"}}}},
                 "display":{"monitors":{"TV":"\\\\?\\TV"},
                  "layouts":{"TVOnly":{"monitors":["TV"],"requires":{"tvInput":"PC"}}}},
                 "audio":{"devices":{"TV":"LG TV"}}}
                """
            ),
            all
        );

        Assert.True(
            TransitionPlan.TryCreate(
                configuration.Modes[0],
                all.ToDictionary(provider => provider.Key, StringComparer.OrdinalIgnoreCase),
                out TransitionPlan? plan,
                out string? problem
            ),
            problem
        );

        Assert.Equal(
            ["tvPower", "tvInput", "tvSignal", "display", "tvProfile", "audio"],
            plan!.Steps.Select(step => step.Key)
        );
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>The configured television, with the two profiles this machine has.</summary>
    private static string WithProfiles() =>
        """
            {"lgTv":{"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31","inputs":{"PC":"HDMI_4"},
             "profiles":{
               "Gaming":{"pictureMode":"hdrGame","signal":{
                 "uhddeepcolor_hdmi{n}":"on","hdmipcmode_hdmi{n}":"on","gamemode_hdmi{n}":"on",
                 "gameoptimization_hdmi{n}":"on","gameoptimization":"on","freesyncoled_hdmi{n}":"on",
                 "freesync":"on","inputoptimization":"boost"},"settings":{}},
               "Movie":{"pictureMode":"hdrFilmMaker","signal":{
                 "uhddeepcolor_hdmi{n}":"on","hdmipcmode_hdmi{n}":"on","gamemode_hdmi{n}":"off",
                 "gameoptimization_hdmi{n}":"off","gameoptimization":"off","freesyncoled_hdmi{n}":"on",
                 "freesync":"on","inputoptimization":"standard"},"settings":{}}}}}
            """;

    /// <remarks>
    /// The address and MAC address are written out rather than interpolated. A raw
    /// string holding JSON is mostly closing braces, and interpolation in one is a
    /// counting exercise that changes every time the JSON does.
    /// </remarks>
    private static string Configured() =>
        """{"lgTv":{"host":"192.0.2.31","macAddress":"00:00:5E:00:53:31","inputs":{"PC":"HDMI_4"}}}""";

    /// <summary>A configured pair of television providers, and what they talk to.</summary>
    private static (TvPowerProvider Power, TvInputProvider Input, FakeTelevision Set, FakeCredentials Keys) Television(
        string providers,
        bool paired = false,
        TimeProvider? clock = null
    )
    {
        (IReadOnlyList<IModeResourceProvider> all, FakeTelevision set, FakeCredentials keys) = Built(
            providers,
            paired,
            clock
        );

        return (all.OfType<TvPowerProvider>().Single(), all.OfType<TvInputProvider>().Single(), set, keys);
    }

    /// <summary>The two halves of a picture profile, and what they talk to.</summary>
    private static (
        TvSignalProvider Signal,
        TvProfileProvider Picture,
        FakeTelevision Set,
        FakeCredentials Keys
    ) Picture(string providers, bool paired = true, TimeProvider? clock = null)
    {
        (IReadOnlyList<IModeResourceProvider> all, FakeTelevision set, FakeCredentials keys) = Built(
            providers,
            paired,
            clock
        );

        return (all.OfType<TvSignalProvider>().Single(), all.OfType<TvProfileProvider>().Single(), set, keys);
    }

    /// <summary>
    /// Every provider, configured through the real loader.
    /// </summary>
    /// <remarks>
    /// Through the loader rather than by hand, so that what is exercised is the
    /// shared-section wiring -- four providers reading one 'lgTv' section -- rather
    /// than a shortcut around it.
    /// </remarks>
    private static (IReadOnlyList<IModeResourceProvider> All, FakeTelevision Set, FakeCredentials Keys) Built(
        string providers,
        bool paired = false,
        TimeProvider? clock = null
    )
    {
        var set = new FakeTelevision();
        var keys = new FakeCredentials();

        if (paired)
        {
            _ = keys.Paired(Host, "stored-key", set.Fingerprint);
        }

        IReadOnlyList<IModeResourceProvider> all = All(new LgTvConnection(set, keys, clock));

        _ = ModeConfiguration.Parse(Document("""{"theme":"Dark"}""", providers), all);

        return (all, set, keys);
    }

    private static IReadOnlyDictionary<string, IModeResourceProvider> Providers(string providers) =>
        Built(providers).All.ToDictionary(provider => provider.Key, StringComparer.OrdinalIgnoreCase);

    private static TransitionPlan Plan(string resources, string providers)
    {
        var set = new FakeTelevision();

        IReadOnlyList<IModeResourceProvider> all = All(new LgTvConnection(set, new FakeCredentials()));

        ModeConfiguration configuration = ModeConfiguration.Parse(Document(resources, providers), all);

        bool made = TransitionPlan.TryCreate(
            configuration.Modes[0],
            all.ToDictionary(provider => provider.Key, StringComparer.OrdinalIgnoreCase),
            out TransitionPlan? plan,
            out string? problem
        );

        Assert.True(made, problem);

        // Planning is a decision, and deciding must not touch anything.
        Assert.Equal(0, set.Connections);
        Assert.Equal(0, set.Prompts);
        Assert.Empty(set.Woken);

        return plan!;
    }

    private static string Refused(string providers) =>
        Assert
            .Throws<ModeConfigurationException>(() =>
                ModeConfiguration.Parse(
                    Document("""{"theme":"Dark"}""", providers),
                    All(new LgTvConnection(new FakeTelevision(), new FakeCredentials()))
                )
            )
            .Message;

    private static IReadOnlyList<IModeResourceProvider> All(LgTvConnection connection) =>
        [
            new WindowsThemeProvider(new NoTheme()),
            new TvPowerProvider(connection),
            new TvInputProvider(connection),
            new TvSignalProvider(connection),
            new DisplayProvider(new FakeDisplayDevices()),
            new TvProfileProvider(connection),
        ];

    private static string Document(string resources, string providers) =>
        $$"""
            {"schemaVersion":1,"providers":{{providers}},
             "spaces":[{"id":"space","name":"Space","activities":[
               {"id":"film","name":"Film","resources":{{resources}}}]}]}
            """;

    /// <summary>A theme that is only here to make a document have two resources.</summary>
    private sealed class NoTheme : IThemeStore
    {
        public bool? ReadUsesLightTheme() => true;

        public void WriteUsesLightTheme(bool light) { }
    }

    /// <summary>A whole engine with a television in it.</summary>
    private sealed class TelevisionWorld : IDisposable
    {
        public TelevisionWorld(string resources, string providers)
        {
            Root = new TemporaryRoot();
            Set = new FakeTelevision();
            Keys = new FakeCredentials();

            IReadOnlyList<IModeResourceProvider> providerList = All(new LgTvConnection(Set, Keys));

            Root.Write(Document(resources, providers));

            Engine = new ModeEngine(
                ModeConfiguration.LoadOrCreate(Root.Path, providerList),
                new ModeStateStore(Root.StateFile),
                providerList,
                new FakeTime()
            );
        }

        public TemporaryRoot Root { get; }

        public FakeTelevision Set { get; }

        public FakeCredentials Keys { get; }

        public ModeEngine Engine { get; }

        public void Dispose() => Root.Dispose();
    }
}

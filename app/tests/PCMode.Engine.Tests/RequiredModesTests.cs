namespace PCMode.Engine.Tests;

/// <summary>
/// The four modes, as a whole document, planned end to end.
/// </summary>
/// <remarks>
/// WHY THIS IS ONE TEST CLASS AND NOT TWELVE ASSERTIONS SPREAD AROUND
///
/// Every provider here is already checked on its own, and every rule of the
/// planner is checked with invented resources that have no meaning. What none of
/// that can catch is the thing that actually goes wrong: a document where each
/// line is individually valid and the mode it describes is not the one somebody
/// wanted. Whether Movie starts the scaler before the player, whether the player
/// waits for the television, whether Desktop stops all four programs -- those are
/// properties of a configuration, and they are only true or false once there is
/// one.
///
/// So this reads the shipped example, builds the same providers the shell builds,
/// and asks what each mode would do. The document is a real file rather than a
/// string in here on purpose: a fixture that drifted from the example would leave
/// the example unchecked, which is the file people actually copy.
///
/// NOTHING REAL IS TOUCHED
///
/// Every provider is given a fake to talk to. Planning asks providers what is
/// available, and the honest implementations of that question look at the machine
/// running the tests -- for a file, a URI handler, a monitor. Answering from an
/// invented machine is what makes this test say the same thing on every machine,
/// and what stops it from having an opinion about whether Steam is installed here.
/// </remarks>
public sealed class RequiredModesTests
{
    private const string Desk1 = @"\\?\DISPLAY#DEL4321#5&1a2b3c&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string Desk2 = @"\\?\DISPLAY#DEL4321#5&1a2b3c&0&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string Tv = @"\\?\DISPLAY#GSM7700#5&1a2b3c&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    // ---- the document itself ------------------------------------------------

    [Fact]
    public void The_example_configuration_is_one_this_build_can_apply()
    {
        // Every resource of every mode, checked against a real provider: unknown
        // key, unusable value, unknown settings section, contradictory
        // requirement. A shipped example that does not load is worse than none.
        Machine machine = Load();

        Assert.Equal(
            ["desk/desktop", "desk/gaming", "desk/movie", "desk/youtube"],
            machine.Configuration.TargetKeys());
    }

    // ---- what each mode is --------------------------------------------------

    [Fact]
    public void Desktop_comes_back_to_the_desk_and_puts_everything_away()
    {
        TransitionPlan plan = Load().Plan("Desktop");

        Assert.Equal("Desktop", Target(plan, "display"));
        Assert.Equal("Headphones", Target(plan, "audio"));
        Assert.Equal("Light", Target(plan, "theme"));
        Assert.Equal("Off", Target(plan, "tvPower"));
        Assert.Equal("Off", Target(plan, "doNotDisturb"));
        Assert.Equal("Center", Target(plan, "cursor"));
        Assert.Equal("0020FF", Target(plan, "caseRgb"));

        foreach (string application in (string[])["Steam", "Stremio", "YouTube", "LosslessScaling"])
        {
            Assert.Equal("Stopped", Target(plan, application));
        }

        // The desktop has somewhere to go before the television goes dark.
        // Switching off the screen the desktop is on leaves Windows nowhere to
        // draw and whoever did it with nothing to look at.
        Assert.True(Before(plan, "display", "tvPower"));
    }

    [Fact]
    public void Gaming_puts_the_television_in_game_mode_and_opens_Big_Picture()
    {
        TransitionPlan plan = Load().Plan("Gaming");

        Assert.Equal("TVOnly", Target(plan, "display"));
        Assert.Equal("Gaming", Target(plan, "tvProfile"));
        Assert.Equal("Headphones", Target(plan, "audio"));
        Assert.Equal("Dark", Target(plan, "theme"));
        Assert.Equal("On", Target(plan, "doNotDisturb"));
        Assert.Equal("Corner", Target(plan, "cursor"));
        Assert.Equal("FFFFFF", Target(plan, "caseRgb"));
        Assert.Equal("Running", Target(plan, "Steam"));

        Assert.Equal("Stopped", Target(plan, "Stremio"));
        Assert.Equal("Stopped", Target(plan, "YouTube"));
        Assert.Equal("Stopped", Target(plan, "LosslessScaling"));

        // Steam is running whether or not it is in Big Picture, so the request
        // goes out again even when the process is already there.
        Assert.True(Step(plan, "Steam").Policy.DispatchAtLeastOnce);
    }

    [Fact]
    public void Movie_runs_the_scaler_first_then_the_player_and_makes_it_full_screen()
    {
        Machine machine = Load();
        TransitionPlan plan = machine.Plan("Movie");

        Assert.Equal("TVOnly", Target(plan, "display"));
        Assert.Equal("Movie", Target(plan, "tvProfile"));
        Assert.Equal("TV", Target(plan, "audio"));
        Assert.Equal("Dark", Target(plan, "theme"));
        Assert.Equal("On", Target(plan, "doNotDisturb"));
        Assert.Equal("Corner", Target(plan, "cursor"));
        Assert.Equal("7B00FF", Target(plan, "caseRgb"));

        Assert.Equal("Running", Target(plan, "LosslessScaling"));
        Assert.Equal("Running", Target(plan, "Stremio"));
        Assert.Equal("Stopped", Target(plan, "Steam"));
        Assert.Equal("Stopped", Target(plan, "YouTube"));

        // The order the mode never states. The scaler has to be up before the
        // player starts, and the player has to open onto the television rather
        // than onto a desktop it is about to leave.
        Assert.True(Before(plan, "LosslessScaling", "Stremio"));
        Assert.True(Before(plan, "display", "Stremio"));

        // And the full-screen key is part of what starting the player means.
        ApplicationDefinition player = machine.Application("Stremio");

        Assert.NotNull(player.Ready);
        Assert.Equal("f", player.Ready!.Keys);
        Assert.True(player.Ready.Activate);
    }

    [Fact]
    public void YouTube_is_the_movie_setup_with_one_different_program_and_one_different_colour()
    {
        TransitionPlan plan = Load().Plan("YouTube");

        Assert.Equal("TVOnly", Target(plan, "display"));
        Assert.Equal("Movie", Target(plan, "tvProfile"));
        Assert.Equal("TV", Target(plan, "audio"));
        Assert.Equal("Dark", Target(plan, "theme"));
        Assert.Equal("On", Target(plan, "doNotDisturb"));
        Assert.Equal("Corner", Target(plan, "cursor"));
        Assert.Equal("FF0000", Target(plan, "caseRgb"));

        Assert.Equal("Running", Target(plan, "YouTube"));
        Assert.Equal("Stopped", Target(plan, "Steam"));
        Assert.Equal("Stopped", Target(plan, "Stremio"));
        Assert.Equal("Stopped", Target(plan, "LosslessScaling"));

        Assert.True(Before(plan, "display", "YouTube"));
    }

    // ---- the ordering nobody wrote down -------------------------------------

    [Fact]
    public void The_television_chain_is_brought_in_whole_by_naming_a_picture()
    {
        // No mode mentions the television's power or its input. Naming a picture
        // profile is enough: the profile needs the HDMI settings, those need the
        // input, and the input needs the television awake.
        TransitionPlan plan = Load().Plan("Gaming");

        Assert.Contains("tvPower", plan.Steps.Select(step => step.Key));
        Assert.Contains("tvInput", plan.Steps.Select(step => step.Key));
        Assert.Contains("tvSignal", plan.Steps.Select(step => step.Key));

        Assert.True(Before(plan, "tvPower", "tvInput"));
        Assert.True(Before(plan, "tvInput", "tvSignal"));

        // The HDMI settings go in before the desktop moves, because they
        // renegotiate the link; the picture mode goes in after, because webOS
        // refuses an HDR mode until the signal really is HDR.
        Assert.True(Before(plan, "tvSignal", "display"));
        Assert.True(Before(plan, "display", "tvProfile"));

        // And the sound is chosen last of all, after everything that disturbs the
        // link has finished disturbing it.
        Assert.True(Before(plan, "tvProfile", "audio"));
    }

    [Fact]
    public void Stopping_a_program_needs_nothing_and_waits_for_nothing()
    {
        // Desktop stops all four programs and never mentions the television's
        // input. A mode that dragged one awake in order to close a media player
        // would be doing the opposite of what it was asked.
        TransitionPlan plan = Load().Plan("Desktop");

        Assert.DoesNotContain("tvInput", plan.Steps.Select(step => step.Key));
        Assert.DoesNotContain("tvSignal", plan.Steps.Select(step => step.Key));
    }

    // ---- what happens on a machine that has less of it -----------------------

    [Fact]
    public void A_machine_without_the_programs_still_gets_the_rest_of_Desktop()
    {
        // Every application in Desktop is a 'Stopped' target, and one that is not
        // installed is already stopped. A machine with one fewer program must
        // still be able to go back to its desk.
        Machine bare = Load(installApplications: false);

        TransitionPlan plan = bare.Plan("Desktop");

        Assert.Equal("Desktop", Target(plan, "display"));
        Assert.Equal("Stopped", Target(plan, "Steam"));
        Assert.Empty(plan.Omissions);
    }

    [Fact]
    public void A_mode_that_needs_a_program_this_machine_does_not_have_is_refused_by_name()
    {
        Machine bare = Load(installApplications: false);

        Assert.False(bare.TryPlan("Gaming", out _, out string? problem));

        Assert.Contains("Steam", problem);
        Assert.Contains("steam:", problem);
    }

    [Fact]
    public void Lighting_is_left_out_rather_than_refusing_a_mode()
    {
        // Ambience is optional in all four modes. A machine with no OpenRGB still
        // gets the mode -- and is told what it did not get, because a mode that
        // quietly does less on one machine than another is how somebody spends an
        // evening looking for a fault in their configuration.
        Machine plain = Load(lighting: false);

        TransitionPlan plan = plain.Plan("Movie");

        Assert.Equal("Running", Target(plan, "Stremio"));

        OmittedResource left = Assert.Single(plan.Omissions);

        Assert.Equal("caseRgb", left.Key);
        Assert.Contains("OpenRGB", left.Reason);
    }

    // ---- the harness --------------------------------------------------------

    private static string Target(TransitionPlan plan, string key) => Step(plan, key).Target;

    private static PlannedResource Step(TransitionPlan plan, string key) =>
        plan.Steps.Single(step => string.Equals(step.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether one resource is applied before another.</summary>
    private static bool Before(TransitionPlan plan, string first, string second) =>
        Position(plan, first) < Position(plan, second);

    private static int Position(TransitionPlan plan, string key)
    {
        for (int index = 0; index < plan.Steps.Count; index++)
        {
            if (string.Equals(plan.Steps[index].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        throw new InvalidOperationException($"'{key}' is not in the plan for '{plan.Mode}'.");
    }

    /// <summary>The shipped example, found by walking up from the test binary.</summary>
    private static string ExampleDocument()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "config", "pcmode.example.json");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find app/config/pcmode.example.json from the test output folder.");
    }

    private static Machine Load(bool installApplications = true, bool lighting = true) =>
        new(ExampleDocument(), installApplications, lighting);

    /// <summary>
    /// The example document, on an invented machine that has everything it names.
    /// </summary>
    private sealed class Machine
    {
        private readonly Dictionary<string, IModeResourceProvider> _byKey;

        public Machine(string document, bool installApplications, bool lighting)
        {
            var screens = new FakeDisplayDevices()
                .Add(new FakeMonitor { Path = Desk1, Name = "DELL U2720Q", Active = true })
                .Add(new FakeMonitor { Path = Desk2, Name = "DELL U2720Q", Active = true })
                .Add(new FakeMonitor { Path = Tv, Name = "LG TV SSCR2" });

            var sound = new FakeAudioEndpoints(
                "Realtek HD Audio 2nd output (Realtek(R) Audio)",
                "4 - LG TV SSCR2 (AMD High Definition Audio Device)");

            var lights = new FakeOpenRgbClient();

            if (!lighting)
            {
                lights.ProbeException = new InvalidOperationException("OpenRGB SDK server unavailable.");
            }

            var desktop = new FakeProcessHost();

            if (installApplications)
            {
                desktop
                    .Handles("steam")
                    .Installed(
                        Environment.ExpandEnvironmentVariables(
                            @"%ProgramFiles(x86)%\Steam\steamapps\common\Lossless Scaling\LosslessScaling.exe"),
                        Environment.ExpandEnvironmentVariables(
                            @"%LOCALAPPDATA%\Programs\Stremio\stremio-shell-ng.exe"),
                        Environment.ExpandEnvironmentVariables(
                            @"%LOCALAPPDATA%\Programs\youtube-tv\YouTube TV.exe"));
            }

            var television = new LgTvConnection(new FakeTelevision(), new FakeCredentials());

            List<IModeResourceProvider> providers =
            [
                new WindowsThemeProvider(new RememberedTheme()),
                new TvPowerProvider(television),
                new TvInputProvider(television),
                new TvSignalProvider(television),
                new DisplayProvider(screens),
                new TvProfileProvider(television),
                new CursorProvider(new FakeCursor(), new FakeDisplay(new DisplayRect(0, 0, 1920, 1080))),
                new AudioProvider(sound),
                new DoNotDisturbProvider(new UnwritableNotifications()),
                new CaseRgbProvider(lights),
            ];

            // The same call the shell makes, from the same document, so the
            // application resources are the ones the product would have.
            providers.AddRange(EngineBootstrap.ApplicationProviders(document, desktop, providers));

            Providers = providers;
            Configuration = ModeConfiguration.Parse(document, providers);

            _byKey = providers.ToDictionary(provider => provider.Key, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<IModeResourceProvider> Providers { get; }

        public ModeConfiguration Configuration { get; }

        public ApplicationDefinition Application(string name) =>
            Providers.OfType<ApplicationProvider>()
                .Single(provider => string.Equals(provider.Key, name, StringComparison.OrdinalIgnoreCase))
                .Definition;

        public TransitionPlan Plan(string mode)
        {
            Assert.True(TryPlan(mode, out TransitionPlan? plan, out string? problem), problem);
            return plan!;
        }

        public bool TryPlan(string mode, out TransitionPlan? plan, out string? problem)
        {
            ConfiguredMode? configured = Configuration.Find(mode);

            Assert.NotNull(configured);

            return TransitionPlan.TryCreate(configured!, _byKey, out plan, out problem);
        }
    }

    /// <summary>A theme that lives in a field.</summary>
    private sealed class RememberedTheme : IThemeStore
    {
        private bool _light = true;

        public bool? ReadUsesLightTheme() => _light;

        public void WriteUsesLightTheme(bool light) => _light = light;
    }

    /// <summary>
    /// A notification setting that answers and is never written.
    /// </summary>
    /// <remarks>
    /// Planning never writes anything, so there is nothing here to stop -- but a
    /// fake that would throw if it were written is how that stays true.
    /// </remarks>
    private sealed class UnwritableNotifications : INotificationSetting
    {
        public bool? Allowed { get; init; } = true;

        public bool? ToastsAllowed() => Allowed;

        public void AllowToasts(bool allowed) =>
            throw new InvalidOperationException("Planning must not write anything.");

        public void Refresh() => throw new InvalidOperationException("Planning must not write anything.");
    }
}

/// <summary>The target keys, in the order the document wrote them.</summary>
internal static class ModeConfigurationNames
{
    public static IReadOnlyList<string> TargetKeys(this ModeConfiguration configuration) =>
        [.. configuration.Modes.Select(mode => mode.Name)];
}

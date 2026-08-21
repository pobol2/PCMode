using System.Text.Json;

namespace PCMode.Engine.Tests;

/// <summary>
/// What an application resource does, and above all what it declines to do.
/// </summary>
public sealed class ApplicationProviderTests
{
    /// <summary>Builds one application from the settings a document would carry.</summary>
    private static (ApplicationProvider Provider, FakeProcessHost Desktop) Application(
        string name,
        string entry,
        FakeProcessHost? desktop = null)
    {
        FakeProcessHost host = desktop ?? new FakeProcessHost();

        var provider = new ApplicationProvider(
            new ApplicationDefinition
            {
                Name = name,
                ProcessName = name,
                Start = new LaunchCommand { Executable = name },
            },
            host);

        using JsonDocument document = JsonDocument.Parse($$"""{ "{{name}}": {{entry}} }""");

        Assert.True(provider.TryConfigure(document.RootElement.Clone(), out string? problem), problem);

        return (provider, host);
    }

    private static IReadOnlySet<string> Changed(params string[] keys) =>
        new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);

    // ---- reading ----------------------------------------------------------

    [Fact]
    public void Reports_whether_the_application_is_running()
    {
        (ApplicationProvider steam, FakeProcessHost desktop) = Application(
            "Steam", """{ "process": "steam", "start": "steam://open/bigpicture" }""");

        Assert.Equal("Stopped", steam.Read());

        desktop.AlreadyRunning("steam");

        Assert.Equal("Running", steam.Read());
    }

    [Fact]
    public void Answers_nothing_rather_than_Stopped_when_it_could_not_look()
    {
        // 'Stopped' would be a claim about the machine that nothing checked, and
        // acting on it means starting a second copy of whatever is already open.
        (ApplicationProvider steam, FakeProcessHost desktop) = Application(
            "Steam", """{ "process": "steam", "start": "steam://open/bigpicture" }""");

        desktop.LookingFails = new InvalidOperationException("the process list could not be read.");

        Assert.Null(steam.Read());
    }

    // ---- starting ---------------------------------------------------------

    [Fact]
    public void Does_not_start_a_second_copy_of_something_already_running()
    {
        (ApplicationProvider player, FakeProcessHost desktop) = Application(
            "Player", """{ "process": "player", "start": "C:\\player.exe" }""",
            new FakeProcessHost().AlreadyRunning("player"));

        player.Apply("Running", CancellationToken.None);

        Assert.Empty(desktop.Started);
    }

    [Fact]
    public void Reissues_the_command_for_an_application_that_asked_to_be_reactivated()
    {
        // Steam is running whether or not it is in Big Picture, so "it is already
        // running" is true and is not a reason to skip. The flag that makes the
        // engine dispatch anyway is on the policy; this is the other half of it.
        (ApplicationProvider steam, FakeProcessHost desktop) = Application(
            "Steam", """{ "process": "steam", "start": "steam://open/bigpicture", "reactivate": true }""",
            new FakeProcessHost().AlreadyRunning("steam"));

        Assert.True(steam.PolicyFor("Running").DispatchAtLeastOnce);

        steam.Apply("Running", CancellationToken.None);

        Assert.Equal(["steam://open/bigpicture"], desktop.Started.Select(command => command.Describe()));
    }

    [Fact]
    public void Never_reissues_a_stop_command_toward_an_application_that_is_not_running()
    {
        // Sending steam://exit at a Steam that is not running makes Windows start
        // it purely to hand it the URI, which it reads as "exit": the application
        // visibly opens and shuts for no reason anybody watching could explain.
        (ApplicationProvider steam, FakeProcessHost desktop) = Application(
            "Steam",
            """{ "process": "steam", "start": "steam://open/bigpicture", "stop": "steam://exit", "reactivate": true }""");

        Assert.False(steam.PolicyFor("Stopped").DispatchAtLeastOnce);

        steam.Apply("Stopped", CancellationToken.None);

        Assert.Empty(desktop.Started);
    }

    // ---- stopping ---------------------------------------------------------

    [Fact]
    public void Asks_an_application_to_close_when_it_was_told_how()
    {
        (ApplicationProvider steam, FakeProcessHost desktop) = Application(
            "Steam",
            """{ "process": "steam", "start": "steam://open/bigpicture", "stop": "steam://exit" }""",
            new FakeProcessHost().AlreadyRunning("steam").Closing("steam://exit", "steam"));

        steam.Apply("Stopped", CancellationToken.None);

        Assert.Equal(["start steam://exit"], desktop.Did);
        Assert.Equal("Stopped", steam.Read());
    }

    [Fact]
    public void Ends_an_application_that_offers_no_way_to_be_asked()
    {
        (ApplicationProvider player, FakeProcessHost desktop) = Application(
            "Player", """{ "process": "player", "start": "C:\\player.exe" }""",
            new FakeProcessHost().AlreadyRunning("player"));

        player.Apply("Stopped", CancellationToken.None);

        Assert.Equal(["kill player"], desktop.Did);
    }

    // ---- staleness --------------------------------------------------------

    [Fact]
    public void A_running_application_is_stale_once_what_it_opened_against_has_moved()
    {
        (ApplicationProvider player, FakeProcessHost desktop) = Application(
            "Player",
            """{ "process": "player", "start": "C:\\player.exe", "restartWhen": [ "display" ] }""",
            new FakeProcessHost().AlreadyRunning("player").Launching("C:\\player.exe", "player"));

        // Nothing has moved yet, so a running copy is exactly what was asked for.
        player.Preceded(Changed());
        Assert.Equal("Running", player.Read());

        // The display changed while it was open, so its window is on a screen
        // that is no longer there. Nothing about the process says so, which is
        // why the answer is "cannot say" rather than "stopped".
        player.Preceded(Changed("display"));
        Assert.Null(player.Read());

        player.Apply("Running", CancellationToken.None);

        Assert.Equal(
            ["kill player", "wait for player to stop", "start C:\\player.exe"],
            desktop.Did);

        // And it is fresh again immediately -- otherwise the readback would find
        // it stale all over again, because the display did still change, and the
        // resource would restart it until it ran out of attempts.
        Assert.Equal("Running", player.Read());
    }

    [Fact]
    public void A_resource_that_was_already_correct_does_not_make_anything_stale()
    {
        // Re-asserting a mode must not restart every application in it. Only a
        // resource that actually moved is in the set the engine reports.
        (ApplicationProvider player, FakeProcessHost desktop) = Application(
            "Player",
            """{ "process": "player", "start": "C:\\player.exe", "restartWhen": [ "display" ] }""",
            new FakeProcessHost().AlreadyRunning("player"));

        player.Preceded(Changed("audio", "theme"));

        Assert.Equal("Running", player.Read());

        player.Apply("Running", CancellationToken.None);

        Assert.Empty(desktop.Did);
    }

    // ---- readiness --------------------------------------------------------

    [Fact]
    public void Acts_on_a_new_window_only_after_a_start_it_performed()
    {
        (ApplicationProvider player, FakeProcessHost desktop) = Application(
            "Player",
            """
            {
              "process": "player",
              "start": "C:\\player.exe",
              "ready": { "activate": true, "keys": "f" }
            }
            """,
            new FakeProcessHost().Launching("C:\\player.exe", "player"));

        player.Apply("Running", CancellationToken.None);

        Assert.Equal(
            ["start C:\\player.exe", "wait for player's window", "activate player", "type 'f' at player"],
            desktop.Did);
    }

    [Fact]
    public void Leaves_an_application_that_was_already_running_alone()
    {
        // The readiness action belongs to a start. Pressing a full-screen key at
        // a player that is already full screen takes it back out again, which is
        // what happens on every re-assert if this is not true.
        (ApplicationProvider player, FakeProcessHost desktop) = Application(
            "Player",
            """
            {
              "process": "player",
              "start": "C:\\player.exe",
              "ready": { "activate": true, "keys": "f" }
            }
            """,
            new FakeProcessHost().AlreadyRunning("player"));

        player.Apply("Running", CancellationToken.None);

        Assert.Empty(desktop.Typed);
    }

    [Fact]
    public void Types_nothing_when_the_window_would_not_come_forward()
    {
        // Windows refuses to move the foreground for a process the user is not
        // interacting with. A keystroke sent anyway lands in whatever they were
        // doing instead.
        (ApplicationProvider player, FakeProcessHost desktop) = Application(
            "Player",
            """
            {
              "process": "player",
              "start": "C:\\player.exe",
              "ready": { "activate": true, "keys": "f" }
            }
            """,
            new FakeProcessHost().Launching("C:\\player.exe", "player"));

        desktop.ActivationWorks = false;

        player.Apply("Running", CancellationToken.None);

        Assert.Empty(desktop.Typed);
    }

    [Fact]
    public void A_window_that_never_appears_does_not_fail_the_launch()
    {
        // The readback decides whether the application started. Failing the whole
        // resource because a keystroke had nowhere to go would report a launch
        // that worked as a launch that did not.
        (ApplicationProvider player, FakeProcessHost desktop) = Application(
            "Player",
            """
            {
              "process": "player",
              "start": "C:\\player.exe",
              "ready": { "keys": "f" }
            }
            """,
            new FakeProcessHost().Launching("C:\\player.exe", "player"));

        desktop.WindowAppears = false;

        player.Apply("Running", CancellationToken.None);

        Assert.Empty(desktop.Typed);
        Assert.Equal("Running", player.Read());
    }

    // ---- dependencies and availability ------------------------------------

    [Fact]
    public void Needs_things_to_start_and_nothing_at_all_to_stop()
    {
        // A mode that dragged a television awake in order to shut something down
        // would be doing the opposite of what it was asked.
        (ApplicationProvider player, _) = Application(
            "Player",
            """{ "process": "player", "start": "C:\\player.exe", "requires": { "display": "TVOnly" } }""");

        Assert.Equal(
            [new ResourceRequirement("display", "TVOnly")],
            player.RequirementsFor("Running"));

        Assert.Empty(player.RequirementsFor("Stopped"));
    }

    [Fact]
    public void An_application_that_is_not_installed_can_still_be_stopped()
    {
        // It is already stopped, so asking for that costs nothing -- and reporting
        // it unavailable would make every mode that tidies up after itself refuse
        // to run on a machine with one fewer program.
        (ApplicationProvider player, _) = Application(
            "Player", """{ "process": "player", "start": "C:\\player.exe" }""");

        Assert.Equal(AvailabilityState.Unavailable, player.AvailabilityOf("Running").State);
        Assert.Equal(AvailabilityState.Available, player.AvailabilityOf("Stopped").State);
    }

    [Fact]
    public void A_uri_is_available_only_when_something_here_opens_it()
    {
        // Without this a missing URI-launched application is indistinguishable
        // from a slow one: the launch appears to succeed, nothing ever arrives,
        // and the mode fails on a timeout rather than saying Steam is not here.
        (ApplicationProvider missing, _) = Application(
            "Steam", """{ "process": "steam", "start": "steam://open/bigpicture" }""");

        Assert.Equal(AvailabilityState.Unavailable, missing.AvailabilityOf("Running").State);

        (ApplicationProvider installed, _) = Application(
            "Steam", """{ "process": "steam", "start": "steam://open/bigpicture" }""",
            new FakeProcessHost().Handles("steam"));

        Assert.Equal(AvailabilityState.Available, installed.AvailabilityOf("Running").State);
    }

    [Fact]
    public void A_lookup_that_failed_has_not_established_that_anything_is_missing()
    {
        (ApplicationProvider player, FakeProcessHost desktop) = Application(
            "Player", """{ "process": "player", "start": "C:\\player.exe" }""");

        desktop.Files.Clear();

        var angry = new ApplicationProvider(player.Definition, new AngryHost());

        Assert.Equal(AvailabilityState.Unknown, angry.AvailabilityOf("Running").State);
    }

    // ---- values -----------------------------------------------------------

    [Theory]
    [InlineData("Running")]
    [InlineData("stopped")]
    public void Takes_running_and_stopped_in_any_case(string value)
    {
        (ApplicationProvider player, _) = Application(
            "Player", """{ "process": "player", "start": "C:\\player.exe" }""");

        Assert.True(player.IsValidValue(value, out _));
    }

    [Fact]
    public void Refuses_a_near_miss_rather_than_guessing_which_it_meant()
    {
        // 'Runnning' falling through to the stop case closes somebody's game, and
        // nothing about the failure would connect it to the typo.
        (ApplicationProvider player, FakeProcessHost desktop) = Application(
            "Player", """{ "process": "player", "start": "C:\\player.exe" }""",
            new FakeProcessHost().AlreadyRunning("player"));

        Assert.False(player.IsValidValue("Runnning", out string? problem));
        Assert.Contains("Running", problem);

        Assert.Throws<ArgumentException>(() => player.Apply("Runnning", CancellationToken.None));
        Assert.Empty(desktop.Did);
    }

    // ---- launch-only --------------------------------------------------------

    [Fact]
    public void A_launch_only_application_is_dispatched_once_rather_than_polled()
    {
        (ApplicationProvider steam, _) = Application(
            "Steam", """{ "start": "steam://open/bigpicture" }""");

        Assert.False(steam.Definition.CanObserve);
        Assert.Equal(ConvergencePolicy.DispatchOnce, steam.PolicyFor("Running"));
    }

    [Fact]
    public void A_launch_only_application_never_claims_a_readback_state()
    {
        (ApplicationProvider steam, FakeProcessHost desktop) = Application(
            "Steam", """{ "start": "steam://open/bigpicture" }""",
            new FakeProcessHost().AlreadyRunning("steam"));

        // Even though the host would say it is running, nothing was configured
        // that PCMode trusts as this application's identity.
        Assert.Null(steam.Read());
    }

    [Fact]
    public void A_launch_only_application_dispatches_the_start_command_and_nothing_more()
    {
        (ApplicationProvider steam, FakeProcessHost desktop) = Application(
            "Steam", """{ "start": "steam://open/bigpicture" }""");

        steam.Apply("Running", CancellationToken.None);

        Assert.Equal(["steam://open/bigpicture"], desktop.Started.Select(command => command.Describe()));
    }

    [Fact]
    public void A_launch_only_application_cannot_be_stopped()
    {
        (ApplicationProvider steam, FakeProcessHost desktop) = Application(
            "Steam", """{ "start": "steam://open/bigpicture" }""");

        Assert.False(steam.IsValidValue("Stopped", out string? problem));
        Assert.Contains("launch-only", problem, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<InvalidOperationException>(() => steam.Apply("Stopped", CancellationToken.None));
        Assert.Empty(desktop.Did);
    }

    /// <summary>A machine where nothing can be looked up at all.</summary>
    private sealed class AngryHost : IProcessHost
    {
        public bool IsRunning(string processName) => throw new InvalidOperationException("no.");

        public void Start(LaunchCommand command) => throw new InvalidOperationException("no.");

        public void Kill(string processName) => throw new InvalidOperationException("no.");

        public bool WaitUntilStopped(string processName, TimeSpan within, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no.");

        public bool WaitForWindow(string processName, TimeSpan within, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no.");

        public bool Activate(string processName) => throw new InvalidOperationException("no.");

        public void SendKeys(string processName, string keys) => throw new InvalidOperationException("no.");

        public bool FileExists(string path) => throw new InvalidOperationException("the disk is asleep.");

        public bool HandlesUriScheme(string scheme) => throw new InvalidOperationException("the disk is asleep.");
    }
}

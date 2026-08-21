using System.Text.Json;

namespace PCMode.Engine.Tests;

/// <summary>
/// Party mode: two outputs, one runtime, and nothing silently rerouted.
/// </summary>
/// <remarks>
/// Everything here stops at the seam. <see cref="FakeAudioMirrorFactory"/> stands
/// in for the capture and render loops, so these tests decide what Party mode
/// does and never what it sounds like -- which is the correct division, because
/// the second question cannot be answered without two real endpoints and is what
/// the hardware check in <see cref="PartyHardwareTest"/> exists for.
/// </remarks>
public sealed class PartyAudioTests
{
    private const string SpeakerEndpoint = "Desk speakers (Realtek(R) Audio)";
    private const string SoundbarEndpoint = "LG Soundbar (Intel Display Audio)";

    // ---- the working path ---------------------------------------------------

    [Fact]
    public async Task Makes_the_reference_the_default_and_mirrors_to_the_other()
    {
        using var root = new TemporaryRoot();

        var endpoints = Endpoints();
        var mirrors = new FakeAudioMirrorFactory();

        TransitionOutcome outcome = await Engine(root, endpoints, mirrors).ApplyAsync("Party");

        Assert.True(outcome.Succeeded, outcome.Message);

        // The soundbar is what Windows plays through, because that is what
        // loopback capture reads. Nothing was pointed at the speakers.
        Assert.Equal([endpoints.IdOf(1)], endpoints.MadeDefault);

        FakeAudioMirror mirror = Assert.Single(mirrors.Live);

        Assert.Equal(endpoints.IdOf(1), mirror.Request.ReferenceId);
        Assert.Equal(endpoints.IdOf(0), mirror.Request.MirrorId);
        Assert.Equal(TimeSpan.FromMilliseconds(60), mirror.Request.Delay);
        Assert.Equal(0.8, mirror.Request.Gain);

        // The configured endpoint volumes, on the endpoints themselves. The
        // reference asked for none, so its own was left where the user had it.
        Assert.Equal(0.4, endpoints.Volumes[endpoints.IdOf(0)]);
        Assert.DoesNotContain(endpoints.IdOf(1), endpoints.Volumes.Keys);

        Assert.True(outcome.StateRecorded);
    }

    [Fact]
    public void Reads_as_the_group_only_while_the_group_is_actually_running()
    {
        // The readback that keeps a dead runtime from being mistaken for a live
        // one -- including across a restart, where there is no runtime at all and
        // the soundbar is still the Windows default from last time.
        var endpoints = Endpoints();
        var mirrors = new FakeAudioMirrorFactory();

        AudioProvider provider = Provider(endpoints, mirrors, out PartyAudioHost host);

        endpoints.Default = endpoints.IdOf(1);
        Assert.Equal("Soundbar", provider.Read());

        provider.Apply("Party", CancellationToken.None);
        Assert.Equal("Party", provider.Read());

        // Something else moved the Windows default. The mirror is still running
        // and still faithfully copying an endpoint nothing plays to, which is
        // not the group and must not read as it.
        endpoints.Default = endpoints.IdOf(0);
        Assert.Equal("Speakers", provider.Read());

        // Applying it again puts the reference back rather than assuming that a
        // running mirror means there is nothing to do.
        provider.Apply("Party", CancellationToken.None);
        Assert.Equal("Party", provider.Read());
        Assert.Single(mirrors.Started);

        // A fresh process, the same machine: the default is still the soundbar
        // and nothing is mirroring.
        AudioProvider restarted = Provider(endpoints, new FakeAudioMirrorFactory(), out PartyAudioHost _);
        Assert.Equal("Soundbar", restarted.Read());

        host.Dispose();
    }

    [Fact]
    public void Needs_whatever_both_of_its_members_need()
    {
        // A group with the television in it depends on the television's display
        // exactly as the television's own alias does, and neither member's
        // requirement may be dropped.
        var provider = new AudioProvider(Endpoints(), new PartyAudioHost(new FakeAudioMirrorFactory()));

        Assert.Null(Configure(
            provider,
            $$$"""
            {"devices":{
               "Speakers":"{{{SpeakerEndpoint}}}",
               "Soundbar":{"device":"{{{SoundbarEndpoint}}}","requires":{"display":"TVOnly"} } },
             "party":{"reference":"Soundbar","mirror":"Speakers"} }
            """));

        ResourceRequirement needed = Assert.Single(provider.RequirementsFor("Party"));

        Assert.Equal("display", needed.Key);
        Assert.Equal("TVOnly", needed.Target);
    }

    // ---- configuration that will not do -------------------------------------

    [Theory]
    [InlineData("""{"reference":"Soundbar"}""", "mirror")]
    [InlineData("""{"reference":"Soundbar","mirror":"Kitchen"}""", "Kitchen")]
    [InlineData("""{"reference":"Soundbar","mirror":"Soundbar"}""", "both")]
    [InlineData("""{"reference":"Soundbar","mirror":"Speakers","delayMs":9000}""", "delayMs")]
    [InlineData("""{"reference":"Soundbar","mirror":"Speakers","gain":11}""", "gain")]
    [InlineData("""{"reference":"Soundbar","mirror":"Speakers","mirrorVolume":4}""", "mirrorVolume")]
    [InlineData("""{"reference":"Soundbar","mirror":"Speakers","loudness":"max"}""", "loudness")]
    [InlineData("""{"reference":"Soundbar","mirror":"Speakers","name":"Speakers"}""", "name")]
    [InlineData("""["Soundbar","Speakers"]""", "object")]
    public void Refuses_a_party_group_that_could_not_work(string party, string mentions)
    {
        // Refused when the document is read, by name, rather than at the moment
        // somebody selects it: a group that validates and then cannot start is
        // one that fails in front of a room.
        string? problem = Configure(
            new AudioProvider(Endpoints(), new PartyAudioHost(new FakeAudioMirrorFactory())),
            $$"""
            {"devices":{"Speakers":"{{SpeakerEndpoint}}","Soundbar":"{{SoundbarEndpoint}}"},
             "party":{{party}}}
            """);

        Assert.NotNull(problem);
        Assert.Contains(mentions, problem, StringComparison.OrdinalIgnoreCase);
    }

    // ---- one runtime, however many times it is asked for --------------------

    [Fact]
    public async Task Entering_the_party_mode_twice_leaves_one_runtime()
    {
        // Two capture loops on one endpoint is the same audio arriving twice,
        // slightly apart, which sounds like broken hardware rather than like a
        // mistake in a mode.
        using var root = new TemporaryRoot();

        var endpoints = Endpoints();
        var mirrors = new FakeAudioMirrorFactory();
        ModeEngine engine = Engine(root, endpoints, mirrors);

        Assert.True((await engine.ApplyAsync("Party")).Succeeded);
        Assert.True((await engine.ApplyAsync("Party")).Succeeded);

        Assert.Single(mirrors.Started);
        Assert.Single(mirrors.Live);

        // The second pass found it already running, so nothing was commanded
        // again -- not the mirror, and not the Windows default either.
        Assert.Single(endpoints.MadeDefault);
    }

    [Fact]
    public void Adjusts_the_running_pair_instead_of_restarting_it()
    {
        // What a live delay change has to be: the two speakers are aligned by
        // ear while something is playing, and a restart would silence the thing
        // being listened to at the moment it needs to be heard.
        var endpoints = Endpoints();
        var mirrors = new FakeAudioMirrorFactory();

        AudioProvider provider = Provider(endpoints, mirrors, out PartyAudioHost host);

        provider.Apply("Party", CancellationToken.None);
        Assert.True(provider.AdjustParty(TimeSpan.FromMilliseconds(95), 0.6));

        FakeAudioMirror mirror = Assert.Single(mirrors.Started);

        Assert.False(mirror.Disposed);
        Assert.Equal((TimeSpan.FromMilliseconds(95), 0.6), Assert.Single(mirror.Adjustments));

        host.Dispose();
    }

    // ---- stopping and cleaning up -------------------------------------------

    [Fact]
    public async Task Choosing_a_single_output_stops_the_mirror_first()
    {
        using var root = new TemporaryRoot();

        var endpoints = Endpoints();
        var mirrors = new FakeAudioMirrorFactory();
        ModeEngine engine = Engine(root, endpoints, mirrors);

        Assert.True((await engine.ApplyAsync("Party")).Succeeded);
        Assert.True((await engine.ApplyAsync("Desk")).Succeeded);

        // Nothing is left capturing the soundbar, and the sound is on the
        // speakers alone.
        Assert.Empty(mirrors.Live);
        Assert.True(Assert.Single(mirrors.Started).Disposed);
        Assert.Equal(endpoints.IdOf(0), endpoints.Default);
    }

    [Fact]
    public void Stops_the_mirror_when_the_shell_exits()
    {
        // The host is what the shell disposes on the way out. A mirror that
        // outlived the process would be audio being duplicated by a program the
        // user has just closed.
        var endpoints = Endpoints();
        var mirrors = new FakeAudioMirrorFactory();

        AudioProvider provider = Provider(endpoints, mirrors, out PartyAudioHost host);

        provider.Apply("Party", CancellationToken.None);
        Assert.Single(mirrors.Live);

        host.Dispose();

        Assert.Empty(mirrors.Live);

        // The reference endpoint is still the Windows default -- stopping a
        // mirror is not a reason to move the sound somewhere else -- but the
        // machine is no longer in the group, and says so.
        Assert.Equal("Soundbar", provider.Read());
    }

    // ---- an endpoint that is not there, or does not stay ---------------------

    [Fact]
    public async Task Refuses_to_start_when_a_member_is_not_plugged_in()
    {
        // The soundbar is switched off, so its endpoint does not exist. Nothing
        // starts, nothing is pointed anywhere else, and the message says which
        // device and what IS there.
        using var root = new TemporaryRoot();

        var endpoints = new FakeAudioEndpoints(SpeakerEndpoint);
        var mirrors = new FakeAudioMirrorFactory();

        TransitionOutcome outcome = await Engine(root, endpoints, mirrors).ApplyAsync("Party");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Contains(SoundbarEndpoint, outcome.Message, StringComparison.Ordinal);

        Assert.Empty(mirrors.Started);
        Assert.Empty(endpoints.MadeDefault);
        Assert.False(outcome.StateRecorded);
    }

    [Fact]
    public async Task Refuses_to_mirror_when_the_reference_did_not_become_the_default()
    {
        // Changing the default is the one thing PCMode does through an interface
        // Windows does not document. A group built on a default that did not
        // take is two outputs one of which plays nothing.
        using var root = new TemporaryRoot();

        var endpoints = Endpoints();
        endpoints.IgnoresMakeDefault = true;
        endpoints.Default = endpoints.IdOf(0);

        var mirrors = new FakeAudioMirrorFactory();

        TransitionOutcome outcome = await Engine(root, endpoints, mirrors).ApplyAsync("Party");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Contains("did not accept 'Soundbar'", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(mirrors.Started);
    }

    [Fact]
    public void Reports_a_mirror_that_died_as_failed_rather_than_as_stopped()
    {
        // An endpoint unplugged mid-session. There is no reconnection and no
        // second choice of device: the group stops being the answer to "what is
        // the audio doing", and says why.
        var endpoints = Endpoints();
        var mirrors = new FakeAudioMirrorFactory();

        AudioProvider provider = Provider(endpoints, mirrors, out PartyAudioHost host);

        provider.Apply("Party", CancellationToken.None);

        Assert.Equal(PartyState.Running, provider.PartyStatus()!.State);

        Assert.Single(mirrors.Started).Failure = "'Soundbar' is no longer available. It was unplugged.";

        AudioPartyStatus status = provider.PartyStatus()!;

        Assert.Equal(PartyState.Failed, status.State);
        Assert.Contains("unplugged", status.Problem!, StringComparison.Ordinal);
        Assert.Contains("unplugged", status.Health!.LastFailure!, StringComparison.Ordinal);

        // And it is no longer the current audio target, so the next transition
        // applies the mode rather than believing it is already in it.
        Assert.NotEqual("Party", provider.Read());

        host.Dispose();
    }

    [Fact]
    public async Task Reports_a_mirror_that_will_not_start_and_changes_nothing_else()
    {
        using var root = new TemporaryRoot();

        var endpoints = Endpoints();

        var mirrors = new FakeAudioMirrorFactory
        {
            Fails = new InvalidOperationException(
                "'Speakers' is held in exclusive mode by another program, so Party mode cannot share it."),
        };

        TransitionOutcome outcome = await Engine(root, endpoints, mirrors).ApplyAsync("Party");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Contains("exclusive mode", outcome.Message, StringComparison.Ordinal);
        Assert.False(outcome.StateRecorded);
    }

    [Fact]
    public void Rolls_back_the_default_device_and_both_volumes_when_the_mirror_fails_to_start()
    {
        // Everything Party had already changed before the mirror itself failed
        // to start -- which device is default, and both endpoints' volumes --
        // is exactly what a failed attempt must not leave behind.
        var endpoints = Endpoints();
        endpoints.Default = endpoints.IdOf(0);
        endpoints.Volumes[endpoints.IdOf(0)] = 0.90;
        endpoints.Volumes[endpoints.IdOf(1)] = 0.20;

        var mirrors = new FakeAudioMirrorFactory
        {
            Fails = new InvalidOperationException("'Speakers' is held in exclusive mode by another program."),
        };

        var provider = new AudioProvider(endpoints, new PartyAudioHost(mirrors));

        Assert.Null(Configure(
            provider,
            $$"""
            {
              "devices": { "Speakers": "{{SpeakerEndpoint}}", "Soundbar": "{{SoundbarEndpoint}}" },
              "party": {
                "outputs": ["Speakers", "Soundbar"],
                "delay": { "output": "Speakers", "ms": 60 },
                "volumes": { "Speakers": 0.35, "Soundbar": 0.55 }
              }
            }
            """));

        Assert.Throws<InvalidOperationException>(() => provider.Apply("Party", CancellationToken.None));

        Assert.Equal(endpoints.IdOf(0), endpoints.Default);
        Assert.Equal(0.90, endpoints.Volumes[endpoints.IdOf(0)]);
        Assert.Equal(0.20, endpoints.Volumes[endpoints.IdOf(1)]);
    }

    // ---- the single-output modes still work exactly as they did -------------

    [Fact]
    public async Task Leaves_a_configuration_without_a_party_group_alone()
    {
        using var root = new TemporaryRoot();

        root.Write(
            $$"""
            {
              "schemaVersion": 1,
              "providers": { "audio": { "devices": {
                "Speakers": "{{SpeakerEndpoint}}", "Soundbar": "{{SoundbarEndpoint}}" } } },
              "spaces": [ { "id": "space", "name": "Space", "activities": [
                { "id": "desk", "name": "Desk", "resources": { "audio": "Speakers" } }
              ] } ]
            }
            """);

        var endpoints = Endpoints();
        endpoints.Default = endpoints.IdOf(1);

        var provider = new AudioProvider(endpoints, new PartyAudioHost(new FakeAudioMirrorFactory()));
        IReadOnlyList<IModeResourceProvider> providers = [provider];

        var engine = new ModeEngine(
            ModeConfiguration.LoadOrCreate(root.Path, providers),
            new ModeStateStore(root.StateFile),
            providers,
            new FakeTime());

        Assert.True((await engine.ApplyAsync("Desk")).Succeeded);
        Assert.Equal([endpoints.IdOf(0)], endpoints.MadeDefault);

        // No group, so no group name to select and nothing claiming there is one.
        Assert.Null(provider.Party);
        Assert.Null(provider.PartyStatus());
        Assert.False(provider.IsValidValue("Party", out string? problem));
        Assert.Contains("Speakers", problem!, StringComparison.Ordinal);
    }

    // ---- helpers ------------------------------------------------------------

    private static readonly string Settings =
        $$"""
        {
          "devices": { "Speakers": "{{SpeakerEndpoint}}", "Soundbar": "{{SoundbarEndpoint}}" },
          "party": {
            "reference": "Soundbar", "mirror": "Speakers",
            "delayMs": 60, "gain": 0.8, "mirrorVolume": 0.4
          }
        }
        """;

    private static readonly string Document =
        $$"""
        {
          "schemaVersion": 1,
          "providers": { "audio": {{Settings}} },
          "spaces": [
            { "id": "space", "name": "Space", "activities": [
              { "id": "party", "name": "Party", "resources": { "audio": "Party" } },
              { "id": "desk", "name": "Desk", "resources": { "audio": "Speakers" } }
            ] }
          ]
        }
        """;

    /// <summary>The speakers first, then the soundbar.</summary>
    private static FakeAudioEndpoints Endpoints() => new(SpeakerEndpoint, SoundbarEndpoint);

    private static ModeEngine Engine(TemporaryRoot root, FakeAudioEndpoints endpoints, FakeAudioMirrorFactory mirrors)
    {
        root.Write(Document);

        IReadOnlyList<IModeResourceProvider> providers =
            [new AudioProvider(endpoints, new PartyAudioHost(mirrors))];

        // A fake clock: this provider retries six times over twelve seconds when
        // a device is missing, and these tests are about what it concludes.
        return new ModeEngine(
            ModeConfiguration.LoadOrCreate(root.Path, providers),
            new ModeStateStore(root.StateFile),
            providers,
            new FakeTime());
    }

    private static AudioProvider Provider(
        FakeAudioEndpoints endpoints,
        FakeAudioMirrorFactory mirrors,
        out PartyAudioHost host)
    {
        host = new PartyAudioHost(mirrors);

        var provider = new AudioProvider(endpoints, host);

        Assert.Null(Configure(provider, Settings));

        return provider;
    }

    private static string? Configure(AudioProvider provider, string settings)
    {
        using JsonDocument document = JsonDocument.Parse(settings);

        bool accepted = provider.TryConfigure(document.RootElement, out string? problem);

        Assert.Equal(accepted, problem is null);

        return problem;
    }
}

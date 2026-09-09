using System.Text.Json;
using PCMode.Audio.Core.Models;

namespace PCMode.Engine.Tests;

/// <summary>
/// What an Audio Target does to the machine, with a made-up sound card and a
/// made-up audio Host.
/// </summary>
/// <remarks>
/// The whole of the desired-state half of audio: which endpoints a target
/// resolves to, what order it changes things in, what it puts back when it
/// cannot finish, when it is allowed to conclude that a target is playing, and
/// what it refuses to guess. Nothing here opens an endpoint or starts a process.
/// </remarks>
public sealed class AudioTargetTests
{
    private const string Speakers = "Desk speakers";
    private const string Television = "LG TV (NVIDIA High Definition Audio)";
    private const string VirtualEndpoint = "PCMode Audio (PCMode Virtual Audio Endpoint)";

    private const string Settings = $$"""
        {
          "devices": { "Headphones": "{{Speakers}}", "TV": "{{Television}}" },
          "targets": {
            "Party": { "outputs": [
              { "output": "TV" },
              { "output": "Headphones", "delayMs": 68, "gain": 0.8, "volume": 0.4 }
            ] }
          }
        }
        """;

    // ---- configuration ------------------------------------------------------

    [Fact]
    public void Offers_a_target_beside_the_devices_as_one_value_of_one_resource()
    {
        // What makes a target and a single output mutually exclusive: a mode
        // names one audio value, and both kinds are values of it.
        AudioProvider provider = Provider(Endpoints(), out _);

        Assert.Equal(["Headphones", "Party", "TV"], provider.SelectableNames);
        Assert.Equal(["Headphones", "TV"], provider.DeviceNames);
        Assert.True(provider.IsValidValue("Party", out _));
    }

    [Fact]
    public void Reads_every_route_setting_it_was_given()
    {
        AudioProvider provider = Provider(Endpoints(), out _);

        AudioTargetSettings target = Assert.Single(provider.Targets);

        Assert.Equal("Party", target.Name);
        Assert.Equal(["TV", "Headphones"], target.Outputs);

        AudioRouteSettings reference = target.Routes[0];
        Assert.Equal(0, reference.DelayMs);
        Assert.Equal(1.0, reference.Gain);
        Assert.Null(reference.Volume);
        Assert.True(reference.Required);

        AudioRouteSettings delayed = target.Routes[1];
        Assert.Equal(68, delayed.DelayMs);
        Assert.Equal(0.8, delayed.Gain);
        Assert.Equal(0.4, delayed.Volume);
        Assert.True(delayed.Required);
    }

    [Theory]
    [InlineData("""{"devices":{"A":"x"},"targets":{"T":{"outputs":[{"output":"B"}]}}}""", "does not define")]
    [InlineData("""{"devices":{"A":"x"},"targets":{"T":{"outputs":[]}}}""", "at least one")]
    [InlineData(
        """{"devices":{"A":"x"},"targets":{"T":{"outputs":[{"output":"A"},{"output":"a"}]}}}""",
        "more than once"
    )]
    [InlineData("""{"devices":{"A":"x"},"targets":{"A":{"outputs":[{"output":"A"}]}}}""", "name of its own")]
    [InlineData("""{"devices":{"A":"x"},"targets":{"T":{"outputs":[{"output":"A","delayMs":5000}]}}}""", "between")]
    [InlineData("""{"devices":{"A":"x"},"targets":{"T":{"outputs":[{"output":"A","gain":9}]}}}""", "between")]
    [InlineData("""{"devices":{"A":"x"},"targets":{"T":{"outputs":[{"output":"A","colour":"red"}]}}}""", "colour")]
    [InlineData("""{"devices":{"A":"x"},"targets":{"T":{"outputs":[{"output":"A","required":false}]}}}""", "required")]
    [InlineData("""{"devices":{"A":"x"},"targets":{"T":{"routes":[]}}}""", "routes")]
    public void Refuses_a_target_it_cannot_make_sense_of(string settings, string mentions)
    {
        // Refused rather than ignored, like every other unknown or unusable
        // field: a target that was silently dropped is one somebody believes
        // they have, and they find out when the sound comes out of one speaker.
        string? problem = Configure(new AudioProvider(new FakeAudioEndpoints()), settings);

        Assert.NotNull(problem);
        Assert.Contains(mentions, problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Brings_along_whatever_all_of_its_routes_need()
    {
        // A target playing through the television needs the television's
        // display, exactly as the television's own alias does -- and the route
        // being optional does not change that. Optional means "the target
        // survives this output failing", not "leave that television off".
        var provider = new AudioProvider(new FakeAudioEndpoints());

        Assert.Null(
            Configure(
                provider,
                """
                {
                  "devices": {
                    "Desk": "Speakers (Realtek)",
                    "TV": { "device": "Digital Audio (HDMI)", "requires": { "display": "TVOnly" } }
                  },
                  "targets": { "Party": { "outputs": [
                    { "output": "Desk" },
                    { "output": "TV", "required": false }
                  ] } }
                }
                """
            )
        );

        ResourceRequirement needed = Assert.Single(provider.RequirementsFor("Party"));

        Assert.Equal("display", needed.Key);
        Assert.Equal("TVOnly", needed.Target);
    }

    // ---- starting a target --------------------------------------------------

    [Fact]
    public void Starts_the_routes_before_it_points_windows_at_the_engine()
    {
        // The order is the feature. Making the virtual endpoint the default
        // first would hand Windows an endpoint whose engine is not running yet,
        // and the machine would go quiet for as long as that took.
        FakeAudioEndpoints endpoints = Endpoints();
        endpoints.Default = endpoints.IdOf(0);

        AudioProvider provider = Provider(endpoints, out FakeAudioRuntime runtime);

        provider.Apply("Party", CancellationToken.None);

        AudioTarget applied = Assert.Single(runtime.Applied);

        Assert.Equal("Party", applied.Name);
        Assert.Equal([endpoints.IdOf(1), endpoints.IdOf(0)], applied.Routes.Select(route => route.EndpointId.Value));
        Assert.Equal([endpoints.IdOf(2)], endpoints.MadeDefault);
    }

    [Fact]
    public void Hands_the_engine_the_delay_gain_and_requirement_of_every_route()
    {
        FakeAudioEndpoints endpoints = Endpoints();
        AudioProvider provider = Provider(endpoints, out FakeAudioRuntime runtime);

        provider.Apply("Party", CancellationToken.None);

        AudioTarget applied = Assert.Single(runtime.Applied);

        Assert.Equal(TimeSpan.Zero, applied.Routes[0].Delay);
        Assert.Equal(0f, applied.Routes[0].Processing.GainDb);

        Assert.Equal(TimeSpan.FromMilliseconds(68), applied.Routes[1].Delay);

        // 0.8 linear is a shade under -2 dB, and the engine's chain works in
        // decibels. The conversion is the provider's job precisely so that the
        // number a person typed stays the number the file holds.
        Assert.Equal(20.0 * Math.Log10(0.8), applied.Routes[1].Processing.GainDb, 3);

        Assert.All(applied.Routes, route => Assert.Equal(AudioRouteRequirement.Required, route.Requirement));
    }

    [Fact]
    public void Mutes_a_route_rather_than_asking_for_silence_in_decibels()
    {
        // Zero gain is a muted route. No float holds -infinity dB, and a chain
        // that tried would produce a NaN in the middle of the audio path.
        FakeAudioEndpoints endpoints = Endpoints();
        var provider = new AudioProvider(endpoints, new FakeAudioRuntime());

        Assert.Null(
            Configure(
                provider,
                $$"""
                {
                  "devices": { "Headphones": "{{Speakers}}" },
                  "targets": { "Quiet": { "outputs": [ { "output": "Headphones", "gain": 0 } ] } }
                }
                """
            )
        );

        AudioTarget composed = Assert.Single(provider.Targets).Compose(_ => endpoints.IdOf(0));

        Assert.True(composed.Routes[0].Muted);
        Assert.Equal(0f, composed.Routes[0].Processing.GainDb);
    }

    [Fact]
    public void Sets_the_endpoint_volumes_a_target_asked_for_and_leaves_the_others_alone()
    {
        // Omission means "leave it alone". A target that normalised every
        // endpoint it touched would undo whatever the user last did with their
        // own volume keys.
        FakeAudioEndpoints endpoints = Endpoints();
        endpoints.Volumes[endpoints.IdOf(0)] = 0.9;
        endpoints.Volumes[endpoints.IdOf(1)] = 0.7;

        Provider(endpoints, out _).Apply("Party", CancellationToken.None);

        Assert.Equal(0.4, endpoints.Volumes[endpoints.IdOf(0)]);
        Assert.Equal(0.7, endpoints.Volumes[endpoints.IdOf(1)]);
    }

    [Fact]
    public void Puts_the_volumes_and_the_playback_device_back_when_it_cannot_finish()
    {
        // A target that cannot start is meant to be exactly as harmless as one
        // that failed validation -- not a machine left at whatever levels the
        // target wanted, with no engine to show for it.
        FakeAudioEndpoints endpoints = Endpoints();
        endpoints.Default = endpoints.IdOf(1);
        endpoints.Volumes[endpoints.IdOf(0)] = 0.9;
        endpoints.IgnoresMakeDefault = true;

        AudioProvider provider = Provider(endpoints, out _);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            provider.Apply("Party", CancellationToken.None)
        );

        Assert.Contains("did not accept", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0.9, endpoints.Volumes[endpoints.IdOf(0)]);
    }

    [Fact]
    public void Changes_nothing_when_the_audio_engine_refuses_the_target()
    {
        FakeAudioEndpoints endpoints = Endpoints();
        endpoints.Default = endpoints.IdOf(1);
        endpoints.Volumes[endpoints.IdOf(0)] = 0.9;

        AudioProvider provider = Provider(endpoints, out FakeAudioRuntime runtime);
        runtime.ApplyFails = new InvalidOperationException("The audio Host is not running.");

        Assert.Throws<InvalidOperationException>(() => provider.Apply("Party", CancellationToken.None));

        // The playback device was never touched, and the volume was put back.
        Assert.Empty(endpoints.MadeDefault);
        Assert.Equal(0.9, endpoints.Volumes[endpoints.IdOf(0)]);
    }

    [Fact]
    public void Refuses_a_target_when_the_pcmode_endpoint_is_not_installed()
    {
        // A machine without the PCMode audio subsystem still uses its single
        // outputs normally. It simply cannot run a target, and it says so in
        // terms somebody can act on.
        var endpoints = new FakeAudioEndpoints(Speakers, Television);
        AudioProvider provider = Provider(endpoints, out FakeAudioRuntime runtime);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            provider.Apply("Party", CancellationToken.None)
        );

        Assert.Contains("virtual audio endpoint is not present", failure.Message, StringComparison.Ordinal);
        Assert.Empty(runtime.Applied);
        Assert.Empty(endpoints.MadeDefault);

        // And a plain device alias still works on the same machine.
        provider.Apply("TV", CancellationToken.None);
        Assert.Equal([endpoints.IdOf(1)], endpoints.MadeDefault);
    }

    [Fact]
    public void Refuses_to_guess_which_endpoint_is_pcmode_s_own()
    {
        var endpoints = new FakeAudioEndpoints(Speakers, Television, VirtualEndpoint).Add(
            "Headphones (PCMode Virtual Audio Endpoint)"
        );

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            Provider(endpoints, out _).Apply("Party", CancellationToken.None)
        );

        Assert.Contains(AudioVirtualEndpoint.SettingsPath, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Takes_the_exact_virtual_endpoint_name_the_document_gives_it()
    {
        var endpoints = new FakeAudioEndpoints(Speakers, Television, "PCMode Audio (one)").Add("PCMode Audio (two)");

        var provider = new AudioProvider(endpoints, new FakeAudioRuntime());

        Assert.Null(
            Configure(
                provider,
                $$"""
                {
                  "virtualEndpoint": "PCMode Audio (two)",
                  "devices": { "Headphones": "{{Speakers}}" },
                  "targets": { "Party": { "outputs": [ { "output": "Headphones" } ] } }
                }
                """
            )
        );

        provider.Apply("Party", CancellationToken.None);

        Assert.Equal([endpoints.IdOf(3)], endpoints.MadeDefault);
    }

    [Fact]
    public void Refuses_a_target_whose_two_outputs_are_one_device_right_now()
    {
        // Two aliases that resolved to the same endpoint. The engine refuses a
        // duplicate endpoint outright; caught here so the message names the two
        // aliases rather than an endpoint ID.
        var endpoints = new FakeAudioEndpoints(Speakers, VirtualEndpoint);

        var provider = new AudioProvider(endpoints, new FakeAudioRuntime());

        Assert.Null(
            Configure(
                provider,
                $$"""
                {
                  "devices": { "Headphones": "{{Speakers}}", "TV": "{{Television}}" },
                  "targets": { "Party": { "outputs": [ { "output": "TV" }, { "output": "Headphones" } ] } }
                }
                """
            )
        );

        // The television is off, so its endpoint is not there at all.
        InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() =>
            provider.Apply("Party", CancellationToken.None)
        );

        Assert.Contains(Television, missing.Message, StringComparison.Ordinal);
    }

    // ---- leaving a target ---------------------------------------------------

    [Fact]
    public void Stops_the_engine_before_it_moves_the_default_to_a_single_output()
    {
        // A target left running would go on rendering to its outputs, so the
        // sound would arrive at the new device and at the old target's speakers,
        // with nothing on screen admitting to it.
        FakeAudioEndpoints endpoints = Endpoints();
        AudioProvider provider = Provider(endpoints, out FakeAudioRuntime runtime);

        provider.Apply("Party", CancellationToken.None);
        Assert.NotNull(runtime.Running);

        provider.Apply("TV", CancellationToken.None);

        Assert.Null(runtime.Running);
        Assert.Equal(1, runtime.Stops);
        Assert.Equal(endpoints.IdOf(1), endpoints.MadeDefault[^1]);
    }

    // ---- readback -----------------------------------------------------------

    [Fact]
    public void Calls_a_target_current_only_when_the_engine_and_windows_both_agree()
    {
        FakeAudioEndpoints endpoints = Endpoints();
        AudioProvider provider = Provider(endpoints, out FakeAudioRuntime runtime);

        provider.Apply("Party", CancellationToken.None);
        Assert.Equal("Party", provider.Read());

        // The engine is still holding the target, but something else moved the
        // playback device. Nothing is feeding those routes any more, so the
        // machine is not in that state.
        endpoints.MakeDefault(endpoints.IdOf(0));
        Assert.Equal("Headphones", provider.Read());

        // And the other way round: Windows plays into the PCMode endpoint, and
        // the engine is holding nothing.
        endpoints.MakeDefault(endpoints.IdOf(2));
        runtime.Stop();
        Assert.Null(provider.Read());
    }

    [Fact]
    public void Does_not_ask_the_host_anything_while_the_machine_is_on_a_plain_output()
    {
        // Read is on the convergence poll, four times a second. A machine
        // playing through its speakers has no reason to open a pipe to be told
        // so.
        FakeAudioEndpoints endpoints = Endpoints();
        endpoints.Default = endpoints.IdOf(0);

        var runtime = new CountingAudioRuntime();
        var provider = new AudioProvider(endpoints, runtime);
        Assert.Null(Configure(provider, Settings));

        Assert.Equal("Headphones", provider.Read());
        Assert.Equal(0, runtime.Reads);

        endpoints.MakeDefault(endpoints.IdOf(2));

        provider.Read();
        Assert.Equal(1, runtime.Reads);
    }

    // ---- idempotence --------------------------------------------------------

    [Fact]
    public async Task Re_applying_the_same_target_neither_restarts_it_nor_commands_the_sound_card_again()
    {
        // The engine re-asserts a target on every verification pass and a user
        // presses the same button twice. Both have to be silent.
        using var root = new TemporaryRoot();

        FakeAudioEndpoints endpoints = Endpoints();
        endpoints.Default = endpoints.IdOf(0);

        var runtime = new FakeAudioRuntime();

        TransitionOutcome first = await Engine(root, endpoints, runtime).ApplyAsync("Movie");

        Assert.True(first.Succeeded, first.Message);
        Assert.Single(runtime.Applied);
        Assert.Equal([endpoints.IdOf(2)], endpoints.MadeDefault);

        TransitionOutcome again = await Engine(root, endpoints, runtime).ApplyAsync("Movie");

        Assert.True(again.Succeeded, again.Message);

        // Already correct, so nothing was dispatched at all.
        Assert.Single(runtime.Applied);
        Assert.Equal([endpoints.IdOf(2)], endpoints.MadeDefault);

        Assert.Equal(
            TransitionStepStatus.AlreadyCorrect,
            Assert.Single(again.Steps, step => step.Resource == AudioProvider.ResourceKey).Status
        );
    }

    [Fact]
    public async Task Fails_the_transition_when_a_route_s_device_is_not_there()
    {
        using var root = new TemporaryRoot();
        // Validation could not have caught this: it is a fact about the room,
        // not the file. The television is off, so its HDMI endpoint is gone.

        var endpoints = new FakeAudioEndpoints(Speakers, VirtualEndpoint);
        var runtime = new FakeAudioRuntime();

        TransitionOutcome outcome = await Engine(root, endpoints, runtime).ApplyAsync("Movie");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Contains(Television, outcome.Message, StringComparison.Ordinal);

        Assert.Empty(runtime.Applied);
        Assert.Empty(endpoints.MadeDefault);
        Assert.False(File.Exists(root.StateFile));
    }

    // ---- what a settings screen and a fault report see ----------------------

    [Fact]
    public void Reports_a_target_as_stopped_running_degraded_failed_or_unavailable()
    {
        FakeAudioEndpoints endpoints = Endpoints();
        AudioProvider provider = Provider(endpoints, out FakeAudioRuntime runtime);

        Assert.Equal(AudioTargetState.Stopped, Status(provider).State);

        provider.Apply("Party", CancellationToken.None);
        Assert.Equal(AudioTargetState.Running, Status(provider).State);

        runtime.Health = AudioHealthState.Degraded;
        Assert.Equal(AudioTargetState.Running, Status(provider).State); // both routes required

        runtime.Health = AudioHealthState.Failed;
        AudioTargetStatus failed = Status(provider);
        Assert.Equal(AudioTargetState.Failed, failed.State);
        Assert.NotNull(failed.Problem);

        runtime.Reachable = false;
        AudioTargetStatus unavailable = Status(provider);
        Assert.Equal(AudioTargetState.Unavailable, unavailable.State);
        Assert.Contains("Host", unavailable.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void Degrades_rather_than_fails_when_only_an_optional_route_is_gone()
    {
        // Half a synchronised pair is not the feature, and it is also not
        // nothing: the required output is still playing.
        FakeAudioEndpoints endpoints = Endpoints();

        var runtime = new FakeAudioRuntime();
        var provider = new AudioProvider(endpoints, runtime);

        Assert.Null(
            Configure(
                provider,
                $$"""
                {
                  "devices": { "Headphones": "{{Speakers}}", "TV": "{{Television}}" },
                  "targets": { "Party": { "outputs": [
                    { "output": "TV" },
                    { "output": "Headphones", "required": false }
                  ] } }
                }
                """
            )
        );

        provider.Apply("Party", CancellationToken.None);
        runtime.Health = AudioHealthState.Degraded;

        AudioTargetStatus status = Status(provider);

        Assert.Equal(AudioTargetState.Degraded, status.State);
        Assert.Equal(AudioRouteHealthState.Healthy, status.Routes[0].Runtime);
        Assert.Equal(AudioRouteHealthState.Unavailable, status.Routes[1].Runtime);
        Assert.NotNull(status.Routes[1].Problem);
    }

    [Fact]
    public void Names_pcmode_s_own_endpoint_so_nothing_offers_to_alias_it()
    {
        AudioSnapshot snapshot = Provider(Endpoints(), out _).Discover();

        AudioEndpointInfo pcmode = Assert.Single(snapshot.Endpoints, endpoint => endpoint.IsVirtualEndpoint);

        Assert.Equal(VirtualEndpoint, pcmode.Name);
    }

    [Fact]
    public void Explains_the_engine_rather_than_the_target_when_there_is_no_host()
    {
        AudioProvider provider = Provider(Endpoints(), out FakeAudioRuntime runtime);
        runtime.Reachable = false;

        AudioSnapshot snapshot = provider.Discover();

        Assert.True(snapshot.Available);
        Assert.False(snapshot.EngineAvailable);
        Assert.NotNull(snapshot.Runtime.Problem);

        Assert.Contains("Host is not running", provider.HealthDetail("Party")!, StringComparison.Ordinal);
        Assert.Null(provider.HealthDetail("TV"));
    }

    // ---- processing profiles -------------------------------------------------

    [Fact]
    public void A_route_naming_a_processing_profile_resolves_it_into_the_engine_facing_target()
    {
        FakeAudioEndpoints endpoints = Endpoints();
        var runtime = new FakeAudioRuntime();
        var provider = new AudioProvider(endpoints, runtime);

        Assert.Null(
            Configure(
                provider,
                $$"""
                {
                  "devices": { "Headphones": "{{Speakers}}" },
                  "profiles": {
                    "Warm": {
                      "preampGainDb": -2,
                      "bands": [ { "type": "lowShelf", "frequencyHz": 200, "gainDb": 3, "q": 0.7 } ]
                    }
                  },
                  "targets": { "Solo": { "outputs": [ { "output": "Headphones", "profile": "Warm" } ] } }
                }
                """
            )
        );

        provider.Apply("Solo", CancellationToken.None);

        AudioTarget applied = Assert.Single(runtime.Applied);
        ParametricEqProfile? eq = applied.Routes[0].Processing.ParametricEq;

        Assert.NotNull(eq);
        Assert.Equal(-2f, eq!.PreampGainDb);
        Assert.Equal(EqFilterType.LowShelf, eq.Bands[0].Type);
    }

    [Fact]
    public void A_target_naming_an_undefined_profile_is_refused()
    {
        string? problem = Configure(
            new AudioProvider(new FakeAudioEndpoints()),
            """
            {
              "devices": { "A": "x" },
              "targets": { "T": { "outputs": [ { "output": "A", "profile": "Missing" } ] } }
            }
            """
        );

        Assert.NotNull(problem);
        Assert.Contains("Missing", problem, StringComparison.Ordinal);
    }

    // ---- live calibration ----------------------------------------------------

    [Fact]
    public void Recalibrate_resolves_the_route_s_endpoint_fresh_and_hands_it_to_the_runtime()
    {
        FakeAudioEndpoints endpoints = Endpoints();
        AudioProvider provider = Provider(endpoints, out FakeAudioRuntime runtime);

        provider.Apply("Party", CancellationToken.None);

        provider.Recalibrate(
            "Party",
            "TV",
            TimeSpan.FromMilliseconds(120),
            0.6,
            volume: 0.3,
            profileName: null,
            CancellationToken.None
        );

        AudioEndpointId calibrated = Assert.Single(runtime.Recalibrated);

        Assert.Equal(endpoints.IdOf(1), calibrated.Value);
    }

    [Fact]
    public void Recalibrate_refuses_a_route_the_target_does_not_have()
    {
        AudioProvider provider = Provider(Endpoints(), out _);

        provider.Apply("Party", CancellationToken.None);

        Assert.Throws<ArgumentException>(() =>
            provider.Recalibrate("Party", "NotARoute", TimeSpan.Zero, 1.0, null, null, CancellationToken.None)
        );
    }

    [Fact]
    public void Reports_the_engine_s_own_health_for_a_running_target()
    {
        AudioProvider provider = Provider(Endpoints(), out _);

        provider.Apply("Party", CancellationToken.None);

        string detail = provider.HealthDetail("Party")!;

        Assert.Contains("Party", detail, StringComparison.Ordinal);
        Assert.Contains("VirtualEndpointDriver", detail, StringComparison.Ordinal);
        Assert.Contains("Route", detail, StringComparison.Ordinal);
        Assert.Contains("delay 68 ms", detail, StringComparison.Ordinal);
    }

    // ---- helpers ------------------------------------------------------------

    private static AudioTargetStatus Status(AudioProvider provider) => Assert.Single(provider.Discover().Targets);

    /// <summary>Speakers, television and PCMode's own endpoint, in that order.</summary>
    private static FakeAudioEndpoints Endpoints() => new(Speakers, Television, VirtualEndpoint);

    private static AudioProvider Provider(FakeAudioEndpoints endpoints, out FakeAudioRuntime runtime)
    {
        runtime = new FakeAudioRuntime();

        var provider = new AudioProvider(endpoints, runtime);

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

    private static readonly string Document = $$"""
        {
          "schemaVersion": 1,
          "providers": {
            "audio": {
              "devices": { "Headphones": "{{Speakers}}", "TV": "{{Television}}" },
              "targets": { "Party": { "outputs": [
                { "output": "TV" },
                { "output": "Headphones", "delayMs": 68, "gain": 0.8 }
              ] } }
            }
          },
          "spaces": [
            { "id": "space", "name": "Space", "activities": [
              { "id": "movie", "name": "Movie", "resources": { "theme": "Dark", "audio": "Party" } }
            ] }
          ]
        }
        """;

    private static ModeEngine Engine(TemporaryRoot root, FakeAudioEndpoints endpoints, FakeAudioRuntime runtime)
    {
        root.Write(Document);

        IReadOnlyList<IModeResourceProvider> providers =
        [
            new FakeProvider("theme", "Dark"),
            new AudioProvider(endpoints, runtime),
        ];

        // A fake clock, because this provider retries six times over twelve
        // seconds when a device is missing, and these tests are about what it
        // concludes rather than how patiently it gets there.
        return new ModeEngine(
            ModeConfiguration.LoadOrCreate(root.Path, providers),
            new ModeStateStore(root.StateFile),
            providers,
            new FakeTime()
        );
    }

    /// <summary>A runtime that counts how often it was asked anything.</summary>
    private sealed class CountingAudioRuntime : IAudioRuntime
    {
        public int Reads { get; private set; }

        public AudioRuntimeStatus Status => AudioRuntimeStatus.Unreachable("nothing");

        public AudioRuntimeStatus Refresh()
        {
            Reads++;
            return AudioRuntimeStatus.Unreachable("nothing");
        }

        public Task<IReadOnlyList<AudioRouteWaveform>> WaveformAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AudioRouteWaveform>>([]);

        public void Apply(AudioTarget target, CancellationToken cancellationToken) { }

        public void Recalibrate(
            AudioEndpointId endpointId,
            float volume,
            bool muted,
            TimeSpan delay,
            AudioProcessingProfile processing,
            CancellationToken cancellationToken
        ) { }

        public void Stop() { }

        public void Dispose() { }
    }
}

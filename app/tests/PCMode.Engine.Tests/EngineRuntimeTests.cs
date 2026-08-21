namespace PCMode.Engine.Tests;

/// <summary>
/// What <see cref="EngineRuntime"/> does with a configuration saved from
/// Hardware, Modes or Settings: rebuild it, validate it the same way a real
/// startup would, and only then let it replace what is running.
/// </summary>
public sealed class EngineRuntimeTests
{
    [Fact]
    public void Reload_activates_a_freshly_validated_engine_and_picks_up_a_newly_saved_alias()
    {
        using var root = new TemporaryRoot();
        root.Write(
            """
            {"schemaVersion":1,
             "providers":{"audio":{"devices":{"Headphones":"Endpoint A"}}},
             "spaces":[{"id":"desk","name":"Desk","activities":[
               {"id":"desktop","name":"Desktop","resources":{"theme":"Light"}},
               {"id":"movie","name":"Movie","resources":{"theme":"Dark"}}]}]}
            """);

        var initial = EngineBootstrap.Create(root.Path);
        var runtime = new EngineRuntime(initial, root.Path, FakeControllers);

        HardwareAliasOutcome alias = HardwareConfigurationStore.SetAudioAlias(root.Path, "Endpoint B", "Speakers");
        Assert.True(alias.Success, alias.Problem);

        RuntimeActivationOutcome outcome = runtime.Reload();

        Assert.True(outcome.Activated, outcome.Message);
        Assert.NotSame(initial, runtime.Engine);
        // The reload picked up the newly saved alias from the file on disk.
        Assert.Equal(["desk/desktop", "desk/movie"], runtime.Engine.TargetKeys);

        var audio = Assert.Single(runtime.Engine.Providers.OfType<AudioProvider>());
        Assert.Contains("Headphones", audio.DeviceNames);
        Assert.Contains("Speakers", audio.DeviceNames);
    }

    [Fact]
    public async Task Reload_is_deferred_while_a_transition_is_running_and_the_previous_engine_stays_active()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        var gate = new TaskCompletionSource();
        var theme = new FakeProvider("theme", "Light") { Gate = gate };
        var initial = BuildEngine(root.Path, [theme]);
        var runtime = new EngineRuntime(initial, root.Path, FakeControllers);

        Task<TransitionOutcome> transition = initial.ApplyAsync("Desktop");

        try
        {
            Assert.True(initial.IsBusy);

            RuntimeActivationOutcome outcome = runtime.Reload();

            Assert.Equal(RuntimeActivationStatus.Deferred, outcome.Status);
            Assert.Same(initial, runtime.Engine);
        }
        finally
        {
            gate.SetResult();
            await transition;
        }
    }

    [Fact]
    public void Reload_fails_and_keeps_the_previous_engine_when_the_saved_configuration_does_not_validate()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        var initial = BuildEngine(root.Path, [new FakeProvider("theme", "Light")]);
        var runtime = new EngineRuntime(initial, root.Path, FakeControllers);

        // Corrupted after the runtime was built with a good one -- exactly what
        // a hand edit, or a save that raced with something else, would leave.
        root.Write("{ not valid json");

        RuntimeActivationOutcome outcome = runtime.Reload();

        Assert.Equal(RuntimeActivationStatus.Failed, outcome.Status);
        Assert.Contains("could not be activated", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(initial, runtime.Engine);
        Assert.False(initial.IsBusy);
    }

    [Fact]
    public async Task An_optional_and_unavailable_resource_is_still_omitted_rather_than_failed_after_a_reload()
    {
        using var root = new TemporaryRoot();
        // No 'providers.lgTv' section: the television reads as unconfigured,
        // which is a real, hardware-free "definitely out" -- no socket opened.
        root.Write(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
              {"id":"desktop","name":"Desktop","resources":{"theme":"Light","tvPower":{"value":"On","optional":true}}}]}]}
            """);

        var initial = EngineBootstrap.Create(root.Path);
        var runtime = new EngineRuntime(initial, root.Path, FakeControllers);

        RuntimeActivationOutcome outcome = runtime.Reload();
        Assert.True(outcome.Activated, outcome.Message);

        TransitionOutcome result = await runtime.Engine.ApplyAsync("Desktop", dryRun: true);

        Assert.Equal(TransitionStatus.Succeeded, result.Status);
        TransitionStep omitted = Assert.Single(result.Omitted);
        Assert.Equal("tvPower", omitted.Resource);
    }

    [Fact]
    public void Dispose_stops_the_party_mirror_and_disposes_a_loaded_controller_source()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        var mirrors = new FakeAudioMirrorFactory();
        var party = new PartyAudioHost(mirrors);
        party.Start(new AudioMirrorRequest("ref-id", "Reference", "mirror-id", "Mirror", TimeSpan.Zero, 1.0));

        var initial = BuildEngine(root.Path, [new FakeProvider("theme", "Light")]);
        var controllers = new DisposableFakeGamepadCapabilities();
        var runtime = new EngineRuntime(initial, root.Path, () => controllers, party);

        // Forces the lazily-cached controller source to actually load, so
        // Dispose has something to dispose.
        _ = runtime.HardwareDiscoverers;

        runtime.Dispose();

        Assert.True(mirrors.Started[0].Disposed);
        Assert.True(controllers.Disposed);
    }

    private static IGamepadCapabilitiesSource FakeControllers() => new FakeGamepadCapabilities();

    private static ModeEngine BuildEngine(string root, IReadOnlyList<IModeResourceProvider> providers) =>
        new(ModeConfiguration.LoadOrCreate(root, providers), new ModeStateStore(PCModePaths.StateFile(root)), providers);

    private const string Document =
        """
        {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
          {"id":"desktop","name":"Desktop","resources":{"theme":"Light"}},
          {"id":"movie","name":"Movie","resources":{"theme":"Dark"}}]}]}
        """;

    private sealed class FakeGamepadCapabilities : IGamepadCapabilitiesSource
    {
        public bool IsAvailable => false;

        public GamepadCapabilities? Read(int slot) => null;
    }

    private sealed class DisposableFakeGamepadCapabilities : IGamepadCapabilitiesSource, IDisposable
    {
        public bool IsAvailable => false;

        public bool Disposed { get; private set; }

        public GamepadCapabilities? Read(int slot) => null;

        public void Dispose() => Disposed = true;
    }
}

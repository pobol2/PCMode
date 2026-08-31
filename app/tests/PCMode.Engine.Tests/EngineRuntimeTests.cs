namespace PCMode.Engine.Tests;

/// <summary>
/// What <see cref="EngineRuntime"/> does with a configuration saved from
/// a feature page: rebuild it, validate it the same way a real
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
        var runtime = new EngineRuntime(initial, root.Path);

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
        var runtime = new EngineRuntime(initial, root.Path);

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
        var runtime = new EngineRuntime(initial, root.Path);

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
        var runtime = new EngineRuntime(initial, root.Path);

        RuntimeActivationOutcome outcome = runtime.Reload();
        Assert.True(outcome.Activated, outcome.Message);

        TransitionOutcome result = await runtime.Engine.ApplyAsync("Desktop", dryRun: true);

        Assert.Equal(TransitionStatus.Succeeded, result.Status);
        TransitionStep omitted = Assert.Single(result.Omitted);
        Assert.Equal("tvPower", omitted.Resource);
    }

    /// <summary>
    /// The audio Host is deliberately not one of the things Dispose stops.
    /// </summary>
    /// <remarks>
    /// While an audio target is active the PCMode virtual endpoint is the
    /// machine's playback device, so a shell that shut the Host down on its way
    /// out would leave Windows playing into an endpoint with nothing behind it.
    /// PCMode's side of the connection is closed; the Host keeps the target and
    /// the next start reconnects to it. See <see cref="AudioHostLink"/>.
    /// </remarks>
    [Fact]
    public void Dispose_closes_the_audio_link_without_stopping_the_host()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        var audio = new FakeAudioRuntime();

        var initial = BuildEngine(root.Path, [new FakeProvider("theme", "Light")]);
        var runtime = new EngineRuntime(initial, root.Path, audio);

        runtime.Dispose();

        Assert.Equal(0, audio.Stops);
    }

    private static ModeEngine BuildEngine(string root, IReadOnlyList<IModeResourceProvider> providers) =>
        new(ModeConfiguration.LoadOrCreate(root, providers), new ModeStateStore(PCModePaths.StateFile(root)), providers);

    private const string Document =
        """
        {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
          {"id":"desktop","name":"Desktop","resources":{"theme":"Light"}},
          {"id":"movie","name":"Movie","resources":{"theme":"Dark"}}]}]}
        """;

}

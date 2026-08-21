namespace PCMode.Engine.Tests;

/// <summary>
/// A test that is skipped unless the machine was explicitly volunteered.
/// </summary>
/// <remarks>
/// See <see cref="HardwareFactAttribute"/> in <c>PartyHardwareTest.cs</c> for why
/// this decision is made before the test class is even constructed rather than
/// with a guard inside the test body.
/// </remarks>
public sealed class HealthHardwareFactAttribute : FactAttribute
{
    public HealthHardwareFactAttribute()
    {
        if (!HealthHardwareTest.Requested)
        {
            Skip =
                "Hardware check. Set PCMODE_HEALTH_HARDWARE_TEST=1 to run it: it starts the real " +
                "configured Party group through ModeEngine, stops it out from under the engine to " +
                "simulate a fault, and confirms Repair notices and fixes it.";
        }
    }
}

/// <summary>
/// Exercises Step 2's reliability surface -- <see cref="ModeHealth"/>,
/// <see cref="IHealthReportingProvider"/> and <see cref="ModeEngine.RepairAsync"/>
/// -- against the real configured Party group rather than a fake endpoint.
/// </summary>
/// <remarks>
/// NOT PART OF THE TEST SUITE, AND CANNOT BECOME PART OF IT BY ACCIDENT
///
/// Like <c>PartyHardwareTest</c>, this skips itself unless
/// PCMODE_HEALTH_HARDWARE_TEST is exactly "1". It opens the real Party endpoints
/// and changes the Windows default playback device.
///
/// WHAT IT USES FROM THE REAL MACHINE, AND WHAT IT DOES NOT TOUCH
///
/// It reads the real Party group from %LOCALAPPDATA%\PCMode\Native\config.json,
/// the same one <c>PartyHardwareTest</c> exercises -- but it drives it through a
/// <see cref="ModeEngine"/> built with its own throwaway mode and its own
/// temporary state file, so nothing here can overwrite the recorded mode a real
/// PCMode install is relying on.
///
/// Run it with:
///
///   $env:PCMODE_HEALTH_HARDWARE_TEST="1"; dotnet test app/tests/PCMode.Engine.Tests --filter "FullyQualifiedName~HealthHardwareTest" --logger "console;verbosity=detailed"
/// </remarks>
public sealed class HealthHardwareTest(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Switch = "PCMODE_HEALTH_HARDWARE_TEST";
    private const string RootVariable = "PCMODE_PARTY_HARDWARE_ROOT";
    private const string ModeName = "PCModeHealthHardwareCheck";

    private readonly Xunit.Abstractions.ITestOutputHelper _output = output;

    public static bool Requested =>
        string.Equals(Environment.GetEnvironmentVariable(Switch), "1", StringComparison.Ordinal);

    [HealthHardwareFact]
    public async Task Repair_notices_and_restarts_a_party_group_stopped_out_from_under_it()
    {
        string configRoot = Environment.GetEnvironmentVariable(RootVariable) ?? PCModePaths.DefaultRoot;
        string testRoot = Path.Combine(Path.GetTempPath(), $"PCMode.HealthHardwareTest.{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);

        var host = new PartyAudioHost();
        var endpoints = new CoreAudioEndpoints();

        try
        {
            (string json, string path) = ModeConfiguration.ReadOrCreate(configRoot);
            IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(configRoot, json, host);

            AudioProvider provider = providers.OfType<AudioProvider>().Single();
            _ = ModeConfiguration.Parse(json, providers, path); // validates the real document, as the shell would

            PartySettings party = provider.Party
                ?? throw new InvalidOperationException(
                    $"No Party group is configured in {path}. Add a '{PartySettings.SettingsPath}' section " +
                    "naming a reference output and a mirror output.");

            Say($"Real configuration: {path}");
            Say($"Party group:        '{party.Name}' ({party.Reference} -> {party.Mirror})");

            // A throwaway mode naming only the real Party group, so this test
            // does not depend on -- or need to know -- how the real
            // configuration's own modes are named.
            string checkDocument =
                "{\"schemaVersion\":1,\"spaces\":[{\"id\":\"space\",\"name\":\"Space\",\"activities\":[{\"id\":\"check\",\"name\":" +
                System.Text.Json.JsonSerializer.Serialize(ModeName) +
                ",\"resources\":{\"audio\":" +
                System.Text.Json.JsonSerializer.Serialize(party.Name) +
                "}}]}]}";

            var checkConfiguration = ModeConfiguration.Parse(checkDocument, providers);

            AudioEndpoint? wasDefault = endpoints.DefaultPlayback();

            // A temporary state file: this engine must never write to the real
            // one, or a hardware check would leave a real install's "current
            // mode" reading something a user never asked for.
            var engine = new ModeEngine(
                checkConfiguration,
                new ModeStateStore(Path.Combine(testRoot, "state.json")),
                providers);

            try
            {
                Assert.Null(engine.LastHealth);

                Say(string.Empty);
                Say("Starting the Party group through ModeEngine...");

                TransitionOutcome started = await engine.ApplyAsync(ModeName);

                Assert.True(started.Succeeded, started.Message);

                ModeHealth? health = engine.LastHealth;
                Assert.NotNull(health);
                Assert.Equal(ResourceHealthState.Correct, health!.Overall);

                ResourceHealth audio = Assert.Single(health.Resources);
                Say($"Health overall:     {health.Overall}");
                Say($"Resource state:     {audio.State}");
                Say($"Diagnostics:        {audio.Diagnostics}");

                Assert.Equal(ResourceHealthState.Correct, audio.State);
                Assert.NotNull(audio.Diagnostics);
                Assert.Contains("Capturing=True", audio.Diagnostics, StringComparison.Ordinal);
                Assert.Contains("Rendering=True", audio.Diagnostics, StringComparison.Ordinal);

                // Simulate a fault without going through PCMode at all: the
                // mirror is stopped directly, the way an unplugged endpoint or a
                // crashed native thread would leave it stopped.
                Say(string.Empty);
                Say("Stopping the mirror directly, to simulate a fault Repair has to notice...");
                host.Stop();

                await Task.Delay(200); // let Core Audio settle before reading it back

                ModeHealth? degraded = ModeHealth.From(
                    await engine.ApplyAsync(ModeName, dryRun: true),
                    DateTimeOffset.UtcNow,
                    providers.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase));

                Say($"Health before repair: {degraded.Overall} ({Assert.Single(degraded.Resources).State})");

                Say(string.Empty);
                Say("Repairing...");

                TransitionOutcome? repaired = await engine.RepairAsync();

                Assert.NotNull(repaired);
                Assert.True(repaired!.Succeeded, repaired.Message);

                ModeHealth? healthAfterRepair = engine.LastHealth;
                Assert.NotNull(healthAfterRepair);
                Assert.Equal(ResourceHealthState.Correct, healthAfterRepair!.Overall);

                ResourceHealth audioAfterRepair = Assert.Single(healthAfterRepair.Resources);
                Say($"Health after repair:  {healthAfterRepair.Overall}");
                Say($"Diagnostics:          {audioAfterRepair.Diagnostics}");

                Assert.Contains("Capturing=True", audioAfterRepair.Diagnostics, StringComparison.Ordinal);
                Assert.Contains("Rendering=True", audioAfterRepair.Diagnostics, StringComparison.Ordinal);

                Say(string.Empty);
                Say("Repair correctly noticed the stopped mirror and restarted it.");
            }
            finally
            {
                provider.StopParty();
                host.Dispose();

                if (wasDefault is not null)
                {
                    endpoints.MakeDefault(wasDefault.Id);
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(testRoot, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary folder is not worth failing a hardware check over.
            }
        }
    }

    private void Say(string line)
    {
        _output.WriteLine(line);
        Console.WriteLine(line);
    }
}

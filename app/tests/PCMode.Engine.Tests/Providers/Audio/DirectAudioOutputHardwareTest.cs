namespace PCMode.Engine.Tests;

/// <summary>
/// A test that is skipped unless the machine was explicitly volunteered.
/// </summary>
/// <remarks>
/// Decided at test discovery, like <c>HardwareFactAttribute</c> beside it: an
/// ordinary run never enters this test, and a mistake in it cannot move
/// somebody's speakers in the middle of a build.
/// </remarks>
public sealed class DirectAudioHardwareFactAttribute : FactAttribute
{
    public DirectAudioHardwareFactAttribute()
    {
        if (!DirectAudioOutputHardwareTest.Requested)
        {
            Skip =
                "Hardware check. Set PCMODE_AUDIO_DIRECT_HARDWARE_TEST=1, "
                + $"{DirectAudioOutputHardwareTest.OutputAVariable} and "
                + $"{DirectAudioOutputHardwareTest.OutputBVariable} (two exact Windows playback device names) to "
                + "run it: it changes the Windows default playback device twice.";
        }
    }
}

/// <summary>
/// PCMode switching between two real physical outputs, with no Native Audio
/// involved at all.
/// </summary>
/// <remarks>
/// WHAT IT PROVES
///
/// <code>
/// PCMode activation (ModeEngine.ApplyAsync)
///   -> AudioProvider                  (built with IAudioRuntime.None)
///   -> IAudioEndpoints (CoreAudioEndpoints)
///   -> Windows default playback device
/// </code>
///
/// The <see cref="AudioProvider"/> under test is given no <see cref="IAudioRuntime"/>
/// at all -- see <see cref="EngineBootstrap.ProductionProviders"/> called with
/// <c>audio: null</c> -- which is exactly what a machine with the Native Audio
/// beta capability not installed builds. If activating either output reached for
/// that runtime in any way, it would throw rather than move Windows' default
/// device, which is what <see cref="AudioProviderTests.Applying_a_device_alias_never_touches_the_runtime_when_no_target_is_configured"/>
/// proves without hardware. This test proves the other half: that the real
/// Windows default playback device actually changes, twice, on real hardware.
///
/// NOT PART OF THE TEST SUITE, AND CANNOT BECOME PART OF IT BY ACCIDENT
///
/// It skips itself unless PCMODE_AUDIO_DIRECT_HARDWARE_TEST is exactly "1".
/// Every other Audio provider test fakes the endpoints; this one changes the
/// real Windows default playback device.
///
/// Run it with:
///
///   $env:PCMODE_AUDIO_DIRECT_HARDWARE_TEST="1"
///   $env:PCMODE_AUDIO_DIRECT_OUTPUT_A="Speakers (Realtek(R) Audio)"
///   $env:PCMODE_AUDIO_DIRECT_OUTPUT_B="Headphones (Realtek(R) Audio)"
///   dotnet test app/tests/PCMode.Engine.Tests --filter "FullyQualifiedName~DirectAudioOutputHardwareTest" --logger "console;verbosity=detailed"
///
/// Use the exact names Windows Sound settings shows for two playback devices
/// that are both active right now on this machine.
///
/// WHAT IT PUTS BACK
///
/// The Windows default playback device is recorded before anything is changed
/// and restored afterwards, including when the test fails.
/// </remarks>
public sealed class DirectAudioOutputHardwareTest(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Switch = "PCMODE_AUDIO_DIRECT_HARDWARE_TEST";
    public const string OutputAVariable = "PCMODE_AUDIO_DIRECT_OUTPUT_A";
    public const string OutputBVariable = "PCMODE_AUDIO_DIRECT_OUTPUT_B";

    private readonly Xunit.Abstractions.ITestOutputHelper _output = output;

    /// <summary>Whether the one environment variable that lets this run is set.</summary>
    public static bool Requested =>
        string.Equals(Environment.GetEnvironmentVariable(Switch), "1", StringComparison.Ordinal);

    [DirectAudioHardwareFact]
    public async Task Activates_two_physical_outputs_and_switches_between_them_without_native_audio()
    {
        string outputA = RequiredVariable(OutputAVariable);
        string outputB = RequiredVariable(OutputBVariable);

        using var root = new TemporaryRoot();
        var endpoints = new CoreAudioEndpoints();

        AudioEndpoint? wasDefault = endpoints.DefaultPlayback();

        try
        {
            string json = Document(outputA, outputB);

            // audio: null is the point of the test -- see the type remarks.
            // This is exactly what EngineBootstrap builds on a machine that
            // has never installed the Native Audio beta capability.
            IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(
                root.Path,
                json,
                audio: null
            );

            ModeConfiguration configuration = ModeConfiguration.Parse(json, providers);
            AudioProvider provider = providers.OfType<AudioProvider>().Single();

            var engine = new ModeEngine(configuration, new ModeStateStore(root.StateFile), providers);

            Say($"Output A      : '{outputA}'");
            Say($"Output B      : '{outputB}'");

            await ActivateAndVerify(engine, provider, endpoints, "A", outputA);
            await ActivateAndVerify(engine, provider, endpoints, "B", outputB);

            // Switching back proves this is a real toggle, not a one-way move.
            await ActivateAndVerify(engine, provider, endpoints, "A", outputA);

            Say(string.Empty);
            Say(
                "PCMode switched between two physical outputs with no Native Audio Host, driver or virtual endpoint involved."
            );
        }
        finally
        {
            if (wasDefault is not null)
            {
                try
                {
                    endpoints.MakeDefault(wasDefault.Id);
                }
                catch (Exception exception)
                {
                    Say($"The playback device could not be put back. {exception.Message}");
                }
            }
        }
    }

    private async Task ActivateAndVerify(
        ModeEngine engine,
        AudioProvider provider,
        CoreAudioEndpoints endpoints,
        string activity,
        string expectedDeviceName
    )
    {
        Say(string.Empty);
        Say($"Activating '{activity}' -> '{expectedDeviceName}'...");

        TransitionOutcome outcome = await engine.ApplyAsync(activity);

        Assert.True(outcome.Succeeded, outcome.Message);

        ModeHealth health = engine.LastHealth!;
        Assert.Equal(ResourceHealthState.Correct, health.Overall);

        AudioEndpoint? current = endpoints.DefaultPlayback();

        Say($"Windows default playback device: '{current?.Name}'");

        Assert.NotNull(current);
        Assert.Equal(expectedDeviceName, current!.Name, StringComparer.OrdinalIgnoreCase);

        // Verified through the normal Engine provider flow, not a second,
        // independent read of Core Audio: what the provider itself now
        // believes is playing has to agree with what Windows just did.
        Assert.Equal(activity == "A" ? "OutputA" : "OutputB", provider.Read());
    }

    private static string Document(string outputA, string outputB) =>
        $$"""
            {
              "schemaVersion": 1,
              "providers": {
                "audio": { "devices": { "OutputA": {{Json(outputA)}}, "OutputB": {{Json(outputB)}} } }
              },
              "spaces": [
                {
                  "id": "space",
                  "name": "Space",
                  "activities": [
                    { "id": "A", "name": "A", "resources": { "audio": "OutputA" } },
                    { "id": "B", "name": "B", "resources": { "audio": "OutputB" } }
                  ]
                }
              ]
            }
            """;

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string RequiredVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is string value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{name} must name a device Windows Sound settings shows right now.");

    private void Say(string line)
    {
        _output.WriteLine(line);
        Console.WriteLine(line);
    }
}

using System.Globalization;
using System.Text.Json;
using PCMode.Audio.Client;
using PCMode.Audio.Core.Models;
using PCMode.Audio.DriverClient.Control;
using PCMode.Audio.DriverClient.Diagnostics;
using PCMode.Audio.Protocol.Messages;

namespace PCMode.Engine.Tests;

/// <summary>
/// A test that is skipped unless the machine was explicitly volunteered.
/// </summary>
/// <remarks>
/// Decided when tests are discovered, before anything in the class is
/// constructed, so an ordinary run never enters it. A guard inside the test body
/// would be weaker in both directions: it would report as a passing test that
/// had quietly done nothing, and a mistake in it would play sound out of
/// somebody's speakers in the middle of a build.
/// </remarks>
public sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute()
    {
        if (!AudioTargetHardwareTest.Requested)
        {
            Skip =
                "Hardware check. Set PCMODE_AUDIO_HARDWARE_TEST=1 to run it: it starts the real audio " +
                "Host, changes the Windows default playback device to the PCMode virtual endpoint, and " +
                "plays a tone out loud through every one of the target's outputs.";
        }
    }
}

/// <summary>
/// The whole production audio path, end to end, on the real machine.
/// </summary>
/// <remarks>
/// WHAT IT PROVES
///
/// <code>
/// PCMode activation (ModeEngine.ApplyAsync)
///   -> AudioProvider
///   -> PCMode.Audio.Client
///   -> PCMode.Audio.Host          (launched here, on a pipe of its own)
///   -> PCMode virtual endpoint     (made the Windows playback device)
///   -> AudioEngine routes: DSP, delay, gain, buffering, synchronisation
///   -> physical outputs            (audible)
/// </code>
///
/// Every step is checked rather than assumed. The driver is asked how many PCM
/// packets it actually received, which is the only evidence that Windows
/// playback really reached the virtual endpoint rather than that PCMode said it
/// had; the Host is asked for its Health, which is where the route state and
/// endpoint state live; and the provider is asked what it reads back, which is
/// what a transition would have concluded.
///
/// NOT PART OF THE TEST SUITE, AND CANNOT BECOME PART OF IT BY ACCIDENT
///
/// It skips itself unless PCMODE_AUDIO_HARDWARE_TEST is exactly "1". Every other
/// test in this project fakes the endpoints and the Host; this one opens them,
/// changes the Windows default playback device, moves endpoint volumes and plays
/// a tone out loud, none of which may happen because somebody ran "dotnet test".
///
/// Run it with:
///
///   $env:PCMODE_AUDIO_HARDWARE_TEST="1"; dotnet test app/tests/PCMode.Engine.Tests --filter "FullyQualifiedName~AudioTargetHardwareTest" --logger "console;verbosity=detailed"
///
/// It reads the real configuration -- the audio targets in
/// %LOCALAPPDATA%\PCMode\Native\config.json -- so it exercises the same outputs
/// the product will. Override the folder with PCMODE_AUDIO_HARDWARE_ROOT, choose
/// which target with PCMODE_AUDIO_HARDWARE_TARGET, how long to listen with
/// PCMODE_AUDIO_HARDWARE_SECONDS, and where the Host is with
/// PCMODE_AUDIO_HOST_PATH.
///
/// A HOST OF ITS OWN
///
/// It launches the Host on a pipe named for this run, so it neither adopts nor
/// fights whatever Host a real PCMode session is using -- see
/// <c>AudioHostOwnership</c>, which keys the session claim on the pipe name.
///
/// WHAT IT PUTS BACK
///
/// The Windows default playback device is recorded before anything is changed
/// and restored afterwards, including when the test fails, and the Host it
/// started is shut down. A hardware check that left the machine playing into a
/// virtual endpoint with no engine behind it would be a worse experience than
/// the bug it was looking for.
/// </remarks>
public sealed class AudioTargetHardwareTest(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Switch = "PCMODE_AUDIO_HARDWARE_TEST";
    private const string RootVariable = "PCMODE_AUDIO_HARDWARE_ROOT";
    private const string TargetVariable = "PCMODE_AUDIO_HARDWARE_TARGET";
    private const string SecondsVariable = "PCMODE_AUDIO_HARDWARE_SECONDS";
    private const string HostPathVariable = "PCMODE_AUDIO_HOST_PATH";

    private const string ActivityName = "PCModeAudioHardwareCheck";

    private readonly Xunit.Abstractions.ITestOutputHelper _output = output;

    /// <summary>Whether the one environment variable that lets this run is set.</summary>
    public static bool Requested =>
        string.Equals(Environment.GetEnvironmentVariable(Switch), "1", StringComparison.Ordinal);

    [HardwareFact]
    public async Task Plays_through_the_virtual_endpoint_and_out_of_every_configured_output()
    {
        string configRoot = Environment.GetEnvironmentVariable(RootVariable) ?? PCModePaths.DefaultRoot;
        string hostPath = Environment.GetEnvironmentVariable(HostPathVariable) ?? FindHost();
        int seconds = Seconds();

        string pipeName = $"PCMode.Audio.Hardware.{Guid.NewGuid():N}";

        string testRoot = Path.Combine(Path.GetTempPath(), $"PCMode.AudioHardwareTest.{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);

        var endpoints = new CoreAudioEndpoints();

        var link = new AudioHostLink(
            new AudioHostClient(pipeName, connectTimeoutMilliseconds: 2000),
            () => new AudioHostLauncher(hostPath, pipeName).Launch());

        AudioEndpoint? wasDefault = endpoints.DefaultPlayback();
        WasapiTestSignal? signal = null;

        try
        {
            // Built and validated exactly as the shell builds it, so this is the
            // configuration the product would run rather than a second reading
            // of it. That includes the one-time Party -> Audio Target
            // conversion EngineBootstrap.Create performs before parsing: a real
            // machine's config.json may still be in the old form, and without
            // this step the strict parser below would refuse it by name rather
            // than exercising anything.
            (string rawJson, string path) = ModeConfiguration.ReadOrCreate(configRoot);
            string json = EngineBootstrap.ConvertAudioTargets(configRoot, path, rawJson);
            IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(configRoot, json, link);
            _ = ModeConfiguration.Parse(json, providers, path);

            AudioProvider provider = providers.OfType<AudioProvider>().Single();

            AudioTargetSettings target = ChooseTarget(provider, path);

            Say($"Configuration : {path}");
            Say($"Audio target  : '{target.Name}' -> {string.Join(", ", target.Outputs)}");
            Say($"Audio Host    : {hostPath}");
            Say($"Host pipe     : {pipeName}");

            string virtualEndpointId = AudioVirtualEndpoint.Resolve(endpoints.ActivePlayback());
            Say($"PCMode endpoint: {Name(endpoints, virtualEndpointId)}");

            // A throwaway activity naming only that target, and a temporary
            // state file: this engine must never write to the real one, or a
            // hardware check would leave a real install's "current mode" reading
            // something a user never asked for.
            var engine = new ModeEngine(
                ModeConfiguration.Parse(CheckDocument(target.Name), providers),
                new ModeStateStore(Path.Combine(testRoot, "state.json")),
                providers);

            using var control = new VirtualEndpointControlClient();

            DriverStatus before = control.GetStatus();

            Assert.True(
                before.Available,
                $"The PCMode audio driver is not available. {before.Failure}");

            Say(string.Empty);
            Say("Activating the audio target through PCMode...");

            TransitionOutcome started = await engine.ApplyAsync(ActivityName);

            Assert.True(started.Succeeded, started.Message);

            // 1. PCMode concluded the machine is in that state.
            Assert.Equal(target.Name, provider.Read());

            ModeHealth health = engine.LastHealth!;
            Assert.Equal(ResourceHealthState.Correct, health.Overall);

            Say($"Health        : {health.Overall}");
            Say($"Diagnostics   : {Assert.Single(health.Resources).Diagnostics}");

            // 2. Windows is playing into PCMode's own endpoint.
            Assert.Equal(virtualEndpointId, endpoints.DefaultPlayback()!.Id);

            // 2b. Every route that configures a Windows volume actually had
            //     the real endpoint's own master volume set to it -- the
            //     Audio page's "Windows volume" field, not the Preamp DSP
            //     gain a processing profile applies further down the chain.
            //     See StartTarget's remark on Recalibrate for why a route
            //     with no volume configured is deliberately left untouched
            //     and so is not checked here.
            Say(string.Empty);

            foreach (AudioRouteSettings route in target.Routes)
            {
                if (route.Volume is not double expectedVolume)
                {
                    continue;
                }

                string deviceEndpointId = ResolveDeviceEndpointId(endpoints, provider, route.Output);
                double? actualVolume = endpoints.ReadVolume(deviceEndpointId);

                Say($"Windows volume '{route.Output}': configured {expectedVolume:0.00}, read back {actualVolume:0.00}");

                Assert.NotNull(actualVolume);
                Assert.Equal(expectedVolume, actualVolume!.Value, 2);
            }

            // 3. A Windows application renders into it -- which is the whole
            //    point of the virtual endpoint, and the only way to put real PCM
            //    through the driver.
            Say(string.Empty);
            Say($"Playing a tone into the PCMode endpoint for {seconds}s. Listen to every output.");

            signal = WasapiTestSignal.Start(virtualEndpointId, "PCMode audio hardware check");
            Say($"Signal format : {signal.Format}");

            for (int second = 1; second <= seconds; second++)
            {
                await Task.Delay(1000);

                if (second % 5 == 0 || second == seconds)
                {
                    Say($"{second,4}s  {Describe(provider, target.Name)}");
                }
            }

            // 4. The driver actually received that PCM.
            DriverStatus after = control.GetStatus();

            Say(string.Empty);
            Say($"Driver packets: {before.PacketsRendered} -> {after.PacketsRendered}");
            Say($"PCM consumer  : {after.PcmConsumerConnected}");

            Assert.True(after.PcmConsumerConnected, "The audio Host is not consuming the driver's PCM.");

            Assert.True(
                after.PacketsRendered > before.PacketsRendered,
                "No Windows PCM reached the PCMode audio driver during the check.");

            // 5. The engine routed it to every configured output, and each route
            //    is healthy with the delay it was configured with.
            AudioRuntimeStatus runtime = link.Refresh();

            Assert.True(runtime.Reachable, runtime.Problem);
            Assert.True(runtime.IsRunning(target.Name));

            AudioEngineHealth engineHealth = runtime.Health!;

            Assert.Equal(AudioHealthState.Healthy, engineHealth.State);
            Assert.Equal(AudioInputHealthState.Running, engineHealth.Input!.State);
            Assert.Equal(AudioInputKind.VirtualEndpointDriver, engineHealth.Input.Kind);
            Assert.Equal(target.Routes.Count, engineHealth.Routes.Count);

            for (int index = 0; index < engineHealth.Routes.Count; index++)
            {
                AudioRouteHealth route = engineHealth.Routes[index];

                Say(
                    $"Route {target.Routes[index].Output,-16} {route.State}/{route.EndpointState}, " +
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"delay {route.ConfiguredDelay.TotalMilliseconds:0} ms, " +
                        $"offset {route.ClockOffsetMilliseconds ?? 0:0.0} ms, " +
                        $"underruns {route.UnderrunCount}, resyncs {route.ResyncCount}"));

                Assert.Equal(AudioRouteHealthState.Healthy, route.State);
                Assert.Equal(AudioEndpointState.Active, route.EndpointState);
                Assert.Equal(target.Routes[index].Delay, route.ConfiguredDelay);
                Assert.Null(route.LastFailure);
            }

            // 6. Applying the same target again neither restarts the Host nor
            //    rebuilds the graph, which is what makes a verification pass and
            //    a second button press silent.
            AudioHostStatus hostBefore = runtime.Host!;

            Say(string.Empty);
            Say("Re-applying the same target. The sound must not break.");

            TransitionOutcome again = await engine.ApplyAsync(ActivityName);

            Assert.True(again.Succeeded, again.Message);

            Assert.Equal(
                TransitionStepStatus.AlreadyCorrect,
                Assert.Single(again.Steps, step => step.Resource == AudioProvider.ResourceKey).Status);

            AudioHostStatus hostAfter = link.Refresh().Host!;

            Assert.Equal(hostBefore.ProcessId, hostAfter.ProcessId);
            Assert.Equal(hostBefore.StartedUtc, hostAfter.StartedUtc);

            // 7. Live calibration changes the first route's gain without
            //    restarting the Host, rebuilding the graph, or touching any
            //    other route -- the contract this hardware test exists to
            //    prove now that AudioProvider.Recalibrate reaches a real
            //    Host and a real output stream, not a fake one.
            AudioRouteSettings calibratedRoute = target.Routes[0];
            const double calibratedGain = 0.5;

            Say(string.Empty);
            Say($"Recalibrating '{calibratedRoute.Output}' to gain {calibratedGain:0.00} live. The sound must not glitch.");

            provider.Recalibrate(
                target.Name,
                calibratedRoute.Output,
                TimeSpan.FromMilliseconds(calibratedRoute.DelayMs),
                calibratedGain,
                calibratedRoute.Volume,
                profileName: null,
                CancellationToken.None);

            AudioHostStatus hostAfterCalibration = link.Refresh().Host!;

            Assert.Equal(hostBefore.ProcessId, hostAfterCalibration.ProcessId);
            Assert.Equal(hostBefore.StartedUtc, hostAfterCalibration.StartedUtc);

            AudioEngineHealth healthAfterCalibration = link.Refresh().Health!;

            Assert.Equal(AudioHealthState.Healthy, healthAfterCalibration.State);

            // Positional, like AudioProvider.Describe itself relies on: a
            // target's routes are handed to the engine in configuration
            // order and Health reports them back in that same order.
            AudioRouteHealth calibratedHealth = healthAfterCalibration.Routes[0];

            Assert.Equal(
                AudioTargetSettings.GainDecibels(calibratedGain),
                calibratedHealth.Processing.GainDb,
                2);

            Say($"Health after live calibration: {calibratedHealth.State}, gain {calibratedHealth.Processing.GainDb:0.0} dB");

            Say(string.Empty);
            Say("PCMode drove the whole path: activation -> provider -> Host -> virtual endpoint -> routes -> outputs.");
        }
        finally
        {
            signal?.Dispose();

            try
            {
                link.Stop();
            }
            catch (Exception exception)
            {
                Say($"The audio target did not stop cleanly. {exception.Message}");
            }

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

            await ShutDownAsync(pipeName);

            link.Dispose();

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

    /// <summary>The target to exercise: the one named, or the only one configured.</summary>
    private static AudioTargetSettings ChooseTarget(AudioProvider provider, string path)
    {
        string? wanted = Environment.GetEnvironmentVariable(TargetVariable);

        if (!string.IsNullOrWhiteSpace(wanted))
        {
            return provider.Target(wanted)
                ?? throw new InvalidOperationException(
                    $"No audio target called '{wanted}' is configured in {path}. " +
                    $"Configured: {string.Join(", ", provider.Targets.Select(target => target.Name))}.");
        }

        return provider.Targets.Count switch
        {
            1 => provider.Targets[0],
            0 => throw new InvalidOperationException(
                $"No audio target is configured in {path}. Add a '{AudioTargetSettings.SettingsPath}' " +
                "section naming the outputs it plays through."),
            _ => throw new InvalidOperationException(
                $"{provider.Targets.Count} audio targets are configured in {path}. " +
                $"Set {TargetVariable} to the one to check."),
        };
    }

    /// <summary>Shuts down the Host this check started, and waits for it to go.</summary>
    private async Task ShutDownAsync(string pipeName)
    {
        try
        {
            await new AudioHostClient(pipeName, connectTimeoutMilliseconds: 2000).ShutdownAsync();
        }
        catch (Exception exception)
        {
            Say($"The audio Host did not shut down cleanly. {exception.Message}");
        }
    }

    /// <summary>
    /// The shipped Host, found from the test output folder.
    /// </summary>
    /// <remarks>
    /// It is copied into a folder of its own beside PCMode.Tray -- see the copy
    /// step in PCMode.Tray.csproj -- because it is a program with its own engine,
    /// Windows and driver assemblies. The test project does not build it, so it
    /// is located rather than referenced.
    /// </remarks>
    private static string FindHost()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = Path.GetFileName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!);

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName, "src", "PCMode.Tray", "bin", configuration,
                "net10.0-windows", "AudioHost", "PCMode.Audio.Host.exe");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "PCMode.Audio.Host.exe was not found. Build PCMode.Tray first, or set " +
            $"{HostPathVariable} to the Host executable.");
    }

    private static string CheckDocument(string targetName) =>
        "{\"schemaVersion\":1,\"spaces\":[{\"id\":\"space\",\"name\":\"Space\",\"activities\":[{\"id\":\"check\",\"name\":" +
        JsonSerializer.Serialize(ActivityName) +
        ",\"resources\":{\"audio\":" +
        JsonSerializer.Serialize(targetName) +
        "}}]}]}";

    private static string Describe(AudioProvider provider, string targetName)
    {
        AudioTargetStatus? status = provider.Discover().Target(targetName);

        return status is null
            ? "no status"
            : $"{status.State}: " + string.Join(
                ", ",
                status.Routes.Select(route => $"{route.Output}={route.Runtime?.ToString() ?? "?"}"));
    }

    private static string Name(IAudioEndpoints endpoints, string endpointId) =>
        endpoints.ActivePlayback().FirstOrDefault(endpoint => endpoint.Id == endpointId)?.Name ?? endpointId;

    /// <summary>
    /// The real, live endpoint ID a route's configured output alias resolves
    /// to right now -- the same lookup <c>AudioProvider.Resolve</c> makes
    /// internally, mirrored here rather than exposed, since a hardware check
    /// reading the live device by name is exactly what it means to verify
    /// PCMode actually reached that device rather than trusting its own say-so.
    /// </summary>
    private static string ResolveDeviceEndpointId(IAudioEndpoints endpoints, AudioProvider provider, string routeOutput)
    {
        AudioDeviceMapping mapping = provider.Discover().Devices.Single(
            device => string.Equals(device.LogicalName, routeOutput, StringComparison.OrdinalIgnoreCase));

        return endpoints.ActivePlayback().Single(
            endpoint => string.Equals(endpoint.Name, mapping.EndpointName, StringComparison.OrdinalIgnoreCase)).Id;
    }

    private static int Seconds() =>
        int.TryParse(
            Environment.GetEnvironmentVariable(SecondsVariable),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int given) && given > 0
            ? given
            : 30;

    private void Say(string line)
    {
        _output.WriteLine(line);
        Console.WriteLine(line);
    }

}

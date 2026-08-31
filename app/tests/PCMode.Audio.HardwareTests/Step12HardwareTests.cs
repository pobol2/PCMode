using System.Diagnostics;
using PCMode.Audio.Client;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Core.Processing;
using PCMode.Audio.DriverClient.Control;
using PCMode.Audio.DriverClient.Diagnostics;
using PCMode.Audio.Engine.Runtime;
using PCMode.Audio.Windows.Streams;
using PCMode.Audio.Windows.Threading;
using Xunit;

namespace PCMode.Audio.HardwareTests;

public sealed class Step12HardwareTests
{
    [HardwareFact]
    public async Task Single_output_playback()
    {
        string outputId =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_OUTPUT_ID");

        await using HostSession host =
            await HostSession.StartAsync();

        using var control =
            new VirtualEndpointControlClient();

        DriverStatus before =
            control.GetStatus();

        await host.Client.ApplyTargetAsync(
            CreateTarget(
                "step12-single",
                new AudioRoute(
                    new AudioEndpointId(
                        outputId))));

        await Task.Delay(
            TimeSpan.FromSeconds(8));

        DriverStatus after =
            control.GetStatus();

        AudioEngineHealth health =
            await host.Client.GetHealthAsync();

        Assert.True(
            after.PacketsRendered >
            before.PacketsRendered,
            "No Windows PCM reached the virtual endpoint. Keep audio playing to 'PCMode Audio' during the test.");

        Assert.Equal(
            AudioHealthState.Healthy,
            health.State);

        Assert.Equal(
            AudioInputKind.VirtualEndpointDriver,
            health.Input?.Kind);

        Assert.Equal(
            AudioInputHealthState.Running,
            health.Input?.State);
    }

    [HardwareFact]
    public async Task Dual_output_synchronization()
    {
        string outputA =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_OUTPUT_A_ID");

        string outputB =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_OUTPUT_B_ID");

        await using HostSession host =
            await HostSession.StartAsync();

        await host.Client.ApplyTargetAsync(
            CreateTarget(
                "step12-dual",
                new AudioRoute(
                    new AudioEndpointId(
                        outputA)),
                new AudioRoute(
                    new AudioEndpointId(
                        outputB))));

        await Task.Delay(
            TimeSpan.FromSeconds(30));

        AudioEngineHealth health =
            await host.Client.GetHealthAsync();

        Assert.Equal(
            AudioHealthState.Healthy,
            health.State);

        Assert.Equal(
            2,
            health.Routes.Count);

        Assert.All(
            health.Routes,
            route =>
            {
                Assert.Equal(
                    AudioRouteHealthState.Healthy,
                    route.State);

                Assert.InRange(
                    route.DriftCorrectionPpm,
                    -500.0,
                    500.0);

                Assert.InRange(
                    route.ResampleRatio,
                    0.98,
                    1.02);
            });
    }

    [HardwareFact]
    public async Task Endpoint_disconnect_and_reconnect()
    {
        string outputId =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_OUTPUT_ID");

        await using HostSession host =
            await HostSession.StartAsync();

        await host.Client.ApplyTargetAsync(
            CreateTarget(
                "step12-reconnect",
                new AudioRoute(
                    new AudioEndpointId(
                        outputId))));

        await WaitForRouteStateAsync(
            host.Client,
            state =>
                state ==
                AudioRouteHealthState.Healthy,
            TimeSpan.FromSeconds(15));

        Console.WriteLine(
            "Disconnect or disable the selected physical output now, then reconnect/re-enable it within 90 seconds.");

        await WaitForRouteStateAsync(
            host.Client,
            state =>
                state is
                    AudioRouteHealthState.Unavailable or
                    AudioRouteHealthState.Failed,
            TimeSpan.FromSeconds(90));

        await WaitForRouteStateAsync(
            host.Client,
            state =>
                state ==
                AudioRouteHealthState.Healthy,
            TimeSpan.FromSeconds(90));

        AudioEngineHealth recovered =
            await host.Client.GetHealthAsync();

        Assert.Equal(
            AudioEngineRuntimeState.Running,
            recovered.EngineState);

        Assert.Equal(
            AudioHealthState.Healthy,
            recovered.State);
    }

    [HardwareFact]
    public async Task Format_mismatch_is_converted_by_windows_boundary()
    {
        string outputId =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_OUTPUT_MISMATCH_ID");

        var endpoint =
            new AudioEndpointId(
                outputId);

        var canonical =
            new AudioFormat(
                48_000,
                2);

        using (var probe =
               new WasapiRenderOutput(
                   endpoint,
                   canonical))
        {
            Assert.NotEqual(
                canonical,
                probe.EndpointMixFormat);

            Assert.Equal(
                canonical,
                probe.Format);
        }

        await using HostSession host =
            await HostSession.StartAsync();

        await host.Client.ApplyTargetAsync(
            CreateTarget(
                "step12-format-mismatch",
                new AudioRoute(
                    endpoint)));

        await Task.Delay(
            TimeSpan.FromSeconds(8));

        AudioEngineHealth health =
            await host.Client.GetHealthAsync();

        AudioRouteHealth route =
            Assert.Single(
                health.Routes);

        Assert.Equal(
            canonical,
            route.Format);

        Assert.Equal(
            AudioRouteHealthState.Healthy,
            route.State);
    }

    [HardwareFact]
    public void Live_peq_reconfiguration_keeps_audio_running()
    {
        string source =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_SOURCE_ID");

        string output =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_OUTPUT_ID");

        Exception? failure =
            null;

        long processedFrames =
            0;

        var thread =
            new Thread(
                () =>
                {
                    try
                    {
                        using MmcssRegistration mmcss =
                            MmcssRegistration.RegisterProAudio();

                        using var input =
                            new WasapiLoopbackInput(
                                new AudioEndpointId(
                                    source));

                        using var render =
                            new WasapiRenderOutput(
                                new AudioEndpointId(
                                    output),
                                input.Format);

                        var peq =
                            new ParametricEqProcessor(
                                ParametricEqProfile.Flat,
                                input.Format);

                        var gain =
                            new GainProcessor(
                                0.0f);

                        var runtime =
                            new AudioRouteRuntime(
                                new AudioRoute(
                                    new AudioEndpointId(
                                        output)),
                                render,
                                new AudioProcessingChain(
                                    peq,
                                    gain),
                                gain,
                                peq,
                                blockFrameCapacity: 480);

                        using var graph =
                            new AudioGraph(
                                input,
                                [runtime],
                                blockFrameCapacity: 480);

                        graph.Start();

                        Stopwatch timer =
                            Stopwatch.StartNew();

                        bool changed =
                            false;

                        while (timer.Elapsed <
                               TimeSpan.FromSeconds(10))
                        {
                            if (!changed &&
                                timer.Elapsed >=
                                TimeSpan.FromSeconds(5))
                            {
                                peq.Reconfigure(
                                    new ParametricEqProfile(
                                        -3.0f,
                                        new EqBand(
                                            EqFilterType.Peak,
                                            1_000.0f,
                                            6.0f,
                                            1.0f)));

                                changed =
                                    true;
                            }

                            processedFrames +=
                                graph.ProcessNextBlock();
                        }

                        Assert.True(
                            changed);

                        Assert.Equal(
                            -3.0f,
                            peq.Profile.PreampGainDb);
                    }
                    catch (Exception ex)
                    {
                        failure =
                            ex;
                    }
                })
            {
                Name =
                    "PCMode.Audio.Step12.LivePeq",

                IsBackground =
                    true
            };

        thread.SetApartmentState(
            ApartmentState.STA);

        thread.Start();

        Assert.True(
            thread.Join(
                TimeSpan.FromSeconds(20)),
            "Live PEQ hardware test did not stop.");

        if (failure is not null)
        {
            throw new InvalidOperationException(
                "Live PEQ hardware test failed.",
                failure);
        }

        Assert.True(
            processedFrames > 0,
            "No source audio was captured. Keep audio playing to the selected loopback source.");
    }

    [HardwareFact]
    public async Task Driver_and_host_restart_recovers_audio()
    {
        string outputId =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_OUTPUT_ID");

        string hostPath =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_HOST_PATH");

        string pipeName =
            $"PCMode.Audio.Step12.Restart.{Guid.NewGuid():N}";

        var client =
            new AudioHostClient(
                pipeName,
                connectTimeoutMilliseconds: 10_000);

        var launcher =
            new AudioHostLauncher(
                hostPath,
                pipeName);

        using var control =
            new VirtualEndpointControlClient();

        AudioTarget target =
            CreateTarget(
                "step12-host-restart",
                new AudioRoute(
                    new AudioEndpointId(
                        outputId)));

        ulong before =
            control.GetStatus().PacketsRendered;

        using (Process first =
               await launcher.LaunchAndWaitUntilReadyAsync(
                   client,
                   TimeSpan.FromSeconds(10)))
        {
            try
            {
                await client.ApplyTargetAsync(
                    target);

                await Task.Delay(
                    TimeSpan.FromSeconds(5));

                await client.ShutdownAsync();

                Assert.True(
                    first.WaitForExit(5_000),
                    "First Host instance did not exit cleanly.");
            }
            finally
            {
                // A failure anywhere above (most commonly ApplyTargetAsync,
                // if the driver was already busy) must not leave this Host
                // process running: it would keep holding the driver's
                // single-consumer handle and fail every test that follows.
                KillIfStillRunning(
                    first);
            }
        }

        ulong between =
            control.GetStatus().PacketsRendered;

        Assert.True(
            between > before,
            "No PCM reached the first Host instance.");

        using (Process second =
               await launcher.LaunchAndWaitUntilReadyAsync(
                   client,
                   TimeSpan.FromSeconds(10)))
        {
            try
            {
                await client.ApplyTargetAsync(
                    target);

                await Task.Delay(
                    TimeSpan.FromSeconds(5));

                AudioEngineHealth health =
                    await client.GetHealthAsync();

                Assert.Equal(
                    AudioHealthState.Healthy,
                    health.State);

                Assert.True(
                    control.GetStatus().PacketsRendered >
                    between,
                    "PCM did not resume after Host restart.");

                Console.WriteLine(
                    "Disable/remove the PCMode Audio virtual device now, then re-enable/reinstall it and resume playback within 90 seconds.");

                await WaitForInputStateAsync(
                    client,
                    state =>
                        state is
                            AudioInputHealthState.Unavailable or
                            AudioInputHealthState.Reconnecting or
                            AudioInputHealthState.Failed,
                    TimeSpan.FromSeconds(90));

                await WaitForInputStateAsync(
                    client,
                    state =>
                        state is
                            AudioInputHealthState.Ready or
                            AudioInputHealthState.Running,
                    TimeSpan.FromSeconds(90));
            }
            finally
            {
                try
                {
                    await client.ShutdownAsync();
                }
                catch
                {
                }

                second.WaitForExit(
                    5_000);

                KillIfStillRunning(
                    second);
            }
        }
    }

    /// <summary>
    /// The last resort after a graceful shutdown attempt and a
    /// <see cref="Process.WaitForExit(int)"/>: without this, a Host process
    /// that failed to shut down (or was never asked to, because an earlier
    /// step threw) leaks past the test and keeps the driver's
    /// single-consumer handle busy, failing every hardware test that runs
    /// after it for a reason that has nothing to do with their own bodies.
    /// </summary>
    private static void KillIfStillRunning(
        Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(
                entireProcessTree: true);

            process.WaitForExit(
                5_000);
        }
        catch
        {
        }
    }

    [HardwareFact]
    public async Task One_hour_synchronization_soak()
    {
        string outputA =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_OUTPUT_A_ID");

        string outputB =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_OUTPUT_B_ID");

        await using HostSession host =
            await HostSession.StartAsync();

        await host.Client.ApplyTargetAsync(
            CreateTarget(
                "step12-one-hour-soak",
                new AudioRoute(
                    new AudioEndpointId(
                        outputA)),
                new AudioRoute(
                    new AudioEndpointId(
                        outputB))));

        DateTime deadline =
            DateTime.UtcNow +
            TimeSpan.FromHours(1);

        long[] initialUnderruns =
            [0, 0];

        long[] initialResyncs =
            [0, 0];

        bool capturedBaseline =
            false;

        while (DateTime.UtcNow <
               deadline)
        {
            AudioEngineHealth health =
                await host.Client.GetHealthAsync();

            Assert.Equal(
                AudioEngineRuntimeState.Running,
                health.EngineState);

            Assert.Equal(
                2,
                health.Routes.Count);

            for (int i = 0;
                 i < health.Routes.Count;
                 i++)
            {
                AudioRouteHealth route =
                    health.Routes[i];

                Assert.DoesNotContain(
                    route.State,
                    new[]
                    {
                        AudioRouteHealthState.Unavailable,
                        AudioRouteHealthState.Failed
                    });

                double maximumCorrectionPpm =
                    route.IsResynchronizing
                        ? 20_000.0
                        : 500.0;

                Assert.InRange(
                    route.DriftCorrectionPpm,
                    -maximumCorrectionPpm,
                    maximumCorrectionPpm);

                if (route.ClockOffsetMilliseconds is double offsetMs &&
                    !route.IsResynchronizing)
                {
                    Assert.InRange(
                        Math.Abs(offsetMs),
                        0.0,
                        25.0);
                }

                if (!capturedBaseline)
                {
                    initialUnderruns[i] =
                        route.UnderrunCount;

                    initialResyncs[i] =
                        route.ResyncCount;
                }
                else
                {
                    Assert.InRange(
                        route.UnderrunCount -
                        initialUnderruns[i],
                        0,
                        60);

                    Assert.InRange(
                        route.ResyncCount -
                        initialResyncs[i],
                        0,
                        60);
                }
            }

            capturedBaseline =
                true;

            await Task.Delay(
                TimeSpan.FromSeconds(10));
        }
    }

    private static AudioTarget CreateTarget(
        string id,
        params AudioRoute[] routes)
    {
        return new AudioTarget(
            id,
            id,
            routes);
    }

    private static async Task WaitForRouteStateAsync(
        AudioHostClient client,
        Func<AudioRouteHealthState, bool> predicate,
        TimeSpan timeout)
    {
        Stopwatch stopwatch =
            Stopwatch.StartNew();

        while (stopwatch.Elapsed <
               timeout)
        {
            AudioEngineHealth health =
                await client.GetHealthAsync();

            AudioRouteHealth? route =
                health.Routes.FirstOrDefault();

            if (route is not null &&
                predicate(
                    route.State))
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(
            "Expected route state was not observed before timeout.");
    }

    private static async Task WaitForInputStateAsync(
        AudioHostClient client,
        Func<AudioInputHealthState, bool> predicate,
        TimeSpan timeout)
    {
        Stopwatch stopwatch =
            Stopwatch.StartNew();

        while (stopwatch.Elapsed <
               timeout)
        {
            AudioEngineHealth health =
                await client.GetHealthAsync();

            if (health.Input is not null &&
                predicate(
                    health.Input.State))
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(
            "Expected driver input state was not observed before timeout.");
    }

    private static string RequireEnvironmentVariable(
        string name)
    {
        string? value =
            Environment.GetEnvironmentVariable(
                name);

        if (string.IsNullOrWhiteSpace(
                value))
        {
            throw new InvalidOperationException(
                $"Environment variable {name} is required.");
        }

        return value;
    }

    private sealed class HostSession :
        IAsyncDisposable
    {
        private readonly Process _process;

        public AudioHostClient Client { get; }

        private HostSession(
            Process process,
            AudioHostClient client)
        {
            _process =
                process;

            Client =
                client;
        }

        public static async Task<HostSession> StartAsync()
        {
            string hostPath =
                RequireEnvironmentVariable(
                    "PCMODE_AUDIO_HOST_PATH");

            string pipeName =
                $"PCMode.Audio.Step12.{Guid.NewGuid():N}";

            var client =
                new AudioHostClient(
                    pipeName,
                    connectTimeoutMilliseconds: 10_000);

            var launcher =
                new AudioHostLauncher(
                    hostPath,
                    pipeName);

            Process process =
                await launcher.LaunchAndWaitUntilReadyAsync(
                    client,
                    TimeSpan.FromSeconds(10));

            return new HostSession(
                process,
                client);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                try
                {
                    await Client.ShutdownAsync();
                }
                catch
                {
                }

                _process.WaitForExit(
                    5_000);

                KillIfStillRunning(
                    _process);
            }

            _process.Dispose();
        }
    }
}

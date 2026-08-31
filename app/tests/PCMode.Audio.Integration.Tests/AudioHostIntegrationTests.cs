using System.IO.Pipes;
using PCMode.Audio.Client;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Engine.Contracts;
using PCMode.Audio.Engine.Runtime;
using PCMode.Audio.Host;
using PCMode.Audio.Protocol;
using PCMode.Audio.Protocol.Messages;
using Xunit;

namespace PCMode.Audio.Integration.Tests;

public sealed class AudioHostIntegrationTests
{
    [Fact]
    public async Task Host_starts_and_owns_one_engine_runtime()
    {
        await using var host =
            HostHarness.Create();

        Assert.Equal(
            1,
            host.EngineCreateCount);

        await host.Client.GetStatusAsync();
        await host.Client.GetHealthAsync();
        await host.Client.GetStatusAsync();

        Assert.Equal(
            1,
            host.EngineCreateCount);
    }

    [Fact]
    public async Task Client_connects_and_negotiates_protocol()
    {
        await using var host =
            HostHarness.Create();

        var status =
            await host.Client.GetStatusAsync();

        Assert.Equal(
            HostProtocolVersion.Current,
            status.ProtocolVersion);
    }

    [Fact]
    public async Task Incompatible_protocol_fails_clearly()
    {
        await using var host =
            HostHarness.Create();

        var incompatibleClient =
            new AudioHostClient(
                host.PipeName,
                HostProtocolVersion.Current + 1,
                5000);

        AudioHostProtocolException exception =
            await Assert.ThrowsAsync<
                AudioHostProtocolException>(
                () =>
                    incompatibleClient
                        .GetStatusAsync());

        Assert.Contains(
            "incompatible",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_target_reaches_host_engine()
    {
        await using var host =
            HostHarness.Create();

        AudioTarget target =
            CreateTarget();

        await host.Client.ApplyTargetAsync(
            target);

        Assert.Equal(
            1,
            host.Engine.ApplyCount);

        Assert.Equal(
            target.Id,
            host.Engine.ActiveTarget?.Id);

        Assert.Equal(
            AudioEngineState.Running,
            host.Engine.State);
    }

    [Fact]
    public async Task Get_health_returns_current_runtime_state()
    {
        await using var host =
            HostHarness.Create();

        AudioTarget target =
            CreateTarget();

        await host.Client.ApplyTargetAsync(
            target);

        AudioEngineHealth health =
            await host.Client
                .GetHealthAsync();

        Assert.Equal(
            AudioEngineRuntimeState.Running,
            health.EngineState);

        Assert.Equal(
            target.Id,
            health.ActiveTargetId);

        Assert.Single(
            health.Routes);
    }

    [Fact]
    public async Task Driver_health_provider_runs_off_audio_thread()
    {
        int providerThreadId =
            0;

        await using var host =
            HostHarness.Create(
                inputHealthProvider:
                    _ =>
                    {
                        providerThreadId =
                            Environment.CurrentManagedThreadId;

                        return new AudioInputHealth(
                            AudioInputKind.VirtualEndpointDriver,
                            AudioInputHealthState.Ready,
                            new AudioFormat(
                                48_000,
                                2));
                    });

        await host.Client.ApplyTargetAsync(
            CreateTarget());

        DateTime deadline =
            DateTime.UtcNow +
            TimeSpan.FromSeconds(2);

        while (host.Engine.ProcessingThreadId ==
                   0 &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        AudioEngineHealth health =
            await host.Client.GetHealthAsync();

        Assert.NotEqual(
            0,
            host.Engine.ProcessingThreadId);

        Assert.NotEqual(
            host.Engine.ProcessingThreadId,
            providerThreadId);

        Assert.Equal(
            AudioInputHealthState.Ready,
            health.Input?.State);
    }

    [Fact]
    public async Task Client_reconnects_after_host_restart()
    {
        string pipeName =
            CreatePipeName();

        var client =
            new AudioHostClient(
                pipeName,
                connectTimeoutMilliseconds:
                    5000);

        await using (
            var firstHost =
                HostHarness.Create(
                    pipeName))
        {
            var firstStatus =
                await client.GetStatusAsync();

            Assert.Equal(
                pipeName,
                firstHost.PipeName);

            Assert.Equal(
                HostProtocolVersion.Current,
                firstStatus.ProtocolVersion);
        }

        await using (
            var secondHost =
                HostHarness.Create(
                    pipeName))
        {
            var secondStatus =
                await client.GetStatusAsync();

            Assert.Equal(
                HostProtocolVersion.Current,
                secondStatus.ProtocolVersion);

            Assert.Equal(
                1,
                secondHost.EngineCreateCount);
        }
    }

    [Fact]
    public async Task Stalled_client_connection_does_not_block_other_clients()
    {
        await using var host =
            HostHarness.Create();

        using var stalledPipe =
            new NamedPipeClientStream(
                ".",
                host.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

        await stalledPipe.ConnectAsync(
            5000);

        // The stalled connection is accepted by the server but
        // never sends a command, simulating a hung or slow client.
        // A concurrently-connecting well-behaved client must still
        // be served promptly instead of queueing behind it.
        Task<AudioHostStatus> statusTask =
            host.Client.GetStatusAsync();

        Task completed =
            await Task.WhenAny(
                statusTask,
                Task.Delay(
                    TimeSpan.FromSeconds(10)));

        Assert.Same(
            statusTask,
            completed);

        await statusTask;
    }

    [Fact]
    public async Task Multiple_clients_do_not_create_multiple_engines()
    {
        await using var host =
            HostHarness.Create();

        AudioHostClient[] clients =
        [
            .. Enumerable.Range(
                    0,
                    5)
                .Select(
                    _ =>
                        new AudioHostClient(
                            host.PipeName,
                            connectTimeoutMilliseconds:
                                10000))
        ];

        await Task.WhenAll(
            clients.Select(
                client =>
                    client.GetStatusAsync()));

        Assert.Equal(
            1,
            host.EngineCreateCount);
    }


    [Fact]
    public async Task Host_recovers_after_audio_process_failure_without_restarting_runtime()
    {
        await using var host =
            HostHarness.Create();

        await host.Client.ApplyTargetAsync(
            CreateTarget());

        int initialApplyCount =
            host.Engine.ApplyCount;

        host.Engine.FailNextProcess();

        DateTime deadline =
            DateTime.UtcNow +
            TimeSpan.FromSeconds(5);

        while (host.Engine.ApplyCount ==
                   initialApplyCount &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(
            host.Engine.ApplyCount >
            initialApplyCount,
            "The Host did not reapply the desired target after a process failure.");

        AudioHostStatus status =
            await host.Client.GetStatusAsync();

        Assert.Equal(
            AudioEngineRuntimeState.Running,
            status.EngineState);

        Assert.Equal(
            1,
            host.EngineCreateCount);
    }

    [Fact]
    public async Task Host_debounces_route_recovery_while_the_engine_keeps_processing()
    {
        await using var host =
            HostHarness.Create();

        await host.Client.ApplyTargetAsync(
            CreateTarget());

        int initialApplyCount =
            host.Engine.ApplyCount;

        host.Engine.DegradeNextProcess();

        DateTime degradationDeadline =
            DateTime.UtcNow +
            TimeSpan.FromSeconds(2);

        while (!host.Engine.NeedsRecovery &&
               DateTime.UtcNow < degradationDeadline)
        {
            await Task.Delay(5);
        }

        Assert.True(
            host.Engine.NeedsRecovery,
            "The fake engine did not enter its degraded running state.");

        await Task.Delay(100);

        Assert.Equal(
            initialApplyCount,
            host.Engine.ApplyCount);

        DateTime recoveryDeadline =
            DateTime.UtcNow +
            TimeSpan.FromSeconds(2);

        while (host.Engine.ApplyCount ==
                   initialApplyCount &&
               DateTime.UtcNow < recoveryDeadline)
        {
            await Task.Delay(10);
        }

        Assert.True(
            host.Engine.ApplyCount >
            initialApplyCount,
            "The Host did not retry the degraded route after the debounce interval.");

        Assert.Equal(
            AudioEngineState.Running,
            host.Engine.State);

        Assert.Equal(
            1,
            host.EngineCreateCount);
    }

    [Fact]
    public async Task Host_shutdown_stops_active_engine()
    {
        var host =
            HostHarness.Create();

        try
        {
            await host.Client.ApplyTargetAsync(
                CreateTarget());

            Assert.Equal(
                AudioEngineState.Running,
                host.Engine.State);

            await host.Client.ShutdownAsync();

            await host.WaitForServerStopAsync();

            Assert.Equal(
                AudioEngineState.Stopped,
                host.Engine.State);

            Assert.True(
                host.Engine.StopCount > 0);
        }
        finally
        {
            await host.DisposeAsync();
        }

        Assert.True(
            host.Engine.Disposed);
    }

    private static AudioTarget CreateTarget()
    {
        return new AudioTarget(
            "integration-target",
            "Integration Target",
            new AudioRoute(
                new AudioEndpointId(
                    "fake-output"),
                processing:
                    new AudioProcessingProfile(
                        -2.0f)));
    }

    private static string CreatePipeName()
    {
        return
            "PCMode.Audio.Tests." +
            Guid.NewGuid()
                .ToString("N");
    }

    private sealed class HostHarness :
        IAsyncDisposable
    {
        private readonly AudioHostLifetime
            _lifetime;

        private readonly AudioHostRuntime
            _runtime;

        private readonly Task _serverTask;

        private int _engineCreateCount;

        public string PipeName { get; }

        public FakeAudioEngine Engine { get; private set; } =
            null!;

        public int EngineCreateCount =>
            Volatile.Read(
                ref _engineCreateCount);

        public AudioHostClient Client { get; }

        private HostHarness(
            string pipeName,
            Func<AudioEngineState, AudioInputHealth>?
                inputHealthProvider = null)
        {
            PipeName =
                pipeName;

            var sourceId =
                new AudioEndpointId(
                    "fake-source");

            _lifetime =
                new AudioHostLifetime();

            _runtime =
                new AudioHostRuntime(
                    sourceId,
                    () =>
                    {
                        Interlocked.Increment(
                            ref _engineCreateCount);

                        Engine =
                            new FakeAudioEngine();

                        return Engine;
                    },
                    inputHealthProvider:
                        inputHealthProvider);

            _runtime.Start();

            var options =
                new HostOptions(
                    pipeName,
                    sourceId.Value);

            var server =
                new AudioHostServer(
                    options,
                    _runtime,
                    _lifetime);

            _serverTask =
                server.RunAsync(
                    _lifetime.Stopping);

            Client =
                new AudioHostClient(
                    pipeName,
                    connectTimeoutMilliseconds:
                        10000);
        }

        public static HostHarness Create(
            string? pipeName = null,
            Func<AudioEngineState, AudioInputHealth>?
                inputHealthProvider = null)
        {
            return new HostHarness(
                pipeName ??
                CreatePipeName(),
                inputHealthProvider);
        }

        public Task WaitForServerStopAsync()
        {
            return _serverTask;
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.RequestStop();

            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
            }

            await _runtime.DisposeAsync();

            _lifetime.Dispose();
        }
    }

    private sealed class FakeAudioEngine :
        IAudioEngine
    {
        public AudioEngineState State { get; private set; } =
            AudioEngineState.Stopped;

        public AudioTarget? ActiveTarget { get; private set; }

        public bool NeedsRecovery { get; private set; }

        private int _processFailuresRemaining;

        private int _processDegradationsRemaining;

        public int ProcessingThreadId { get; private set; }

        public int ApplyCount { get; private set; }

        public int StopCount { get; private set; }

        public bool Disposed { get; private set; }

        public void Apply(
            AudioTarget target)
        {
            ObjectDisposedException
                .ThrowIf(
                    Disposed,
                    this);

            ApplyCount++;

            ActiveTarget =
                target;

            State =
                AudioEngineState.Running;

            NeedsRecovery =
                false;
        }

        public int RecalibrateCount { get; private set; }

        public void Recalibrate(
            AudioEndpointId endpointId,
            float volume,
            bool muted,
            float gainDb,
            TimeSpan delay,
            ParametricEqProfile? parametricEq)
        {
            ObjectDisposedException
                .ThrowIf(
                    Disposed,
                    this);

            if (State !=
                    AudioEngineState.Running ||
                ActiveTarget is null)
            {
                throw new InvalidOperationException(
                    "No audio target is running, so there is nothing to calibrate.");
            }

            AudioRoute[] routes =
                [.. ActiveTarget.Routes];

            int index =
                Array.FindIndex(
                    routes,
                    route =>
                        route.EndpointId ==
                        endpointId);

            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"'{endpointId}' is not one of the routes the running target uses.");
            }

            RecalibrateCount++;

            routes[index] =
                new AudioRoute(
                    endpointId,
                    routes[index].Requirement,
                    volume,
                    muted,
                    delay,
                    new AudioProcessingProfile(
                        gainDb,
                        parametricEq));

            ActiveTarget =
                new AudioTarget(
                    ActiveTarget.Id,
                    ActiveTarget.Name,
                    routes);
        }

        public void FailNextProcess()
        {
            Interlocked.Exchange(
                ref _processFailuresRemaining,
                1);
        }

        public void DegradeNextProcess()
        {
            Interlocked.Exchange(
                ref _processDegradationsRemaining,
                1);
        }

        public int ProcessNextBlock()
        {
            ProcessingThreadId =
                Environment.CurrentManagedThreadId;

            ObjectDisposedException
                .ThrowIf(
                    Disposed,
                    this);

            if (Interlocked.Exchange(
                    ref _processFailuresRemaining,
                    0) > 0)
            {
                NeedsRecovery =
                    true;

                State =
                    AudioEngineState.Failed;

                throw new InvalidOperationException(
                    "Simulated process failure.");
            }

            if (Interlocked.Exchange(
                    ref _processDegradationsRemaining,
                    0) > 0)
            {
                NeedsRecovery =
                    true;
            }

            Thread.Sleep(
                1);

            return 480;
        }

        public AudioEngineHealth GetHealth()
        {
            AudioRouteHealth[] routes =
                ActiveTarget is null
                    ? []
                    :
                    [
                        .. ActiveTarget.Routes.Select(
                            route =>
                                new AudioRouteHealth(
                                    route.EndpointId,
                                    route.Requirement,
                                    AudioRouteHealthState
                                        .Healthy,
                                    AudioEndpointState.Active,
                                    new AudioFormat(
                                        48000,
                                        2),
                                    route.Delay,
                                    0.0,
                                    0.0,
                                    1.0,
                                    false,
                                    0,
                                    0,
                                    0,
                                    0.0,
                                    0,
                                    0,
                                    0.0,
                                    0.0,
                                    0.0,
                                    route.Processing,
                                    null))
                    ];

            return new AudioEngineHealth(
                State switch
                {
                    AudioEngineState.Stopped =>
                        AudioEngineRuntimeState.Stopped,

                    AudioEngineState.Starting =>
                        AudioEngineRuntimeState.Starting,

                    AudioEngineState.Running =>
                        AudioEngineRuntimeState.Running,

                    AudioEngineState.Stopping =>
                        AudioEngineRuntimeState.Stopping,

                    AudioEngineState.Failed =>
                        AudioEngineRuntimeState.Failed,

                    _ =>
                        AudioEngineRuntimeState.Failed
                },
                ActiveTarget?.Id,
                ActiveTarget?.Name,
                routes,
                null);
        }

        public void Stop()
        {
            if (Disposed)
            {
                return;
            }

            StopCount++;

            ActiveTarget =
                null;

            State =
                AudioEngineState.Stopped;

            NeedsRecovery =
                false;
        }

        public void Dispose()
        {
            if (Disposed)
            {
                return;
            }

            Stop();

            Disposed =
                true;
        }
    }
}

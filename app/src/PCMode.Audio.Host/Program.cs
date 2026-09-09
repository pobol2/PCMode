using PCMode.Audio.Core.Models;
using PCMode.Audio.DriverClient.Control;
using PCMode.Audio.DriverClient.Diagnostics;
using PCMode.Audio.Engine.Runtime;
using PCMode.Audio.Protocol;
using PCMode.Audio.Windows.Threading;

namespace PCMode.Audio.Host;

internal static class Program
{
    /// <summary>
    /// This session already has an Audio Host, so this one did nothing.
    /// </summary>
    /// <remarks>
    /// Its own code rather than the general failure code, because it is not a
    /// failure: it is the answer to "start one if there is not one", and the
    /// launcher connects to the Host that is already there.
    /// </remarks>
    public const int ExitCodeAlreadyRunning = 3;

    private static async Task<int> Main(string[] args)
    {
        // MMCSS raises the audio thread's OS scheduling priority, but does
        // not exempt it from a GC safepoint: a blocking Gen2 collection can
        // still stall it mid-block regardless of priority. SustainedLowLatency
        // asks the GC to avoid exactly that on this process, at the cost of a
        // larger working set -- the standard trade for a real-time audio
        // thread.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

        try
        {
            HostOptions options = HostOptions.Parse(args);

            // Before the runtime is built and before the pipe is opened. A
            // second Host that got as far as either would already be holding
            // the driver transport it is not entitled to.
            using AudioHostOwnership ownership = AudioHostOwnership.Claim(options.PipeName);

            if (!ownership.Owned)
            {
                Console.Error.WriteLine(ownership.HeldBy);

                return ExitCodeAlreadyRunning;
            }

            string sourceIdentity = options.UsesDevelopmentInput
                ? options.DevelopmentSourceEndpointId!
                : AudioHostSourceIds.VirtualEndpoint;

            var sourceId = new AudioEndpointId(sourceIdentity);

            var engineFactory = new AudioEngineFactory(options);

            using VirtualEndpointControlClient? driverControl = options.UsesDevelopmentInput
                ? null
                : new VirtualEndpointControlClient();

            Func<AudioEngineState, AudioInputHealth> inputHealthProvider = options.UsesDevelopmentInput
                ? CreateDevelopmentInputHealth
                : state => CreateDriverInputHealth(driverControl!, state);

            using var lifetime = new AudioHostLifetime();

            await using var runtime = new AudioHostRuntime(
                sourceId,
                engineFactory.Create,
                () => MmcssRegistration.RegisterProAudio(),
                inputHealthProvider
            );

            runtime.Start();

            var server = new AudioHostServer(options, runtime, lifetime);

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;

                lifetime.RequestStop();
            };

            Console.WriteLine("PCMode.Audio.Host started.");

            Console.WriteLine($"Pipe   : {options.PipeName}");

            if (options.UsesDevelopmentInput)
            {
                Console.WriteLine($"Input  : development loopback ({options.DevelopmentSourceEndpointId})");
            }
            else
            {
                Console.WriteLine("Input  : PCMode Audio virtual endpoint");
            }

            Console.WriteLine($"PID    : {Environment.ProcessId}");

            await server.RunAsync(lifetime.Stopping);

            await runtime.ShutdownAsync();

            Console.WriteLine("PCMode.Audio.Host stopped cleanly.");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);

            return 1;
        }
    }

    private static AudioInputHealth CreateDevelopmentInputHealth(AudioEngineState engineState)
    {
        AudioInputHealthState state = engineState switch
        {
            AudioEngineState.Stopped => AudioInputHealthState.Stopped,

            AudioEngineState.Starting => AudioInputHealthState.Ready,

            AudioEngineState.Running => AudioInputHealthState.Running,

            AudioEngineState.Stopping => AudioInputHealthState.Stopped,

            AudioEngineState.Failed => AudioInputHealthState.Failed,

            _ => AudioInputHealthState.Unknown,
        };

        return new AudioInputHealth(AudioInputKind.DevelopmentLoopback, state);
    }

    private static AudioInputHealth CreateDriverInputHealth(
        VirtualEndpointControlClient control,
        AudioEngineState engineState
    )
    {
        DriverStatus status = control.GetStatus();

        AudioFailure? failure = string.IsNullOrWhiteSpace(status.Failure)
            ? null
            : new AudioFailure("Driver", status.Failure, 0, DateTimeOffset.UtcNow);

        AudioInputHealthState state =
            !status.Available || !status.DeviceStarted ? AudioInputHealthState.Unavailable
            : !status.PcmConsumerConnected && engineState == AudioEngineState.Running
                ? AudioInputHealthState.Reconnecting
            : status.StreamRunning ? AudioInputHealthState.Running
            : AudioInputHealthState.Ready;

        return new AudioInputHealth(
            AudioInputKind.VirtualEndpointDriver,
            state,
            status.CurrentFormat ?? status.PreferredFormat,
            failure
        );
    }
}

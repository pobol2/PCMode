using PCMode.Audio.Core.Models;
using PCMode.Audio.Protocol.Messages;

namespace PCMode.Engine.Tests;

/// <summary>
/// The audio Host, made up.
/// </summary>
/// <remarks>
/// The reason <see cref="IAudioRuntime"/> is an interface. Everything about
/// deciding whether an audio target may run, which endpoints it uses, what
/// happens when it is asked for twice, and what stops it is above that line and
/// testable without a driver, a Host process, or a sound card; everything below
/// it moves samples and can only be proved by listening. See
/// <c>AudioTargetHardwareTest</c> for the half that listens.
/// </remarks>
internal sealed class FakeAudioRuntime : IAudioRuntime
{
    private AudioTarget? _running;

    /// <summary>Whether a Host answers at all.</summary>
    public bool Reachable { get; set; } = true;

    /// <summary>Thrown from <see cref="Apply"/>, when set.</summary>
    public Exception? ApplyFails { get; set; }

    /// <summary>What the engine reports about the running target.</summary>
    public AudioHealthState Health { get; set; } = AudioHealthState.Healthy;

    /// <summary>Every target handed over, in order. Length is what proves idempotence.</summary>
    public List<AudioTarget> Applied { get; } = [];

    /// <summary>Thrown from <see cref="Recalibrate"/>, when set.</summary>
    public Exception? RecalibrateFails { get; set; }

    /// <summary>Every live recalibration handed over, in order.</summary>
    public List<AudioEndpointId> Recalibrated { get; } = [];

    /// <summary>How many times the runtime was told to stop.</summary>
    public int Stops { get; private set; }

    /// <summary>What is running, or null.</summary>
    public AudioTarget? Running => _running;

    public AudioRuntimeStatus Status => Read();

    public AudioRuntimeStatus Refresh() => Read();

    public void Apply(AudioTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        if (ApplyFails is not null)
        {
            throw ApplyFails;
        }

        Applied.Add(target);
        _running = target;
    }

    public void Recalibrate(
        AudioEndpointId endpointId,
        float volume,
        bool muted,
        TimeSpan delay,
        AudioProcessingProfile processing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processing);
        cancellationToken.ThrowIfCancellationRequested();

        if (RecalibrateFails is not null)
        {
            throw RecalibrateFails;
        }

        if (_running is null)
        {
            throw new InvalidOperationException("No audio target is running, so there is nothing to calibrate.");
        }

        AudioRoute[] routes = [.. _running.Routes];
        int index = Array.FindIndex(routes, route => route.EndpointId == endpointId);

        if (index < 0)
        {
            throw new InvalidOperationException($"'{endpointId}' is not one of the routes the running target uses.");
        }

        Recalibrated.Add(endpointId);

        routes[index] = new AudioRoute(endpointId, routes[index].Requirement, volume, muted, delay, processing);
        _running = new AudioTarget(_running.Id, _running.Name, routes);
    }

    public void Stop()
    {
        Stops++;
        _running = null;
    }

    public void Dispose()
    {
    }

    private AudioRuntimeStatus Read()
    {
        if (!Reachable)
        {
            return AudioRuntimeStatus.Unreachable("No PCMode audio Host is running.");
        }

        var status = new AudioHostStatus(
            ProtocolVersion: 1,
            ProcessId: 4242,
            StartedUtc: DateTimeOffset.UnixEpoch,
            SourceEndpointId: "driver:PCMode.Audio.VirtualEndpoint",
            EngineState: _running is null ? AudioEngineRuntimeState.Stopped : AudioEngineRuntimeState.Running,
            ActiveTargetId: _running?.Id,
            ActiveTargetName: _running?.Name);

        return new AudioRuntimeStatus(true, null, status, EngineHealth());
    }

    private AudioEngineHealth? EngineHealth()
    {
        if (_running is null)
        {
            return new AudioEngineHealth(AudioEngineRuntimeState.Stopped, null, null, [], null);
        }

        var routes = new List<AudioRouteHealth>();

        foreach (AudioRoute route in _running.Routes)
        {
            AudioRouteHealthState state = Health switch
            {
                AudioHealthState.Healthy => AudioRouteHealthState.Healthy,
                AudioHealthState.Degraded => route.Requirement == AudioRouteRequirement.Optional
                    ? AudioRouteHealthState.Unavailable
                    : AudioRouteHealthState.Healthy,
                _ => AudioRouteHealthState.Failed,
            };

            routes.Add(new AudioRouteHealth(
                route.EndpointId,
                route.Requirement,
                state,
                AudioEndpointState.Active,
                new AudioFormat(48000, 2),
                route.Delay,
                clockOffsetFrames: 0,
                driftCorrectionPpm: 0,
                resampleRatio: 1,
                isResynchronizing: false,
                underrunCount: 0,
                resyncCount: 0,
                stallRecoveryCount: 0,
                longestProcessGapMilliseconds: 0,
                outputRecoveryFadeCount: 0,
                outputEndpointDryCount: 0,
                longestOutputSubmitGapMilliseconds: 0,
                longestOutputWriteMilliseconds: 0,
                longestOutputBufferWaitMilliseconds: 0,
                route.Processing,
                state is AudioRouteHealthState.Failed or AudioRouteHealthState.Unavailable
                    ? new AudioFailure("Endpoint", "That output was unplugged.", 0, DateTimeOffset.UnixEpoch)
                    : null));
        }

        return new AudioEngineHealth(
            AudioEngineRuntimeState.Running,
            _running.Id,
            _running.Name,
            routes,
            Health == AudioHealthState.Failed
                ? new AudioFailure("Engine", "The audio engine stopped.", 0, DateTimeOffset.UnixEpoch)
                : null,
            new AudioInputHealth(
                AudioInputKind.VirtualEndpointDriver,
                AudioInputHealthState.Running,
                new AudioFormat(48000, 2)));
    }
}

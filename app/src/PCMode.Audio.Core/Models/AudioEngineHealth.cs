namespace PCMode.Audio.Core.Models;

public enum AudioHealthState
{
    Inactive,
    Pending,
    Healthy,
    Degraded,
    Failed
}

public enum AudioEngineRuntimeState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed
}

public sealed class AudioEngineHealth
{
    private readonly AudioRouteHealth[] _routes;

    private readonly IReadOnlyList<AudioRouteHealth>
        _readOnlyRoutes;

    public AudioHealthState State { get; }

    public AudioEngineRuntimeState EngineState { get; }

    public string? ActiveTargetId { get; }

    public string? ActiveTargetName { get; }

    public AudioInputHealth? Input { get; }

    public IReadOnlyList<AudioRouteHealth> Routes =>
        _readOnlyRoutes;

    public AudioFailure? LastFailure { get; }

    public AudioEngineHealth(
        AudioEngineRuntimeState engineState,
        string? activeTargetId,
        string? activeTargetName,
        IEnumerable<AudioRouteHealth> routes,
        AudioFailure? lastFailure,
        AudioInputHealth? input = null)
    {
        ArgumentNullException.ThrowIfNull(
            routes);

        EngineState =
            engineState;

        ActiveTargetId =
            activeTargetId;

        ActiveTargetName =
            activeTargetName;

        Input =
            input;

        _routes =
            routes.ToArray();

        _readOnlyRoutes =
            Array.AsReadOnly(
                _routes);

        LastFailure =
            lastFailure;

        State =
            Aggregate(
                engineState,
                input,
                _routes);
    }

    public AudioEngineHealth WithInput(
        AudioInputHealth input)
    {
        ArgumentNullException.ThrowIfNull(
            input);

        return new AudioEngineHealth(
            EngineState,
            ActiveTargetId,
            ActiveTargetName,
            _routes,
            LastFailure,
            input);
    }

    private static AudioHealthState Aggregate(
        AudioEngineRuntimeState engineState,
        AudioInputHealth? input,
        IReadOnlyList<AudioRouteHealth> routes)
    {
        switch (engineState)
        {
            case AudioEngineRuntimeState.Stopped:
                return AudioHealthState.Inactive;

            case AudioEngineRuntimeState.Starting:
            case AudioEngineRuntimeState.Stopping:
                return AudioHealthState.Pending;

            case AudioEngineRuntimeState.Failed:
                return AudioHealthState.Failed;
        }

        if (input is not null)
        {
            switch (input.State)
            {
                case AudioInputHealthState.Unavailable:
                case AudioInputHealthState.Failed:
                    return AudioHealthState.Failed;

                case AudioInputHealthState.Reconnecting:
                case AudioInputHealthState.Stopped:
                case AudioInputHealthState.Unknown:
                    return AudioHealthState.Degraded;
            }
        }

        bool degraded =
            false;

        for (int i = 0;
             i < routes.Count;
             i++)
        {
            AudioRouteHealth route =
                routes[i];

            switch (route.State)
            {
                case AudioRouteHealthState.Failed:
                case AudioRouteHealthState.Unavailable:
                    if (route.Requirement ==
                        AudioRouteRequirement.Required)
                    {
                        return AudioHealthState.Failed;
                    }

                    degraded =
                        true;

                    break;

                case AudioRouteHealthState.Degraded:
                case AudioRouteHealthState.Pending:
                    degraded =
                        true;

                    break;
            }
        }

        return degraded
            ? AudioHealthState.Degraded
            : AudioHealthState.Healthy;
    }
}

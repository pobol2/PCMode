namespace PCMode.Audio.Core.Models;

public enum AudioInputKind
{
    Unknown,
    VirtualEndpointDriver,
    DevelopmentLoopback
}

public enum AudioInputHealthState
{
    Unknown,
    Stopped,
    Ready,
    Running,
    Reconnecting,
    Unavailable,
    Failed
}

public sealed record AudioInputHealth
{
    public AudioInputKind Kind { get; }

    public AudioInputHealthState State { get; }

    public AudioFormat? Format { get; }

    public AudioFailure? LastFailure { get; }

    public AudioInputHealth(
        AudioInputKind kind,
        AudioInputHealthState state,
        AudioFormat? format = null,
        AudioFailure? lastFailure = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state));
        }

        Kind = kind;
        State = state;
        Format = format;
        LastFailure = lastFailure;
    }
}

namespace PCMode.Audio.Protocol.Messages;

public enum AudioHostCommandKind
{
    Hello,
    ApplyTarget,
    Stop,
    GetStatus,
    GetHealth,
    Shutdown,
    RecalibrateRoute,
    GetWaveform,
}

public sealed record AudioHostCommand(
    int ProtocolVersion,
    Guid RequestId,
    AudioHostCommandKind Kind,
    ApplyAudioTargetRequest? ApplyTarget,
    RecalibrateRouteRequest? Recalibrate = null
)
{
    public static AudioHostCommand Create(
        AudioHostCommandKind kind,
        ApplyAudioTargetRequest? applyTarget = null,
        int protocolVersion = HostProtocolVersion.Current,
        RecalibrateRouteRequest? recalibrate = null
    )
    {
        return new AudioHostCommand(protocolVersion, Guid.NewGuid(), kind, applyTarget, recalibrate);
    }
}

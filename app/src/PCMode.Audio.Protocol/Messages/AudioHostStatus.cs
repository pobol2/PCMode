using PCMode.Audio.Core.Models;

namespace PCMode.Audio.Protocol.Messages;

public sealed record AudioHostStatus(
    int ProtocolVersion,
    int ProcessId,
    DateTimeOffset StartedUtc,
    string SourceEndpointId,
    AudioEngineRuntimeState EngineState,
    string? ActiveTargetId,
    string? ActiveTargetName
);

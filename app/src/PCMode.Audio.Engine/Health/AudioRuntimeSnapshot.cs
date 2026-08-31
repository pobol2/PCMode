using PCMode.Audio.Core.Models;
using PCMode.Audio.Engine.Contracts;
using PCMode.Audio.Engine.Runtime;

namespace PCMode.Audio.Engine.Health;

internal enum AudioRouteRuntimeHealthState
{
    Configured,
    Starting,
    Running,
    Stopped,
    Unavailable,
    Failed
}

internal sealed record AudioRouteRuntimeSnapshot(
    AudioRoute Route,
    AudioRouteRuntimeHealthState State,
    AudioEndpointState EndpointState,
    AudioFormat? Format,
    double ClockOffsetFrames,
    double DriftCorrectionPpm,
    double ResampleRatio,
    bool IsResynchronizing,
    long UnderrunCount,
    long ResyncCount,
    long StallRecoveryCount,
    double LongestProcessGapMilliseconds,
    AudioOutputDiagnostics OutputDiagnostics,
    AudioFailure? LastFailure);

internal sealed record AudioRuntimeSnapshot(
    AudioEngineState EngineState,
    AudioTarget? ActiveTarget,
    IReadOnlyList<AudioRouteRuntimeSnapshot> Routes,
    AudioFailure? LastFailure);
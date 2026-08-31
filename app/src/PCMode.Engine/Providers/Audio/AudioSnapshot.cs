using PCMode.Audio.Core.Models;

namespace PCMode.Engine;

/// <summary>Whether a configured device can actually be selected right now.</summary>
public enum AudioMappingState
{
    /// <summary>Exactly one active endpoint has that name.</summary>
    Ready,

    /// <summary>
    /// No active endpoint has that name. A typo, or a device that is switched
    /// off -- for HDMI audio the second is the ordinary case, not a fault.
    /// </summary>
    Missing,

    /// <summary>
    /// More than one active endpoint has that name. Windows does not enforce
    /// unique friendly names, and there is no way to tell which was meant.
    /// </summary>
    Ambiguous,

    /// <summary>
    /// Core Audio could not be reached, so nothing could be checked. Not the
    /// same as <see cref="Missing"/>, which is a statement about the machine.
    /// </summary>
    Unknown,
}

/// <summary>One playback device Windows has right now.</summary>
/// <remarks>
/// The endpoint ID is deliberately not here. What a settings UI writes into the
/// configuration is the name, because that is the identifier that survives the
/// device being unplugged and plugged back in; publishing the ID alongside it
/// would invite somebody to store the one that does not.
/// </remarks>
/// <param name="IsVirtualEndpoint">
/// Whether this is PCMode's own virtual endpoint -- the one audio targets play
/// into. Shown so it is not mistaken for a device to alias; nothing routes on it.
/// </param>
public sealed record AudioEndpointInfo(string Name, bool IsDefault, bool IsVirtualEndpoint = false);

/// <summary>One configured logical name, and what it currently resolves to.</summary>
public sealed record AudioDeviceMapping(
    string LogicalName,
    string EndpointName,
    AudioMappingState State,
    int Matches);

/// <summary>Whether an Audio Target is configured, and if so what it is doing.</summary>
public enum AudioTargetState
{
    /// <summary>Configured, and not running. The ordinary state of every target nobody selected.</summary>
    Stopped,

    /// <summary>Running, with every route it needs.</summary>
    Running,

    /// <summary>
    /// Running, with something wrong -- an optional route that failed, a route
    /// still recovering. Deliberately not folded into <see cref="Running"/>:
    /// half a synchronised pair is not the feature.
    /// </summary>
    Degraded,

    /// <summary>
    /// It was asked for and it is not playing. Deliberately not folded into
    /// <see cref="Stopped"/>: one of them is what was asked for and the other is
    /// a fault nobody has been told about.
    /// </summary>
    Failed,

    /// <summary>
    /// The audio Host is not there, so this target cannot run at all. Not a
    /// fault of the target: a machine without the PCMode audio subsystem
    /// installed still uses its single outputs normally.
    /// </summary>
    Unavailable,
}

/// <summary>One route of a target, as a settings screen and a fault report need it.</summary>
public sealed record AudioTargetRouteStatus(
    string Output,
    string EndpointName,
    AudioMappingState Mapping,
    int DelayMs,
    double Gain,
    double? Volume,
    bool Required,
    string? ProfileName,
    AudioRouteHealthState? Runtime,
    string? Problem);

/// <summary>One configured Audio Target as it stands.</summary>
/// <param name="Routes">The routes in configuration order.</param>
/// <param name="Detail">
/// The Host's own report about this target, when it is the one running.
/// </param>
public sealed record AudioTargetStatus(
    string Name,
    IReadOnlyList<AudioTargetRouteStatus> Routes,
    AudioTargetState State,
    string? Problem,
    string? Detail);

/// <summary>
/// What the audio provider can see: the smallest thing a settings UI needs.
/// </summary>
/// <remarks>
/// Presentation-neutral on purpose -- names, states and counts, no formatting
/// and no UI types. The same snapshot answers the questions anything
/// configuring audio has to ask: is audio working at all, what are this
/// machine's devices actually called, does what I already wrote down still match
/// one of them, and is the audio engine there.
///
/// It is a snapshot rather than a live view. Devices appear and disappear, so a
/// value that claimed to stay current would be wrong without saying when.
/// </remarks>
/// <param name="Runtime">
/// What the audio Host is doing. Never null: a machine with no Host installed
/// gets an unreachable status with the reason, which is an answer rather than an
/// absence.
/// </param>
public sealed record AudioSnapshot(
    bool Available,
    string? Problem,
    IReadOnlyList<AudioEndpointInfo> Endpoints,
    IReadOnlyList<AudioDeviceMapping> Devices,
    IReadOnlyList<AudioTargetStatus> Targets,
    AudioRuntimeStatus Runtime)
{
    /// <summary>
    /// Whether any logical device has been defined. Being unconfigured is not a
    /// fault: it is what a machine that has never set up audio looks like.
    /// </summary>
    public bool Configured => Devices.Count > 0;

    /// <summary>
    /// Whether the PCMode audio engine is available to run targets at all.
    /// </summary>
    public bool EngineAvailable => Runtime.Reachable;

    /// <summary>That target's status, or null for a name nothing defines.</summary>
    public AudioTargetStatus? Target(string name) =>
        Targets.FirstOrDefault(target => string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase));
}

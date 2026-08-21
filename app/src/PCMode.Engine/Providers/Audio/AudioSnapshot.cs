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
public sealed record AudioEndpointInfo(string Name, bool IsDefault);

/// <summary>One configured logical name, and what it currently resolves to.</summary>
public sealed record AudioDeviceMapping(
    string LogicalName,
    string EndpointName,
    AudioMappingState State,
    int Matches);

/// <summary>Whether the Party group is configured, and if so what it is doing.</summary>
public enum PartyState
{
    /// <summary>No Party group is configured. Not a fault: it is optional.</summary>
    NotConfigured,

    /// <summary>Configured, and not running. The ordinary state of every mode that is not Party.</summary>
    Stopped,

    /// <summary>Capturing and rendering.</summary>
    Running,

    /// <summary>
    /// It was running and stopped on its own -- almost always an endpoint that
    /// went away. Deliberately not folded into <see cref="Stopped"/>: one of them
    /// is what was asked for and the other is a fault nobody has been told about.
    /// </summary>
    Failed,
}

/// <summary>The Party group as it stands, for diagnostics and for a settings UI.</summary>
/// <param name="Members">
/// The reference output first, then the mirrored one -- the order the signal
/// travels in.
/// </param>
/// <param name="Health">
/// The running mirror's own report, or null when there is nothing running to
/// report on.
/// </param>
public sealed record AudioPartyStatus(
    string Name,
    IReadOnlyList<string> Members,
    PartyState State,
    int DelayMs,
    double Gain,
    AudioMirrorHealth? Health,
    string? Problem);

/// <summary>
/// What the audio provider can see: the smallest thing a settings UI needs.
/// </summary>
/// <remarks>
/// Presentation-neutral on purpose -- names, states and counts, no formatting
/// and no UI types. The same snapshot answers the three questions anything
/// configuring audio has to ask: is audio working at all, what are this
/// machine's devices actually called, and does what I already wrote down still
/// match one of them.
///
/// It is a snapshot rather than a live view. Devices appear and disappear, so a
/// value that claimed to stay current would be wrong without saying when.
/// </remarks>
public sealed record AudioSnapshot(
    bool Available,
    string? Problem,
    IReadOnlyList<AudioEndpointInfo> Endpoints,
    IReadOnlyList<AudioDeviceMapping> Devices,
    AudioPartyStatus? Party = null)
{
    /// <summary>
    /// Whether any logical device has been defined. Being unconfigured is not a
    /// fault: it is what a machine that has never set up audio looks like.
    /// </summary>
    public bool Configured => Devices.Count > 0;
}

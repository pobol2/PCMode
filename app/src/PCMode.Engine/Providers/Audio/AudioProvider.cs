using System.Text.Json;

namespace PCMode.Engine;

/// <summary>
/// Chooses which device sound comes out of.
/// </summary>
/// <remarks>
/// LOGICAL NAMES, NOT WINDOWS NAMES
///
/// A mode says "TV" or "Headphones". What those mean on this particular machine
/// is configuration, because the string Windows uses is a fact about somebody's
/// sound card -- "Speakers (Realtek(R) Audio)" -- and nothing shipped can guess
/// it. That is the same division the cursor provider already keeps: the mode
/// names an intention, the settings say what it means here.
///
/// RESOLVED FRESH, EVERY TIME
///
/// Configuration stores the friendly name and never an endpoint ID. See
/// <see cref="IAudioEndpoints"/>: the HDMI endpoint exists only while the
/// television is awake, so the device a mode wants may legitimately not be there
/// when the mode is validated and be there by the time it is applied.
///
/// WHAT IS CHECKED WHEN
///
/// That a logical name exists is a fact about the configuration, so it is
/// checked at load time and again before a transition dispatches. That the
/// device is plugged in right now is a fact about the machine, so it is checked
/// in <see cref="Apply"/> and nowhere earlier. Refusing a mode at load because a
/// television was off would make the configuration valid or invalid depending on
/// what was switched on at the time.
///
/// PARTY IS A DEVICE NAME LIKE ANY OTHER
///
/// A Party group is another value this resource takes -- "Party" beside
/// "Headphones" and "TV" -- and that is the whole of what makes the two mutually
/// exclusive. A mode asks for one audio target, so applying a single output
/// stops the mirror without anything having to coordinate it, and applying Party
/// sets its reference output as the Windows default before the mirror starts.
/// Nothing else in the engine needs to know Party exists.
/// </remarks>
public sealed class AudioProvider :
    IConfigurableProvider, IDependentProvider, IConvergentProvider, IHealthReportingProvider
{
    /// <summary>The configuration key.</summary>
    public const string ResourceKey = "audio";

    /// <summary>Where this provider's settings live in the document.</summary>
    public const string SettingsPath = "providers.audio.devices";

    private readonly IAudioEndpoints _endpoints;
    private readonly PartyAudioHost _party;

    private IReadOnlyDictionary<string, string> _devices =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The one configured Party group, when there is one.</summary>
    private PartySettings? _group;

    /// <summary>
    /// What each device needs before it exists, keyed by logical name.
    /// </summary>
    /// <remarks>
    /// Kept beside the endpoint names rather than folded in with them, because
    /// almost every device has nothing here and the mapping is what this provider
    /// is actually about.
    /// </remarks>
    private IReadOnlyDictionary<string, IReadOnlyList<ResourceRequirement>> _requires =
        new Dictionary<string, IReadOnlyList<ResourceRequirement>>(StringComparer.OrdinalIgnoreCase);

    public AudioProvider()
        : this(new CoreAudioEndpoints())
    {
    }

    /// <param name="party">
    /// Who owns the running mirror. Shared across every engine this process
    /// builds, so that re-reading the configuration does not abandon a running
    /// Party group -- see <see cref="PartyAudioHost"/>. A provider given its own
    /// host is one nothing else can see, which is right for validation and wrong
    /// for the shell.
    /// </param>
    public AudioProvider(IAudioEndpoints endpoints, PartyAudioHost? party = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        _endpoints = endpoints;
        _party = party ?? new PartyAudioHost();
    }

    public string Key => ResourceKey;

    public string DisplayName => "Audio output";

    /// <summary>
    /// After the displays, when a mode changes those too.
    /// </summary>
    /// <remarks>
    /// An endpoint carried over a display cable is not present until the
    /// display is. Choosing the sound first would mean choosing between the
    /// devices that happened to exist beforehand, and the one the mode actually
    /// wants would arrive moments later, unselected.
    ///
    /// Last of all, after the television's picture too. Not because the sound
    /// depends on the picture, but because the settings written there are the end
    /// of a sequence that has been disturbing the HDMI link, and an endpoint
    /// selected while that is still happening is an endpoint that can disappear
    /// from underneath the selection.
    ///
    /// An ordering preference and not a requirement. Which display layout, if
    /// any, a given endpoint arrives with is a fact about somebody's cabling
    /// that this provider has no way to know -- so a mode that sets only the
    /// sound is applied as written rather than having a layout invented for it.
    /// </remarks>
    public IReadOnlyList<string> RunsAfter => [DisplayProvider.ResourceKey, TvProfileProvider.ResourceKey];

    /// <summary>
    /// Whatever the device being selected says it needs.
    /// </summary>
    /// <remarks>
    /// The stronger half of the same story as <see cref="RunsAfter"/>. Running
    /// after the displays is a preference that costs nothing to state for every
    /// device; declaring that one particular device does not exist until one
    /// particular layout is up is a fact about somebody's cabling, so it is
    /// configuration and applies only to the device it was written under.
    ///
    /// With it, a mode that says nothing but "sound on the television" brings the
    /// television's layout along and gets an endpoint that exists. Without it,
    /// that mode fails six times over twelve seconds and then reports that no
    /// active playback device has that name, which is true and useless.
    /// </remarks>
    /// <remarks>
    /// A Party group needs whatever both its members need. Nothing else would
    /// do: the group cannot start unless both endpoints exist, so a group with a
    /// television in it depends on the television's display exactly as the
    /// television's own alias does.
    /// </remarks>
    public IReadOnlyList<ResourceRequirement> RequirementsFor(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return [];
        }

        string wanted = target.Trim();

        if (_group is PartySettings group && string.Equals(wanted, group.Name, StringComparison.OrdinalIgnoreCase))
        {
            return [.. group.Members.SelectMany(RequirementsFor).Distinct()];
        }

        return _requires.TryGetValue(wanted, out IReadOnlyList<ResourceRequirement>? needed) ? needed : [];
    }

    /// <summary>
    /// Patient, because the device may not exist yet.
    /// </summary>
    /// <remarks>
    /// WHY THIS ONE RETRIES THE MOST
    ///
    /// <see cref="Apply"/> throws when no active endpoint carries the wanted
    /// name, and on this machine that is a routine, temporary state rather than
    /// an error: the television's HDMI endpoint appears some seconds after the
    /// display layout that woke the television up. Running after the displays
    /// gets the order right but not the timing -- Windows reports the layout as
    /// applied before Core Audio has finished enumerating what arrived with it.
    /// A single attempt would fail a mode that was about to work.
    ///
    /// So the attempts are the point, and the interval between them is short
    /// enough that a device appearing is noticed rather than waited out.
    ///
    /// It settles, briefly, because the default endpoint is contested. Windows
    /// moves the default itself when devices arrive, so a readback taken the
    /// instant after the change can report a device that is about to be replaced
    /// by whatever Windows preferred.
    /// </remarks>
    public ConvergencePolicy PolicyFor(string target) => new()
    {
        Verify = true,
        MaxAttempts = 6,
        RetryInterval = TimeSpan.FromSeconds(2),
        PollInterval = TimeSpan.FromMilliseconds(250),
        Settle = TimeSpan.FromMilliseconds(500),
    };

    /// <summary>The configured logical names, in name order.</summary>
    public IReadOnlyList<string> DeviceNames =>
        [.. _devices.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The configured Party group, or null when there is not one.</summary>
    public PartySettings? Party => _group;

    /// <summary>
    /// The Windows device name a logical name points at, or null for a name this
    /// provider does not have.
    /// </summary>
    /// <remarks>
    /// The configured string, not a live endpoint. What it currently resolves to
    /// -- one device, none, or two with the same name -- is what
    /// <see cref="Discover"/> answers.
    /// </remarks>
    public string? EndpointNameFor(string logical) =>
        !string.IsNullOrWhiteSpace(logical) && _devices.TryGetValue(logical.Trim(), out string? endpoint)
            ? endpoint
            : null;

    /// <summary>
    /// Every value a mode may give this resource, in name order: the device
    /// aliases, and the Party group when one is configured.
    /// </summary>
    /// <remarks>
    /// What a settings screen must offer, and deliberately not the same list as
    /// <see cref="DeviceNames"/>. A Party group is a value of this resource like
    /// any alias -- that is the whole of what makes it and a single output
    /// mutually exclusive -- so a mode editor offering only the aliases would
    /// leave a configured group impossible to ask for without editing JSON.
    /// </remarks>
    public IReadOnlyList<string> SelectableNames
    {
        get
        {
            List<string> names = [.. _devices.Keys];

            if (_group is not null)
            {
                names.Add(_group.Name);
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);

            return names;
        }
    }

    public bool TryConfigure(JsonElement? settings, out string? problem)
    {
        // No settings at all is allowed and leaves this provider with nothing it
        // can apply. Audio is optional: a machine with one pair of speakers has
        // no reason to define anything here, and refusing that document would
        // make a feature nobody asked for mandatory.
        if (settings is not JsonElement element || element.ValueKind == JsonValueKind.Null)
        {
            _devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _requires = new Dictionary<string, IReadOnlyList<ResourceRequirement>>(StringComparer.OrdinalIgnoreCase);
            _group = null;
            problem = null;
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = "'providers.audio' must be an object.";
            return false;
        }

        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var required = new Dictionary<string, IReadOnlyList<ResourceRequirement>>(StringComparer.OrdinalIgnoreCase);

        // Read after the devices, whatever order they were written in: a Party
        // group is defined in terms of them, so it cannot be checked until they
        // are all known.
        JsonElement? group = null;

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.NameEquals("party"))
            {
                group = property.Value;
                continue;
            }

            if (!property.NameEquals("devices"))
            {
                problem = $"'providers.audio' does not understand '{property.Name}'. It takes 'devices' and 'party'.";
                return false;
            }

            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                problem =
                    $"'{SettingsPath}' must be an object mapping a name you choose to the exact " +
                    "device name Windows shows in Sound settings.";

                return false;
            }

            foreach (JsonProperty device in property.Value.EnumerateObject())
            {
                if (!TryReadDevice(
                        device,
                        out string? logical,
                        out string? endpoint,
                        out IReadOnlyList<ResourceRequirement>? needs,
                        out problem))
                {
                    return false;
                }

                if (needs!.Count > 0)
                {
                    required[logical!] = needs;
                }

                if (!parsed.TryAdd(logical!, endpoint!))
                {
                    // Case-insensitively, because that is how a mode looks one
                    // up. Two entries differing only in case means one of them
                    // can never be selected and nothing would say which.
                    problem = $"'{SettingsPath}' defines '{logical}' more than once.";
                    return false;
                }
            }
        }

        if (!TryRefuseSharedEndpoint(parsed, out problem))
        {
            return false;
        }

        PartySettings? party = null;

        if (group is JsonElement written && !PartySettings.TryParse(written, parsed, out party, out problem))
        {
            return false;
        }

        _devices = parsed;
        _requires = required;
        _group = party;
        problem = null;
        return true;
    }

    public bool IsValidValue(string value, out string? problem)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problem = "the audio device has no name.";
            return false;
        }

        if (_devices.Count == 0)
        {
            problem =
                $"'{value.Trim()}' cannot be applied because no audio devices are defined. " +
                $"Add one under '{SettingsPath}'.";

            return false;
        }

        if (!_devices.ContainsKey(value.Trim()) && !IsParty(value))
        {
            problem =
                $"'{value.Trim()}' is not an audio device. " +
                $"'{SettingsPath}' defines: {string.Join(", ", SelectableNames)}.";

            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>
    /// The logical name of whatever is currently the default, when it has one.
    /// </summary>
    /// <remarks>
    /// Null covers three genuinely different situations -- Core Audio could not
    /// be reached, there is no default at all, and the default is a device
    /// nothing in the configuration names -- and they are folded together on
    /// purpose. To the engine null means one thing, "apply it anyway", and that
    /// is the correct answer to all three. Telling them apart is what
    /// <see cref="Discover"/> is for.
    /// </remarks>
    public string? Read()
    {
        if (_devices.Count == 0)
        {
            return null;
        }

        AudioEndpoint? current;

        try
        {
            current = _endpoints.DefaultPlayback();
        }
        catch (Exception)
        {
            return null;
        }

        if (current is null || string.IsNullOrWhiteSpace(current.Name))
        {
            return null;
        }

        // A group is the current audio target only while BOTH halves of it are
        // true: a mirror of this pair is running, and the reference endpoint is
        // still what Windows plays through. Either one alone is not the feature.
        //
        // Which is what stops three different things from being mistaken for a
        // running group -- a runtime that died with its endpoint, a fresh
        // process that has never started one, and something else having moved
        // the default out from under a mirror that is still faithfully copying
        // an endpoint nothing plays to any more.
        if (_group is PartySettings group &&
            Running(group) &&
            string.Equals(current.Name, _devices[group.Reference], StringComparison.OrdinalIgnoreCase))
        {
            return group.Name;
        }

        // The first match is the only match: two logical names for one endpoint
        // are refused when the settings are read, precisely so that this answer
        // cannot depend on the order somebody wrote them in.
        foreach ((string logical, string endpoint) in _devices)
        {
            if (string.Equals(endpoint, current.Name, StringComparison.OrdinalIgnoreCase))
            {
                return logical;
            }
        }

        return null;
    }

    public void Apply(string value, CancellationToken cancellationToken)
    {
        if (IsParty(value))
        {
            StartParty(_group!, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(value) ||
            !_devices.TryGetValue(value.Trim(), out string? wanted))
        {
            // Unreachable through the engine, which validates first. Kept so a
            // direct caller cannot send sound to a device nobody named.
            throw new ArgumentException($"'{value}' is not an audio device.", nameof(value));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Before the default moves, not after. A mirror left running would go on
        // capturing an endpoint that is no longer what the machine plays through,
        // so the sound would arrive at the new device and at the old group's
        // mirror, seconds apart, with nothing on screen admitting to it.
        _party.Stop();

        _endpoints.MakeDefault(Resolve(wanted));
    }

    /// <summary>
    /// Establishes the reference output, then starts the mirror.
    /// </summary>
    /// <remarks>
    /// THE ORDER IS THE FEATURE
    ///
    /// Loopback capture reads what an endpoint is playing, so the reference
    /// endpoint has to be the one Windows is actually playing through before
    /// there is anything to mirror. Starting the mirror first would capture
    /// silence from a device nothing is using and render it, perfectly, to the
    /// speakers.
    ///
    /// The default is read back rather than assumed. Changing it is the one
    /// thing PCMode does through an interface Windows does not document, and a
    /// Party group built on a default that did not take would be two outputs
    /// where one of them plays nothing.
    ///
    /// Volumes are set before the mirror starts, so that a group configured
    /// quietly does not announce itself at whatever the endpoints were left at.
    ///
    /// EVERYTHING IT CHANGES, IT CAN PUT BACK
    ///
    /// A failure at any point after the default has moved rolls back the default
    /// and whichever volumes were actually changed, best-effort, before the
    /// exception reaches the caller. A Party group that cannot start is meant to
    /// be exactly as harmless as one that failed validation -- not a machine left
    /// on the reference endpoint at whatever volume the group wanted, with no
    /// mirror to show for it.
    /// </remarks>
    private void StartParty(PartySettings group, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string reference = Resolve(_devices[group.Reference]);
        string mirror = Resolve(_devices[group.Mirror]);

        if (string.Equals(reference, mirror, StringComparison.Ordinal))
        {
            // Two aliases that resolved to one endpoint. Capturing an endpoint's
            // own output and rendering it back into the same endpoint is a
            // feedback loop, and it is loud.
            throw new InvalidOperationException(
                $"'{group.Reference}' and '{group.Mirror}' are the same playback device right now, " +
                "so there is nothing to mirror to.");
        }

        var request = new AudioMirrorRequest(reference, group.Reference, mirror, group.Mirror, group.Delay, group.Gain);

        // Read before anything changes, so a failure partway through has
        // something to restore. Best-effort: a read that cannot be taken simply
        // leaves nothing to roll that particular thing back to.
        AudioEndpoint? previousDefault = SafeReadDefault();
        double? previousReferenceVolume = SafeReadVolume(reference);
        double? previousMirrorVolume = SafeReadVolume(mirror);

        bool defaultChanged = false;
        bool referenceVolumeChanged = false;
        bool mirrorVolumeChanged = false;

        try
        {
            // Set every time, including when the mirror for this pair is already
            // running. Getting here at all means the readback disagreed, and one
            // of the two things it checks is that the reference is still the
            // default -- so this is either the first start or something that has
            // to be put back.
            _endpoints.MakeDefault(reference);
            defaultChanged = true;

            cancellationToken.ThrowIfCancellationRequested();

            AudioEndpoint? now = _endpoints.DefaultPlayback();

            if (now is null || !string.Equals(now.Id, reference, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Windows did not accept '{group.Reference}' as the playback device, so there is nothing " +
                    $"for '{group.Mirror}' to mirror. It reports " +
                    (now is null ? "no default playback device." : $"'{now.Name}'."));
            }

            if (group.ReferenceVolume is not null)
            {
                SetVolume(group.Reference, group.ReferenceVolume);
                referenceVolumeChanged = true;
            }

            if (group.MirrorVolume is not null)
            {
                SetVolume(group.Mirror, group.MirrorVolume);
                mirrorVolumeChanged = true;
            }

            cancellationToken.ThrowIfCancellationRequested();

            _party.Start(request);
        }
        catch
        {
            RollBackPartyStart(
                reference, mirror,
                defaultChanged, previousDefault,
                referenceVolumeChanged, previousReferenceVolume,
                mirrorVolumeChanged, previousMirrorVolume);

            throw;
        }
    }

    /// <summary>
    /// Puts back whatever a failed Party start had already changed, best-effort.
    /// </summary>
    /// <remarks>
    /// Each thing is restored independently, and a failure restoring one does
    /// not stop the others: the exception this is called from is what the
    /// caller needs to see, and there is nothing more useful to do about an
    /// endpoint that will not take its own former value back.
    /// </remarks>
    private void RollBackPartyStart(
        string reference,
        string mirror,
        bool defaultChanged,
        AudioEndpoint? previousDefault,
        bool referenceVolumeChanged,
        double? previousReferenceVolume,
        bool mirrorVolumeChanged,
        double? previousMirrorVolume)
    {
        if (referenceVolumeChanged && previousReferenceVolume is double referenceLevel)
        {
            TryRestore(() => _endpoints.SetVolume(reference, referenceLevel));
        }

        if (mirrorVolumeChanged && previousMirrorVolume is double mirrorLevel)
        {
            TryRestore(() => _endpoints.SetVolume(mirror, mirrorLevel));
        }

        if (defaultChanged && previousDefault is not null)
        {
            TryRestore(() => _endpoints.MakeDefault(previousDefault.Id));
        }

        static void TryRestore(Action restore)
        {
            try
            {
                restore();
            }
            catch (Exception)
            {
            }
        }
    }

    private AudioEndpoint? SafeReadDefault()
    {
        try
        {
            return _endpoints.DefaultPlayback();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private double? SafeReadVolume(string endpointId)
    {
        try
        {
            return _endpoints.ReadVolume(endpointId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Moves one endpoint's volume slider, when the group asked for one.</summary>
    private void SetVolume(string logical, double? level)
    {
        if (level is not double wanted)
        {
            // Omission means "leave it alone". A group that silently normalised
            // every endpoint it touched would undo whatever the user last did
            // with their own volume keys.
            return;
        }

        try
        {
            _endpoints.SetVolume(Resolve(_devices[logical]), wanted);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"The volume of '{logical}' could not be set. {exception.Message}", exception);
        }
    }

    /// <summary>Whether the mirror running right now is this group's.</summary>
    private bool Running(PartySettings group) =>
        _party.Current is AudioMirrorRequest current &&
        string.Equals(current.ReferenceName, group.Reference, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(current.MirrorName, group.Mirror, StringComparison.OrdinalIgnoreCase);

    private bool IsParty(string? value) =>
        _group is not null &&
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value.Trim(), _group.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Changes a running Party group's delay and gain without restarting it.
    /// </summary>
    /// <remarks>
    /// For calibration: two speakers are aligned by ear, in small steps, while
    /// something is playing. The configured values stay as they are -- this
    /// moves what is running, and nothing here writes to the document.
    /// </remarks>
    /// <returns>Whether there was a running group to adjust.</returns>
    public bool AdjustParty(TimeSpan delay, double gain) =>
        _party.Adjust(
            TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 0, PartySettings.MaxDelayMs)),
            Math.Clamp(gain, 0, PartySettings.MaxGain));

    /// <summary>Stops the Party mirror if one is running.</summary>
    public void StopParty() => _party.Stop();

    /// <summary>
    /// The Party group's own report, for a health page. Null for a target that
    /// is not the configured Party group -- an ordinary device has nothing this
    /// provider's <see cref="Read"/> answer does not already say.
    /// </summary>
    public string? HealthDetail(string target)
    {
        if (!IsParty(target))
        {
            return null;
        }

        AudioPartyStatus? status = PartyStatus();

        if (status is null)
        {
            return null;
        }

        if (status.Health is not AudioMirrorHealth health)
        {
            return status.Problem is null
                ? $"Party '{status.Name}' is not running."
                : $"Party '{status.Name}' is not running. {status.Problem}";
        }

        return
            $"Party '{status.Name}': reference '{status.Members.ElementAtOrDefault(0)}', " +
            $"mirror '{status.Members.ElementAtOrDefault(1)}'. " +
            $"Capturing={health.Capturing}, Rendering={health.Rendering}, " +
            $"Delay={health.DelayMs}ms, Gain={health.Gain:0.00}, " +
            $"ClockOffset={health.ClockOffsetMs:0.0}ms, DriftCorrections={health.DriftCorrections}, " +
            $"Resyncs={health.Resyncs}, Underruns={health.Underruns}, " +
            $"Buffer(ref={health.ReferenceBuffer.ActualMs:0.0}ms, mirror={health.MirrorBuffer.ActualMs:0.0}ms, " +
            $"cushion={health.CushionMs:0.0}ms), EffectiveLatency={health.EffectiveLatencyMs:0.0}ms" +
            (health.LastFailure is null ? "." : $". Last failure: {health.LastFailure}");
    }

    /// <summary>
    /// What audio looks like on this machine right now.
    /// </summary>
    /// <remarks>
    /// The one call anything configuring audio needs, and the reason it is here
    /// rather than in a settings screen: which names are usable, and whether the
    /// ones already written down still resolve, are questions this provider
    /// answers by the same rules it applies by. Answering them somewhere else
    /// would be a second implementation of the matching rule, and the two would
    /// disagree exactly when it mattered.
    /// </remarks>
    public AudioSnapshot Discover()
    {
        IReadOnlyList<AudioEndpoint> live;
        AudioEndpoint? current;

        try
        {
            live = _endpoints.ActivePlayback();
            current = _endpoints.DefaultPlayback();
        }
        catch (Exception exception)
        {
            // Unavailable, and said so. Reporting the configured devices as
            // missing here would be a claim about the machine that nothing
            // actually checked.
            return new AudioSnapshot(
                Available: false,
                exception.Message,
                [],
                [.. _devices.Select(device =>
                    new AudioDeviceMapping(device.Key, device.Value, AudioMappingState.Unknown, Matches: 0))],
                PartyStatus());
        }

        var endpoints = new List<AudioEndpointInfo>(live.Count);

        foreach (AudioEndpoint endpoint in live)
        {
            endpoints.Add(new AudioEndpointInfo(
                endpoint.Name,
                current is not null && string.Equals(endpoint.Id, current.Id, StringComparison.Ordinal)));
        }

        var mappings = new List<AudioDeviceMapping>(_devices.Count);

        foreach ((string logical, string endpoint) in _devices)
        {
            int matches = live.Count(candidate =>
                string.Equals(candidate.Name, endpoint, StringComparison.OrdinalIgnoreCase));

            mappings.Add(new AudioDeviceMapping(
                logical,
                endpoint,
                matches switch
                {
                    1 => AudioMappingState.Ready,
                    0 => AudioMappingState.Missing,
                    _ => AudioMappingState.Ambiguous,
                },
                matches));
        }

        return new AudioSnapshot(Available: true, Problem: null, endpoints, mappings, PartyStatus());
    }

    /// <summary>
    /// What the Party group is doing, in the terms a fault report needs.
    /// </summary>
    /// <remarks>
    /// Failed is reported as failed. A mirror whose endpoint was unplugged has
    /// stopped, and reporting that as "not running" would make an unnoticed
    /// fault look like a mode nobody selected.
    /// </remarks>
    public AudioPartyStatus? PartyStatus()
    {
        if (_group is not PartySettings group)
        {
            return null;
        }

        AudioMirrorHealth? health = _party.Health;

        PartyState state = health switch
        {
            null => PartyState.Stopped,
            { Healthy: true } => PartyState.Running,
            _ => PartyState.Failed,
        };

        return new AudioPartyStatus(
            group.Name,
            group.Members,
            state,
            group.DelayMs,
            group.Gain,
            health,
            state == PartyState.Running ? null : _party.LastFailure);
    }

    /// <summary>
    /// Turns a configured name into a live endpoint ID, or refuses to guess.
    /// </summary>
    private string Resolve(string endpointName)
    {
        IReadOnlyList<AudioEndpoint> live = _endpoints.ActivePlayback();

        List<AudioEndpoint> matches =
        [
            .. live.Where(endpoint =>
                string.Equals(endpoint.Name, endpointName, StringComparison.OrdinalIgnoreCase)),
        ];

        if (matches.Count == 0)
        {
            // The names of what IS there, because the usual cause is a device
            // that is switched off and the second usual cause is a name that is
            // one character out, and the list tells the two apart at a glance.
            throw new InvalidOperationException(
                $"No active playback device is named '{endpointName}'. " +
                (live.Count == 0
                    ? "Windows reports no active playback devices at all."
                    : $"Active right now: {string.Join(", ", live.Select(endpoint => $"'{endpoint.Name}'"))}."));
        }

        if (matches.Count > 1)
        {
            // Windows does not enforce unique friendly names. Picking one would
            // send sound to whichever device happened to enumerate first, which
            // is a coin toss that looks like a working feature until the day it
            // lands the other way.
            throw new InvalidOperationException(
                $"{matches.Count} active playback devices are all named '{endpointName}', " +
                "so there is no way to tell which one was meant. " +
                "Rename one in Windows Sound settings.");
        }

        return matches[0].Id;
    }

    /// <summary>
    /// Reads one device, in either of the two forms it is written in.
    /// </summary>
    /// <remarks>
    /// TWO FORMS, ONE MEANING
    ///
    /// <c>"TV": "Digital Audio (HDMI)"</c> is what every existing document says and
    /// goes on meaning exactly what it meant. The longer form,
    /// <c>"TV": { "device": "...", "requires": { "display": "TVOnly" } }</c>, exists
    /// only to say the thing the short form cannot: that this particular endpoint
    /// does not exist until something else has happened.
    ///
    /// The same shape as a mode's optional resource, and for the same reason. A
    /// second document version would have made every configuration on every machine
    /// need rewriting in order to express something almost none of them want to say.
    /// </remarks>
    private static bool TryReadDevice(
        JsonProperty device,
        out string? logical,
        out string? endpoint,
        out IReadOnlyList<ResourceRequirement>? requires,
        out string? problem)
    {
        logical = null;
        endpoint = null;
        requires = [];

        if (string.IsNullOrWhiteSpace(device.Name))
        {
            problem = $"A device in '{SettingsPath}' has no name.";
            return false;
        }

        string named = device.Name.Trim();

        if (device.Value.ValueKind == JsonValueKind.Object)
        {
            if (!TryReadDetailedDevice(named, device.Value, out endpoint, out requires, out problem))
            {
                return false;
            }

            logical = named;
            return true;
        }

        if (device.Value.ValueKind != JsonValueKind.String)
        {
            problem =
                $"'{SettingsPath}.{named}' must be the exact device name Windows shows " +
                "in Sound settings, in quotes.";

            return false;
        }

        string? value = device.Value.GetString();

        if (string.IsNullOrWhiteSpace(value))
        {
            // An empty mapping is worse than a missing one: the logical name
            // exists, so every mode using it validates, and the failure waits
            // until a transition is half way through.
            problem =
                $"'{SettingsPath}.{named}' is empty. It needs the exact device name " +
                "Windows shows in Sound settings.";

            return false;
        }

        // Only the surrounding whitespace, which is what a copied name picks up.
        // Nothing about the name itself is interpreted: it is matched against
        // Windows' own string, and a provider that corrected it would be
        // guessing at what somebody's sound card is called.
        logical = named;
        endpoint = value.Trim();
        problem = null;
        return true;
    }

    /// <summary>Reads the long form of one device.</summary>
    private static bool TryReadDetailedDevice(
        string named,
        JsonElement written,
        out string? endpoint,
        out IReadOnlyList<ResourceRequirement>? requires,
        out string? problem)
    {
        endpoint = null;
        requires = [];

        string where = $"'{SettingsPath}.{named}'";

        foreach (JsonProperty field in written.EnumerateObject())
        {
            if (field.NameEquals("device"))
            {
                if (field.Value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(field.Value.GetString()))
                {
                    problem =
                        $"{where}.device must be the exact device name Windows shows " +
                        "in Sound settings, in quotes.";

                    return false;
                }

                endpoint = field.Value.GetString()!.Trim();
            }
            else if (field.NameEquals(TargetRequirements.FieldName))
            {
                if (!TargetRequirements.TryParse(where, ResourceKey, field.Value, out requires, out problem))
                {
                    return false;
                }
            }
            else
            {
                // Refused rather than ignored, like every other unknown field.
                problem = $"{where} does not understand '{field.Name}'. It takes 'device' and 'requires'.";
                return false;
            }
        }

        if (endpoint is null)
        {
            // An empty mapping is worse than a missing one: the logical name
            // exists, so every mode using it validates, and the failure waits
            // until a transition is half way through.
            problem =
                $"{where} says what it needs but not what it is. Add \"device\": " +
                "the exact name Windows shows in Sound settings.";

            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>
    /// Refuses two logical names pointing at one device.
    /// </summary>
    /// <remarks>
    /// The readback would have to pick one of them, and whichever it picked
    /// would make the other mode report the wrong current device forever. The
    /// second name also adds nothing: a mode that wants that device can use the
    /// first one.
    /// </remarks>
    private static bool TryRefuseSharedEndpoint(Dictionary<string, string> devices, out string? problem)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string logical, string endpoint) in devices)
        {
            if (!seen.TryAdd(endpoint, logical))
            {
                problem =
                    $"'{SettingsPath}' points both '{seen[endpoint]}' and '{logical}' at " +
                    $"'{endpoint}'. One device cannot be two, and nothing could say which " +
                    "of them the machine is currently in.";

                return false;
            }
        }

        problem = null;
        return true;
    }
}

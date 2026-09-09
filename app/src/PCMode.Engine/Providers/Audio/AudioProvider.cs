using System.Globalization;
using System.Text.Json;
using PCMode.Audio.Core.Models;

namespace PCMode.Engine;

/// <summary>
/// Chooses which device sound comes out of, and which Audio Target runs.
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
/// TWO KINDS OF VALUE, ONE RESOURCE
///
/// This resource takes a device alias or an Audio Target name, and a mode names
/// exactly one of them -- which is the whole of what makes them mutually
/// exclusive.
///
/// A device alias means "let Windows play into that device", and is a Windows
/// setting rather than a runtime: it is what PCMode has always done, it needs
/// nothing installed, and it goes on working on a machine with no PCMode audio
/// subsystem at all.
///
/// An Audio Target means "run this route graph in the PCMode audio engine". The
/// target is applied to <see cref="IAudioRuntime"/> and then the PCMode virtual
/// endpoint is made the machine's playback device, so applications render into
/// the engine and the engine fans them out to the target's routes. One route or
/// four, it is the same graph -- there is no separate synchronised-pair runtime,
/// and nothing is captured back off an endpoint any more.
///
/// A THIN ADAPTER, AND NOTHING BELOW IT
///
/// Everything about real-time audio -- PCM, DSP, routing, buffering, resampling,
/// clock correction, synchronisation, the driver transport -- belongs to
/// PCMode.Audio.Host and the engine inside it. This provider decides which
/// target is wanted, resolves its aliases to endpoints, hands it over, and
/// reports what came back.
/// </remarks>
public sealed class AudioProvider : IConfigurableProvider, IDependentProvider, IHealthReportingProvider
{
    /// <summary>The resource key a mode names.</summary>
    public const string ResourceKey = "audio";

    /// <summary>Where the device aliases live in the document.</summary>
    public const string SettingsPath = "providers.audio.devices";

    private readonly IAudioEndpoints _endpoints;
    private readonly IAudioRuntime _runtime;

    private IReadOnlyDictionary<string, string> _devices = new Dictionary<string, string>(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>The configured Audio Targets, keyed by the name a mode asks for.</summary>
    private IReadOnlyDictionary<string, AudioTargetSettings> _targets = new Dictionary<string, AudioTargetSettings>(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>The configured processing profiles, keyed by name.</summary>
    private IReadOnlyDictionary<string, AudioProcessingProfileSettings> _profiles = new Dictionary<
        string,
        AudioProcessingProfileSettings
    >(StringComparer.OrdinalIgnoreCase);

    /// <summary>The exact Windows name of the virtual endpoint, when one is written.</summary>
    private string? _virtualEndpointName;

    /// <summary>
    /// What each device needs before it exists, keyed by logical name.
    /// </summary>
    /// <remarks>
    /// Kept beside the endpoint names rather than folded in with them, because
    /// almost every device has nothing here and the mapping is what this provider
    /// is actually about.
    /// </remarks>
    private IReadOnlyDictionary<string, IReadOnlyList<ResourceRequirement>> _requires = new Dictionary<
        string,
        IReadOnlyList<ResourceRequirement>
    >(StringComparer.OrdinalIgnoreCase);

    public AudioProvider()
        : this(new CoreAudioEndpoints()) { }

    /// <param name="runtime">
    /// Who owns the audio Host. Shared across every engine this process builds,
    /// so that re-reading the configuration does not abandon a running target --
    /// see <see cref="AudioHostLink"/>. A provider given nothing gets
    /// <see cref="IAudioRuntime.None"/>: its device aliases work exactly as they
    /// always did, and its audio targets refuse to play rather than reaching for
    /// a Host this engine was never given.
    /// </param>
    public AudioProvider(IAudioEndpoints endpoints, IAudioRuntime? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        _endpoints = endpoints;
        _runtime = runtime ?? IAudioRuntime.None;
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
    /// An Audio Target needs whatever its routes need, including the optional
    /// ones. Nothing else would do: an optional route is one whose failure the
    /// target survives, not one whose television PCMode may leave switched off.
    /// </remarks>
    public IReadOnlyList<ResourceRequirement> RequirementsFor(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return [];
        }

        string wanted = target.Trim();

        if (_targets.TryGetValue(wanted, out AudioTargetSettings? configured))
        {
            return [.. configured.Routes.SelectMany(route => RequirementsFor(route.Output)).Distinct()];
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
    /// enough that a device appearing is noticed rather than waited out. It is
    /// also what makes an Audio Target converge rather than fail: applying the
    /// same target again is idempotent all the way down to the engine, so an
    /// attempt that got the routes running and lost the race for the default
    /// playback device finishes the job on the next one.
    ///
    /// It settles, briefly, because the default endpoint is contested. Windows
    /// moves the default itself when devices arrive, so a readback taken the
    /// instant after the change can report a device that is about to be replaced
    /// by whatever Windows preferred.
    /// </remarks>
    public ConvergencePolicy PolicyFor(string target) =>
        new()
        {
            Verify = true,
            MaxAttempts = 6,
            RetryInterval = TimeSpan.FromSeconds(2),
            PollInterval = TimeSpan.FromMilliseconds(250),
            Settle = TimeSpan.FromMilliseconds(500),
        };

    /// <summary>The configured logical names, in name order.</summary>
    public IReadOnlyList<string> DeviceNames => [.. _devices.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The configured Audio Targets, in name order.</summary>
    public IReadOnlyList<AudioTargetSettings> Targets =>
        [.. _targets.Values.OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>That Audio Target, or null for a name nothing defines.</summary>
    public AudioTargetSettings? Target(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _targets.TryGetValue(name.Trim(), out AudioTargetSettings? found)
            ? found
            : null;

    /// <summary>The configured processing profiles, in name order.</summary>
    public IReadOnlyList<AudioProcessingProfileSettings> Profiles =>
        [.. _profiles.Values.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>That processing profile, or null for a name nothing defines.</summary>
    public AudioProcessingProfileSettings? Profile(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && _profiles.TryGetValue(name.Trim(), out AudioProcessingProfileSettings? found)
            ? found
            : null;

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
    /// aliases and the Audio Targets.
    /// </summary>
    /// <remarks>
    /// What a settings screen must offer, and deliberately not the same list as
    /// <see cref="DeviceNames"/>. A target is a value of this resource like any
    /// alias -- that is the whole of what makes it and a single output mutually
    /// exclusive -- so a mode editor offering only the aliases would leave a
    /// configured target impossible to ask for without editing JSON.
    /// </remarks>
    public IReadOnlyList<string> SelectableNames
    {
        get
        {
            List<string> names = [.. _devices.Keys, .. _targets.Keys];
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
            _targets = new Dictionary<string, AudioTargetSettings>(StringComparer.OrdinalIgnoreCase);
            _profiles = new Dictionary<string, AudioProcessingProfileSettings>(StringComparer.OrdinalIgnoreCase);
            _virtualEndpointName = null;
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

        // Read after the devices, whatever order they were written in: a target
        // is defined in terms of them, so it cannot be checked until they are
        // all known. Profiles have no such dependency, but are still read
        // before targets are validated, since a route may name one.
        JsonElement? targets = null;
        string? virtualEndpoint = null;

        IReadOnlyDictionary<string, AudioProcessingProfileSettings>? profiles = null;

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.NameEquals("targets"))
            {
                targets = property.Value;
                continue;
            }

            if (property.NameEquals("virtualEndpoint"))
            {
                if (
                    property.Value.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(property.Value.GetString())
                )
                {
                    problem =
                        $"'{AudioVirtualEndpoint.SettingsPath}' must be the exact device name Windows shows "
                        + "in Sound settings for the PCMode virtual audio endpoint, in quotes.";

                    return false;
                }

                virtualEndpoint = property.Value.GetString()!.Trim();
                continue;
            }

            if (property.NameEquals("profiles"))
            {
                if (!AudioProcessingProfileSettings.TryParse(property.Value, out profiles, out problem))
                {
                    return false;
                }

                continue;
            }

            if (!property.NameEquals("devices"))
            {
                problem =
                    $"'providers.audio' does not understand '{property.Name}'. "
                    + "It takes 'devices', 'targets', 'profiles' and 'virtualEndpoint'.";

                return false;
            }

            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                problem =
                    $"'{SettingsPath}' must be an object mapping a name you choose to the exact "
                    + "device name Windows shows in Sound settings.";

                return false;
            }

            foreach (JsonProperty device in property.Value.EnumerateObject())
            {
                if (
                    !TryReadDevice(
                        device,
                        out string? logical,
                        out string? endpoint,
                        out IReadOnlyList<ResourceRequirement>? needs,
                        out problem
                    )
                )
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

        IReadOnlyDictionary<string, AudioProcessingProfileSettings> resolvedProfiles =
            profiles ?? new Dictionary<string, AudioProcessingProfileSettings>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyDictionary<string, AudioTargetSettings>? configured = new Dictionary<string, AudioTargetSettings>(
            StringComparer.OrdinalIgnoreCase
        );

        if (
            targets is JsonElement written
            && !AudioTargetSettings.TryParse(written, parsed, resolvedProfiles, out configured, out problem)
        )
        {
            return false;
        }

        _devices = parsed;
        _requires = required;
        _targets = configured!;
        _profiles = resolvedProfiles;
        _virtualEndpointName = virtualEndpoint;
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
                $"'{value.Trim()}' cannot be applied because no audio devices are defined. "
                + $"Add one under '{SettingsPath}'.";

            return false;
        }

        if (!_devices.ContainsKey(value.Trim()) && !_targets.ContainsKey(value.Trim()))
        {
            problem =
                $"'{value.Trim()}' is not an audio device. "
                + $"'{SettingsPath}' defines: {string.Join(", ", SelectableNames)}.";

            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>
    /// The logical name of whatever is currently playing, when it has one.
    /// </summary>
    /// <remarks>
    /// Null covers several genuinely different situations -- Core Audio could not
    /// be reached, there is no default at all, and the default is a device
    /// nothing in the configuration names -- and they are folded together on
    /// purpose. To the engine null means one thing, "apply it anyway", and that
    /// is the correct answer to all of them. Telling them apart is what
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

        // An Audio Target is what the machine is doing only while BOTH halves of
        // it are true: the Host is running that target, and the PCMode virtual
        // endpoint is still what Windows plays into. Either one alone is not the
        // feature.
        //
        // Which is what stops three different things from being mistaken for a
        // running target -- a Host that failed with its endpoint, a fresh Host
        // that has never been given one, and something else having moved the
        // default out from under an engine still faithfully rendering to routes
        // nothing is feeding.
        //
        // Asked of the Host only when the virtual endpoint is the default. This
        // is on the convergence poll, and a machine playing through its speakers
        // has no reason to open a pipe four times a second to be told so.
        if (AudioVirtualEndpoint.IsVirtualEndpointName(current.Name, _virtualEndpointName) && _targets.Count > 0)
        {
            AudioRuntimeStatus runtime = _runtime.Refresh();

            foreach (AudioTargetSettings target in _targets.Values)
            {
                if (runtime.IsRunning(target.Name))
                {
                    return target.Name;
                }
            }
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
        string wanted = value?.Trim() ?? string.Empty;

        if (_targets.TryGetValue(wanted, out AudioTargetSettings? target))
        {
            StartTarget(target, cancellationToken);
            return;
        }

        if (!_devices.TryGetValue(wanted, out string? device))
        {
            // Unreachable through the engine, which validates first. Kept so a
            // direct caller cannot send sound to a device nobody named.
            throw new ArgumentException($"'{value}' is not an audio device.", nameof(value));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Before the default moves, not after. A target left running would go on
        // rendering to its routes, so the sound would arrive at the new device
        // and at the old target's outputs, with nothing on screen admitting to it.
        //
        // Skipped entirely when nothing here has ever defined an Audio Target:
        // there is then nothing that could be running, and a machine using only
        // direct outputs must not depend on the Native Audio runtime at all --
        // not even to be told it has nothing to stop.
        if (_targets.Count > 0)
        {
            _runtime.Stop();
        }

        _endpoints.MakeDefault(Resolve(device, _endpoints.ActivePlayback()));
    }

    /// <summary>
    /// Starts the routes, then points Windows at the engine.
    /// </summary>
    /// <remarks>
    /// THE ORDER IS THE FEATURE
    ///
    /// The routes are started first and the PCMode virtual endpoint is made the
    /// default last. The other way round would hand Windows an endpoint whose
    /// engine is not running yet, and the machine would go silent for as long as
    /// that took.
    ///
    /// The default is read back rather than assumed. Changing it is the one
    /// thing PCMode does through an interface Windows does not document, and a
    /// target built on a default that did not take is an engine rendering
    /// silence to two speakers.
    ///
    /// Endpoint volumes are set before the routes start, so that a target
    /// configured quietly does not announce itself at whatever the endpoints
    /// were left at.
    ///
    /// EVERY ALIAS IS RESOLVED FROM ONE ENUMERATION
    ///
    /// Devices appear and disappear, and two routes resolved from two separate
    /// enumerations could disagree about what is present. One snapshot is taken
    /// and every route, and the virtual endpoint, is resolved from it.
    ///
    /// WHAT IT PUTS BACK
    ///
    /// A failure before the routes start rolls back whichever endpoint volumes
    /// were actually changed. A failure after them does not stop the engine: the
    /// Host has been told what PCMode wants and owns converging on it, the
    /// default playback device is put back so nothing plays into an endpoint
    /// PCMode is not using, and the next attempt -- this resource retries six
    /// times -- finishes the job. Applying the same target again is idempotent
    /// all the way down, so that costs nothing and restarts nothing.
    /// </remarks>
    private void StartTarget(AudioTargetSettings target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<AudioEndpoint> active = _endpoints.ActivePlayback();
        string virtualEndpointId = AudioVirtualEndpoint.Resolve(active, _virtualEndpointName);

        AudioTarget composed = target.Compose(
            output => Resolve(_devices[output], active),
            profileName => _profiles[profileName].ToProfile()
        );

        // Read before anything changes, so a failure partway through has
        // something to restore. Best-effort: a read that cannot be taken simply
        // leaves nothing to roll that particular thing back to.
        AudioEndpoint? previousDefault = SafeReadDefault();
        var restore = new List<(string EndpointId, double Level)>();

        try
        {
            foreach (AudioRouteSettings route in target.Routes)
            {
                if (route.Volume is not double level)
                {
                    // Omission means "leave it alone". A target that silently
                    // normalised every endpoint it touched would undo whatever
                    // the user last did with their own volume keys.
                    continue;
                }

                string endpointId = Resolve(_devices[route.Output], active);

                if (SafeReadVolume(endpointId) is double previous)
                {
                    restore.Add((endpointId, previous));
                }

                SetVolume(route.Output, endpointId, level);
            }

            cancellationToken.ThrowIfCancellationRequested();

            _runtime.Apply(composed, cancellationToken);
        }
        catch
        {
            RollBackVolumes(restore);
            throw;
        }

        try
        {
            _endpoints.MakeDefault(virtualEndpointId);

            cancellationToken.ThrowIfCancellationRequested();

            AudioEndpoint? now = _endpoints.DefaultPlayback();

            if (now is null || !string.Equals(now.Id, virtualEndpointId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Windows did not accept the PCMode virtual audio endpoint as the playback device, so "
                        + $"'{target.Name}' has nothing to play. It reports "
                        + (now is null ? "no default playback device." : $"'{now.Name}'.")
                );
            }
        }
        catch
        {
            // The routes are running and the engine keeps them; only the
            // playback device is put back, so that Windows is not left pointing
            // at an endpoint this attempt could not finish setting up.
            if (previousDefault is not null)
            {
                TryRestore(() => _endpoints.MakeDefault(previousDefault.Id));
            }

            RollBackVolumes(restore);
            throw;
        }
    }

    /// <summary>
    /// Nudges one running route's delay, gain, endpoint volume and processing
    /// profile, without reapplying the whole target.
    /// </summary>
    /// <remarks>
    /// A thin adapter over <see cref="IAudioRuntime.Recalibrate"/>, exactly
    /// like <see cref="StartTarget"/> is over <see cref="IAudioRuntime.Apply"/>:
    /// this resolves the route's alias to the live endpoint it names right
    /// now, and hands the rest to the Host. Whether the target is actually
    /// running to be calibrated is the Host's own answer, not a guess made
    /// here from a stale snapshot.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="targetName"/> is not a configured Audio Target, or
    /// <paramref name="routeOutput"/> is not one of its outputs.
    /// </exception>
    public void Recalibrate(
        string targetName,
        string routeOutput,
        TimeSpan delay,
        double gain,
        double? volume,
        string? profileName,
        CancellationToken cancellationToken
    )
    {
        if (Target(targetName) is not AudioTargetSettings target)
        {
            throw new ArgumentException($"'{targetName}' is not a configured audio target.", nameof(targetName));
        }

        if (!target.Uses(routeOutput))
        {
            throw new ArgumentException(
                $"'{routeOutput}' is not one of '{targetName}''s outputs.",
                nameof(routeOutput)
            );
        }

        ParametricEqProfile? parametricEq = profileName is null ? null : _profiles[profileName].ToProfile();

        string endpointId = Resolve(_devices[routeOutput], _endpoints.ActivePlayback());

        _runtime.Recalibrate(
            new AudioEndpointId(endpointId),
            volume is double level ? (float)level : 1.0f,
            muted: gain <= 0,
            delay,
            new AudioProcessingProfile(AudioTargetSettings.GainDecibels(gain), parametricEq),
            cancellationToken
        );
    }

    /// <summary>
    /// Puts back whichever endpoint volumes a failed start had changed,
    /// best-effort.
    /// </summary>
    /// <remarks>
    /// Each is restored independently, and a failure restoring one does not stop
    /// the others: the exception this is called from is what the caller needs to
    /// see, and there is nothing more useful to do about an endpoint that will
    /// not take its own former value back.
    /// </remarks>
    private void RollBackVolumes(IReadOnlyList<(string EndpointId, double Level)> restore)
    {
        foreach ((string endpointId, double level) in restore)
        {
            TryRestore(() => _endpoints.SetVolume(endpointId, level));
        }
    }

    private static void TryRestore(Action restore)
    {
        try
        {
            restore();
        }
        catch (Exception) { }
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

    /// <summary>Moves one endpoint's volume slider, when the route asked for one.</summary>
    private void SetVolume(string logical, string endpointId, double level)
    {
        try
        {
            _endpoints.SetVolume(endpointId, level);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"The volume of '{logical}' could not be set. {exception.Message}",
                exception
            );
        }
    }

    /// <summary>
    /// The audio engine's own report, for a health page.
    /// </summary>
    /// <remarks>
    /// Null for a target that is not a configured Audio Target -- an ordinary
    /// device has nothing this provider's <see cref="Read"/> answer does not
    /// already say.
    /// </remarks>
    public string? HealthDetail(string target)
    {
        if (Target(target) is not AudioTargetSettings configured)
        {
            return null;
        }

        AudioRuntimeStatus runtime = _runtime.Status;

        if (!runtime.Reachable)
        {
            return $"Audio target '{configured.Name}': the PCMode audio Host is not running. {runtime.Problem}";
        }

        if (runtime.Health is not AudioEngineHealth health)
        {
            return $"Audio target '{configured.Name}': the PCMode audio Host is running and reported no Health.";
        }

        var written = new List<string>
        {
            $"Audio target '{configured.Name}': {health.State}, engine {health.EngineState}",
            $"Host pid {runtime.Host?.ProcessId.ToString(CultureInfo.InvariantCulture) ?? "?"}",
            $"Active '{health.ActiveTargetName ?? "none"}'",
        };

        if (health.Input is AudioInputHealth input)
        {
            written.Add(
                $"Input {input.Kind}/{input.State}"
                    + (
                        input.Format is AudioFormat format
                            ? $" {format.SampleRate} Hz x{format.ChannelCount}"
                            : string.Empty
                    )
            );
        }

        foreach (AudioRouteHealth route in health.Routes)
        {
            written.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Route {route.EndpointId} {route.State}/{route.EndpointState} "
                        + $"({route.Requirement}), delay {route.ConfiguredDelay.TotalMilliseconds:0} ms, "
                        + $"offset {route.ClockOffsetMilliseconds ?? 0:0.0} ms, drift {route.DriftCorrectionPpm:0.0} ppm, "
                        + $"underruns {route.UnderrunCount}, resyncs {route.ResyncCount}"
                ) + (route.LastFailure is null ? string.Empty : $", last failure: {route.LastFailure.Message}")
            );
        }

        if (health.LastFailure is AudioFailure failure)
        {
            written.Add($"Last failure: {failure.Type} {failure.Message}");
        }

        return string.Join(". ", written) + ".";
    }

    /// <summary>
    /// Each currently running Audio Target route's waveform envelope. Never
    /// touches Core Audio's own device enumeration, unlike <see cref="Discover"/>,
    /// so a waveform redraw loop can call this many times a second.
    /// </summary>
    public Task<IReadOnlyList<AudioRouteWaveform>> WaveformAsync(CancellationToken cancellationToken) =>
        _runtime.WaveformAsync(cancellationToken);

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
        AudioRuntimeStatus runtime = _runtime.Refresh();

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
            var unknown = _devices.ToDictionary(
                device => device.Key,
                device => new AudioDeviceMapping(device.Key, device.Value, AudioMappingState.Unknown, Matches: 0),
                StringComparer.OrdinalIgnoreCase
            );

            return new AudioSnapshot(
                Available: false,
                exception.Message,
                [],
                [.. unknown.Values],
                [.. Targets.Select(target => Describe(target, unknown, runtime))],
                runtime
            );
        }

        var endpoints = new List<AudioEndpointInfo>(live.Count);

        foreach (AudioEndpoint endpoint in live)
        {
            endpoints.Add(
                new AudioEndpointInfo(
                    endpoint.Name,
                    current is not null && string.Equals(endpoint.Id, current.Id, StringComparison.Ordinal),
                    AudioVirtualEndpoint.IsVirtualEndpointName(endpoint.Name, _virtualEndpointName)
                )
            );
        }

        var mappings = new Dictionary<string, AudioDeviceMapping>(StringComparer.OrdinalIgnoreCase);

        foreach ((string logical, string endpoint) in _devices)
        {
            int matches = live.Count(candidate =>
                string.Equals(candidate.Name, endpoint, StringComparison.OrdinalIgnoreCase)
            );

            mappings[logical] = new AudioDeviceMapping(
                logical,
                endpoint,
                matches switch
                {
                    1 => AudioMappingState.Ready,
                    0 => AudioMappingState.Missing,
                    _ => AudioMappingState.Ambiguous,
                },
                matches
            );
        }

        return new AudioSnapshot(
            Available: true,
            Problem: null,
            endpoints,
            [.. mappings.Values],
            [.. Targets.Select(target => Describe(target, mappings, runtime))],
            runtime
        );
    }

    /// <summary>
    /// One configured target as it stands, in the terms a fault report needs.
    /// </summary>
    /// <remarks>
    /// Failed is reported as failed, and degraded as degraded. A target whose
    /// optional route was unplugged is still playing through the other one, and
    /// calling that either "running" or "stopped" would hide the half of it that
    /// somebody needs to fix.
    /// </remarks>
    private AudioTargetStatus Describe(
        AudioTargetSettings target,
        IReadOnlyDictionary<string, AudioDeviceMapping> mappings,
        AudioRuntimeStatus runtime
    )
    {
        bool active = runtime.IsRunning(target.Name);

        IReadOnlyList<AudioRouteHealth> routeHealth =
            active && runtime.Health is AudioEngineHealth reported ? reported.Routes : [];

        var routes = new List<AudioTargetRouteStatus>(target.Routes.Count);

        for (int index = 0; index < target.Routes.Count; index++)
        {
            AudioRouteSettings route = target.Routes[index];

            AudioDeviceMapping? mapping = mappings.GetValueOrDefault(route.Output);

            // Positional, because a target's routes are handed to the engine in
            // configuration order and come back in it. Matching on endpoint ID
            // would be stronger and is not available here: this side deliberately
            // never stores one.
            AudioRouteHealth? health = index < routeHealth.Count ? routeHealth[index] : null;

            routes.Add(
                new AudioTargetRouteStatus(
                    route.Output,
                    mapping?.EndpointName ?? string.Empty,
                    mapping?.State ?? AudioMappingState.Unknown,
                    route.DelayMs,
                    route.Gain,
                    route.Volume,
                    route.Required,
                    route.Profile,
                    health?.State,
                    health?.LastFailure?.Message
                )
            );
        }

        AudioTargetState state =
            !runtime.Reachable ? AudioTargetState.Unavailable
            : !active ? AudioTargetState.Stopped
            : runtime.Health?.State switch
            {
                AudioHealthState.Healthy => AudioTargetState.Running,
                AudioHealthState.Degraded => AudioTargetState.Degraded,
                AudioHealthState.Failed => AudioTargetState.Failed,
                _ => AudioTargetState.Stopped,
            };

        string? problem = state switch
        {
            AudioTargetState.Unavailable => runtime.Problem,
            AudioTargetState.Failed or AudioTargetState.Degraded => runtime.Health?.LastFailure?.Message
                ?? routes.FirstOrDefault(route => route.Problem is not null)?.Problem,
            _ => null,
        };

        return new AudioTargetStatus(target.Name, routes, state, problem, active ? HealthDetail(target.Name) : null);
    }

    /// <summary>
    /// Turns a configured name into a live endpoint ID, or refuses to guess.
    /// </summary>
    private static string Resolve(string endpointName, IReadOnlyList<AudioEndpoint> active)
    {
        List<AudioEndpoint> matches =
        [
            .. active.Where(endpoint => string.Equals(endpoint.Name, endpointName, StringComparison.OrdinalIgnoreCase)),
        ];

        if (matches.Count == 0)
        {
            // The names of what IS there, because the usual cause is a device
            // that is switched off and the second usual cause is a name that is
            // one character out, and the list tells the two apart at a glance.
            throw new InvalidOperationException(
                $"No active playback device is named '{endpointName}'. "
                    + (
                        active.Count == 0
                            ? "Windows reports no active playback devices at all."
                            : $"Active right now: {string.Join(", ", active.Select(endpoint => $"'{endpoint.Name}'"))}."
                    )
            );
        }

        if (matches.Count > 1)
        {
            // Windows does not enforce unique friendly names. Picking one would
            // send sound to whichever device happened to enumerate first, which
            // is a coin toss that looks like a working feature until the day it
            // lands the other way.
            throw new InvalidOperationException(
                $"{matches.Count} active playback devices are all named '{endpointName}', "
                    + "so there is no way to tell which one was meant. "
                    + "Rename one in Windows Sound settings."
            );
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
        out string? problem
    )
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
                $"'{SettingsPath}.{named}' must be the exact device name Windows shows "
                + "in Sound settings, in quotes.";

            return false;
        }

        string? value = device.Value.GetString();

        if (string.IsNullOrWhiteSpace(value))
        {
            // An empty mapping is worse than a missing one: the logical name
            // exists, so every mode using it validates, and the failure waits
            // until a transition is half way through.
            problem =
                $"'{SettingsPath}.{named}' is empty. It needs the exact device name "
                + "Windows shows in Sound settings.";

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
        out string? problem
    )
    {
        endpoint = null;
        requires = [];

        string where = $"'{SettingsPath}.{named}'";

        foreach (JsonProperty field in written.EnumerateObject())
        {
            if (field.NameEquals("device"))
            {
                if (field.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.Value.GetString()))
                {
                    problem =
                        $"{where}.device must be the exact device name Windows shows "
                        + "in Sound settings, in quotes.";

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
                $"{where} says what it needs but not what it is. Add \"device\": "
                + "the exact name Windows shows in Sound settings.";

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
                    $"'{SettingsPath}' points both '{seen[endpoint]}' and '{logical}' at "
                    + $"'{endpoint}'. One device cannot be two, and nothing could say which "
                    + "of them the machine is currently in.";

                return false;
            }
        }

        problem = null;
        return true;
    }
}

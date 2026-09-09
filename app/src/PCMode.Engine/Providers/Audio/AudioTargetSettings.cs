using System.Globalization;
using System.Text.Json;
using PCMode.Audio.Core.Models;

namespace PCMode.Engine;

/// <summary>
/// One output inside an Audio Target: which configured device it plays through,
/// and how that one path is treated.
/// </summary>
/// <remarks>
/// Everything here is per-route, because that is what the audio engine is. A
/// route owns its own endpoint, buffering, timing, resampling, gain and DSP
/// state -- see PCMode.Audio.Engine -- so a single output and a synchronised
/// pair are the same thing with one route or two, rather than two features.
/// </remarks>
/// <param name="Output">
/// The audio device alias this route plays through, defined in
/// 'providers.audio.devices'. Named by alias rather than by endpoint ID for the
/// same reason a mode is: the alias already carries the "requires" that says a
/// television's endpoint does not exist until its display does, and an ID would
/// need its own answer for that on day one. The ID is resolved from the alias
/// the instant before the target is applied.
/// </param>
/// <param name="Delay">
/// How far behind the source timeline this route runs. Alignment is per-route
/// now: whichever output is physically slower in the room is left at zero, and
/// the faster one is held back to meet it.
/// </param>
/// <param name="Gain">
/// Linear gain on this route, 1.0 being unchanged. Converted to the decibels
/// the engine's processing chain works in when the target is built; zero is a
/// muted route rather than negative infinity.
/// </param>
/// <param name="Volume">
/// The Windows endpoint volume slider for this route's device, 0 to 1, or null
/// to leave it wherever the user last put it. Deliberately not the same thing
/// as <see cref="Gain"/>: this is the level a person can see in Sound settings
/// and it applies to the device, while gain is applied inside the route.
/// </param>
/// <param name="Required">
/// Whether the whole target fails when this route cannot run. An optional route
/// that fails degrades the target and leaves the healthy routes playing.
/// </param>
/// <param name="Profile">
/// The named PCMode processing profile this route's parametric EQ comes from,
/// defined in 'providers.audio.profiles', or null for a flat, bypassed route.
/// Deliberately separate from <see cref="Gain"/>: gain is a plain linear trim a
/// person types for this one route, while a profile is a named EQ curve meant
/// to be reused across routes and targets.
/// </param>
public sealed record AudioRouteSettings(
    string Output,
    TimeSpan Delay,
    double Gain,
    double? Volume,
    bool Required,
    string? Profile = null
)
{
    public int DelayMs => (int)Math.Round(Delay.TotalMilliseconds);
}

/// <summary>
/// One named Audio Target: what a mode asks for, and the routes it is made of.
/// </summary>
/// <remarks>
/// WHAT REPLACED PARTY
///
/// A Party group was two endpoints, one of which Windows played through while
/// the other received a delayed copy captured back off it. An Audio Target is
/// one source timeline -- the PCMode virtual endpoint -- fanned out to as many
/// independent routes as it names. A single output is one route and a
/// synchronised pair is two; there is no separate Party runtime any more, and
/// no reference output, because nothing is captured from anything.
///
/// The paid-v1 GUI offers one or two routes. The model does not enforce that:
/// the engine underneath is route-based, and a limit written into the parser
/// would be a product decision recorded in the wrong place.
///
/// NAMED LIKE A DEVICE, BECAUSE IT IS ONE VALUE OF ONE RESOURCE
///
/// A target's name is a value the audio resource takes, beside the device
/// aliases -- and that is the whole of what makes a target and a single output
/// mutually exclusive. A mode asks for one audio value, so applying a device
/// alias stops the target runtime without anything having to coordinate it.
/// </remarks>
public sealed record AudioTargetSettings(string Name, IReadOnlyList<AudioRouteSettings> Routes)
{
    /// <summary>Where these live in the document.</summary>
    public const string SettingsPath = "providers.audio.targets";

    /// <summary>
    /// The longest delay a route may be given.
    /// </summary>
    /// <remarks>
    /// The engine's own bound -- see <see cref="AudioRoute.MaximumDelayMilliseconds"/> --
    /// restated here so a document that exceeds it is refused when it is read
    /// rather than when somebody asks for it. Every real speaker pair in one
    /// room lands between 20 and 300 ms.
    /// </remarks>
    public const int MaxDelayMs = AudioRoute.MaximumDelayMilliseconds;

    /// <summary>
    /// The most a route may be amplified.
    /// </summary>
    /// <remarks>
    /// +6 dB. Anything above unity can clip a mix already near full scale, so
    /// the bound is what stops somebody typing 20 and wondering why their
    /// speakers are distorting. Attenuation is unrestricted: 0 is a legitimate
    /// way to mute one route while keeping the target.
    /// </remarks>
    public const double MaxGain = 2.0;

    /// <summary>The route outputs, in the order the document wrote them.</summary>
    public IReadOnlyList<string> Outputs => [.. Routes.Select(route => route.Output)];

    /// <summary>Whether this target names <paramref name="output"/> as one of its routes.</summary>
    public bool Uses(string output) =>
        Routes.Any(route => string.Equals(route.Output, output, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The engine-facing target, with every alias resolved to a live endpoint ID.
    /// </summary>
    /// <remarks>
    /// The one place a PCMode alias becomes a Core Audio endpoint, and it
    /// happens immediately before the target is handed to the Host. Endpoint IDs
    /// are never stored: the HDMI endpoint exists only while the television is
    /// awake, so an ID kept from an earlier call is a handle to a device that
    /// may not be there.
    /// </remarks>
    /// <param name="endpointIdFor">
    /// Turns one alias into the endpoint ID it unambiguously names right now, or
    /// throws saying why it cannot.
    /// </param>
    /// <param name="parametricEqFor">
    /// Turns a route's named profile into the engine-facing parametric EQ it
    /// resolves to. Called only for a route that names one; a route with no
    /// profile stays flat.
    /// </param>
    public AudioTarget Compose(
        Func<string, string> endpointIdFor,
        Func<string, ParametricEqProfile>? parametricEqFor = null
    )
    {
        ArgumentNullException.ThrowIfNull(endpointIdFor);

        var routes = new AudioRoute[Routes.Count];
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int index = 0; index < Routes.Count; index++)
        {
            AudioRouteSettings route = Routes[index];
            string endpointId = endpointIdFor(route.Output);

            if (!seen.TryAdd(endpointId, route.Output))
            {
                // Two aliases that resolved to one endpoint. The engine refuses
                // a duplicate endpoint outright; caught here so the message can
                // name the two aliases rather than an endpoint ID.
                throw new InvalidOperationException(
                    $"'{seen[endpointId]}' and '{route.Output}' are the same playback device right now, "
                        + $"so '{Name}' would send the same sound to it twice."
                );
            }

            ParametricEqProfile? parametricEq = route.Profile is null
                ? null
                : (
                    parametricEqFor
                    ?? throw new InvalidOperationException(
                        $"'{route.Output}' names the processing profile '{route.Profile}', but nothing was given to resolve it."
                    )
                )(route.Profile);

            routes[index] = new AudioRoute(
                new AudioEndpointId(endpointId),
                route.Required ? AudioRouteRequirement.Required : AudioRouteRequirement.Optional,
                volume: 1.0f,
                muted: route.Gain <= 0,
                delay: route.Delay,
                processing: new AudioProcessingProfile(GainDecibels(route.Gain), parametricEq)
            );
        }

        return new AudioTarget(Name, Name, routes);
    }

    /// <summary>
    /// A linear gain as the decibels the engine's processing chain applies.
    /// </summary>
    /// <remarks>
    /// Zero is not -infinity dB: it is a muted route, and the route carries that
    /// as a flag so the chain can short its output rather than multiply by a
    /// number no float can hold.
    /// </remarks>
    public static float GainDecibels(double gain) =>
        gain <= 0 ? 0f : (float)Math.Clamp(20.0 * Math.Log10(gain), -24.0, 24.0);

    /// <summary>
    /// Reads the "targets" object, or says what is wrong with it.
    /// </summary>
    /// <param name="devices">
    /// The device aliases defined alongside them. Every route has to name one:
    /// a target naming an output nothing defines would validate and then fail at
    /// the moment somebody asked for it.
    /// </param>
    public static bool TryParse(
        JsonElement written,
        IReadOnlyDictionary<string, string> devices,
        IReadOnlyDictionary<string, AudioProcessingProfileSettings> profiles,
        out IReadOnlyDictionary<string, AudioTargetSettings>? targets,
        out string? problem
    )
    {
        ArgumentNullException.ThrowIfNull(profiles);

        targets = null;

        if (written.ValueKind != JsonValueKind.Object)
        {
            problem =
                $"'{SettingsPath}' must be an object mapping a name a mode asks for to the outputs it "
                + "plays through.";

            return false;
        }

        var parsed = new Dictionary<string, AudioTargetSettings>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty entry in written.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                problem = $"An audio target in '{SettingsPath}' has no name.";
                return false;
            }

            string name = entry.Name.Trim();

            if (devices.ContainsKey(name))
            {
                // A mode names one value for the audio resource, and it would
                // mean two different things.
                problem =
                    $"'{SettingsPath}.{name}' is also an audio device in "
                    + $"'{AudioProvider.SettingsPath}'. Give the target a name of its own.";

                return false;
            }

            if (!TryReadTarget(name, entry.Value, devices, profiles, out AudioTargetSettings? target, out problem))
            {
                return false;
            }

            if (!parsed.TryAdd(name, target!))
            {
                // Case-insensitively, because that is how a mode looks one up.
                problem = $"'{SettingsPath}' defines '{name}' more than once.";
                return false;
            }
        }

        targets = parsed;
        problem = null;
        return true;
    }

    private static bool TryReadTarget(
        string name,
        JsonElement written,
        IReadOnlyDictionary<string, string> devices,
        IReadOnlyDictionary<string, AudioProcessingProfileSettings> profiles,
        out AudioTargetSettings? target,
        out string? problem
    )
    {
        target = null;
        string where = $"'{SettingsPath}.{name}'";

        if (written.ValueKind != JsonValueKind.Object)
        {
            problem = $"{where} must be an object with an 'outputs' list.";
            return false;
        }

        JsonElement? outputs = null;

        foreach (JsonProperty field in written.EnumerateObject())
        {
            if (field.NameEquals("outputs"))
            {
                outputs = field.Value;
                continue;
            }

            // Refused rather than ignored, like every other unknown field: a
            // document whose fields were silently dropped is a configuration the
            // user believes they have and does not.
            problem = $"{where} does not understand '{field.Name}'. It takes 'outputs'.";
            return false;
        }

        if (outputs is not JsonElement list || list.ValueKind != JsonValueKind.Array)
        {
            problem =
                $"{where} needs 'outputs': a list of the outputs it plays through, each "
                + "{ \"output\": \"...\" } with optional 'delayMs', 'gain', 'volume' and 'required'.";

            return false;
        }

        var routes = new List<AudioRouteSettings>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement element in list.EnumerateArray())
        {
            if (!TryReadRoute(where, element, devices, profiles, out AudioRouteSettings? route, out problem))
            {
                return false;
            }

            if (!seen.Add(route!.Output))
            {
                // The same alias twice is the same endpoint twice, which the
                // engine refuses outright. Caught here so the message names the
                // alias rather than an endpoint ID.
                problem = $"{where} names '{route.Output}' more than once.";
                return false;
            }

            routes.Add(route);
        }

        if (routes.Count == 0)
        {
            problem = $"{where} names no outputs. An audio target needs at least one.";
            return false;
        }

        if (!routes.Any(route => route.Required))
        {
            // Every route optional means nothing can fail the target, and a
            // target that cannot fail is one whose failure nobody is told about.
            problem = $"{where} makes every output optional. At least one has to be required.";
            return false;
        }

        target = new AudioTargetSettings(name, routes);
        problem = null;
        return true;
    }

    private static bool TryReadRoute(
        string where,
        JsonElement written,
        IReadOnlyDictionary<string, string> devices,
        IReadOnlyDictionary<string, AudioProcessingProfileSettings> profiles,
        out AudioRouteSettings? route,
        out string? problem
    )
    {
        route = null;

        // The short form: just the alias. What almost every route is.
        if (written.ValueKind == JsonValueKind.String)
        {
            return TryBuildRoute(
                where,
                written.GetString(),
                0,
                1.0,
                null,
                required: true,
                profile: null,
                devices,
                profiles,
                out route,
                out problem
            );
        }

        if (written.ValueKind != JsonValueKind.Object)
        {
            problem =
                $"{where}.outputs takes either an output name or "
                + "{ \"output\": \"...\", \"delayMs\": ..., \"gain\": ..., \"volume\": ..., \"required\": ..., \"profile\": ... }.";

            return false;
        }

        string? output = null;
        int delayMs = 0;
        double gain = 1.0;
        double? volume = null;
        bool required = true;
        string? profile = null;

        foreach (JsonProperty field in written.EnumerateObject())
        {
            if (field.NameEquals("output"))
            {
                output = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString()?.Trim() : null;

                if (string.IsNullOrWhiteSpace(output))
                {
                    problem = $"{where}.outputs has an entry whose 'output' is not an output name.";
                    return false;
                }
            }
            else if (field.NameEquals("delayMs"))
            {
                if (
                    !TryReadNumber(field.Value, $"{where}.outputs.delayMs", 0, MaxDelayMs, out double read, out problem)
                )
                {
                    return false;
                }

                delayMs = (int)Math.Round(read);
            }
            else if (field.NameEquals("gain"))
            {
                if (!TryReadNumber(field.Value, $"{where}.outputs.gain", 0, MaxGain, out gain, out problem))
                {
                    return false;
                }
            }
            else if (field.NameEquals("volume"))
            {
                if (!TryReadNumber(field.Value, $"{where}.outputs.volume", 0, 1, out double level, out problem))
                {
                    return false;
                }

                volume = level;
            }
            else if (field.NameEquals("required"))
            {
                if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    problem = $"{where}.outputs.required must be true or false.";
                    return false;
                }

                required = field.Value.GetBoolean();
            }
            else if (field.NameEquals("profile"))
            {
                profile = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString()?.Trim() : null;

                if (string.IsNullOrWhiteSpace(profile))
                {
                    problem = $"{where}.outputs has an entry whose 'profile' is not a profile name.";
                    return false;
                }
            }
            else
            {
                problem =
                    $"{where}.outputs does not understand '{field.Name}'. An output takes 'output', "
                    + "'delayMs', 'gain', 'volume', 'required' and 'profile'.";

                return false;
            }
        }

        return TryBuildRoute(
            where,
            output,
            delayMs,
            gain,
            volume,
            required,
            profile,
            devices,
            profiles,
            out route,
            out problem
        );
    }

    private static bool TryBuildRoute(
        string where,
        string? output,
        int delayMs,
        double gain,
        double? volume,
        bool required,
        string? profile,
        IReadOnlyDictionary<string, string> devices,
        IReadOnlyDictionary<string, AudioProcessingProfileSettings> profiles,
        out AudioRouteSettings? route,
        out string? problem
    )
    {
        route = null;

        if (string.IsNullOrWhiteSpace(output))
        {
            problem = $"{where}.outputs has an entry with no output name.";
            return false;
        }

        string named = output.Trim();

        if (!devices.TryGetValue(named, out _))
        {
            problem =
                $"{where} names '{named}', which '{AudioProvider.SettingsPath}' does not define. "
                + (
                    devices.Count == 0
                        ? "No audio devices are defined at all."
                        : $"Defined: {string.Join(", ", devices.Keys.Order(StringComparer.OrdinalIgnoreCase))}."
                );

            return false;
        }

        // The alias exactly as the devices section spells it, so that everything
        // downstream compares one spelling rather than whichever case the target
        // happened to be written in.
        string canonical = devices.Keys.First(device =>
            string.Equals(device, named, StringComparison.OrdinalIgnoreCase)
        );

        string? canonicalProfile = null;

        if (profile is not null)
        {
            if (!profiles.TryGetValue(profile, out AudioProcessingProfileSettings? found))
            {
                problem =
                    $"{where} names the processing profile '{profile}', which '{AudioProcessingProfileSettings.SettingsPath}' "
                    + "does not define. "
                    + (
                        profiles.Count == 0
                            ? "No processing profiles are defined at all."
                            : $"Defined: {string.Join(", ", profiles.Keys.Order(StringComparer.OrdinalIgnoreCase))}."
                    );

                return false;
            }

            canonicalProfile = found.Name;
        }

        route = new AudioRouteSettings(
            canonical,
            TimeSpan.FromMilliseconds(delayMs),
            gain,
            volume,
            required,
            canonicalProfile
        );
        problem = null;
        return true;
    }

    private static bool TryReadNumber(
        JsonElement written,
        string where,
        double lowest,
        double highest,
        out double value,
        out string? problem
    )
    {
        value = 0;

        if (
            written.ValueKind != JsonValueKind.Number
            || !written.TryGetDouble(out value)
            || double.IsNaN(value)
            || double.IsInfinity(value)
        )
        {
            problem = $"'{where}' must be a number.";
            return false;
        }

        if (value < lowest || value > highest)
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"'{where}' must be between {lowest} and {highest}.");

            return false;
        }

        problem = null;
        return true;
    }
}

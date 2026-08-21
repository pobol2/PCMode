using System.Globalization;
using System.Text.Json;

namespace PCMode.Engine;

/// <summary>
/// The one Party group: two configured outputs and how they are balanced.
/// </summary>
/// <remarks>
/// TWO WAYS IN, ONE SHAPE ONCE PARSED
///
/// The document may write the group as a plain list of two outputs plus which
/// one carries the delay:
///
/// <code>
/// "party": {
///   "outputs": ["Speakers", "TV"],
///   "delay": { "output": "Speakers", "ms": 68 },
///   "gain": { "output": "Speakers", "value": 1 }
/// }
/// </code>
///
/// or the older, more technical form that names the two roles directly:
///
/// <code>
/// "party": { "reference": "TV", "mirror": "Speakers", "delayMs": 68, "gain": 1 }
/// </code>
///
/// Both are read by <see cref="TryParse"/> into exactly the same
/// <see cref="Reference"/>/<see cref="Mirror"/> pair, and nothing downstream of
/// this file -- <see cref="AudioProvider"/>, the discoverer, the hardware check
/// -- knows or cares which form a document used. See "ROUTING IS NOT ORDER"
/// below for what "outputs" deliberately does not decide.
///
/// EXACTLY TWO, AND DELIBERATELY SO
///
/// A reference output and a mirror. The reference is the one Windows plays
/// through and the one loopback capture reads back; the mirror is the copy, and
/// the only one of the two that can be delayed. That is a real constraint of the
/// signal path rather than a simplification: the reference is already playing by
/// the time anything can be captured from it, so there is no way to hold it
/// back. Whichever output is slower in the room -- usually the soundbar or TV,
/// over HDMI or eARC -- has to be the reference, and the faster one is delayed
/// to meet it.
///
/// ROUTING IS NOT ORDER
///
/// "outputs" lists the two members for a person to read, in whatever order they
/// find natural, and that order is never used to decide which one is which: the
/// two are told apart only by "delay.output", the one the document says is
/// deliberately behind. Reading routing off list position would make swapping
/// two names in a JSON array -- an edit a formatter or a copy-paste could make
/// by accident -- silently swap which endpoint becomes the Windows default.
///
/// The members are named by their configured audio aliases rather than by
/// endpoint IDs. That keeps one identity model in the file: the aliases already
/// carry the "requires" that says a television's endpoint does not exist until
/// its display does, and a second, ID-shaped way of naming the same devices
/// would need its own answer for that on day one. Endpoint IDs are still what
/// the running mirror holds -- <see cref="AudioMirrorRequest"/> is resolved from
/// these names the instant before it starts.
/// </remarks>
/// <param name="Name">
/// What a mode asks for. It is a value of the audio resource like any device
/// alias, which is what makes Party and a single output mutually exclusive
/// without anything having to arrange it.
/// </param>
/// <param name="ReferenceVolume">
/// A Windows endpoint volume, 0 to 1, or null to leave the endpoint alone.
/// Independent of <see cref="Gain"/>: this moves the slider a user can see in
/// Sound settings, and applies whether Party mode is running or not.
/// </param>
public sealed record PartySettings(
    string Name,
    string Reference,
    string Mirror,
    TimeSpan Delay,
    double Gain,
    double? ReferenceVolume,
    double? MirrorVolume)
{
    /// <summary>Where this lives in the document.</summary>
    public const string SettingsPath = "providers.audio.party";

    /// <summary>
    /// The longest delay a mirror may be given.
    /// </summary>
    /// <remarks>
    /// Two seconds is far more than any speaker pair in one room needs -- HDMI
    /// and eARC paths land between 20 and 300 ms -- and it bounds the buffer the
    /// runtime has to hold. A number an order of magnitude larger would be a
    /// configuration mistake that showed up as a second of latency rather than as
    /// a refusal.
    /// </remarks>
    public const int MaxDelayMs = 2000;

    /// <summary>
    /// The most the mirrored stream may be amplified.
    /// </summary>
    /// <remarks>
    /// +6 dB. Anything above unity can clip a mix that is already near full
    /// scale, so the runtime hard-limits the samples it writes; the bound here is
    /// what stops somebody typing 20 and wondering why their speakers are
    /// distorting. Attenuation is unrestricted -- 0 is a legitimate way to mute
    /// the mirror while keeping the group.
    /// </remarks>
    public const double MaxGain = 2.0;

    public int DelayMs => (int)Math.Round(Delay.TotalMilliseconds);

    /// <summary>The two members, in reference-then-mirror order.</summary>
    public IReadOnlyList<string> Members => [Reference, Mirror];

    /// <summary>
    /// Reads the "party" object, or says what is wrong with it.
    /// </summary>
    /// <param name="devices">
    /// The device aliases defined alongside it. Both members have to be among
    /// them: a Party group naming an output nothing defines would validate and
    /// then fail at the moment somebody asked for it.
    /// </param>
    public static bool TryParse(
        JsonElement written,
        IReadOnlyDictionary<string, string> devices,
        out PartySettings? party,
        out string? problem)
    {
        party = null;

        if (written.ValueKind != JsonValueKind.Object)
        {
            problem =
                $"'{SettingsPath}' must be an object naming two 'outputs' and which one is delayed, " +
                "or the older 'reference'/'mirror' form.";

            return false;
        }

        string name = "Party";
        List<string>? outputs = null;
        JsonElement? delayField = null;
        JsonElement? gainField = null;
        JsonElement? volumesField = null;

        string? legacyReference = null;
        string? legacyMirror = null;
        int legacyDelayMs = 0;
        double? legacyReferenceVolume = null;
        double? legacyMirrorVolume = null;

        foreach (JsonProperty field in written.EnumerateObject())
        {
            switch (field.Name)
            {
                case "name":
                    if (!TryReadName(field.Value, out name!, out problem))
                    {
                        return false;
                    }

                    break;

                case "outputs":
                    if (!TryReadOutputs(field.Value, out outputs, out problem))
                    {
                        return false;
                    }

                    break;

                case "delay":
                    delayField = field.Value;
                    break;

                case "gain":
                    // Read once every field is known. The same key means a plain
                    // number in the older form and an {"output","value"} object
                    // in the newer one, and which of those is expected depends on
                    // whether "outputs" was also written.
                    gainField = field.Value;
                    break;

                case "volumes":
                    volumesField = field.Value;
                    break;

                case "reference":
                    if (!TryReadName(field.Value, out legacyReference, out problem, "reference"))
                    {
                        return false;
                    }

                    break;

                case "mirror":
                    if (!TryReadName(field.Value, out legacyMirror, out problem, "mirror"))
                    {
                        return false;
                    }

                    break;

                case "delayMs":
                    if (!TryReadNumber(field.Value, "delayMs", 0, MaxDelayMs, out double delay, out problem))
                    {
                        return false;
                    }

                    legacyDelayMs = (int)Math.Round(delay);
                    break;

                case "referenceVolume":
                    if (!TryReadNumber(field.Value, "referenceVolume", 0, 1, out double rv, out problem))
                    {
                        return false;
                    }

                    legacyReferenceVolume = rv;
                    break;

                case "mirrorVolume":
                    if (!TryReadNumber(field.Value, "mirrorVolume", 0, 1, out double mv, out problem))
                    {
                        return false;
                    }

                    legacyMirrorVolume = mv;
                    break;

                default:
                    problem =
                        $"'{SettingsPath}' does not understand '{field.Name}'. It takes 'name', 'outputs', " +
                        "'delay', 'gain' and 'volumes' (or the older 'reference', 'mirror', 'delayMs', " +
                        "'gain', 'referenceVolume' and 'mirrorVolume').";

                    return false;
            }
        }

        bool usesOutputs = outputs is not null;
        bool usesLegacyMembers = legacyReference is not null || legacyMirror is not null;

        string reference;
        string mirror;
        int delayMs;
        double gain;
        double? referenceVolume;
        double? mirrorVolume;

        if (usesOutputs)
        {
            if (usesLegacyMembers)
            {
                problem = $"'{SettingsPath}' mixes 'outputs' with 'reference'/'mirror'. Use one form or the other.";
                return false;
            }

            if (legacyReferenceVolume is not null || legacyMirrorVolume is not null)
            {
                problem =
                    $"'{SettingsPath}' uses 'outputs', so endpoint volumes belong under 'volumes': " +
                    "{ \"<output>\": 0.5 }, not 'referenceVolume'/'mirrorVolume'.";

                return false;
            }

            if (!TryReadNewForm(
                    outputs!, delayField, gainField, volumesField,
                    out reference, out mirror, out delayMs, out gain, out referenceVolume, out mirrorVolume,
                    out problem))
            {
                return false;
            }
        }
        else
        {
            if (delayField is not null)
            {
                problem =
                    $"'{SettingsPath}.delay' is only valid together with 'outputs'. The older 'reference'/" +
                    "'mirror' form uses 'delayMs': a plain number of milliseconds.";

                return false;
            }

            if (volumesField is not null)
            {
                problem =
                    $"'{SettingsPath}.volumes' is only valid together with 'outputs'. The older 'reference'/" +
                    "'mirror' form uses 'referenceVolume' and 'mirrorVolume'.";

                return false;
            }

            if (legacyReference is null || legacyMirror is null)
            {
                problem =
                    $"'{SettingsPath}' needs 'outputs': [\"A\", \"B\"] naming the two outputs, with 'delay' " +
                    "saying which one is delayed -- or the older 'reference' output -- the slower one, " +
                    "usually the soundbar or TV -- and 'mirror' output to delay to meet it.";

                return false;
            }

            if (string.Equals(legacyReference, legacyMirror, StringComparison.OrdinalIgnoreCase))
            {
                // One endpoint cannot be both halves of the pair: capturing an
                // endpoint's own loopback and rendering back into it is a
                // feedback loop that gets loud.
                problem = $"'{SettingsPath}' points both 'reference' and 'mirror' at '{legacyReference}'.";
                return false;
            }

            double legacyGain = 1.0;

            if (gainField is JsonElement legacyGainValue)
            {
                if (legacyGainValue.ValueKind != JsonValueKind.Number)
                {
                    problem =
                        $"'{SettingsPath}.gain' must be a number when using 'reference'/'mirror'. The " +
                        "{ \"output\", \"value\" } form is used together with 'outputs'.";

                    return false;
                }

                if (!TryReadNumber(legacyGainValue, "gain", 0, MaxGain, out legacyGain, out problem))
                {
                    return false;
                }
            }

            reference = legacyReference;
            mirror = legacyMirror;
            delayMs = legacyDelayMs;
            gain = legacyGain;
            referenceVolume = legacyReferenceVolume;
            mirrorVolume = legacyMirrorVolume;
        }

        string[] members = [reference, mirror];

        foreach (string member in members)
        {
            if (!devices.ContainsKey(member))
            {
                problem =
                    $"'{SettingsPath}' names '{member}', which '{AudioProvider.SettingsPath}' does not define. " +
                    (devices.Count == 0
                        ? "No audio devices are defined at all."
                        : $"Defined: {string.Join(", ", devices.Keys.Order(StringComparer.OrdinalIgnoreCase))}.");

                return false;
            }
        }

        if (devices.ContainsKey(name))
        {
            // A mode names one value for the audio resource, and it would mean
            // two different things.
            problem =
                $"'{SettingsPath}.name' is '{name}', which is also an audio device in " +
                $"'{AudioProvider.SettingsPath}'. Give the Party group a name of its own.";

            return false;
        }

        party = new PartySettings(
            name,
            devices.Keys.First(device => string.Equals(device, reference, StringComparison.OrdinalIgnoreCase)),
            devices.Keys.First(device => string.Equals(device, mirror, StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromMilliseconds(delayMs),
            gain,
            referenceVolume,
            mirrorVolume);

        problem = null;
        return true;
    }

    /// <summary>
    /// Reads the "outputs" / "delay" / "gain" / "volumes" form once "outputs"
    /// has already been read.
    /// </summary>
    /// <remarks>
    /// "delay.output" is required even when the delay itself is zero: it is the
    /// only thing in this form that says which output is the mirror, and a
    /// group that left it out would have two outputs and no way to say which one
    /// becomes the Windows default. Presentation order in "outputs" is
    /// deliberately never consulted for this -- see the class remarks.
    /// </remarks>
    private static bool TryReadNewForm(
        List<string> outputs,
        JsonElement? delayField,
        JsonElement? gainField,
        JsonElement? volumesField,
        out string reference,
        out string mirror,
        out int delayMs,
        out double gain,
        out double? referenceVolume,
        out double? mirrorVolume,
        out string? problem)
    {
        reference = mirror = string.Empty;
        delayMs = 0;
        gain = 1.0;
        referenceVolume = null;
        mirrorVolume = null;

        if (delayField is not JsonElement delay)
        {
            problem =
                $"'{SettingsPath}' needs 'delay': {{ \"output\": ..., \"ms\": ... }} saying which of " +
                $"'{outputs[0]}' and '{outputs[1]}' is delayed to meet the other. Even a delay of 0 needs " +
                "this, because it is what decides which output becomes the Windows default.";

            return false;
        }

        if (!TryReadDelay(delay, outputs, out string delayedOutput, out delayMs, out problem))
        {
            return false;
        }

        mirror = delayedOutput;
        reference = outputs.First(output => !string.Equals(output, delayedOutput, StringComparison.OrdinalIgnoreCase));

        if (gainField is JsonElement gainValue)
        {
            if (!TryReadGain(gainValue, delayedOutput, out gain, out problem))
            {
                return false;
            }
        }

        if (volumesField is JsonElement volumes && !TryReadVolumes(volumes, outputs, mirror, out referenceVolume, out mirrorVolume, out problem))
        {
            return false;
        }

        problem = null;
        return true;
    }

    private static bool TryReadDelay(
        JsonElement delay,
        List<string> outputs,
        out string output,
        out int ms,
        out string? problem)
    {
        output = string.Empty;
        ms = 0;

        if (delay.ValueKind != JsonValueKind.Object)
        {
            problem = $"'{SettingsPath}.delay' must be an object: {{ \"output\": ..., \"ms\": ... }}.";
            return false;
        }

        string? namedOutput = null;
        double milliseconds = 0;
        bool sawMs = false;

        foreach (JsonProperty field in delay.EnumerateObject())
        {
            switch (field.Name)
            {
                case "output":
                    if (!TryReadOutputReference(field.Value, outputs, "delay.output", out namedOutput, out problem))
                    {
                        return false;
                    }

                    break;

                case "ms":
                    if (!TryReadNumber(field.Value, "delay.ms", 0, MaxDelayMs, out milliseconds, out problem))
                    {
                        return false;
                    }

                    sawMs = true;
                    break;

                default:
                    problem = $"'{SettingsPath}.delay' does not understand '{field.Name}'. It takes 'output' and 'ms'.";
                    return false;
            }
        }

        if (namedOutput is null)
        {
            problem =
                $"'{SettingsPath}.delay.output' must name one of '{outputs[0]}' or '{outputs[1]}' -- whichever " +
                "one is delayed to meet the other.";

            return false;
        }

        output = namedOutput;
        ms = sawMs ? (int)Math.Round(milliseconds) : 0;
        problem = null;
        return true;
    }

    private static bool TryReadGain(
        JsonElement gain,
        string mirrorOutput,
        out double value,
        out string? problem)
    {
        value = 1.0;

        if (gain.ValueKind != JsonValueKind.Object)
        {
            problem =
                $"'{SettingsPath}.gain' must be an object: {{ \"output\": ..., \"value\": ... }} when using " +
                "'outputs'. The plain-number form is used together with 'reference'/'mirror'.";

            return false;
        }

        string? namedOutput = null;
        bool sawValue = false;

        foreach (JsonProperty field in gain.EnumerateObject())
        {
            switch (field.Name)
            {
                case "output":
                    // Matched only against the mirror: gain is a property of the
                    // mirrored stream, and the runtime has no gain stage on the
                    // reference to apply one to.
                    if (field.Value.ValueKind != JsonValueKind.String ||
                        !string.Equals(field.Value.GetString()?.Trim(), mirrorOutput, StringComparison.OrdinalIgnoreCase))
                    {
                        problem =
                            $"'{SettingsPath}.gain.output' must be '{mirrorOutput}' -- gain only applies to " +
                            "the delayed output, because that is the only one this runtime generates a copy of.";

                        return false;
                    }

                    namedOutput = mirrorOutput;
                    break;

                case "value":
                    if (!TryReadNumber(field.Value, "gain.value", 0, MaxGain, out value, out problem))
                    {
                        return false;
                    }

                    sawValue = true;
                    break;

                default:
                    problem = $"'{SettingsPath}.gain' does not understand '{field.Name}'. It takes 'output' and 'value'.";
                    return false;
            }
        }

        if (namedOutput is null)
        {
            problem =
                $"'{SettingsPath}.gain.output' must be '{mirrorOutput}', named explicitly so a document that " +
                "later swaps which output is delayed does not silently move the gain with it.";

            return false;
        }

        if (!sawValue)
        {
            problem = $"'{SettingsPath}.gain' needs a 'value'.";
            return false;
        }

        problem = null;
        return true;
    }

    private static bool TryReadVolumes(
        JsonElement volumes,
        List<string> outputs,
        string mirrorOutput,
        out double? referenceVolume,
        out double? mirrorVolume,
        out string? problem)
    {
        referenceVolume = null;
        mirrorVolume = null;

        if (volumes.ValueKind != JsonValueKind.Object)
        {
            problem = $"'{SettingsPath}.volumes' must be an object mapping an output to a level from 0 to 1.";
            return false;
        }

        foreach (JsonProperty field in volumes.EnumerateObject())
        {
            string? matched = outputs.FirstOrDefault(output => string.Equals(output, field.Name, StringComparison.OrdinalIgnoreCase));

            if (matched is null)
            {
                problem =
                    $"'{SettingsPath}.volumes' names '{field.Name}', which is not one of the configured " +
                    $"outputs ('{outputs[0]}', '{outputs[1]}').";

                return false;
            }

            if (!TryReadNumber(field.Value, $"volumes.{field.Name}", 0, 1, out double level, out problem))
            {
                return false;
            }

            if (string.Equals(matched, mirrorOutput, StringComparison.OrdinalIgnoreCase))
            {
                mirrorVolume = level;
            }
            else
            {
                referenceVolume = level;
            }
        }

        problem = null;
        return true;
    }

    private static bool TryReadOutputs(JsonElement value, out List<string>? outputs, out string? problem)
    {
        outputs = null;

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
        {
            problem = $"'{SettingsPath}.outputs' must list exactly two configured outputs, for example [\"Speakers\", \"TV\"].";
            return false;
        }

        var read = new List<string>(2);

        foreach (JsonElement element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            {
                problem = $"'{SettingsPath}.outputs' must be the names of two configured outputs, in quotes.";
                return false;
            }

            read.Add(element.GetString()!.Trim());
        }

        if (string.Equals(read[0], read[1], StringComparison.OrdinalIgnoreCase))
        {
            problem = $"'{SettingsPath}.outputs' lists '{read[0]}' twice. Party mode needs two different outputs.";
            return false;
        }

        outputs = read;
        problem = null;
        return true;
    }

    /// <summary>Reads a string that must name one of a known set of outputs.</summary>
    private static bool TryReadOutputReference(
        JsonElement value,
        IReadOnlyList<string> outputs,
        string field,
        out string? matched,
        out string? problem)
    {
        matched = null;

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            problem = $"'{SettingsPath}.{field}' must be the name of a configured output, in quotes.";
            return false;
        }

        string wanted = value.GetString()!.Trim();

        matched = outputs.FirstOrDefault(output => string.Equals(output, wanted, StringComparison.OrdinalIgnoreCase));

        if (matched is null)
        {
            problem =
                $"'{SettingsPath}.{field}' is '{wanted}', which is not one of the configured outputs " +
                $"({string.Join(", ", outputs.Select(output => $"'{output}'"))}).";

            return false;
        }

        problem = null;
        return true;
    }

    private static bool TryReadName(JsonElement value, out string? name, out string? problem, string field = "name")
    {
        name = null;

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            problem = $"'{SettingsPath}.{field}' must be the name of a configured audio output, in quotes.";
            return false;
        }

        name = value.GetString()!.Trim();
        problem = null;
        return true;
    }

    private static bool TryReadNumber(
        JsonElement value,
        string field,
        double lowest,
        double highest,
        out double number,
        out string? problem)
    {
        number = 0;

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out number) ||
            double.IsNaN(number) || double.IsInfinity(number))
        {
            problem = $"'{SettingsPath}.{field}' must be a number.";
            return false;
        }

        if (number < lowest || number > highest)
        {
            problem = string.Create(
                CultureInfo.InvariantCulture,
                $"'{SettingsPath}.{field}' is {number}, which is outside {lowest} to {highest}.");

            return false;
        }

        problem = null;
        return true;
    }
}

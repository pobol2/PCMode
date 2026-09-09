using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCMode.Engine;

/// <summary>One television setting, as it will be sent.</summary>
/// <remarks>
/// A list rather than a dictionary, because the order settings are written in is
/// occasionally load-bearing and a dictionary's order is an implementation
/// detail. What somebody wrote in the file is what goes over the wire.
/// </remarks>
public sealed record LgTvSetting(string Key, string Value);

/// <summary>
/// One named picture profile, split into the half that renegotiates the HDMI
/// link and the half that only changes the picture.
/// </summary>
/// <remarks>
/// WHY ONE PROFILE IS TWO RESOURCES
///
/// Not a tidiness exercise. The two halves have OPPOSITE ordering requirements
/// against the display, so no single resource containing both can be correct.
///
/// <see cref="Signal"/> changes how the HDMI port negotiates -- deep colour, PC
/// mode, game mode, FreeSync, input latency. Writing any of them makes the
/// television re-handshake the link, which drops the sink off the bus. If the
/// television is the active Windows display when that happens -- and in a
/// television mode it is the ONLY one -- Windows is left with no display at all
/// and recovers by reactivating the desk monitors on its own, silently undoing
/// the layout that was just applied. So these are written BEFORE the display
/// switches.
///
/// <see cref="PictureMode"/> and <see cref="Settings"/> are purely about the
/// picture, and the picture mode in particular can only be selected against a
/// live signal: webOS exposes 'filmMaker' and 'hdrFilmMaker' as separate modes
/// and refuses the HDR one until the incoming signal really is HDR. So these are
/// written AFTER the display switches.
///
/// WHAT IS IN THE FILE AND WHAT GOES OVER THE WIRE
///
/// The file names settings the way a person writes them --
/// "uhddeepcolor_hdmi{n}" -- and the television answers to something else
/// entirely -- "uhdDeepColorHDMI4". <see cref="SignalNames"/> is the whole of
/// that translation, and it is closed: a name that is not in it is refused when
/// the configuration is read. An unrecognised key is not an error the television
/// reports, it is a setting that silently does nothing, and finding that out by
/// watching a picture that never changes is the worst way to find it out.
///
/// WHICH INPUT A PROFILE IS FOR
///
/// <see cref="InputName"/> is which of the television's named inputs "{n}" stands
/// for in this profile's own signal settings -- "PC Gaming" says "PC", "Console
/// Gaming" says "Console", and each resolves its own HDMI socket independently.
/// It is resolved once, when the configuration is read: explicitly, from a
/// document written before names existed, or automatically when the television
/// has only one input to mean. See <see cref="LgTvSettings.TryParse"/>.
/// </remarks>
public sealed record LgTvProfile(
    string Name,
    string PictureMode,
    IReadOnlyList<LgTvSetting> Signal,
    IReadOnlyList<LgTvSetting> Settings,
    string? InputName
)
{
    /// <summary>The settings category the signal half is written under.</summary>
    public const string SignalCategory = "other";

    /// <summary>The settings category the picture half is written under.</summary>
    public const string PictureCategory = "picture";

    /// <summary>The key the picture mode itself is written under.</summary>
    public const string PictureModeKey = "pictureMode";

    /// <summary>
    /// Every signal setting that may be written, and what the television calls
    /// it.
    /// </summary>
    /// <remarks>
    /// "{n}" stands for the HDMI socket the machine is plugged into and is
    /// substituted on both sides, so a profile written for HDMI 4 works
    /// unchanged on somebody else's HDMI 2. The capitalisation on the right is
    /// not decorative -- webOS is case-sensitive here, and the inconsistency
    /// between "hdmiPcMode_hdmi4" and "uhdDeepColorHDMI4" is the television's,
    /// not a mistake.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> SignalNames = new Dictionary<string, string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["uhddeepcolor_hdmi{n}"] = "uhdDeepColorHDMI{n}",
        ["hdmipcmode_hdmi{n}"] = "hdmiPcMode_hdmi{n}",
        ["gamemode_hdmi{n}"] = "gameMode_hdmi{n}",
        ["gameoptimization_hdmi{n}"] = "gameOptimizationHDMI{n}",
        ["gameoptimization"] = "gameOptimization",
        ["freesyncoled_hdmi{n}"] = "freesyncOLEDHDMI{n}",
        ["freesync"] = "freesync",
        ["inputoptimization"] = "inputOptimization",
    };

    /// <summary>The settings whose value is not simply on or off.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> Choices = new Dictionary<string, string[]>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["inputoptimization"] = ["auto", "on", "standard", "boost"],
    };

    /// <summary>Values accepted for one configuration-side signal setting.</summary>
    public static IReadOnlyList<string> SignalValuesFor(string writtenName) =>
        Choices.TryGetValue(writtenName, out string[]? choices) ? choices : ["on", "off"];

    /// <summary>Whether one configuration-side signal setting is tied to one HDMI socket.</summary>
    public static bool SignalUsesHdmi(string writtenName) =>
        SignalNames.TryGetValue(writtenName, out string? wireName)
        && wireName.Contains("{n}", StringComparison.Ordinal);

    /// <summary>
    /// Converts a parsed wire-side signal name back to the portable configuration
    /// name used by the editor.
    /// </summary>
    public static string? WrittenSignalName(string wireName)
    {
        if (string.IsNullOrWhiteSpace(wireName))
        {
            return null;
        }

        foreach ((string written, string wire) in SignalNames)
        {
            if (!wire.Contains("{n}", StringComparison.Ordinal))
            {
                if (string.Equals(wire, wireName, StringComparison.OrdinalIgnoreCase))
                {
                    return written;
                }

                continue;
            }

            for (int hdmi = 1; hdmi <= 9; hdmi++)
            {
                string candidate = wire.Replace(
                    "{n}",
                    hdmi.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal
                );

                if (string.Equals(candidate, wireName, StringComparison.OrdinalIgnoreCase))
                {
                    return written;
                }
            }
        }

        return null;
    }

    /// <summary>Whether this profile has anything to say before the display switches.</summary>
    public bool HasSignal => Signal.Count > 0;

    /// <summary>
    /// Reads the "profiles" section, or says what is wrong with it.
    /// </summary>
    /// <param name="inputs">The television's own named inputs, friendly name to id.</param>
    /// <param name="legacyDefaultInput">
    /// The input a document written before a profile could name its own says to
    /// use instead, already checked against <paramref name="inputs"/>. Null on an
    /// ordinary document. See <see cref="LgTvSettings.TryParse"/>.
    /// </param>
    public static bool TryParse(
        JsonElement element,
        IReadOnlyDictionary<string, string> inputs,
        string? legacyDefaultInput,
        out IReadOnlyDictionary<string, LgTvProfile>? profiles,
        out string? problem
    )
    {
        profiles = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem =
                $"'{LgTvSettings.SettingsPath}.profiles' must be an object mapping a name you "
                + "choose to that profile's television settings.";

            return false;
        }

        var parsed = new Dictionary<string, LgTvProfile>(StringComparer.OrdinalIgnoreCase);
        var pictureModes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                problem = $"A profile in '{LgTvSettings.SettingsPath}.profiles' has no name.";
                return false;
            }

            string name = property.Name.Trim();
            string where = $"'{LgTvSettings.SettingsPath}.profiles.{name}'";

            if (
                !TryParseOne(
                    where,
                    name,
                    property.Value,
                    inputs,
                    legacyDefaultInput,
                    out LgTvProfile? profile,
                    out problem
                )
            )
            {
                return false;
            }

            if (!parsed.TryAdd(name, profile!))
            {
                problem = $"'{LgTvSettings.SettingsPath}.profiles' defines '{name}' more than once.";
                return false;
            }

            if (!pictureModes.TryAdd(profile!.PictureMode, name))
            {
                // The picture mode is the only part of a profile the television
                // will report back, so it is how a profile is recognised. Two
                // profiles sharing one would make that answer a coin toss, and
                // whichever lost would read as wrong forever. A mode that wants
                // the same picture as another names the same profile.
                problem =
                    $"'{LgTvSettings.SettingsPath}.profiles' gives both "
                    + $"'{pictureModes[profile.PictureMode]}' and '{name}' the picture mode "
                    + $"'{profile.PictureMode}'. That is the only setting the television reports "
                    + "back, so nothing could tell which of them it is in.";

                return false;
            }
        }

        profiles = parsed;
        problem = null;
        return true;
    }

    private static bool TryParseOne(
        string where,
        string name,
        JsonElement element,
        IReadOnlyDictionary<string, string> inputs,
        string? legacyDefaultInput,
        out LgTvProfile? profile,
        out string? problem
    )
    {
        profile = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = $"{where} must be an object.";
            return false;
        }

        string? pictureMode = null;
        string? declaredInput = null;
        JsonElement? signalSection = null;
        IReadOnlyList<LgTvSetting> settings = [];

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.NameEquals("pictureMode"))
            {
                if (
                    property.Value.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(property.Value.GetString())
                )
                {
                    problem = $"{where}.pictureMode must be a value in quotes, like \"hdrFilmMaker\".";
                    return false;
                }

                pictureMode = property.Value.GetString()!.Trim();
            }
            else if (property.NameEquals("input"))
            {
                if (
                    property.Value.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(property.Value.GetString())
                )
                {
                    problem = $"{where}.input must be a value in quotes, naming one of this television's inputs.";
                    return false;
                }

                declaredInput = property.Value.GetString()!.Trim();
            }
            else if (property.NameEquals("signal"))
            {
                // Held rather than read here: which HDMI socket "{n}" stands for
                // depends on 'input', which may not have been read yet. Fields in
                // a document have no order worth relying on.
                signalSection = property.Value;
            }
            else if (property.NameEquals("settings"))
            {
                if (!TryParseSettings(where, property.Value, out settings, out problem))
                {
                    return false;
                }
            }
            else
            {
                problem =
                    $"{where} does not understand '{property.Name}'. "
                    + "It takes 'pictureMode', 'input', 'signal' and 'settings'.";

                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(pictureMode))
        {
            // Required, because the picture mode is the one thing about a profile
            // that can be read back. Without it the profile could be written and
            // never confirmed, which is a resource that always reports success.
            problem =
                $"{where} has no 'pictureMode'. It is the only setting the television reports "
                + "back, so a profile without one could never be verified.";

            return false;
        }

        if (declaredInput is not null && !inputs.ContainsKey(declaredInput))
        {
            problem =
                inputs.Count == 0
                    ? $"{where}.input is '{declaredInput}', and no inputs are defined."
                    : $"{where}.input is '{declaredInput}', which is not one of the inputs defined here: "
                        + $"{string.Join(", ", inputs.Keys.Order(StringComparer.OrdinalIgnoreCase))}.";

            return false;
        }

        // Named on the profile itself if it says so; otherwise the input a
        // document written before a profile could name its own already pointed
        // at; otherwise, with exactly one input to mean, that one -- the ordinary
        // machine needs to say this nowhere at all.
        string? resolvedInput =
            declaredInput ?? legacyDefaultInput ?? (inputs.Count == 1 ? inputs.Keys.Single() : null);
        string? hdmi = resolvedInput is null ? null : HdmiNumber(inputs[resolvedInput]);

        IReadOnlyList<LgTvSetting> signal = [];

        if (
            signalSection is JsonElement declaredSignal
            && !TryParseSignal(where, declaredSignal, hdmi, out signal, out problem)
        )
        {
            return false;
        }

        profile = new LgTvProfile(name, pictureMode!, signal, settings, resolvedInput);
        problem = null;
        return true;
    }

    private static bool TryParseSignal(
        string where,
        JsonElement element,
        string? hdmi,
        out IReadOnlyList<LgTvSetting> signal,
        out string? problem
    )
    {
        signal = [];

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = $"{where}.signal must be an object mapping a setting to its value.";
            return false;
        }

        var parsed = new List<LgTvSetting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            string written = property.Name.Trim();

            if (!SignalNames.TryGetValue(written, out string? wire))
            {
                problem =
                    $"{where}.signal does not understand '{written}'. It takes: "
                    + $"{string.Join(", ", SignalNames.Keys.Order(StringComparer.OrdinalIgnoreCase))}.";

                return false;
            }

            if (!seen.Add(written))
            {
                problem = $"{where}.signal sets '{written}' more than once.";
                return false;
            }

            if (
                property.Value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(property.Value.GetString())
            )
            {
                problem = $"{where}.signal.{written} must be a value in quotes.";
                return false;
            }

            string value = property.Value.GetString()!.Trim().ToLowerInvariant();
            string[] allowed = Choices.TryGetValue(written, out string[]? choices) ? choices : ["on", "off"];

            if (!allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                // The television refuses nothing here: an unrecognised value is
                // accepted and ignored. So the check has to happen on this side or
                // not at all.
                problem = $"{where}.signal.{written} is '{value}'. It takes {string.Join(", ", allowed)}.";
                return false;
            }

            if (wire.Contains("{n}", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(hdmi))
            {
                problem =
                    $"{where}.signal sets '{written}', which applies to one HDMI socket, and nothing "
                    + $"says which. Add {where}.input, naming one of this television's inputs.";

                return false;
            }

            parsed.Add(new LgTvSetting(wire.Replace("{n}", hdmi ?? string.Empty, StringComparison.Ordinal), value));
        }

        signal = parsed;
        problem = null;
        return true;
    }

    /// <summary>
    /// Reads the picture settings written alongside the picture mode.
    /// </summary>
    /// <remarks>
    /// Not checked against a list of known keys, unlike the signal half. These go
    /// into the same category the picture mode does, webOS has a great many of
    /// them, and which exist varies by model and firmware -- so a closed list here
    /// would be a list that goes out of date and refuses things that work. What is
    /// checked is the shape: a name, and a value in quotes.
    /// </remarks>
    private static bool TryParseSettings(
        string where,
        JsonElement element,
        out IReadOnlyList<LgTvSetting> settings,
        out string? problem
    )
    {
        settings = [];

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = $"{where}.settings must be an object mapping a setting to its value.";
            return false;
        }

        var parsed = new List<LgTvSetting>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            string key = property.Name.Trim();

            if (string.IsNullOrWhiteSpace(key))
            {
                problem = $"{where}.settings has an entry with no name.";
                return false;
            }

            if (string.Equals(key, PictureModeKey, StringComparison.OrdinalIgnoreCase))
            {
                problem =
                    $"{where}.settings sets '{PictureModeKey}', which belongs in "
                    + $"{where}.pictureMode. Two places to write it is one place to disagree.";

                return false;
            }

            if (!seen.Add(key))
            {
                problem = $"{where}.settings sets '{key}' more than once.";
                return false;
            }

            if (
                property.Value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(property.Value.GetString())
            )
            {
                problem = $"{where}.settings.{key} must be a value in quotes.";
                return false;
            }

            parsed.Add(new LgTvSetting(key, property.Value.GetString()!.Trim()));
        }

        settings = parsed;
        problem = null;
        return true;
    }

    /// <summary>The digit in an input id like "HDMI_4", or null.</summary>
    internal static string? HdmiNumber(string? inputId)
    {
        if (string.IsNullOrWhiteSpace(inputId))
        {
            return null;
        }

        Match hdmi = Regex.Match(inputId, @"([1-9])$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        return hdmi.Success ? hdmi.Groups[1].Value : null;
    }
}

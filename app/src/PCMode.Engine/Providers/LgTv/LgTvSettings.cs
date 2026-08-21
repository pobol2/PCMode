using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCMode.Engine;

/// <summary>
/// One television, as the configuration describes it.
/// </summary>
/// <remarks>
/// ONE PROVIDER SECTION, NAMED TELEVISIONS
///
/// The section under <c>providers.lgTv</c> can contain several named
/// televisions. Each entry keeps one device's address, MAC address, input names
/// and picture profiles together, and each gets its own connection and its own
/// four resources -- see <see cref="LgTvCatalog"/> for which of them answers to
/// the historical tvPower/tvInput/tvSignal/tvProfile keys.
///
/// LOGICAL NAMES, NOT SOCKET NUMBERS
///
/// A mode says "PC". Which HDMI socket that is depends on which one somebody
/// plugged the machine into, so it is configuration -- the same division the
/// display, cursor and audio providers already keep. It also means moving the
/// cable is one edit here rather than an edit in every mode.
///
/// ABSENT IS NOT BROKEN
///
/// Most machines have no television. A missing section, or "enabled": false, is
/// an ordinary answer and produces <see cref="Disabled"/>: the resources still
/// exist, still validate, and are left out of transitions that can manage without
/// them. What is refused is a section that says it is enabled and then does not
/// say enough to reach anything -- because that is a mistake somebody made, and
/// finding it when a mode is applied instead of when the file is read would mean
/// finding it in the middle of a transition.
/// </remarks>
public sealed record LgTvSettings
{
    /// <summary>Where these settings live in the document.</summary>
    public const string SettingsPath = "providers.lgTv";

    /// <summary>The provider section shared by the television resources.</summary>
    public const string SectionKey = "lgTv";

    /// <summary>
    /// The secure webOS port.
    /// </summary>
    /// <remarks>
    /// Not configurable. 3001 is the TLS port every webOS television listens on
    /// and 3000 is the plaintext one; offering the choice would be offering to
    /// send a pairing key in the clear.
    /// </remarks>
    public const int SecurePort = 3001;

    /// <summary>The highest HDMI socket number a television is addressed by.</summary>
    /// <remarks>
    /// One number, here, because three things depend on agreeing about it: what
    /// <see cref="TryNormaliseInput"/> accepts, what
    /// <see cref="LgTvProfile.WrittenSignalName"/> searches, and what a settings
    /// screen offers. A screen that offered fewer than the parser accepts would
    /// make a configured input impossible to see, let alone edit.
    /// </remarks>
    public const int HighestHdmiInput = 9;

    /// <summary>Every input id a television may be pointed at, in socket order.</summary>
    public static readonly IReadOnlyList<string> HdmiInputIds =
        [.. Enumerable.Range(1, HighestHdmiInput).Select(number => $"HDMI_{number}")];

    /// <summary>The answer for a machine with no television configured.</summary>
    public static readonly LgTvSettings Disabled = new();

    private LgTvSettings()
    {
    }

    /// <summary>Whether there is a television to talk to at all.</summary>
    public bool Enabled { get; private init; }

    /// <summary>Its address on the network.</summary>
    public string Host { get; private init; } = string.Empty;

    /// <summary>Its MAC address, normalised to AA:BB:CC:DD:EE:FF.</summary>
    public string MacAddress { get; private init; } = string.Empty;

    /// <summary>Logical input names, mapped to the ids the television uses.</summary>
    public IReadOnlyDictionary<string, string> Inputs { get; private init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The display layout the desktop must be in before the television is
    /// switched off, when one is configured.
    /// </summary>
    /// <remarks>
    /// Switching off a television that is currently showing the desktop leaves
    /// Windows with no display to put anything on, and whoever did it with nothing
    /// to look at. Naming a layout here makes returning to it part of switching
    /// off, ordered before it, rather than something the user has to remember to
    /// put in every mode that turns the television off.
    ///
    /// Which layout is safe is a fact about somebody's monitors, so it is a name
    /// from their own configuration. It is not checked against the display
    /// provider here: the planner resolves it like any other requirement, and
    /// refuses with a better message than this could write.
    /// </remarks>
    public string? SafeDisplayTarget { get; private init; }

    /// <summary>The named picture profiles.</summary>
    public IReadOnlyDictionary<string, LgTvProfile> Profiles { get; private init; } =
        new Dictionary<string, LgTvProfile>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The configured input names, in name order.</summary>
    public IReadOnlyList<string> InputNames =>
        [.. Inputs.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The configured profile names, in name order.</summary>
    public IReadOnlyList<string> ProfileNames =>
        [.. Profiles.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The profile a mode named, if there is one.</summary>
    public LgTvProfile? ProfileFor(string name) =>
        !string.IsNullOrWhiteSpace(name) && Profiles.TryGetValue(name.Trim(), out LgTvProfile? profile)
            ? profile
            : null;

    /// <summary>
    /// The name of the profile that declares a picture mode the television
    /// reported, if any declares it.
    /// </summary>
    /// <remarks>
    /// Unambiguous because two profiles sharing a picture mode are refused when
    /// the settings are read -- see <see cref="LgTvProfile.TryParse"/>. A picture
    /// mode no profile declares is somebody having changed it with the remote
    /// control, which is not an error and simply is not one of the answers a mode
    /// can ask for.
    /// </remarks>
    public string? ProfileForPictureMode(string pictureMode)
    {
        if (string.IsNullOrWhiteSpace(pictureMode))
        {
            return null;
        }

        foreach ((string name, LgTvProfile profile) in Profiles)
        {
            if (string.Equals(profile.PictureMode, pictureMode.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>What the television calls the input a mode named, if anything.</summary>
    public string? InputIdFor(string logical) =>
        !string.IsNullOrWhiteSpace(logical) && Inputs.TryGetValue(logical.Trim(), out string? id) ? id : null;

    /// <summary>The name a mode would use for an input id the television reported.</summary>
    /// <remarks>
    /// The reverse of <see cref="InputIdFor"/>, and unambiguous because two names
    /// for one input are refused when the settings are read. Without that rule this
    /// answer would depend on the order somebody wrote them in.
    /// </remarks>
    public string? LogicalFor(string inputId)
    {
        foreach ((string logical, string id) in Inputs)
        {
            if (string.Equals(id, inputId, StringComparison.OrdinalIgnoreCase))
            {
                return logical;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads one television body, or says what is wrong with it.
    /// </summary>
    /// <remarks>
    /// The only parser of a television. <see cref="LgTvCatalog.TryParse"/> is what
    /// decides how many bodies there are and what each is called, and hands each
    /// one here; nothing else reads this section, so there is one set of rules
    /// about what a malformed television is and one place they are written.
    ///
    /// Shape is checked whether or not the television is enabled -- an unknown
    /// field, a wrong type, a repeated name. Those are typing mistakes, and a
    /// mistake that is only reported once somebody switches the television on is a
    /// mistake that gets found in the dark. What is only required when enabled is
    /// the information needed to actually reach it.
    /// </remarks>
    internal static bool TryParse(JsonElement? settings, out LgTvSettings? parsed, out string? problem)
    {
        parsed = null;

        if (settings is not JsonElement element || element.ValueKind == JsonValueKind.Null)
        {
            parsed = Disabled;
            problem = null;
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = $"'{SettingsPath}' must be an object.";
            return false;
        }

        // A section that exists means a television that is meant to work. Saying
        // so again with "enabled": true would be a second switch to forget.
        bool enabled = true;
        string? host = null;
        string? mac = null;
        string? safeDisplay = null;
        string? profileInput = null;
        JsonElement? profileSection = null;
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.NameEquals("enabled"))
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    problem = $"'{SettingsPath}.enabled' must be true or false.";
                    return false;
                }

                enabled = property.Value.ValueKind == JsonValueKind.True;
            }
            else if (property.NameEquals("host"))
            {
                if (!TryReadText(property, "host", out host, out problem))
                {
                    return false;
                }
            }
            else if (property.NameEquals("macAddress"))
            {
                if (!TryReadText(property, "macAddress", out string? written, out problem) ||
                    !TryNormaliseMac(written!, out mac, out problem))
                {
                    return false;
                }
            }
            else if (property.NameEquals("safeDisplayTarget"))
            {
                if (!TryReadText(property, "safeDisplayTarget", out safeDisplay, out problem))
                {
                    return false;
                }
            }
            else if (property.NameEquals("profileInput"))
            {
                // Superseded by naming the input on each profile that needs one
                // -- see LgTvProfile.InputName. Still read, and still honoured as
                // the default for any profile that does not name its own, so a
                // document written before a profile could say this keeps working
                // unedited. Never written by this build: LgTvConfigurationStore
                // writes 'input' into each profile that needs it instead, so
                // saving migrates a document off this key.
                if (!TryReadText(property, "profileInput", out profileInput, out problem))
                {
                    return false;
                }
            }
            else if (property.NameEquals("inputs"))
            {
                if (!TryReadInputs(property.Value, inputs, out problem))
                {
                    return false;
                }
            }
            else if (property.NameEquals("profiles"))
            {
                // Held rather than read, because a profile's settings are written
                // per HDMI socket and which socket that is has not necessarily
                // been read yet. Fields in a document have no order worth relying
                // on.
                profileSection = property.Value;
            }
            else
            {
                // Refused rather than ignored, like every other unknown field. A
                // dropped one is a setting the user believes they have written.
                problem =
                    $"'{SettingsPath}' does not understand '{property.Name}'. It takes " +
                    "'enabled', 'host', 'macAddress', 'inputs', 'profileInput', 'profiles' " +
                    "and 'safeDisplayTarget'.";

                return false;
            }
        }

        if (profileInput is not null && !inputs.ContainsKey(profileInput))
        {
            problem = inputs.Count == 0
                ? $"'{SettingsPath}.profileInput' is '{profileInput}', and no inputs are defined."
                : $"'{SettingsPath}.profileInput' is '{profileInput}', which is not one of the " +
                  $"inputs defined here: {string.Join(", ", inputs.Keys.Order(StringComparer.OrdinalIgnoreCase))}.";

            return false;
        }

        IReadOnlyDictionary<string, LgTvProfile> profiles =
            new Dictionary<string, LgTvProfile>(StringComparer.OrdinalIgnoreCase);

        if (profileSection is JsonElement declared)
        {
            if (!LgTvProfile.TryParse(declared, inputs, profileInput, out IReadOnlyDictionary<string, LgTvProfile>? read, out problem))
            {
                return false;
            }

            profiles = read!;
        }

        if (!enabled)
        {
            // Kept exactly as written apart from being switched off, so that
            // turning it back on does not also have to re-supply everything.
            parsed = new LgTvSettings
            {
                Enabled = false,
                Host = host ?? string.Empty,
                MacAddress = mac ?? string.Empty,
                Inputs = inputs,
                Profiles = profiles,
                SafeDisplayTarget = safeDisplay,
            };

            problem = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            problem =
                $"'{SettingsPath}' is enabled but has no 'host'. " +
                "Give the television's address on your network, in quotes.";

            return false;
        }

        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            problem = $"'{SettingsPath}.host' is '{host}', which is not an address or a host name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(mac))
        {
            // Required rather than optional because switching the television ON is
            // the whole point of the resource, and a magic packet is the only way
            // to do it: a television that is off is not answering its network
            // socket to be asked. A configuration that can only ever switch it off
            // is a trap that springs the first time somebody selects a TV mode.
            problem =
                $"'{SettingsPath}' is enabled but has no 'macAddress'. " +
                "Switching a television on is done with a Wake-on-LAN packet, which needs it.";

            return false;
        }

        parsed = new LgTvSettings
        {
            Enabled = true,
            Host = host!.Trim(),
            MacAddress = mac!,
            Inputs = inputs,
            Profiles = profiles,
            SafeDisplayTarget = safeDisplay,
        };

        problem = null;
        return true;
    }

    private static bool TryReadText(JsonProperty property, string name, out string? value, out string? problem)
    {
        value = null;

        if (property.Value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.Value.GetString()))
        {
            problem = $"'{SettingsPath}.{name}' must be a value in quotes, and not empty.";
            return false;
        }

        value = property.Value.GetString()!.Trim();
        problem = null;
        return true;
    }

    private static bool TryReadInputs(
        JsonElement element,
        Dictionary<string, string> inputs,
        out string? problem)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            problem =
                $"'{SettingsPath}.inputs' must be an object mapping a name you choose to " +
                "the television's input, like { \"PC\": \"HDMI_4\" }.";

            return false;
        }

        var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty input in element.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(input.Name))
            {
                problem = $"An input in '{SettingsPath}.inputs' has no name.";
                return false;
            }

            string logical = input.Name.Trim();

            if (input.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(input.Value.GetString()))
            {
                problem =
                    $"'{SettingsPath}.inputs.{logical}' must name one of the television's " +
                    "inputs, like \"HDMI_4\", in quotes.";

                return false;
            }

            if (!TryNormaliseInput(input.Value.GetString()!, out string? id, out problem))
            {
                problem = $"'{SettingsPath}.inputs.{logical}': {problem}";
                return false;
            }

            if (!inputs.TryAdd(logical, id!))
            {
                // Case-insensitively, because that is how a mode looks one up.
                problem = $"'{SettingsPath}.inputs' defines '{logical}' more than once.";
                return false;
            }

            if (!used.TryAdd(id!, logical))
            {
                // The readback would have to pick one of them, and whichever it
                // picked would make the other mode report the wrong input forever.
                problem =
                    $"'{SettingsPath}.inputs' points both '{used[id!]}' and '{logical}' at " +
                    $"'{id}'. One input cannot be two, and nothing could say which of them " +
                    "the television is currently showing.";

                return false;
            }
        }

        problem = null;
        return true;
    }

    /// <summary>
    /// Accepts the ways somebody writes an HDMI socket, and answers in the one
    /// way the television does.
    /// </summary>
    /// <remarks>
    /// "HDMI_4", "hdmi4", "HDMI 4" and "4" are all the same socket and all get
    /// typed. The television only answers to one of them, so the translating is
    /// done here rather than left to be got right in the file.
    /// </remarks>
    private static bool TryNormaliseInput(string written, out string? id, out string? problem)
    {
        id = null;

        Match hdmi = Regex.Match(
            written.Trim(),
            @"^(?:hdmi[_\- ]?)?([1-9])$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        if (!hdmi.Success)
        {
            problem = $"'{written.Trim()}' is not an HDMI input. Use HDMI_1 to HDMI_9.";
            return false;
        }

        id = $"HDMI_{hdmi.Groups[1].Value}";
        problem = null;
        return true;
    }

    /// <summary>
    /// Accepts a MAC address written any of the usual ways, and answers in one.
    /// </summary>
    private static bool TryNormaliseMac(string written, out string? mac, out string? problem)
    {
        mac = null;

        string digits = written
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (digits.Length != 12 || !digits.All(Uri.IsHexDigit))
        {
            problem =
                $"'{SettingsPath}.macAddress' is '{written.Trim()}', which is not a MAC address. " +
                "It needs exactly 12 hexadecimal digits, like AA:BB:CC:DD:EE:FF.";

            return false;
        }

        mac = string.Join(":", Convert.FromHexString(digits).Select(part => part.ToString("X2")));
        problem = null;
        return true;
    }
}

/// <summary>
/// The named televisions stored under <c>providers.lgTv</c>.
/// </summary>
/// <remarks>
/// The section may contain several televisions:
///
/// <code>
/// "lgTv": {
///   "default": "Living Room",
///   "televisions": {
///     "Living Room": { ...one LgTvSettings body... },
///     "Bedroom": { ...one LgTvSettings body... }
///   }
/// }
/// </code>
///
/// The single-TV body written before this existed is still accepted, and reads as
/// one television called <see cref="DefaultTelevisionName"/>.
///
/// WHAT "DEFAULT" IS FOR, AND THE ONLY THING IT DOES
///
/// Modes written before televisions had names say <c>tvPower</c>, <c>tvInput</c>,
/// <c>tvSignal</c> and <c>tvProfile</c> -- as does every document this build has
/// ever written, including the shipped example. Those keys have to keep meaning a
/// television, so exactly one television answers to them, and
/// <see cref="DefaultName"/> is the name of the one that does. Every other
/// television is addressed as <c>tv.&lt;name&gt;.power</c> and so on -- see
/// <see cref="LgTvResourceKeys"/>.
///
/// That is the whole of it. Being the default changes nothing about how a
/// television is reached, configured, paired with, read or applied: it is one
/// connection and one set of four resources either way, and only the spelling of
/// the four keys differs. A machine with one television -- which is nearly all of
/// them -- therefore has exactly the four resources it has always had.
/// </remarks>
public sealed record LgTvCatalog
{
    public const string DefaultTelevisionName = "Television";
    public const string DefaultProperty = "default";
    public const string TelevisionsProperty = "televisions";

    public static readonly LgTvCatalog Empty = new(
        new Dictionary<string, LgTvSettings>(StringComparer.OrdinalIgnoreCase),
        null);

    private LgTvCatalog(
        IReadOnlyDictionary<string, LgTvSettings> televisions,
        string? defaultName)
    {
        Televisions = televisions;
        DefaultName = defaultName;
    }

    public IReadOnlyDictionary<string, LgTvSettings> Televisions { get; }

    public string? DefaultName { get; }

    public IReadOnlyList<string> Names =>
        [.. Televisions.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    public LgTvSettings DefaultSettings =>
        DefaultName is not null && Televisions.TryGetValue(DefaultName, out LgTvSettings? settings)
            ? settings
            : LgTvSettings.Disabled;

    public LgTvSettings? SettingsFor(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        Televisions.TryGetValue(name.Trim(), out LgTvSettings? settings)
            ? settings
            : null;

    /// <summary>
    /// Which televisions a whole configuration document declares, and which of
    /// them the historical resource keys mean.
    /// </summary>
    /// <remarks>
    /// The same kind of pre-pass used for applications: it answers only which
    /// provider instances must exist, and what they are called, before the
    /// document can be validated. It deliberately refuses nothing -- invalid
    /// JSON or a malformed television is diagnosed a moment later by the ordinary
    /// configuration parse, where the full and useful message belongs.
    ///
    /// The default is resolved by exactly the rule <see cref="TryParse"/> uses,
    /// because the two must agree: a provider set built for one television and
    /// then handed another's settings would be a resource that silently commands
    /// the wrong device.
    /// </remarks>
    public static LgTvDeclarations DeclarationsIn(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, DocumentReadOptions);

            if (!TryFindProperty(document.RootElement, "providers", out JsonElement providers) ||
                providers.ValueKind != JsonValueKind.Object ||
                !TryFindProperty(providers, LgTvSettings.SectionKey, out JsonElement section) ||
                section.ValueKind != JsonValueKind.Object)
            {
                return LgTvDeclarations.None;
            }

            if (!TryFindProperty(section, TelevisionsProperty, out JsonElement televisions))
            {
                // An object without the named-TV collection is the one-TV shape
                // written before televisions had names. Validation decides
                // whether that body is actually valid.
                return new LgTvDeclarations([DefaultTelevisionName], DefaultTelevisionName);
            }

            if (televisions.ValueKind != JsonValueKind.Object)
            {
                return LgTvDeclarations.None;
            }

            var names = new List<string>();

            foreach (JsonProperty television in televisions.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(television.Name))
                {
                    names.Add(television.Name.Trim());
                }
            }

            string? requested =
                TryFindProperty(section, DefaultProperty, out JsonElement declared) &&
                declared.ValueKind == JsonValueKind.String
                    ? declared.GetString()?.Trim()
                    : null;

            return new LgTvDeclarations(names, ResolveDefault(names, requested));
        }
        catch (JsonException)
        {
            return LgTvDeclarations.None;
        }
    }

    public static bool TryParse(JsonElement? settings, out LgTvCatalog? parsed, out string? problem)
    {
        parsed = null;

        if (settings is not JsonElement element || element.ValueKind == JsonValueKind.Null)
        {
            parsed = Empty;
            problem = null;
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = $"'{LgTvSettings.SettingsPath}' must be an object.";
            return false;
        }

        bool collectionShape = element.EnumerateObject().Any(property =>
            property.NameEquals(DefaultProperty) ||
            property.NameEquals(TelevisionsProperty));

        if (!collectionShape)
        {
            if (!LgTvSettings.TryParse(element, out LgTvSettings? legacy, out problem))
            {
                return false;
            }

            parsed = new LgTvCatalog(
                new Dictionary<string, LgTvSettings>(StringComparer.OrdinalIgnoreCase)
                {
                    [DefaultTelevisionName] = legacy!,
                },
                DefaultTelevisionName);

            return true;
        }

        string? requestedDefault = null;
        JsonElement? televisionSection = null;

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.NameEquals(DefaultProperty))
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    requestedDefault = null;
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    problem = $"'{LgTvSettings.SettingsPath}.{DefaultProperty}' must be a television name in quotes.";
                    return false;
                }

                requestedDefault = property.Value.GetString()!.Trim();
            }
            else if (property.NameEquals(TelevisionsProperty))
            {
                televisionSection = property.Value;
            }
            else
            {
                problem =
                    $"'{LgTvSettings.SettingsPath}' does not understand '{property.Name}' in the named-TV form. " +
                    $"It takes '{DefaultProperty}' and '{TelevisionsProperty}'.";

                return false;
            }
        }

        if (televisionSection is not JsonElement declared)
        {
            problem = $"'{LgTvSettings.SettingsPath}.{TelevisionsProperty}' is required in the named-TV form.";
            return false;
        }

        if (declared.ValueKind != JsonValueKind.Object)
        {
            problem = $"'{LgTvSettings.SettingsPath}.{TelevisionsProperty}' must be an object keyed by television name.";
            return false;
        }

        var televisions = new Dictionary<string, LgTvSettings>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty television in declared.EnumerateObject())
        {
            string name = television.Name.Trim();

            if (name.Length == 0)
            {
                problem = $"A television in '{LgTvSettings.SettingsPath}.{TelevisionsProperty}' has no name.";
                return false;
            }

            if (!LgTvSettings.TryParse(television.Value, out LgTvSettings? tv, out string? televisionProblem))
            {
                problem =
                    $"Television '{name}' in '{LgTvSettings.SettingsPath}.{TelevisionsProperty}' is invalid. " +
                    televisionProblem;

                return false;
            }

            if (!televisions.TryAdd(name, tv!))
            {
                problem =
                    $"'{LgTvSettings.SettingsPath}.{TelevisionsProperty}' defines '{name}' more than once.";
                return false;
            }
        }

        if (televisions.Count == 0)
        {
            if (requestedDefault is not null)
            {
                problem =
                    $"'{LgTvSettings.SettingsPath}.{DefaultProperty}' names '{requestedDefault}', but no televisions are defined.";
                return false;
            }

            parsed = Empty;
            problem = null;
            return true;
        }

        if (requestedDefault is not null &&
            !televisions.Keys.Any(name => string.Equals(name, requestedDefault, StringComparison.OrdinalIgnoreCase)))
        {
            problem =
                $"'{LgTvSettings.SettingsPath}.{DefaultProperty}' names '{requestedDefault}', which is not one of: " +
                $"{string.Join(", ", televisions.Keys.Order(StringComparer.OrdinalIgnoreCase))}.";
            return false;
        }

        parsed = new LgTvCatalog(televisions, ResolveDefault([.. televisions.Keys], requestedDefault));
        problem = null;
        return true;
    }

    /// <summary>
    /// Which of the declared televisions answers to the historical resource keys.
    /// </summary>
    /// <remarks>
    /// Shared by the pre-pass that builds the providers and the parse that
    /// configures them, so the two cannot disagree about which television the
    /// unprefixed keys mean. A document that names a default which does not exist
    /// is refused by the parse; this answers for it anyway, because the pre-pass
    /// runs first and refuses nothing.
    ///
    /// With nothing named, the first in name order -- an answer that does not
    /// depend on the order somebody happened to write the entries in, so adding a
    /// television never silently re-points existing modes at a different one.
    /// </remarks>
    private static string? ResolveDefault(IReadOnlyList<string> names, string? requested)
    {
        if (names.Count == 0)
        {
            return null;
        }

        if (requested is not null)
        {
            string? named = names.FirstOrDefault(name =>
                string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));

            if (named is not null)
            {
                return named;
            }
        }

        return names.Order(StringComparer.OrdinalIgnoreCase).First();
    }

    private static readonly JsonDocumentOptions DocumentReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static bool TryFindProperty(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in parent.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}

/// <summary>
/// The televisions a document declares, before any of it has been validated.
/// </summary>
/// <param name="Names">
/// Every television named in the document, in the order it wrote them, including
/// any it names twice. Deduplicating is the provider builder's job, and refusing
/// is the parser's.
/// </param>
/// <param name="DefaultName">
/// Which of them answers to tvPower/tvInput/tvSignal/tvProfile, or null when the
/// document declares no televisions at all. See <see cref="LgTvCatalog"/>.
/// </param>
public readonly record struct LgTvDeclarations(IReadOnlyList<string> Names, string? DefaultName)
{
    /// <summary>A document with no televisions in it.</summary>
    public static readonly LgTvDeclarations None = new([], null);

    /// <summary>Whether a name is the one the historical resource keys mean.</summary>
    public bool IsDefault(string name) =>
        DefaultName is not null && string.Equals(name, DefaultName, StringComparison.OrdinalIgnoreCase);
}


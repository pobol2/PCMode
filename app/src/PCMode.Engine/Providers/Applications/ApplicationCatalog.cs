using System.Text.Json;

namespace PCMode.Engine;

/// <summary>
/// Reads the applications section, and is the only thing that knows its shape.
/// </summary>
/// <remarks>
/// ONE SECTION, MANY RESOURCES
///
/// Every application is a separate resource, because a mode sets them
/// separately: Movie runs a player and stops a game, and one resource holding
/// both would have to invent a vocabulary for "these running and those not".
/// Separate resources get the planner's dependency ordering, the convergence
/// loop's readback, optionality, availability and dry runs without any of it
/// being written again here.
///
/// They share one settings section for the same reason the television's four
/// resources do: the applications are one list, and splitting it across four
/// sections named after four applications would mean nothing could tell an
/// application's settings from any other provider's.
///
/// READ TWICE, DELIBERATELY
///
/// Once before the providers exist, to learn which resources there are to build,
/// and once when each provider is configured. The first pass is only interested
/// in names; the second is where everything is actually checked, so a malformed
/// entry is refused by the same rules whichever way the document arrived.
/// </remarks>
internal static class ApplicationCatalog
{
    /// <summary>The section every application provider reads.</summary>
    public const string SectionKey = "applications";

    /// <summary>Where this lives in the document.</summary>
    public const string SettingsPath = "providers.applications";

    public const string Running = "Running";

    public const string Stopped = "Stopped";

    /// <summary>How long a readiness action waits for a window by default.</summary>
    private static readonly TimeSpan DefaultReadyWithin = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The shortest wait that is worth writing down.
    /// </summary>
    /// <remarks>
    /// Below this the readback that watches for the application would not run
    /// twice, so the wait would decide the result rather than measuring it. The
    /// convergence policy also refuses a poll longer than the attempt it is inside
    /// of, and this is what keeps that refusal impossible to reach from a file.
    /// </remarks>
    private static readonly TimeSpan ShortestWait = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The names of the applications a document defines, without checking any of
    /// them.
    /// </summary>
    /// <remarks>
    /// The pre-pass, and it answers exactly one question: which resources have to
    /// exist before the document can be validated at all. It refuses nothing --
    /// a name that is unusable is refused by <see cref="TryRead"/> a moment later,
    /// with a message about the application rather than about a missing provider.
    ///
    /// A document that cannot be parsed is not this method's business either. It
    /// answers nothing, and the real parse produces the real error.
    /// </remarks>
    public static IReadOnlyList<string> NamesIn(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, ReadOptions);

            if (!document.RootElement.TryGetProperty("providers", out JsonElement providers) ||
                providers.ValueKind != JsonValueKind.Object ||
                !TryFindSection(providers, out JsonElement section) ||
                section.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var names = new List<string>();

            foreach (JsonProperty application in section.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(application.Name))
                {
                    names.Add(application.Name.Trim());
                }
            }

            return names;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Reads every application, or says what is wrong with one.</summary>
    public static bool TryRead(
        JsonElement? settings,
        out IReadOnlyDictionary<string, ApplicationDefinition> applications,
        out string? problem)
    {
        applications = Empty;

        if (settings is not JsonElement element || element.ValueKind == JsonValueKind.Null)
        {
            // No applications at all is an ordinary machine, not a mistake. A mode
            // naming one is refused where the mode is read, which is where the
            // name that was not defined can actually be quoted.
            problem = null;
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = $"'{SettingsPath}' must be an object mapping an application name to its settings.";
            return false;
        }

        var parsed = new Dictionary<string, ApplicationDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty application in element.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(application.Name))
            {
                problem = $"An application in '{SettingsPath}' has no name.";
                return false;
            }

            string name = application.Name.Trim();

            if (!TryReadApplication(name, application.Value, out ApplicationDefinition? definition, out problem))
            {
                return false;
            }

            if (!parsed.TryAdd(name, definition!))
            {
                // Case-insensitively, because that is how a mode looks one up.
                // Two entries differing only in case means one of them can never
                // be selected and nothing would say which.
                problem = $"'{SettingsPath}' defines '{name}' more than once.";
                return false;
            }
        }

        applications = parsed;
        problem = null;
        return true;
    }

    private static readonly IReadOnlyDictionary<string, ApplicationDefinition> Empty =
        new Dictionary<string, ApplicationDefinition>(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The applications section, matched the way every other key is.</summary>
    private static bool TryFindSection(JsonElement providers, out JsonElement section)
    {
        foreach (JsonProperty property in providers.EnumerateObject())
        {
            if (string.Equals(property.Name, SectionKey, StringComparison.OrdinalIgnoreCase))
            {
                section = property.Value;
                return true;
            }
        }

        section = default;
        return false;
    }

    private static bool TryReadApplication(
        string name,
        JsonElement written,
        out ApplicationDefinition? definition,
        out string? problem)
    {
        definition = null;
        string where = $"'{SettingsPath}.{name}'";

        if (written.ValueKind != JsonValueKind.Object)
        {
            problem = $"{where} must be an object describing how to start and stop the application.";
            return false;
        }

        string? process = null;
        LaunchCommand? start = null;
        LaunchCommand? stop = null;
        bool reactivate = false;
        IReadOnlyList<ResourceRequirement> requires = [];
        IReadOnlyList<string> restartWhen = [];
        ReadinessAction? ready = null;
        TimeSpan startWithin = TimeSpan.FromSeconds(3);
        TimeSpan stopWithin = TimeSpan.FromSeconds(5);

        foreach (JsonProperty field in written.EnumerateObject())
        {
            if (field.NameEquals("process"))
            {
                if (field.Value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(field.Value.GetString()))
                {
                    problem = $"{where}.process must be the process name Windows reports, without '.exe', in quotes.";
                    return false;
                }

                // Windows reports process names without the extension, and a
                // configuration that wrote one would match nothing at all --
                // which reads as "the application is never running" and starts a
                // second copy every transition.
                process = field.Value.GetString()!.Trim();

                if (process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    process = process[..^4];
                }
            }
            else if (field.NameEquals("start"))
            {
                if (!TryReadCommand($"{where}.start", field.Value, out start, out problem))
                {
                    return false;
                }
            }
            else if (field.NameEquals("stop"))
            {
                if (!TryReadCommand($"{where}.stop", field.Value, out stop, out problem))
                {
                    return false;
                }
            }
            else if (field.NameEquals("reactivate"))
            {
                if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    problem = $"{where}.reactivate must be true or false.";
                    return false;
                }

                reactivate = field.Value.ValueKind == JsonValueKind.True;
            }
            else if (field.NameEquals(TargetRequirements.FieldName))
            {
                if (!TargetRequirements.TryParse(where, name, field.Value, out IReadOnlyList<ResourceRequirement>? needed, out problem))
                {
                    return false;
                }

                requires = needed!;
            }
            else if (field.NameEquals("restartWhen"))
            {
                if (!TryReadKeys(name, $"{where}.restartWhen", field.Value, out restartWhen, out problem))
                {
                    return false;
                }
            }
            else if (field.NameEquals("ready"))
            {
                if (!TryReadReady($"{where}.ready", field.Value, out ready, out problem))
                {
                    return false;
                }
            }
            else if (field.NameEquals("startWithin"))
            {
                if (!TryReadWait($"{where}.startWithin", field.Value, out startWithin, out problem))
                {
                    return false;
                }
            }
            else if (field.NameEquals("stopWithin"))
            {
                if (!TryReadWait($"{where}.stopWithin", field.Value, out stopWithin, out problem))
                {
                    return false;
                }
            }
            else
            {
                // Refused rather than ignored, like every other unknown field. A
                // dropped one is a setting the user believes they have written
                // and has not.
                problem =
                    $"{where} does not understand '{field.Name}'. It takes 'process', 'start', 'stop', " +
                    "'reactivate', 'requires', 'restartWhen', 'ready', 'startWithin' and 'stopWithin'.";

                return false;
            }
        }

        if (start is null)
        {
            problem = $"{where} has no 'start'. Give an executable path, or a URI such as \"steam://open/bigpicture\".";
            return false;
        }

        if (process is null)
        {
            if (stop is not null)
            {
                problem = $"{where}.stop needs process detection. Launch-only applications cannot be stopped by PCMode.";
                return false;
            }

            if (reactivate)
            {
                problem = $"{where}.reactivate needs process detection. A launch-only application is dispatched when activated.";
                return false;
            }

            if (restartWhen.Count > 0)
            {
                problem = $"{where}.restartWhen needs process detection so PCMode can identify the copy to restart.";
                return false;
            }

            if (ready is not null)
            {
                problem = $"{where}.ready needs process detection so PCMode can identify the application's window.";
                return false;
            }
        }

        definition = new ApplicationDefinition
        {
            Name = name,
            Observation = process is null
                ? ApplicationObservation.None
                : ApplicationObservation.ForProcess(process),
            Start = start,
            Stop = stop,
            Reactivate = reactivate,
            Requires = requires,
            RestartWhen = restartWhen,
            Ready = ready,
            StartWithin = startWithin,
            StopWithin = stopWithin,
        };

        problem = null;
        return true;
    }

    /// <summary>
    /// Reads a start or stop command, in either of the forms it is written in.
    /// </summary>
    /// <remarks>
    /// TWO FORMS, ONE MEANING
    ///
    /// <c>"start": "steam://open/bigpicture"</c> is what almost every entry says,
    /// and what it is -- a URI or a path -- is decided by whether it has a scheme.
    /// The longer form, <c>{ "executable": "...", "arguments": ["-x"] }</c>, exists
    /// for the two things the short one cannot say: arguments, and "this is a path
    /// even though it looks like something else".
    /// </remarks>
    private static bool TryReadCommand(
        string where,
        JsonElement written,
        out LaunchCommand? command,
        out string? problem)
    {
        command = null;

        if (written.ValueKind == JsonValueKind.String)
        {
            string? value = written.GetString();

            if (string.IsNullOrWhiteSpace(value))
            {
                problem = $"{where} is empty. Give an executable path, or a URI.";
                return false;
            }

            string expanded = Expand(value);

            command = ApplicationDefinition.SchemeOf(expanded) is not null
                ? new LaunchCommand { Uri = expanded }
                : new LaunchCommand { Executable = expanded };

            problem = null;
            return true;
        }

        if (written.ValueKind != JsonValueKind.Object)
        {
            problem =
                $"{where} must be an executable path, a URI, or " +
                "{ \"executable\": \"...\", \"arguments\": [ ... ] }.";

            return false;
        }

        string? executable = null;
        string? uri = null;
        var arguments = new List<string>();

        foreach (JsonProperty field in written.EnumerateObject())
        {
            if (field.NameEquals("executable"))
            {
                if (field.Value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(field.Value.GetString()))
                {
                    problem = $"{where}.executable must be a path, in quotes.";
                    return false;
                }

                executable = Expand(field.Value.GetString()!);
            }
            else if (field.NameEquals("uri"))
            {
                if (field.Value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(field.Value.GetString()))
                {
                    problem = $"{where}.uri must be a URI, in quotes.";
                    return false;
                }

                uri = Expand(field.Value.GetString()!);

                if (ApplicationDefinition.SchemeOf(uri) is null)
                {
                    problem = $"{where}.uri is '{uri}', which has no scheme. A URI looks like \"steam://open/bigpicture\".";
                    return false;
                }
            }
            else if (field.NameEquals("arguments"))
            {
                if (field.Value.ValueKind != JsonValueKind.Array)
                {
                    problem = $"{where}.arguments must be a list, with one entry per argument.";
                    return false;
                }

                foreach (JsonElement argument in field.Value.EnumerateArray())
                {
                    if (argument.ValueKind != JsonValueKind.String)
                    {
                        problem = $"{where}.arguments must contain only values in quotes.";
                        return false;
                    }

                    arguments.Add(Expand(argument.GetString() ?? string.Empty));
                }
            }
            else
            {
                problem = $"{where} does not understand '{field.Name}'. It takes 'executable', 'arguments' and 'uri'.";
                return false;
            }
        }

        if (executable is null && uri is null)
        {
            problem = $"{where} says neither what to run nor what URI to open.";
            return false;
        }

        if (executable is not null && uri is not null)
        {
            // Both would mean two different things to launch and no way to say
            // which was meant. Whichever won would be the one nobody checked.
            problem = $"{where} gives both an executable and a URI. It can only be one of them.";
            return false;
        }

        if (uri is not null && arguments.Count > 0)
        {
            // The shell hands a URI to whatever is registered for the scheme, as
            // one string. There is nowhere for an argument to go.
            problem = $"{where} gives arguments for a URI, which cannot take any. Put them in the URI itself.";
            return false;
        }

        command = uri is not null
            ? new LaunchCommand { Uri = uri }
            : new LaunchCommand { Executable = executable, Arguments = arguments };

        problem = null;
        return true;
    }

    private static bool TryReadReady(
        string where,
        JsonElement written,
        out ReadinessAction? ready,
        out string? problem)
    {
        ready = null;

        if (written.ValueKind != JsonValueKind.Object)
        {
            problem = $"{where} must be an object, like {{ \"activate\": true, \"keys\": \"f\" }}.";
            return false;
        }

        bool activate = false;
        string? keys = null;
        TimeSpan within = DefaultReadyWithin;

        foreach (JsonProperty field in written.EnumerateObject())
        {
            if (field.NameEquals("activate"))
            {
                if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    problem = $"{where}.activate must be true or false.";
                    return false;
                }

                activate = field.Value.ValueKind == JsonValueKind.True;
            }
            else if (field.NameEquals("keys"))
            {
                if (field.Value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(field.Value.GetString()))
                {
                    problem = $"{where}.keys must be the characters to type, in quotes.";
                    return false;
                }

                keys = field.Value.GetString()!;

                foreach (char character in keys)
                {
                    // Printable ASCII and nothing else. Anything richer -- a
                    // modifier, a chord, a named key -- is a small scripting
                    // language for typing at whatever happens to be in front,
                    // and this is not the place to grow one.
                    if (character is < ' ' or > '~')
                    {
                        problem =
                            $"{where}.keys may only contain ordinary printable characters. " +
                            "Modifiers, chords and named keys are not supported.";

                        return false;
                    }
                }
            }
            else if (field.NameEquals("within"))
            {
                if (!TryReadWait($"{where}.within", field.Value, out within, out problem))
                {
                    return false;
                }
            }
            else
            {
                problem = $"{where} does not understand '{field.Name}'. It takes 'activate', 'keys' and 'within'.";
                return false;
            }
        }

        if (!activate && keys is null)
        {
            // A readiness action that does nothing is a wait for a window that
            // nothing then uses, which is a slower transition and no difference.
            problem = $"{where} does nothing. Give it 'activate', 'keys', or remove it.";
            return false;
        }

        ready = new ReadinessAction(activate, keys, within);
        problem = null;
        return true;
    }

    /// <summary>Reads a list of resource keys, refusing the useless ones.</summary>
    private static bool TryReadKeys(
        string owner,
        string where,
        JsonElement written,
        out IReadOnlyList<string> keys,
        out string? problem)
    {
        keys = [];

        if (written.ValueKind != JsonValueKind.Array)
        {
            problem = $"{where} must be a list of resource names, like [ \"display\" ].";
            return false;
        }

        var parsed = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement entry in written.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString()))
            {
                problem = $"{where} must contain only resource names, in quotes.";
                return false;
            }

            string key = entry.GetString()!.Trim();

            if (string.Equals(key, owner, StringComparison.OrdinalIgnoreCase))
            {
                // An application made stale by its own starting would be
                // restarted for ever, one transition at a time.
                problem = $"{where} names '{key}', which is the application itself.";
                return false;
            }

            if (!seen.Add(key))
            {
                problem = $"{where} names '{key}' more than once.";
                return false;
            }

            parsed.Add(key);
        }

        keys = parsed;
        problem = null;
        return true;
    }

    /// <summary>Reads a wait, in milliseconds, and refuses one that cannot work.</summary>
    private static bool TryReadWait(string where, JsonElement written, out TimeSpan wait, out string? problem)
    {
        wait = TimeSpan.Zero;

        if (written.ValueKind != JsonValueKind.Number || !written.TryGetInt32(out int milliseconds))
        {
            problem = $"{where} must be a whole number of milliseconds.";
            return false;
        }

        TimeSpan given = TimeSpan.FromMilliseconds(milliseconds);

        if (given < ShortestWait || given > LongestWait)
        {
            problem =
                $"{where} is {milliseconds}ms, and must be between {ShortestWait.TotalMilliseconds:0}ms " +
                $"and {LongestWait.TotalSeconds:0} seconds.";

            return false;
        }

        wait = given;
        problem = null;
        return true;
    }

    /// <summary>
    /// Expands %VARIABLES%, which is how a path to somebody's profile is written.
    /// </summary>
    /// <remarks>
    /// Done once, when the document is read, so that everything downstream --
    /// availability, the launch, the message when it is not there -- is talking
    /// about the same real path.
    /// </remarks>
    private static string Expand(string value) =>
        Environment.ExpandEnvironmentVariables(value.Trim());
}

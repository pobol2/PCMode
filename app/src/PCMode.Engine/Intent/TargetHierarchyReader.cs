using System.Text.Json;

namespace PCMode.Engine;

/// <summary>
/// Reads 'spaces' into the intent model, refusing anything that could not be
/// activated.
/// </summary>
/// <remarks>
/// VALIDATED WHOLE, BEFORE ANYTHING IS COMPOSED
///
/// Identities, relationships and every resource value are checked here, against
/// the same real providers <see cref="ModeConfiguration"/> validates a flat
/// document with. A hierarchy with one unusable value is refused entire, for
/// the reason the flat form always was: a half-loaded configuration is a list
/// where some targets work and some quietly do nothing.
///
/// WHAT AN ID IS, AND WHY IT IS NOT A NAME
///
/// An ID is the stable half of an identity: references point at it, and it is
/// deliberately restricted to characters that can appear in a key without
/// needing to be escaped, quoted or guessed at. The name beside it is the
/// user's, may be changed freely, and is what every screen shows.
/// </remarks>
internal static class TargetHierarchyReader
{
    /// <summary>Characters an ID may contain besides letters and digits.</summary>
    private const string ExtraIdCharacters = "-_.";

    private const int MaximumIdLength = 64;

    /// <summary>Reads the whole 'spaces' array, or refuses the document.</summary>
    public static IReadOnlyList<ConfiguredSpace> Read(
        JsonElement spaces,
        Dictionary<string, IModeResourceProvider> known)
    {
        if (spaces.ValueKind != JsonValueKind.Array)
        {
            throw new ModeConfigurationException("'spaces' must be an array.");
        }

        var read = new List<ConfiguredSpace>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement element in spaces.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new ModeConfigurationException("Each entry of 'spaces' must be an object.");
            }

            string id = ReadId(element, "A space");
            string name = ReadName(element, $"Space '{id}'");

            if (!ids.Add(id))
            {
                throw new ModeConfigurationException($"'{id}' is the id of more than one space.");
            }

            if (!names.Add(name))
            {
                // Names are how a target is shown and how an old reference is
                // matched, so two spaces called the same thing would leave
                // 'Desk — Gaming' meaning either of two different targets.
                throw new ModeConfigurationException($"'{name}' is the name of more than one space.");
            }

            read.Add(new ConfiguredSpace(
                id,
                name,
                ResourceAssignments.Read($"Space '{name}'", Property(element, "resources"), known),
                ReadActivities(element, name, known)));
        }

        return read;
    }

    private static IReadOnlyList<ConfiguredActivity> ReadActivities(
        JsonElement space,
        string spaceName,
        Dictionary<string, IModeResourceProvider> known)
    {
        JsonElement? declared = Property(space, "activities");

        if (declared is not JsonElement element || element.ValueKind == JsonValueKind.Null)
        {
            // A Space with nothing in it yet is a Space somebody has just
            // created and is about to fill in, not a broken document.
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ModeConfigurationException($"Space '{spaceName}': 'activities' must be an array.");
        }

        var read = new List<ConfiguredActivity>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new ModeConfigurationException(
                    $"Space '{spaceName}': each entry of 'activities' must be an object.");
            }

            string id = ReadId(entry, $"An activity of space '{spaceName}'");
            string name = ReadName(entry, $"Activity '{id}' of space '{spaceName}'");

            if (!ids.Add(id))
            {
                throw new ModeConfigurationException(
                    $"Space '{spaceName}' has more than one activity with the id '{id}'.");
            }

            if (!names.Add(name))
            {
                throw new ModeConfigurationException(
                    $"Space '{spaceName}' has more than one activity called '{name}'.");
            }

            string where = $"Activity '{spaceName} — {name}'";

            read.Add(new ConfiguredActivity(
                id,
                name,
                ResourceAssignments.Read(where, Property(entry, "resources"), known),
                ReadSources(entry, where, known))
            {
                Enabled = ReadEnabled(entry, where),
            });
        }

        return read;
    }

    private static IReadOnlyList<ConfiguredSource> ReadSources(
        JsonElement activity,
        string where,
        Dictionary<string, IModeResourceProvider> known)
    {
        JsonElement? declared = Property(activity, "sources");

        if (declared is not JsonElement element || element.ValueKind == JsonValueKind.Null)
        {
            // An Activity with no Sources is an ordinary Activity, not an
            // incomplete one: it is activated directly.
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ModeConfigurationException($"{where}: 'sources' must be an array.");
        }

        var read = new List<ConfiguredSource>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new ModeConfigurationException($"{where}: each entry of 'sources' must be an object.");
            }

            string id = ReadId(entry, $"A source of {where}");
            string name = ReadName(entry, $"Source '{id}' of {where}");

            if (!ids.Add(id))
            {
                throw new ModeConfigurationException($"{where} has more than one source with the id '{id}'.");
            }

            if (!names.Add(name))
            {
                throw new ModeConfigurationException($"{where} has more than one source called '{name}'.");
            }

            string sourceWhere = $"Source '{name}' of {where}";

            read.Add(new ConfiguredSource(
                id,
                name,
                ReadKind(entry, sourceWhere),
                ResourceAssignments.Read(sourceWhere, Property(entry, "resources"), known))
            {
                Enabled = ReadEnabled(entry, sourceWhere),
            });
        }

        return read;
    }

    /// <summary>
    /// What kind of thing a Source is, defaulting to a program on this PC.
    /// </summary>
    /// <remarks>
    /// Defaulted rather than required because it changes nothing about what a
    /// Source does -- it is how the Source is described -- and requiring it
    /// would make every hand-written entry longer for no gain. An unrecognised
    /// one is still refused: a typo that silently became "a PC application"
    /// would misdescribe a games console on every screen that mentions it.
    /// </remarks>
    private static SourceKind ReadKind(JsonElement element, string where)
    {
        string? declared = ReadString(element, "kind");

        return declared?.Trim().ToLowerInvariant() switch
        {
            null or "" or "pcapplication" => SourceKind.PcApplication,
            "externaldevice" => SourceKind.ExternalDevice,
            "tvapplication" => SourceKind.TvApplication,
            string value => throw new ModeConfigurationException(
                $"{where} is of kind '{value}'. Use pcApplication, externalDevice or tvApplication."),
        };
    }

    /// <summary>Targets are enabled unless the document explicitly turns one off.</summary>
    private static bool ReadEnabled(JsonElement element, string where)
    {
        if (Property(element, "enabled") is not JsonElement declared)
        {
            return true;
        }

        return declared.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ModeConfigurationException($"{where}: 'enabled' must be true or false."),
        };
    }

    private static string ReadId(JsonElement element, string what)
    {
        string? declared = ReadString(element, "id");

        if (string.IsNullOrWhiteSpace(declared))
        {
            throw new ModeConfigurationException($"{what} has no id.");
        }

        string id = declared.Trim();

        if (id.Length > MaximumIdLength)
        {
            throw new ModeConfigurationException(
                $"{what} has an id of {id.Length} characters; {MaximumIdLength} is the most an id may be.");
        }

        foreach (char character in id)
        {
            if (!char.IsAsciiLetterOrDigit(character) && !ExtraIdCharacters.Contains(character, StringComparison.Ordinal))
            {
                throw new ModeConfigurationException(
                    $"{what} has the id '{id}'. An id may contain letters, digits, and {ExtraIdCharacters} " +
                    "-- it is what favorites, startup and the recorded current target point at, so it cannot " +
                    "contain spaces or separators.");
            }
        }

        return id;
    }

    private static string ReadName(JsonElement element, string what)
    {
        string? declared = ReadString(element, "name");

        if (string.IsNullOrWhiteSpace(declared))
        {
            throw new ModeConfigurationException($"{what} has no name.");
        }

        return declared.Trim();
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (Property(element, name) is not JsonElement value || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new ModeConfigurationException($"'{name}' must be a value in quotes.");
    }

    /// <summary>One property, matched the way every other name in this document is.</summary>
    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}

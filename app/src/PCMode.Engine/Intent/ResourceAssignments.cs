using System.Text.Json;

namespace PCMode.Engine;

/// <summary>
/// Reads a 'resources' object, in every form one is written in.
/// </summary>
/// <remarks>
/// THREE FORMS, ONE MEANING EACH
///
/// <c>"theme": "Dark"</c> is what every existing document says and goes on
/// meaning exactly what it meant: a resource this level requires. The longer
/// form, <c>"theme": { "value": "Dark", "optional": true }</c>, exists only to
/// say the one thing the short form cannot -- see
/// <see cref="ConfiguredResource.Optional"/>. And <c>"theme": null</c> clears
/// an inherited resource, which is only meaningful below something to inherit
/// from and is refused where there is nothing.
///
/// Optional is about availability and nothing else. It does not make a value
/// less checked, a provider less required to exist, or a failure less of a
/// failure -- an unknown key and an unusable value are refused whether or not
/// the resource is optional, because those are mistakes in the file rather than
/// facts about the machine.
///
/// Enumerated rather than deserialised into a dictionary so that two entries
/// for one resource can be seen at all. A dictionary keeps the last one, and a
/// level where a line silently does nothing is worse than one that is refused
/// by name.
/// </remarks>
internal static class ResourceAssignments
{
    /// <summary>
    /// One level's resources.
    /// </summary>
    /// <param name="where">
    /// What to call this level in a message: the whole point of refusing a
    /// document is that the person reading the message can find the line.
    /// </param>
    public static List<ResourceAssignment> Read(
        string where,
        JsonElement? declared,
        Dictionary<string, IModeResourceProvider> known)
    {
        var resources = new List<ResourceAssignment>();

        if (declared is not JsonElement element || element.ValueKind == JsonValueKind.Null)
        {
            return resources;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ModeConfigurationException($"{where}: 'resources' must be an object.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty resource in element.EnumerateObject())
        {
            if (!known.TryGetValue(resource.Name, out IModeResourceProvider? provider))
            {
                throw new ModeConfigurationException(
                    $"{where} sets '{resource.Name}', which nothing here knows how to apply. " +
                    $"Known: {string.Join(", ", known.Keys.Order(StringComparer.OrdinalIgnoreCase))}.");
            }

            if (!seen.Add(provider.Key))
            {
                // Case-insensitively, because that is how a resource is matched
                // to its provider. Two values for one resource is a level with no
                // single meaning, and whichever ran last would silently be the
                // one that counted.
                throw new ModeConfigurationException($"{where} sets '{provider.Key}' more than once.");
            }

            if (resource.Value.ValueKind == JsonValueKind.Null)
            {
                // Recorded rather than dropped. "Not inherited here" is a
                // decision the document is making, and composition has to be
                // able to see it.
                resources.Add(new ResourceAssignment(provider.Key, Value: null));
                continue;
            }

            (string value, bool optional) = ReadOne(where, provider.Key, resource.Value);

            if (!provider.IsValidValue(value, out string? problem))
            {
                throw new ModeConfigurationException($"{where}: {problem}");
            }

            resources.Add(new ResourceAssignment(provider.Key, value, optional));
        }

        return resources;
    }

    /// <summary>The value and the optionality, from either written form.</summary>
    private static (string Value, bool Optional) ReadOne(string where, string key, JsonElement written)
    {
        if (written.ValueKind == JsonValueKind.String)
        {
            return (written.GetString() ?? string.Empty, false);
        }

        if (written.ValueKind != JsonValueKind.Object)
        {
            throw new ModeConfigurationException(
                $"{where} sets '{key}' to something that is neither a value nor a " +
                $"{{ \"value\": ..., \"optional\": ... }} object.");
        }

        string? value = null;
        bool optional = false;
        bool said = false;

        foreach (JsonProperty field in written.EnumerateObject())
        {
            if (field.NameEquals("value"))
            {
                if (field.Value.ValueKind == JsonValueKind.Null)
                {
                    throw new ModeConfigurationException(
                        $"{where}: '{key}.value' is empty. Write the whole resource as null to clear it.");
                }

                if (field.Value.ValueKind != JsonValueKind.String)
                {
                    throw new ModeConfigurationException($"{where}: '{key}.value' must be a value in quotes.");
                }

                value = field.Value.GetString();
                said = true;
            }
            else if (field.NameEquals("optional"))
            {
                optional = field.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw new ModeConfigurationException(
                        $"{where}: '{key}.optional' must be true or false."),
                };
            }
            else
            {
                // Refused rather than ignored, like every other unknown field. A
                // dropped one is a setting the user believes they have written
                // and has not.
                throw new ModeConfigurationException(
                    $"{where}: '{key}' does not understand '{field.Name}'. It takes 'value' and 'optional'.");
            }
        }

        if (!said || value is null)
        {
            throw new ModeConfigurationException($"{where}: '{key}' says whether it is optional but not what it is.");
        }

        return (value, optional);
    }
}

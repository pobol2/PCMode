using System.Text.Json;

namespace PCMode.Engine;

/// <summary>
/// Reads the DDC/CI monitor section, and is the only thing that knows its shape.
/// </summary>
/// <remarks>
/// ONE NAME, ONE DEVICE PATH
///
/// A mode never names a monitor by device path -- see
/// <see cref="DdcMonitorConnection"/>. This is where the alias somebody chose
/// is bound to the exact monitor Windows enumerates, the same division
/// <see cref="DisplayProvider"/> keeps for its own <c>monitors</c> map, and
/// deliberately the same shape: a flat object from a chosen name to a device
/// path. A user who has already found a monitor's path for a display layout
/// can paste the identical string here.
///
/// READ TWICE
///
/// Once before the providers exist, to learn which aliases must become
/// resources -- <see cref="NamesIn"/> -- and once when each connection is
/// configured -- <see cref="TryRead"/>. See <see cref="ApplicationCatalog"/>
/// for why that split exists.
/// </remarks>
internal static class DdcMonitorCatalog
{
    /// <summary>The section every DDC/CI provider reads.</summary>
    public const string SectionKey = "ddcMonitors";

    /// <summary>Where this lives in the document.</summary>
    public const string SettingsPath = "providers.ddcMonitors";

    /// <summary>
    /// The aliases a document declares, without checking any of them.
    /// </summary>
    /// <remarks>
    /// The pre-pass: it answers only which resources have to exist before the
    /// document can be validated at all, and refuses nothing -- an unusable
    /// entry is refused by <see cref="TryRead"/> a moment later, with a message
    /// about the monitor rather than about a missing provider.
    /// </remarks>
    public static IReadOnlyList<string> NamesIn(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, ReadOptions);

            if (
                !document.RootElement.TryGetProperty("providers", out JsonElement providers)
                || providers.ValueKind != JsonValueKind.Object
                || !TryFindSection(providers, out JsonElement section)
                || section.ValueKind != JsonValueKind.Object
            )
            {
                return [];
            }

            var names = new List<string>();

            foreach (JsonProperty monitor in section.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(monitor.Name))
                {
                    names.Add(monitor.Name.Trim());
                }
            }

            return names;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Reads every configured monitor, or says what is wrong with one.</summary>
    public static bool TryRead(
        JsonElement? settings,
        out IReadOnlyDictionary<string, string> monitors,
        out string? problem
    )
    {
        monitors = Empty;

        if (settings is not JsonElement element || element.ValueKind == JsonValueKind.Null)
        {
            // No DDC/CI monitors at all is an ordinary machine, not a mistake.
            // A mode naming one is refused where the resource is configured,
            // which is where the alias that was not defined can actually be
            // quoted.
            problem = null;
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = $"'{SettingsPath}' must be an object mapping a name you choose to a monitor's device path.";
            return false;
        }

        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty monitor in element.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(monitor.Name))
            {
                problem = $"A monitor in '{SettingsPath}' has no name.";
                return false;
            }

            string alias = monitor.Name.Trim();

            if (monitor.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(monitor.Value.GetString()))
            {
                // An empty mapping is worse than a missing one: the alias
                // exists, so every mode using it validates, and the failure
                // waits until a transition is already under way.
                problem =
                    $"'{SettingsPath}.{alias}' is empty. It needs the exact device path of a monitor, "
                    + $"which '{DisplayProvider.MonitorsPath}' and the display Health line both report.";

                return false;
            }

            // Only the surrounding whitespace, which is what a copied value
            // picks up. Nothing about the path itself is interpreted: it is
            // matched exactly against what Windows enumerates.
            if (!parsed.TryAdd(alias, monitor.Value.GetString()!.Trim()))
            {
                problem = $"'{SettingsPath}' defines '{alias}' more than once.";
                return false;
            }
        }

        monitors = parsed;
        problem = null;
        return true;
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>(
        StringComparer.OrdinalIgnoreCase
    );

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

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
}

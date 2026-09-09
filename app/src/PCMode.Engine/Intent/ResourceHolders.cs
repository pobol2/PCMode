using System.Text.Json.Nodes;

namespace PCMode.Engine;

/// <summary>
/// Every place in a configuration document that sets resources, whatever shape
/// the document is in.
/// </summary>
/// <remarks>
/// WHY THE OTHER STORES NEED THIS
///
/// Renaming a display layout, an audio alias or an application means finding
/// every resource that names it. That used to be "walk modes[]", and a
/// hierarchy puts resources at three levels instead of one. Every store that
/// has to reach them walks this, so a rename cannot quietly miss the Sources
/// and leave a document naming a layout that no longer exists.
///
/// The label is what a message calls the level: it is the whole value of
/// telling somebody which parts of their configuration are in the way.
/// </remarks>
internal static class ResourceHolders
{
    /// <summary>Every 'resources' object in the document, with what to call it.</summary>
    internal static IReadOnlyList<(string Label, JsonObject Resources)> In(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var found = new List<(string, JsonObject)>();

        if (HardwareConfigurationStore.FindProperty(document, "spaces") is JsonArray spaces)
        {
            foreach (JsonNode? spaceNode in spaces)
            {
                if (spaceNode is not JsonObject space)
                {
                    continue;
                }

                string spaceName = NameOf(space);
                Add(found, spaceName, space);

                if (HardwareConfigurationStore.FindProperty(space, "activities") is not JsonArray activities)
                {
                    continue;
                }

                foreach (JsonNode? activityNode in activities)
                {
                    if (activityNode is not JsonObject activity)
                    {
                        continue;
                    }

                    string activityName = $"{spaceName} — {NameOf(activity)}";
                    Add(found, activityName, activity);

                    if (HardwareConfigurationStore.FindProperty(activity, "sources") is not JsonArray sources)
                    {
                        continue;
                    }

                    foreach (JsonNode? sourceNode in sources)
                    {
                        if (sourceNode is JsonObject source)
                        {
                            Add(found, $"{activityName} — {NameOf(source)}", source);
                        }
                    }
                }
            }
        }

        return found;
    }

    private static void Add(List<(string, JsonObject)> found, string label, JsonObject holder)
    {
        if (HardwareConfigurationStore.FindProperty(holder, "resources") is JsonObject resources)
        {
            found.Add((label, resources));
        }
    }

    private static string NameOf(JsonObject holder) =>
        HardwareConfigurationStore.FindProperty(holder, "name") is JsonValue value
        && value.TryGetValue(out string? name)
        && !string.IsNullOrWhiteSpace(name)
            ? name
            : "?";
}

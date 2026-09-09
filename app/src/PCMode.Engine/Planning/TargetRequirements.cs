using System.Text.Json;

namespace PCMode.Engine;

/// <summary>
/// Reads a "requires" block: what else has to be true for one target to mean
/// anything on this machine.
/// </summary>
/// <remarks>
/// WHY THIS IS CONFIGURATION AND NOT CODE
///
/// That sound over HDMI needs the television awake is a fact about televisions,
/// and a provider can state it. That the layout somebody calls "TVOnly" needs the
/// television on the input somebody calls "PC" is a fact about one house's
/// cabling: which HDMI socket the machine is plugged into, what the layouts were
/// named, whether there is a television at all. Nothing shipped can know it, and a
/// provider that guessed would be wrong on every machine but one.
///
/// So the relationship is declared where the names it uses are declared. The
/// provider hands what it read to the planner through
/// <see cref="IDependentProvider.RequirementsFor"/>, and the planner treats it
/// exactly like a requirement any provider states in code -- same ordering, same
/// promotion, same conflict detection, same refusal. There is no configured kind
/// of dependency and no code kind.
///
/// ONE SHARED READER
///
/// Display and audio both need this, and a second implementation of it would be a
/// second set of rules about what a malformed requirement is. Both get the same
/// strictness: an object, string values, no blanks, no repeats, and a resource key
/// that is not this resource's own.
///
/// WHAT IS NOT CHECKED HERE
///
/// Whether the named resource exists, and whether the named target is one it
/// accepts. Those are questions for the providers that own them, and the planner
/// already asks both when it resolves a requirement -- with better messages than
/// anything this could produce, because it can name the provider that refused.
/// Checking here as well would be a second opinion that has to be kept in
/// agreement by hand.
/// </remarks>
internal static class TargetRequirements
{
    /// <summary>The field a target's requirements are written under.</summary>
    public const string FieldName = "requires";

    /// <summary>
    /// Reads one "requires" object, or says what is wrong with it.
    /// </summary>
    /// <param name="where">
    /// How to name the thing being read, for a message: "Display layout 'TVOnly'".
    /// </param>
    /// <param name="owner">
    /// The resource key this belongs to, so that a target requiring its own
    /// resource is refused rather than becoming a self-edge nothing can satisfy.
    /// </param>
    public static bool TryParse(
        string where,
        string owner,
        JsonElement element,
        out IReadOnlyList<ResourceRequirement>? requirements,
        out string? problem
    )
    {
        requirements = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem =
                $"{where} has a '{FieldName}' that is not an object. "
                + $"It maps a resource to the value it must be set to, like "
                + $"{{ \"tvInput\": \"PC\" }}.";

            return false;
        }

        var parsed = new List<ResourceRequirement>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty required in element.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(required.Name))
            {
                problem = $"{where} has a '{FieldName}' entry with no resource name.";
                return false;
            }

            string key = required.Name.Trim();

            if (string.Equals(key, owner, StringComparison.OrdinalIgnoreCase))
            {
                // A target that requires its own resource to be something else is
                // asking for two values at once, and one that requires its own
                // resource to be itself says nothing. Neither is worth carrying
                // into the planner to be refused less clearly there.
                problem = $"{where} says it requires '{key}', which is what it sets.";
                return false;
            }

            if (
                required.Value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(required.Value.GetString())
            )
            {
                problem =
                    $"{where} requires '{key}' but does not say what to set it to. " + "Give the value in quotes.";

                return false;
            }

            if (!seen.Add(key))
            {
                // Case-insensitively, because that is how a resource key is
                // matched. Two demands for one resource is a requirement with no
                // single meaning, and whichever was read last would silently win.
                problem = $"{where} requires '{key}' more than once.";
                return false;
            }

            parsed.Add(new ResourceRequirement(key, required.Value.GetString()!.Trim()));
        }

        requirements = parsed;
        problem = null;
        return true;
    }
}

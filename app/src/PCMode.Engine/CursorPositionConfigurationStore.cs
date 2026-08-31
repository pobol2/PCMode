using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCMode.Engine;

/// <summary>
/// Creates, renames and deletes cursor positions in the same configuration
/// file <see cref="CursorProvider"/> reads, by editing
/// 'providers.cursor.positions'.
/// </summary>
/// <remarks>
/// NO SECOND SOURCE OF TRUTH
///
/// This writes exactly the document <see cref="CursorProvider"/> parses, and
/// validates the result the same way <see cref="EngineBootstrap.Create"/> does,
/// through <see cref="EngineBootstrap.ProductionProviders"/> and
/// <see cref="ModeConfiguration.Parse"/>, before a single byte reaches disk.
///
/// RENAME AND DELETE FOLLOW REFERENCES
///
/// A position's name is what a mode's 'cursor' resource names, so a rename
/// rewrites every mode that names the old one, and a delete is refused -- with
/// nothing changed -- while any mode still names it.
/// </remarks>
public static class CursorPositionConfigurationStore
{
    /// <summary>
    /// Creates a new position, or replaces an existing one of the same name.
    /// Exactly one rule per axis: <paramref name="xPercent"/> or
    /// <paramref name="xFromRight"/>, and <paramref name="yPercent"/> or
    /// <paramref name="yFromBottom"/>.
    /// </summary>
    public static ModeSaveOutcome Save(
        string? root,
        string positionName,
        double? xPercent,
        int? xFromRight,
        double? yPercent,
        int? yFromBottom)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(positionName);

        string trimmed = positionName.Trim();

        if (xPercent is null == xFromRight is null)
        {
            return ModeSaveOutcome.Failed("Choose either a percentage or a distance from the right for X, not both or neither.");
        }

        if (yPercent is null == yFromBottom is null)
        {
            return ModeSaveOutcome.Failed("Choose either a percentage or a distance from the bottom for Y, not both or neither.");
        }

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            JsonObject providers = HardwareConfigurationStore.AsObject(document, "providers");
            JsonObject cursor = HardwareConfigurationStore.AsObject(providers, "cursor");
            JsonObject positions = HardwareConfigurationStore.AsObject(cursor, "positions");

            var body = new JsonObject();

            if (xPercent is not null)
            {
                body["xPercent"] = xPercent.Value;
            }
            else
            {
                body["xFromRight"] = xFromRight!.Value;
            }

            if (yPercent is not null)
            {
                body["yPercent"] = yPercent.Value;
            }
            else
            {
                body["yFromBottom"] = yFromBottom!.Value;
            }

            HardwareConfigurationStore.RemoveCaseInsensitive(positions, trimmed);
            positions.Add(trimmed, body);
            return null;
        });
    }

    /// <summary>
    /// Renames a position and every mode's 'cursor' resource that names it.
    /// </summary>
    public static ModeSaveOutcome Rename(string? root, string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        string trimmedNew = newName.Trim();

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            if (HardwareConfigurationStore.FindProperty(document, "providers") is not JsonObject providers ||
                HardwareConfigurationStore.FindProperty(providers, "cursor") is not JsonObject cursor ||
                HardwareConfigurationStore.FindProperty(cursor, "positions") is not JsonObject positions)
            {
                return ModeSaveOutcome.Failed($"'{oldName}' is not a configured cursor position.");
            }

            JsonNode? existing = HardwareConfigurationStore.FindProperty(positions, oldName);

            if (existing is null)
            {
                return ModeSaveOutcome.Failed($"'{oldName}' is not a configured cursor position.");
            }

            if (!string.Equals(oldName, trimmedNew, StringComparison.OrdinalIgnoreCase) &&
                HardwareConfigurationStore.FindProperty(positions, trimmedNew) is not null)
            {
                return ModeSaveOutcome.Failed($"'{trimmedNew}' is already a configured cursor position.");
            }

            positions.Remove(FindKey(positions, oldName)!);
            positions.Add(trimmedNew, existing.DeepClone());

            LayoutConfigurationStore.RenameModeReferences(document, CursorProvider.ResourceKey, oldName, trimmedNew);
            return null;
        });
    }

    /// <summary>
    /// Deletes a position, refused while any mode's 'cursor' resource still
    /// names it.
    /// </summary>
    public static ModeSaveOutcome Delete(string? root, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            if (HardwareConfigurationStore.FindProperty(document, "providers") is not JsonObject providers ||
                HardwareConfigurationStore.FindProperty(providers, "cursor") is not JsonObject cursor ||
                HardwareConfigurationStore.FindProperty(cursor, "positions") is not JsonObject positions)
            {
                return ModeSaveOutcome.Failed($"'{name}' is not a configured cursor position.");
            }

            string? key = FindKey(positions, name);

            if (key is null)
            {
                return ModeSaveOutcome.Failed($"'{name}' is not a configured cursor position.");
            }

            IReadOnlyList<string> referencing =
                LayoutConfigurationStore.ModesReferencing(document, CursorProvider.ResourceKey, name);

            if (referencing.Count > 0)
            {
                return ModeSaveOutcome.Failed(
                    $"'{name}' is used by {string.Join(", ", referencing)}. Change those first.");
            }

            positions.Remove(key);
            return null;
        });
    }

    private static string? FindKey(JsonObject obj, string name) =>
        obj.Select(pair => pair.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
}

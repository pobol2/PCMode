using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCMode.Engine;

/// <summary>One monitor's row in a layout being edited.</summary>
/// <param name="Monitor">A logical monitor name, already defined under 'providers.display.monitors'.</param>
/// <param name="Primary">Whether this is the layout's primary. Exactly one row must set this.</param>
/// <param name="Direction">
/// How this monitor sits relative to <see cref="Anchor"/> -- 'LeftOf', 'RightOf', 'Above' or
/// 'Below'. Required for every non-primary row; ignored for the primary, which has no placement.
/// </param>
/// <param name="Anchor">The other included monitor this one is placed against. Required alongside <see cref="Direction"/>.</param>
/// <param name="RefreshHz">The rate to request, or null to accept whatever Windows chooses.</param>
public sealed record LayoutMonitorEdit(string Monitor, bool Primary, string? Direction, string? Anchor, double? RefreshHz);

/// <summary>
/// Creates, renames and deletes display layouts in the same configuration file
/// <see cref="DisplayProvider"/> reads, by editing 'providers.display.layouts'.
/// </summary>
/// <remarks>
/// NO SECOND SOURCE OF TRUTH
///
/// This writes exactly the document <see cref="DisplayProvider"/> parses, and
/// validates the result the same way <see cref="EngineBootstrap.Create"/> does,
/// through <see cref="EngineBootstrap.ProductionProviders"/> and
/// <see cref="ModeConfiguration.Parse"/>, before a single byte reaches disk. A
/// topology that does not form one connected, non-cyclic arrangement -- or that
/// asks for two primaries, or a placement anchored on a monitor that is not
/// included -- is refused with the same message a real startup would refuse it
/// with, by <see cref="DisplayLayout.TryParse"/> itself: there is no second,
/// hand-rolled topology check here to disagree with it.
///
/// RENAME AND DELETE FOLLOW REFERENCES
///
/// A layout's name is what a mode's 'display' resource names, so a rename
/// rewrites every mode that names the old one, and a delete is refused -- with
/// nothing changed -- while any mode still names it.
/// </remarks>
public static class LayoutConfigurationStore
{
    /// <summary>
    /// Creates a new layout, or replaces an existing one of the same name,
    /// under 'providers.display.layouts'.
    /// </summary>
    public static ModeSaveOutcome Save(string? root, string layoutName, IReadOnlyList<LayoutMonitorEdit> monitors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layoutName);
        ArgumentNullException.ThrowIfNull(monitors);

        string trimmed = layoutName.Trim();

        if (monitors.Count == 0)
        {
            return ModeSaveOutcome.Failed("Add at least one monitor to the layout.");
        }

        if (monitors.Count(monitor => monitor.Primary) != 1)
        {
            return ModeSaveOutcome.Failed("Exactly one included monitor must be primary.");
        }

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            JsonObject providers = HardwareConfigurationStore.AsObject(document, "providers");
            JsonObject display = HardwareConfigurationStore.AsObject(providers, "display");
            JsonObject layouts = HardwareConfigurationStore.AsObject(display, "layouts");

            var body = new JsonObject
            {
                ["monitors"] = new JsonArray([.. monitors.Select(monitor => JsonValue.Create(monitor.Monitor))]),
                ["primary"] = monitors.First(monitor => monitor.Primary).Monitor,
            };

            var placement = new JsonArray();

            foreach (LayoutMonitorEdit monitor in monitors.Where(monitor => !monitor.Primary))
            {
                if (string.IsNullOrWhiteSpace(monitor.Direction) || string.IsNullOrWhiteSpace(monitor.Anchor))
                {
                    return ModeSaveOutcome.Failed(
                        $"'{monitor.Monitor}' needs a placement relative to another included monitor.");
                }

                placement.Add(JsonValue.Create($"{monitor.Monitor} {monitor.Direction} {monitor.Anchor}"));
            }

            if (placement.Count > 0)
            {
                body["placement"] = placement;
            }

            var refresh = new JsonArray();

            foreach (LayoutMonitorEdit monitor in monitors.Where(monitor => monitor.RefreshHz is not null))
            {
                refresh.Add(JsonValue.Create(
                    $"{monitor.Monitor} {monitor.RefreshHz!.Value.ToString(CultureInfo.InvariantCulture)}"));
            }

            if (refresh.Count > 0)
            {
                body["refresh"] = refresh;
            }

            HardwareConfigurationStore.RemoveCaseInsensitive(layouts, trimmed);
            layouts.Add(trimmed, body);
            return null;
        });
    }

    /// <summary>
    /// Renames a layout and every mode's 'display' resource that names it.
    /// </summary>
    public static ModeSaveOutcome Rename(string? root, string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        string trimmedNew = newName.Trim();

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            if (HardwareConfigurationStore.FindProperty(document, "providers") is not JsonObject providers ||
                HardwareConfigurationStore.FindProperty(providers, "display") is not JsonObject display ||
                HardwareConfigurationStore.FindProperty(display, "layouts") is not JsonObject layouts)
            {
                return ModeSaveOutcome.Failed($"'{oldName}' is not a configured display layout.");
            }

            JsonNode? existing = HardwareConfigurationStore.FindProperty(layouts, oldName);

            if (existing is null)
            {
                return ModeSaveOutcome.Failed($"'{oldName}' is not a configured display layout.");
            }

            if (!string.Equals(oldName, trimmedNew, StringComparison.OrdinalIgnoreCase) &&
                HardwareConfigurationStore.FindProperty(layouts, trimmedNew) is not null)
            {
                return ModeSaveOutcome.Failed($"'{trimmedNew}' is already a configured display layout.");
            }

            layouts.Remove(FindKey(layouts, oldName)!);
            layouts.Add(trimmedNew, existing.DeepClone());

            RenameModeReferences(document, DisplayProvider.ResourceKey, oldName, trimmedNew);
            return null;
        });
    }

    /// <summary>
    /// Deletes a layout, refused while any mode's 'display' resource still
    /// names it.
    /// </summary>
    public static ModeSaveOutcome Delete(string? root, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            if (HardwareConfigurationStore.FindProperty(document, "providers") is not JsonObject providers ||
                HardwareConfigurationStore.FindProperty(providers, "display") is not JsonObject display ||
                HardwareConfigurationStore.FindProperty(display, "layouts") is not JsonObject layouts)
            {
                return ModeSaveOutcome.Failed($"'{name}' is not a configured display layout.");
            }

            string? key = FindKey(layouts, name);

            if (key is null)
            {
                return ModeSaveOutcome.Failed($"'{name}' is not a configured display layout.");
            }

            IReadOnlyList<string> referencing = ModesReferencing(document, DisplayProvider.ResourceKey, name);

            if (referencing.Count > 0)
            {
                return ModeSaveOutcome.Failed(
                    $"'{name}' is used by {string.Join(", ", referencing)}. Change those first.");
            }

            layouts.Remove(key);
            return null;
        });
    }

    private static string? FindKey(JsonObject obj, string name) =>
        obj.Select(pair => pair.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every level of the hierarchy whose <paramref name="resourceKey"/> names
    /// <paramref name="value"/>, case-insensitively.
    /// </summary>
    internal static IReadOnlyList<string> ModesReferencing(JsonObject document, string resourceKey, string value)
    {
        var referencing = new List<string>();

        foreach ((string label, JsonObject resources) in ResourceHolders.In(document))
        {
            JsonNode? resource = HardwareConfigurationStore.FindProperty(resources, resourceKey);
            string? referencedValue = resource is JsonValue scalar && scalar.TryGetValue(out string? text)
                ? text
                : resource is JsonObject wrapper && HardwareConfigurationStore.FindProperty(wrapper, "value") is JsonValue wrapped &&
                  wrapped.TryGetValue(out string? wrappedText)
                    ? wrappedText
                    : null;

            if (referencedValue is not null && string.Equals(referencedValue, value, StringComparison.OrdinalIgnoreCase))
            {
                referencing.Add($"'{label}'");
            }
        }

        return referencing;
    }

    /// <summary>
    /// Renames every level's <paramref name="resourceKey"/> that names
    /// <paramref name="oldName"/>.
    /// </summary>
    internal static void RenameModeReferences(JsonObject document, string resourceKey, string oldName, string newName)
    {
        foreach ((_, JsonObject resources) in ResourceHolders.In(document))
        {
            string? actualKey = FindKey(resources, resourceKey);

            if (actualKey is null)
            {
                continue;
            }

            JsonNode? resource = resources[actualKey];

            if (resource is JsonValue scalar && scalar.TryGetValue(out string? text) &&
                string.Equals(text, oldName, StringComparison.OrdinalIgnoreCase))
            {
                resources[actualKey] = newName;
            }
            else if (resource is JsonObject wrapper &&
                     HardwareConfigurationStore.FindProperty(wrapper, "value") is JsonValue wrapped &&
                     wrapped.TryGetValue(out string? wrappedText) &&
                     string.Equals(wrappedText, oldName, StringComparison.OrdinalIgnoreCase))
            {
                wrapper["value"] = newName;
            }
        }
    }

    /// <summary>
    /// Every level that defines <paramref name="resourceKey"/> at all, whatever
    /// it was set to.
    /// </summary>
    /// <remarks>
    /// Different from <see cref="ModesReferencing"/>, which asks whether a
    /// resource holds one particular value. An application's resource key is its
    /// own name, so what matters when it might be removed is only whether any
    /// level has an opinion about it -- Running, Stopped and an explicit clear
    /// are all a reason to keep it.
    /// </remarks>
    internal static IReadOnlyList<string> ModesReferencingKey(JsonObject document, string resourceKey)
    {
        var referencing = new List<string>();

        foreach ((string label, JsonObject resources) in ResourceHolders.In(document))
        {
            if (HardwareConfigurationStore.FindProperty(resources, resourceKey) is not null)
            {
                referencing.Add($"'{label}'");
            }
        }

        return referencing;
    }

    /// <summary>
    /// Renames a resource KEY itself -- not its value -- across every level that
    /// defines it, keeping whatever that level set it to.
    /// </summary>
    /// <remarks>
    /// An application's resource key is its own name, so renaming the
    /// application means every level that has an opinion about it now has to
    /// hold that opinion under the new key. <see cref="RenameModeReferences"/>
    /// is the opposite shape: a fixed key whose value happens to name something
    /// else, like a display layout or an audio device.
    /// </remarks>
    internal static void RenameResourceKey(JsonObject document, string oldKey, string newKey)
    {
        foreach ((_, JsonObject resources) in ResourceHolders.In(document))
        {
            string? actualKey = FindKey(resources, oldKey);

            if (actualKey is null)
            {
                continue;
            }

            JsonNode? existing = resources[actualKey];
            resources.Remove(actualKey);
            resources.Add(newKey, existing?.DeepClone());
        }
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCMode.Engine;

/// <summary>
/// Creates, renames and deletes DDC/CI monitor bindings in the same
/// configuration file <see cref="DdcBrightnessProvider"/> and
/// <see cref="DdcInputProvider"/> read, by editing 'providers.ddcMonitors'.
/// </summary>
/// <remarks>
/// KEY-BASED, NOT VALUE-BASED
///
/// A cursor position's name is a value a mode's 'cursor' resource names, so
/// <see cref="CursorPositionConfigurationStore"/> can rewrite mode references
/// on rename. A DDC/CI alias is part of the resource key itself
/// (<c>ddc.Left.brightness</c>) -- see <see cref="DdcResourceKeys"/> -- the
/// same shape a television's name is, and this follows
/// <see cref="LgTvConfigurationStore"/>'s precedent for that shape: renaming or
/// deleting an alias does not go looking for modes that still reference the
/// old keys. It does not need to --
/// <see cref="ConfigurationDocumentEditor.Edit"/> revalidates the whole
/// candidate document, rebuilding the DDC/CI provider set from the new alias
/// list, before a single byte reaches disk, so a mode left pointing at a
/// renamed-away alias is refused there, in the engine's own words, rather than
/// silently orphaned.
/// </remarks>
public static class DdcMonitorConfigurationStore
{
    /// <summary>Every configured alias and the device path it is bound to.</summary>
    public static IReadOnlyDictionary<string, string> Read(string? root = null)
    {
        (string json, _) = ModeConfiguration.ReadOrCreate(root ?? PCModePaths.DefaultRoot);

        JsonNode? parsed = JsonNode.Parse(json, documentOptions: HardwareConfigurationStore.ReadOptions);

        if (parsed is not JsonObject document)
        {
            throw new ModeConfigurationException("The configuration is not a JSON object.");
        }

        JsonElement? section = null;

        if (
            HardwareConfigurationStore.FindProperty(document, "providers") is JsonObject providers
            && HardwareConfigurationStore.FindProperty(providers, DdcMonitorCatalog.SectionKey) is JsonNode monitorsNode
        )
        {
            using JsonDocument monitorsDocument = JsonDocument.Parse(monitorsNode.ToJsonString());
            section = monitorsDocument.RootElement.Clone();
        }

        if (!DdcMonitorCatalog.TryRead(section, out IReadOnlyDictionary<string, string> monitors, out string? problem))
        {
            throw new ModeConfigurationException(problem!);
        }

        return monitors;
    }

    /// <summary>Creates a new alias, or rebinds an existing one to a different device path.</summary>
    public static ModeSaveOutcome Save(string? root, string alias, string devicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);

        string trimmedAlias = alias.Trim();
        string trimmedPath = devicePath.Trim();

        return ConfigurationDocumentEditor.Edit(
            root,
            document =>
            {
                JsonObject providers = HardwareConfigurationStore.AsObject(document, "providers");
                JsonObject monitors = HardwareConfigurationStore.AsObject(providers, DdcMonitorCatalog.SectionKey);

                HardwareConfigurationStore.RemoveCaseInsensitive(monitors, trimmedAlias);
                monitors.Add(trimmedAlias, trimmedPath);
                return null;
            }
        );
    }

    /// <summary>Renames a configured alias, keeping its device path.</summary>
    public static ModeSaveOutcome Rename(string? root, string oldAlias, string newAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(newAlias);

        string trimmedNew = newAlias.Trim();

        if (string.Equals(oldAlias.Trim(), trimmedNew, StringComparison.OrdinalIgnoreCase))
        {
            return ModeSaveOutcome.Ok;
        }

        return ConfigurationDocumentEditor.Edit(
            root,
            document =>
            {
                if (
                    HardwareConfigurationStore.FindProperty(document, "providers") is not JsonObject providers
                    || HardwareConfigurationStore.FindProperty(providers, DdcMonitorCatalog.SectionKey)
                        is not JsonObject monitors
                )
                {
                    return ModeSaveOutcome.Failed($"'{oldAlias}' is not a configured DDC/CI monitor.");
                }

                JsonNode? existing = HardwareConfigurationStore.FindProperty(monitors, oldAlias);

                if (existing is null)
                {
                    return ModeSaveOutcome.Failed($"'{oldAlias}' is not a configured DDC/CI monitor.");
                }

                if (HardwareConfigurationStore.FindProperty(monitors, trimmedNew) is not null)
                {
                    return ModeSaveOutcome.Failed($"'{trimmedNew}' is already a configured DDC/CI monitor.");
                }

                monitors.Remove(FindKey(monitors, oldAlias)!);
                monitors.Add(trimmedNew, existing.DeepClone());
                return null;
            }
        );
    }

    /// <summary>Removes a configured alias.</summary>
    public static ModeSaveOutcome Delete(string? root, string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        return ConfigurationDocumentEditor.Edit(
            root,
            document =>
            {
                if (
                    HardwareConfigurationStore.FindProperty(document, "providers") is not JsonObject providers
                    || HardwareConfigurationStore.FindProperty(providers, DdcMonitorCatalog.SectionKey)
                        is not JsonObject monitors
                )
                {
                    return ModeSaveOutcome.Failed($"'{alias}' is not a configured DDC/CI monitor.");
                }

                string? key = FindKey(monitors, alias);

                if (key is null)
                {
                    return ModeSaveOutcome.Failed($"'{alias}' is not a configured DDC/CI monitor.");
                }

                monitors.Remove(key);

                // Removing the last alias has to remove the section with it. An
                // empty 'providers.ddcMonitors' builds no DDC/CI providers, so
                // nothing reads it, and ModeConfiguration refuses a settings
                // section nothing reads -- which would make the last monitor the
                // one that could never be removed.
                if (monitors.Count == 0)
                {
                    HardwareConfigurationStore.RemoveCaseInsensitive(providers, DdcMonitorCatalog.SectionKey);
                }

                return null;
            }
        );
    }

    private static string? FindKey(JsonObject obj, string name) =>
        obj.Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
}

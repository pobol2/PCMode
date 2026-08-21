using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCMode.Engine;

/// <summary>One logical input, as a settings screen has it: a name a mode can use, and the HDMI socket it means.</summary>
public sealed record LgTvInputEdit(string Name, string Input);

/// <summary>One setting inside a profile's signal or picture half.</summary>
public sealed record LgTvSettingEdit(string Key, string Value);

/// <summary>
/// One named picture profile, as a settings screen has it.
/// </summary>
/// <param name="Input">
/// Which of the television's named inputs this profile's HDMI-specific options
/// are for, or null when the television has at most one and it needs no saying.
/// </param>
public sealed record LgTvProfileEdit(
    string Name,
    string PictureMode,
    string? Input,
    IReadOnlyList<LgTvSettingEdit> Signal,
    IReadOnlyList<LgTvSettingEdit> Settings);

/// <summary>One television body, as a settings screen has it.</summary>
public sealed record LgTvEdit(
    bool Enabled,
    string Host,
    string MacAddress,
    string? SafeDisplayTarget,
    IReadOnlyList<LgTvInputEdit> Inputs,
    IReadOnlyList<LgTvProfileEdit> Profiles);

/// <summary>
/// Creates and edits the named televisions stored under <c>providers.lgTv</c>.
/// </summary>
/// <remarks>
/// STEP 1: NAMED CONFIGURATION, SINGLE ACTIVE PROVIDER SET
///
/// The configuration section is now a named collection, but the engine still has
/// one set of television resource keys. <see cref="LgTvSettings.TryParse"/> therefore
/// projects the collection's default television into the existing providers. A
/// later step can create instance-specific providers without changing this file's
/// on-disk model again.
///
/// LEGACY MIGRATION
///
/// The old single-TV body is still accepted. The first successful edit converts
/// it in place to the named form with a television called
/// <see cref="LgTvCatalog.DefaultTelevisionName"/>. Nothing is rewritten merely
/// because the configuration was read.
///
/// NO SECOND SOURCE OF TRUTH
///
/// Every mutation still goes through <see cref="ConfigurationDocumentEditor"/>,
/// so the candidate document is validated by the production provider pipeline
/// before it reaches disk.
///
/// PROFILE INPUT MIGRATION
///
/// A document written before a profile could name its own input instead named
/// one television-wide, under <c>profileInput</c>. That key is still read -- see
/// <see cref="LgTvSettings.TryParse"/> -- but this store never writes it: every
/// save rebuilds each profile's body from <see cref="LgTvProfileEdit.Input"/>
/// alone, so the first edit through this screen quietly moves a document off the
/// old key without anybody having to ask for that.
/// </remarks>
public static class LgTvConfigurationStore
{
    /// <summary>Reads all configured televisions, accepting both legacy and named shapes.</summary>
    public static LgTvCatalog Read(string? root = null)
    {
        (string json, _) = ModeConfiguration.ReadOrCreate(root ?? PCModePaths.DefaultRoot);

        JsonNode? parsed = JsonNode.Parse(json, documentOptions: HardwareConfigurationStore.ReadOptions);

        if (parsed is not JsonObject document)
        {
            throw new ModeConfigurationException("The configuration is not a JSON object.");
        }

        JsonElement? section = null;

        if (HardwareConfigurationStore.FindProperty(document, "providers") is JsonObject providers &&
            HardwareConfigurationStore.FindProperty(providers, LgTvSettings.SectionKey) is JsonNode televisionNode)
        {
            using JsonDocument televisionDocument = JsonDocument.Parse(televisionNode.ToJsonString());
            section = televisionDocument.RootElement.Clone();
        }

        if (!LgTvCatalog.TryParse(section, out LgTvCatalog? catalog, out string? problem))
        {
            throw new ModeConfigurationException(problem!);
        }

        return catalog!;
    }

    /// <summary>
    /// Saves the television currently projected into the legacy provider set.
    /// This is the compatibility entry point used by today's single-TV GUI.
    /// </summary>
    public static ModeSaveOutcome Save(string? root, LgTvEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        if (!TryBuildBody(edit, out JsonObject? body, out string? problem))
        {
            return ModeSaveOutcome.Failed(problem!);
        }

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            JsonObject providers = HardwareConfigurationStore.AsObject(document, "providers");

            if (!TryEnsureNamedSection(
                    providers,
                    out JsonObject? section,
                    out JsonObject? televisions,
                    out ModeSaveOutcome? refused))
            {
                return refused;
            }

            string name = ResolveDefaultName(section!, televisions!);

            HardwareConfigurationStore.RemoveCaseInsensitive(televisions!, name);
            televisions!.Add(name, body!.DeepClone());

            WriteDefault(section!, name);
            return null;
        });
    }

    /// <summary>
    /// Creates one named television without replacing an existing entry.
    /// </summary>
    public static ModeSaveOutcome Create(
        string? root,
        string name,
        LgTvEdit edit,
        bool makeDefault = false)
    {
        ArgumentNullException.ThrowIfNull(edit);

        if (!TryNormaliseName(name, out string? normalized, out string? nameProblem))
        {
            return ModeSaveOutcome.Failed(nameProblem!);
        }

        if (!TryBuildBody(edit, out JsonObject? body, out string? problem))
        {
            return ModeSaveOutcome.Failed(problem!);
        }

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            JsonObject providers = HardwareConfigurationStore.AsObject(document, "providers");

            if (!TryEnsureNamedSection(
                    providers,
                    out JsonObject? section,
                    out JsonObject? televisions,
                    out ModeSaveOutcome? refused))
            {
                return refused;
            }

            if (FindNamed(televisions!, normalized!) is not null)
            {
                return ModeSaveOutcome.Failed($"A television named '{normalized}' already exists.");
            }

            televisions!.Add(normalized!, body!.DeepClone());

            string currentDefault = ResolveDefaultName(section!, televisions);

            if (makeDefault ||
                televisions.Count == 1 ||
                !televisions.Any(pair =>
                    string.Equals(pair.Key, currentDefault, StringComparison.OrdinalIgnoreCase)))
            {
                WriteDefault(section!, normalized!);
            }
            else if (HardwareConfigurationStore.FindProperty(section!, LgTvCatalog.DefaultProperty) is null)
            {
                WriteDefault(section!, currentDefault);
            }

            return null;
        });
    }

    /// <summary>
    /// Creates or replaces one named television. The current default remains
    /// unchanged unless <paramref name="makeDefault"/> is true or no valid default
    /// exists yet.
    /// </summary>
    public static ModeSaveOutcome Save(
        string? root,
        string name,
        LgTvEdit edit,
        bool makeDefault = false)
    {
        ArgumentNullException.ThrowIfNull(edit);

        if (!TryNormaliseName(name, out string? normalized, out string? nameProblem))
        {
            return ModeSaveOutcome.Failed(nameProblem!);
        }

        if (!TryBuildBody(edit, out JsonObject? body, out string? problem))
        {
            return ModeSaveOutcome.Failed(problem!);
        }

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            JsonObject providers = HardwareConfigurationStore.AsObject(document, "providers");

            if (!TryEnsureNamedSection(
                    providers,
                    out JsonObject? section,
                    out JsonObject? televisions,
                    out ModeSaveOutcome? refused))
            {
                return refused;
            }

            HardwareConfigurationStore.RemoveCaseInsensitive(televisions!, normalized!);
            televisions!.Add(normalized!, body!.DeepClone());

            string currentDefault = ResolveDefaultName(section!, televisions!);

            if (makeDefault ||
                !televisions!.Any(pair => string.Equals(pair.Key, currentDefault, StringComparison.OrdinalIgnoreCase)))
            {
                WriteDefault(section!, normalized!);
            }
            else if (HardwareConfigurationStore.FindProperty(section!, LgTvCatalog.DefaultProperty) is null)
            {
                WriteDefault(section!, currentDefault);
            }

            return null;
        });
    }

    /// <summary>Duplicates one named television without changing the default.</summary>
    public static ModeSaveOutcome Duplicate(string? root, string sourceName, string newName)
    {
        if (!TryNormaliseName(sourceName, out string? source, out string? sourceProblem))
        {
            return ModeSaveOutcome.Failed(sourceProblem!);
        }

        if (!TryNormaliseName(newName, out string? target, out string? targetProblem))
        {
            return ModeSaveOutcome.Failed(targetProblem!);
        }

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            JsonObject providers = HardwareConfigurationStore.AsObject(document, "providers");

            if (!TryEnsureNamedSection(
                    providers,
                    out JsonObject? section,
                    out JsonObject? televisions,
                    out ModeSaveOutcome? refused))
            {
                return refused;
            }

            if (FindNamed(televisions!, target!) is not null)
            {
                return ModeSaveOutcome.Failed($"A television named '{target}' already exists.");
            }

            KeyValuePair<string, JsonNode?>? found = FindNamed(televisions!, source!);

            if (found is null || found.Value.Value is not JsonObject sourceBody)
            {
                return ModeSaveOutcome.Failed($"Television '{source}' does not exist.");
            }

            televisions!.Add(target!, sourceBody.DeepClone());
            WriteDefault(section!, ResolveDefaultName(section!, televisions!));
            return null;
        });
    }

    /// <summary>Renames one television and preserves default selection.</summary>
    public static ModeSaveOutcome Rename(string? root, string oldName, string newName)
    {
        if (!TryNormaliseName(oldName, out string? source, out string? sourceProblem))
        {
            return ModeSaveOutcome.Failed(sourceProblem!);
        }

        if (!TryNormaliseName(newName, out string? target, out string? targetProblem))
        {
            return ModeSaveOutcome.Failed(targetProblem!);
        }

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return ModeSaveOutcome.Ok;
        }

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            JsonObject providers = HardwareConfigurationStore.AsObject(document, "providers");

            if (!TryEnsureNamedSection(
                    providers,
                    out JsonObject? section,
                    out JsonObject? televisions,
                    out ModeSaveOutcome? refused))
            {
                return refused;
            }

            if (FindNamed(televisions!, target!) is not null)
            {
                return ModeSaveOutcome.Failed($"A television named '{target}' already exists.");
            }

            KeyValuePair<string, JsonNode?>? found = FindNamed(televisions!, source!);

            if (found is null || found.Value.Value is not JsonObject body)
            {
                return ModeSaveOutcome.Failed($"Television '{source}' does not exist.");
            }

            string currentDefault = ResolveDefaultName(section!, televisions!);
            bool renamedDefault = string.Equals(currentDefault, found.Value.Key, StringComparison.OrdinalIgnoreCase);

            televisions!.Remove(found.Value.Key);
            televisions.Add(target!, body.DeepClone());

            WriteDefault(section!, renamedDefault ? target! : ResolveDefaultName(section!, televisions));
            return null;
        });
    }

    /// <summary>Removes one named television and selects another default if necessary.</summary>
    public static ModeSaveOutcome Delete(string? root, string name)
    {
        if (!TryNormaliseName(name, out string? normalized, out string? problem))
        {
            return ModeSaveOutcome.Failed(problem!);
        }

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            JsonObject providers = HardwareConfigurationStore.AsObject(document, "providers");

            if (!TryEnsureNamedSection(
                    providers,
                    out JsonObject? section,
                    out JsonObject? televisions,
                    out ModeSaveOutcome? refused))
            {
                return refused;
            }

            KeyValuePair<string, JsonNode?>? found = FindNamed(televisions!, normalized!);

            if (found is null)
            {
                return ModeSaveOutcome.Failed($"Television '{normalized}' does not exist.");
            }

            string currentDefault = ResolveDefaultName(section!, televisions!);
            televisions!.Remove(found.Value.Key);

            if (televisions.Count == 0)
            {
                HardwareConfigurationStore.RemoveCaseInsensitive(section!, LgTvCatalog.DefaultProperty);
            }
            else if (string.Equals(currentDefault, found.Value.Key, StringComparison.OrdinalIgnoreCase))
            {
                WriteDefault(
                    section!,
                    televisions.Select(pair => pair.Key).Order(StringComparer.OrdinalIgnoreCase).First());
            }
            else
            {
                WriteDefault(section!, ResolveDefaultName(section!, televisions));
            }

            return null;
        });
    }

    /// <summary>Selects which named television is projected through the legacy/default provider keys.</summary>
    public static ModeSaveOutcome SetDefault(string? root, string name)
    {
        if (!TryNormaliseName(name, out string? normalized, out string? problem))
        {
            return ModeSaveOutcome.Failed(problem!);
        }

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            JsonObject providers = HardwareConfigurationStore.AsObject(document, "providers");

            if (!TryEnsureNamedSection(
                    providers,
                    out JsonObject? section,
                    out JsonObject? televisions,
                    out ModeSaveOutcome? refused))
            {
                return refused;
            }

            KeyValuePair<string, JsonNode?>? found = FindNamed(televisions!, normalized!);

            if (found is null)
            {
                return ModeSaveOutcome.Failed($"Television '{normalized}' does not exist.");
            }

            WriteDefault(section!, found.Value.Key);
            return null;
        });
    }

    private static bool TryBuildBody(LgTvEdit edit, out JsonObject? body, out string? problem)
    {
        body = null;

        string host = edit.Host?.Trim() ?? string.Empty;
        string mac = edit.MacAddress?.Trim() ?? string.Empty;

        if (edit.Enabled && host.Length == 0)
        {
            problem = "Give the television's address on your network, or turn it off.";
            return false;
        }

        if (edit.Enabled && mac.Length == 0)
        {
            problem =
                "Give the television's MAC address -- switching it on needs it -- or turn the television off.";
            return false;
        }

        var inputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (LgTvInputEdit input in edit.Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Name))
            {
                problem = "An input needs a name a mode can use.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(input.Input))
            {
                problem = $"'{input.Name}' needs the television's socket, such as HDMI_4.";
                return false;
            }

            if (!inputNames.Add(input.Name.Trim()))
            {
                problem = $"'{input.Name}' is used for more than one input.";
                return false;
            }

            if (!inputIds.Add(input.Input.Trim()))
            {
                problem = $"More than one input points at '{input.Input.Trim()}'.";
                return false;
            }
        }

        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (LgTvProfileEdit profile in edit.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                problem = "A profile needs a name a mode can use.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(profile.PictureMode))
            {
                problem = $"'{profile.Name}' needs a picture mode, such as 'filmMaker'.";
                return false;
            }

            if (!profileNames.Add(profile.Name.Trim()))
            {
                problem = $"'{profile.Name}' is used for more than one profile.";
                return false;
            }
        }

        body = new JsonObject { ["enabled"] = edit.Enabled };

        if (host.Length > 0)
        {
            body["host"] = host;
        }

        if (mac.Length > 0)
        {
            body["macAddress"] = mac;
        }

        if (!string.IsNullOrWhiteSpace(edit.SafeDisplayTarget))
        {
            body["safeDisplayTarget"] = edit.SafeDisplayTarget.Trim();
        }

        if (edit.Inputs.Count > 0)
        {
            var inputs = new JsonObject();

            foreach (LgTvInputEdit input in edit.Inputs)
            {
                inputs[input.Name.Trim()] = input.Input.Trim();
            }

            body["inputs"] = inputs;
        }

        if (edit.Profiles.Count > 0)
        {
            var profiles = new JsonObject();

            foreach (LgTvProfileEdit profile in edit.Profiles)
            {
                var written = new JsonObject { ["pictureMode"] = profile.PictureMode.Trim() };

                if (!string.IsNullOrWhiteSpace(profile.Input))
                {
                    written["input"] = profile.Input.Trim();
                }

                if (profile.Signal.Count > 0)
                {
                    var signal = new JsonObject();

                    foreach (LgTvSettingEdit setting in profile.Signal)
                    {
                        signal[setting.Key.Trim()] = setting.Value.Trim();
                    }

                    written["signal"] = signal;
                }

                if (profile.Settings.Count > 0)
                {
                    var settings = new JsonObject();

                    foreach (LgTvSettingEdit setting in profile.Settings)
                    {
                        settings[setting.Key.Trim()] = setting.Value.Trim();
                    }

                    written["settings"] = settings;
                }

                profiles[profile.Name.Trim()] = written;
            }

            body["profiles"] = profiles;
        }

        problem = null;
        return true;
    }

    private static bool TryEnsureNamedSection(
        JsonObject providers,
        out JsonObject? section,
        out JsonObject? televisions,
        out ModeSaveOutcome? refused)
    {
        section = null;
        televisions = null;
        refused = null;

        JsonNode? existing = HardwareConfigurationStore.FindProperty(providers, LgTvSettings.SectionKey);

        if (existing is null)
        {
            section = new JsonObject();
            televisions = new JsonObject();
            section[LgTvCatalog.TelevisionsProperty] = televisions;

            HardwareConfigurationStore.RemoveCaseInsensitive(providers, LgTvSettings.SectionKey);
            providers[LgTvSettings.SectionKey] = section;
            return true;
        }

        if (existing is not JsonObject existingObject)
        {
            refused = ModeSaveOutcome.Failed($"'{LgTvSettings.SettingsPath}' is not an object.");
            return false;
        }

        bool alreadyNamed =
            HardwareConfigurationStore.FindProperty(existingObject, LgTvCatalog.TelevisionsProperty) is not null ||
            HardwareConfigurationStore.FindProperty(existingObject, LgTvCatalog.DefaultProperty) is not null;

        if (!alreadyNamed)
        {
            var migratedTelevisions = new JsonObject
            {
                [LgTvCatalog.DefaultTelevisionName] = existingObject.DeepClone(),
            };

            var migrated = new JsonObject
            {
                [LgTvCatalog.DefaultProperty] = LgTvCatalog.DefaultTelevisionName,
                [LgTvCatalog.TelevisionsProperty] = migratedTelevisions,
            };

            HardwareConfigurationStore.RemoveCaseInsensitive(providers, LgTvSettings.SectionKey);
            providers[LgTvSettings.SectionKey] = migrated;

            section = migrated;
            televisions = migratedTelevisions;
            return true;
        }

        JsonNode? declaredTelevisions =
            HardwareConfigurationStore.FindProperty(existingObject, LgTvCatalog.TelevisionsProperty);

        if (declaredTelevisions is null)
        {
            televisions = new JsonObject();
            existingObject[LgTvCatalog.TelevisionsProperty] = televisions;
        }
        else if (declaredTelevisions is JsonObject map)
        {
            televisions = map;
        }
        else
        {
            refused = ModeSaveOutcome.Failed(
                $"'{LgTvSettings.SettingsPath}.{LgTvCatalog.TelevisionsProperty}' is not an object.");
            return false;
        }

        section = existingObject;
        return true;
    }

    private static string ResolveDefaultName(JsonObject section, JsonObject televisions)
    {
        string? requested = null;

        if (HardwareConfigurationStore.FindProperty(section, LgTvCatalog.DefaultProperty) is JsonValue defaultValue &&
            defaultValue.TryGetValue(out string? written) &&
            !string.IsNullOrWhiteSpace(written))
        {
            requested = written.Trim();
        }

        if (requested is not null)
        {
            string? exact = televisions.Select(pair => pair.Key)
                .FirstOrDefault(name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));

            if (exact is not null)
            {
                return exact;
            }
        }

        return televisions.Select(pair => pair.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? LgTvCatalog.DefaultTelevisionName;
    }

    private static void WriteDefault(JsonObject section, string name)
    {
        HardwareConfigurationStore.RemoveCaseInsensitive(section, LgTvCatalog.DefaultProperty);
        section[LgTvCatalog.DefaultProperty] = name;
    }

    private static KeyValuePair<string, JsonNode?>? FindNamed(JsonObject televisions, string name)
    {
        foreach (KeyValuePair<string, JsonNode?> pair in televisions)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair;
            }
        }

        return null;
    }

    private static bool TryNormaliseName(string name, out string? normalized, out string? problem)
    {
        normalized = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            problem = "Give the television a name.";
            return false;
        }

        normalized = name.Trim();

        if (normalized.Contains('/') || normalized.Contains('\\'))
        {
            problem = $"'{normalized}' cannot be used as a television name. Do not use '/' or '\\'.";
            return false;
        }

        problem = null;
        return true;
    }
}

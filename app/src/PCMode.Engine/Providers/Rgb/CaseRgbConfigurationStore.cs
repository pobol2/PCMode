using System.Globalization;
using System.Text.Json.Nodes;

namespace PCMode.Engine;

/// <summary>One case-lighting controller row.</summary>
public sealed record CaseRgbDeviceEdit(int Index, string Mode)
{
    /// <summary>Stable OpenRGB controller identity. Null keeps legacy index-only configuration.</summary>
    public string? Identity { get; init; }
}

/// <summary>One controller state inside a named RGB scene.</summary>
public sealed record CaseRgbSceneDeviceEdit(int Index, string Mode, string Color)
{
    /// <summary>Optional exact per-LED colours in the controller's SDK LED order.</summary>
    public IReadOnlyList<string>? LedColors { get; init; }
}

/// <summary>One named RGB scene as edited by the configuration UI.</summary>
public sealed record CaseRgbSceneEdit(string Name, IReadOnlyList<CaseRgbSceneDeviceEdit> Devices);

/// <summary>The whole of 'providers.caseRgb', as a settings screen has it.</summary>
public sealed record CaseRgbEdit(
    bool Enabled,
    string Executable,
    IReadOnlyList<CaseRgbDeviceEdit> Devices,
    int WithinMs
)
{
    /// <summary>
    /// Null preserves an existing scenes section for compatibility with older callers.
    /// A non-null collection replaces the configured scenes.
    /// </summary>
    public IReadOnlyList<CaseRgbSceneEdit>? Scenes { get; init; }

    /// <summary>Scene names changed by the editor, used to update caseRgb references in spaces.</summary>
    public IReadOnlyDictionary<string, string>? SceneRenames { get; init; }
}

/// <summary>Creates and edits the case lighting section in the configuration document.</summary>
public static class CaseRgbConfigurationStore
{
    public static ModeSaveOutcome Save(string? root, CaseRgbEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        string executable = edit.Executable?.Trim() ?? string.Empty;

        if (edit.Enabled && edit.Devices.Count == 0)
        {
            return ModeSaveOutcome.Failed("Add at least one device, or turn case lighting off.");
        }

        var seenDevices = new HashSet<int>();

        foreach (CaseRgbDeviceEdit device in edit.Devices)
        {
            if (device.Index < 0)
            {
                return ModeSaveOutcome.Failed(
                    "A device number cannot be negative. OpenRGB numbers its devices from 0."
                );
            }

            if (string.IsNullOrWhiteSpace(device.Mode))
            {
                return ModeSaveOutcome.Failed(
                    $"Device {device.Index} needs a lighting mode, such as 'Direct' or 'Static'."
                );
            }

            if (device.Identity is not null && string.IsNullOrWhiteSpace(device.Identity))
            {
                return ModeSaveOutcome.Failed($"Device {device.Index} has an empty OpenRGB identity.");
            }

            if (!seenDevices.Add(device.Index))
            {
                return ModeSaveOutcome.Failed($"Device {device.Index} is listed more than once.");
            }
        }

        if (edit.WithinMs < 500 || edit.WithinMs > 60_000)
        {
            return ModeSaveOutcome.Failed("The OpenRGB SDK timeout must be between 500 and 60000 milliseconds.");
        }

        if (edit.Scenes is not null && ValidateSceneEdits(edit.Scenes, seenDevices, out string? sceneProblem) is false)
        {
            return ModeSaveOutcome.Failed(sceneProblem!);
        }

        return ConfigurationDocumentEditor.Edit(
            root,
            document =>
            {
                JsonObject providers = HardwareConfigurationStore.AsObject(document, "providers");
                IReadOnlyDictionary<int, string> existingIdentities = ReadExistingIdentities(providers);
                JsonNode? existingScenes = ReadExistingScenes(providers);

                if (edit.SceneRenames is { Count: > 0 })
                {
                    foreach ((string oldName, string newName) in edit.SceneRenames)
                    {
                        LayoutConfigurationStore.RenameModeReferences(
                            document,
                            CaseRgbProvider.ResourceKey,
                            oldName,
                            newName
                        );
                    }
                }

                if (edit.Scenes is not null && existingScenes is JsonObject existingSceneObject)
                {
                    var kept = new HashSet<string>(
                        edit.Scenes.Select(scene => scene.Name.Trim()),
                        StringComparer.OrdinalIgnoreCase
                    );

                    foreach (string oldName in existingSceneObject.Select(pair => pair.Key))
                    {
                        if (kept.Contains(oldName) || edit.SceneRenames?.ContainsKey(oldName) == true)
                        {
                            continue;
                        }

                        IReadOnlyList<string> referencing = LayoutConfigurationStore.ModesReferencing(
                            document,
                            CaseRgbProvider.ResourceKey,
                            oldName
                        );

                        if (referencing.Count > 0)
                        {
                            return ModeSaveOutcome.Failed(
                                $"Lighting scene '{oldName}' is used by {string.Join(", ", referencing)}. Change those first."
                            );
                        }
                    }
                }

                var body = new JsonObject { ["enabled"] = edit.Enabled, ["within"] = edit.WithinMs };

                if (executable.Length > 0)
                {
                    body["executable"] = executable;
                }

                if (edit.Scenes is null)
                {
                    if (existingScenes is not null)
                    {
                        body["scenes"] = existingScenes;
                    }
                }
                else if (edit.Scenes.Count > 0)
                {
                    body["scenes"] = WriteScenes(edit.Scenes);
                }

                if (edit.Devices.Count > 0)
                {
                    var devices = new JsonObject();

                    foreach (CaseRgbDeviceEdit device in edit.Devices)
                    {
                        string? identity = string.IsNullOrWhiteSpace(device.Identity)
                            ? existingIdentities.GetValueOrDefault(device.Index)
                            : device.Identity.Trim();

                        if (identity is null)
                        {
                            devices[device.Index.ToString(CultureInfo.InvariantCulture)] = device.Mode.Trim();
                        }
                        else
                        {
                            devices[device.Index.ToString(CultureInfo.InvariantCulture)] = new JsonObject
                            {
                                ["mode"] = device.Mode.Trim(),
                                ["identity"] = identity,
                            };
                        }
                    }

                    body["devices"] = devices;
                }

                HardwareConfigurationStore.RemoveCaseInsensitive(providers, "caseRgb");
                providers.Add("caseRgb", body);

                return null;
            }
        );
    }

    private static bool ValidateSceneEdits(
        IReadOnlyList<CaseRgbSceneEdit> scenes,
        IReadOnlySet<int> configuredDevices,
        out string? problem
    )
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CaseRgbSceneEdit scene in scenes)
        {
            string name = scene.Name?.Trim() ?? string.Empty;

            if (name.Length == 0)
            {
                problem = "Every RGB scene needs a name.";
                return false;
            }

            if (IsRgb(name))
            {
                problem = $"RGB scene name '{name}' looks like a direct RRGGBB target. Choose another name.";
                return false;
            }

            if (!names.Add(name))
            {
                problem = $"RGB scene '{name}' is listed more than once.";
                return false;
            }

            var sceneDevices = new HashSet<int>();

            foreach (CaseRgbSceneDeviceEdit device in scene.Devices)
            {
                if (!configuredDevices.Contains(device.Index))
                {
                    problem = $"RGB scene '{name}' contains unknown controller {device.Index}.";
                    return false;
                }

                if (!sceneDevices.Add(device.Index))
                {
                    problem = $"RGB scene '{name}' contains controller {device.Index} more than once.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(device.Mode))
                {
                    problem = $"RGB scene '{name}' needs a mode for controller {device.Index}.";
                    return false;
                }

                string color = device.Color?.Trim() ?? string.Empty;

                if (!IsRgb(color))
                {
                    problem = $"RGB scene '{name}' needs a six-digit RGB colour for controller {device.Index}.";
                    return false;
                }

                if (device.LedColors is { } ledColors)
                {
                    if (ledColors.Count == 0)
                    {
                        problem = $"RGB scene '{name}' has an empty LED colour list for controller {device.Index}.";
                        return false;
                    }

                    if (ledColors.Any(ledColor => !IsRgb(ledColor?.Trim() ?? string.Empty)))
                    {
                        problem = $"RGB scene '{name}' has an invalid per-LED colour for controller {device.Index}.";
                        return false;
                    }
                }
            }

            if (!sceneDevices.SetEquals(configuredDevices))
            {
                problem = $"RGB scene '{name}' must define every configured RGB controller.";
                return false;
            }
        }

        problem = null;
        return true;
    }

    private static JsonObject WriteScenes(IReadOnlyList<CaseRgbSceneEdit> scenes)
    {
        var written = new JsonObject();

        foreach (CaseRgbSceneEdit scene in scenes)
        {
            var devices = new JsonObject();

            foreach (CaseRgbSceneDeviceEdit device in scene.Devices.OrderBy(device => device.Index))
            {
                var state = new JsonObject
                {
                    ["mode"] = device.Mode.Trim(),
                    ["color"] = device.Color.Trim().ToUpperInvariant(),
                };

                if (device.LedColors is { Count: > 0 } ledColors)
                {
                    var writtenLeds = new JsonArray();

                    foreach (string color in ledColors)
                    {
                        writtenLeds.Add(color.Trim().ToUpperInvariant());
                    }

                    state["leds"] = writtenLeds;
                }

                devices[device.Index.ToString(CultureInfo.InvariantCulture)] = state;
            }

            written.Add(scene.Name.Trim(), devices);
        }

        return written;
    }

    private static JsonNode? ReadExistingScenes(JsonObject providers)
    {
        if (HardwareConfigurationStore.FindProperty(providers, "caseRgb") is not JsonObject caseRgb)
        {
            return null;
        }

        return HardwareConfigurationStore.FindProperty(caseRgb, "scenes")?.DeepClone();
    }

    private static IReadOnlyDictionary<int, string> ReadExistingIdentities(JsonObject providers)
    {
        var identities = new Dictionary<int, string>();

        if (
            HardwareConfigurationStore.FindProperty(providers, "caseRgb") is not JsonObject caseRgb
            || HardwareConfigurationStore.FindProperty(caseRgb, "devices") is not JsonObject devices
        )
        {
            return identities;
        }

        foreach ((string key, JsonNode? value) in devices)
        {
            if (
                !int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || value is not JsonObject detailed
                || HardwareConfigurationStore.FindProperty(detailed, "identity") is not JsonValue identityValue
                || !identityValue.TryGetValue(out string? identity)
                || string.IsNullOrWhiteSpace(identity)
            )
            {
                continue;
            }

            identities[index] = identity.Trim();
        }

        return identities;
    }

    private static bool IsRgb(string value) => value.Length == 6 && value.All(char.IsAsciiHexDigit);
}

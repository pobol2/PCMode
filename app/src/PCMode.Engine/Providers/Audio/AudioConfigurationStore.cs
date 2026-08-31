using System.Globalization;
using System.Text.Json.Nodes;
using PCMode.Audio.Core.Models;

namespace PCMode.Engine;

/// <summary>
/// One route of an Audio Target as a settings screen has it.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="AudioRouteSettings"/> itself. That type is the
/// parsed result of a document that has already been validated -- its output is
/// known to exist, and its delay and gain are known to be in range. This is the
/// unchecked form a person has just typed, which is exactly what
/// <see cref="AudioConfigurationStore.SaveTarget"/> is for.
/// </remarks>
/// <param name="Volume">
/// A Windows endpoint volume from 0 to 1, or null to leave that endpoint's own
/// slider wherever the user last put it.
/// </param>
public sealed record AudioRouteEdit(
    string Output,
    int DelayMs = 0,
    double Gain = 1.0,
    double? Volume = null,
    bool Required = true,
    string? Profile = null);

public sealed record AudioProcessingProfileEdit(
    string Name,
    float PreampGainDb,
    IReadOnlyList<EqBand> Bands);

/// <summary>One Audio Target as a settings screen has it: a name and its routes.</summary>
public sealed record AudioTargetEdit(string Name, IReadOnlyList<AudioRouteEdit> Routes);

/// <summary>
/// Creates, edits and removes Audio Targets and audio output aliases in the
/// same configuration file <see cref="AudioProvider"/> reads, by editing
/// 'providers.audio'.
/// </summary>
/// <remarks>
/// NO SECOND SOURCE OF TRUTH
///
/// This writes exactly the document <see cref="AudioProvider"/> parses and
/// <see cref="AudioTargetSettings.TryParse"/> checks, and validates the result
/// through the same production pipeline a real startup uses -- see
/// <see cref="ConfigurationDocumentEditor"/> -- before a single byte reaches
/// disk. The checks made here before that are the ones a person needs phrased in
/// their own terms ("choose two different outputs"); they never replace the
/// parser's, and a disagreement between the two is resolved by the parser
/// refusing the write.
///
/// REMOVAL FOLLOWS REFERENCES
///
/// A target's name is a value a mode's 'audio' resource takes, exactly as a
/// device alias is, so renaming a target rewrites every mode that named it, and
/// removing a target -- or an output a target is built from -- is refused, with
/// nothing changed, while anything still names it.
/// </remarks>
public static class AudioConfigurationStore
{
    /// <summary>
    /// Creates an Audio Target, or replaces one already configured.
    /// </summary>
    /// <param name="previousName">
    /// The name this target had before, when it is being renamed. Every mode
    /// naming the old one is rewritten: the name is what a mode's 'audio'
    /// resource holds, not a copy of it, so left alone those modes would name
    /// nothing.
    /// </param>
    public static ModeSaveOutcome SaveTarget(string? root, AudioTargetEdit target, string? previousName = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        string name = target.Name?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            return ModeSaveOutcome.Failed("Give the audio target a name. A mode asks for it by that name.");
        }

        if (target.Routes is null || target.Routes.Count == 0)
        {
            return ModeSaveOutcome.Failed("Choose at least one output for the audio target.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (AudioRouteEdit route in target.Routes)
        {
            string output = route.Output?.Trim() ?? string.Empty;

            if (output.Length == 0)
            {
                return ModeSaveOutcome.Failed("Choose every one of the audio target's outputs.");
            }

            if (!seen.Add(output))
            {
                // The same alias twice is the same endpoint twice, which the
                // engine refuses outright.
                return ModeSaveOutcome.Failed(
                    $"'{output}' cannot be two of the audio target's outputs. Choose different outputs.");
            }

            if (route.DelayMs < 0 || route.DelayMs > AudioTargetSettings.MaxDelayMs)
            {
                return ModeSaveOutcome.Failed(
                    $"The delay for '{output}' must be between 0 and {AudioTargetSettings.MaxDelayMs} ms.");
            }

            if (!IsUsable(route.Gain) || route.Gain < 0 || route.Gain > AudioTargetSettings.MaxGain)
            {
                return ModeSaveOutcome.Failed(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The gain for '{output}' must be between 0 and {AudioTargetSettings.MaxGain}."));
            }

            if (route.Volume is double level && (!IsUsable(level) || level < 0 || level > 1))
            {
                return ModeSaveOutcome.Failed($"The volume for '{output}' must be between 0 and 100 percent.");
            }
        }

        if (!target.Routes.Any(route => route.Required))
        {
            return ModeSaveOutcome.Failed(
                "At least one of the audio target's outputs has to be required, or nothing could fail it.");
        }

        string? renamedFrom = string.IsNullOrWhiteSpace(previousName) ? null : previousName.Trim();

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            JsonObject providers = HardwareConfigurationStore.AsObject(document, "providers");
            JsonObject audio = HardwareConfigurationStore.AsObject(providers, "audio");
            JsonObject targets = HardwareConfigurationStore.AsObject(audio, "targets");

            if (renamedFrom is not null && !string.Equals(renamedFrom, name, StringComparison.OrdinalIgnoreCase))
            {
                HardwareConfigurationStore.RemoveCaseInsensitive(targets, renamedFrom);
                LayoutConfigurationStore.RenameModeReferences(document, AudioProvider.ResourceKey, renamedFrom, name);
            }

            HardwareConfigurationStore.RemoveCaseInsensitive(targets, name);
            targets.Add(name, Write(target.Routes));

            return null;
        });
    }

    private static void UpdateProfileReferences(
        JsonObject document,
        string oldName,
        string? replacement)
    {
        if (
            document["audioTargets"]
            is not JsonObject targets)
        {
            return;
        }

        foreach (
            KeyValuePair<string, JsonNode?> target
            in targets)
        {
            if (
                target.Value
                is not JsonObject targetObject ||
                targetObject["routes"]
                is not JsonArray routes)
            {
                continue;
            }

            foreach (JsonNode? routeNode in routes)
            {
                if (
                    routeNode
                    is not JsonObject route)
                {
                    continue;
                }

                string? profile =
                    route["processingProfile"]
                        ?.GetValue<string>();

                if (
                    !string.Equals(
                        profile,
                        oldName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (replacement is null)
                {
                    route.Remove(
                        "processingProfile");
                }
                else
                {
                    route["processingProfile"] =
                        replacement;
                }
            }
        }
    }

    public static ModeSaveOutcome SaveProfile(
        string? root,
        AudioProcessingProfileEdit profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        string name = profile.Name?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            return ModeSaveOutcome.Failed("Choose a processing profile first.");
        }

        if (profile.PreampGainDb < ParametricEqProfile.MinPreampGainDb ||
            profile.PreampGainDb > ParametricEqProfile.MaxPreampGainDb)
        {
            return ModeSaveOutcome.Failed(
                $"Preamp must be between {ParametricEqProfile.MinPreampGainDb} and {ParametricEqProfile.MaxPreampGainDb} dB.");
        }

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            JsonObject providers =
                HardwareConfigurationStore.AsObject(document, "providers");

            JsonObject audio =
                HardwareConfigurationStore.AsObject(providers, "audio");

            JsonObject profiles =
                HardwareConfigurationStore.AsObject(audio, "profiles");

            HardwareConfigurationStore.RemoveCaseInsensitive(profiles, name);

            var bands = new JsonArray();

            foreach (EqBand band in profile.Bands)
            {
                bands.Add(new JsonObject
                {
                    ["type"] = band.Type.ToString(),
                    ["frequencyHz"] = band.FrequencyHz,
                    ["gainDb"] = band.GainDb,
                    ["q"] = band.Q,
                });
            }

            profiles.Add(
                name,
                new JsonObject
                {
                    ["preampGainDb"] = profile.PreampGainDb,
                    ["bands"] = bands,
                });

            return null;
        });
    }

    public static ModeSaveOutcome RenameProfile(
        string? root,
        string oldName,
        string newName)
    {
        string source =
            oldName?.Trim()
            ?? string.Empty;

        string destination =
            newName?.Trim()
            ?? string.Empty;

        if (
            source.Length == 0 ||
            destination.Length == 0)
        {
            return ModeSaveOutcome.Failed(
                "Both processing profile names are required.");
        }

        return ConfigurationDocumentEditor.Edit(
            root,
            document =>
            {
                JsonObject providers =
                    HardwareConfigurationStore.AsObject(
                        document,
                        "providers");

                JsonObject audio =
                    HardwareConfigurationStore.AsObject(
                        providers,
                        "audio");

                JsonObject profiles =
                    HardwareConfigurationStore.AsObject(
                        audio,
                        "profiles");

                KeyValuePair<string, JsonNode?> existing =
                    profiles.FirstOrDefault(
                        pair =>
                            string.Equals(
                                pair.Key,
                                source,
                                StringComparison.OrdinalIgnoreCase));

                if (existing.Key is null)
                {
                    return ModeSaveOutcome.Failed($"Processing profile '{source}' does not exist.");
                }

                if (
                    profiles.Any(
                        pair =>
                            !string.Equals(
                                pair.Key,
                                source,
                                StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(
                                pair.Key,
                                destination,
                                StringComparison.OrdinalIgnoreCase)))
                {
                    return ModeSaveOutcome.Failed("Processing profile '{destination}' already exists.");
                }

                JsonNode? value =
                    existing.Value?.DeepClone();

                profiles.Remove(
                    existing.Key);

                profiles[destination] =
                    value;

                UpdateProfileReferences(
                    document,
                    source,
                    destination);

                return null;
            }
        );
    }

    public static ModeSaveOutcome RemoveProfile(
        string? root,
        string name)
    {
        string trimmed =
            name?.Trim()
            ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return ModeSaveOutcome.Failed(
                "Choose a processing profile first.");
        }

        return ConfigurationDocumentEditor.Edit(
            root,
            document =>
            {
                JsonObject providers =
                    HardwareConfigurationStore.AsObject(
                        document,
                        "providers");

                JsonObject audio =
                    HardwareConfigurationStore.AsObject(
                        providers,
                        "audio");

                JsonObject profiles =
                    HardwareConfigurationStore.AsObject(
                        audio,
                        "profiles");

                string? actualName =
                    profiles
                        .Select(
                            pair => pair.Key)
                        .FirstOrDefault(
                            key =>
                                string.Equals(
                                    key,
                                    trimmed,
                                    StringComparison.OrdinalIgnoreCase));

                if (actualName is null)
                {
                    return ModeSaveOutcome.Failed("Processing profile '{trimmed}' does not exist.");
                }

                profiles.Remove(
                    actualName);

                UpdateProfileReferences(
                    document,
                    trimmed,
                    replacement: null);

                return null;
            });
    }

    /// <summary>
    /// Removes one Audio Target, refused while any mode still asks for it.
    /// </summary>
    public static ModeSaveOutcome RemoveTarget(string? root, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string wanted = name.Trim();

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            if (FindTargets(document) is not JsonObject targets || FindKey(targets, wanted) is not string key)
            {
                return ModeSaveOutcome.Failed($"'{wanted}' is not a configured audio target.");
            }

            if (LayoutConfigurationStore.ModesReferencing(document, AudioProvider.ResourceKey, key)
                is { Count: > 0 } referencing)
            {
                return ModeSaveOutcome.Failed(
                    $"'{key}' is used by {string.Join(", ", referencing)}. Change those first.");
            }

            targets.Remove(key);
            return null;
        });
    }

    /// <summary>
    /// Removes one audio output alias from 'providers.audio.devices', refused
    /// while any Audio Target or any mode still names it.
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="HardwareConfigurationStore.SetAudioAlias"/>,
    /// which adds and renames one. Without this, an alias added by a mis-click
    /// during setup could only be taken out again by editing JSON -- which is
    /// precisely what the graphical configuration exists to stop being necessary.
    /// </remarks>
    public static ModeSaveOutcome RemoveOutput(string? root, string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        string wanted = alias.Trim();

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            if (FindAudioSection(document) is not JsonObject audio ||
                HardwareConfigurationStore.FindProperty(audio, "devices") is not JsonObject devices ||
                FindKey(devices, wanted) is not string key)
            {
                return ModeSaveOutcome.Failed($"'{wanted}' is not a configured audio output.");
            }

            if (HardwareConfigurationStore.FindProperty(audio, "targets") is JsonObject targets)
            {
                List<string> used =
                [
                    .. targets
                        .Where(pair => pair.Value is JsonObject target && TargetNamesOutput(target, wanted))
                        .Select(pair => $"'{pair.Key}'"),
                ];

                if (used.Count > 0)
                {
                    return ModeSaveOutcome.Failed(
                        $"'{wanted}' is an output of {string.Join(", ", used)}. Change those first.");
                }
            }

            if (LayoutConfigurationStore.ModesReferencing(document, AudioProvider.ResourceKey, wanted)
                is { Count: > 0 } referencing)
            {
                return ModeSaveOutcome.Failed(
                    $"'{wanted}' is used by {string.Join(", ", referencing)}. Change those first.");
            }

            devices.Remove(key);
            return null;
        });
    }

    /// <summary>One target's routes, in the shape the document holds them.</summary>
    private static JsonObject Write(IReadOnlyList<AudioRouteEdit> routes)
    {
        var outputs = new JsonArray();

        foreach (AudioRouteEdit route in routes)
        {
            var written = new JsonObject
            {
                ["output"] = route.Output.Trim(),

                // Delay and gain are written even at their defaults. They are
                // the two numbers somebody calibrating by ear goes looking for
                // in the file, and a document that omitted them would read as
                // though nothing had ever been set.
                ["delayMs"] = route.DelayMs,
                ["gain"] = route.Gain,
            };

            if (route.Volume is double level)
            {
                written["volume"] = level;
            }

            if (!string.IsNullOrWhiteSpace(route.Profile))
            {
                written["profile"] = route.Profile.Trim();
            }

            if (!route.Required)
            {
                // Only when it is false. Required is what a route is unless
                // somebody said otherwise, and writing it everywhere would put
                // the exception on every line.
                written["required"] = false;
            }

            outputs.Add(written);
        }

        return new JsonObject { ["outputs"] = outputs };
    }

    /// <summary>Whether a written target names <paramref name="alias"/> as one of its outputs.</summary>
    private static bool TargetNamesOutput(JsonObject target, string alias) =>
        HardwareConfigurationStore.FindProperty(target, "outputs") is JsonArray outputs &&
        outputs.Any(output => string.Equals(OutputNameOf(output), alias, StringComparison.OrdinalIgnoreCase));

    /// <summary>The alias one written route names, in either form it may take.</summary>
    internal static string? OutputNameOf(JsonNode? written) => written switch
    {
        JsonValue value when value.TryGetValue(out string? name) => name?.Trim(),
        JsonObject route when HardwareConfigurationStore.FindProperty(route, "output") is JsonValue named &&
            named.TryGetValue(out string? name) => name?.Trim(),
        _ => null,
    };

    private static JsonObject? FindAudioSection(JsonObject document) =>
        HardwareConfigurationStore.FindProperty(document, "providers") is JsonObject providers
            ? HardwareConfigurationStore.FindProperty(providers, "audio") as JsonObject
            : null;

    private static JsonObject? FindTargets(JsonObject document) =>
        FindAudioSection(document) is JsonObject audio
            ? HardwareConfigurationStore.FindProperty(audio, "targets") as JsonObject
            : null;

    private static bool IsUsable(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static string? FindKey(JsonObject obj, string name) =>
        obj.Select(pair => pair.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
}

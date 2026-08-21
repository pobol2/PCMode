using System.Globalization;
using System.Text.Json.Nodes;

namespace PCMode.Engine;

/// <summary>
/// One Party group as a settings screen has it: two outputs named by their
/// PCMode aliases, which of the two is delayed, and how the pair is balanced.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="PartySettings"/> itself. That type is the parsed
/// result of a document that has already been validated -- its members are known
/// to exist, and its delay and gain are known to be in range. This is the
/// unchecked form a person has just typed, which is exactly what
/// <see cref="AudioConfigurationStore.SaveParty"/> is for.
/// </remarks>
/// <param name="ReferenceOutput">
/// The output Windows plays through and loopback capture reads back -- the
/// slower path in the room, usually the soundbar or television.
/// </param>
/// <param name="MirroredOutput">The copy, and the only one of the two that can be delayed.</param>
/// <param name="ReferenceVolume">
/// A Windows endpoint volume from 0 to 1, or null to leave that endpoint's own
/// slider wherever the user last put it.
/// </param>
public sealed record PartyGroupEdit(
    string Name,
    string ReferenceOutput,
    string MirroredOutput,
    int DelayMs,
    double Gain,
    double? ReferenceVolume = null,
    double? MirroredVolume = null);

/// <summary>
/// Creates, edits and removes the Party group and audio output aliases in the
/// same configuration file <see cref="AudioProvider"/> reads, by editing
/// 'providers.audio'.
/// </summary>
/// <remarks>
/// NO SECOND SOURCE OF TRUTH
///
/// This writes exactly the document <see cref="AudioProvider"/> parses and
/// <see cref="PartySettings.TryParse"/> checks, and validates the result through
/// the same production pipeline a real startup uses -- see
/// <see cref="ConfigurationDocumentEditor"/> -- before a single byte reaches
/// disk. The checks made here before that are the ones a person needs phrased in
/// their own terms ("choose two different outputs"); they never replace the
/// parser's, and a disagreement between the two is resolved by the parser
/// refusing the write.
///
/// EDITING NORMALISES TO THE CURRENT FORM
///
/// A Party group can be written two ways -- the 'outputs'/'delay' form and the
/// older 'reference'/'mirror' one -- and <see cref="PartySettings"/> reads both.
/// Saving through this writes the current form whichever one was there before,
/// which is what makes a document a person hand-wrote in the older form editable
/// on screen without a migration step. Nothing about what the group MEANS
/// changes: the two forms parse to the same reference-and-mirror pair.
///
/// REMOVAL FOLLOWS REFERENCES
///
/// The group's name is a value a mode's 'audio' resource takes, exactly as a
/// device alias is, so renaming the group rewrites every mode that named it, and
/// removing the group -- or an output the group is built from -- is refused, with
/// nothing changed, while anything still names it.
/// </remarks>
public static class AudioConfigurationStore
{
    /// <summary>What the group is called when a document defines one without naming it.</summary>
    private const string DefaultPartyName = "Party";

    /// <summary>
    /// Creates the Party group, or replaces the one already configured.
    /// </summary>
    /// <remarks>
    /// There is exactly one group, so this is a save rather than an add: the
    /// 'party' object is written whole every time. A group whose name changed
    /// takes every mode that named it along with it.
    /// </remarks>
    public static ModeSaveOutcome SaveParty(string? root, PartyGroupEdit group)
    {
        ArgumentNullException.ThrowIfNull(group);

        string name = group.Name?.Trim() ?? string.Empty;
        string reference = group.ReferenceOutput?.Trim() ?? string.Empty;
        string mirrored = group.MirroredOutput?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            return ModeSaveOutcome.Failed("Give the Party group a name. A mode asks for it by that name.");
        }

        if (reference.Length == 0 || mirrored.Length == 0)
        {
            return ModeSaveOutcome.Failed("Choose both of the Party group's outputs.");
        }

        if (string.Equals(reference, mirrored, StringComparison.OrdinalIgnoreCase))
        {
            // Capturing an endpoint's own loopback and rendering it back into
            // the same endpoint is a feedback loop, and it gets loud.
            return ModeSaveOutcome.Failed(
                $"'{reference}' cannot be both of the Party group's outputs. Choose two different outputs.");
        }

        if (group.DelayMs < 0 || group.DelayMs > PartySettings.MaxDelayMs)
        {
            return ModeSaveOutcome.Failed(
                $"The delay must be between 0 and {PartySettings.MaxDelayMs} ms.");
        }

        if (!IsUsable(group.Gain) || group.Gain < 0 || group.Gain > PartySettings.MaxGain)
        {
            return ModeSaveOutcome.Failed(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The mirrored gain must be between 0 and {PartySettings.MaxGain}."));
        }

        if (VolumeProblem(group.ReferenceVolume, reference) is string referenceProblem)
        {
            return ModeSaveOutcome.Failed(referenceProblem);
        }

        if (VolumeProblem(group.MirroredVolume, mirrored) is string mirroredProblem)
        {
            return ModeSaveOutcome.Failed(mirroredProblem);
        }

        return ConfigurationDocumentEditor.Edit(root, document =>
        {
            JsonObject providers = HardwareConfigurationStore.AsObject(document, "providers");
            JsonObject audio = HardwareConfigurationStore.AsObject(providers, "audio");

            string? previousName = ConfiguredPartyName(audio);

            var body = new JsonObject
            {
                ["name"] = name,
                ["outputs"] = new JsonArray(JsonValue.Create(reference), JsonValue.Create(mirrored)),

                // 'delay' is what says which output is the mirror, so it is
                // written even when the delay is zero. See PartySettings.
                ["delay"] = new JsonObject
                {
                    ["output"] = mirrored,
                    ["ms"] = group.DelayMs,
                },
                ["gain"] = new JsonObject
                {
                    ["output"] = mirrored,
                    ["value"] = group.Gain,
                },
            };

            if (group.ReferenceVolume is double referenceVolume || group.MirroredVolume is double)
            {
                var volumes = new JsonObject();

                if (group.ReferenceVolume is double level)
                {
                    volumes[reference] = level;
                }

                if (group.MirroredVolume is double mirroredLevel)
                {
                    volumes[mirrored] = mirroredLevel;
                }

                body["volumes"] = volumes;
            }

            HardwareConfigurationStore.RemoveCaseInsensitive(audio, "party");
            audio.Add("party", body);

            if (previousName is not null && !string.Equals(previousName, name, StringComparison.OrdinalIgnoreCase))
            {
                // The group's name is what a mode's 'audio' resource holds, not
                // a copy of it. Left alone, every mode that asked for the group
                // would now name nothing, and the validation below would say so.
                LayoutConfigurationStore.RenameModeReferences(
                    document, AudioProvider.ResourceKey, previousName, name);
            }

            return null;
        });
    }

    /// <summary>
    /// Removes the Party group, refused while any mode still asks for it.
    /// </summary>
    public static ModeSaveOutcome RemoveParty(string? root) =>
        ConfigurationDocumentEditor.Edit(root, document =>
        {
            if (FindAudioSection(document) is not JsonObject audio ||
                HardwareConfigurationStore.FindProperty(audio, "party") is null)
            {
                return ModeSaveOutcome.Failed("No Party group is configured.");
            }

            string name = ConfiguredPartyName(audio) ?? DefaultPartyName;

            if (LayoutConfigurationStore.ModesReferencing(document, AudioProvider.ResourceKey, name) is { Count: > 0 } referencing)
            {
                return ModeSaveOutcome.Failed(
                    $"'{name}' is used by {string.Join(", ", referencing)}. Change those first.");
            }

            HardwareConfigurationStore.RemoveCaseInsensitive(audio, "party");
            return null;
        });

    /// <summary>
    /// Removes one audio output alias from 'providers.audio.devices', refused
    /// while the Party group or any mode still names it.
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

            if (HardwareConfigurationStore.FindProperty(audio, "party") is JsonObject party &&
                PartyNamesOutput(party, wanted))
            {
                return ModeSaveOutcome.Failed(
                    $"'{wanted}' is one of the Party group's two outputs. Change the Party group first.");
            }

            if (LayoutConfigurationStore.ModesReferencing(document, AudioProvider.ResourceKey, wanted) is { Count: > 0 } referencing)
            {
                return ModeSaveOutcome.Failed(
                    $"'{wanted}' is used by {string.Join(", ", referencing)}. Change those first.");
            }

            devices.Remove(key);
            return null;
        });
    }

    /// <summary>
    /// The configured group's name, or null when no group is configured.
    /// </summary>
    /// <remarks>
    /// A group written without a 'name' is called Party, which is the same
    /// default <see cref="PartySettings.TryParse"/> applies -- so a rename away
    /// from the implicit name still finds the modes that were naming it.
    /// </remarks>
    private static string? ConfiguredPartyName(JsonObject audio) =>
        HardwareConfigurationStore.FindProperty(audio, "party") switch
        {
            JsonObject party => HardwareConfigurationStore.FindProperty(party, "name") is JsonValue named &&
                named.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
                    ? text.Trim()
                    : DefaultPartyName,
            _ => null,
        };

    /// <summary>
    /// Whether a written group names <paramref name="alias"/> as one of its two
    /// outputs, in either of the forms a group may be written in.
    /// </summary>
    private static bool PartyNamesOutput(JsonObject party, string alias)
    {
        if (HardwareConfigurationStore.FindProperty(party, "outputs") is JsonArray outputs &&
            outputs.Any(output => output is JsonValue value && value.TryGetValue(out string? name) &&
                string.Equals(name?.Trim(), alias, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (string field in (string[])["reference", "mirror"])
        {
            if (HardwareConfigurationStore.FindProperty(party, field) is JsonValue member &&
                member.TryGetValue(out string? memberName) &&
                string.Equals(memberName?.Trim(), alias, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonObject? FindAudioSection(JsonObject document) =>
        HardwareConfigurationStore.FindProperty(document, "providers") is JsonObject providers
            ? HardwareConfigurationStore.FindProperty(providers, "audio") as JsonObject
            : null;

    private static string? VolumeProblem(double? volume, string output) =>
        volume is double level && (!IsUsable(level) || level < 0 || level > 1)
            ? $"The volume for '{output}' must be between 0 and 100 percent."
            : null;

    private static bool IsUsable(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static string? FindKey(JsonObject obj, string name) =>
        obj.Select(pair => pair.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
}

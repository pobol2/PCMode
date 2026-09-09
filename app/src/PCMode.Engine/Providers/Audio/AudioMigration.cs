using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCMode.Engine;

/// <summary>
/// Converts a document's Party group into an Audio Target, once.
/// </summary>
/// <remarks>
/// WHY A CONVERSION AND NOT A COMPATIBILITY LAYER
///
/// A Party group and an Audio Target say the same thing about a room -- these
/// two outputs, this one delayed by that much, at these levels -- through two
/// different runtimes. The runtime that read 'party' no longer exists: nothing
/// captures an endpoint's loopback any more, and there is no reference output
/// because there is nothing to be a reference for. Keeping a parser for it would
/// mean keeping a shape whose words stopped being true, forever, so that
/// documents nobody has opened since can go on being read.
///
/// So it is converted the first time PCMode reads a document that still has one,
/// and the shape is then gone. The old file is kept beside the new one, and a
/// conversion that does not produce a document this build can actually load is
/// abandoned with nothing written.
///
/// WHAT SURVIVES, AND WHAT CANNOT
///
/// The group's name, both outputs, the delay, the mirrored gain and both
/// endpoint volumes all carry over exactly. The reference output becomes the
/// route with no delay and the mirrored output becomes the delayed one, which is
/// what the two roles meant: the reference was the slower path in the room and
/// the other was held back to meet it. Both routes are required, because a Party
/// group could not start unless both endpoints were there.
///
/// The one thing that can change is a delay above the engine's own bound -- the
/// old runtime allowed two seconds, a route allows one -- which is clamped and
/// reported rather than refused. A delay that large was a mistake in a file, and
/// refusing to start over it would strand the machine.
///
/// DETERMINISTIC
///
/// A pure function of the document text. The same input converts to the same
/// output every time, and converting an already-converted document is a no-op,
/// which is what makes it safe to run on every start.
/// </remarks>
public static class AudioMigration
{
    /// <summary>What a group with no name of its own is called, as it always was.</summary>
    private const string DefaultTargetName = "Party";

    /// <summary>Where the pre-conversion document is kept.</summary>
    public const string BackupFileName = "config.before-audio-targets.json";

    /// <summary>
    /// Converts 'providers.audio.party' into 'providers.audio.targets', if there
    /// is one.
    /// </summary>
    /// <param name="json">The document as it stands.</param>
    /// <param name="migrated">
    /// The converted document, or <paramref name="json"/> unchanged when there
    /// was nothing to convert.
    /// </param>
    /// <param name="summary">
    /// What was converted, for a log line, or null when nothing was.
    /// </param>
    /// <returns>Whether anything changed.</returns>
    public static bool TryConvert(string json, out string migrated, out string? summary)
    {
        ArgumentNullException.ThrowIfNull(json);

        migrated = json;
        summary = null;

        JsonObject? document;

        try
        {
            document = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            // Not readable as JSON at all. Refusing it is the loader's job, and
            // it phrases that failure far better than this could.
            return false;
        }

        if (
            document is null
            || HardwareConfigurationStore.FindProperty(document, "providers") is not JsonObject providers
            || HardwareConfigurationStore.FindProperty(providers, "audio") is not JsonObject audio
            || HardwareConfigurationStore.FindProperty(audio, "party") is not JsonObject party
        )
        {
            return false;
        }

        if (
            !TryRead(
                party,
                out string name,
                out string reference,
                out string delayed,
                out int delayMs,
                out double gain,
                out double? referenceVolume,
                out double? delayedVolume,
                out string? note
            )
        )
        {
            // A group this build cannot make sense of is left exactly where it
            // is. The loader then refuses the document by name, which is a far
            // better outcome than a silent half-conversion.
            return false;
        }

        JsonObject targets = HardwareConfigurationStore.AsObject(audio, "targets");

        if (FindKey(targets, name) is not null)
        {
            // A target already has the group's name. Converting on top of it
            // would overwrite something somebody wrote deliberately.
            return false;
        }

        targets.Add(
            name,
            new JsonObject
            {
                ["outputs"] = new JsonArray(
                    Route(reference, 0, 1.0, referenceVolume),
                    Route(delayed, delayMs, gain, delayedVolume)
                ),
            }
        );

        HardwareConfigurationStore.RemoveCaseInsensitive(audio, "party");

        migrated = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        summary =
            string.Create(
                CultureInfo.InvariantCulture,
                $"Converted the Party group '{name}' into an audio target: '{reference}' and '{delayed}' "
                    + $"delayed by {delayMs} ms at gain {gain:0.00}."
            ) + (note is null ? string.Empty : $" {note}");

        return true;
    }

    private static JsonObject Route(string output, int delayMs, double gain, double? volume)
    {
        var written = new JsonObject
        {
            ["output"] = output,
            ["delayMs"] = delayMs,
            ["gain"] = gain,
        };

        if (volume is double level)
        {
            written["volume"] = level;
        }

        return written;
    }

    /// <summary>
    /// Reads a written group in either of the two forms it was ever written in.
    /// </summary>
    /// <remarks>
    /// The 'outputs'/'delay' form and the older 'reference'/'mirror' one. Both
    /// meant one output that Windows played through and one that was delayed to
    /// meet it, so both convert to the same pair of routes.
    /// </remarks>
    private static bool TryRead(
        JsonObject party,
        out string name,
        out string reference,
        out string delayed,
        out int delayMs,
        out double gain,
        out double? referenceVolume,
        out double? delayedVolume,
        out string? note
    )
    {
        name = Text(party, "name") ?? DefaultTargetName;
        reference = string.Empty;
        delayed = string.Empty;
        delayMs = 0;
        gain = 1.0;
        referenceVolume = null;
        delayedVolume = null;
        note = null;

        if (HardwareConfigurationStore.FindProperty(party, "outputs") is JsonArray outputs)
        {
            List<string> members =
            [
                .. outputs
                    .Select(output =>
                        output is JsonValue value && value.TryGetValue(out string? text) ? text?.Trim() : null
                    )
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Select(text => text!),
            ];

            if (
                members.Count != 2
                || HardwareConfigurationStore.FindProperty(party, "delay") is not JsonObject delay
                || Text(delay, "output") is not string delayedOutput
                || members.FirstOrDefault(member =>
                    string.Equals(member, delayedOutput, StringComparison.OrdinalIgnoreCase)
                )
                    is not string chosen
            )
            {
                return false;
            }

            delayed = chosen;

            reference = members.First(member => !string.Equals(member, chosen, StringComparison.OrdinalIgnoreCase));

            delayMs = (int)Math.Round(Number(delay, "ms") ?? 0);

            if (HardwareConfigurationStore.FindProperty(party, "gain") is JsonObject gainObject)
            {
                gain = Number(gainObject, "value") ?? 1.0;
            }

            if (HardwareConfigurationStore.FindProperty(party, "volumes") is JsonObject volumes)
            {
                referenceVolume = Number(volumes, reference);
                delayedVolume = Number(volumes, delayed);
            }
        }
        else
        {
            if (
                Text(party, "reference") is not string legacyReference
                || Text(party, "mirror") is not string legacyMirror
                || string.Equals(legacyReference, legacyMirror, StringComparison.OrdinalIgnoreCase)
            )
            {
                return false;
            }

            reference = legacyReference;
            delayed = legacyMirror;
            delayMs = (int)Math.Round(Number(party, "delayMs") ?? 0);
            gain = Number(party, "gain") ?? 1.0;
            referenceVolume = Number(party, "referenceVolume");
            delayedVolume = Number(party, "mirrorVolume");
        }

        if (delayMs < 0 || gain < 0 || gain > AudioTargetSettings.MaxGain)
        {
            return false;
        }

        if (delayMs > AudioTargetSettings.MaxDelayMs)
        {
            note =
                $"The configured delay of {delayMs} ms was above the audio engine's limit and was reduced to "
                + $"{AudioTargetSettings.MaxDelayMs} ms.";

            delayMs = AudioTargetSettings.MaxDelayMs;
        }

        return !string.IsNullOrWhiteSpace(reference) && !string.IsNullOrWhiteSpace(delayed);
    }

    private static string? Text(JsonObject written, string field) =>
        HardwareConfigurationStore.FindProperty(written, field) is JsonValue value
        && value.TryGetValue(out string? text)
        && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static double? Number(JsonObject written, string field) =>
        HardwareConfigurationStore.FindProperty(written, field) is JsonValue value
        && value.TryGetValue(out double number)
        && !double.IsNaN(number)
        && !double.IsInfinity(number)
            ? number
            : null;

    private static string? FindKey(JsonObject obj, string name) =>
        obj.Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
}

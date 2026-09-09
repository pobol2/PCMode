using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCMode.Engine;

/// <summary>What PCMode last recorded, and when.</summary>
public sealed record RecordedMode(string Mode, DateTimeOffset RecordedUtc);

/// <summary>
/// The one place the current mode is written.
/// </summary>
/// <remarks>
/// WRITTEN ONLY AFTER A TRANSITION FULLY SUCCEEDS
///
/// The rule lives in <see cref="ModeEngine"/>, not here, but it is the reason
/// this class is deliberately dumb: it records what it is told and nothing
/// more, so there is no path by which a partial or dry run can quietly become a
/// recorded mode.
///
/// UNREADABLE IS NOT A CRASH
///
/// A missing, empty, truncated or foreign document reads as "no mode recorded",
/// with the problem reported alongside. The application has to start on a
/// machine where something else has been writing files.
/// </remarks>
public sealed class ModeStateStore(string path)
{
    /// <summary>The one document version this build reads and writes.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string Path { get; } =
        !string.IsNullOrWhiteSpace(path)
            ? path
            : throw new ArgumentException("A state file path is required.", nameof(path));

    /// <summary>
    /// The recorded mode, or null with <paramref name="problem"/> saying why
    /// there is not one.
    /// </summary>
    public RecordedMode? Read(out string? problem)
    {
        problem = null;

        if (!File.Exists(Path))
        {
            // The ordinary state of a machine that has not completed a
            // transition yet. Not a problem, and not reported as one.
            return null;
        }

        StateDocument? document;

        try
        {
            document = JsonSerializer.Deserialize<StateDocument>(File.ReadAllText(Path), ReadOptions);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = $"Could not read {Path}. {exception.Message}";
            return null;
        }

        if (document is null || string.IsNullOrWhiteSpace(document.Mode))
        {
            problem = $"{Path} does not name a mode.";
            return null;
        }

        if (document.SchemaVersion != SchemaVersion)
        {
            problem =
                $"{Path} is schema version {document.SchemaVersion}, and this build reads version {SchemaVersion}.";

            return null;
        }

        return new RecordedMode(document.Mode.Trim(), document.RecordedUtc);
    }

    /// <summary>
    /// Records <paramref name="mode"/>, or explains why it could not.
    /// </summary>
    /// <remarks>
    /// Best effort by design, and the caller must treat it that way. The
    /// machine has already been changed by the time this runs; a failure to
    /// write a file cannot be allowed to turn a transition that genuinely
    /// happened into one reported as failed. What it does mean is that the
    /// displayed current mode is no longer trustworthy, which is a different
    /// thing and is reported as such.
    /// </remarks>
    public bool TryWrite(string mode, DateTimeOffset recordedUtc, out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);

        try
        {
            string? folder = System.IO.Path.GetDirectoryName(Path);

            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var document = new StateDocument
            {
                SchemaVersion = SchemaVersion,
                Mode = mode,
                RecordedUtc = recordedUtc,
            };

            File.WriteAllText(Path, JsonSerializer.Serialize(document, WriteOptions));

            problem = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = $"Could not record the current mode in {Path}. {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// Forgets whatever mode was recorded, so that nothing claims to know.
    /// </summary>
    /// <remarks>
    /// Called at the last moment before a transition may change anything, and
    /// not afterwards. The recorded name stops being true the instant the first
    /// resource moves, not when the transition finishes going wrong -- and a
    /// transition that is cancelled, times out, fails part way, or is killed
    /// with the machine may never reach an "afterwards" at all. Clearing first
    /// is what makes a restarted application say "Unknown" rather than confidently
    /// name a mode the machine was dragged out of an hour ago.
    ///
    /// Absence is the representation, because absence is already what this reads
    /// as unknown: there is no second way of saying it and therefore no way for
    /// the two to disagree.
    /// </remarks>
    public bool TryClear(out string? problem)
    {
        problem = null;

        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Reported, never thrown. A transition must not be stopped from
            // happening by a file that could not be tidied up first, but the
            // recorded mode is now stale and somebody should be told.
            problem = $"Could not clear the recorded mode in {Path}. {exception.Message}";
            return false;
        }
    }

    private sealed class StateDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("recordedUtc")]
        public DateTimeOffset RecordedUtc { get; set; }
    }
}

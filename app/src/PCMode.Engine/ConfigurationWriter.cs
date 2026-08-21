namespace PCMode.Engine;

/// <summary>
/// Writes PCMode's configuration file the same way everywhere it is written:
/// atomically, and never without a backup of what worked before.
/// </summary>
/// <remarks>
/// ATOMIC, SO A CRASH MID-WRITE CANNOT LEAVE HALF A FILE
///
/// The candidate is written to a temporary file beside the real one first, and
/// only then put in its place with a single filesystem rename. A process
/// killed, a disk that fills, or a power cut in the middle leaves either the
/// old file or the new one -- on the same volume, never a file that is half of
/// each. <see cref="File.Replace(string, string, string?)"/> is what gives the
/// rename its second job: the file it replaces becomes <paramref name="path"/>'s
/// backup in the same atomic step, rather than in a second write that could
/// itself be interrupted.
///
/// LAST KNOWN GOOD, KEPT ONE GENERATION DEEP
///
/// Whatever <paramref name="path"/> held immediately before this call is what
/// the backup holds after it -- one generation, not a history. That is what
/// this backs up against: a candidate that validated cleanly and still turns
/// out to be wrong once it is the file everything reads, or a save someone
/// wants undone a moment after making it. <see cref="TryReadBackup"/> and
/// <see cref="RestoreBackup"/> are how it is read back.
/// </remarks>
public static class ConfigurationWriter
{
    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/>, backing up
    /// whatever was there first if anything was.
    /// </summary>
    public static void WriteAtomically(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        string? folder = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, content);

        if (File.Exists(path))
        {
            // One filesystem operation: path becomes what was in temp, and the
            // backup becomes what path held a moment ago.
            File.Replace(temp, path, BackupPath(path));
        }
        else
        {
            // Nothing to back up and nothing to replace -- the first write of a
            // file that has never existed.
            File.Move(temp, path);
        }
    }

    /// <summary>Where the backup of <paramref name="path"/> lives.</summary>
    public static string BackupPath(string path) => path + ".bak";

    /// <summary>
    /// The last-known-good backup of <paramref name="path"/>, if there is one
    /// and it can be read.
    /// </summary>
    public static bool TryReadBackup(string path, out string? content)
    {
        string backup = BackupPath(path);

        if (!File.Exists(backup))
        {
            content = null;
            return false;
        }

        try
        {
            content = File.ReadAllText(backup);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            content = null;
            return false;
        }
    }

    /// <summary>
    /// Puts the backup of <paramref name="path"/> back in place of it, or
    /// explains why it could not.
    /// </summary>
    /// <remarks>
    /// Goes back through <see cref="WriteAtomically"/>, so a restore is exactly
    /// as safe as any other write: the file it is replacing becomes the backup
    /// in turn, rather than being discarded. A recovery that could not itself be
    /// recovered from would not be much of one.
    /// </remarks>
    public static bool TryRestoreBackup(string path, out string? problem)
    {
        if (!TryReadBackup(path, out string? content) || content is null)
        {
            problem = $"There is no readable backup at {BackupPath(path)}.";
            return false;
        }

        try
        {
            WriteAtomically(path, content);
            problem = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problem = $"Could not restore {path} from its backup. {exception.Message}";
            return false;
        }
    }
}

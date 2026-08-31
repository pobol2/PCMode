using System.Text;
using System.IO;
using PCMode.Engine;

namespace PCMode.Tray.Platform.Logging;

/// <summary>Severity written to PCMode's local application log.</summary>
public enum LogLevel
{
    Information,
    Warning,
    Error,
}

/// <summary>
/// A small, dependency-free, bounded local log for user-support information.
/// </summary>
/// <remarks>
/// The current file and a small fixed number of archives are the whole store.
/// Logging is best effort: inability to diagnose PCMode must never stop PCMode
/// from running or hide the error it was trying to report.
/// </remarks>
public sealed class LocalLog
{
    public const long DefaultMaximumBytes = 512 * 1024;
    public const int DefaultFileCount = 3;

    private readonly object _gate = new();
    private readonly long _maximumBytes;
    private readonly int _fileCount;

    public LocalLog(
        string root,
        long maximumBytes = DefaultMaximumBytes,
        int fileCount = DefaultFileCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (maximumBytes < 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "A log file needs room for at least one useful entry.");
        }

        if (fileCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fileCount));
        }

        Folder = PCModePaths.LogFolder(root);
        CurrentFile = Path.Combine(Folder, "pcmode.log");
        _maximumBytes = maximumBytes;
        _fileCount = fileCount;
    }

    public string Folder { get; }

    public string CurrentFile { get; }

    public void Information(string area, string message) => Write(LogLevel.Information, area, message);

    public void Warning(string area, string message) => Write(LogLevel.Warning, area, message);

    public void Error(string area, string message, Exception? exception = null) =>
        Write(LogLevel.Error, area, exception is null ? message : $"{message} {Describe(exception)}");

    public void Write(LogLevel level, string area, string message)
    {
        int messageCharacters = (int)Math.Min(16_000, Math.Max(16, (_maximumBytes - 160) / 4));
        string entry = Format(level, Limit(area, 40), Limit(message, messageCharacters));

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                RotateIfNeeded(Encoding.UTF8.GetByteCount(entry));
                File.AppendAllText(CurrentFile, entry, Encoding.UTF8);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Best effort by design. There is nowhere safer to report a
                // logging failure, and recursive logging would only make it worse.
            }
        }
    }

    /// <summary>Reads the newest lines from the current log without throwing.</summary>
    public IReadOnlyList<string> ReadRecentLines(int maximumLines = 80)
    {
        if (maximumLines <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            try
            {
                if (!File.Exists(CurrentFile))
                {
                    return [];
                }

                string[] lines = File.ReadAllLines(CurrentFile);
                return [.. lines.TakeLast(maximumLines)];
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return [];
            }
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (!File.Exists(CurrentFile) || new FileInfo(CurrentFile).Length + incomingBytes <= _maximumBytes)
        {
            return;
        }

        if (_fileCount == 1)
        {
            File.Delete(CurrentFile);
            return;
        }

        string oldest = Archive(_fileCount - 1);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int index = _fileCount - 2; index >= 1; index--)
        {
            string source = Archive(index);
            if (File.Exists(source))
            {
                File.Move(source, Archive(index + 1));
            }
        }

        File.Move(CurrentFile, Archive(1));
    }

    private string Archive(int index) => Path.Combine(Folder, $"pcmode.{index}.log");

    private static string Format(LogLevel level, string area, string message)
    {
        string safeArea = string.IsNullOrWhiteSpace(area) ? "Application" : SingleLine(area);
        string safeMessage = string.IsNullOrWhiteSpace(message) ? "(no detail)" : SingleLine(message);

        return $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [{safeArea}] {safeMessage}{Environment.NewLine}";
    }

    private static string SingleLine(string text) =>
        text.Replace("\r\n", " | ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');

    private static string Limit(string? text, int maximumCharacters)
    {
        string value = text ?? string.Empty;
        return value.Length <= maximumCharacters ? value : value[..Math.Max(0, maximumCharacters - 3)] + "...";
    }

    private static string Describe(Exception exception)
    {
        var descriptions = new List<string>();

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            descriptions.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(" -> ", descriptions);
    }
}

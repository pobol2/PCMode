namespace PCMode.Engine;

/// <summary>
/// Where PCMode keeps its configuration and state.
/// </summary>
/// <remarks>
/// Per-user, under %LOCALAPPDATA%\PCMode\Native. The application owns that
/// subfolder outright and never reads or writes anything above it, so nothing
/// else keeping files under %LOCALAPPDATA%\PCMode can be mistaken for its own.
///
/// Every path is derived from a root that callers may override, so a test never
/// writes into the real profile.
/// </remarks>
public static class PCModePaths
{
    /// <summary>The parent folder under LocalApplicationData. Never written to directly.</summary>
    public const string FolderName = "PCMode";

    /// <summary>The subfolder this application owns outright.</summary>
    public const string NativeFolderName = "Native";

    /// <summary>The real per-user root.</summary>
    public static string DefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            FolderName,
            NativeFolderName
        );

    /// <summary>The versioned mode configuration.</summary>
    public static string ConfigurationFile(string root) => Path.Combine(root, "config.json");

    /// <summary>The recorded current mode.</summary>
    public static string StateFile(string root) => Path.Combine(root, "state.json");

    /// <summary>Bounded local diagnostic logs.</summary>
    public static string LogFolder(string root) => Path.Combine(root, "logs");
}

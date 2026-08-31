using System.Diagnostics;

namespace PCMode.Tray.Platform.Native;

/// <summary>The single shell boundary used to reveal PCMode's log folder.</summary>
public static class ShellFolderOpener
{
    public static void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

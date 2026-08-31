using System.IO;

namespace PCMode.Tray.Tests.Support;

internal static class TraySource
{
    private static readonly Lazy<string> Root = new(FindRoot);

    public static string PathTo(params string[] parts) =>
        Path.Combine([Root.Value, .. parts]);

    public static string Relative(string file) =>
        Path.GetRelativePath(Root.Value, file);

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string[] candidates =
            [
                Path.Combine(directory.FullName, "src", "PCMode.Tray"),
                Path.Combine(directory.FullName, "app", "src", "PCMode.Tray"),
            ];

            foreach (string candidate in candidates)
            {
                if (File.Exists(Path.Combine(candidate, "PCMode.Tray.csproj")))
                    return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate app/src/PCMode.Tray above '{AppContext.BaseDirectory}'.");
    }
}

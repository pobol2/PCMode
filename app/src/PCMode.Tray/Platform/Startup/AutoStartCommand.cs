using System.Text;

namespace PCMode.Tray.Platform.Startup;

/// <summary>Builds the command stored in the Run value.</summary>
public static class AutoStartCommand
{
    public static string Build(string executablePath, string? dataRoot, bool background = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        string command = Quote(executablePath);

        if (background)
        {
            command += " --background";
        }

        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            command += " --data-root " + Quote(dataRoot);
        }

        return command;
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder(value.Length + 2).Append('"');
        int slashes = 0;

        foreach (char character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }

            result.Append('\\', slashes).Append(character);
            slashes = 0;
        }

        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }
}

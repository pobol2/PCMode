using Microsoft.Win32;
using System.Text;

namespace PCMode.Tray.Settings;

/// <summary>The one per-user Windows startup value PCMode owns.</summary>
public interface IAutoStartRegistration
{
    string? ReadCommand();

    void WriteCommand(string? command);
}

/// <summary>HKCU Run registration; no elevation, service, or helper process.</summary>
public sealed class WindowsAutoStartRegistration : IAutoStartRegistration
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "PCMode";

    public string? ReadCommand()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    public void WriteCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            using RegistryKey? deleteKey = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            deleteKey?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}

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

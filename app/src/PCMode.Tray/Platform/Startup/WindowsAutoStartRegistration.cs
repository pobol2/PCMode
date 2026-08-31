using Microsoft.Win32;

namespace PCMode.Tray.Platform.Startup;

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

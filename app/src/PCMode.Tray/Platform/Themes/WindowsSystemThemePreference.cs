using System.IO;
using Microsoft.Win32;

namespace PCMode.Tray.Platform.Themes;

public enum EffectiveTheme
{
    Light,
    Dark,
}

public interface ISystemThemePreference
{
    EffectiveTheme Read();
}

public interface ISystemThemeChangeSource
{
    event EventHandler? Changed;
}

/// <summary>Reads the Windows preference used by ordinary application surfaces.</summary>
public sealed class WindowsSystemThemePreference : ISystemThemePreference, ISystemThemeChangeSource, IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public WindowsSystemThemePreference()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public event EventHandler? Changed;

    public EffectiveTheme Read()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0
                ? EffectiveTheme.Dark
                : EffectiveTheme.Light;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Windows' default when the value is absent or unreadable is light.
            return EffectiveTheme.Light;
        }
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Changed?.Invoke(this, EventArgs.Empty);
}

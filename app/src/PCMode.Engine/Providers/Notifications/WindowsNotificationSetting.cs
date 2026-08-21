using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PCMode.Engine;

/// <summary>
/// The undocumented registry value Windows checks before showing a toast.
/// </summary>
/// <remarks>
/// WHY THIS IS THE INTERFACE AND NOT A BETTER ONE
///
/// Because there is not a better one. Windows has no supported way for a program
/// to turn notifications off on the user's behalf: Focus Assist is a setting a
/// person changes, and the API that reads it cannot write it. What is left is the
/// value below, which Windows genuinely consults, which is genuinely not
/// documented, and whose behaviour has moved between builds.
///
/// That is the whole reason the resource is opt-in. Nothing here can make the
/// mechanism supported; what it can do is make sure the machine agreed to it,
/// write the one value and nothing else, and put the whole thing behind an
/// interface so that not one test has to touch a real registry to check the rules
/// that matter.
///
/// WHY THE NOTIFICATION SERVICE IS NOT RESTARTED
///
/// Restarting the per-user notification service would make the change take
/// effect at once rather than at the next toast, but stopping a service is a
/// machine-wide operation that ordinarily needs rights this application does not
/// have -- so the common outcome is failed attempts and a pause in the middle of
/// every transition. The setting is already written by the time it would run, so
/// what it buys is promptness rather than correctness. A broadcast is the cheap,
/// harmless, in-session version of the same nudge, and is what the theme does for
/// the same reason.
/// </remarks>
public sealed class WindowsNotificationSetting : INotificationSetting
{
    /// <summary>Where Windows keeps it.</summary>
    private const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    /// <summary>The value itself. Non-zero means toasts are allowed.</summary>
    private const string ToastsEnabled = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";

    public bool? ToastsAllowed()
    {
        try
        {
            using RegistryKey? settings = Registry.CurrentUser.OpenSubKey(SettingsKey);

            object? written = settings?.GetValue(ToastsEnabled);

            if (written is null)
            {
                // Nothing has ever been written, which is not an absence of an
                // answer: it is the Windows default, and the default is that
                // notifications are shown.
                return true;
            }

            return Convert.ToInt32(written, System.Globalization.CultureInfo.InvariantCulture) != 0;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void AllowToasts(bool allowed)
    {
        using RegistryKey settings = Registry.CurrentUser.CreateSubKey(SettingsKey)
            ?? throw new InvalidOperationException(
                $"Could not open HKCU\\{SettingsKey} to change whether notifications are shown.");

        settings.SetValue(ToastsEnabled, allowed ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Tells everything already running that a user setting changed.
    /// </summary>
    /// <remarks>
    /// Sent with a timeout and ABORTIFHUNG, because this is a broadcast to every
    /// top-level window on the desktop and one wedged program must not be able to
    /// hold a mode transition open. The result is deliberately ignored: the value
    /// is written either way.
    /// </remarks>
    public void Refresh()
    {
        _ = SendMessageTimeout(
            HwndBroadcast,
            WmSettingChange,
            nint.Zero,
            "Settings",
            SmtoAbortIfHung,
            millisecondsTimeout: 1000,
            out _);
    }

    private const int WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly nint HwndBroadcast = 0xFFFF;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint window,
        int message,
        nint wParam,
        string lParam,
        uint flags,
        uint millisecondsTimeout,
        out nint result);
}

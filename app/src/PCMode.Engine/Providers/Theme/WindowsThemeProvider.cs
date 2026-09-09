using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PCMode.Engine;

/// <summary>
/// Where the light/dark preference actually lives.
/// </summary>
/// <remarks>
/// A seam, and only a seam. It exists so the provider's parsing, comparison and
/// idempotency rules can be tested without writing to the registry of the
/// machine running the tests. The production implementation is
/// <see cref="RegistryThemeStore"/>; no fake is ever wired into the shell.
/// </remarks>
public interface IThemeStore
{
    /// <summary>
    /// True for light, false for dark, null when it cannot be read or when the
    /// two halves of the preference disagree.
    /// </summary>
    bool? ReadUsesLightTheme();

    /// <summary>Sets both the application and the system preference.</summary>
    void WriteUsesLightTheme(bool light);
}

/// <summary>
/// The first real native provider: the Windows light/dark preference.
/// </summary>
/// <remarks>
/// WHY THIS ONE FIRST
///
/// It is small, genuinely useful for the modes PCMode already has, and
/// completely reversible -- applying the other value restores exactly what was
/// there, with no hardware, no topology and no timing involved. It also needs
/// nothing installed and no configuration beyond a word, so a machine that has
/// never run PCMode gets a mode that really works on first launch.
///
/// That matters more than it sounds. Until now every transition this
/// application performed was somebody else's; this is the first one it does
/// itself, and it should be one where being wrong costs a user a repaint rather
/// than a black screen.
/// </remarks>
public sealed class WindowsThemeProvider : IConvergentProvider
{
    /// <summary>The configuration key.</summary>
    public const string ResourceKey = "theme";

    public const string Light = "Light";
    public const string Dark = "Dark";

    private readonly IThemeStore _store;

    public WindowsThemeProvider()
        : this(new RegistryThemeStore()) { }

    public WindowsThemeProvider(IThemeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public string Key => ResourceKey;

    public string DisplayName => "Windows theme";

    /// <summary>
    /// Confirmed by reading the preference back, and quickly.
    /// </summary>
    /// <remarks>
    /// The write is two registry values, so it is either there or it is not --
    /// there is nothing to settle and no reason to poll for long. It is still
    /// verified rather than assumed, because the failure this catches is not a
    /// slow one: a policy or another program that puts the preference back is
    /// invisible to a provider that only ever writes.
    ///
    /// A second attempt, and no more. Once a write has landed and been undone,
    /// writing it a third time is arguing with whatever is undoing it.
    /// </remarks>
    public ConvergencePolicy PolicyFor(string target) =>
        new()
        {
            Verify = true,
            MaxAttempts = 2,
            RetryInterval = TimeSpan.FromSeconds(2),
            PollInterval = TimeSpan.FromMilliseconds(200),
            Settle = TimeSpan.Zero,
        };

    public bool IsValidValue(string value, out string? problem)
    {
        if (TryNormalise(value, out _))
        {
            problem = null;
            return true;
        }

        problem = $"'{value}' is not a theme. Use {Light} or {Dark}.";
        return false;
    }

    public string? Read()
    {
        bool? light = _store.ReadUsesLightTheme();

        return light switch
        {
            true => Light,
            false => Dark,
            null => null,
        };
    }

    public void Apply(string value, CancellationToken cancellationToken)
    {
        if (!TryNormalise(value, out bool light))
        {
            // Unreachable through the engine, which validates first. Kept
            // because a provider that trusted its caller would write a
            // meaningless value the one time somebody called it directly.
            throw new ArgumentException($"'{value}' is not a theme.", nameof(value));
        }

        cancellationToken.ThrowIfCancellationRequested();

        _store.WriteUsesLightTheme(light);
    }

    private static bool TryNormalise(string? value, out bool light)
    {
        light = false;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();

        if (string.Equals(trimmed, Light, StringComparison.OrdinalIgnoreCase))
        {
            light = true;
            return true;
        }

        return string.Equals(trimmed, Dark, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The real thing: two per-user registry values, and a broadcast so the shell
/// notices.
/// </summary>
/// <remarks>
/// Both values are set together. Windows keeps the application preference and
/// the system preference (taskbar, Start) separately, and setting only one
/// leaves a half-dark desktop that looks like a bug rather than like a mode.
///
/// Everything here is HKEY_CURRENT_USER. Nothing is written outside the user's
/// own hive, nothing is registered to run at startup, and the change is undone
/// by writing the other value.
/// </remarks>
public sealed class RegistryThemeStore : IThemeStore
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string AppsValue = "AppsUseLightTheme";
    private const string SystemValue = "SystemUsesLightTheme";

    /// <summary>
    /// Light only when both halves say light, dark only when both say dark.
    /// </summary>
    /// <remarks>
    /// The write sets both values, so the readback has to check both. Reading
    /// only the application preference would report a desktop with a dark taskbar
    /// and light windows as a finished theme, which is precisely the half-applied
    /// state the two values are set together to avoid -- and, being a readback,
    /// it would also be the thing that told the engine the transition had
    /// converged.
    ///
    /// Disagreement answers null rather than picking a side. Null already means
    /// "not there yet", which is exactly what a half-written preference is, so the
    /// engine applies it and reads again instead of settling for it.
    /// </remarks>
    public bool? ReadUsesLightTheme()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);

        if (key is null)
        {
            // The whole key is missing, which is not something this code can
            // interpret. Null means "apply it anyway", which is the safe answer.
            return null;
        }

        return Agreed(ReadLight(key, AppsValue), ReadLight(key, SystemValue));
    }

    /// <summary>
    /// What the two halves of the preference say together, or null when they do
    /// not say the same thing.
    /// </summary>
    /// <remarks>
    /// Separated from the registry so that the rule can be stated and checked
    /// without a test having to write to the hive of the machine it is running
    /// on. It is one line, and it is the line that decides whether a
    /// half-applied theme is reported to the engine as a finished one.
    /// </remarks>
    public static bool? Agreed(bool? apps, bool? system) => apps == system ? apps : null;

    /// <summary>
    /// One half of the preference, or null when it is there and unreadable.
    /// </summary>
    /// <remarks>
    /// A profile that has never been switched has no value at all, and the
    /// documented default is light. Answering null for that would make the first
    /// Light transition on a fresh machine report work it did not do.
    /// </remarks>
    private static bool? ReadLight(RegistryKey key, string name)
    {
        object? value = key.GetValue(name);

        if (value is null)
        {
            return true;
        }

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            // Something else has written a value of a shape this cannot read.
            // Not knowing is an answer; guessing is not.
            return null;
        }
    }

    public void WriteUsesLightTheme(bool light)
    {
        using RegistryKey key =
            Registry.CurrentUser.CreateSubKey(PersonalizeKey, writable: true)
            ?? throw new InvalidOperationException(
                $"Could not open HKCU\\{PersonalizeKey} to change the Windows theme."
            );

        int value = light ? 1 : 0;

        key.SetValue(AppsValue, value, RegistryValueKind.DWord);
        key.SetValue(SystemValue, value, RegistryValueKind.DWord);

        Announce();
    }

    /// <summary>
    /// Tells everything already running that the colours changed.
    /// </summary>
    /// <remarks>
    /// Without this the registry is correct and the desktop is not: Explorer
    /// and every open application keep the old theme until they are restarted,
    /// so the mode would appear to have done nothing.
    ///
    /// Sent with a timeout and ABORTIFHUNG, because this is a broadcast to
    /// every top-level window on the desktop and one wedged program must not be
    /// able to hold a mode transition open. A failed or timed-out broadcast is
    /// deliberately ignored: the preference itself is written either way, and
    /// the worst case is a window that repaints later.
    /// </remarks>
    private static void Announce()
    {
        _ = SendMessageTimeout(
            HwndBroadcast,
            WmSettingChange,
            nint.Zero,
            "ImmersiveColorSet",
            SmtoAbortIfHung,
            millisecondsTimeout: 1000,
            out _
        );
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
        out nint result
    );
}

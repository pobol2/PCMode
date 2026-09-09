using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PCMode.Tray.Platform.Themes;

/// <summary>
/// Owns the one semantic color dictionary used by every WPF surface.
/// </summary>
/// <remarks>
/// Both palettes are namespaced: <c>UI/Components/Theme/Dark.xaml</c> defines the
/// 13-key palette under a <c>Dark.</c> prefix and <c>UI/Components/Theme/Light.xaml</c>
/// defines the same 13 keys under a <c>Light.</c> prefix. Neither file defines
/// the unprefixed keys that <c>UI/Components/Theme/heme.xaml</c> binds to via
/// <c>DynamicResource</c> -- <see cref="Apply"/> is what writes the unprefixed,
/// in-<see cref="_resources"/> copy of each key from whichever palette
/// currently applies, so the merge order of the two files in <c>App.xaml</c>
/// is not what decides which is visible at any moment.
/// </remarks>
public sealed class ApplicationThemeService : IDisposable
{
    private const int ImmersiveDarkMode = 20;
    private const int ImmersiveDarkModeBefore20H1 = 19;

    private readonly ResourceDictionary _resources;
    private readonly ISystemThemePreference _system;
    private readonly ISystemThemeChangeSource? _systemChanges;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly IReadOnlyDictionary<string, object> _lightPalette;
    private readonly IReadOnlyDictionary<string, object> _darkPalette;

    /// <summary>Target's 13-key palette (<c>UI/Components/Theme/Theme.xaml</c>), not legacy's 34-key set.</summary>
    public static IReadOnlyList<string> SemanticResourceKeys { get; } =
    [
        "AccentBrush",
        "AccentTextBrush",
        "ErrorBrush",
        "WarningBrush",
        "DisabledTextBrush",
        "WindowBackgroundBrush",
        "SurfaceBrush",
        "SurfaceHoverBrush",
        "ForegroundBrush",
        "MutedForegroundBrush",
        "DividerBrush",
        "BorderBrush",
        "SuccessBrush",
    ];

    public ApplicationThemeService(ResourceDictionary resources, ISystemThemePreference system)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(system);

        _resources = resources;
        _system = system;
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _darkPalette = CapturePalette(resources, "Dark.");
        _lightPalette = CapturePalette(resources, "Light.");

        _systemChanges = system as ISystemThemeChangeSource;
        if (_systemChanges is not null)
        {
            _systemChanges.Changed += OnSystemThemeChanged;
        }
    }

    public ApplicationTheme Selected { get; private set; } = ApplicationTheme.System;

    public EffectiveTheme Effective { get; private set; } = EffectiveTheme.Dark;

    /// <summary>Raised after the semantic brushes and native title bars have changed.</summary>
    public event EventHandler? ThemeChanged;

    public void Apply(ApplicationTheme selected)
    {
        Selected = selected;
        Effective = Resolve(selected, _system.Read());

        IReadOnlyDictionary<string, object> palette = Effective == EffectiveTheme.Dark ? _darkPalette : _lightPalette;

        foreach (string key in SemanticResourceKeys)
        {
            _resources[key] = palette[key];
        }

        if (System.Windows.Application.Current is { } application)
        {
            foreach (Window window in application.Windows)
            {
                ApplyTitleBar(window, Effective);
            }
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyTo(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (new WindowInteropHelper(window).Handle == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) => ApplyTitleBar(window, Effective);
        }
        else
        {
            ApplyTitleBar(window, Effective);
        }
    }

    public static EffectiveTheme Resolve(ApplicationTheme selected, EffectiveTheme system) =>
        selected switch
        {
            ApplicationTheme.Light => EffectiveTheme.Light,
            ApplicationTheme.Dark => EffectiveTheme.Dark,
            _ => system,
        };

    public void Dispose()
    {
        if (_systemChanges is not null)
        {
            _systemChanges.Changed -= OnSystemThemeChanged;
        }

        if (_system is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        if (Selected != ApplicationTheme.System)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            Apply(ApplicationTheme.System);
        }
        else
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (Selected == ApplicationTheme.System)
                {
                    Apply(ApplicationTheme.System);
                }
            });
        }
    }

    private static IReadOnlyDictionary<string, object> CapturePalette(ResourceDictionary resources, string? prefix)
    {
        var palette = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (string key in SemanticResourceKeys)
        {
            string sourceKey = $"{prefix}{key}";
            palette[key] =
                resources[sourceKey]
                ?? throw new InvalidOperationException($"The theme resource '{sourceKey}' is missing.");
        }

        return palette;
    }

    private static void ApplyTitleBar(Window window, EffectiveTheme theme)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int enabled = theme == EffectiveTheme.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, ImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}

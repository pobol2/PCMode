using PCMode.Engine;
using PCMode.Picker;

namespace PCMode.Tray.Settings;

/// <summary>What PCMode should do after its shell has started.</summary>
public enum LaunchBehavior
{
    DoNothing,
    RestorePreviousMode,
    SwitchToMode,
}

/// <summary>How PCMode chooses the colors for its own windows.</summary>
public enum ApplicationTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Which display the picker appears on.
/// </summary>
/// <remarks>
/// Ported ahead of the picker itself, because <see cref="ApplicationSettings"/>
/// persists it. Legacy defines this alongside <c>MonitorLayout</c>'s screen
/// geometry in its own <c>Picker/</c> folder; there is nothing to geometrically
/// resolve here yet, so this stays a plain enum next to the settings that read
/// and write it until the picker page gives it a real home.
/// </remarks>
public enum PickerMonitorChoice
{
    Primary,
    UnderPointer,
    WithForegroundWindow,
}

/// <summary>A direct Ctrl+Alt shortcut to one activatable target.</summary>
/// <remarks>
/// This deliberately does not belong to <see cref="ModeFavorite"/>. A target
/// can be launched directly without occupying one of the picker's four
/// positions, and one target may retain multiple legacy shortcut letters.
/// </remarks>
public sealed record DirectShortcut(string Target, char Key, string Label);

/// <summary>
/// Presentation-neutral settings for the PCMode application itself.
/// </summary>
/// <remarks>
/// The intent hierarchy remains in ModeConfiguration and picker mechanics remain
/// in PCMode.Picker. This model only says how the tray application connects
/// those pieces for one user.
/// </remarks>
public sealed record ApplicationSettings
{
    public ApplicationTheme Theme { get; init; } = ApplicationTheme.System;

    public PickerMonitorChoice PickerMonitor { get; init; } = PickerMonitorChoice.Primary;

    public FavoriteSet Favorites { get; init; } = FavoriteSet.Default;

    public IReadOnlyList<DirectShortcut> DirectShortcuts { get; init; } =
    [
        .. FavoriteSet.Default.All
            .Where(favorite => favorite.Hotkey is not null)
            .Select(favorite => new DirectShortcut(
                favorite.ModeName,
                char.ToLowerInvariant(favorite.Hotkey!.Value),
                favorite.Label)),
    ];

    public bool ControllerEnabled { get; init; } = true;

    public bool StartWithWindows { get; init; }

    public bool StartMinimizedToTray { get; init; }

    public LaunchBehavior LaunchBehavior { get; init; } = LaunchBehavior.DoNothing;

    /// <summary>The key of the target PCMode switches to at startup.</summary>
    public string? StartupTarget { get; init; }

    /// <summary>Set once the first-run setup wizard is explicitly finished or dismissed.</summary>
    public bool SetupCompleted { get; init; }

    /// <summary>
    /// Defaults for a configuration with no application section.
    /// </summary>
    /// <remarks>
    /// The four traditional wheel names are retained when a target is called
    /// one of them -- so a machine whose activities are still Desktop, YouTube,
    /// Gaming and Movie gets the wheel it always had -- and otherwise the
    /// configured targets are assigned in their existing order.
    ///
    /// A favorite holds the target's key and the target's name separately,
    /// because those are two different jobs: the key is what is applied and must
    /// survive a rename, and the name is what is drawn on the wedge.
    /// </remarks>
    public static ApplicationSettings DefaultFor(TargetCatalog targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Targets.Count == 0)
        {
            return new ApplicationSettings();
        }

        List<ModeFavorite> favorites = [];
        List<DirectShortcut> shortcuts = [];
        int fallback = 0;

        foreach (ModeFavorite favorite in FavoriteSet.Default.All)
        {
            ConfiguredTarget target = targets.Targets.FirstOrDefault(candidate =>
                    string.Equals(candidate.DisplayName, favorite.ModeName, StringComparison.OrdinalIgnoreCase))
                ?? targets.Targets[fallback++ % targets.Targets.Count];

            ModeFavorite mapped = favorite with
            {
                Id = $"{favorite.Direction.ToString().ToLowerInvariant()}-{target.Key.ToLowerInvariant()}",
                ModeName = target.Key,
                Label = target.DisplayName,
                Hotkey = null,
            };

            favorites.Add(mapped);

            if (favorite.Hotkey is char key)
            {
                shortcuts.Add(new DirectShortcut(target.Key, char.ToLowerInvariant(key), target.DisplayName));
            }
        }

        return new ApplicationSettings
        {
            Favorites = FavoriteSet.Create(favorites),
            DirectShortcuts = shortcuts,
        };
    }

    /// <summary>Validates a complete candidate without changing anything.</summary>
    /// <remarks>
    /// Through the catalog, so a reference that still uses a name rather than a
    /// key is fine while it names one configured target, and refused the moment
    /// it names two. Disabled targets still resolve here because disabling one
    /// preserves its favorite and startup references; the engine refuses an
    /// actual switch until it is enabled again.
    /// </remarks>
    public string? Validate(TargetCatalog targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        foreach (ModeFavorite favorite in Favorites.All)
        {
            if (!favorite.IsAssigned)
            {
                continue;
            }

            if (!targets.TryResolve(favorite.ModeName, out _, out string? problem))
            {
                return $"The {favorite.Direction} picker favorite names '{favorite.ModeName}'. {problem}";
            }
        }

        var shortcutKeys = new HashSet<char>();

        foreach (DirectShortcut shortcut in DirectShortcuts)
        {
            if (!char.IsAsciiLetter(shortcut.Key))
            {
                return $"The direct shortcut for '{shortcut.Target}' must be one letter A to Z.";
            }

            char key = char.ToLowerInvariant(shortcut.Key);
            if (!shortcutKeys.Add(key))
            {
                return $"Two direct shortcuts use Ctrl+Alt+{char.ToUpperInvariant(key)}.";
            }

            if (!targets.TryResolve(shortcut.Target, out _, out string? shortcutProblem))
            {
                return $"The Ctrl+Alt+{char.ToUpperInvariant(key)} shortcut names '{shortcut.Target}'. {shortcutProblem}";
            }
        }

        if (LaunchBehavior == LaunchBehavior.SwitchToMode)
        {
            if (string.IsNullOrWhiteSpace(StartupTarget))
            {
                return "Choose the target PCMode should switch to at startup.";
            }

            if (!targets.TryResolve(StartupTarget, out _, out string? startupProblem))
            {
                return $"The startup target names '{StartupTarget}'. {startupProblem}";
            }
        }

        return null;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using PCMode.Engine;
using PCMode.Picker;
using PCMode.Tray.Platform.Startup;

namespace PCMode.Tray.Elements.Settings;

public sealed record SettingsSaveOutcome(bool Success, string? Problem)
{
    public static readonly SettingsSaveOutcome Ok = new(true, null);

    public static SettingsSaveOutcome Failed(string problem) => new(false, problem);
}

/// <summary>
/// Reads and writes the single 'application' section in PCMode's config.json.
/// </summary>
public sealed class ApplicationSettingsStore
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _root;
    private readonly IAutoStartRegistration _autoStart;
    private readonly string _autoStartCommand;

    public ApplicationSettingsStore(
        string? root,
        IAutoStartRegistration autoStart,
        string autoStartCommand)
    {
        ArgumentNullException.ThrowIfNull(autoStart);
        ArgumentException.ThrowIfNullOrWhiteSpace(autoStartCommand);

        _root = string.IsNullOrWhiteSpace(root) ? PCModePaths.DefaultRoot : root;
        _autoStart = autoStart;
        _autoStartCommand = autoStartCommand;
    }

    public ApplicationSettings Load(TargetCatalog targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        (string json, string path) = ModeConfiguration.ReadOrCreate(_root);
        JsonObject document = ParseObject(json, path);

        if (Find(document, "application") is null)
        {
            return ApplicationSettings.DefaultFor(targets);
        }

        if (Find(document, "application") is not JsonObject application)
        {
            throw new ModeConfigurationException("'application' must be an object.");
        }

        ApplicationSettings defaults = ApplicationSettings.DefaultFor(targets);
        ApplicationSettings settings = ReadApplication(application, defaults, targets);
        string? problem = settings.Validate(targets);

        if (problem is not null)
        {
            throw new ModeConfigurationException($"'application': {problem}");
        }

        return settings;
    }

    /// <summary>
    /// Validates the whole candidate, changes the Run value, then writes the
    /// configuration. A write failure restores the previous Run value.
    /// </summary>
    public SettingsSaveOutcome Save(ApplicationSettings settings, TargetCatalog targets)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(targets);

        string? settingsProblem = settings.Validate(targets);
        if (settingsProblem is not null)
        {
            return SettingsSaveOutcome.Failed(settingsProblem);
        }

        string path = PCModePaths.ConfigurationFile(_root);
        string original;

        try
        {
            original = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SettingsSaveOutcome.Failed($"Could not read {path}. {exception.Message}");
        }

        JsonObject document;

        try
        {
            document = ParseObject(original, path);
        }
        catch (ModeConfigurationException exception)
        {
            return SettingsSaveOutcome.Failed(exception.Message);
        }

        Remove(document, "application");
        document.Add("application", WriteApplication(settings));
        string candidate = document.ToJsonString(WriteOptions);

        try
        {
            IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(_root, candidate);
            ModeConfiguration.Parse(candidate, providers, path);
        }
        catch (ModeConfigurationException exception)
        {
            return SettingsSaveOutcome.Failed($"That could not be saved: {exception.Message}");
        }

        string? previousCommand;

        try
        {
            previousCommand = _autoStart.ReadCommand();
            _autoStart.WriteCommand(DesiredAutoStartCommand(settings));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return SettingsSaveOutcome.Failed($"Windows startup could not be updated. {exception.Message}");
        }

        try
        {
            // Atomic, and backed up: see ConfigurationWriter.
            ConfigurationWriter.WriteAtomically(path, candidate);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                _autoStart.WriteCommand(previousCommand);
            }
            catch (Exception rollback) when (rollback is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                return SettingsSaveOutcome.Failed(
                    $"Could not write {path}. {exception.Message} Windows startup also could not be restored: {rollback.Message}");
            }

            return SettingsSaveOutcome.Failed($"Could not write {path}. {exception.Message}");
        }

        return SettingsSaveOutcome.Ok;
    }

    /// <summary>Repairs a missing/stale Run value from the persisted source of truth.</summary>
    public string? SynchronizeAutoStart(ApplicationSettings settings)
    {
        string? desired = DesiredAutoStartCommand(settings);

        try
        {
            string? current = _autoStart.ReadCommand();
            if (!string.Equals(current, desired, StringComparison.Ordinal))
            {
                _autoStart.WriteCommand(desired);
            }

            return null;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return $"Windows startup registration could not be synchronized. {exception.Message}";
        }
    }

    private static ApplicationSettings ReadApplication(
        JsonObject application, ApplicationSettings defaults, TargetCatalog targets)
    {
        JsonObject picker = OptionalObject(application, "picker");
        JsonObject controller = OptionalObject(application, "controller");
        JsonObject startup = OptionalObject(application, "startup");
        JsonObject appearance = OptionalObject(application, "appearance");
        JsonObject setup = OptionalObject(application, "setup");

        ApplicationTheme theme = ReadString(appearance, "theme")?.ToLowerInvariant() switch
        {
            null or "system" => ApplicationTheme.System,
            "light" => ApplicationTheme.Light,
            "dark" => ApplicationTheme.Dark,
            string value => throw new ModeConfigurationException(
                $"'application.appearance.theme' is '{value}'. Use system, light or dark."),
        };

        PickerMonitorChoice monitor = ReadString(picker, "monitor")?.ToLowerInvariant() switch
        {
            null => defaults.PickerMonitor,
            "primary" => PickerMonitorChoice.Primary,
            "pointer" => PickerMonitorChoice.UnderPointer,
            "foreground" => PickerMonitorChoice.WithForegroundWindow,
            string value => throw new ModeConfigurationException(
                $"'application.picker.monitor' is '{value}'. Use primary, pointer or foreground."),
        };

        JsonNode? favoriteNode = Find(picker, "favorites");
        FavoriteSet favoritesWithLegacyHotkeys = favoriteNode switch
        {
            null => defaults.Favorites,
            JsonArray array => ReadFavorites(array, defaults.Favorites, targets),
            _ => throw new ModeConfigurationException("'application.picker.favorites' must be an array."),
        };

        IReadOnlyList<DirectShortcut> shortcuts = Find(application, "shortcuts") switch
        {
            null when favoriteNode is null => defaults.DirectShortcuts,
            null => LegacyShortcuts(favoritesWithLegacyHotkeys, targets),
            JsonArray array => ReadShortcuts(array, targets),
            _ => throw new ModeConfigurationException("'application.shortcuts' must be an array."),
        };

        FavoriteSet favorites = FavoriteSet.Create(
            favoritesWithLegacyHotkeys.All.Select(favorite => favorite with { Hotkey = null }));

        bool controllerEnabled = ReadBoolean(controller, "enabled") ?? defaults.ControllerEnabled;
        bool setupCompleted = ReadBoolean(setup, "completed") ?? defaults.SetupCompleted;
        bool startWithWindows = ReadBoolean(startup, "startWithWindows") ?? defaults.StartWithWindows;
        bool startMinimizedToTray = ReadBoolean(startup, "startMinimizedToTray") ?? defaults.StartMinimizedToTray;

        LaunchBehavior behavior = ReadString(startup, "behavior")?.ToLowerInvariant() switch
        {
            null or "donothing" => LaunchBehavior.DoNothing,
            "restorepreviousmode" => LaunchBehavior.RestorePreviousMode,
            "switchtomode" => LaunchBehavior.SwitchToMode,
            string value => throw new ModeConfigurationException(
                $"'application.startup.behavior' is '{value}'. Use doNothing, restorePreviousMode or switchToMode."),
        };

        return defaults with
        {
            Theme = theme,
            PickerMonitor = monitor,
            Favorites = favorites,
            DirectShortcuts = shortcuts,
            ControllerEnabled = controllerEnabled,
            StartWithWindows = startWithWindows,
            StartMinimizedToTray = startMinimizedToTray,
            LaunchBehavior = behavior,
            StartupTarget = ReadTarget(startup),
            SetupCompleted = setupCompleted,
        };
    }

    private static FavoriteSet ReadFavorites(JsonArray array, FavoriteSet defaults, TargetCatalog targets)
    {
        var favorites = new List<ModeFavorite>();

        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject item)
            {
                throw new ModeConfigurationException("Each application picker favorite must be an object.");
            }

            string directionText = ReadString(item, "direction")
                ?? throw new ModeConfigurationException("An application picker favorite has no direction.");

            if (!Enum.TryParse(directionText, ignoreCase: true, out PickerDirection direction))
            {
                throw new ModeConfigurationException($"'{directionText}' is not a picker direction.");
            }

            string reference = ReadTarget(item) ?? string.Empty;

            string? hotkeyText = ReadString(item, "hotkey");
            char? hotkey = hotkeyText switch
            {
                null or "" => null,
                { Length: 1 } value when char.IsAsciiLetter(value[0]) => char.ToLowerInvariant(value[0]),
                _ => throw new ModeConfigurationException(
                    $"The {direction} picker hotkey must be one letter A to Z, or empty."),
            };

            ModeFavorite appearance = defaults[direction];

            favorites.Add(appearance with
            {
                Id = reference.Length == 0
                    ? $"{direction.ToString().ToLowerInvariant()}-none"
                    : $"{direction.ToString().ToLowerInvariant()}-{reference.ToLowerInvariant()}",
                ModeName = reference,

                // What the wedge says is the target's name, not its key: the key
                // is an identity, and 'desk/gaming/steam' is not a label. An
                // unresolved reference is drawn as itself, so a wheel pointing
                // at something that no longer exists says so rather than
                // showing a blank.
                Label = targets.Resolve(reference)?.DisplayName ??
                    (reference.Length == 0 ? "Not assigned" : reference),
                Icon = reference.Length == 0 ? FavoriteIcon.None : appearance.Icon,
                Hotkey = hotkey,
            });
        }

        try
        {
            return FavoriteSet.Create(favorites);
        }
        catch (ArgumentException exception)
        {
            throw new ModeConfigurationException($"'application.picker.favorites': {exception.Message}");
        }
    }

    private static IReadOnlyList<DirectShortcut> ReadShortcuts(JsonArray array, TargetCatalog targets)
    {
        var shortcuts = new List<DirectShortcut>();

        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject item)
            {
                throw new ModeConfigurationException("Each application shortcut must be an object.");
            }

            string reference = ReadTarget(item)
                ?? throw new ModeConfigurationException("An application shortcut has no target.");
            string keyText = ReadString(item, "key") ?? ReadString(item, "hotkey") ?? string.Empty;

            if (keyText.Length != 1 || !char.IsAsciiLetter(keyText[0]))
            {
                throw new ModeConfigurationException(
                    $"The direct shortcut for '{reference}' must be one letter A to Z.");
            }

            shortcuts.Add(new DirectShortcut(
                reference,
                char.ToLowerInvariant(keyText[0]),
                targets.Resolve(reference)?.DisplayName ?? reference));
        }

        return shortcuts;
    }

    private static IReadOnlyList<DirectShortcut> LegacyShortcuts(
        FavoriteSet favorites, TargetCatalog targets) =>
    [
        .. favorites.All
            .Where(favorite => favorite.Hotkey is not null)
            .Select(favorite => new DirectShortcut(
                favorite.ModeName,
                char.ToLowerInvariant(favorite.Hotkey!.Value),
                targets.Resolve(favorite.ModeName)?.DisplayName ?? favorite.Label)),
    ];

    private static JsonObject WriteApplication(ApplicationSettings settings) => new()
    {
        ["appearance"] = new JsonObject
        {
            ["theme"] = settings.Theme.ToString().ToLowerInvariant(),
        },
        ["picker"] = new JsonObject
        {
            ["monitor"] = settings.PickerMonitor switch
            {
                PickerMonitorChoice.UnderPointer => "pointer",
                PickerMonitorChoice.WithForegroundWindow => "foreground",
                _ => "primary",
            },
            ["favorites"] = new JsonArray(settings.Favorites.All.Select(favorite =>
                (JsonNode)new JsonObject
                {
                    ["direction"] = favorite.Direction.ToString(),
                    ["target"] = favorite.IsAssigned ? favorite.ModeName : null,
                }).ToArray()),
        },
        ["shortcuts"] = new JsonArray(settings.DirectShortcuts.Select(shortcut =>
            (JsonNode)new JsonObject
            {
                ["target"] = shortcut.Target,
                ["key"] = shortcut.Key.ToString().ToUpperInvariant(),
            }).ToArray()),
        ["controller"] = new JsonObject { ["enabled"] = settings.ControllerEnabled },
        ["startup"] = new JsonObject
        {
            ["startWithWindows"] = settings.StartWithWindows,
            ["startMinimizedToTray"] = settings.StartMinimizedToTray,
            ["behavior"] = settings.LaunchBehavior switch
            {
                LaunchBehavior.RestorePreviousMode => "restorePreviousMode",
                LaunchBehavior.SwitchToMode => "switchToMode",
                _ => "doNothing",
            },
            ["target"] = settings.StartupTarget,
        },
        ["setup"] = new JsonObject { ["completed"] = settings.SetupCompleted },
    };

    /// <summary>Persists only the setup-wizard completion flag, leaving every other setting untouched.</summary>
    public SettingsSaveOutcome MarkSetupCompleted(TargetCatalog targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        ApplicationSettings current = Load(targets);
        return current.SetupCompleted ? SettingsSaveOutcome.Ok : Save(current with { SetupCompleted = true }, targets);
    }

    /// <summary>
    /// What a favorite or startup points at.
    /// </summary>
    /// <remarks>
    /// 'target' is what this build writes. 'mode' is what the same field was
    /// called before the product had Spaces, and is still read: a configuration
    /// that has been migrated has both rewritten, and one hand-edited in between
    /// should not lose its wheel over the spelling.
    /// </remarks>
    private static string? ReadTarget(JsonObject holder)
    {
        if (ReadString(holder, "target")?.Trim() is { Length: > 0 } target)
        {
            return target;
        }

        return ReadString(holder, "mode")?.Trim() is { Length: > 0 } mode ? mode : null;
    }

    private string? DesiredAutoStartCommand(ApplicationSettings settings)
    {
        if (!settings.StartWithWindows)
        {
            return null;
        }

        return settings.StartMinimizedToTray ? _autoStartCommand + " --background" : _autoStartCommand;
    }

    private static JsonObject ParseObject(string json, string path)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: ReadOptions) as JsonObject
                ?? throw new ModeConfigurationException($"{path} is not a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new ModeConfigurationException($"The configuration is not valid JSON. {exception.Message}");
        }
    }

    private static JsonObject OptionalObject(JsonObject parent, string name) => Find(parent, name) switch
    {
        null => [],
        JsonObject value => value,
        _ => throw new ModeConfigurationException($"'application.{name}' must be an object."),
    };

    private static string? ReadString(JsonObject parent, string name)
    {
        JsonNode? node = Find(parent, name);
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            return text;
        }

        throw new ModeConfigurationException($"'{name}' must be text.");
    }

    private static bool? ReadBoolean(JsonObject parent, string name)
    {
        JsonNode? node = Find(parent, name);
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue(out bool result))
        {
            return result;
        }

        throw new ModeConfigurationException($"'{name}' must be true or false.");
    }

    private static JsonNode? Find(JsonObject parent, string name) =>
        parent.FirstOrDefault(property => string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void Remove(JsonObject parent, string name)
    {
        string? actual = parent.Select(property => property.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

        if (actual is not null)
        {
            parent.Remove(actual);
        }
    }
}

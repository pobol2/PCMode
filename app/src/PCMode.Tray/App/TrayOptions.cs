using System.Globalization;
using PCMode.Picker;
using PCMode.Tray.Elements.Settings;

namespace PCMode.Tray;

/// <summary>
/// What the command line asked for.
/// </summary>
/// <remarks>
/// NOT SETTINGS, AND THAT IS THE POINT
///
/// Nothing here is persisted. Persistent settings live in
/// <see cref="ApplicationSettings"/> with their own rules about versioning and
/// validation; a second small file holding four favorites would be a parallel
/// source of truth. These are runtime overrides: they last as long as the
/// process.
///
/// INPUT IS ON, AND --no-input IS A DIAGNOSTIC
///
/// The picker, the hotkeys and the controller are the normal behaviour of this
/// application. <see cref="NoInput"/> exists so it can be started without them
/// when they are the thing being investigated -- a hotkey another program has
/// taken, or a controller that is misbehaving -- not as a mode anybody is meant
/// to run in.
/// </remarks>
public sealed record TrayOptions
{
    /// <summary>Start the shell without initially showing its main window.</summary>
    public bool Background { get; init; }

    /// <summary>Start without global hotkeys, the controller or the picker.</summary>
    public bool NoInput { get; init; }

    /// <summary>Whether to poll for a controller.</summary>
    public bool PollController { get; init; } = true;

    /// <summary>Which display the picker opens on.</summary>
    public PickerMonitorChoice MonitorChoice { get; init; } = PickerMonitorChoice.Primary;

    /// <summary>Whether the monitor choice was explicitly present.</summary>
    public bool HasMonitorOverride { get; init; }

    /// <summary>The four compass favorites.</summary>
    public FavoriteSet Favorites { get; init; } = FavoriteSet.Default;

    /// <summary>The favorite edits, retained so they can be applied over persisted settings.</summary>
    public IReadOnlyList<string> FavoriteOverrides { get; init; } = [];

    /// <summary>
    /// Where the configuration and state live, when something other than the
    /// real profile folder is wanted.
    /// </summary>
    public string? DataRoot { get; init; }

    /// <summary>What could not be understood, if anything.</summary>
    public string? Error { get; init; }

    public bool IsValid => Error is null;

    /// <summary>
    /// Reads the command line.
    /// </summary>
    /// <remarks>
    /// A malformed argument produces an <see cref="Error"/> rather than an
    /// exception or a silent default. Silently ignoring "--favorite Up=Destkop"
    /// gives somebody a picker that looks right and applies the wrong mode.
    ///
    /// An argument this does not recognise is refused for the same reason.
    /// "--no-inupt" ignored is a shell that starts perfectly and then does not
    /// do the one thing it was asked to do, with nothing anywhere saying why;
    /// and an option that was removed is worse still, because the command line
    /// that used to work now quietly means something else.
    /// </remarks>
    public static TrayOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var options = new TrayOptions();
        var favoriteOverrides = new List<string>();
        Dictionary<PickerDirection, ModeFavorite> favorites =
            FavoriteSet.Default.All.ToDictionary(favorite => favorite.Direction);

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];

            switch (argument.ToLowerInvariant())
            {
                case "--background":
                    options = options with { Background = true };
                    break;

                case "--no-input":
                    options = options with { NoInput = true };
                    break;

                case "--no-controller":
                    options = options with { PollController = false };
                    break;

                case "--data-root":
                {
                    if (!Next(arguments, ref index, out string? root))
                    {
                        return Failed("--data-root needs a folder.");
                    }

                    options = options with { DataRoot = root };
                    break;
                }

                case "--picker-monitor":
                {
                    if (!Next(arguments, ref index, out string? value))
                    {
                        return Failed("--picker-monitor needs a value: primary, pointer or foreground.");
                    }

                    PickerMonitorChoice? choice = value.ToLowerInvariant() switch
                    {
                        "primary" => PickerMonitorChoice.Primary,
                        "pointer" => PickerMonitorChoice.UnderPointer,
                        "foreground" => PickerMonitorChoice.WithForegroundWindow,
                        _ => null,
                    };

                    if (choice is null)
                    {
                        return Failed($"'{value}' is not a monitor choice. Use primary, pointer or foreground.");
                    }

                    options = options with { MonitorChoice = choice.Value, HasMonitorOverride = true };
                    break;
                }

                case "--favorite":
                {
                    if (!Next(arguments, ref index, out string? value))
                    {
                        return Failed("--favorite needs a value, for example Up=Desktop.");
                    }

                    if (!TryParseFavorite(value, favorites, out ModeFavorite? favorite, out string? problem))
                    {
                        return Failed(problem!);
                    }

                    favorites[favorite!.Direction] = favorite;
                    favoriteOverrides.Add(value);
                    break;
                }

                default:
                    return Failed(
                        $"'{argument}' is not an option PCMode understands. Valid options are: " +
                        "--background, --no-input, --no-controller, --data-root, --picker-monitor, --favorite.");
            }
        }

        try
        {
            return options with
            {
                Favorites = FavoriteSet.Create(favorites.Values),
                FavoriteOverrides = favoriteOverrides,
            };
        }
        catch (ArgumentException exception)
        {
            return Failed(exception.Message);
        }

        static TrayOptions Failed(string message) => new() { Error = message };
    }

    /// <summary>
    /// Resolves temporary command-line changes over persisted application
    /// settings. The returned value is never written by this class.
    /// </summary>
    public ApplicationSettings? Resolve(ApplicationSettings persisted, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(persisted);

        Dictionary<PickerDirection, ModeFavorite> favorites =
            persisted.Favorites.All.ToDictionary(favorite => favorite.Direction);

        foreach (string text in FavoriteOverrides)
        {
            if (!TryParseFavorite(text, favorites, out ModeFavorite? favorite, out problem))
            {
                return null;
            }

            favorites[favorite!.Direction] = favorite;
        }

        try
        {
            FavoriteSet resolvedFavorites = FavoriteSet.Create(favorites.Values);
            List<DirectShortcut> shortcuts = [.. persisted.DirectShortcuts];

            foreach (string text in FavoriteOverrides)
            {
                string[] halves = text.Split('=', 2);
                string[] parts = halves.Length == 2 ? halves[1].Split(':') : [];

                if (parts.Length <= 3 ||
                    !Enum.TryParse(halves[0].Trim(), ignoreCase: true, out PickerDirection direction))
                {
                    continue;
                }

                ModeFavorite favorite = resolvedFavorites[direction];
                string hotkey = parts[3].Trim();

                if (hotkey.Length == 0)
                {
                    shortcuts.RemoveAll(shortcut =>
                        string.Equals(shortcut.Target, favorite.ModeName, StringComparison.OrdinalIgnoreCase));
                    continue;
                }

                char key = char.ToLowerInvariant(hotkey[0]);
                shortcuts.RemoveAll(shortcut => char.ToLowerInvariant(shortcut.Key) == key);
                shortcuts.Add(new DirectShortcut(favorite.ModeName, key, favorite.Label));
            }

            problem = null;

            return persisted with
            {
                PickerMonitor = HasMonitorOverride ? MonitorChoice : persisted.PickerMonitor,
                Favorites = resolvedFavorites,
                DirectShortcuts = shortcuts,
                ControllerEnabled = persisted.ControllerEnabled && PollController,
            };
        }
        catch (ArgumentException exception)
        {
            problem = exception.Message;
            return null;
        }
    }

    private static bool Next(IReadOnlyList<string> arguments, ref int index, out string value)
    {
        if (index + 1 >= arguments.Count)
        {
            value = string.Empty;
            return false;
        }

        value = arguments[++index];
        return !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Reads "Direction=Mode[:Icon[:RRGGBB[:key]]]".
    /// </summary>
    /// <remarks>
    /// Everything after the mode name is optional and keeps whatever the
    /// favorite already had, so moving a mode to a different compass point does
    /// not silently cost it its icon or its hotkey.
    /// </remarks>
    private static bool TryParseFavorite(
        string text,
        IReadOnlyDictionary<PickerDirection, ModeFavorite> current,
        out ModeFavorite? favorite,
        out string? problem)
    {
        favorite = null;
        problem = null;

        string[] halves = text.Split('=', 2);

        if (halves.Length != 2)
        {
            problem = $"'{text}' is not a favorite. Use Direction=Mode, for example Up=Desktop.";
            return false;
        }

        if (!Enum.TryParse(halves[0].Trim(), ignoreCase: true, out PickerDirection direction))
        {
            problem = $"'{halves[0]}' is not a direction. Use Up, Right, Down or Left.";
            return false;
        }

        string[] parts = halves[1].Split(':');
        string modeName = parts[0].Trim();

        if (string.IsNullOrWhiteSpace(modeName))
        {
            problem = $"The {direction} favorite has no mode name.";
            return false;
        }

        ModeFavorite existing = current[direction];

        FavoriteIcon icon = existing.Icon;
        PickerColor color = existing.Color;
        char? hotkey = existing.Hotkey;

        if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
        {
            if (!Enum.TryParse(parts[1].Trim(), ignoreCase: true, out icon))
            {
                problem =
                    $"'{parts[1]}' is not an icon. Use one of: " +
                    string.Join(", ", Enum.GetNames<FavoriteIcon>()) + ".";

                return false;
            }
        }

        if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]))
        {
            try
            {
                color = PickerColor.Parse(parts[2].Trim());
            }
            catch (FormatException exception)
            {
                problem = exception.Message;
                return false;
            }
        }

        if (parts.Length > 3)
        {
            string key = parts[3].Trim();

            if (key.Length == 0)
            {
                // Explicitly emptied, meaning this favorite wants no hotkey.
                hotkey = null;
            }
            else if (key.Length == 1 && char.IsAsciiLetter(key[0]))
            {
                hotkey = key[0];
            }
            else
            {
                problem = $"'{key}' is not a hotkey letter. Use a single letter A to Z, or nothing for none.";
                return false;
            }
        }

        favorite = existing with
        {
            Direction = direction,
            ModeName = modeName,

            // The label follows the mode name unless it was already something
            // different, so the common case needs no second argument.
            Label = !existing.IsAssigned ||
                    string.Equals(existing.Label, existing.ModeName, StringComparison.Ordinal)
                ? modeName
                : existing.Label,

            Icon = icon,
            Color = color,
            Hotkey = hotkey,
            Id = modeName.ToLower(CultureInfo.InvariantCulture),
        };

        return true;
    }
}

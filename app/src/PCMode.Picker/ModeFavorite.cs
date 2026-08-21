using System.Globalization;

namespace PCMode.Picker;

/// <summary>The four compass points, and the only positions the picker has.</summary>
/// <remarks>
/// The picker is deliberately four wedges rather than a list of every
/// configured mode. It is a compass: one arrow key, one D-pad direction or one
/// stick deflection reaches any of them with no hover-and-scan, and that
/// property is worth more than completeness. Every configured mode stays
/// reachable from the tray menu and the window.
/// </remarks>
public enum PickerDirection
{
    Up,
    Right,
    Down,
    Left,
}

/// <summary>Which drawing a favorite uses.</summary>
/// <remarks>
/// A KIND, not a mode name. Choosing the drawing by display name would mean a
/// rename silently costs a wedge its icon; naming the drawing separately lets a
/// favorite be called anything and still say which picture it wants.
/// </remarks>
public enum FavoriteIcon
{
    /// <summary>Nothing is drawn. Legitimate for a favorite with no icon.</summary>
    None,

    /// <summary>A monitor on a stand.</summary>
    Monitor,

    /// <summary>A rounded tile with a play triangle.</summary>
    PlayTile,

    /// <summary>A gamepad.</summary>
    Gamepad,

    /// <summary>A strip of film.</summary>
    FilmStrip,
}

/// <summary>A colour, with no presentation type behind it.</summary>
public readonly record struct PickerColor(byte Red, byte Green, byte Blue)
{
    /// <summary>Reads "RRGGBB", with or without a leading #.</summary>
    public static PickerColor Parse(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);

        ReadOnlySpan<char> digits = hex.AsSpan().Trim();

        if (digits.Length > 0 && digits[0] == '#')
        {
            digits = digits[1..];
        }

        if (digits.Length != 6 ||
            !byte.TryParse(digits[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte red) ||
            !byte.TryParse(digits[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte green) ||
            !byte.TryParse(digits[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte blue))
        {
            throw new FormatException($"'{hex}' is not a six-digit RRGGBB colour.");
        }

        return new PickerColor(red, green, blue);
    }

    public override string ToString() => $"{Red:X2}{Green:X2}{Blue:X2}";
}

/// <summary>A point, in whatever space the caller is working in.</summary>
public readonly record struct PickerPoint(double X, double Y);

/// <summary>
/// One wedge of the picker.
/// </summary>
/// <remarks>
/// Every axis is its own property on purpose. Tying the icon, the hotkey and
/// the on-screen text to one display name makes renaming a mode a multi-file
/// edit that fails silently when it is done incompletely.
///
/// <see cref="ModeName"/> is what PCMode is asked to apply -- the target's
/// stable reference, which survives it being renamed -- and <see cref="Label"/>
/// is what a person reads. They are frequently different, and nothing here
/// assumes otherwise.
/// </remarks>
public sealed record ModeFavorite
{
    /// <summary>Stable identity, independent of name, position or colour.</summary>
    public required string Id { get; init; }

    /// <summary>Which compass point this favorite occupies.</summary>
    public required PickerDirection Direction { get; init; }

    /// <summary>
    /// The reference sent to PCMode -- a target's key. Never a label.
    /// </summary>
    /// <remarks>
    /// The picker knows nothing about what a target is; it only carries the
    /// string back to whatever applies it. The four shipped defaults name the
    /// four traditional wheel positions, which the tray matches against real
    /// targets when a configuration says nothing about the wheel.
    /// </remarks>
    public required string ModeName { get; init; }

    /// <summary>Whether this picker position currently launches a target.</summary>
    public bool IsAssigned => !string.IsNullOrWhiteSpace(ModeName);

    /// <summary>What is drawn under the icon.</summary>
    public required string Label { get; init; }

    /// <summary>The wedge colour. States are told apart by opacity, not hue.</summary>
    public required PickerColor Color { get; init; }

    public FavoriteIcon Icon { get; init; } = FavoriteIcon.None;

    /// <summary>
    /// The letter for this favorite's direct Ctrl+Alt hotkey, or null for none.
    /// </summary>
    public char? Hotkey { get; init; }

    /// <summary>Cosmetic nudge for the icon, as a fraction of wheel diameter.</summary>
    public PickerPoint IconOffset { get; init; }

    /// <summary>Cosmetic nudge for the label, relative to its place under the icon.</summary>
    public PickerPoint LabelOffset { get; init; }
}

/// <summary>
/// Exactly four favorites, one per compass point.
/// </summary>
/// <remarks>
/// Validated on construction rather than checked at each use. A set with two
/// favorites pointing Up has no sensible rendering and no sensible hit test,
/// and the useful moment to say so is when somebody's command line asks for
/// it -- not several frames into an animation.
/// </remarks>
public sealed class FavoriteSet
{
    private readonly Dictionary<PickerDirection, ModeFavorite> _byDirection;

    private FavoriteSet(IReadOnlyList<ModeFavorite> favorites)
    {
        All = favorites;
        _byDirection = favorites.ToDictionary(favorite => favorite.Direction);
    }

    /// <summary>In compass order: Up, Right, Down, Left.</summary>
    public IReadOnlyList<ModeFavorite> All { get; }

    /// <summary>The four favorites shipped out of the box.</summary>
    public static FavoriteSet Default { get; } = Create(
    [
        new ModeFavorite
        {
            Id = "desktop",
            Direction = PickerDirection.Up,
            ModeName = "Desktop",
            Label = "Desktop",
            Color = PickerColor.Parse("0078D4"),
            Icon = FavoriteIcon.Monitor,
            Hotkey = 'd',
            IconOffset = new PickerPoint(0.000, -0.020),
        },
        new ModeFavorite
        {
            Id = "youtube",
            Direction = PickerDirection.Right,
            ModeName = "YouTube",
            Label = "YouTube",
            Color = PickerColor.Parse("FF0033"),
            Icon = FavoriteIcon.PlayTile,
            Hotkey = 'y',
            IconOffset = new PickerPoint(0.000, -0.030),
        },
        new ModeFavorite
        {
            Id = "gaming",
            Direction = PickerDirection.Down,
            ModeName = "Gaming",
            Label = "Gaming",
            Color = PickerColor.Parse("171A21"),
            Icon = FavoriteIcon.Gamepad,
            Hotkey = 'g',
        },
        new ModeFavorite
        {
            Id = "movie",
            Direction = PickerDirection.Left,
            ModeName = "Movie",
            Label = "Movie",
            Color = PickerColor.Parse("7B5BF5"),
            Icon = FavoriteIcon.FilmStrip,
            Hotkey = 'm',
            IconOffset = new PickerPoint(0.000, -0.030),
        },
    ]);

    public ModeFavorite this[PickerDirection direction] => _byDirection[direction];

    /// <summary>Builds a validated set, or explains why it is not one.</summary>
    /// <exception cref="ArgumentException">
    /// A direction is missing or claimed twice, or an identity or hotkey is
    /// duplicated. A position may deliberately be unassigned.
    /// </exception>
    public static FavoriteSet Create(IEnumerable<ModeFavorite> favorites)
    {
        ArgumentNullException.ThrowIfNull(favorites);

        ModeFavorite[] all = [.. favorites];

        foreach (PickerDirection direction in Enum.GetValues<PickerDirection>())
        {
            int count = all.Count(favorite => favorite.Direction == direction);

            if (count != 1)
            {
                throw new ArgumentException(
                    $"The picker needs exactly one favorite per direction; {direction} has {count}.",
                    nameof(favorites));
            }
        }

        Duplicate(all.Select(favorite => favorite.Id), StringComparer.OrdinalIgnoreCase, "identity");

        Duplicate(
            all.Where(favorite => favorite.Hotkey is not null)
               .Select(favorite => favorite.Hotkey!.Value.ToString()),
            StringComparer.OrdinalIgnoreCase,
            "hotkey");

        // Compass order, so All can be enumerated for rendering and for a
        // stable command line without every caller sorting it again.
        ModeFavorite[] ordered =
        [
            .. all.OrderBy(favorite => (int)favorite.Direction)
        ];

        return new FavoriteSet(ordered);

        static void Duplicate(IEnumerable<string> values, StringComparer comparer, string what)
        {
            string[] repeated =
            [
                .. values.GroupBy(value => value, comparer)
                         .Where(group => group.Count() > 1)
                         .Select(group => group.Key)
            ];

            if (repeated.Length > 0)
            {
                throw new ArgumentException(
                    $"Two favorites share the same {what}: {string.Join(", ", repeated)}.",
                    nameof(favorites));
            }
        }
    }

    /// <summary>
    /// The favorite for a mode name, or null when that mode is not on the
    /// wheel.
    /// </summary>
    /// <remarks>Case-insensitive, matching how the engine treats mode names.</remarks>
    public ModeFavorite? ByModeName(string? modeName) =>
        string.IsNullOrWhiteSpace(modeName)
            ? null
            : All.FirstOrDefault(favorite =>
                string.Equals(favorite.ModeName, modeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The favorite a direct hotkey letter belongs to, if any.</summary>
    public ModeFavorite? ByHotkey(char letter) =>
        All.FirstOrDefault(favorite =>
            favorite.Hotkey is char key && char.ToUpperInvariant(key) == char.ToUpperInvariant(letter));
}

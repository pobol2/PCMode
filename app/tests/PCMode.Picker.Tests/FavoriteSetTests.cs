namespace PCMode.Picker.Tests;

/// <summary>
/// What a set of favorites is allowed to be.
/// </summary>
/// <remarks>
/// Validation matters here because favorites are injectable. A malformed set
/// arriving from a command line has to be refused where somebody can still read
/// the reason, not several frames into an animation where the only symptom is a
/// wedge that does not draw.
/// </remarks>
public sealed class FavoriteSetTests
{
    private static ModeFavorite Favorite(PickerDirection direction, string name) => new()
    {
        Id = name.ToLowerInvariant(),
        Direction = direction,
        ModeName = name,
        Label = name,
        Color = PickerColor.Parse("112233"),
    };

    private static ModeFavorite[] Four() =>
    [
        Favorite(PickerDirection.Up, "One"),
        Favorite(PickerDirection.Right, "Two"),
        Favorite(PickerDirection.Down, "Three"),
        Favorite(PickerDirection.Left, "Four"),
    ];

    [Fact]
    public void Ships_four_favorites_in_compass_order()
    {
        // Deliberately identical. Somebody switching the native shell on
        // should meet the picker they already know; a redesign landing at the
        // same moment as a new implementation makes it impossible to tell
        // which of the two broke something.
        FavoriteSet defaults = FavoriteSet.Default;

        Assert.Equal(
            ["Desktop", "YouTube", "Gaming", "Movie"],
            defaults.All.Select(favorite => favorite.ModeName));

        Assert.Equal(PickerDirection.Up, defaults.ByModeName("Desktop")!.Direction);
        Assert.Equal(PickerDirection.Right, defaults.ByModeName("YouTube")!.Direction);
        Assert.Equal(PickerDirection.Down, defaults.ByModeName("Gaming")!.Direction);
        Assert.Equal(PickerDirection.Left, defaults.ByModeName("Movie")!.Direction);
    }

    [Fact]
    public void Keeps_the_direct_hotkey_letters_the_users_fingers_know()
    {
        FavoriteSet defaults = FavoriteSet.Default;

        Assert.Equal("Desktop", defaults.ByHotkey('d')!.ModeName);
        Assert.Equal("YouTube", defaults.ByHotkey('y')!.ModeName);
        Assert.Equal("Gaming", defaults.ByHotkey('g')!.ModeName);
        Assert.Equal("Movie", defaults.ByHotkey('m')!.ModeName);
    }

    [Fact]
    public void Orders_the_favorites_by_compass_point_however_they_arrive()
    {
        FavoriteSet set = FavoriteSet.Create(
        [
            Favorite(PickerDirection.Left, "Four"),
            Favorite(PickerDirection.Up, "One"),
            Favorite(PickerDirection.Down, "Three"),
            Favorite(PickerDirection.Right, "Two"),
        ]);

        Assert.Equal(
            [PickerDirection.Up, PickerDirection.Right, PickerDirection.Down, PickerDirection.Left],
            set.All.Select(favorite => favorite.Direction));
    }

    [Fact]
    public void Refuses_a_direction_claimed_twice()
    {
        ModeFavorite[] clashing =
        [
            Favorite(PickerDirection.Up, "One"),
            Favorite(PickerDirection.Up, "Two"),
            Favorite(PickerDirection.Down, "Three"),
            Favorite(PickerDirection.Left, "Four"),
        ];

        ArgumentException failure = Assert.Throws<ArgumentException>(() => FavoriteSet.Create(clashing));

        Assert.Contains("Up", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_direction_nobody_claimed()
    {
        ModeFavorite[] incomplete =
        [
            Favorite(PickerDirection.Up, "One"),
            Favorite(PickerDirection.Right, "Two"),
            Favorite(PickerDirection.Down, "Three"),
        ];

        Assert.Throws<ArgumentException>(() => FavoriteSet.Create(incomplete));
    }

    [Fact]
    public void Refuses_two_favorites_sharing_a_hotkey()
    {
        // One of the two would silently never fire, and which one depends on
        // registration order.
        ModeFavorite[] clashing =
        [
            .. Four().Select(favorite => favorite.Direction is PickerDirection.Up or PickerDirection.Down
                ? favorite with { Hotkey = 'x' }
                : favorite)
        ];

        ArgumentException failure = Assert.Throws<ArgumentException>(() => FavoriteSet.Create(clashing));

        Assert.Contains("hotkey", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refuses_two_favorites_sharing_an_identity()
    {
        ModeFavorite[] clashing =
        [
            .. Four().Select(favorite => favorite with { Id = "same" })
        ];

        Assert.Throws<ArgumentException>(() => FavoriteSet.Create(clashing));
    }

    [Fact]
    public void Allows_a_picker_position_to_be_unassigned()
    {
        ModeFavorite[] empty =
        [
            .. Four().Select(favorite => favorite.Direction == PickerDirection.Up
                ? favorite with { ModeName = "  " }
                : favorite)
        ];

        FavoriteSet set = FavoriteSet.Create(empty);

        Assert.False(set[PickerDirection.Up].IsAssigned);
        Assert.Null(set.ByModeName(""));
    }

    [Fact]
    public void Finds_a_mode_whatever_case_it_was_written_in()
    {
        // The engine treats mode names case-insensitively and so did the
        // original picker's lookup. A hub that says "Unknown" because the
        // state file says "desktop" would be a lie about the machine.
        Assert.NotNull(FavoriteSet.Default.ByModeName("DESKTOP"));
        Assert.NotNull(FavoriteSet.Default.ByModeName("desktop"));
    }

    [Fact]
    public void Finds_nothing_for_a_mode_that_is_not_on_the_wheel()
    {
        Assert.Null(FavoriteSet.Default.ByModeName("Presentation"));
        Assert.Null(FavoriteSet.Default.ByModeName(null));
    }

    [Fact]
    public void Lets_a_label_differ_from_the_mode_it_applies()
    {
        // The coupling this whole type exists to break.
        ModeFavorite favorite = FavoriteSet.Default.All[0] with { Label = "Work" };

        Assert.Equal("Work", favorite.Label);
        Assert.Equal("Desktop", favorite.ModeName);
    }

    [Theory]
    [InlineData("0078D4", 0x00, 0x78, 0xD4)]
    [InlineData("#FF0033", 0xFF, 0x00, 0x33)]
    [InlineData("ff0033", 0xFF, 0x00, 0x33)]
    public void Reads_a_colour_the_way_the_original_wrote_them(string hex, byte red, byte green, byte blue)
    {
        Assert.Equal(new PickerColor(red, green, blue), PickerColor.Parse(hex));
    }

    [Theory]
    [InlineData("00")]
    [InlineData("nothex")]
    [InlineData("0078D41")]
    public void Refuses_something_that_is_not_a_colour(string hex)
    {
        Assert.Throws<FormatException>(() => PickerColor.Parse(hex));
    }
}

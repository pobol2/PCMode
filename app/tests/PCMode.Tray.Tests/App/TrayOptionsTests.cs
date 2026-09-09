using PCMode.Picker;
using PCMode.Tray;
using PCMode.Tray.Elements.Settings;
using PCMode.Tray.Platform.Picker;

namespace PCMode.Tray.Tests.App;

/// <summary>
/// What the command line is allowed to change, and what it must refuse.
/// Ported from legacy PCMode.Tray's own <c>TrayOptionsTests</c>; the two
/// removed-switch cases (<c>--script</c>, <c>--own-input</c>) are not ported
/// since Target never shipped those switches in the first place -- there is
/// no prior command line for it to explain the removal of.
/// </summary>
public sealed class TrayOptionsTests
{
    private static TrayOptions Parse(params string[] arguments) => TrayOptions.Parse(arguments);

    [Fact]
    public void Starts_with_the_picker_and_its_hotkeys()
    {
        // The normal behaviour of the application, not an opt-in. A tray icon
        // whose wheel had to be asked for on the command line would be a
        // feature nobody finds.
        TrayOptions options = Parse();

        Assert.False(options.NoInput);
        Assert.True(options.IsValid);
    }

    [Fact]
    public void Starts_from_the_picker_the_user_already_has()
    {
        TrayOptions options = Parse();

        Assert.Equal(
            ["Desktop", "YouTube", "Gaming", "Movie"],
            options.Favorites.All.Select(favorite => favorite.ModeName)
        );

        Assert.Equal(PickerMonitorChoice.Primary, options.MonitorChoice);
        Assert.True(options.PollController);
    }

    [Fact]
    public void Starts_without_any_global_input_when_that_is_being_investigated()
    {
        // A diagnostic. It exists so the shell can be started when a hotkey or
        // a controller is the thing going wrong, not as a mode to run in.
        Assert.True(Parse("--no-input").NoInput);
    }

    [Fact]
    public void Background_starts_without_requesting_an_initial_window()
    {
        TrayOptions options = Parse("--background");

        Assert.True(options.IsValid);
        Assert.True(options.Background);
    }

    [Fact]
    public void Takes_a_data_folder_of_its_own()
    {
        const string elsewhere = "C:/elsewhere";

        Assert.Equal(elsewhere, Parse("--data-root", elsewhere).DataRoot);
        Assert.False(Parse("--data-root").IsValid);
    }

    [Fact]
    public void Turns_controller_polling_off_on_request()
    {
        // Keyboard and mouse are unaffected either way, which is why this is a
        // separate switch from --no-input.
        TrayOptions options = Parse("--no-controller");

        Assert.False(options.NoInput);
        Assert.False(options.PollController);
    }

    [Theory]
    [InlineData("primary", PickerMonitorChoice.Primary)]
    [InlineData("pointer", PickerMonitorChoice.UnderPointer)]
    [InlineData("foreground", PickerMonitorChoice.WithForegroundWindow)]
    public void Chooses_the_monitor_the_picker_opens_on(string value, PickerMonitorChoice expected)
    {
        Assert.Equal(expected, Parse("--picker-monitor", value).MonitorChoice);
    }

    [Fact]
    public void Refuses_a_monitor_choice_it_does_not_understand()
    {
        TrayOptions options = Parse("--picker-monitor", "leftmost");

        Assert.False(options.IsValid);
        Assert.Contains("leftmost", options.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Points_a_favorite_at_a_different_mode()
    {
        TrayOptions options = Parse("--favorite", "Up=Work");

        Assert.True(options.IsValid);
        Assert.Equal("Work", options.Favorites[PickerDirection.Up].ModeName);

        // The other three are untouched.
        Assert.Equal("Gaming", options.Favorites[PickerDirection.Down].ModeName);
    }

    [Fact]
    public void Keeps_a_favorites_icon_and_hotkey_when_only_its_mode_changed()
    {
        // Otherwise repointing a wedge silently costs it its picture and its
        // key, and the user has to discover that by pressing one.
        ModeFavorite before = FavoriteSet.Default[PickerDirection.Up];
        ModeFavorite after = Parse("--favorite", "Up=Work").Favorites[PickerDirection.Up];

        Assert.Equal(before.Icon, after.Icon);
        Assert.Equal(before.Hotkey, after.Hotkey);
        Assert.Equal(before.Color, after.Color);
    }

    [Fact]
    public void Sets_an_icon_a_colour_and_a_hotkey_when_they_are_given()
    {
        TrayOptions options = Parse("--favorite", "Right=Work:Gamepad:00FF00:w");

        ModeFavorite favorite = options.Favorites[PickerDirection.Right];

        Assert.Equal("Work", favorite.ModeName);
        Assert.Equal(FavoriteIcon.Gamepad, favorite.Icon);
        Assert.Equal(PickerColor.Parse("00FF00"), favorite.Color);
        Assert.Equal('w', favorite.Hotkey);
    }

    [Fact]
    public void Lets_a_favorite_give_up_its_hotkey()
    {
        TrayOptions options = Parse("--favorite", "Up=Work:Monitor:0078D4:");

        Assert.Null(options.Favorites[PickerDirection.Up].Hotkey);
    }

    [Fact]
    public void Refuses_a_favorite_that_names_no_direction()
    {
        TrayOptions options = Parse("--favorite", "Desktop");

        Assert.False(options.IsValid);
    }

    [Fact]
    public void Refuses_a_direction_that_is_not_one()
    {
        TrayOptions options = Parse("--favorite", "Northwest=Desktop");

        Assert.False(options.IsValid);
        Assert.Contains("Northwest", options.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_an_icon_that_does_not_exist()
    {
        TrayOptions options = Parse("--favorite", "Up=Work:Teapot");

        Assert.False(options.IsValid);

        // Naming the alternatives, because the answer is a short closed list
        // and making somebody find it in source is unkind.
        Assert.Contains("Gamepad", options.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_colour_that_is_not_one()
    {
        Assert.False(Parse("--favorite", "Up=Work:Monitor:zzzzzz").IsValid);
    }

    [Fact]
    public void Refuses_a_hotkey_that_is_not_a_single_letter()
    {
        Assert.False(Parse("--favorite", "Up=Work:Monitor:0078D4:ctrl").IsValid);
    }

    [Fact]
    public void Refuses_two_favorites_that_would_share_a_hotkey()
    {
        // Caught by FavoriteSet, and it has to be caught somewhere: one of the
        // two registrations would silently never fire.
        TrayOptions options = Parse("--favorite", "Up=Work:Monitor:0078D4:g");

        Assert.False(options.IsValid);
        Assert.Contains("hotkey", options.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refuses_a_switch_with_its_value_missing()
    {
        Assert.False(Parse("--favorite").IsValid);
        Assert.False(Parse("--picker-monitor").IsValid);
    }

    [Fact]
    public void Refuses_an_argument_it_does_not_recognise()
    {
        // Ignoring it is the failure mode worth avoiding: a misspelled switch
        // starts a shell that looks completely healthy and does not do the one
        // thing it was asked to, with nothing anywhere explaining it.
        TrayOptions options = Parse("--no-inupt");

        Assert.False(options.IsValid);
        Assert.Contains("--no-inupt", options.Error!, StringComparison.Ordinal);

        // Naming the alternatives, so the answer is on screen rather than in
        // the source.
        Assert.Contains("--no-input", options.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_a_value_without_mistaking_it_for_a_switch()
    {
        // The rejection above must not fire on the value half of a pair. A
        // folder or a mode name is consumed by the switch that asked for it.
        Assert.True(Parse("--data-root", "C:/elsewhere", "--favorite", "Up=Work").IsValid);
    }

    [Fact]
    public void Resolve_applies_a_monitor_override_over_persisted_settings()
    {
        var persisted = new ApplicationSettings { PickerMonitor = PickerMonitorChoice.Primary };
        TrayOptions options = Parse("--picker-monitor", "pointer");

        ApplicationSettings? resolved = options.Resolve(persisted, out string? problem);

        Assert.Null(problem);
        Assert.NotNull(resolved);
        Assert.Equal(PickerMonitorChoice.UnderPointer, resolved!.PickerMonitor);
    }

    [Fact]
    public void Resolve_leaves_persisted_settings_untouched_with_no_overrides()
    {
        var persisted = new ApplicationSettings { PickerMonitor = PickerMonitorChoice.WithForegroundWindow };
        TrayOptions options = Parse();

        ApplicationSettings? resolved = options.Resolve(persisted, out string? problem);

        Assert.Null(problem);
        Assert.Equal(PickerMonitorChoice.WithForegroundWindow, resolved!.PickerMonitor);
        Assert.Equal(persisted.Favorites.All, resolved.Favorites.All);
    }

    [Fact]
    public void Resolve_disables_the_controller_when_either_side_disables_it()
    {
        var persisted = new ApplicationSettings { ControllerEnabled = true };
        TrayOptions options = Parse("--no-controller");

        ApplicationSettings? resolved = options.Resolve(persisted, out _);

        Assert.False(resolved!.ControllerEnabled);
    }
}

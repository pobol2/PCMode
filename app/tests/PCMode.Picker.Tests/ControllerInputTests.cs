namespace PCMode.Picker.Tests;

/// <summary>
/// What a controller poll means, without a controller.
/// </summary>
/// <remarks>
/// XInput has no events, only polls, so every rule about repeats and holds is
/// something this code has to impose -- and can therefore be checked here
/// rather than by holding a button and watching.
/// </remarks>
public sealed class ControllerInputTests
{
    private static GamepadState Pressing(GamepadButtons buttons) => new(buttons, 0, 0);

    private static GamepadState Nothing => new(GamepadButtons.None, 0, 0);

    // ---- buttons are edge triggered ----------------------------------------

    [Fact]
    public void Opens_the_picker_on_start()
    {
        var translator = new ControllerInputTranslator();

        IReadOnlyList<ControllerCommand> commands =
            translator.Translate(Pressing(GamepadButtons.Start), pickerOpen: false);

        Assert.Equal([ControllerCommand.Open], commands);
    }

    [Fact]
    public void Does_nothing_more_while_a_button_stays_held()
    {
        // At thirty polls a second, a held A button would apply the selected
        // mode thirty times over for one comfortable press.
        var translator = new ControllerInputTranslator();

        translator.Translate(Pressing(GamepadButtons.A), pickerOpen: true);

        Assert.Empty(translator.Translate(Pressing(GamepadButtons.A), pickerOpen: true));
        Assert.Empty(translator.Translate(Pressing(GamepadButtons.A), pickerOpen: true));
    }

    [Fact]
    public void Acts_again_once_a_button_has_been_released_and_pressed()
    {
        var translator = new ControllerInputTranslator();

        translator.Translate(Pressing(GamepadButtons.A), pickerOpen: true);
        translator.Translate(Nothing, pickerOpen: true);

        Assert.Equal([ControllerCommand.Activate], translator.Translate(Pressing(GamepadButtons.A), pickerOpen: true));
    }

    [Fact]
    public void Ignores_everything_but_start_while_the_picker_is_closed()
    {
        // A gamepad has to stay a gamepad while somebody is playing. Claiming
        // A or the D-pad system-wide would make PCMode unusable alongside the
        // games it exists to set the machine up for.
        var translator = new ControllerInputTranslator();

        Assert.Empty(translator.Translate(Pressing(GamepadButtons.A), pickerOpen: false));
        translator.Translate(Nothing, pickerOpen: false);

        Assert.Empty(translator.Translate(Pressing(GamepadButtons.DPadUp), pickerOpen: false));
        translator.Translate(Nothing, pickerOpen: false);

        Assert.Empty(translator.Translate(new GamepadState(GamepadButtons.None, 32000, 0), pickerOpen: false));
    }

    [Theory]
    [InlineData(GamepadButtons.B)]
    [InlineData(GamepadButtons.Start)]
    public void Closes_the_picker_without_applying_anything(GamepadButtons button)
    {
        var translator = new ControllerInputTranslator();

        Assert.Equal([ControllerCommand.Close], translator.Translate(Pressing(button), pickerOpen: true));
    }

    [Fact]
    public void Applies_the_selection_on_a()
    {
        var translator = new ControllerInputTranslator();

        Assert.Equal(
            [ControllerCommand.Activate],
            translator.Translate(Pressing(GamepadButtons.A), pickerOpen: true));
    }

    [Theory]
    [InlineData(GamepadButtons.DPadUp, PickerDirection.Up)]
    [InlineData(GamepadButtons.DPadRight, PickerDirection.Right)]
    [InlineData(GamepadButtons.DPadDown, PickerDirection.Down)]
    [InlineData(GamepadButtons.DPadLeft, PickerDirection.Left)]
    public void Moves_the_selection_with_the_d_pad(GamepadButtons button, PickerDirection expected)
    {
        var translator = new ControllerInputTranslator();

        Assert.Equal(
            [ControllerCommand.Select(expected)],
            translator.Translate(Pressing(button), pickerOpen: true));
    }

    [Fact]
    public void Forgets_held_buttons_when_the_controller_goes_away()
    {
        // Unplugged mid-press. Without the reset the button stays "already
        // down" for ever, and pressing it after reconnecting registers no edge
        // at all -- a controller that silently stops working.
        var translator = new ControllerInputTranslator();

        translator.Translate(Pressing(GamepadButtons.A), pickerOpen: true);

        Assert.Empty(translator.Translate(state: null, pickerOpen: true));

        Assert.Equal(
            [ControllerCommand.Activate],
            translator.Translate(Pressing(GamepadButtons.A), pickerOpen: true));
    }

    // ---- the stick is different on purpose ---------------------------------

    [Theory]
    [InlineData(30000, 0, PickerDirection.Right)]
    [InlineData(-30000, 0, PickerDirection.Left)]
    [InlineData(0, 30000, PickerDirection.Up)]
    [InlineData(0, -30000, PickerDirection.Down)]
    public void Reads_the_stick_as_a_compass_point(short x, short y, PickerDirection expected)
    {
        // XInput reports Y positive-up, the opposite of every screen
        // coordinate in the picker. Getting this backwards gives a wheel where
        // pushing up selects the wedge at the bottom.
        Assert.Equal(expected, ControllerInputTranslator.StickDirection(x, y));
    }

    [Fact]
    public void Ignores_a_stick_resting_near_the_centre()
    {
        // Worn sticks do not return to exactly zero. Without a dead zone the
        // picker would sit there re-selecting whichever way the stick droops.
        Assert.Null(ControllerInputTranslator.StickDirection(3000, 2000));
    }

    [Fact]
    public void Uses_a_round_dead_zone_rather_than_a_square_one()
    {
        // Both axes are inside the threshold on their own, but together they
        // are a deliberate diagonal push well past it.
        Assert.NotNull(ControllerInputTranslator.StickDirection(7000, 7000));
    }

    [Fact]
    public void Does_not_overflow_at_full_deflection()
    {
        // 32767 squared does not fit in a 32-bit signed integer, and the
        // wrapped value is negative -- which compares as inside any dead zone.
        // Full deflection is the one input a stick is guaranteed to produce.
        Assert.Equal(PickerDirection.Right, ControllerInputTranslator.StickDirection(32767, 0));
        Assert.Equal(PickerDirection.Down, ControllerInputTranslator.StickDirection(0, -32768));
    }

    [Fact]
    public void Selects_once_when_the_stick_is_pushed_and_held()
    {
        // The stick has no press and release to work from. Firing on every
        // poll while it is held would let a stick somebody is merely resting
        // their thumb on override every other input, thirty times a second.
        var translator = new ControllerInputTranslator();
        var pushed = new GamepadState(GamepadButtons.None, 30000, 0);

        Assert.Equal([ControllerCommand.Select(PickerDirection.Right)], translator.Translate(pushed, pickerOpen: true));
        Assert.Empty(translator.Translate(pushed, pickerOpen: true));
        Assert.Empty(translator.Translate(pushed, pickerOpen: true));
    }

    [Fact]
    public void Selects_again_when_the_stick_is_moved_somewhere_else()
    {
        var translator = new ControllerInputTranslator();

        translator.Translate(new GamepadState(GamepadButtons.None, 30000, 0), pickerOpen: true);

        Assert.Equal(
            [ControllerCommand.Select(PickerDirection.Up)],
            translator.Translate(new GamepadState(GamepadButtons.None, 0, 30000), pickerOpen: true));
    }

    [Fact]
    public void Lets_the_d_pad_and_the_stick_both_be_heard_in_one_poll()
    {
        // They are separate controls and a poll can genuinely carry both.
        var translator = new ControllerInputTranslator();

        IReadOnlyList<ControllerCommand> commands =
            translator.Translate(new GamepadState(GamepadButtons.DPadUp, 30000, 0), pickerOpen: true);

        Assert.Equal(
            [ControllerCommand.Select(PickerDirection.Up), ControllerCommand.Select(PickerDirection.Right)],
            commands);
    }

    [Fact]
    public void Does_not_let_a_resting_stick_undo_a_d_pad_press()
    {
        // The case the change-only rule is for. The stick is still where it
        // was left from a moment ago; a level-triggered reading would fight
        // every D-pad press and every arrow key for as long as it stays there.
        var translator = new ControllerInputTranslator();
        var stickRight = new GamepadState(GamepadButtons.None, 30000, 0);

        translator.Translate(stickRight, pickerOpen: true);

        IReadOnlyList<ControllerCommand> commands =
            translator.Translate(stickRight with { Buttons = GamepadButtons.DPadDown }, pickerOpen: true);

        Assert.Equal([ControllerCommand.Select(PickerDirection.Down)], commands);
    }
}

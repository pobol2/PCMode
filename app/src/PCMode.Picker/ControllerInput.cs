namespace PCMode.Picker;

/// <summary>The gamepad buttons, with XInput's own bit values.</summary>
[Flags]
public enum GamepadButtons : ushort
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

/// <summary>One poll's worth of controller state.</summary>
/// <param name="LeftThumbY">Positive is up, as XInput reports it.</param>
public readonly record struct GamepadState(GamepadButtons Buttons, short LeftThumbX, short LeftThumbY);

/// <summary>What the picker should do about a controller input.</summary>
public enum ControllerAction
{
    /// <summary>Open the picker.</summary>
    Open,

    /// <summary>Close it without applying anything.</summary>
    Close,

    /// <summary>Apply whatever is selected.</summary>
    Activate,

    /// <summary>Move the selection to <see cref="ControllerCommand.Direction"/>.</summary>
    Select,
}

/// <summary>One thing to do, and where.</summary>
public readonly record struct ControllerCommand(ControllerAction Action, PickerDirection Direction = default)
{
    public static ControllerCommand Open => new(ControllerAction.Open);

    public static ControllerCommand Close => new(ControllerAction.Close);

    public static ControllerCommand Activate => new(ControllerAction.Activate);

    public static ControllerCommand Select(PickerDirection direction) => new(ControllerAction.Select, direction);
}

/// <summary>
/// Turns a stream of controller polls into picker commands.
/// </summary>
/// <remarks>
/// Separated from the polling itself so the semantics can be tested without a
/// controller, a timer, or XInput being present at all. Everything that is
/// hard to get right here is in this class; what is left in the adapter is a
/// P/Invoke and a timer.
///
/// THE TWO TRIGGERING RULES, AND WHY THEY DIFFER
///
/// Buttons are EDGE triggered: only what became pressed since the last poll
/// counts. At thirty polls a second, a level-triggered A button would apply the
/// selected mode thirty times for one comfortable press.
///
/// The stick is level triggered but only fires when its direction CHANGES. It
/// has no press and release to work from -- it is simply deflected or not --
/// and a stick still resting where it was left must not keep overriding a
/// D-pad press or an arrow key made a moment later. Re-selecting the direction
/// already selected is harmless in any case; the coordinator ignores it.
///
/// The scope is deliberately narrow. A controller opens, closes and drives the
/// same picker the keyboard and mouse do, and can do nothing else.
/// </remarks>
public sealed class ControllerInputTranslator
{
    /// <summary>Microsoft's documented default, out of a range of +/-32767.</summary>
    public const short DefaultDeadzone = 8000;

    private readonly short _deadzone;

    private GamepadButtons _previousButtons;
    private PickerDirection? _previousStick;

    public ControllerInputTranslator(short deadzone = DefaultDeadzone)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(deadzone);
        _deadzone = deadzone;
    }

    /// <summary>
    /// What one poll means.
    /// </summary>
    /// <param name="state">
    /// The controller's state, or null when nothing is connected -- which is
    /// the ordinary case on a machine with no gamepad, not an error.
    /// </param>
    /// <param name="pickerOpen">Whether the picker is currently up.</param>
    public IReadOnlyList<ControllerCommand> Translate(GamepadState? state, bool pickerOpen)
    {
        if (state is null)
        {
            // A controller unplugged mid-press must not leave a phantom button
            // held down, so that reconnecting and pressing it registers an edge.
            Reset();
            return [];
        }

        GamepadState current = state.Value;
        GamepadButtons pressed = current.Buttons & ~_previousButtons;
        _previousButtons = current.Buttons;

        if (!pickerOpen)
        {
            // Start is the only thing a controller can do while the picker is
            // closed. Nothing else on the pad is claimed, so a gamepad stays a
            // gamepad while somebody is playing.
            return pressed.HasFlag(GamepadButtons.Start) ? [ControllerCommand.Open] : [];
        }

        if (pressed.HasFlag(GamepadButtons.Start) || pressed.HasFlag(GamepadButtons.B))
        {
            return [ControllerCommand.Close];
        }

        if (pressed.HasFlag(GamepadButtons.A))
        {
            return [ControllerCommand.Activate];
        }

        List<ControllerCommand> commands = [];

        // First match wins, so a diagonal on the D-pad resolves to one wedge
        // rather than fighting between two.
        PickerDirection? pad = null;

        if (pressed.HasFlag(GamepadButtons.DPadUp))
        {
            pad = PickerDirection.Up;
        }
        else if (pressed.HasFlag(GamepadButtons.DPadDown))
        {
            pad = PickerDirection.Down;
        }
        else if (pressed.HasFlag(GamepadButtons.DPadLeft))
        {
            pad = PickerDirection.Left;
        }
        else if (pressed.HasFlag(GamepadButtons.DPadRight))
        {
            pad = PickerDirection.Right;
        }

        if (pad is not null)
        {
            commands.Add(ControllerCommand.Select(pad.Value));
        }

        PickerDirection? stick = StickDirection(current.LeftThumbX, current.LeftThumbY, _deadzone);

        if (stick is not null && stick != _previousStick)
        {
            commands.Add(ControllerCommand.Select(stick.Value));
        }

        _previousStick = stick;

        return commands;
    }

    /// <summary>
    /// Forgets which buttons were held and where the stick was.
    /// </summary>
    public void Reset()
    {
        _previousButtons = GamepadButtons.None;
        _previousStick = null;
    }

    /// <summary>
    /// Which wedge a stick deflection points at, or null if it is inside the
    /// dead zone.
    /// </summary>
    /// <remarks>
    /// The dead zone is radial rather than per-axis, so a stick resting
    /// slightly off centre does not register just because one axis crept past a
    /// threshold on its own.
    /// </remarks>
    public static PickerDirection? StickDirection(short x, short y, short deadzone = DefaultDeadzone)
    {
        // int, not short: 32767 squared overflows every 32-bit signed type on
        // the way to the comparison.
        long magnitudeSquared = ((long)x * x) + ((long)y * y);

        if (magnitudeSquared < (long)deadzone * deadzone)
        {
            return null;
        }

        // Same |x| versus |y| quadrant rule the hit test uses, so the stick and
        // the mouse divide the wheel along exactly the same diagonals.
        if (Math.Abs((int)x) > Math.Abs((int)y))
        {
            return x > 0 ? PickerDirection.Right : PickerDirection.Left;
        }

        // XInput reports Y positive-up, which is the compass convention here
        // and the opposite of every screen coordinate in this file.
        return y > 0 ? PickerDirection.Up : PickerDirection.Down;
    }
}

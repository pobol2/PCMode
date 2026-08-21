using System.Runtime.InteropServices;
using PCMode.Picker;

namespace PCMode.Tray.Input;

/// <summary>Reads one controller slot.</summary>
/// <remarks>
/// XInput is a polling API with no notifications of any kind, which is why
/// everything above this is a timer rather than an event. Behind an interface
/// so the slot-searching and command semantics can be tested with no gamepad,
/// no driver and no DLL present.
/// </remarks>
public interface IGamepadReader
{
    /// <summary>The slot's state, or null when nothing is plugged into it.</summary>
    GamepadState? Read(int slot);
}

/// <summary>
/// Finds a controller and turns its polls into picker commands.
/// </summary>
/// <remarks>
/// The searching rule is sticky: whichever slot answered last is asked first,
/// and the other three are only swept when it stops answering. Sweeping in
/// order every time would mean that plugging a second controller in -- or a
/// wireless one waking up in slot 0 -- silently moves control away from the
/// pad somebody is holding.
/// </remarks>
public sealed class ControllerPoller
{
    /// <summary>XInput has exactly four slots.</summary>
    public const int SlotCount = 4;

    private readonly IGamepadReader _reader;
    private readonly ControllerInputTranslator _translator;

    private int _activeSlot = -1;

    public ControllerPoller(IGamepadReader reader, ControllerInputTranslator? translator = null)
    {
        ArgumentNullException.ThrowIfNull(reader);

        _reader = reader;
        _translator = translator ?? new ControllerInputTranslator();
    }

    /// <summary>The slot in use, or -1 when nothing is connected.</summary>
    public int ActiveSlot => _activeSlot;

    /// <summary>One tick. Returns what the picker should do about it.</summary>
    public IReadOnlyList<ControllerCommand> Poll(bool pickerOpen) =>
        _translator.Translate(FindController(), pickerOpen);

    private GamepadState? FindController()
    {
        if (_activeSlot >= 0)
        {
            GamepadState? held = _reader.Read(_activeSlot);

            if (held is not null)
            {
                return held;
            }

            _activeSlot = -1;
        }

        for (int slot = 0; slot < SlotCount; slot++)
        {
            GamepadState? found = _reader.Read(slot);

            if (found is not null)
            {
                _activeSlot = slot;
                return found;
            }
        }

        return null;
    }
}

/// <summary>
/// The real XInput binding.
/// </summary>
/// <remarks>
/// Resolved by hand rather than with a DllImport attribute, because which
/// library exists depends on the machine: 1_4 ships with Windows 8 and later,
/// 1_3 comes with the DirectX runtime, and 9_1_0 is the compatibility shim
/// that is always present. A DllImport naming one of them turns a missing file
/// into an exception on first use, at which point the choice has already been
/// made; NativeLibrary lets the probe be a question instead.
///
/// A machine with no XInput at all is a supported configuration, not a fault.
/// It reads as "no controller connected", which is what it is.
/// </remarks>
public sealed class XInputReader : IGamepadReader, IDisposable
{
    private static readonly string[] Candidates = ["xinput1_4", "xinput1_3", "xinput9_1_0"];

    private readonly nint _library;
    private readonly XInputGetState? _getState;

    private bool _disposed;

    private XInputReader(nint library, XInputGetState? getState)
    {
        _library = library;
        _getState = getState;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint XInputGetState(uint userIndex, out XInputStateNative state);

    /// <summary>Whether XInput was found at all.</summary>
    public bool IsAvailable => _getState is not null;

    /// <summary>Which library answered, for diagnostics.</summary>
    public string? Library { get; private init; }

    /// <summary>
    /// Loads whichever XInput is present. Always returns a reader; one that
    /// found nothing simply reports every slot as empty.
    /// </summary>
    public static XInputReader Load()
    {
        foreach (string candidate in Candidates)
        {
            if (!NativeLibrary.TryLoad(candidate, out nint handle))
            {
                continue;
            }

            if (NativeLibrary.TryGetExport(handle, "XInputGetState", out nint export))
            {
                return new XInputReader(handle, Marshal.GetDelegateForFunctionPointer<XInputGetState>(export))
                {
                    Library = candidate,
                };
            }

            NativeLibrary.Free(handle);
        }

        return new XInputReader(0, null);
    }

    public GamepadState? Read(int slot)
    {
        if (_disposed || _getState is null || slot < 0 || slot >= ControllerPoller.SlotCount)
        {
            return null;
        }

        // ERROR_SUCCESS. Anything else -- almost always
        // ERROR_DEVICE_NOT_CONNECTED for an empty slot -- means there is
        // nothing here to read, which is not a failure worth reporting.
        if (_getState((uint)slot, out XInputStateNative native) != 0)
        {
            return null;
        }

        return new GamepadState(
            (GamepadButtons)native.Gamepad.Buttons,
            native.Gamepad.ThumbLX,
            native.Gamepad.ThumbLY);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_library != 0)
        {
            NativeLibrary.Free(_library);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputStateNative
    {
        public uint PacketNumber;
        public XInputGamepadNative Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepadNative
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }
}

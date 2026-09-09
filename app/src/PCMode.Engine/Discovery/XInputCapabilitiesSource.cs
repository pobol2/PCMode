using System.Runtime.InteropServices;

namespace PCMode.Engine;

/// <summary>
/// The real thing: XInputGetCapabilities, resolved by hand.
/// </summary>
/// <remarks>
/// SAME PROBE AS THE PICKER'S READER, FOR THE SAME REASON
///
/// Which XInput library exists depends on the machine -- 1_4 ships with Windows
/// 8 and later, 1_3 with the DirectX runtime, 9_1_0 is the compatibility shim
/// that is always present -- so this is resolved with NativeLibrary rather than
/// a DllImport naming one of them, exactly as the tray's controller polling
/// does. It is a separate, minimal binding rather than a shared one: this asks
/// for capabilities, not button state, and has no reason to depend on the
/// picker's gamepad model to say so.
///
/// A machine with no XInput at all is a supported configuration. It reads as
/// "no controllers", which is what it is.
/// </remarks>
public sealed class XInputCapabilitiesSource : IGamepadCapabilitiesSource, IDisposable
{
    private static readonly string[] Candidates = ["xinput1_4", "xinput1_3", "xinput9_1_0"];

    private readonly nint _library;
    private readonly XInputGetCapabilities? _getCapabilities;

    private bool _disposed;

    private XInputCapabilitiesSource(nint library, XInputGetCapabilities? getCapabilities)
    {
        _library = library;
        _getCapabilities = getCapabilities;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint XInputGetCapabilities(uint userIndex, uint flags, out XInputCapabilitiesNative capabilities);

    public bool IsAvailable => _getCapabilities is not null;

    /// <summary>
    /// Loads whichever XInput is present. Always returns a source; one that
    /// found nothing simply reports itself unavailable.
    /// </summary>
    public static XInputCapabilitiesSource Load()
    {
        foreach (string candidate in Candidates)
        {
            if (!NativeLibrary.TryLoad(candidate, out nint handle))
            {
                continue;
            }

            if (NativeLibrary.TryGetExport(handle, "XInputGetCapabilities", out nint export))
            {
                return new XInputCapabilitiesSource(
                    handle,
                    Marshal.GetDelegateForFunctionPointer<XInputGetCapabilities>(export)
                );
            }

            NativeLibrary.Free(handle);
        }

        return new XInputCapabilitiesSource(0, null);
    }

    public GamepadCapabilities? Read(int slot)
    {
        if (_disposed || _getCapabilities is null || slot < 0 || slot >= ControllerDiscoverer.SlotCount)
        {
            return null;
        }

        // ERROR_SUCCESS. Anything else -- almost always
        // ERROR_DEVICE_NOT_CONNECTED for an empty slot -- means there is
        // nothing here to report.
        if (_getCapabilities((uint)slot, 0, out XInputCapabilitiesNative native) != 0)
        {
            return null;
        }

        return new GamepadCapabilities(SubTypeName(native.SubType), (native.Flags & FlagWireless) != 0);
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

    private static string SubTypeName(byte subType) =>
        subType switch
        {
            1 => "Gamepad",
            2 => "Wheel",
            3 => "Arcade stick",
            4 => "Flight stick",
            5 => "Dance pad",
            6 => "Guitar",
            8 => "Guitar (alternate)",
            9 => "Drum kit",
            11 => "Guitar (bass)",
            19 => "Arcade pad",
            _ => "Unknown controller",
        };

    private const ushort FlagWireless = 0x0002;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibrationNative
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputCapabilitiesNative
    {
        public byte Type;
        public byte SubType;
        public ushort Flags;
        public XInputGamepadNative Gamepad;
        public XInputVibrationNative Vibration;
    }
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PCMode.Audio.Core.Interop;

/// <summary>
/// The Windows Core Audio interfaces for naming and enumerating endpoints.
/// </summary>
/// <remarks>
/// SHARED BECAUSE THE VTABLE IS THE CONTRACT
///
/// Two callers need to ask Windows "what playback devices exist and what are
/// they called": the engine's own device picker, and the Windows audio host
/// that actually renders into one. Both used to declare their own copy of
/// these COM interfaces, which is the kind of duplication that goes stale
/// quietly -- a v-table is positional, so a well-meaning edit to one copy and
/// not the other is a bug that only shows up on real hardware.
///
/// Declaring the interfaces once, here, means there is exactly one place that
/// has to match the documented Core Audio v-table order.
///
/// NOT THE STREAMING INTERFACES
///
/// This is about naming a device, not moving samples through it. IAudioClient
/// and its render/capture siblings stay with whichever project actually opens
/// a stream, because a mix format, a buffer, and a clock are a much larger
/// surface than "list the endpoints and read a friendly name" and mixing the
/// two would make this file that much larger for the benefit of readers who
/// only wanted one of them.
/// </remarks>
public enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}

public enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

[Flags]
public enum DeviceState : uint
{
    Active = 0x00000001,
    Disabled = 0x00000002,
    NotPresent = 0x00000004,
    Unplugged = 0x00000008,
    All = 0x0000000F,
}

[Flags]
public enum ClsCtx : uint
{
    InprocServer = 0x1,
    InprocHandler = 0x2,
    LocalServer = 0x4,
    RemoteServer = 0x10,

    All = InprocServer | InprocHandler | LocalServer | RemoteServer,
}

public enum StorageAccessMode : uint
{
    Read = 0,
}

[StructLayout(LayoutKind.Sequential)]
public struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;

    public PropertyKey(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

/// <summary>Enough of a PROPVARIANT to read a string.</summary>
[StructLayout(LayoutKind.Explicit)]
public struct PropVariant
{
    [FieldOffset(0)]
    public ushort VarType;

    [FieldOffset(8)]
    public nint PointerValue;
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection? devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(nint client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(nint client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice? device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice
{
    [PreserveSig]
    int Activate(
        ref Guid interfaceId,
        ClsCtx clsCtx,
        nint activationParameters,
        [MarshalAs(UnmanagedType.IUnknown)] out object? instance
    );

    [PreserveSig]
    int OpenPropertyStore(StorageAccessMode access, out IPropertyStore? properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);

    [PreserveSig]
    int GetState(out DeviceState state);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint propertyCount);

    [PreserveSig]
    int GetAt(uint propertyIndex, out PropertyKey key);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant value);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant value);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
public sealed class MMDeviceEnumeratorComObject { }

/// <summary>Creates the enumerator and reads the two device properties every caller wants.</summary>
[SupportedOSPlatform("windows")]
public static class CoreAudioEndpointInterop
{
    private const ushort VtLpWStr = 31;

    private static readonly PropertyKey FriendlyNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    public static IMMDeviceEnumerator CreateEnumerator()
    {
        object instance =
            Activator.CreateInstance(typeof(MMDeviceEnumeratorComObject))
            ?? throw new InvalidOperationException("Failed to create the MMDeviceEnumerator COM object.");

        return (IMMDeviceEnumerator)instance;
    }

    /// <summary>Opens one endpoint by its Core Audio ID.</summary>
    public static IMMDevice OpenDevice(IMMDeviceEnumerator enumerator, string endpointId, string what)
    {
        int hr = enumerator.GetDevice(endpointId, out IMMDevice? device);

        if (hr != 0 || device is null)
        {
            throw new InvalidOperationException(
                $"Windows could not open {what}. It may have been unplugged or disabled (0x{hr:X8})."
            );
        }

        return device;
    }

    public static string GetDeviceId(IMMDevice device)
    {
        int hr = device.GetId(out string? id);

        if (hr != 0 || string.IsNullOrEmpty(id))
        {
            throw new InvalidOperationException($"Core Audio could not read a device ID (0x{hr:X8}).");
        }

        return id;
    }

    public static string GetFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(StorageAccessMode.Read, out IPropertyStore? properties) != 0 || properties is null)
        {
            return string.Empty;
        }

        try
        {
            PropertyKey key = FriendlyNameKey;

            if (properties.GetValue(ref key, out PropVariant value) != 0)
            {
                return string.Empty;
            }

            try
            {
                return value.VarType == VtLpWStr && value.PointerValue != nint.Zero
                    ? Marshal.PtrToStringUni(value.PointerValue) ?? string.Empty
                    : string.Empty;
            }
            finally
            {
                if (value.VarType != 0)
                {
                    PropVariantClear(ref value);
                }
            }
        }
        finally
        {
            Release(properties);
        }
    }

    public static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}

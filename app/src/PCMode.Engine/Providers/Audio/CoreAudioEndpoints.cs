using System.Globalization;
using System.Runtime.InteropServices;

namespace PCMode.Engine;

/// <summary>
/// The real thing: Windows Core Audio, through COM.
/// </summary>
/// <remarks>
/// READING IS DOCUMENTED, WRITING IS NOT
///
/// Core Audio has a public, supported way to ask which endpoint is the default
/// (IMMDeviceEnumerator) and never shipped one to change it. IPolicyConfig is
/// the undocumented interface Windows' own Sound settings uses, and the one
/// every tool that switches audio devices has used since Vista. There is no
/// supported alternative; the choice is this or shelling out to somebody else's
/// program, and this application starts no processes.
///
/// EVERY CALL STARTS OVER
///
/// The enumerator is created per call and no endpoint is kept between calls, for
/// the reason in <see cref="IAudioEndpoints"/>: an HDMI endpoint comes and goes
/// with the television, and a cached one is a handle to a device that may not be
/// there any more.
///
/// FAILURES BECOME MESSAGES
///
/// COM answers in HRESULTs, which are no use to somebody reading a transition
/// report. Each one that matters is turned into a sentence naming what was being
/// attempted, because "audio failed (0x80070490)" and "that device is not there
/// any more" call for different things from a user.
/// </remarks>
public sealed class CoreAudioEndpoints : IAudioEndpoints
{
    public IReadOnlyList<AudioEndpoint> ActivePlayback()
    {
        IMMDeviceEnumerator enumerator = CreateEnumerator();

        try
        {
            Check(
                enumerator.EnumAudioEndpoints(DataFlowRender, DeviceStateActive, out IMMDeviceCollection? collection),
                "list the playback devices");

            if (collection is null)
            {
                throw new InvalidOperationException("Windows returned no playback device list.");
            }

            try
            {
                Check(collection.GetCount(out int count), "count the playback devices");

                var endpoints = new List<AudioEndpoint>(count);

                for (int index = 0; index < count; index++)
                {
                    Check(collection.Item(index, out IMMDevice? device), "read a playback device");

                    if (device is null)
                    {
                        continue;
                    }

                    try
                    {
                        Check(device.GetId(out string? id), "read a playback device's identifier");

                        if (!string.IsNullOrEmpty(id))
                        {
                            endpoints.Add(new AudioEndpoint(id, ReadFriendlyName(device)));
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }

                return endpoints;
            }
            finally
            {
                Marshal.ReleaseComObject(collection);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public AudioEndpoint? DefaultPlayback()
    {
        IMMDeviceEnumerator enumerator = CreateEnumerator();

        try
        {
            // Not checked: a machine with no active playback device has no default
            // and says so with an error code. That is a state, not a fault, and the
            // interface answers null for it.
            int hr = enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out IMMDevice? device);

            if (hr != 0 || device is null)
            {
                return null;
            }

            try
            {
                Check(device.GetId(out string? id), "read the current playback device's identifier");

                return string.IsNullOrEmpty(id) ? null : new AudioEndpoint(id, ReadFriendlyName(device));
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Points all three roles at one endpoint.
    /// </summary>
    /// <remarks>
    /// Console, Multimedia and Communications, set explicitly rather than
    /// assumed to follow one another. The Sound settings UI only offers two
    /// visible choices, but nothing guarantees the three stay together, and a
    /// mode that moved the film to the television while leaving system sounds on
    /// the desk speakers would be half applied without ever reporting it.
    /// </remarks>
    public void MakeDefault(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        IPolicyConfig policy = CreatePolicyConfig();

        try
        {
            Check(policy.SetDefaultEndpoint(endpointId, RoleConsole), "change the default playback device");
            Check(policy.SetDefaultEndpoint(endpointId, RoleMultimedia), "change the default device for media");
            Check(policy.SetDefaultEndpoint(endpointId, RoleCommunications), "change the default device for calls");
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    public double? ReadVolume(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        Wasapi.IAudioEndpointVolume? volume = OpenVolume(endpointId, out _);

        if (volume is null)
        {
            return null;
        }

        try
        {
            return volume.GetMasterVolumeLevelScalar(out float level) == 0 ? level : null;
        }
        finally
        {
            Marshal.ReleaseComObject(volume);
        }
    }

    /// <summary>
    /// Moves the endpoint's own volume slider.
    /// </summary>
    /// <remarks>
    /// Refuses rather than shrugs when the endpoint has no volume control. A
    /// configured level that silently did nothing would be found by somebody
    /// wondering why one half of their Party group is too loud.
    /// </remarks>
    public void SetVolume(string endpointId, double level)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        Wasapi.IAudioEndpointVolume volume = OpenVolume(endpointId, out string what)
            ?? throw new InvalidOperationException($"{what} has no volume control that Windows will let PCMode set.");

        try
        {
            Guid context = Guid.Empty;

            Check(
                volume.SetMasterVolumeLevelScalar((float)Math.Clamp(level, 0, 1), ref context),
                $"set the volume of {what}");
        }
        finally
        {
            Marshal.ReleaseComObject(volume);
        }
    }

    private static Wasapi.IAudioEndpointVolume? OpenVolume(string endpointId, out string what)
    {
        what = "that playback device";

        Wasapi.IMMDeviceEnumerator enumerator = Wasapi.CreateEnumerator();
        Wasapi.IMMDevice device = Wasapi.OpenDevice(enumerator, endpointId, what);

        try
        {
            Guid iid = Wasapi.AudioEndpointVolumeIid;

            return device.Activate(ref iid, Wasapi.ClsCtxAll, nint.Zero, out object? instance) == 0
                ? instance as Wasapi.IAudioEndpointVolume
                : null;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static string ReadFriendlyName(IMMDevice device)
    {
        // A device whose name cannot be read is still a device, and returning
        // empty rather than throwing keeps one odd endpoint from making the
        // whole list unavailable. It simply matches nothing, which is exactly
        // what an unnamed device should do.
        if (device.OpenPropertyStore(StorageRead, out IPropertyStore? store) != 0 || store is null)
        {
            return string.Empty;
        }

        PropertyKey key = FriendlyNameKey;

        if (store.GetValue(ref key, out PropVariant value) != 0)
        {
            return string.Empty;
        }

        try
        {
            return value.Pointer == nint.Zero
                ? string.Empty
                : Marshal.PtrToStringUni(value.Pointer) ?? string.Empty;
        }
        finally
        {
            // The string belongs to COM. Without this every enumeration leaks
            // one allocation per device, and modes are switched all day.
            _ = PropVariantClear(ref value);
        }
    }

    private static IMMDeviceEnumerator CreateEnumerator() =>
        Create<IMMDeviceEnumerator>(DeviceEnumeratorClsid, "Windows Core Audio");

    private static IPolicyConfig CreatePolicyConfig() =>
        Create<IPolicyConfig>(PolicyConfigClsid, "the Windows audio policy service");

    private static T Create<T>(Guid clsid, string what)
        where T : class
    {
        try
        {
            Type? type = Type.GetTypeFromCLSID(clsid);
            object? instance = type is null ? null : Activator.CreateInstance(type);

            return instance as T
                ?? throw new InvalidOperationException($"{what} is not available on this machine.");
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"{what} could not be reached. {exception.Message}",
                exception);
        }
    }

    private static void Check(int hr, string attempted)
    {
        if (hr != 0)
        {
            throw new InvalidOperationException(
                $"Windows could not {attempted} " +
                $"(0x{hr.ToString("X8", CultureInfo.InvariantCulture)}).");
        }
    }

    private const int DataFlowRender = 0;
    private const int RoleConsole = 0;
    private const int RoleMultimedia = 1;
    private const int RoleCommunications = 2;

    /// <summary>DEVICE_STATE_ACTIVE. See <see cref="IAudioEndpoints.ActivePlayback"/>.</summary>
    private const int DeviceStateActive = 0x1;

    private const int StorageRead = 0;

    private static readonly Guid DeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid PolicyConfigClsid = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    /// <summary>PKEY_Device_FriendlyName: the name Sound settings shows.</summary>
    private static readonly PropertyKey FriendlyNameKey = new()
    {
        FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        PropertyId = 14,
    };

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    /// <summary>
    /// Enough of a PROPVARIANT to read a string.
    /// </summary>
    /// <remarks>
    /// The tag and the first slot of the union, which is all a VT_LPWSTR value
    /// needs. The union begins eight bytes in on both 32 and 64 bit, after the
    /// tag and its three reserved fields.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public short Type;

        [FieldOffset(8)]
        public nint Pointer;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? devices);

        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);

        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out int count);

        int Item(int index, out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int classContext, nint activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

        int OpenPropertyStore(int access, out IPropertyStore? properties);

        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);

        int GetState(out int state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out int count);

        int GetAt(int index, out PropertyKey key);

        int GetValue(ref PropertyKey key, out PropVariant value);

        int SetValue(ref PropertyKey key, ref PropVariant value);

        int Commit();
    }

    /// <summary>
    /// The undocumented one. See the class remarks: there is no supported way to
    /// change the default endpoint, and this is the interface Windows uses.
    /// </summary>
    /// <remarks>
    /// Every method before SetDefaultEndpoint is declared and never called. They
    /// cannot be left out: this is a v-table, and removing an entry would shift
    /// every one after it, so the call would land on a different function
    /// entirely. The unused ones take nint rather than real signatures because
    /// nothing here needs to describe an audio format correctly, only to occupy
    /// the right slot.
    /// </remarks>
    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, nint format);

        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool isDefault, nint format);

        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);

        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, nint endpointFormat, nint mixFormat);

        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out long defaultPeriod, out long minimumPeriod);

        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, long period);

        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, nint mode);

        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, nint mode);

        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, out PropVariant value);

        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, ref PropVariant value);

        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);

        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }
}

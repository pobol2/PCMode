using System.Globalization;
using System.Runtime.InteropServices;
using PCMode.Audio.Core.Interop;

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
                enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out IMMDeviceCollection? collection),
                "list the playback devices");

            if (collection is null)
            {
                throw new InvalidOperationException("Windows returned no playback device list.");
            }

            try
            {
                Check(collection.GetCount(out uint count), "count the playback devices");

                var endpoints = new List<AudioEndpoint>((int)count);

                for (uint index = 0; index < count; index++)
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
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out IMMDevice? device);

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
            Check(policy.SetDefaultEndpoint(endpointId, ERole.Console), "change the default playback device");
            Check(policy.SetDefaultEndpoint(endpointId, ERole.Multimedia), "change the default device for media");
            Check(policy.SetDefaultEndpoint(endpointId, ERole.Communications), "change the default device for calls");
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

        IMMDeviceEnumerator enumerator = Wasapi.CreateEnumerator();
        IMMDevice device = Wasapi.OpenDevice(enumerator, endpointId, what);

        try
        {
            Guid iid = Wasapi.AudioEndpointVolumeIid;

            return device.Activate(ref iid, ClsCtx.All, nint.Zero, out object? instance) == 0
                ? instance as Wasapi.IAudioEndpointVolume
                : null;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// A device whose name cannot be read is still a device, and returning
    /// empty rather than throwing keeps one odd endpoint from making the whole
    /// list unavailable. It simply matches nothing, which is exactly what an
    /// unnamed device should do.
    /// </summary>
    private static string ReadFriendlyName(IMMDevice device) =>
        CoreAudioEndpointInterop.GetFriendlyName(device);

    private static IMMDeviceEnumerator CreateEnumerator() =>
        CoreAudioEndpointInterop.CreateEnumerator();

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

    private static readonly Guid PolicyConfigClsid = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

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

        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);

        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }
}

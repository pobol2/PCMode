using System.Globalization;
using System.Runtime.InteropServices;

namespace PCMode.Engine;

/// <summary>
/// The Core Audio streaming interfaces, declared once.
/// </summary>
/// <remarks>
/// SEPARATE FROM CoreAudioEndpoints ON PURPOSE
///
/// That file exists to name devices and move the Windows default. This one is
/// about moving samples, which needs a different and much larger set of
/// interfaces, and mixing the two would mean every reader of "which device is
/// the default" had to walk past a WAVEFORMATEXTENSIBLE first.
///
/// Everything here is documented Windows API. The one undocumented interface
/// PCMode uses lives in <see cref="CoreAudioEndpoints"/> and is named in
/// THIRD_PARTY_NOTICES.md.
///
/// VTABLE ORDER IS THE CONTRACT
///
/// These are COM v-tables: a method that is never called still has to occupy
/// its slot, and every declaration below is in header order for that reason.
/// Removing an unused one would shift every method after it onto the wrong
/// function.
/// </remarks>
internal static class Wasapi
{
    public const int DataFlowRender = 0;

    public const int RoleConsole = 0;
    public const int RoleMultimedia = 1;
    public const int RoleCommunications = 2;

    public const int DeviceStateActive = 0x1;

    public const int ShareModeShared = 0;

    public const int StreamFlagsCrossProcess = 0x00010000;
    public const int StreamFlagsLoopback = 0x00020000;
    public const int StreamFlagsEventCallback = 0x00040000;
    public const int StreamFlagsNoPersist = 0x00080000;

    /// <summary>
    /// Whether IAudioClient3 can initialize this shared stream. Its specialized
    /// initializer does not accept loopback capture; that path must use the
    /// ordinary IAudioClient initializer instead.
    /// </summary>
    public static bool CanUseLowLatencySharedStream(int requestedFlags) =>
        (requestedFlags & StreamFlagsLoopback) == 0;

    /// <summary>The subset IAudioClient3 accepts when opening a shared stream.</summary>
    public static int LowLatencySharedStreamFlags(int requestedFlags) =>
        requestedFlags & StreamFlagsEventCallback;

    public const int BufferFlagsDataDiscontinuity = 0x1;
    public const int BufferFlagsSilent = 0x2;

    public const int ClsCtxAll = 0x17;

    /// <summary>The device is gone: unplugged, disabled, or its driver restarted.</summary>
    public const int DeviceInvalidated = unchecked((int)0x88890004);

    /// <summary>The endpoint will not accept a stream in the requested mode.</summary>
    public const int UnsupportedFormat = unchecked((int)0x88890008);

    /// <summary>Something else holds the endpoint in exclusive mode.</summary>
    public const int DeviceInUse = unchecked((int)0x8889000A);

    /// <summary>The endpoint is exclusive-mode only, so shared-mode Party cannot use it.</summary>
    public const int ExclusiveModeOnly = unchecked((int)0x8889000B);

    /// <summary>This client will not take an event handle, so it must be polled.</summary>
    public const int EventHandleNotExpected = unchecked((int)0x8889000D);

    /// <summary>The Windows Audio service is not running.</summary>
    public const int ServiceNotRunning = unchecked((int)0x88890017);

    public static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid AudioClient3Iid = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
    public static readonly Guid AudioCaptureClientIid = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static readonly Guid AudioRenderClientIid = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid AudioEndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    public static readonly Guid DeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>KSDATAFORMAT_SUBTYPE_PCM.</summary>
    public static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT.</summary>
    public static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public static IMMDeviceEnumerator CreateEnumerator()
    {
        Type? type = Type.GetTypeFromCLSID(DeviceEnumeratorClsid);
        object? instance = type is null ? null : Activator.CreateInstance(type);

        return instance as IMMDeviceEnumerator
            ?? throw new InvalidOperationException("Windows Core Audio is not available on this machine.");
    }

    /// <summary>Opens one endpoint by its Core Audio ID.</summary>
    /// <exception cref="InvalidOperationException">
    /// The ID names nothing Windows currently has. That is the ordinary state of
    /// an endpoint somebody has just unplugged, so it is a sentence rather than
    /// an HRESULT.
    /// </exception>
    public static IMMDevice OpenDevice(IMMDeviceEnumerator enumerator, string endpointId, string what)
    {
        int hr = enumerator.GetDevice(endpointId, out IMMDevice? device);

        if (hr != 0 || device is null)
        {
            throw new InvalidOperationException(
                $"Windows could not open {what}. It may have been unplugged or disabled " +
                $"({Hresult(hr)}).");
        }

        return device;
    }

    public static IAudioClient ActivateClient(IMMDevice device, string what)
    {
        Guid iid = AudioClientIid;

        int hr = device.Activate(ref iid, ClsCtxAll, nint.Zero, out object? instance);

        if (hr != 0 || instance is not IAudioClient client)
        {
            throw new InvalidOperationException($"Windows could not open an audio stream on {what} ({Hresult(hr)}).");
        }

        return client;
    }

    /// <summary>Turns an audio HRESULT into something worth showing a user.</summary>
    public static string Explain(int hr, string what) => hr switch
    {
        DeviceInvalidated =>
            $"{what} is no longer available. It was unplugged, disabled, or its driver restarted.",
        UnsupportedFormat =>
            $"{what} will not accept the shared-mode format Windows mixes at.",
        DeviceInUse or ExclusiveModeOnly =>
            $"{what} is held in exclusive mode by another program, so Party mode cannot share it.",
        ServiceNotRunning =>
            "The Windows Audio service is not running.",
        _ => $"{what} reported {Hresult(hr)}.",
    };

    public static string Hresult(int hr) =>
        "0x" + hr.ToString("X8", CultureInfo.InvariantCulture);

    public static void Check(int hr, string what)
    {
        if (hr != 0)
        {
            throw new InvalidOperationException(Explain(hr, what));
        }
    }

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(nint memory);

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int classContext, nint activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

        [PreserveSig]
        int OpenPropertyStore(int access, out nint properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, nint format, nint sessionGuid);

        [PreserveSig]
        int GetBufferSize(out int bufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out int padding);

        [PreserveSig]
        int IsFormatSupported(int shareMode, nint format, out nint closestMatch);

        [PreserveSig]
        int GetMixFormat(out nint format);

        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(nint handle);

        [PreserveSig]
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out nint data, out int frames, out int flags, out long devicePosition, out long performancePosition);

        [PreserveSig]
        int ReleaseBuffer(int frames);

        [PreserveSig]
        int GetNextPacketSize(out int frames);
    }

    [ComImport]
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioRenderClient
    {
        [PreserveSig]
        int GetBuffer(int frames, out nint data);

        [PreserveSig]
        int ReleaseBuffer(int frames, int flags);
    }

    /// <summary>
    /// The low-latency shared-mode engine, where the endpoint's driver offers
    /// one.
    /// </summary>
    /// <remarks>
    /// Introduced in Windows 10 to give a shared-mode stream a period close to
    /// what exclusive mode gets, without exclusive mode's "nobody else may use
    /// this endpoint" cost. <see cref="WasapiAudioMirror"/> tries this for the
    /// render endpoint and falls back to plain <see cref="IAudioClient"/> at the
    /// device's own default period when the driver does not support it. Loopback
    /// capture uses IAudioClient directly because this initializer does not
    /// accept the loopback stream flag.
    ///
    /// Declared with every <see cref="IAudioClient"/> and IAudioClient2 method
    /// ahead of the three this file actually calls, because C# COM interop walks
    /// the v-table in declaration order and IAudioClient3 inherits both.
    /// </remarks>
    [ComImport]
    [Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient3
    {
        // IAudioClient
        [PreserveSig]
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, nint format, nint sessionGuid);

        [PreserveSig]
        int GetBufferSize(out int bufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out int padding);

        [PreserveSig]
        int IsFormatSupported(int shareMode, nint format, out nint closestMatch);

        [PreserveSig]
        int GetMixFormat(out nint format);

        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(nint handle);

        [PreserveSig]
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

        // IAudioClient2. Neither is called; both still occupy their slot.
        [PreserveSig]
        int IsOffloadCapable(int category, [MarshalAs(UnmanagedType.Bool)] out bool capable);

        [PreserveSig]
        int SetClientProperties(nint properties);

        [PreserveSig]
        int GetBufferSizeLimits(nint format, [MarshalAs(UnmanagedType.Bool)] bool eventDriven, out long minDuration, out long maxDuration);

        // IAudioClient3
        [PreserveSig]
        int GetSharedModeEnginePeriod(
            nint format,
            out int defaultPeriodInFrames,
            out int fundamentalPeriodInFrames,
            out int minPeriodInFrames,
            out int maxPeriodInFrames);

        [PreserveSig]
        int GetCurrentSharedModeEnginePeriod(out nint format, out int currentPeriodInFrames);

        [PreserveSig]
        int InitializeSharedAudioStream(int streamFlags, int periodInFrames, nint format, nint sessionGuid);
    }

    /// <summary>
    /// The endpoint's own master volume -- the slider in Sound settings.
    /// </summary>
    /// <remarks>
    /// Declared as far as GetMute and no further: everything after it is channel
    /// stepping and hardware capability queries that Party mode has no use for.
    /// </remarks>
    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(nint notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(nint notify);

        [PreserveSig]
        int GetChannelCount(out int channels);

        [PreserveSig]
        int SetMasterVolumeLevel(float decibels, ref Guid eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float decibels);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(int channel, float decibels, ref Guid eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(int channel, float level, ref Guid eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(int channel, out float decibels);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(int channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}

/// <summary>
/// What one endpoint's shared-mode stream is made of.
/// </summary>
/// <remarks>
/// Read out of the WAVEFORMATEX Windows hands back rather than kept as a
/// pointer, because the pointer is COM-allocated and has to be freed while the
/// numbers are needed for as long as the stream lives.
///
/// The two endpoints in a Party group are not required to agree about any of
/// this. A soundbar over HDMI commonly mixes at 48 kHz while onboard speakers
/// sit at 44.1 kHz, and one of them being 7.1 while the other is stereo is
/// ordinary. Conversion is therefore the normal path, not an error case.
/// </remarks>
/// <param name="Bits">Container bits per sample: 16, 24 or 32.</param>
/// <param name="IsFloat">Whether samples are IEEE floats rather than integers.</param>
internal sealed record WaveFormat(int SampleRate, int Channels, int Bits, bool IsFloat, int BlockAlign)
{
    public int BytesPerSample => Bits / 8;

    /// <summary>What a health report calls this, for example "48000 Hz, 2 ch, 32-bit float".</summary>
    public override string ToString() =>
        SampleRate <= 0
            ? "not yet known"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{SampleRate} Hz, {Channels} ch, {Bits}-bit {(IsFloat ? "float" : "PCM")}");

    /// <summary>
    /// Reads a WAVEFORMATEX, following it into WAVEFORMATEXTENSIBLE when it says
    /// it is one.
    /// </summary>
    /// <remarks>
    /// The shared-mode mix format is almost always extensible 32-bit float, but
    /// "almost always" is not a thing to write a stream loop against: a machine
    /// whose endpoint reports 16-bit PCM would otherwise get white noise at full
    /// volume through a soundbar.
    /// </remarks>
    public static WaveFormat Read(nint format, string what)
    {
        if (format == nint.Zero)
        {
            throw new InvalidOperationException($"Windows described {what}'s audio format as nothing at all.");
        }

        short tag = Marshal.ReadInt16(format, 0);
        short channels = Marshal.ReadInt16(format, 2);
        int sampleRate = Marshal.ReadInt32(format, 4);
        short blockAlign = Marshal.ReadInt16(format, 12);
        short bits = Marshal.ReadInt16(format, 14);

        const short WaveFormatPcm = 1;
        const short WaveFormatFloat = 3;
        const short WaveFormatExtensible = unchecked((short)0xFFFE);

        bool isFloat;

        if (tag == WaveFormatExtensible)
        {
            // cbSize is at 16; the extensible payload begins at 18 with the valid
            // bits, then the channel mask at 20, then the sub-format GUID at 24.
            byte[] subFormat = new byte[16];
            Marshal.Copy(format + 24, subFormat, 0, 16);

            var subtype = new Guid(subFormat);

            isFloat = subtype == Wasapi.SubtypeFloat;

            if (!isFloat && subtype != Wasapi.SubtypePcm)
            {
                throw new InvalidOperationException(
                    $"{what} mixes in a compressed or encoded format that Party mode cannot mirror. " +
                    "Set that endpoint to a plain PCM format in Sound settings.");
            }
        }
        else if (tag is WaveFormatPcm or WaveFormatFloat)
        {
            isFloat = tag == WaveFormatFloat;
        }
        else
        {
            throw new InvalidOperationException(
                $"{what} mixes in an audio format ({tag}) that Party mode cannot mirror.");
        }

        if (channels <= 0 || sampleRate <= 0 || bits is not (16 or 24 or 32))
        {
            throw new InvalidOperationException(
                $"Windows described {what} as {sampleRate} Hz, {channels} channel(s), {bits}-bit, " +
                "which is not something Party mode can mirror.");
        }

        return new WaveFormat(sampleRate, channels, bits, isFloat, blockAlign);
    }
}

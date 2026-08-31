using System.Runtime.InteropServices;

namespace PCMode.Audio.Windows.Interop;

internal enum AudioClientShareMode
{
    Shared = 0,
    Exclusive = 1
}

[Flags]
internal enum AudioClientStreamFlags : uint
{
    None = 0,
    Loopback = 0x00020000,
    EventCallback = 0x00040000,
    SrcDefaultQuality = 0x08000000,
    AutoConvertPcm = 0x80000000
}

[Flags]
internal enum AudioClientBufferFlags : uint
{
    None = 0,
    DataDiscontinuity = 0x1,
    Silent = 0x2,
    TimestampError = 0x4
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSecond;
    public uint AverageBytesPerSecond;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraSize;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatExtensible
{
    public WaveFormatEx Format;
    public ushort ValidBitsPerSample;
    public uint ChannelMask;
    public Guid SubFormat;
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(
        AudioClientShareMode shareMode,
        AudioClientStreamFlags streamFlags,
        long bufferDuration,
        long periodicity,
        IntPtr format,
        IntPtr audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out uint paddingFrames);

    [PreserveSig]
    int IsFormatSupported(
        AudioClientShareMode shareMode,
        IntPtr format,
        out IntPtr closestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr deviceFormat);

    [PreserveSig]
    int GetDevicePeriod(
        out long defaultPeriod,
        out long minimumPeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.IUnknown)]
        out object service);
}

[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(
        out IntPtr data,
        out uint frameCount,
        out AudioClientBufferFlags flags,
        out ulong devicePosition,
        out ulong qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint frameCount);

    [PreserveSig]
    int GetNextPacketSize(
        out uint frameCount);
}

[ComImport]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig]
    int GetBuffer(
        uint frameCount,
        out IntPtr data);

    [PreserveSig]
    int ReleaseBuffer(
        uint frameCount,
        AudioClientBufferFlags flags);
}

[ComImport]
[Guid("CD63314F-3FBA-4A1B-812C-EF96358728E7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClock
{
    [PreserveSig]
    int GetFrequency(
        out ulong frequency);

    [PreserveSig]
    int GetPosition(
        out ulong devicePosition,
        out ulong qpcPosition);

    [PreserveSig]
    int GetCharacteristics(
        out uint characteristics);
}
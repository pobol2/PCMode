using System.Runtime.InteropServices;
using PCMode.Audio.Core.Models;

namespace PCMode.Audio.DriverClient.Protocol;

[Flags]
public enum DriverCapabilities :
    ulong
{
    None =
        0,

    Status =
        1UL << 0,

    ReadPcmIoctl =
        1UL << 1,

    SingleConsumer =
        1UL << 2,

    DropOldest =
        1UL << 3,

    Pcm16 =
        1UL << 4,

    Float32 =
        1UL << 5
}

public enum DriverSampleFormat :
    uint
{
    Unknown =
        0,

    Pcm16 =
        1,

    Float32 =
        2
}

[StructLayout(
    LayoutKind.Sequential,
    Pack = 8)]
internal struct NativeDriverFormat
{
    public uint Size;

    public uint SampleRate;

    public uint Channels;

    public DriverSampleFormat
        SampleFormat;

    public uint BitsPerSample;

    public uint BlockAlign;
}

[StructLayout(
    LayoutKind.Sequential,
    Pack = 8)]
internal struct NativeDriverProtocolInfo
{
    public uint Size;

    public uint Major;

    public uint Minor;

    public ulong Capabilities;

    public uint MaxPacketBytes;

    public uint TransportSlotCount;

    public NativeDriverFormat
        PreferredFormat;
}

[StructLayout(
    LayoutKind.Sequential,
    Pack = 8)]
internal struct NativeDriverStatus
{
    public uint Size;

    public byte DeviceStarted;

    public byte StreamRunning;

    public byte UserModeConnected;

    public byte Reserved0;

    public uint ActiveStreams;

    public ulong PacketsRendered;

    public ulong BytesRendered;

    public ulong PacketsDropped;

    public ulong DiscontinuityCount;

    public NativeDriverFormat
        CurrentFormat;
}

[StructLayout(
    LayoutKind.Sequential,
    Pack = 8)]
internal struct NativeDriverPacketHeader
{
    public uint Size;

    public uint PayloadBytes;

    public ulong Sequence;

    public ulong Qpc;

    public NativeDriverFormat Format;
}

public sealed class DriverProtocolException :
    InvalidOperationException
{
    public DriverProtocolException(
        string message)
        : base(message)
    {
    }
}

public static class DriverProtocol
{
    public const uint MajorVersion =
        1;

    public const uint MinimumMinorVersion =
        1;

    public static readonly Guid
        DeviceInterfaceGuid =
            new(
                "84EEBFB8-E079-4E56-BCDE-E06BFD432708");

    public const uint IoctlGetProtocol =
        0x80106000;

    public const uint IoctlGetStatus =
        0x80106004;

    public const uint IoctlReadPcm =
        0x80106008;

    public const uint IoctlConnect =
        0x8010600C;

    /// <summary>
    /// Releases the transport claim while the handle that holds it is still
    /// open. The driver's implicit release on handle close
    /// (<c>EvtFileCleanup</c>) is not reliably reaching it, so a
    /// well-behaved client releases explicitly before disconnecting instead
    /// of depending on that notification.
    /// </summary>
    public const uint IoctlDisconnect =
        0x80106010;

    public static readonly
        DriverCapabilities
        RequiredCapabilities =
            DriverCapabilities.Status |
            DriverCapabilities.ReadPcmIoctl |
            DriverCapabilities.SingleConsumer |
            DriverCapabilities.DropOldest;

    public static void Validate(
        uint major,
        uint minor,
        DriverCapabilities capabilities)
    {
        if (major !=
            MajorVersion)
        {
            throw new DriverProtocolException(
                $"Driver protocol {major}.{minor} is incompatible with DriverClient {MajorVersion}.{MinimumMinorVersion}.");
        }

        if (minor <
            MinimumMinorVersion)
        {
            throw new DriverProtocolException(
                $"Driver protocol {major}.{minor} is too old. Version {MajorVersion}.{MinimumMinorVersion} or newer is required.");
        }

        DriverCapabilities missing =
            RequiredCapabilities &
            ~capabilities;

        if (missing !=
            DriverCapabilities.None)
        {
            throw new DriverProtocolException(
                $"Driver is missing required capabilities: {missing}.");
        }
    }

    internal static AudioFormat
        ToAudioFormat(
            NativeDriverFormat format)
    {
        if (format.SampleRate == 0 ||
            format.Channels == 0)
        {
            throw new DriverProtocolException(
                "Driver returned an invalid audio format.");
        }

        return new AudioFormat(
            checked(
                (int)format.SampleRate),
            checked(
                (int)format.Channels));
    }
}
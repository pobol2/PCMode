#pragma once

#include <ntddk.h>

//
// User-mode transport interface.
//
// {84EEBFB8-E079-4E56-BCDE-E06BFD432708}
//
DEFINE_GUID(GUID_DEVINTERFACE_PCMODE_AUDIO, 0x84eebfb8, 0xe079, 0x4e56, 0xbc, 0xde, 0xe0, 0x6b, 0xfd, 0x43, 0x27, 0x08);

constexpr ULONG PCMODE_AUDIO_PROTOCOL_MAJOR = 1;
constexpr ULONG PCMODE_AUDIO_PROTOCOL_MINOR = 1;

constexpr ULONG PCMODE_AUDIO_MAX_CHANNELS = 2;

constexpr ULONG PCMODE_AUDIO_TRANSPORT_SLOT_COUNT = 32;

constexpr ULONG PCMODE_AUDIO_MAX_PACKET_BYTES = 32768;

enum class PCMODE_AUDIO_SAMPLE_FORMAT : ULONG
{
    Unknown = 0,
    Pcm16 = 1,
    Float32 = 2,
};

enum PCMODE_AUDIO_CAPABILITY : ULONGLONG
{
    PCMODE_AUDIO_CAPABILITY_NONE = 0,

    PCMODE_AUDIO_CAPABILITY_STATUS = 1ull << 0,

    PCMODE_AUDIO_CAPABILITY_READ_PCM_IOCTL = 1ull << 1,

    PCMODE_AUDIO_CAPABILITY_SINGLE_CONSUMER = 1ull << 2,

    PCMODE_AUDIO_CAPABILITY_DROP_OLDEST = 1ull << 3,

    PCMODE_AUDIO_CAPABILITY_PCM16 = 1ull << 4,

    PCMODE_AUDIO_CAPABILITY_FLOAT32 = 1ull << 5,
};

struct PCMODE_AUDIO_FORMAT
{
    ULONG Size;

    ULONG SampleRate;

    ULONG Channels;

    PCMODE_AUDIO_SAMPLE_FORMAT
    SampleFormat;

    ULONG BitsPerSample;

    ULONG BlockAlign;
};

struct PCMODE_AUDIO_PROTOCOL_INFO
{
    ULONG Size;

    ULONG Major;

    ULONG Minor;

    ULONGLONG Capabilities;

    ULONG MaxPacketBytes;

    ULONG TransportSlotCount;

    PCMODE_AUDIO_FORMAT
    PreferredFormat;
};

struct PCMODE_AUDIO_STATUS
{
    ULONG Size;

    BOOLEAN DeviceStarted;

    BOOLEAN StreamRunning;

    BOOLEAN UserModeConnected;

    UCHAR Reserved0;

    ULONG ActiveStreams;

    ULONGLONG PacketsRendered;

    ULONGLONG BytesRendered;

    ULONGLONG PacketsDropped;

    //
    // Times SkipMissedRenderSlices reported a fresh fall-behind (see its
    // comment in RenderStream.cpp) and the driver signaled the Host to
    // re-prime its jitter buffer. Does not include the routine signal a
    // Pause/Run transition already sends. A healthy system stays at zero;
    // any nonzero count during a display topology change is this driver's
    // own render timer having fallen behind real time, independent of
    // whatever the physical output route is doing.
    //
    ULONGLONG DiscontinuityCount;

    PCMODE_AUDIO_FORMAT
    CurrentFormat;
};

//
// PCM bytes immediately follow this header.
//
struct PCMODE_AUDIO_PACKET_HEADER
{
    ULONG Size;

    ULONG PayloadBytes;

    ULONGLONG Sequence;

    ULONGLONG Qpc;

    PCMODE_AUDIO_FORMAT Format;
};

#define FILE_DEVICE_PCMODE_AUDIO 0x00008010

#define IOCTL_PCMODE_AUDIO_GET_PROTOCOL CTL_CODE(FILE_DEVICE_PCMODE_AUDIO, 0x800, METHOD_BUFFERED, FILE_READ_ACCESS)

#define IOCTL_PCMODE_AUDIO_GET_STATUS CTL_CODE(FILE_DEVICE_PCMODE_AUDIO, 0x801, METHOD_BUFFERED, FILE_READ_ACCESS)

#define IOCTL_PCMODE_AUDIO_READ_PCM CTL_CODE(FILE_DEVICE_PCMODE_AUDIO, 0x802, METHOD_BUFFERED, FILE_READ_ACCESS)

#define IOCTL_PCMODE_AUDIO_CONNECT CTL_CODE(FILE_DEVICE_PCMODE_AUDIO, 0x803, METHOD_BUFFERED, FILE_READ_ACCESS)

//
// Explicit release of the transport claim, sent while the handle that holds
// it is still open. EvtFileCleanup is meant to release the claim implicitly
// when that handle closes, but that notification is not reliably reaching
// this driver, so a well-behaved client releases explicitly instead of
// depending on it.
//
#define IOCTL_PCMODE_AUDIO_DISCONNECT CTL_CODE(FILE_DEVICE_PCMODE_AUDIO, 0x804, METHOD_BUFFERED, FILE_READ_ACCESS)

#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <acx.h>

//
// Mirrors the Stop/Pause/Run states used by Microsoft's ACX StreamEngine
// sample. A single "Running" bool cannot express the difference between "a
// fresh stream that has never played" and "a stream that was paused and is
// resuming" -- the former should reset the render timeline, the latter must
// preserve it (position, PacketTimer catch-up bookkeeping) or resuming after
// a pause reports a discontinuous position to Windows.
//
enum class PCMODE_STREAM_STATE
{
    Stop,
    Pause,
    Run,
};

//
// PCMode's render pin is a plain (non-offload) WaveRT streaming circuit.
// Per the ACX streaming contract ("ACX Streaming", Windows driver docs), a
// stream like this is only ever allocated PacketCount 1 (timer-driven: one
// continuous ring buffer, flow-controlled purely by presentation position)
// or 2 (event-driven ping-pong: two alternating regions of one shared
// buffer, flow-controlled by packet index via SetRenderPacket/
// GetCurrentPacket). There is no real-world case where more are requested.
//
constexpr ULONG PCMODE_MAX_RENDER_PACKETS = 2;

struct PCMODE_STREAM_CONTEXT
{
    ACXSTREAM Stream;

    ACXDATAFORMAT Format;

    //
    // The sole backing allocation for both PacketCount cases above: for
    // PacketCount==1 it is used as a single ring; for PacketCount==2 it is
    // one buffer split into two adjacent regions that share this same MDL,
    // per the ACX ping-pong allocation contract (packet 1 has no MDL of its
    // own -- see PCModeEvtStreamAllocateRtPackets). RingSize is the logical
    // byte length used for wraparound arithmetic (PacketSize * PacketCount);
    // the actual allocation may be rounded up to a page boundary beyond
    // that, but nothing ever reads the padding.
    //
    PMDL RingMdl;

    ULONG RingSize;

    ULONG PacketCount;

    ULONG PacketSize;

    //
    // Bytes consumed per render-timer tick. For PacketCount==2 this equals
    // PacketSize (each tick fully swaps one ping/pong region, exactly the
    // event-driven model). For PacketCount==1 it is a small fraction of the
    // ring, so the single buffer is consumed incrementally as time passes
    // rather than re-read wholesale on every tick -- see the ring-slicing
    // remark on RenderTick in RenderStream.cpp for why the wholesale re-read
    // was wrong.
    //
    ULONG TickBytes;

    PCMODE_STREAM_STATE State;

    //
    // The render timeline. CurrentPacket is a monotonically increasing tick
    // counter driven only by the timer (PCModeEvtPacketTimer) on its own
    // schedule, never by what EvtAcxStreamSetRenderPacket reports the OS has
    // written. It is used both as the ring-consumption cursor
    // (RenderRingSlice reads from CurrentPacket * TickBytes % RingSize) and
    // as the value reported to EvtAcxStreamGetCurrentPacket and
    // AcxRtStreamNotifyPacketComplete on every tick.
    //
    // An earlier version of this driver split these into two counters --
    // a fine one driving ring reads and a coarse one, advancing only once
    // per full ACX packet (once per ring lap for PacketCount==1), driving
    // the ACX notification -- reasoning that ACX's own documentation
    // frames timer-driven flow control as purely position-based and
    // packet-index notifications as informational only. Real-hardware
    // testing (RMS/peak dropping from the expected ~0.707 for a clean sine
    // to ~0.5, indicating intermittent dropouts) showed that assumption
    // wrong for real playback: throttling the notification -- which
    // appears to be what wakes the audio engine's thread to check position
    // and top up the buffer -- to 1/16th its previous frequency starved
    // it. Kept as one counter, notified every tick, because that is the
    // behavior actually verified working.
    //
    volatile LONG CurrentPacket;

    //
    // Set by SkipMissedRenderSlices while the render timer is behind real
    // time, cleared once it catches back up. Only single-threaded WDFTIMER
    // callback access, so a plain field is enough. Lets SkipMissedRenderSlices
    // tell a fresh fall-behind (worth signaling to the Host as one
    // discontinuity) apart from an already-signaled streak still catching up
    // tick by tick (which would otherwise re-signal on every one of those
    // ticks and repeatedly reset the Host's jitter buffer for what is really
    // a single outage).
    //
    BOOLEAN RenderBehind;

    //
    // Position is derived from wall-clock time relative to StartTime/
    // StartPosition, so it stays a truthful, monotonic function of real
    // elapsed time across pause/resume rather than a count of timer fires.
    //
    ULONGLONG Position;

    LONGLONG StartTime;

    ULONGLONG StartPosition;

    LARGE_INTEGER PerformanceCounterFrequency;

    volatile LONG TransportRegistered;

    //
    // Idempotent latch for whether PCModeTransportStreamRunningStarted has
    // been told about this stream, independent of the Stop/Pause/Run enum
    // above so that EvtStreamContextCleanup can safely unwind it defensively
    // regardless of what state an abnormal teardown leaves State in.
    //
    volatile LONG TransportRunningRegistered;

    WDFTIMER PacketTimer;
};

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(
    PCMODE_STREAM_CONTEXT,
    PCModeGetStreamContext);

EVT_ACX_STREAM_PREPARE_HARDWARE
    PCModeEvtStreamPrepareHardware;

EVT_ACX_STREAM_RELEASE_HARDWARE
    PCModeEvtStreamReleaseHardware;

EVT_ACX_STREAM_RUN
    PCModeEvtStreamRun;

EVT_ACX_STREAM_PAUSE
    PCModeEvtStreamPause;

EVT_ACX_STREAM_ALLOCATE_RTPACKETS
    PCModeEvtStreamAllocateRtPackets;

EVT_ACX_STREAM_FREE_RTPACKETS
    PCModeEvtStreamFreeRtPackets;

EVT_ACX_STREAM_SET_RENDER_PACKET
    PCModeEvtStreamSetRenderPacket;

EVT_ACX_STREAM_GET_CURRENT_PACKET
    PCModeEvtStreamGetCurrentPacket;

EVT_ACX_STREAM_GET_PRESENTATION_POSITION
    PCModeEvtStreamGetPresentationPosition;

EVT_ACX_STREAM_GET_HW_LATENCY
    PCModeEvtStreamGetHwLatency;

EVT_WDF_TIMER
    PCModeEvtPacketTimer;

EVT_WDF_OBJECT_CONTEXT_CLEANUP
    PCModeEvtStreamContextCleanup;

NTSTATUS
PCModeCreateRenderStream(
    _In_ WDFDEVICE Device,
    _In_ ACXCIRCUIT Circuit,
    _Inout_ PACXSTREAM_INIT StreamInit,
    _In_ ACXDATAFORMAT Format);

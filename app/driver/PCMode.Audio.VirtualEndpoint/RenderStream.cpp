#include <wdm.h>
#include <windef.h>
#include <ntintsafe.h>
#include <ks.h>

#include "RenderStream.h"
#include "UserModeTransport.h"
#include "Trace.h"

namespace
{
    constexpr ULONG PoolTag =
        'APCP';

    //
    // 100-nanosecond units per second. Not a WDK macro; KSCONVERT_PERFORMANCE_
    // TIME (ks.h) and this constant are the same pair Microsoft's ACX
    // StreamEngine sample uses for its HNS-based absolute scheduling math.
    //
    constexpr ULONGLONG HnsPerSecond =
        10'000'000ull;

    //
    // How many render-timer ticks make up one full pass through the ring
    // when PacketCount==1 (timer-driven). Only used in that case -- see
    // ComputeTickBytes. Chosen to land in the low-single-digit
    // milliseconds (768 bytes / 4ms for the 12288-byte/64ms ring PCMode
    // negotiates at 48kHz stereo PCM16), comfortably inside the "presentation
    // position needs to be updated more than once per pass through the
    // buffer" guidance from the ACX streaming docs without making the timer
    // needlessly chatty.
    //
    constexpr ULONG RenderSliceCount =
        16;

    VOID
    NotifyTransportRunningStopped(
        _In_ PCMODE_STREAM_CONTEXT* Context,
        _In_ ACXSTREAM Stream)
    {
        if (InterlockedExchange(
                &Context->TransportRunningRegistered,
                FALSE))
        {
            PCModeTransportStreamRunningStopped(
                AcxCircuitGetWdfDevice(
                    AcxStreamGetCircuit(
                        Stream)));
        }
    }

    //
    // Bytes consumed per render-timer tick.
    //
    // For PacketCount==2 (event-driven ping-pong), a tick is a full swap of
    // one region -- TickBytes==PacketSize -- exactly the model the client
    // expects: each of the two regions is meant to be completely rewritten
    // by the client between renders.
    //
    // For PacketCount==1 (timer-driven), there is only one region and the
    // client writes into it continuously as a true ring, not as discrete
    // atomic chunks. Re-reading the whole thing once per full pass (as if
    // it behaved like the ping-pong case) means every tick resends a mix of
    // stale bytes from earlier in the pass and freshly-written bytes the
    // client is mid-writing underneath us -- audible as spikes and
    // irregular playback. Consuming it in small slices instead means each
    // tick only reads and forwards the portion of the ring that has
    // genuinely become new since the previous tick.
    //
    ULONG
    ComputeTickBytes(
        _In_ ULONG PacketCount,
        _In_ ULONG PacketSize,
        _In_ ULONG RingSize,
        _In_ ULONG BlockAlign)
    {
        if (PacketCount != 1)
        {
            return PacketSize;
        }

        ULONG sliceBytes =
            RingSize /
            RenderSliceCount;

        //
        // Rounded down to a whole number of frames -- an unaligned slice
        // size would drift the read cursor off the left/right sample
        // boundary a little more with every wrap, slowly corrupting stereo
        // phase over a long playback session.
        //
        if (BlockAlign != 0)
        {
            sliceBytes -=
                sliceBytes %
                BlockAlign;
        }

        return
            sliceBytes != 0
                ? sliceBytes
                : RingSize;
    }

    //
    // Renders one tick's worth of PCM into the user-mode transport, read
    // from the ring at (Tick * TickBytes) % RingSize. That single formula
    // covers both packet-count cases: for PacketCount==2 it alternates
    // between 0 and PacketSize (ping-pong), and for PacketCount==1 it
    // advances smoothly through the single ring, wrapping every RingSize /
    // TickBytes ticks. A tick that straddles the end of the ring is pushed
    // as two separate contiguous chunks rather than assembled into a
    // scratch buffer first -- the downstream transport already treats each
    // push as an independent chunk of PCM, so there is nothing to gain from
    // copying the wraparound into one buffer before forwarding it.
    //
    VOID
    RenderRingSlice(
        _In_ PCMODE_STREAM_CONTEXT* Context,
        _In_ ACXSTREAM Stream,
        _In_ LONG Tick)
    {
        if (Context->RingMdl == nullptr ||
            Context->RingSize == 0 ||
            Context->TickBytes == 0)
        {
            return;
        }

        UCHAR* buffer =
            static_cast<UCHAR*>(
                MmGetSystemAddressForMdlSafe(
                    Context->RingMdl,
                    NormalPagePriority |
                    MdlMappingNoExecute));

        if (buffer == nullptr)
        {
            return;
        }

        ULONG ringOffset =
            static_cast<ULONG>(
                (static_cast<ULONGLONG>(
                     static_cast<ULONG>(Tick)) *
                 Context->TickBytes) %
                Context->RingSize);

        ULONG remainingInRing =
            Context->RingSize -
            ringOffset;

        ULONG firstChunk =
            Context->TickBytes <
                remainingInRing
                ? Context->TickBytes
                : remainingInRing;

        WDFDEVICE device =
            AcxCircuitGetWdfDevice(
                AcxStreamGetCircuit(
                    Stream));

        PCModeTransportPushPcm(
            device,
            Context->Format,
            buffer +
                ringOffset,
            firstChunk);

        if (firstChunk <
            Context->TickBytes)
        {
            PCModeTransportPushPcm(
                device,
                Context->Format,
                buffer,
                Context->TickBytes -
                    firstChunk);
        }
    }

    //
    // Position elapsed since the stream was last started or resumed,
    // expressed in bytes, per CStreamEngine::UpdatePosition in the
    // reference sample. Read at Pause() to save a resumable position, and
    // at EvtAcxStreamGetPresentationPosition.
    //
    VOID
    UpdatePosition(
        _In_ PCMODE_STREAM_CONTEXT* Context)
    {
        if (Context->State !=
            PCMODE_STREAM_STATE::Run)
        {
            return;
        }

        ULONG bytesPerSecond =
            AcxDataFormatGetAverageBytesPerSec(
                Context->Format);

        if (bytesPerSecond == 0)
        {
            return;
        }

        LONGLONG currentTime =
            KSCONVERT_PERFORMANCE_TIME(
                Context->PerformanceCounterFrequency
                    .QuadPart,
                KeQueryPerformanceCounter(
                    nullptr));

        LONGLONG elapsed =
            currentTime -
            Context->StartTime;

        if (elapsed < 0)
        {
            elapsed =
                0;
        }

        Context->Position =
            Context->StartPosition +
            static_cast<ULONGLONG>(
                elapsed) *
            bytesPerSecond /
            HnsPerSecond;
    }

    //
    // A late render timer represents audio time that has already passed.
    // Replaying every missed ring slice makes the virtual device lag real
    // time and eventually jump when the client catches up. Advance the ring
    // cursor to the latest slice whose deadline has elapsed, dropping only
    // the stale slices a physical device could no longer have rendered.
    //
    // Returns TRUE only on the leading edge of the stream falling behind --
    // i.e. once per outage, not once per timer tick still catching up from
    // it. The transport packet sequence stays contiguous across a skip like
    // this -- unlike a real Pause/Run transition, nothing here tells user
    // mode that part of the waveform was silently dropped -- so the caller
    // uses a TRUE return to signal the transport explicitly and let the
    // Host's jitter buffer re-prime instead of splicing the audio on either
    // side of the gap together as if it were continuous.
    //
    // A sustained stall (the render timer landing late tick after tick, not
    // just once) still updates CurrentPacket -- and still reports itself
    // behind -- on every one of those ticks, but only the first one returns
    // TRUE. Real-hardware testing during a display topology change showed
    // why this matters: signaling on every still-behind tick re-clears the
    // Host's queue and re-primes its jitter buffer on every one of them too,
    // which repeatedly restarts the recovery ramp this same stall is trying
    // to get through and measurably lengthens the audible gap rather than
    // shortening it. RenderBehind resets to FALSE below (or on the next
    // caught-up tick) so a later, genuinely new stall still signals.
    //
    BOOLEAN
    SkipMissedRenderSlices(
        _In_ PCMODE_STREAM_CONTEXT* Context)
    {
        if (Context->TickBytes == 0)
        {
            return FALSE;
        }

        UpdatePosition(
            Context);

        ULONGLONG completedTicks =
            Context->Position /
            Context->TickBytes;

        if (completedTicks == 0)
        {
            Context->RenderBehind =
                FALSE;

            return FALSE;
        }

        LONG latestDueTick =
            static_cast<LONG>(
                completedTicks -
                1);

        LONG current =
            InterlockedCompareExchange(
                &Context->CurrentPacket,
                0,
                0);

        if (latestDueTick <=
            current)
        {
            Context->RenderBehind =
                FALSE;

            return FALSE;
        }

        BOOLEAN alreadyBehind =
            Context->RenderBehind;

        Context->RenderBehind =
            TRUE;

        while (latestDueTick >
               current)
        {
            LONG observed =
                InterlockedCompareExchange(
                    &Context->CurrentPacket,
                    latestDueTick,
                    current);

            if (observed ==
                current)
            {
                break;
            }

            current =
                observed;
        }

        return !alreadyBehind;
    }

    //
    // Renders the current tick, advances the render timeline by exactly
    // one tick, and reports that tick complete to ACX every time -- see
    // CurrentPacket's comment in RenderStream.h for why this is one
    // counter notified on every tick rather than two counters at different
    // granularities: real-hardware testing showed the audio engine relies
    // on the notification's frequency, not just its content, to know when
    // to check position and write more data.
    //
    // Per CStreamEngine::StreamPassCallback: the tick processed is the
    // CURRENT value of CurrentPacket (not the incremented one), and the
    // tick reported complete to AcxRtStreamNotifyPacketComplete is the one
    // that was just processed -- CurrentPacket has already moved on to the
    // next one by the time the notification goes out.
    //
    VOID
    RenderCurrentPacketAndAdvance(
        _In_ PCMODE_STREAM_CONTEXT* Context,
        _In_ ACXSTREAM Stream)
    {
        LONG tickJustRendered =
            InterlockedCompareExchange(
                &Context->CurrentPacket,
                0,
                0);

        RenderRingSlice(
            Context,
            Stream,
            tickJustRendered);

        InterlockedIncrement(
            &Context->CurrentPacket);

        LARGE_INTEGER qpc =
            KeQueryPerformanceCounter(
                nullptr);

        (void)AcxRtStreamNotifyPacketComplete(
            Stream,
            static_cast<ULONGLONG>(
                tickJustRendered),
            static_cast<ULONGLONG>(
                qpc.QuadPart));
    }

    //
    // Computes the next tick's absolute deadline from the stream's
    // start-of-run anchor and arms a one-shot high-resolution timer for
    // exactly that delay, per CStreamEngine::ScheduleNextPass. Because the
    // deadline is computed from an absolute position (tick index times
    // TickBytes, converted through the format's real bytes-per-second)
    // rather than restarting a fixed-period relative timer every tick, this
    // does not accumulate the OS timer's ~15.6ms rounding error the way a
    // periodic relative timer does -- each tick is scheduled against where
    // it truly belongs in the stream's timeline, not "some fixed interval
    // after the last one fired."
    //
    // If that deadline has already passed, re-arm immediately. The timer
    // callback advances past stale slices before it renders; it never emits
    // a burst of old PCM to catch up.
    //
    VOID
    ScheduleNextPass(
        _In_ PCMODE_STREAM_CONTEXT* Context)
    {
        ULONG bytesPerSecond =
            AcxDataFormatGetAverageBytesPerSec(
                Context->Format);

        if (Context->TickBytes == 0 ||
            bytesPerSecond == 0)
        {
            //
            // Not yet in a renderable state (packets not allocated, or an
            // unresolved format). Retry shortly rather than spin or divide
            // by zero.
            //
            WdfTimerStart(
                Context->PacketTimer,
                WDF_REL_TIMEOUT_IN_MS(
                    10));

            return;
        }

        LONG nextTick =
            InterlockedCompareExchange(
                &Context->CurrentPacket,
                0,
                0) +
            1;

        ULONGLONG nextTickStartPosition =
            static_cast<ULONGLONG>(
                nextTick) *
            Context->TickBytes;

        LONGLONG nextTickPositionFromLastPause =
            static_cast<LONGLONG>(
                nextTickStartPosition) -
            static_cast<LONGLONG>(
                Context->StartPosition);

        LONGLONG nextTickTimeFromLastPauseHns =
            nextTickPositionFromLastPause *
            static_cast<LONGLONG>(
                HnsPerSecond) /
            bytesPerSecond;

        LONGLONG nextTickTime =
            Context->StartTime +
            nextTickTimeFromLastPauseHns;

        LONGLONG currentTime =
            KSCONVERT_PERFORMANCE_TIME(
                Context->PerformanceCounterFrequency
                    .QuadPart,
                KeQueryPerformanceCounter(
                    nullptr));

        //
        // Relative delay in HNS. Negative means "in the future" -- that
        // is what WdfTimerStart's DueTime expects for a relative wait.
        //
        LONGLONG delay =
            -(nextTickTime -
              currentTime);

        WdfTimerStart(
            Context->PacketTimer,
            delay < 0
                ? delay
                : -1);
    }
}

NTSTATUS
PCModeCreateRenderStream(
    _In_ WDFDEVICE Device,
    _In_ ACXCIRCUIT Circuit,
    _Inout_ PACXSTREAM_INIT StreamInit,
    _In_ ACXDATAFORMAT Format)
{
    PCMODE_TRACE_INFO(
        "CreateRenderStream: entered, format SampleRate=%lu Channels=%lu Bits=%lu BlockAlign=%lu",
        AcxDataFormatGetSampleRate(Format),
        AcxDataFormatGetChannelsCount(Format),
        AcxDataFormatGetBitsPerSample(Format),
        AcxDataFormatGetBlockAlign(Format));

    ACX_STREAM_CALLBACKS
        streamCallbacks;

    ACX_STREAM_CALLBACKS_INIT(
        &streamCallbacks);

    streamCallbacks
        .EvtAcxStreamPrepareHardware =
            PCModeEvtStreamPrepareHardware;

    streamCallbacks
        .EvtAcxStreamReleaseHardware =
            PCModeEvtStreamReleaseHardware;

    streamCallbacks
        .EvtAcxStreamRun =
            PCModeEvtStreamRun;

    streamCallbacks
        .EvtAcxStreamPause =
            PCModeEvtStreamPause;

    NTSTATUS status =
        AcxStreamInitAssignAcxStreamCallbacks(
            StreamInit,
            &streamCallbacks);

    if (!NT_SUCCESS(status))
    {
        PCMODE_TRACE_ERROR(
            "CreateRenderStream: AcxStreamInitAssignAcxStreamCallbacks failed 0x%08X",
            status);

        return status;
    }

    ACX_RT_STREAM_CALLBACKS
        rtCallbacks;

    ACX_RT_STREAM_CALLBACKS_INIT(
        &rtCallbacks);

    rtCallbacks
        .EvtAcxStreamGetHwLatency =
            PCModeEvtStreamGetHwLatency;

    rtCallbacks
        .EvtAcxStreamAllocateRtPackets =
            PCModeEvtStreamAllocateRtPackets;

    rtCallbacks
        .EvtAcxStreamFreeRtPackets =
            PCModeEvtStreamFreeRtPackets;

    rtCallbacks
        .EvtAcxStreamSetRenderPacket =
            PCModeEvtStreamSetRenderPacket;

    rtCallbacks
        .EvtAcxStreamGetCurrentPacket =
            PCModeEvtStreamGetCurrentPacket;

    rtCallbacks
        .EvtAcxStreamGetPresentationPosition =
            PCModeEvtStreamGetPresentationPosition;

    status =
        AcxStreamInitAssignAcxRtStreamCallbacks(
            StreamInit,
            &rtCallbacks);

    if (!NT_SUCCESS(status))
    {
        PCMODE_TRACE_ERROR(
            "CreateRenderStream: AcxStreamInitAssignAcxRtStreamCallbacks failed 0x%08X",
            status);

        return status;
    }

    AcxStreamInitSetAcxRtStreamSupportsNotifications(
        StreamInit);

    WDF_OBJECT_ATTRIBUTES attributes;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(
        &attributes,
        PCMODE_STREAM_CONTEXT);

    attributes.EvtCleanupCallback =
        PCModeEvtStreamContextCleanup;

    ACXSTREAM stream =
        nullptr;

    status =
        AcxRtStreamCreate(
            Device,
            Circuit,
            &attributes,
            &StreamInit,
            &stream);

    if (!NT_SUCCESS(status))
    {
        PCMODE_TRACE_ERROR(
            "CreateRenderStream: AcxRtStreamCreate failed 0x%08X",
            status);

        return status;
    }

    auto context =
        PCModeGetStreamContext(
            stream);

    RtlZeroMemory(
        context,
        sizeof(*context));

    context->Stream =
        stream;

    context->Format =
        Format;

    context->State =
        PCMODE_STREAM_STATE::Stop;

    KeQueryPerformanceCounter(
        &context->PerformanceCounterFrequency);

    //
    // High-resolution, one-shot: WDF_TIMER_CONFIG_INIT already configures a
    // one-shot timer (Period 0), matching how this timer is used -- each
    // callback computes the next tick's absolute deadline and re-arms
    // itself for exactly that delay (ScheduleNextPass), rather than firing
    // on a fixed period. UseHighResolutionTimer asks for sub-15.6ms
    // resolution, which the deadline math depends on to stay accurate.
    //
    WDF_TIMER_CONFIG
        timerConfig;

    WDF_TIMER_CONFIG_INIT(
        &timerConfig,
        PCModeEvtPacketTimer);

    timerConfig.UseHighResolutionTimer =
        WdfTrue;

    WDF_OBJECT_ATTRIBUTES
        timerAttributes;

    WDF_OBJECT_ATTRIBUTES_INIT(
        &timerAttributes);

    timerAttributes.ParentObject =
        stream;

    timerAttributes.ExecutionLevel =
        WdfExecutionLevelPassive;

    status =
        WdfTimerCreate(
            &timerConfig,
            &timerAttributes,
            &context->PacketTimer);

    if (!NT_SUCCESS(status))
    {
        PCMODE_TRACE_ERROR(
            "CreateRenderStream: WdfTimerCreate failed 0x%08X",
            status);

        return status;
    }

    PCMODE_TRACE_INFO(
        "%s",
        "CreateRenderStream: stream created successfully");

    PCModeTransportStreamStarted(
        Device,
        Format);

    InterlockedExchange(
        &context->TransportRegistered,
        TRUE);

    return STATUS_SUCCESS;
}

VOID
PCModeEvtStreamContextCleanup(
    _In_ WDFOBJECT Object)
{
    ACXSTREAM stream =
        reinterpret_cast<ACXSTREAM>(
            Object);

    auto context =
        PCModeGetStreamContext(
            stream);

    //
    // Defensive: ACX is expected to always drive Run -> Pause -> Stop
    // before tearing the stream object down, but an abnormal teardown
    // (e.g. surprise removal) is not something this driver should crash
    // over. TransportRunningRegistered/TransportRegistered are idempotent
    // latches independent of the state enum specifically so this can
    // unwind safely regardless of what state the stream was left in.
    //
    NotifyTransportRunningStopped(
        context,
        stream);

    if (InterlockedExchange(
            &context->TransportRegistered,
            FALSE))
    {
        PCModeTransportStreamStopped(
            AcxCircuitGetWdfDevice(
                AcxStreamGetCircuit(
                    stream)));
    }
}

NTSTATUS
PCModeEvtStreamAllocateRtPackets(
    _In_ ACXSTREAM Stream,
    _In_ ULONG PacketCount,
    _In_ ULONG PacketSize,
    _Out_ PACX_RTPACKET* Packets)
{
    PCMODE_TRACE_INFO(
        "AllocateRtPackets: requested PacketCount=%lu PacketSize=%lu",
        PacketCount,
        PacketSize);

    if (Packets == nullptr ||
        PacketCount == 0 ||
        PacketCount > PCMODE_MAX_RENDER_PACKETS ||
        PacketSize == 0)
    {
        PCMODE_TRACE_ERROR(
            "AllocateRtPackets: rejecting PacketCount=%lu PacketSize=%lu",
            PacketCount,
            PacketSize);

        return STATUS_INVALID_PARAMETER;
    }

    auto context =
        PCModeGetStreamContext(
            Stream);

    //
    // One shared buffer regardless of PacketCount, per the ACX allocation
    // contract: PacketCount==1 uses it as a single ring (RtPacketOffset
    // must be 0, and ACX itself maps it into user mode twice back-to-back
    // so the client can write across the wrap point without special
    // casing it); PacketCount==2 splits it into two adjacent regions that
    // share this one MDL -- packet 1 has no MDL of its own
    // (WdfMemoryDescriptorTypeInvalid) and starts right where packet 0
    // ends. Rounded up to a whole number of pages; any padding beyond
    // RingSize sits at the end and this driver never reads it.
    //
    ULONG ringSize = 0;

    NTSTATUS status =
        RtlULongMult(
            PacketSize,
            PacketCount,
            &ringSize);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    ULONG allocationSizeInPages = 0;

    status =
        RtlULongAdd(
            ringSize,
            PAGE_SIZE - 1,
            &allocationSizeInPages);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    allocationSizeInPages /=
        PAGE_SIZE;

    ULONG allocationSizeInBytes =
        PAGE_SIZE *
        allocationSizeInPages;

    void* buffer =
        ExAllocatePool2(
            POOL_FLAG_NON_PAGED,
            allocationSizeInBytes,
            PoolTag);

    if (buffer == nullptr)
    {
        return
            STATUS_INSUFFICIENT_RESOURCES;
    }

    RtlZeroMemory(
        buffer,
        allocationSizeInBytes);

    PMDL mdl =
        IoAllocateMdl(
            buffer,
            allocationSizeInBytes,
            FALSE,
            TRUE,
            nullptr);

    if (mdl == nullptr)
    {
        ExFreePoolWithTag(
            buffer,
            PoolTag);

        return
            STATUS_INSUFFICIENT_RESOURCES;
    }

    MmBuildMdlForNonPagedPool(
        mdl);

    size_t packetsArraySize = 0;

    status =
        RtlSizeTMult(
            PacketCount,
            sizeof(ACX_RTPACKET),
            &packetsArraySize);

    if (!NT_SUCCESS(status))
    {
        IoFreeMdl(
            mdl);

        ExFreePoolWithTag(
            buffer,
            PoolTag);

        return status;
    }

    auto packets =
        static_cast<PACX_RTPACKET>(
            ExAllocatePool2(
                POOL_FLAG_NON_PAGED,
                packetsArraySize,
                PoolTag));

    if (packets == nullptr)
    {
        IoFreeMdl(
            mdl);

        ExFreePoolWithTag(
            buffer,
            PoolTag);

        return
            STATUS_INSUFFICIENT_RESOURCES;
    }

    RtlZeroMemory(
        packets,
        packetsArraySize);

    ACX_RTPACKET_INIT(
        &packets[0]);

    packets[0]
        .RtPacketBuffer.Type =
            WdfMemoryDescriptorTypeMdl;

    packets[0]
        .RtPacketBuffer
        .u.MdlType.Mdl =
            mdl;

    packets[0]
        .RtPacketBuffer
        .u.MdlType.BufferLength =
            allocationSizeInBytes;

    packets[0].RtPacketOffset =
        0;

    packets[0].RtPacketSize =
        PacketSize;

    if (PacketCount == 2)
    {
        ACX_RTPACKET_INIT(
            &packets[1]);

        packets[1]
            .RtPacketBuffer.Type =
                WdfMemoryDescriptorTypeInvalid;

        packets[1].RtPacketOffset =
            PacketSize;

        packets[1].RtPacketSize =
            PacketSize;
    }

    context->RingMdl =
        mdl;

    context->RingSize =
        ringSize;

    context->PacketCount =
        PacketCount;

    context->PacketSize =
        PacketSize;

    context->TickBytes =
        ComputeTickBytes(
            PacketCount,
            PacketSize,
            ringSize,
            AcxDataFormatGetBlockAlign(
                context->Format));

    *Packets =
        packets;

    return STATUS_SUCCESS;
}

VOID
PCModeEvtStreamFreeRtPackets(
    _In_ ACXSTREAM Stream,
    _In_ PACX_RTPACKET Packets,
    _In_ ULONG PacketCount)
{
    UNREFERENCED_PARAMETER(
        PacketCount);

    auto context =
        PCModeGetStreamContext(
            Stream);

    if (context->RingMdl != nullptr)
    {
        void* buffer =
            MmGetMdlVirtualAddress(
                context->RingMdl);

        IoFreeMdl(
            context->RingMdl);

        if (buffer != nullptr)
        {
            ExFreePoolWithTag(
                buffer,
                PoolTag);
        }

        context->RingMdl =
            nullptr;
    }

    if (Packets != nullptr)
    {
        ExFreePoolWithTag(
            Packets,
            PoolTag);
    }

    context->RingSize =
        0;

    context->PacketCount =
        0;

    context->PacketSize =
        0;

    context->TickBytes =
        0;
}

NTSTATUS
PCModeEvtStreamPrepareHardware(
    _In_ ACXSTREAM Stream)
{
    PCMODE_TRACE_INFO(
        "%s",
        "PrepareHardware: invoked");

    auto context =
        PCModeGetStreamContext(
            Stream);

    if (context->State ==
        PCMODE_STREAM_STATE::Pause)
    {
        return STATUS_SUCCESS;
    }

    if (context->State !=
        PCMODE_STREAM_STATE::Stop)
    {
        return
            STATUS_INVALID_DEVICE_STATE;
    }

    context->State =
        PCMODE_STREAM_STATE::Pause;

    return STATUS_SUCCESS;
}

NTSTATUS
PCModeEvtStreamReleaseHardware(
    _In_ ACXSTREAM Stream)
{
    auto context =
        PCModeGetStreamContext(
            Stream);

    if (context->State ==
        PCMODE_STREAM_STATE::Stop)
    {
        return STATUS_SUCCESS;
    }

    //
    // ACX is expected to always Pause before releasing hardware. Handled
    // defensively rather than asserted: if a Run stream somehow reaches
    // here directly, stop the timer and notify the transport before
    // proceeding instead of leaving either running.
    //
    if (context->State ==
        PCMODE_STREAM_STATE::Run)
    {
        WdfTimerStop(
            context->PacketTimer,
            TRUE);

        NotifyTransportRunningStopped(
            context,
            Stream);
    }

    //
    // Full reset to a fresh timeline, ready for whenever this stream is
    // next prepared and run. Position/CurrentPacket are deliberately NOT
    // reset in PrepareHardware or Run -- only here, on the way down -- so
    // that a Pause/Run cycle preserves timeline continuity and only a true
    // stop starts over from zero.
    //
    context->Position =
        0;

    InterlockedExchange(
        &context->CurrentPacket,
        0);

    context->State =
        PCMODE_STREAM_STATE::Stop;

    return STATUS_SUCCESS;
}

NTSTATUS
PCModeEvtStreamRun(
    _In_ ACXSTREAM Stream)
{
    PCMODE_TRACE_INFO(
        "%s",
        "Run: invoked");

    auto context =
        PCModeGetStreamContext(
            Stream);

    if (context->State ==
        PCMODE_STREAM_STATE::Run)
    {
        return STATUS_SUCCESS;
    }

    if (context->State !=
        PCMODE_STREAM_STATE::Pause)
    {
        return
            STATUS_INVALID_DEVICE_STATE;
    }

    //
    // Save the time and position of this run/resume. If this stream ran
    // and paused before, StartPosition carries the position Pause() saved,
    // so the schedule and reported position continue from there instead of
    // jumping back to zero.
    //
    context->StartTime =
        KSCONVERT_PERFORMANCE_TIME(
            context->PerformanceCounterFrequency
                .QuadPart,
            KeQueryPerformanceCounter(
                nullptr));

    context->StartPosition =
        context->Position;

    context->RenderBehind =
        FALSE;

    context->State =
        PCMODE_STREAM_STATE::Run;

    InterlockedExchange(
        &context->TransportRunningRegistered,
        TRUE);

    PCModeTransportStreamRunningStarted(
        AcxCircuitGetWdfDevice(
            AcxStreamGetCircuit(
                Stream)));

    ScheduleNextPass(
        context);

    return STATUS_SUCCESS;
}

NTSTATUS
PCModeEvtStreamPause(
    _In_ ACXSTREAM Stream)
{
    auto context =
        PCModeGetStreamContext(
            Stream);

    if (context->State ==
        PCMODE_STREAM_STATE::Pause)
    {
        return STATUS_SUCCESS;
    }

    if (context->State !=
        PCMODE_STREAM_STATE::Run)
    {
        return
            STATUS_INVALID_DEVICE_STATE;
    }

    WdfTimerStop(
        context->PacketTimer,
        TRUE);

    //
    // Save the position at the instant of pause so Run() can resume from
    // here instead of restarting the timeline.
    //
    UpdatePosition(
        context);

    NotifyTransportRunningStopped(
        context,
        Stream);

    context->State =
        PCMODE_STREAM_STATE::Pause;

    return STATUS_SUCCESS;
}

NTSTATUS
PCModeEvtStreamSetRenderPacket(
    _In_ ACXSTREAM Stream,
    _In_ ULONG Packet,
    _In_ ULONG Flags,
    _In_ ULONG EosPacketLength)
{
    UNREFERENCED_PARAMETER(
        Flags);

    UNREFERENCED_PARAMETER(
        EosPacketLength);

    auto context =
        PCModeGetStreamContext(
            Stream);

    //
    // Only meaningful for PacketCount==2 (event-driven ping-pong), where
    // the client releases each packet explicitly. For PacketCount==1
    // (timer-driven) the client is flow-controlled entirely by presentation
    // position instead and this callback is not expected to fire at all --
    // see the ACX streaming docs' timer-driven buffering guidance.
    //
    // Pure validation of what the OS has written so far -- it does not
    // drive or gate the render timeline. That belongs to the packet timer
    // alone (RenderCurrentPacketAndAdvance/ScheduleNextPass), which is
    // free-running from the moment Run() starts it. Requiring the OS to
    // submit a packet before the driver would render past it is exactly
    // the deadlock the original implementation had: the OS asks what the
    // current packet is, sees 0, and will not write until the driver moves
    // past it, while the driver was waiting for that very write.
    //
    LONG current =
        InterlockedCompareExchange(
            &context->CurrentPacket,
            0,
            0);

    PCMODE_TRACE_INFO(
        "SetRenderPacket: Packet=%lu Flags=0x%lX EosPacketLength=%lu CurrentPacket=%ld",
        Packet,
        Flags,
        EosPacketLength,
        current);

    if (static_cast<LONG>(
            Packet) <= current)
    {
        return
            STATUS_DATA_LATE_ERROR;
    }

    if (static_cast<LONG>(
            Packet) >
        current + 1)
    {
        return
            STATUS_DATA_OVERRUN;
    }

    return STATUS_SUCCESS;
}

VOID
PCModeEvtPacketTimer(
    _In_ WDFTIMER Timer)
{
    ACXSTREAM stream =
        reinterpret_cast<ACXSTREAM>(
            WdfTimerGetParentObject(
                Timer));

    auto context =
        PCModeGetStreamContext(
            stream);

    if (context->State !=
        PCMODE_STREAM_STATE::Run)
    {
        return;
    }

    if (SkipMissedRenderSlices(
            context))
    {
        PCModeTransportSignalDiscontinuity(
            AcxCircuitGetWdfDevice(
                AcxStreamGetCircuit(
                    stream)));
    }

    RenderCurrentPacketAndAdvance(
        context,
        stream);

    if (context->State ==
        PCMODE_STREAM_STATE::Run)
    {
        ScheduleNextPass(
            context);
    }
}

NTSTATUS
PCModeEvtStreamGetCurrentPacket(
    _In_ ACXSTREAM Stream,
    _Out_ PULONG CurrentPacket)
{
    auto context =
        PCModeGetStreamContext(
            Stream);

    *CurrentPacket =
        static_cast<ULONG>(
            InterlockedCompareExchange(
                &context->CurrentPacket,
                0,
                0));

    return STATUS_SUCCESS;
}

NTSTATUS
PCModeEvtStreamGetPresentationPosition(
    _In_ ACXSTREAM Stream,
    _Out_ PULONGLONG PositionInBlocks,
    _Out_ PULONGLONG QPCPosition)
{
    auto context =
        PCModeGetStreamContext(
            Stream);

    UpdatePosition(
        context);

    ULONG blockAlign =
        AcxDataFormatGetBlockAlign(
            context->Format);

    *PositionInBlocks =
        blockAlign != 0
            ? context->Position / blockAlign
            : 0;

    LARGE_INTEGER qpc =
        KeQueryPerformanceCounter(
            nullptr);

    *QPCPosition =
        static_cast<ULONGLONG>(
            qpc.QuadPart);

    return STATUS_SUCCESS;
}

NTSTATUS
PCModeEvtStreamGetHwLatency(
    _In_ ACXSTREAM Stream,
    _Out_ PULONG FifoSize,
    _Out_ PULONG Delay)
{
    UNREFERENCED_PARAMETER(
        Stream);

    *FifoSize =
        0;

    *Delay =
        0;

    return STATUS_SUCCESS;
}

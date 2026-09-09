#include <wdm.h>
#include <windef.h>
#include <wdf.h>
#include <acx.h>

#include <ks.h>
#include <mmsystem.h>

#define NOBITMAP
#include <mmreg.h>
#include <ksmedia.h>

#include "UserModeTransport.h"
#include "Public.h"
#include "Trace.h"

EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL
TransportEvtIoDeviceControl;

namespace
{
    struct TRANSPORT_SLOT
    {
        BOOLEAN Valid;

        ULONGLONG Sequence;

        ULONGLONG Qpc;

        PCMODE_AUDIO_FORMAT Format;

        ULONG Bytes;

        UCHAR Data[PCMODE_AUDIO_MAX_PACKET_BYTES];
    };

    struct TRANSPORT_CONTEXT
    {
        WDFSPINLOCK Lock;

        WDFQUEUE Queue;

        BOOLEAN DeviceStarted;

        ULONG ActiveStreams;

        ULONG RunningStreams;

        WDFFILEOBJECT OwnerFileObject;

        ULONGLONG Sequence;

        ULONGLONG PacketsRendered;

        ULONGLONG BytesRendered;

        ULONGLONG PacketsDropped;

        ULONGLONG DiscontinuityCount;

        ULONG ReadIndex;

        ULONG WriteIndex;

        ULONG Count;

        PCMODE_AUDIO_FORMAT
        CurrentFormat;

        TRANSPORT_SLOT Slots[PCMODE_AUDIO_TRANSPORT_SLOT_COUNT];
    };

    WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(TRANSPORT_CONTEXT, GetTransportContext);

    void ConvertFormat(ACXDATAFORMAT Format, PCMODE_AUDIO_FORMAT* Output)
    {
        RtlZeroMemory(Output, sizeof(*Output));

        Output->Size = sizeof(*Output);

        Output->SampleRate = AcxDataFormatGetSampleRate(Format);

        Output->Channels = AcxDataFormatGetChannelsCount(Format);

        Output->BitsPerSample = AcxDataFormatGetBitsPerSample(Format);

        Output->BlockAlign = AcxDataFormatGetBlockAlign(Format);

        GUID subtype = AcxDataFormatGetSubFormat(Format);

        //
        // Explicit membership test for both formats this transport actually
        // understands, rather than treating "not float" as "must be PCM16":
        // the render circuit only ever registers those two subtypes today,
        // but silently mislabeling anything else as Pcm16 would have the
        // consumer reinterpret whatever bytes it actually is as 16-bit
        // samples -- audible corruption with no error raised anywhere.
        // Unknown makes DriverAudioInput.ConvertPacket fail loudly instead
        // (it throws on any format other than Float32/Pcm16).
        //
        if (IsEqualGUID(subtype, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT))
        {
            Output->SampleFormat = PCMODE_AUDIO_SAMPLE_FORMAT::Float32;
        }
        else if (IsEqualGUID(subtype, KSDATAFORMAT_SUBTYPE_PCM))
        {
            Output->SampleFormat = PCMODE_AUDIO_SAMPLE_FORMAT::Pcm16;
        }
        else
        {
            Output->SampleFormat = PCMODE_AUDIO_SAMPLE_FORMAT::Unknown;

            PCMODE_TRACE_ERROR("%s", "ConvertFormat: unrecognized sample format subtype, reporting Unknown");
        }
    }

    void FillPreferredFormat(PCMODE_AUDIO_FORMAT* Format)
    {
        RtlZeroMemory(Format, sizeof(*Format));

        Format->Size = sizeof(*Format);

        Format->SampleRate = 48000;

        Format->Channels = 2;

        Format->SampleFormat = PCMODE_AUDIO_SAMPLE_FORMAT::Float32;

        Format->BitsPerSample = 32;

        Format->BlockAlign = 8;
    }

    void ClearQueueLocked(TRANSPORT_CONTEXT* Context)
    {
        Context->ReadIndex = 0;
        Context->WriteIndex = 0;
        Context->Count = 0;

        for (ULONG i = 0; i < PCMODE_AUDIO_TRANSPORT_SLOT_COUNT; ++i)
        {
            Context->Slots[i].Valid = FALSE;

            Context->Slots[i].Bytes = 0;
        }
    }

    void FillStatus(TRANSPORT_CONTEXT* Context, PCMODE_AUDIO_STATUS* Status)
    {
        RtlZeroMemory(Status, sizeof(*Status));

        Status->Size = sizeof(*Status);

        Status->DeviceStarted = Context->DeviceStarted;

        Status->StreamRunning = Context->RunningStreams > 0 ? TRUE : FALSE;

        Status->UserModeConnected = Context->OwnerFileObject != nullptr ? TRUE : FALSE;

        Status->ActiveStreams = Context->ActiveStreams;

        Status->PacketsRendered = Context->PacketsRendered;

        Status->BytesRendered = Context->BytesRendered;

        Status->PacketsDropped = Context->PacketsDropped;

        Status->DiscontinuityCount = Context->DiscontinuityCount;

        Status->CurrentFormat = Context->CurrentFormat;
    }
} // namespace

NTSTATUS
PCModeTransportInitialize(_In_ WDFDEVICE Device)
{
    WDF_OBJECT_ATTRIBUTES attributes;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, TRANSPORT_CONTEXT);

    TRANSPORT_CONTEXT* context = nullptr;

    NTSTATUS status = WdfObjectAllocateContext(Device, &attributes, reinterpret_cast<void**>(&context));

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    RtlZeroMemory(context, sizeof(*context));

    WDF_OBJECT_ATTRIBUTES
    lockAttributes;

    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttributes);

    lockAttributes.ParentObject = Device;

    status = WdfSpinLockCreate(&lockAttributes, &context->Lock);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    WDF_IO_QUEUE_CONFIG queueConfig;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);

    queueConfig.EvtIoDeviceControl = TransportEvtIoDeviceControl;

    return WdfIoQueueCreate(Device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &context->Queue);
}

VOID PCModeTransportSetDeviceStarted(_In_ WDFDEVICE Device, _In_ BOOLEAN Started)
{
    auto context = GetTransportContext(Device);

    WdfSpinLockAcquire(context->Lock);

    context->DeviceStarted = Started;

    WdfSpinLockRelease(context->Lock);
}

VOID PCModeTransportStreamStarted(_In_ WDFDEVICE Device, _In_ ACXDATAFORMAT Format)
{
    auto context = GetTransportContext(Device);

    WdfSpinLockAcquire(context->Lock);

    context->ActiveStreams++;

    ConvertFormat(Format, &context->CurrentFormat);

    WdfSpinLockRelease(context->Lock);
}

VOID PCModeTransportStreamStopped(_In_ WDFDEVICE Device)
{
    auto context = GetTransportContext(Device);

    WdfSpinLockAcquire(context->Lock);

    if (context->ActiveStreams > 0)
    {
        context->ActiveStreams--;
    }

    if (context->ActiveStreams == 0)
    {
        context->RunningStreams = 0;

        RtlZeroMemory(&context->CurrentFormat, sizeof(context->CurrentFormat));

        //
        // Without this, any packets this now-dead stream already queued
        // stay sitting in the ring for whatever connects or starts next --
        // handed to a consumer as if they were fresh audio from a stream
        // that may have used a different format, ahead of anything the new
        // stream actually renders. The per-packet format tag means a
        // mismatch fails loudly downstream rather than corrupting silently,
        // but stale audio from a dead stream has no business being played
        // as if it were live regardless.
        //
        ClearQueueLocked(context);
    }

    WdfSpinLockRelease(context->Lock);
}

VOID PCModeTransportStreamRunningStarted(_In_ WDFDEVICE Device)
{
    auto context = GetTransportContext(Device);

    WdfSpinLockAcquire(context->Lock);

    context->RunningStreams++;

    WdfSpinLockRelease(context->Lock);
}

VOID PCModeTransportStreamRunningStopped(_In_ WDFDEVICE Device)
{
    auto context = GetTransportContext(Device);

    WdfSpinLockAcquire(context->Lock);

    if (context->RunningStreams > 0)
    {
        context->RunningStreams--;

        if (context->RunningStreams == 0)
        {
            // A Pause/Run transition is a real break in the source
            // timeline. Do not hand pre-pause packets to user mode, and
            // deliberately leave a sequence gap so the Host flushes its
            // own jitter cushion before accepting resumed PCM.
            ClearQueueLocked(context);

            context->Sequence++;
        }
    }

    WdfSpinLockRelease(context->Lock);
}

VOID PCModeTransportSignalDiscontinuity(_In_ WDFDEVICE Device)
{
    auto context = GetTransportContext(Device);

    WdfSpinLockAcquire(context->Lock);

    // Same treatment as a real Pause/Run transition (see
    // PCModeTransportStreamRunningStopped above): drop whatever is already
    // queued for user mode and deliberately leave a sequence gap so the
    // Host's jitter buffer sees the same discontinuity it would see across a
    // pause and re-primes instead of splicing stale audio onto what comes
    // next.
    ClearQueueLocked(context);

    context->Sequence++;

    context->DiscontinuityCount++;

    WdfSpinLockRelease(context->Lock);
}

VOID PCModeTransportReleaseConnection(_In_ WDFDEVICE Device, _In_ WDFFILEOBJECT FileObject)
{
    auto context = GetTransportContext(Device);

    WdfSpinLockAcquire(context->Lock);

    PCMODE_TRACE_INFO(
        "RELEASE: closing FileObject=%p, current OwnerFileObject=%p", FileObject, context->OwnerFileObject);

    if (context->OwnerFileObject == FileObject)
    {
        context->OwnerFileObject = nullptr;

        ClearQueueLocked(context);

        PCMODE_TRACE_INFO("%s", "RELEASE: matched, OwnerFileObject cleared");
    }
    else
    {
        PCMODE_TRACE_INFO("%s", "RELEASE: did not match the current owner, nothing cleared");
    }

    WdfSpinLockRelease(context->Lock);
}

VOID PCModeTransportPushPcm(_In_ WDFDEVICE Device,
                            _In_ ACXDATAFORMAT Format,
                            _In_reads_bytes_(Bytes) const void* Buffer,
                            _In_ ULONG Bytes)
{
    if (Buffer == nullptr || Bytes == 0)
    {
        return;
    }

    //
    // Not expected in practice at PCMode's real packet sizes (well under
    // this ceiling), but silently truncating and forwarding a partial
    // packet -- as if it were the whole thing -- would corrupt audio with
    // no signal anywhere that anything was wrong. Dropping the whole
    // oversized packet instead is the same "drop, don't corrupt" policy
    // already used for ring overflow below.
    //
    if (Bytes > PCMODE_AUDIO_MAX_PACKET_BYTES)
    {
        PCMODE_TRACE_ERROR(
            "PushPcm: rejecting oversized packet Bytes=%lu (max %lu)", Bytes, PCMODE_AUDIO_MAX_PACKET_BYTES);

        return;
    }

    auto context = GetTransportContext(Device);

    WdfSpinLockAcquire(context->Lock);

    context->PacketsRendered++;

    context->BytesRendered += Bytes;

    //
    // Do not preserve stale timeline data
    // when no user-mode consumer exists.
    //
    if (context->OwnerFileObject == nullptr)
    {
        WdfSpinLockRelease(context->Lock);

        return;
    }

    if (context->Count == PCMODE_AUDIO_TRANSPORT_SLOT_COUNT)
    {
        context->ReadIndex = (context->ReadIndex + 1) % PCMODE_AUDIO_TRANSPORT_SLOT_COUNT;

        context->Count--;

        context->PacketsDropped++;
    }

    auto& slot = context->Slots[context->WriteIndex];

    slot.Valid = TRUE;

    slot.Sequence = context->Sequence++;

    slot.Qpc = KeQueryPerformanceCounter(nullptr).QuadPart;

    ConvertFormat(Format, &slot.Format);

    slot.Bytes = Bytes;

    RtlCopyMemory(slot.Data, Buffer, Bytes);

    context->WriteIndex = (context->WriteIndex + 1) % PCMODE_AUDIO_TRANSPORT_SLOT_COUNT;

    context->Count++;

    WdfSpinLockRelease(context->Lock);
}

VOID TransportEvtIoDeviceControl(_In_ WDFQUEUE Queue,
                                 _In_ WDFREQUEST Request,
                                 _In_ size_t OutputBufferLength,
                                 _In_ size_t InputBufferLength,
                                 _In_ ULONG IoControlCode)
{
    UNREFERENCED_PARAMETER(InputBufferLength);

    WDFDEVICE device = WdfIoQueueGetDevice(Queue);

    auto context = GetTransportContext(device);

    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;

    size_t information = 0;

    switch (IoControlCode)
    {
        case IOCTL_PCMODE_AUDIO_GET_PROTOCOL:
        {
            PCMODE_AUDIO_PROTOCOL_INFO* result = nullptr;

            status =
                WdfRequestRetrieveOutputBuffer(Request, sizeof(*result), reinterpret_cast<void**>(&result), nullptr);

            if (!NT_SUCCESS(status))
            {
                break;
            }

            RtlZeroMemory(result, sizeof(*result));

            result->Size = sizeof(*result);

            result->Major = PCMODE_AUDIO_PROTOCOL_MAJOR;

            result->Minor = PCMODE_AUDIO_PROTOCOL_MINOR;

            result->Capabilities = PCMODE_AUDIO_CAPABILITY_STATUS | PCMODE_AUDIO_CAPABILITY_READ_PCM_IOCTL |
                                   PCMODE_AUDIO_CAPABILITY_SINGLE_CONSUMER | PCMODE_AUDIO_CAPABILITY_DROP_OLDEST |
                                   PCMODE_AUDIO_CAPABILITY_PCM16 | PCMODE_AUDIO_CAPABILITY_FLOAT32;

            result->MaxPacketBytes = PCMODE_AUDIO_MAX_PACKET_BYTES;

            result->TransportSlotCount = PCMODE_AUDIO_TRANSPORT_SLOT_COUNT;

            FillPreferredFormat(&result->PreferredFormat);

            information = sizeof(*result);

            status = STATUS_SUCCESS;

            break;
        }

        case IOCTL_PCMODE_AUDIO_GET_STATUS:
        {
            PCMODE_AUDIO_STATUS* result = nullptr;

            status =
                WdfRequestRetrieveOutputBuffer(Request, sizeof(*result), reinterpret_cast<void**>(&result), nullptr);

            if (!NT_SUCCESS(status))
            {
                break;
            }

            WdfSpinLockAcquire(context->Lock);

            FillStatus(context, result);

            WdfSpinLockRelease(context->Lock);

            information = sizeof(*result);

            status = STATUS_SUCCESS;

            break;
        }

        case IOCTL_PCMODE_AUDIO_CONNECT:
        {
            WDFFILEOBJECT fileObject = WdfRequestGetFileObject(Request);

            if (fileObject == nullptr)
            {
                status = STATUS_INVALID_DEVICE_STATE;

                break;
            }

            WdfSpinLockAcquire(context->Lock);

            PCMODE_TRACE_INFO(
                "CONNECT: request FileObject=%p, current OwnerFileObject=%p", fileObject, context->OwnerFileObject);

            if (context->OwnerFileObject != nullptr && context->OwnerFileObject != fileObject)
            {
                status = STATUS_DEVICE_BUSY;

                PCMODE_TRACE_INFO("%s", "CONNECT: refused, another FileObject already owns the transport");
            }
            else
            {
                context->OwnerFileObject = fileObject;

                ClearQueueLocked(context);

                status = STATUS_SUCCESS;

                PCMODE_TRACE_INFO("CONNECT: accepted, OwnerFileObject now %p", fileObject);
            }

            WdfSpinLockRelease(context->Lock);

            break;
        }

        case IOCTL_PCMODE_AUDIO_DISCONNECT:
        {
            WDFFILEOBJECT fileObject = WdfRequestGetFileObject(Request);

            if (fileObject == nullptr)
            {
                status = STATUS_INVALID_DEVICE_STATE;

                break;
            }

            PCModeTransportReleaseConnection(device, fileObject);

            status = STATUS_SUCCESS;

            break;
        }

        case IOCTL_PCMODE_AUDIO_READ_PCM:
        {
            if (OutputBufferLength < sizeof(PCMODE_AUDIO_PACKET_HEADER))
            {
                status = STATUS_BUFFER_TOO_SMALL;

                break;
            }

            WDFFILEOBJECT fileObject = WdfRequestGetFileObject(Request);

            if (fileObject == nullptr)
            {
                status = STATUS_INVALID_DEVICE_STATE;

                break;
            }

            void* output = nullptr;

            status = WdfRequestRetrieveOutputBuffer(Request, sizeof(PCMODE_AUDIO_PACKET_HEADER), &output, nullptr);

            if (!NT_SUCCESS(status))
            {
                break;
            }

            WdfSpinLockAcquire(context->Lock);

            if (context->OwnerFileObject != fileObject)
            {
                WdfSpinLockRelease(context->Lock);

                status = STATUS_ACCESS_DENIED;

                break;
            }

            if (context->Count == 0)
            {
                WdfSpinLockRelease(context->Lock);

                status = STATUS_NO_MORE_ENTRIES;

                break;
            }

            auto& slot = context->Slots[context->ReadIndex];

            const size_t required = sizeof(PCMODE_AUDIO_PACKET_HEADER) + slot.Bytes;

            if (OutputBufferLength < required)
            {
                WdfSpinLockRelease(context->Lock);

                status = STATUS_BUFFER_TOO_SMALL;

                break;
            }

            auto header = static_cast<PCMODE_AUDIO_PACKET_HEADER*>(output);

            header->Size = sizeof(*header);

            header->PayloadBytes = slot.Bytes;

            header->Sequence = slot.Sequence;

            header->Qpc = slot.Qpc;

            header->Format = slot.Format;

            RtlCopyMemory(reinterpret_cast<UCHAR*>(output) + sizeof(*header), slot.Data, slot.Bytes);

            slot.Valid = FALSE;

            slot.Bytes = 0;

            context->ReadIndex = (context->ReadIndex + 1) % PCMODE_AUDIO_TRANSPORT_SLOT_COUNT;

            context->Count--;

            WdfSpinLockRelease(context->Lock);

            information = required;

            status = STATUS_SUCCESS;

            break;
        }
    }

    WdfRequestCompleteWithInformation(Request, status, information);
}

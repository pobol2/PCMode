#include <wdm.h>
#include <windef.h>
#include <wdf.h>
#include <acx.h>

#include <ks.h>
#include <mmsystem.h>

#define NOBITMAP
#include <mmreg.h>
#include <ksmedia.h>

#include "RenderCircuit.h"
#include "RenderStream.h"
#include "Trace.h"

namespace
{
    const GUID PCMODE_RENDER_COMPONENT_GUID = {
        0xb54fc4c5, 0xa27c, 0x49e0, {0xb7, 0xaa, 0xe0, 0x87, 0x91, 0x54, 0xcf, 0x50}};

    //
    // 48 kHz stereo only, in both sample representations the transport
    // understands (PCM16, Float32). PCMode.Audio.DriverClient negotiates one
    // fixed nominal format with the driver (IOCTL_PCMODE_AUDIO_GET_PROTOCOL's
    // PreferredFormat, see UserModeTransport.cpp) and rejects any packet whose
    // sample rate does not match it -- see DriverAudioInput.ConvertPacket. A
    // raw format list that also advertised 44.1 kHz would let Windows (via a
    // shared-mode default format change in Sound settings, or an exclusive-mode
    // application) open a real, driver-accepted stream at a rate the consumer
    // was never able to accept, failing every packet from the moment such a
    // stream started. Advertising only the one rate the rest of the pipeline
    // actually handles makes that mismatch structurally impossible rather than
    // relying on format-conversion resampling to paper over it.
    //
    KSDATAFORMAT_WAVEFORMATEXTENSIBLE g_Pcm48000_16 = {
        {sizeof(KSDATAFORMAT_WAVEFORMATEXTENSIBLE),
         0,
         0,
         0,
         STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
         STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
         STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)},
        {{WAVE_FORMAT_EXTENSIBLE, 2, 48000, 48000 * 4, 4, 16, sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX)},
         16,
         KSAUDIO_SPEAKER_STEREO,
         STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM)}};

    KSDATAFORMAT_WAVEFORMATEXTENSIBLE g_Float48000 = {
        {sizeof(KSDATAFORMAT_WAVEFORMATEXTENSIBLE),
         0,
         0,
         0,
         STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
         STATICGUIDOF(KSDATAFORMAT_SUBTYPE_IEEE_FLOAT),
         STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)},
        {{WAVE_FORMAT_EXTENSIBLE, 2, 48000, 48000 * 8, 8, 32, sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX)},
         32,
         KSAUDIO_SPEAKER_STEREO,
         STATICGUIDOF(KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)}};

    NTSTATUS
    CreateFormat(WDFDEVICE Device, void* KsFormat, ACXDATAFORMAT* Format)
    {
        ACX_DATAFORMAT_CONFIG config;
        WDF_OBJECT_ATTRIBUTES attributes;

        ACX_DATAFORMAT_CONFIG_INIT_KS(&config, KsFormat);

        WDF_OBJECT_ATTRIBUTES_INIT(&attributes);

        return AcxDataFormatCreate(Device, &attributes, &config, Format);
    }
} // namespace

NTSTATUS
PCModeCreateRenderCircuit(_In_ WDFDEVICE Device, _Out_ ACXCIRCUIT* Circuit)
{
    *Circuit = nullptr;

    PACXCIRCUIT_INIT circuitInit = AcxCircuitInitAllocate(Device);

    if (circuitInit == nullptr)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    AcxCircuitInitSetComponentId(circuitInit, &PCMODE_RENDER_COMPONENT_GUID);

    UNICODE_STRING circuitName = RTL_CONSTANT_STRING(L"PCMode Audio");

    NTSTATUS status = AcxCircuitInitAssignName(circuitInit, &circuitName);

    if (!NT_SUCCESS(status))
    {
        AcxCircuitInitFree(circuitInit);
        return status;
    }

    AcxCircuitInitSetCircuitType(circuitInit, AcxCircuitTypeRender);

    ACX_CIRCUIT_PNPPOWER_CALLBACKS powerCallbacks;

    ACX_CIRCUIT_PNPPOWER_CALLBACKS_INIT(&powerCallbacks);

    powerCallbacks.EvtAcxCircuitPowerUp = PCModeEvtCircuitPowerUp;

    powerCallbacks.EvtAcxCircuitPowerDown = PCModeEvtCircuitPowerDown;

    AcxCircuitInitSetAcxCircuitPnpPowerCallbacks(circuitInit, &powerCallbacks);

    status = AcxCircuitInitAssignAcxCreateStreamCallback(circuitInit, PCModeEvtCircuitCreateStream);

    if (!NT_SUCCESS(status))
    {
        AcxCircuitInitFree(circuitInit);
        return status;
    }

    WDF_OBJECT_ATTRIBUTES circuitAttributes;

    WDF_OBJECT_ATTRIBUTES_INIT(&circuitAttributes);

    ACXCIRCUIT circuit = nullptr;

    status = AcxCircuitCreate(Device, &circuitAttributes, &circuitInit, &circuit);

    if (!NT_SUCCESS(status))
    {
        if (circuitInit != nullptr)
        {
            AcxCircuitInitFree(circuitInit);
        }

        return status;
    }

    ACXPIN pins[2] = {};

    //
    // Host/render input.
    //
    ACX_PIN_CONFIG pinConfig;
    WDF_OBJECT_ATTRIBUTES pinAttributes;

    ACX_PIN_CONFIG_INIT(&pinConfig);

    pinConfig.Type = AcxPinTypeSink;

    pinConfig.Communication = AcxPinCommunicationSink;

    pinConfig.Category = &KSCATEGORY_AUDIO;

    pinConfig.MaxStreams = 1;

    WDF_OBJECT_ATTRIBUTES_INIT(&pinAttributes);

    status = AcxPinCreate(circuit, &pinAttributes, &pinConfig, &pins[0]);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    //
    // Virtual speaker output.
    //
    ACX_PIN_CONFIG_INIT(&pinConfig);

    pinConfig.Type = AcxPinTypeSource;

    pinConfig.Communication = AcxPinCommunicationNone;

    pinConfig.Category = &KSNODETYPE_SPEAKER;

    WDF_OBJECT_ATTRIBUTES_INIT(&pinAttributes);

    status = AcxPinCreate(circuit, &pinAttributes, &pinConfig, &pins[1]);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    status = AcxCircuitAddPins(circuit, pins, ARRAYSIZE(pins));

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    ACXDATAFORMAT formats[2] = {};

    status = CreateFormat(Device, &g_Pcm48000_16, &formats[0]);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    status = CreateFormat(Device, &g_Float48000, &formats[1]);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    ACXDATAFORMATLIST formatList = AcxPinGetRawDataFormatList(pins[0]);

    if (formatList == nullptr)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    for (const auto format : formats)
    {
        status = AcxDataFormatListAddDataFormat(formatList, format);

        if (!NT_SUCCESS(status))
        {
            return status;
        }
    }

    status = AcxDataFormatListAssignDefaultDataFormat(formatList, formats[0]);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    *Circuit = circuit;

    PCMODE_TRACE_INFO("%s", "Render circuit created");

    return STATUS_SUCCESS;
}

NTSTATUS
PCModeEvtCircuitCreateStream(_In_ WDFDEVICE Device,
                             _In_ ACXCIRCUIT Circuit,
                             _In_ ACXPIN Pin,
                             _In_ PACXSTREAM_INIT StreamInit,
                             _In_ ACXDATAFORMAT StreamFormat,
                             _In_ const GUID* SignalProcessingMode,
                             _In_ ACXOBJECTBAG VarArguments)
{
    UNREFERENCED_PARAMETER(Pin);
    UNREFERENCED_PARAMETER(SignalProcessingMode);
    UNREFERENCED_PARAMETER(VarArguments);

    PCMODE_TRACE_INFO("%s", "CreateStream: EvtCircuitCreateStream invoked");

    NTSTATUS status = PCModeCreateRenderStream(Device, Circuit, StreamInit, StreamFormat);

    PCMODE_TRACE_INFO("CreateStream: PCModeCreateRenderStream returned 0x%08X", status);

    return status;
}

NTSTATUS
PCModeEvtCircuitPowerUp(_In_ WDFDEVICE Device, _In_ ACXCIRCUIT Circuit, _In_ WDF_POWER_DEVICE_STATE PreviousState)
{
    UNREFERENCED_PARAMETER(Device);
    UNREFERENCED_PARAMETER(Circuit);
    UNREFERENCED_PARAMETER(PreviousState);

    return STATUS_SUCCESS;
}

NTSTATUS
PCModeEvtCircuitPowerDown(_In_ WDFDEVICE Device, _In_ ACXCIRCUIT Circuit, _In_ WDF_POWER_DEVICE_STATE TargetState)
{
    UNREFERENCED_PARAMETER(Device);
    UNREFERENCED_PARAMETER(Circuit);
    UNREFERENCED_PARAMETER(TargetState);

    return STATUS_SUCCESS;
}

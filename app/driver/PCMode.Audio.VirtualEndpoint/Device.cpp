#include "Device.h"
#include "RenderCircuit.h"
#include "UserModeTransport.h"
#include "Public.h"
#include "Trace.h"

NTSTATUS
PCModeEvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    UNREFERENCED_PARAMETER(Driver);

    return PCModeCreateDevice(DeviceInit);
}

NTSTATUS
PCModeCreateDevice(
    _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    ACX_DEVICEINIT_CONFIG acxDeviceInit;
    ACX_DEVICE_CONFIG acxDeviceConfig;

    ACX_DEVICEINIT_CONFIG_INIT(
        &acxDeviceInit);

    NTSTATUS status =
        AcxDeviceInitInitialize(
            DeviceInit,
            &acxDeviceInit);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    WDF_PNPPOWER_EVENT_CALLBACKS pnpPowerCallbacks;

    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(
        &pnpPowerCallbacks);

    pnpPowerCallbacks.EvtDevicePrepareHardware =
        PCModeEvtDevicePrepareHardware;

    pnpPowerCallbacks.EvtDeviceReleaseHardware =
        PCModeEvtDeviceReleaseHardware;

    pnpPowerCallbacks.EvtDeviceD0Entry =
        PCModeEvtDeviceD0Entry;

    pnpPowerCallbacks.EvtDeviceD0Exit =
        PCModeEvtDeviceD0Exit;

    WdfDeviceInitSetPnpPowerEventCallbacks(
        DeviceInit,
        &pnpPowerCallbacks);

    WDF_FILEOBJECT_CONFIG fileObjectConfig;

    WDF_FILEOBJECT_CONFIG_INIT(
        &fileObjectConfig,
        PCModeEvtDeviceFileCreate,
        WDF_NO_EVENT_CALLBACK,
        PCModeEvtFileCleanup);

    WdfDeviceInitSetFileObjectConfig(
        DeviceInit,
        &fileObjectConfig,
        WDF_NO_OBJECT_ATTRIBUTES);

    WDF_OBJECT_ATTRIBUTES attributes;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(
        &attributes,
        PCMODE_DEVICE_CONTEXT);

    WDFDEVICE device = nullptr;

    status =
        WdfDeviceCreate(
            &DeviceInit,
            &attributes,
            &device);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    auto deviceContext =
        PCModeGetDeviceContext(device);

    deviceContext->RenderCircuit = nullptr;
    deviceContext->CircuitAdded = FALSE;
    deviceContext->Started = FALSE;

    ACX_DEVICE_CONFIG_INIT(
        &acxDeviceConfig);

    status =
        AcxDeviceInitialize(
            device,
            &acxDeviceConfig);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    status =
        WdfDeviceCreateDeviceInterface(
            device,
            &GUID_DEVINTERFACE_PCMODE_AUDIO,
            nullptr);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    status =
        PCModeTransportInitialize(device);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    status =
        PCModeCreateRenderCircuit(
            device,
            &deviceContext->RenderCircuit);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    PCMODE_TRACE_INFO(
        "%s",
        "Device created");

    return STATUS_SUCCESS;
}

NTSTATUS
PCModeEvtDevicePrepareHardware(
    _In_ WDFDEVICE Device,
    _In_ WDFCMRESLIST ResourcesRaw,
    _In_ WDFCMRESLIST ResourcesTranslated)
{
    UNREFERENCED_PARAMETER(ResourcesRaw);
    UNREFERENCED_PARAMETER(ResourcesTranslated);

    auto context =
        PCModeGetDeviceContext(Device);

    if (!context->CircuitAdded)
    {
        NTSTATUS status =
            AcxDeviceAddCircuit(
                Device,
                context->RenderCircuit);

        if (!NT_SUCCESS(status))
        {
            return status;
        }

        context->CircuitAdded = TRUE;
    }

    return STATUS_SUCCESS;
}

NTSTATUS
PCModeEvtDeviceReleaseHardware(
    _In_ WDFDEVICE Device,
    _In_ WDFCMRESLIST ResourcesTranslated)
{
    UNREFERENCED_PARAMETER(ResourcesTranslated);

    auto context =
        PCModeGetDeviceContext(Device);

    if (context->CircuitAdded)
    {
        const NTSTATUS status =
            AcxDeviceRemoveCircuit(
                Device,
                context->RenderCircuit);

        if (!NT_SUCCESS(status))
        {
            return status;
        }

        context->CircuitAdded = FALSE;
    }

    return STATUS_SUCCESS;
}

NTSTATUS
PCModeEvtDeviceD0Entry(
    _In_ WDFDEVICE Device,
    _In_ WDF_POWER_DEVICE_STATE PreviousState)
{
    UNREFERENCED_PARAMETER(PreviousState);

    auto context =
        PCModeGetDeviceContext(Device);

    context->Started = TRUE;

    PCModeTransportSetDeviceStarted(
        Device,
        TRUE);

    return STATUS_SUCCESS;
}

NTSTATUS
PCModeEvtDeviceD0Exit(
    _In_ WDFDEVICE Device,
    _In_ WDF_POWER_DEVICE_STATE TargetState)
{
    UNREFERENCED_PARAMETER(TargetState);

    auto context =
        PCModeGetDeviceContext(Device);

    context->Started = FALSE;

    PCModeTransportSetDeviceStarted(
        Device,
        FALSE);

    return STATUS_SUCCESS;
}

VOID
PCModeEvtDeviceFileCreate(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request,
    _In_ WDFFILEOBJECT FileObject)
{
    UNREFERENCED_PARAMETER(Device);

    PCMODE_TRACE_INFO(
        "CREATE: FileObject=%p",
        FileObject);

    WdfRequestComplete(
        Request,
        STATUS_SUCCESS);
}

VOID
PCModeEvtFileCleanup(
    _In_ WDFFILEOBJECT FileObject)
{
    //
    // Release the PCM transport claim when the owning
    // handle closes (including process exit/crash), so a
    // restarted Host can reconnect instead of permanently
    // observing STATUS_DEVICE_BUSY.
    //
    PCMODE_TRACE_INFO(
        "CLEANUP: EvtFileCleanup fired for FileObject=%p",
        FileObject);

    WDFDEVICE device =
        WdfFileObjectGetDevice(
            FileObject);

    PCMODE_TRACE_INFO(
        "CLEANUP: WdfFileObjectGetDevice returned Device=%p",
        device);

    PCModeTransportReleaseConnection(
        device,
        FileObject);
}
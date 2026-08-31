#include "Driver.h"
#include "Device.h"
#include "Trace.h"

extern "C"
NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG driverConfig;
    WDF_OBJECT_ATTRIBUTES attributes;
    ACX_DRIVER_CONFIG acxConfig;

    WDF_DRIVER_CONFIG_INIT(
        &driverConfig,
        PCModeEvtDeviceAdd);

    WDF_OBJECT_ATTRIBUTES_INIT(
        &attributes);

    attributes.EvtCleanupCallback =
        PCModeEvtDriverContextCleanup;

    WDFDRIVER driver =
        nullptr;

    NTSTATUS status =
        WdfDriverCreate(
            DriverObject,
            RegistryPath,
            &attributes,
            &driverConfig,
            &driver);

    if (!NT_SUCCESS(status))
    {
        return status;
    }

    ACX_DRIVER_CONFIG_INIT(
        &acxConfig);

    status =
        AcxDriverInitialize(
            driver,
            &acxConfig);

    if (!NT_SUCCESS(status))
    {
        PCMODE_TRACE_ERROR(
            "AcxDriverInitialize failed: 0x%08X",
            status);

        return status;
    }

    PCMODE_TRACE_INFO(
        "%s",
        "Driver initialized");

    return STATUS_SUCCESS;
}

VOID
PCModeEvtDriverContextCleanup(
    _In_ WDFOBJECT DriverObject)
{
    UNREFERENCED_PARAMETER(
        DriverObject);

    PCMODE_TRACE_INFO(
        "%s",
        "Driver cleanup");
}
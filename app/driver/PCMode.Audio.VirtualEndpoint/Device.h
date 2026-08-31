#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <acx.h>

struct PCMODE_DEVICE_CONTEXT
{
    ACXCIRCUIT RenderCircuit;

    BOOLEAN CircuitAdded;
    BOOLEAN Started;
};

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(
    PCMODE_DEVICE_CONTEXT,
    PCModeGetDeviceContext);

EVT_WDF_DEVICE_PREPARE_HARDWARE
    PCModeEvtDevicePrepareHardware;

EVT_WDF_DEVICE_RELEASE_HARDWARE
    PCModeEvtDeviceReleaseHardware;

EVT_WDF_DEVICE_D0_ENTRY
    PCModeEvtDeviceD0Entry;

EVT_WDF_DEVICE_D0_EXIT
    PCModeEvtDeviceD0Exit;

EVT_WDF_FILE_CLEANUP
    PCModeEvtFileCleanup;

EVT_WDF_DEVICE_FILE_CREATE
    PCModeEvtDeviceFileCreate;

NTSTATUS
PCModeCreateDevice(
    _Inout_ PWDFDEVICE_INIT DeviceInit);
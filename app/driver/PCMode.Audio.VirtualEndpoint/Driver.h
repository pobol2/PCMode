#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <acx.h>

extern "C" DRIVER_INITIALIZE DriverEntry;

EVT_WDF_DRIVER_DEVICE_ADD
    PCModeEvtDeviceAdd;

EVT_WDF_OBJECT_CONTEXT_CLEANUP
    PCModeEvtDriverContextCleanup;
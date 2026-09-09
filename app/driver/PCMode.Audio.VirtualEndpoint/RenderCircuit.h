#pragma once

#include <wdm.h>
#include <windef.h>
#include <wdf.h>
#include <acx.h>

EVT_ACX_CIRCUIT_CREATE_STREAM
PCModeEvtCircuitCreateStream;

EVT_ACX_CIRCUIT_POWER_UP
PCModeEvtCircuitPowerUp;

EVT_ACX_CIRCUIT_POWER_DOWN
PCModeEvtCircuitPowerDown;

NTSTATUS
PCModeCreateRenderCircuit(_In_ WDFDEVICE Device, _Out_ ACXCIRCUIT* Circuit);

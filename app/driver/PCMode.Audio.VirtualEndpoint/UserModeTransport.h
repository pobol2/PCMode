#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <acx.h>

NTSTATUS
PCModeTransportInitialize(_In_ WDFDEVICE Device);

VOID PCModeTransportSetDeviceStarted(_In_ WDFDEVICE Device, _In_ BOOLEAN Started);

VOID PCModeTransportStreamStarted(_In_ WDFDEVICE Device, _In_ ACXDATAFORMAT Format);

VOID PCModeTransportStreamStopped(_In_ WDFDEVICE Device);

VOID PCModeTransportStreamRunningStarted(_In_ WDFDEVICE Device);

VOID PCModeTransportStreamRunningStopped(_In_ WDFDEVICE Device);

//
// Signals a break in the render timeline that happened without a Pause/Run
// transition -- specifically, a late render-timer callback skipping one or
// more WaveRT slices to catch up to real time (see SkipMissedRenderSlices in
// RenderStream.cpp). The skipped bytes were never rendered, so anything
// already queued for user mode, and anything the Host has already buffered
// from it, is no longer contiguous with what comes next.
//
VOID PCModeTransportSignalDiscontinuity(_In_ WDFDEVICE Device);

VOID PCModeTransportPushPcm(_In_ WDFDEVICE Device,
                            _In_ ACXDATAFORMAT Format,
                            _In_reads_bytes_(Bytes) const void* Buffer,
                            _In_ ULONG Bytes);

VOID PCModeTransportReleaseConnection(_In_ WDFDEVICE Device, _In_ WDFFILEOBJECT FileObject);

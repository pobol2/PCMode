#pragma once

#include <ntddk.h>

#define PCMODE_TRACE_INFO(_fmt_, ...) \
    DbgPrintEx( \
        DPFLTR_IHVDRIVER_ID, \
        DPFLTR_INFO_LEVEL, \
        "[PCMode.Audio] " _fmt_ "\n", \
        __VA_ARGS__)

#define PCMODE_TRACE_ERROR(_fmt_, ...) \
    DbgPrintEx( \
        DPFLTR_IHVDRIVER_ID, \
        DPFLTR_ERROR_LEVEL, \
        "[PCMode.Audio] ERROR: " _fmt_ "\n", \
        __VA_ARGS__)
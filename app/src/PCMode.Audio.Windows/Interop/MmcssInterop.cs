using System.Runtime.InteropServices;

namespace PCMode.Audio.Windows.Interop;

internal enum AvrtPriority
{
    Low = -1,
    Normal = 0,
    High = 1,
    Critical = 2
}

internal static class MmcssInterop
{
    [DllImport(
        "avrt.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    internal static extern IntPtr AvSetMmThreadCharacteristicsW(
        string taskName,
        ref uint taskIndex);

    [DllImport(
        "avrt.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AvSetMmThreadPriority(
        IntPtr avrtHandle,
        AvrtPriority priority);

    [DllImport(
        "avrt.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AvRevertMmThreadCharacteristics(
        IntPtr avrtHandle);
}
using System.ComponentModel;
using PCMode.Audio.Windows.Interop;

namespace PCMode.Audio.Windows.Threading;

public sealed class MmcssRegistration :
    IDisposable
{
    private IntPtr _handle;

    private readonly int _managedThreadId;

    private MmcssRegistration(
        IntPtr handle)
    {
        _handle = handle;

        _managedThreadId =
            Environment.CurrentManagedThreadId;
    }

    public static MmcssRegistration RegisterProAudio()
    {
        uint taskIndex = 0;

        IntPtr handle =
            MmcssInterop.AvSetMmThreadCharacteristicsW(
                "Pro Audio",
                ref taskIndex);

        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(
                "AvSetMmThreadCharacteristicsW failed.");
        }

        if (!MmcssInterop.AvSetMmThreadPriority(
                handle,
                AvrtPriority.High))
        {
            int error =
                System.Runtime.InteropServices.Marshal
                    .GetLastWin32Error();

            MmcssInterop
                .AvRevertMmThreadCharacteristics(
                    handle);

            throw new Win32Exception(
                error,
                "AvSetMmThreadPriority failed.");
        }

        return new MmcssRegistration(
            handle);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        if (Environment.CurrentManagedThreadId !=
            _managedThreadId)
        {
            throw new InvalidOperationException(
                "MMCSS registration must be released on the thread that registered it.");
        }

        if (!MmcssInterop
                .AvRevertMmThreadCharacteristics(
                    _handle))
        {
            throw new Win32Exception(
                "AvRevertMmThreadCharacteristics failed.");
        }

        _handle = IntPtr.Zero;
    }
}
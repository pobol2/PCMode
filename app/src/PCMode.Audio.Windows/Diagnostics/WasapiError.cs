using System.Runtime.InteropServices;

namespace PCMode.Audio.Windows.Interop;

internal static class WasapiError
{
    public static void ThrowIfFailed(
        int hresult,
        string operation)
    {
        if (hresult >= 0)
        {
            return;
        }

        throw new COMException(
            $"{operation} failed with HRESULT 0x{hresult:X8}.",
            hresult);
    }
}
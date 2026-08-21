using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PCMode.Engine;

/// <summary>
/// Encrypts a small secret so that only this Windows user, on this machine, can
/// read it back.
/// </summary>
/// <remarks>
/// P/Invoked rather than taken from a package, because
/// <c>System.Security.Cryptography.ProtectedData</c> is a NuGet dependency and
/// this is two functions. The engine has no package references at all, and adding
/// one to reach an API that ships in the operating system it already targets
/// would be paying a supply chain for a DllImport.
///
/// UI is forbidden explicitly. These calls can be made to prompt, and a tray
/// application part way through a mode transition is the worst possible moment for
/// a dialog to appear behind whatever is full-screen. Failure comes back as an
/// exception, which the caller is written to treat as "there is no key".
/// </remarks>
internal static class WindowsDpapi
{
    private const uint CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] value, byte[] entropy) =>
        Transform(value, entropy, protect: true);

    public static byte[] Unprotect(byte[] value, byte[] entropy) =>
        Transform(value, entropy, protect: false);

    private static byte[] Transform(byte[] value, byte[] entropy, bool protect)
    {
        DataBlob input = Allocate(value);
        DataBlob optionalEntropy = Allocate(entropy);
        DataBlob output = default;
        nint description = nint.Zero;

        try
        {
            bool succeeded = protect
                ? CryptProtectData(
                    ref input,
                    "PCMode television pairing key",
                    ref optionalEntropy,
                    nint.Zero,
                    nint.Zero,
                    CryptProtectUiForbidden,
                    out output)
                : CryptUnprotectData(
                    ref input,
                    out description,
                    ref optionalEntropy,
                    nint.Zero,
                    nint.Zero,
                    CryptProtectUiForbidden,
                    out output);

            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            byte[] result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            Free(input);
            Free(optionalEntropy);

            if (output.Data != nint.Zero)
            {
                // The plaintext pairing key, on a decrypt: zeroed before the
                // free, not just after, so it does not sit readable in
                // reclaimed process heap.
                ZeroFill(output.Data, output.Size);
                _ = LocalFree(output.Data);
            }

            if (description != nint.Zero)
            {
                _ = LocalFree(description);
            }
        }
    }

    private static DataBlob Allocate(byte[] value)
    {
        nint memory = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, memory, value.Length);

        return new DataBlob { Size = value.Length, Data = memory };
    }

    private static void Free(DataBlob blob)
    {
        if (blob.Data != nint.Zero)
        {
            // The plaintext pairing key, on an encrypt: same reasoning as the
            // output blob below.
            ZeroFill(blob.Data, blob.Size);
            Marshal.FreeHGlobal(blob.Data);
        }
    }

    private static void ZeroFill(nint pointer, int length)
    {
        for (int index = 0; index < length; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        nint reserved,
        nint prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out nint description,
        ref DataBlob optionalEntropy,
        nint reserved,
        nint prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}

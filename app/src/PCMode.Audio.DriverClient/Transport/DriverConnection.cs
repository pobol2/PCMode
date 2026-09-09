using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PCMode.Audio.Core.Models;
using PCMode.Audio.DriverClient.Diagnostics;
using PCMode.Audio.DriverClient.Protocol;

namespace PCMode.Audio.DriverClient.Transport;

internal interface IDriverConnection : IDisposable
{
    bool IsConnected { get; }

    AudioFormat PreferredFormat { get; }

    int MaximumPacketBytes { get; }

    void Connect();

    void Disconnect();

    bool TryReadPacket(out DriverPacket packet);

    DriverStatus GetStatus();
}

public sealed class DriverUnavailableException : IOException
{
    public DriverUnavailableException(string message)
        : base(message) { }
}

public sealed class DriverConnection : IDriverConnection
{
    private const uint DigcfPresent = 0x00000002;

    private const uint DigcfDeviceInterface = 0x00000010;

    private const uint GenericRead = 0x80000000;

    private const uint FileShareRead = 0x00000001;

    private const uint FileShareWrite = 0x00000002;

    private const uint OpenExisting = 3;

    private const int ErrorNoMoreItems = 259;

    private const int ErrorInsufficientBuffer = 122;

    private readonly bool _claimPcmTransport;

    private readonly object _syncRoot = new();

    private SafeFileHandle? _handle;

    private NativeDriverProtocolInfo _protocol;

    private byte[]? _packetBuffer;

    private bool _disposed;

    public bool IsConnected
    {
        get
        {
            lock (_syncRoot)
            {
                return IsConnectedUnsynchronized();
            }
        }
    }

    public AudioFormat PreferredFormat
    {
        get
        {
            lock (_syncRoot)
            {
                EnsureConnected();

                return DriverProtocol.ToAudioFormat(_protocol.PreferredFormat);
            }
        }
    }

    public int MaximumPacketBytes
    {
        get
        {
            lock (_syncRoot)
            {
                EnsureConnected();

                return checked((int)_protocol.MaxPacketBytes);
            }
        }
    }

    public DriverConnection(bool claimPcmTransport = true)
    {
        _claimPcmTransport = claimPcmTransport;
    }

    public void Connect()
    {
        lock (_syncRoot)
        {
            ConnectUnsynchronized();
        }
    }

    private void ConnectUnsynchronized()
    {
        ThrowIfDisposed();

        if (IsConnectedUnsynchronized())
        {
            return;
        }

        string path = FindDeviceInterfacePath();

        SafeFileHandle handle = CreateFile(
            path,
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero
        );

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();

            handle.Dispose();

            throw new IOException($"Failed to open PCMode Audio driver interface '{path}'.", new Win32Exception(error));
        }

        try
        {
            _handle = handle;

            _protocol = InvokeStruct<NativeDriverProtocolInfo>(DriverProtocol.IoctlGetProtocol);

            DriverCapabilities capabilities = (DriverCapabilities)_protocol.Capabilities;

            DriverProtocol.Validate(_protocol.Major, _protocol.Minor, capabilities);

            if (_protocol.MaxPacketBytes == 0 || _protocol.MaxPacketBytes > 1024 * 1024)
            {
                throw new DriverProtocolException("Driver returned an invalid maximum packet size.");
            }

            if (_claimPcmTransport)
            {
                InvokeNoOutput(DriverProtocol.IoctlConnect);
            }

            int headerSize = Marshal.SizeOf<NativeDriverPacketHeader>();

            _packetBuffer = new byte[checked(headerSize + (int)_protocol.MaxPacketBytes)];
        }
        catch
        {
            DisconnectUnsynchronized();

            throw;
        }
    }

    public void Disconnect()
    {
        lock (_syncRoot)
        {
            DisconnectUnsynchronized();
        }
    }

    private void DisconnectUnsynchronized()
    {
        SafeFileHandle? handle = _handle;

        // Released explicitly, while the handle is still open, rather than
        // left for the driver to notice on handle close: that notification
        // (EvtFileCleanup) does not reliably reach it, and without this the
        // transport claim would survive every ordinary disconnect and
        // refuse the next one as busy.
        if (_claimPcmTransport && handle is { IsClosed: false, IsInvalid: false })
        {
            try
            {
                InvokeNoOutput(DriverProtocol.IoctlDisconnect);
            }
            catch { }
        }

        _handle = null;

        handle?.Dispose();

        _packetBuffer = null;

        _protocol = default;
    }

    public DriverStatus GetStatus()
    {
        lock (_syncRoot)
        {
            return GetStatusUnsynchronized();
        }
    }

    private DriverStatus GetStatusUnsynchronized()
    {
        ThrowIfDisposed();

        try
        {
            EnsureConnected();

            NativeDriverStatus native = InvokeStruct<NativeDriverStatus>(DriverProtocol.IoctlGetStatus);

            AudioFormat? currentFormat =
                native.CurrentFormat.SampleRate > 0 && native.CurrentFormat.Channels > 0
                    ? DriverProtocol.ToAudioFormat(native.CurrentFormat)
                    : null;

            return new DriverStatus(
                Available: true,
                DeviceStarted: native.DeviceStarted != 0,
                StreamRunning: native.StreamRunning != 0,
                PcmConsumerConnected: native.UserModeConnected != 0,
                ActiveStreams: native.ActiveStreams,
                ProtocolMajor: _protocol.Major,
                ProtocolMinor: _protocol.Minor,
                Capabilities: (DriverCapabilities)_protocol.Capabilities,
                PreferredFormat: DriverProtocol.ToAudioFormat(_protocol.PreferredFormat),
                CurrentFormat: currentFormat,
                PacketsRendered: native.PacketsRendered,
                BytesRendered: native.BytesRendered,
                PacketsDropped: native.PacketsDropped,
                DiscontinuityCount: native.DiscontinuityCount,
                Failure: null
            );
        }
        catch (IOException ex)
        {
            DisconnectUnsynchronized();

            return DriverStatus.Unavailable(ex.Message);
        }
    }

    internal bool TryReadPacket(out DriverPacket packet)
    {
        return ((IDriverConnection)this).TryReadPacket(out packet);
    }

    bool IDriverConnection.TryReadPacket(out DriverPacket packet)
    {
        lock (_syncRoot)
        {
            return TryReadPacketUnsynchronized(out packet);
        }
    }

    private bool TryReadPacketUnsynchronized(out DriverPacket packet)
    {
        ThrowIfDisposed();

        if (!_claimPcmTransport)
        {
            throw new InvalidOperationException("This driver connection does not own the PCM transport.");
        }

        EnsureConnected();

        byte[] buffer =
            _packetBuffer ?? throw new InvalidOperationException("Driver packet buffer is not initialized.");

        bool success = DeviceIoControl(
            _handle!,
            DriverProtocol.IoctlReadPcm,
            IntPtr.Zero,
            0,
            buffer,
            buffer.Length,
            out int bytesReturned,
            IntPtr.Zero
        );

        if (!success)
        {
            int error = Marshal.GetLastWin32Error();

            if (error == ErrorNoMoreItems)
            {
                packet = default;

                return false;
            }

            DisconnectUnsynchronized();

            throw new IOException("PCMode Audio driver PCM read failed.", new Win32Exception(error));
        }

        int headerSize = Marshal.SizeOf<NativeDriverPacketHeader>();

        if (bytesReturned < headerSize)
        {
            throw new DriverProtocolException("Driver returned a truncated PCM packet.");
        }

        NativeDriverPacketHeader header = MemoryMarshal.Read<NativeDriverPacketHeader>(buffer.AsSpan(0, headerSize));

        if (header.Size != headerSize)
        {
            throw new DriverProtocolException("Driver returned an incompatible PCM packet header.");
        }

        int payloadBytes = checked((int)header.PayloadBytes);

        if (payloadBytes > _protocol.MaxPacketBytes || payloadBytes > bytesReturned - headerSize)
        {
            throw new DriverProtocolException("Driver returned an invalid PCM packet size.");
        }

        packet = new DriverPacket(
            header.Sequence,
            header.Qpc,
            header.Format,
            buffer.AsMemory(headerSize, payloadBytes)
        );

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Disconnect();

        _disposed = true;
    }

    private bool IsConnectedUnsynchronized()
    {
        return _handle is not null && !_handle.IsInvalid && !_handle.IsClosed;
    }

    private void EnsureConnected()
    {
        if (!IsConnectedUnsynchronized())
        {
            ConnectUnsynchronized();
        }
    }

    private T InvokeStruct<T>(uint ioctl)
        where T : unmanaged
    {
        int size = Marshal.SizeOf<T>();

        byte[] output = new byte[size];

        bool success = DeviceIoControl(
            _handle!,
            ioctl,
            IntPtr.Zero,
            0,
            output,
            output.Length,
            out int returned,
            IntPtr.Zero
        );

        if (!success)
        {
            throw CreateIoException($"Driver IOCTL 0x{ioctl:X8} failed.");
        }

        if (returned < size)
        {
            throw new DriverProtocolException(
                $"Driver IOCTL 0x{ioctl:X8} returned {returned} bytes instead of {size}."
            );
        }

        return MemoryMarshal.Read<T>(output);
    }

    private void InvokeNoOutput(uint ioctl)
    {
        bool success = DeviceIoControl(_handle!, ioctl, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

        if (!success)
        {
            throw CreateIoException($"Driver IOCTL 0x{ioctl:X8} failed.");
        }
    }

    private static IOException CreateIoException(string message)
    {
        int error = Marshal.GetLastWin32Error();

        return new IOException(message, new Win32Exception(error));
    }

    private static string FindDeviceInterfacePath()
    {
        Guid guid = DriverProtocol.DeviceInterfaceGuid;

        IntPtr deviceInfoSet = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);

        if (deviceInfoSet == new IntPtr(-1))
        {
            throw new IOException(
                "Failed to enumerate PCMode Audio driver interfaces.",
                new Win32Exception(Marshal.GetLastWin32Error())
            );
        }

        try
        {
            var interfaceData = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };

            if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref guid, 0, ref interfaceData))
            {
                int error = Marshal.GetLastWin32Error();

                if (error == ErrorNoMoreItems)
                {
                    throw new DriverUnavailableException(
                        "PCMode Audio virtual-endpoint driver is not installed or started."
                    );
                }

                throw new IOException("Failed to enumerate PCMode Audio driver interfaces.", new Win32Exception(error));
            }

            SetupDiGetDeviceInterfaceDetail(
                deviceInfoSet,
                ref interfaceData,
                IntPtr.Zero,
                0,
                out uint requiredSize,
                IntPtr.Zero
            );

            int firstError = Marshal.GetLastWin32Error();

            if (requiredSize == 0 || firstError != ErrorInsufficientBuffer)
            {
                throw new IOException(
                    "Failed to query PCMode Audio driver interface path size.",
                    new Win32Exception(firstError)
                );
            }

            IntPtr detail = Marshal.AllocHGlobal(checked((int)requiredSize));

            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);

                if (
                    !SetupDiGetDeviceInterfaceDetail(
                        deviceInfoSet,
                        ref interfaceData,
                        detail,
                        requiredSize,
                        out _,
                        IntPtr.Zero
                    )
                )
                {
                    throw new IOException(
                        "Failed to retrieve PCMode Audio driver interface path.",
                        new Win32Exception(Marshal.GetLastWin32Error())
                    );
                }

                IntPtr pathPointer = IntPtr.Add(detail, 4);

                return Marshal.PtrToStringUni(pathPointer)
                    ?? throw new IOException("PCMode Audio driver interface path was empty.");
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int Size;

        public Guid InterfaceClassGuid;

        public int Flags;

        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags
    );

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData
    );

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData
    );

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        int inputBufferSize,
        byte[] outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        int inputBufferSize,
        IntPtr outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped
    );
}

internal readonly record struct DriverPacket(
    ulong Sequence,
    ulong Qpc,
    NativeDriverFormat Format,
    ReadOnlyMemory<byte> Payload
);

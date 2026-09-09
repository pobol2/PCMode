using PCMode.Audio.DriverClient.Diagnostics;
using PCMode.Audio.DriverClient.Transport;

namespace PCMode.Audio.DriverClient.Control;

public sealed class VirtualEndpointControlClient : IDisposable
{
    private readonly DriverConnection _connection;

    private bool _disposed;

    public VirtualEndpointControlClient()
    {
        _connection = new DriverConnection(claimPcmTransport: false);
    }

    public DriverStatus GetStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _connection.GetStatus();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _connection.Dispose();

        _disposed = true;
    }
}

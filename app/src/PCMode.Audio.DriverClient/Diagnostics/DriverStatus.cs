using PCMode.Audio.Core.Models;
using PCMode.Audio.DriverClient.Protocol;

namespace PCMode.Audio.DriverClient.Diagnostics;

public sealed record DriverStatus(
    bool Available,
    bool DeviceStarted,
    bool StreamRunning,
    bool PcmConsumerConnected,
    uint ActiveStreams,
    uint ProtocolMajor,
    uint ProtocolMinor,
    DriverCapabilities Capabilities,
    AudioFormat? PreferredFormat,
    AudioFormat? CurrentFormat,
    ulong PacketsRendered,
    ulong BytesRendered,
    ulong PacketsDropped,
    ulong DiscontinuityCount,
    string? Failure
)
{
    public static DriverStatus Unavailable(string failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);

        return new DriverStatus(
            Available: false,
            DeviceStarted: false,
            StreamRunning: false,
            PcmConsumerConnected: false,
            ActiveStreams: 0,
            ProtocolMajor: 0,
            ProtocolMinor: 0,
            Capabilities: DriverCapabilities.None,
            PreferredFormat: null,
            CurrentFormat: null,
            PacketsRendered: 0,
            BytesRendered: 0,
            PacketsDropped: 0,
            DiscontinuityCount: 0,
            Failure: failure
        );
    }
}

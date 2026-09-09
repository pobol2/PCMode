using PCMode.Audio.Core.Models;
using PCMode.Audio.Protocol.Messages;

namespace PCMode.Audio.Client;

public interface IAudioHostClient
{
    Task ApplyTargetAsync(AudioTarget target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recalibrates one running route's gain, mute, endpoint volume,
    /// parametric EQ and delay, without reapplying the whole target.
    /// </summary>
    Task RecalibrateRouteAsync(
        AudioEndpointId endpointId,
        float volume,
        bool muted,
        TimeSpan delay,
        AudioProcessingProfile processing,
        CancellationToken cancellationToken = default
    );

    Task StopAsync(CancellationToken cancellationToken = default);

    Task<AudioHostStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<AudioEngineHealth> GetHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Each route's current waveform envelope -- a small, dedicated request
    /// meant to be polled many times a second by a redraw loop, unlike
    /// <see cref="GetHealthAsync"/>.
    /// </summary>
    Task<IReadOnlyList<AudioRouteWaveform>> GetWaveformAsync(CancellationToken cancellationToken = default);

    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

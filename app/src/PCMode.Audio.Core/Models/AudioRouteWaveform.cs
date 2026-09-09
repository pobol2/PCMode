using PCMode.Audio.Core.Buffers;

namespace PCMode.Audio.Core.Models;

/// <summary>
/// One route's rolling min/max waveform envelope, for a "what is this route
/// actually playing" visualization.
/// </summary>
/// <param name="Available">
/// Whether this route is currently running and producing audio.
/// <see cref="Buckets"/> is empty when false -- there is no telemetry to
/// show, which is a different thing from a route that is running and
/// genuinely silent (every bucket at (0, 0)). Collapsing the two would make
/// "nothing is playing" and "PCMode cannot tell you" look identical.
/// </param>
/// <param name="Buckets">
/// Oldest bucket first, after EQ and gain, before resampling. Empty when
/// <paramref name="Available"/> is false.
/// </param>
public sealed record AudioRouteWaveform(
    AudioEndpointId EndpointId,
    bool Available,
    IReadOnlyList<WaveformEnvelopeSample> Buckets
);

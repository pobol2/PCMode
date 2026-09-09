using PCMode.Audio.Core.Models;
using PCMode.Audio.Windows.Formats;

namespace PCMode.Audio.Windows.Endpoints;

public sealed record CoreAudioEndpointSnapshot(
    AudioEndpointId Id,
    string FriendlyName,
    AudioFormat Format,
    WaveFormatInfo NativeFormat
);

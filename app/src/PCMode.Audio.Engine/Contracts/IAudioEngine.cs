using PCMode.Audio.Core.Models;
using PCMode.Audio.Engine.Runtime;

namespace PCMode.Audio.Engine.Contracts;

public interface IAudioEngine : IDisposable
{
    AudioEngineState State { get; }

    AudioTarget? ActiveTarget { get; }

    bool NeedsRecovery { get; }

    AudioEngineHealth GetHealth();

    IReadOnlyList<AudioRouteWaveform> GetWaveform();

    void Apply(AudioTarget target);

    void Recalibrate(
        AudioEndpointId endpointId,
        float volume,
        bool muted,
        float gainDb,
        TimeSpan delay,
        ParametricEqProfile? parametricEq
    );

    int ProcessNextBlock();

    void Stop();
}

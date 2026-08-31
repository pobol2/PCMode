using PCMode.Audio.Core.Models;

namespace PCMode.Audio.Engine.Contracts;

public interface IAudioInputStream : IDisposable
{
    AudioFormat Format { get; }

    void Start();

    int Read(Span<float> destination);

    void Stop();
}
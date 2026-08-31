using PCMode.Audio.Core.Buffers;
using PCMode.Audio.Core.Models;

namespace PCMode.Audio.Engine.Contracts;

public interface IAudioOutputStream :
    IDisposable
{
    AudioFormat Format { get; }

    void Start();

    AudioOutputClockSnapshot GetClockSnapshot();

    /// <summary>
    /// What this stream itself observed about its contact with the device.
    /// Diagnostic only -- nothing about routing behaviour depends on it.
    /// </summary>
    AudioOutputDiagnostics GetDiagnostics();

    void Write(
        AudioBlock block);

    void Stop();
}
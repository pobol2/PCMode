using PCMode.Audio.Core.Models;

namespace PCMode.Audio.Engine.Contracts;

public interface IAudioOutputStreamFactory
{
    IAudioOutputStream Create(
        AudioEndpointId endpointId,
        AudioFormat requestedFormat);
}
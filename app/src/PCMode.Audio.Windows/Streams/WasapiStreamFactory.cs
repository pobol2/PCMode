using PCMode.Audio.Core.Models;
using PCMode.Audio.Engine.Contracts;

namespace PCMode.Audio.Windows.Streams;

public sealed class WasapiStreamFactory : IAudioInputStreamFactory, IAudioOutputStreamFactory
{
    public AudioEndpointId LoopbackSourceId { get; }

    public WasapiStreamFactory(AudioEndpointId loopbackSourceId)
    {
        if (loopbackSourceId == default)
        {
            throw new ArgumentException("A loopback source endpoint is required.", nameof(loopbackSourceId));
        }

        LoopbackSourceId = loopbackSourceId;
    }

    public IAudioInputStream Create()
    {
        return new WasapiLoopbackInput(LoopbackSourceId);
    }

    public IAudioOutputStream Create(AudioEndpointId endpointId, AudioFormat requestedFormat)
    {
        if (endpointId == LoopbackSourceId)
        {
            throw new InvalidOperationException(
                "The loopback source and physical output must be different endpoints. "
                    + "Otherwise AudioLab would capture its own rendered output."
            );
        }

        return new WasapiRenderOutput(endpointId, requestedFormat);
    }
}

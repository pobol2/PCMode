using PCMode.Audio.Core.Models;
using PCMode.Audio.DriverClient.Transport;
using PCMode.Audio.Engine.Contracts;
using PCMode.Audio.Engine.Runtime;
using PCMode.Audio.Windows.Streams;

namespace PCMode.Audio.Host;

public sealed class AudioEngineFactory
{
    private readonly HostOptions
        _options;

    public AudioEngineFactory(
        HostOptions options)
    {
        ArgumentNullException.ThrowIfNull(
            options);

        _options =
            options;
    }

    public IAudioEngine Create()
    {
        IAudioInputStreamFactory
            inputFactory =
                _options.UsesDevelopmentInput
                    ? new DevelopmentInputFactory(
                        new AudioEndpointId(
                            _options
                                .DevelopmentSourceEndpointId!))
                    : new DriverInputFactory();

        return new AudioEngine(
            inputFactory,
            new WasapiOutputFactory(),
            blockFrameCapacity:
                480);
    }

    private sealed class
        DriverInputFactory :
        IAudioInputStreamFactory
    {
        public IAudioInputStream Create()
        {
            // The driver delivers PCM in bursts on its own render-timer
            // schedule, not in the steady small increments the real output
            // device expects -- without this cushion, the processing
            // thread's wait for the next burst delays its write to the
            // real device by the same amount, which is audible as stutter.
            return new JitterBufferedAudioInput(
                new DriverAudioInput());
        }
    }

    private sealed class
        DevelopmentInputFactory :
        IAudioInputStreamFactory
    {
        private readonly AudioEndpointId
            _sourceId;

        public DevelopmentInputFactory(
            AudioEndpointId sourceId)
        {
            _sourceId =
                sourceId;
        }

        public IAudioInputStream Create()
        {
            // Loopback capture is subject to the same scheduling and
            // buffer-availability jitter as any other WASAPI stream, so it
            // gets the same cushion for consistency with the driver path.
            return new JitterBufferedAudioInput(
                new WasapiLoopbackInput(
                    _sourceId));
        }
    }

    private sealed class
        WasapiOutputFactory :
        IAudioOutputStreamFactory
    {
        public IAudioOutputStream Create(
            AudioEndpointId endpointId,
            AudioFormat requestedFormat)
        {
            return new WasapiRenderOutput(
                endpointId,
                requestedFormat);
        }
    }
}
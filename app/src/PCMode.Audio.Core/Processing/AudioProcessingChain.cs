using PCMode.Audio.Core.Buffers;

namespace PCMode.Audio.Core.Processing;

public sealed class AudioProcessingChain : IAudioProcessor
{
    private readonly IAudioProcessor[] _processors;

    public int Count => _processors.Length;

    public AudioProcessingChain(params IAudioProcessor[] processors)
    {
        ArgumentNullException.ThrowIfNull(processors);

        _processors = new IAudioProcessor[processors.Length];

        for (int i = 0; i < processors.Length; i++)
        {
            _processors[i] =
                processors[i] ?? throw new ArgumentException($"Processor {i} cannot be null.", nameof(processors));
        }
    }

    public void Process(AudioBlock block)
    {
        for (int i = 0; i < _processors.Length; i++)
        {
            _processors[i].Process(block);
        }
    }
}

using PCMode.Audio.Core.Buffers;

namespace PCMode.Audio.Core.Processing;

public interface IAudioProcessor
{
    void Process(AudioBlock block);
}

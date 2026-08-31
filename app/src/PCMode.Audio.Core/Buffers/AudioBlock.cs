using PCMode.Audio.Core.Models;

namespace PCMode.Audio.Core.Buffers;

public readonly ref struct AudioBlock
{
    public Span<float> Samples { get; }

    public AudioFormat Format { get; }

    public long FramePosition { get; }

    public int FrameCount => Samples.Length / Format.ChannelCount;

    public AudioBlock(
        Span<float> samples,
        AudioFormat format,
        long framePosition = 0)
    {
        if (!format.IsValid)
        {
            throw new ArgumentException(
                "Audio format must be valid.",
                nameof(format));
        }

        if (samples.Length % format.ChannelCount != 0)
        {
            throw new ArgumentException(
                "Sample count must be divisible by the channel count.",
                nameof(samples));
        }

        if (framePosition < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(framePosition),
                framePosition,
                "Frame position cannot be negative.");
        }

        Samples = samples;
        Format = format;
        FramePosition = framePosition;
    }
}
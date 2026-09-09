namespace PCMode.Audio.Core.Models;

public readonly record struct AudioFormat
{
    public int SampleRate { get; }

    public int ChannelCount { get; }

    public bool IsValid => SampleRate > 0 && ChannelCount > 0;

    public AudioFormat(int sampleRate, int channelCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);

        SampleRate = sampleRate;
        ChannelCount = channelCount;
    }
}

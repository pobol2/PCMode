
using PCMode.Audio.Core.Buffers;
using PCMode.Audio.Core.Models;
using Xunit;

namespace PCMode.Audio.Core.Tests.Buffers;

public sealed class AudioBlockTests
{
    [Fact]
    public void AudioBlock_derives_frame_count_from_interleaved_samples()
    {
        var format = new AudioFormat(48_000, 2);
        var samples = new float[8];

        var block = new AudioBlock(
            samples,
            format,
            framePosition: 128);

        Assert.Equal(8, block.Samples.Length);
        Assert.Equal(4, block.FrameCount);
        Assert.Equal(128, block.FramePosition);
        Assert.Equal(format, block.Format);
    }

    [Fact]
    public void AudioBlock_rejects_partial_frames()
    {
        Assert.Throws<ArgumentException>(
            ConstructBlockWithPartialFrame);
    }

    private static void ConstructBlockWithPartialFrame()
    {
        var format = new AudioFormat(48_000, 2);
        var samples = new float[3];

        _ = new AudioBlock(samples, format);
    }
}
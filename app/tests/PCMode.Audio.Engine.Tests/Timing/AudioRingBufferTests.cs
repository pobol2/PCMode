using PCMode.Audio.Engine.Timing;
using Xunit;

namespace PCMode.Audio.Engine.Tests.Timing;

public sealed class AudioRingBufferTests
{
    [Fact]
    public void Read_reports_underrun_and_zero_fills_missing_frames()
    {
        var buffer =
            new AudioRingBuffer(
                capacityFrames: 4,
                channelCount: 2);

        buffer.Write(
            [
                1.0f,
                -1.0f
            ]);

        float[] destination =
            new float[4];

        int framesRead =
            buffer.Read(
                destination);

        Assert.Equal(
            1,
            framesRead);

        Assert.Equal(
            new[]
            {
                1.0f,
                -1.0f,
                0.0f,
                0.0f
            },
            destination);

        Assert.Equal(
            1,
            buffer.UnderrunCount);

        Assert.Equal(
            1,
            buffer.UnderrunFrameCount);
    }

    [Fact]
    public void Reset_clears_buffered_audio_and_underrun_state()
    {
        var buffer =
            new AudioRingBuffer(
                capacityFrames: 4,
                channelCount: 1);

        buffer.Write(
            [
                1.0f,
                2.0f
            ]);

        float[] underrunRead =
            new float[3];

        buffer.Read(
            underrunRead);

        Assert.Equal(
            1,
            buffer.UnderrunCount);

        buffer.Write(
            [
                9.0f,
                10.0f
            ]);

        buffer.Reset();

        Assert.Equal(
            0,
            buffer.CountFrames);

        Assert.Equal(
            0,
            buffer.UnderrunCount);

        Assert.Equal(
            0,
            buffer.UnderrunFrameCount);

        float[] destination =
        [
            -1.0f,
            -1.0f
        ];

        buffer.Read(
            destination);

        Assert.Equal(
            new[]
            {
                0.0f,
                0.0f
            },
            destination);
    }

    [Fact]
    public void Write_rejects_overflow_instead_of_overwriting_unread_audio()
    {
        var buffer =
            new AudioRingBuffer(
                capacityFrames: 2,
                channelCount: 1);

        buffer.Write(
            [
                1.0f,
                2.0f
            ]);

        InvalidOperationException ex =
            Assert.Throws<InvalidOperationException>(
                () =>
                    buffer.Write(
                        [
                            3.0f
                        ]));

        Assert.Contains(
            "overflow",
            ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }
}
using PCMode.Audio.Core.Buffers;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Core.Processing;
using Xunit;

namespace PCMode.Audio.Core.Tests.Processing;

public sealed class GainProcessorTests
{
    private static readonly AudioFormat Stereo48K =
        new(48_000, 2);

    [Fact]
    public void Zero_db_leaves_samples_unchanged()
    {
        var samples = new[]
        {
            -1.0f,
            -0.25f,
            0.25f,
            1.0f
        };

        var processor = new GainProcessor(0.0f);
        var block = new AudioBlock(samples, Stereo48K);

        processor.Process(block);

        Assert.Equal(-1.0f, samples[0]);
        Assert.Equal(-0.25f, samples[1]);
        Assert.Equal(0.25f, samples[2]);
        Assert.Equal(1.0f, samples[3]);
    }

    [Fact]
    public void Minus_six_db_approximately_halves_samples()
    {
        var samples = new[]
        {
            1.0f,
            -1.0f
        };

        var processor = new GainProcessor(-6.0206f);
        var block = new AudioBlock(samples, Stereo48K);

        processor.Process(block);

        Assert.InRange(samples[0], 0.49999f, 0.50001f);
        Assert.InRange(samples[1], -0.50001f, -0.49999f);
    }

    [Fact]
    public void Positive_gain_does_not_clip_samples()
    {
        var samples = new[]
        {
            0.75f,
            -0.75f
        };

        var processor = new GainProcessor(6.0206f);
        var block = new AudioBlock(samples, Stereo48K);

        processor.Process(block);

        Assert.True(samples[0] > 1.0f);
        Assert.True(samples[1] < -1.0f);
    }

    [Fact]
    public void Gain_must_be_finite()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GainProcessor(float.NaN));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GainProcessor(float.PositiveInfinity));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GainProcessor(float.NegativeInfinity));
    }

    [Fact]
    public void Route_volume_multiplies_processing_gain()
    {
        var samples =
            new[]
            {
                1.0f,
                -1.0f
            };

        var processor =
            new GainProcessor(
                -6.0206f,
                volume: 0.5f,
                muted: false);

        processor.Process(
            new AudioBlock(
                samples,
                Stereo48K));

        Assert.InRange(
            samples[0],
            0.24999f,
            0.25001f);

        Assert.InRange(
            samples[1],
            -0.25001f,
            -0.24999f);
    }

    [Fact]
    public void Muted_route_writes_silence()
    {
        var samples =
            new[]
            {
                1.0f,
                -1.0f
            };

        var processor =
            new GainProcessor(
                12.0f,
                volume: 1.0f,
                muted: true);

        processor.Process(
            new AudioBlock(
                samples,
                Stereo48K));

        Assert.All(
            samples,
            sample =>
                Assert.Equal(
                    0.0f,
                    sample));
    }

    private static readonly AudioFormat Mono48K =
        new(48_000, 1);

    [Fact]
    public void Reconfigure_glides_rather_than_stepping_to_the_new_gain()
    {
        // Live calibration must not click: the first sample after a
        // Reconfigure has to still be close to the old level, not already at
        // the new one.
        var processor =
            new GainProcessor(0.0f);

        var samples =
            new float[GainProcessor.RampFrameCount + 2];

        Array.Fill(
            samples,
            1.0f);

        processor.Reconfigure(-6.0206f, 1.0f, false);

        processor.Process(
            new AudioBlock(
                samples,
                Mono48K));

        // Frame 0 of the ramp uses the gain it started from -- unity here --
        // not the -6 dB target it is heading toward.
        Assert.InRange(
            samples[0],
            0.99f,
            1.0f);
    }

    [Fact]
    public void Reconfigure_reaches_the_new_gain_and_holds_it()
    {
        var processor =
            new GainProcessor(0.0f);

        var samples =
            new float[GainProcessor.RampFrameCount + 4];

        Array.Fill(
            samples,
            1.0f);

        processor.Reconfigure(-6.0206f, 1.0f, false);

        processor.Process(
            new AudioBlock(
                samples,
                Mono48K));

        // Once the ramp has run its full length, every later sample sits at
        // exactly the target gain -- roughly half amplitude for -6 dB -- and
        // stays there rather than continuing to drift.
        Assert.InRange(
            samples[^1],
            0.49999f,
            0.50001f);

        Assert.Equal(
            samples[^1],
            samples[^2]);
    }

    [Fact]
    public void Reconfigure_mid_ramp_retargets_from_the_gain_reached_so_far_rather_than_restarting()
    {
        var processor =
            new GainProcessor(0.0f);

        var halfway =
            new float[GainProcessor.RampFrameCount / 2];

        Array.Fill(
            halfway,
            1.0f);

        processor.Reconfigure(-24.0f, 1.0f, false);

        processor.Process(
            new AudioBlock(
                halfway,
                Mono48K));

        // Retargeting to the gain the first ramp was already heading toward
        // must not reopen a gap: the level reached so far is close to -24 dB
        // already, so gliding from there to the same target should barely
        // move it, not restart from unity.
        float reached =
            halfway[^1];

        processor.Reconfigure(-24.0f, 1.0f, false);

        var next =
            new float[1]
            {
                1.0f
            };

        processor.Process(
            new AudioBlock(
                next,
                Mono48K));

        Assert.InRange(
            next[0],
            reached - 0.05f,
            reached + 0.05f);
    }

    [Fact]
    public void Reset_clears_a_live_ramp_and_restores_its_configured_target()
    {
        var processor =
            new GainProcessor(
                0.0f);

        processor.Reconfigure(
            -20.0f,
            1.0f,
            false);

        processor.Process(
            new AudioBlock(
                new float[]
                {
                    1.0f
                },
                Mono48K));

        processor.Reset();

        var restarted =
            new float[]
            {
                1.0f
            };

        processor.Process(
            new AudioBlock(
                restarted,
                Mono48K));

        Assert.InRange(
            restarted[0],
            0.09999f,
            0.10001f);
    }

}

using PCMode.Audio.Core.Buffers;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Core.Processing;
using Xunit;

namespace PCMode.Audio.Core.Tests.Processing;

public sealed class ParametricEqProcessorTests
{
    private const int SampleRate = 48_000;

    private static readonly AudioFormat Mono48K = new(SampleRate, 1);

    private static readonly AudioFormat Stereo48K = new(SampleRate, 2);

    [Fact]
    public void Zero_db_peak_has_unity_coefficients_and_response()
    {
        Biquad filter = Biquad.Create(new EqBand(EqFilterType.Peak, 1_000.0f, 0.0f, 1.0f), SampleRate);

        Assert.InRange(filter.B0, 0.999999999, 1.000000001);

        Assert.InRange(filter.B1 - filter.A1, -1e-12, 1e-12);

        Assert.InRange(filter.B2 - filter.A2, -1e-12, 1e-12);

        Assert.InRange(filter.GetMagnitude(1_000.0, SampleRate), 0.999999, 1.000001);
    }

    [Fact]
    public void Positive_peak_boosts_center_frequency()
    {
        Biquad filter = Biquad.Create(new EqBand(EqFilterType.Peak, 1_000.0f, 6.0f, 1.0f), SampleRate);

        double magnitude = filter.GetMagnitude(1_000.0, SampleRate);

        Assert.InRange(magnitude, 1.99, 2.01);
    }

    [Fact]
    public void Negative_peak_cuts_center_frequency()
    {
        Biquad filter = Biquad.Create(new EqBand(EqFilterType.Peak, 1_000.0f, -6.0f, 1.0f), SampleRate);

        double magnitude = filter.GetMagnitude(1_000.0, SampleRate);

        Assert.InRange(magnitude, 0.49, 0.51);
    }

    [Fact]
    public void Low_shelf_boosts_low_frequencies_more_than_high_frequencies()
    {
        Biquad filter = Biquad.Create(new EqBand(EqFilterType.LowShelf, 500.0f, 6.0f, 0.70710677f), SampleRate);

        double low = filter.GetMagnitude(50.0, SampleRate);

        double high = filter.GetMagnitude(10_000.0, SampleRate);

        Assert.True(low > 1.8);

        Assert.InRange(high, 0.95, 1.05);
    }

    [Fact]
    public void High_shelf_boosts_high_frequencies_more_than_low_frequencies()
    {
        Biquad filter = Biquad.Create(new EqBand(EqFilterType.HighShelf, 5_000.0f, 6.0f, 0.70710677f), SampleRate);

        double low = filter.GetMagnitude(100.0, SampleRate);

        double high = filter.GetMagnitude(20_000.0, SampleRate);

        Assert.InRange(low, 0.95, 1.05);

        Assert.True(high > 1.8);
    }

    [Fact]
    public void Low_pass_attenuates_frequencies_above_cutoff()
    {
        Biquad filter = Biquad.Create(new EqBand(EqFilterType.LowPass, 2_000.0f, q: 0.70710677f), SampleRate);

        double low = filter.GetMagnitude(200.0, SampleRate);

        double high = filter.GetMagnitude(10_000.0, SampleRate);

        Assert.True(low > 0.95);

        Assert.True(high < 0.05);
    }

    [Fact]
    public void High_pass_attenuates_frequencies_below_cutoff()
    {
        Biquad filter = Biquad.Create(new EqBand(EqFilterType.HighPass, 2_000.0f, q: 0.70710677f), SampleRate);

        double low = filter.GetMagnitude(100.0, SampleRate);

        double high = filter.GetMagnitude(10_000.0, SampleRate);

        Assert.True(low < 0.01);

        Assert.True(high > 0.95);
    }

    [Fact]
    public void Stereo_channels_keep_independent_filter_history()
    {
        var processor = new ParametricEqProcessor(
            new ParametricEqProfile(0.0f, new EqBand(EqFilterType.LowPass, 1_000.0f, q: 0.70710677f)),
            Stereo48K
        );

        var leftImpulse = new float[64 * 2];

        leftImpulse[0] = 1.0f;

        processor.Process(new AudioBlock(leftImpulse, Stereo48K));

        for (int frame = 0; frame < 64; frame++)
        {
            Assert.Equal(0.0f, leftImpulse[(frame * 2) + 1]);
        }

        var silence = new float[64 * 2];

        processor.Process(new AudioBlock(silence, Stereo48K));

        bool leftHasTail = false;

        for (int frame = 0; frame < 64; frame++)
        {
            leftHasTail |= MathF.Abs(silence[frame * 2]) > 1e-7f;

            Assert.Equal(0.0f, silence[(frame * 2) + 1]);
        }

        Assert.True(leftHasTail);
    }

    [Fact]
    public void Reset_clears_filter_history()
    {
        var processor = new ParametricEqProcessor(
            new ParametricEqProfile(0.0f, new EqBand(EqFilterType.LowPass, 1_000.0f, q: 0.70710677f)),
            Mono48K
        );

        var impulse = new[] { 1.0f };

        processor.Process(new AudioBlock(impulse, Mono48K));

        processor.Reset();

        var silence = new float[64];

        processor.Process(new AudioBlock(silence, Mono48K));

        Assert.All(silence, sample => Assert.Equal(0.0f, sample));
    }

    [Fact]
    public void Preamp_gain_is_applied()
    {
        var processor = new ParametricEqProcessor(new ParametricEqProfile(-6.0206f), Mono48K);

        var samples = new[] { 1.0f };

        processor.Process(new AudioBlock(samples, Mono48K));

        Assert.InRange(samples[0], 0.49999f, 0.50001f);
    }

    [Fact]
    public void Reconfigure_crossfades_without_an_instantaneous_gain_step()
    {
        var processor = new ParametricEqProcessor(new ParametricEqProfile(0.0f), Mono48K);

        var replacement = new ParametricEqProfile(-6.0206f);

        processor.Reconfigure(replacement);

        var samples = Enumerable.Repeat(1.0f, ParametricEqProcessor.ReconfigurationFadeFrames + 1).ToArray();

        processor.Process(new AudioBlock(samples, Mono48K));

        Assert.Same(replacement, processor.Profile);

        Assert.InRange(samples[0], 0.99f, 1.0f);

        Assert.InRange(samples[^1], 0.49999f, 0.50001f);

        float maximumStep = 0.0f;

        for (int i = 1; i < samples.Length; i++)
        {
            maximumStep = MathF.Max(maximumStep, MathF.Abs(samples[i] - samples[i - 1]));
        }

        Assert.True(maximumStep < 0.01f, $"PEQ reconfiguration stepped by {maximumStep}.");
    }

    [Fact]
    public void Invalid_filter_parameters_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EqBand(EqFilterType.Peak, 0.0f, 3.0f, 1.0f));

        Assert.Throws<ArgumentOutOfRangeException>(() => new EqBand(EqFilterType.Peak, 1_000.0f, 25.0f, 1.0f));

        Assert.Throws<ArgumentOutOfRangeException>(() => new EqBand(EqFilterType.Peak, 1_000.0f, 3.0f, 0.0f));

        Assert.Throws<ArgumentOutOfRangeException>(() => new ParametricEqProfile(25.0f));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Biquad.Create(new EqBand(EqFilterType.Peak, 24_000.0f, 3.0f, 1.0f), SampleRate)
        );
    }
}

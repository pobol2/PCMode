using PCMode.Audio.Core.Models;
using PCMode.Audio.Core.Processing;
using Xunit;

namespace PCMode.Audio.Core.Tests.Processing;

public sealed class EqResponseCurveTests
{
    [Fact]
    public void LogFrequencies_spans_20_hz_to_20_khz_in_log_steps()
    {
        double[] frequencies = EqResponseCurve.LogFrequencies(5);

        Assert.Equal(5, frequencies.Length);
        Assert.InRange(frequencies[0], 19.999, 20.001);
        Assert.InRange(frequencies[^1], 19_999.0, 20_001.0);

        for (int i = 1; i < frequencies.Length; i++)
        {
            Assert.True(frequencies[i] > frequencies[i - 1]);
        }
    }

    [Fact]
    public void MagnitudeDb_of_a_peak_band_matches_its_gain_at_the_center_frequency()
    {
        var band = new EqBand(EqFilterType.Peak, 1_000.0f, 6.0f, 1.0f);

        double atCenter = EqResponseCurve.MagnitudeDb(band, 1_000.0);

        Assert.InRange(atCenter, 5.99, 6.01);
    }

    [Fact]
    public void MagnitudeDb_of_a_peak_band_is_near_zero_far_from_the_center_frequency()
    {
        var band = new EqBand(EqFilterType.Peak, 1_000.0f, 12.0f, 4.0f);

        double farAway = EqResponseCurve.MagnitudeDb(band, 20.0);

        Assert.InRange(farAway, -0.5, 0.5);
    }

    [Fact]
    public void MagnitudeDb_of_a_low_shelf_approaches_its_shelf_gain_well_below_the_corner()
    {
        var band = new EqBand(EqFilterType.LowShelf, 200.0f, -6.0f, 0.7071f);

        double deepInShelf = EqResponseCurve.MagnitudeDb(band, 20.0);

        Assert.InRange(deepInShelf, -6.5, -5.0);
    }

    [Fact]
    public void MagnitudeDb_of_a_flat_zero_gain_band_is_zero_everywhere_sampled()
    {
        var band = new EqBand(EqFilterType.Peak, 1_000.0f, 0.0f, 1.0f);

        foreach (double frequency in EqResponseCurve.LogFrequencies(21))
        {
            Assert.InRange(EqResponseCurve.MagnitudeDb(band, frequency), -0.01, 0.01);
        }
    }
}

using PCMode.Audio.Core.Models;

namespace PCMode.Audio.Core.Processing;

/// <summary>
/// The frequency-response math behind a "what does this profile actually do"
/// graph, built on exactly the <see cref="Biquad"/> coefficients the real
/// engine runs -- not a separate, approximate curve that could disagree with
/// what a listener hears.
/// </summary>
public static class EqResponseCurve
{
    public const double MinFrequencyHz = 20.0;
    public const double MaxFrequencyHz = 20_000.0;

    /// <summary>
    /// A representative sample rate for a visualization that has no live
    /// device to ask. The bilinear-transform warping <see cref="Biquad"/>
    /// applies depends on sample rate, but the difference across ordinary
    /// device rates (44.1 kHz-192 kHz) is negligible across the audible band,
    /// so one fixed rate is enough for a graph rather than a measurement.
    /// </summary>
    public const int VisualizationSampleRate = 48_000;

    /// <summary>
    /// <paramref name="pointCount"/> frequencies, log-spaced from
    /// <see cref="MinFrequencyHz"/> to <see cref="MaxFrequencyHz"/>, the shape
    /// any EQ graph is read in.
    /// </summary>
    public static double[] LogFrequencies(int pointCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pointCount, 2);

        var frequencies = new double[pointCount];

        double logMin = Math.Log10(MinFrequencyHz);
        double logMax = Math.Log10(MaxFrequencyHz);

        for (int index = 0; index < pointCount; index++)
        {
            double t = index / (double)(pointCount - 1);
            frequencies[index] = Math.Pow(10.0, logMin + (t * (logMax - logMin)));
        }

        return frequencies;
    }

    /// <summary>
    /// One band's own magnitude at <paramref name="frequencyHz"/>, in dB --
    /// what that single filter does to the signal there, independent of any
    /// other band or the profile's preamp.
    /// </summary>
    public static double MagnitudeDb(EqBand band, double frequencyHz, int sampleRate = VisualizationSampleRate)
    {
        double nyquist = sampleRate * 0.5;
        double clamped = Math.Clamp(frequencyHz, 1.0, nyquist - 1.0);

        Biquad filter = Biquad.Create(band, sampleRate);

        double magnitude = filter.GetMagnitude(clamped, sampleRate);

        return 20.0 * Math.Log10(Math.Max(magnitude, 1e-9));
    }
}

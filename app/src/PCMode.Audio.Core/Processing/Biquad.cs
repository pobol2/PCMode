using System.Numerics;
using PCMode.Audio.Core.Models;

namespace PCMode.Audio.Core.Processing;

public sealed class Biquad
{
    private double _z1;
    private double _z2;

    public double B0 { get; }

    public double B1 { get; }

    public double B2 { get; }

    public double A1 { get; }

    public double A2 { get; }

    private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        if (!double.IsFinite(a0) || a0 == 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(a0), a0, "Biquad a0 must be finite and non-zero.");
        }

        B0 = b0 / a0;
        B1 = b1 / a0;
        B2 = b2 / a0;
        A1 = a1 / a0;
        A2 = a2 / a0;

        if (
            !double.IsFinite(B0)
            || !double.IsFinite(B1)
            || !double.IsFinite(B2)
            || !double.IsFinite(A1)
            || !double.IsFinite(A2)
        )
        {
            throw new ArgumentException("The requested EQ band produced non-finite biquad coefficients.");
        }
    }

    public static Biquad Create(EqBand band, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        double nyquist = sampleRate * 0.5;

        if (band.FrequencyHz >= nyquist)
        {
            throw new ArgumentOutOfRangeException(
                nameof(band),
                band.FrequencyHz,
                $"Filter frequency must be below Nyquist ({nyquist:F1} Hz for {sampleRate} Hz audio)."
            );
        }

        double w0 = 2.0 * Math.PI * band.FrequencyHz / sampleRate;

        double cosW0 = Math.Cos(w0);

        double sinW0 = Math.Sin(w0);

        double alpha = sinW0 / (2.0 * band.Q);

        double a = Math.Pow(10.0, band.GainDb / 40.0);

        if (!double.IsFinite(a))
        {
            throw new ArgumentOutOfRangeException(nameof(band), band.GainDb, "Filter gain is too large to represent.");
        }

        double b0;
        double b1;
        double b2;
        double a0;
        double a1;
        double a2;

        switch (band.Type)
        {
            case EqFilterType.Peak:
                b0 = 1.0 + (alpha * a);

                b1 = -2.0 * cosW0;

                b2 = 1.0 - (alpha * a);

                a0 = 1.0 + (alpha / a);

                a1 = -2.0 * cosW0;

                a2 = 1.0 - (alpha / a);

                break;

            case EqFilterType.LowShelf:
            {
                double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;

                b0 = a * ((a + 1.0) - ((a - 1.0) * cosW0) + twoSqrtAAlpha);

                b1 = 2.0 * a * ((a - 1.0) - ((a + 1.0) * cosW0));

                b2 = a * ((a + 1.0) - ((a - 1.0) * cosW0) - twoSqrtAAlpha);

                a0 = (a + 1.0) + ((a - 1.0) * cosW0) + twoSqrtAAlpha;

                a1 = -2.0 * ((a - 1.0) + ((a + 1.0) * cosW0));

                a2 = (a + 1.0) + ((a - 1.0) * cosW0) - twoSqrtAAlpha;

                break;
            }

            case EqFilterType.HighShelf:
            {
                double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;

                b0 = a * ((a + 1.0) + ((a - 1.0) * cosW0) + twoSqrtAAlpha);

                b1 = -2.0 * a * ((a - 1.0) + ((a + 1.0) * cosW0));

                b2 = a * ((a + 1.0) + ((a - 1.0) * cosW0) - twoSqrtAAlpha);

                a0 = (a + 1.0) - ((a - 1.0) * cosW0) + twoSqrtAAlpha;

                a1 = 2.0 * ((a - 1.0) - ((a + 1.0) * cosW0));

                a2 = (a + 1.0) - ((a - 1.0) * cosW0) - twoSqrtAAlpha;

                break;
            }

            case EqFilterType.LowPass:
                b0 = (1.0 - cosW0) * 0.5;

                b1 = 1.0 - cosW0;

                b2 = (1.0 - cosW0) * 0.5;

                a0 = 1.0 + alpha;

                a1 = -2.0 * cosW0;

                a2 = 1.0 - alpha;

                break;

            case EqFilterType.HighPass:
                b0 = (1.0 + cosW0) * 0.5;

                b1 = -(1.0 + cosW0);

                b2 = (1.0 + cosW0) * 0.5;

                a0 = 1.0 + alpha;

                a1 = -2.0 * cosW0;

                a2 = 1.0 - alpha;

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(band), band.Type, "Unsupported EQ filter type.");
        }

        return new Biquad(b0, b1, b2, a0, a1, a2);
    }

    public float ProcessSample(float sample)
    {
        double input = sample;

        double output = (B0 * input) + _z1;

        _z1 = (B1 * input) - (A1 * output) + _z2;

        _z2 = (B2 * input) - (A2 * output);

        return (float)output;
    }

    public double GetMagnitude(double frequencyHz, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        if (!double.IsFinite(frequencyHz) || frequencyHz < 0.0 || frequencyHz > sampleRate * 0.5)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        }

        double omega = 2.0 * Math.PI * frequencyHz / sampleRate;

        Complex z1 = Complex.FromPolarCoordinates(1.0, -omega);

        Complex z2 = z1 * z1;

        Complex numerator = B0 + (B1 * z1) + (B2 * z2);

        Complex denominator = 1.0 + (A1 * z1) + (A2 * z2);

        return numerator.Magnitude / denominator.Magnitude;
    }

    public void Reset()
    {
        _z1 = 0.0;
        _z2 = 0.0;
    }
}

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PCMode.Audio.Core.Buffers;

namespace PCMode.Tray.UI.Components.Visualizations;

/// <summary>
/// The Audio page's top-of-page waveform plot, and the only place its pixel
/// geometry is decided.
/// </summary>
/// <remarks>
/// The feature -- engine, then <c>Elements/Audio</c> -- stops at a rolling
/// min/max envelope, unitless and unscaled. Turning that into a filled band
/// is entirely this class's business, the same division <see cref="ResponseGraph"/>
/// keeps for the EQ response curve.
/// </remarks>
public static class WaveformGraph
{
    /// <summary>The plot area, in device-independent pixels.</summary>
    public const double Width = 420.0;

    /// <summary>See <see cref="Width"/>.</summary>
    public const double Height = 90.0;

    /// <summary>
    /// The signal is expected in [-1, 1]; anything beyond it is clamped so a
    /// single runaway sample cannot pull the whole envelope off its own
    /// plot.
    /// </summary>
    public const double MaxAmplitude = 1.0;

    /// <summary>
    /// The envelope as a single filled band: <see cref="WaveformEnvelopeSample.Max"/>
    /// traced left to right, then <see cref="WaveformEnvelopeSample.Min"/>
    /// traced back right to left. A flat, empty, or all-zero envelope -- the
    /// idle state -- collapses to a band with no height, which is exactly
    /// the flat line the idle waveform is meant to show.
    /// </summary>
    public static Geometry ToGeometry(IReadOnlyList<WaveformEnvelopeSample>? buckets)
    {
        if (buckets is null || buckets.Count == 0)
        {
            return Geometry.Empty;
        }

        double step = Width / buckets.Count;

        var figure = new PathFigure
        {
            IsClosed = true,
            IsFilled = true,
            StartPoint = new System.Windows.Point(0, ToY(buckets[0].Max)),
        };

        for (int index = 0; index < buckets.Count; index++)
        {
            figure.Segments.Add(new LineSegment(new System.Windows.Point(index * step, ToY(buckets[index].Max)), true));
        }

        for (int index = buckets.Count - 1; index >= 0; index--)
        {
            figure.Segments.Add(new LineSegment(new System.Windows.Point(index * step, ToY(buckets[index].Min)), true));
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static double ToY(float value)
    {
        double clamped = Math.Clamp(value, -MaxAmplitude, MaxAmplitude);
        return (Height / 2.0) - (clamped / MaxAmplitude * (Height / 2.0));
    }
}

/// <summary>
/// A waveform envelope as a filled band geometry. Key: WaveformEnvelope.
/// </summary>
/// <remarks>Declared beside the graph it serves, for the same reason <see cref="ResponseCurveConverter"/> is.</remarks>
public sealed class WaveformEnvelopeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        WaveformGraph.ToGeometry(value as IReadOnlyList<WaveformEnvelopeSample>);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PCMode.Audio.Core.Processing;
// The tray enables WinForms for the notify icon, whose implicit usings bring
// System.Drawing's own Point into scope alongside WPF's.
using Point = System.Windows.Point;

namespace PCMode.Tray.UI.Components.Visualizations;

/// <summary>
/// The plot the Audio profile's response curves are drawn into, and the only
/// place their pixel geometry is decided.
/// </summary>
/// <remarks>
/// The feature computes what the filters do -- frequency and decibels, in
/// <see cref="ResponseSample"/> -- and stops there. Everything about turning
/// that into a picture lives here: how big the plot is, how many decibels fit
/// on it, and which pixel a sample lands on. The Layout reads the same
/// constants through <c>x:Static</c>, so the canvas and the curves drawn on it
/// cannot drift apart.
/// </remarks>
public static class ResponseGraph
{
    /// <summary>The plot area, in device-independent pixels.</summary>
    public const double Width = 420.0;

    /// <summary>See <see cref="Width"/>.</summary>
    public const double Height = 140.0;

    /// <summary>The decibel range the plot's full height covers; anything beyond is clamped to the edge.</summary>
    public const double MinDb = -30.0;

    /// <summary>See <see cref="MinDb"/>.</summary>
    public const double MaxDb = 30.0;

    /// <summary>Where 0 dB sits, for the Layout's dashed reference line.</summary>
    public const double ZeroDbY = Height - ((0.0 - MinDb) / (MaxDb - MinDb) * Height);

    /// <summary>
    /// One response curve as a frozen polyline, mapped log-frequency across
    /// <see cref="Width"/> and clamped decibels up <see cref="Height"/>.
    /// </summary>
    public static PointCollection ToPoints(IReadOnlyList<ResponseSample>? samples)
    {
        if (samples is null || samples.Count == 0)
        {
            var empty = new PointCollection();
            empty.Freeze();
            return empty;
        }

        var points = new PointCollection(samples.Count);

        // The samples are spaced logarithmically, so position is taken from
        // the frequency itself rather than from the index: the graph does not
        // have to assume how the feature chose to sample.
        double minLog = Math.Log10(samples[0].FrequencyHz);
        double maxLog = Math.Log10(samples[^1].FrequencyHz);
        double logSpan = maxLog - minLog;

        for (int index = 0; index < samples.Count; index++)
        {
            double t =
                logSpan > 0.0 ? (Math.Log10(samples[index].FrequencyHz) - minLog) / logSpan
                : samples.Count == 1 ? 0.0
                : index / (double)(samples.Count - 1);

            double clampedDb = Math.Clamp(samples[index].MagnitudeDb, MinDb, MaxDb);
            double normalized = (clampedDb - MinDb) / (MaxDb - MinDb);

            points.Add(new Point(t * Width, Height - (normalized * Height)));
        }

        points.Freeze();
        return points;
    }
}

/// <summary>
/// A response curve as polyline points. Key: ResponseCurve.
/// </summary>
/// <remarks>
/// Declared beside the graph it serves rather than with the shared converters,
/// because nothing outside Audio's tree plots a response curve.
/// </remarks>
public sealed class ResponseCurveConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ResponseGraph.ToPoints(value as IReadOnlyList<ResponseSample>);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

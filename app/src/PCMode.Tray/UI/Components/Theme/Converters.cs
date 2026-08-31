using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

// The tray enables WinForms for the notify icon, whose implicit usings bring
// System.Drawing's Color and Brushes into scope alongside WPF's own.
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace PCMode.Tray.UI.Components.Theme;

/// <summary>
/// Visible when the bound string has something in it. Key: StringToVisibility.
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Visible when the bound bool is true, or -- with <see cref="Invert"/> --
/// when it is false. Keys: BooleanToVisibility, InverseBoolToVisibility.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The bound bool, negated -- for an IsEnabled that follows an IsBusy.
/// Key: InverseBool.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// A left margin proportional to the bound depth -- for indenting rows of a
/// flattened hierarchy. Key: IndentMargin.
/// </summary>
public sealed class IndentMarginConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(value is double indent ? indent : 0, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A six-digit RRGGBB string as a fill, or transparent when it is not one.
/// Key: HexToBrush.
/// </summary>
/// <remarks>
/// A colour is stored, and edited, as the hex string the controller actually
/// takes. Turning it into something paintable is a rendering concern, so it
/// happens here rather than in the feature state that owns the string --
/// state-only element types should not need to name a Brush.
/// </remarks>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string color = (value as string ?? string.Empty).Trim();

        if (color.Length != 6 || !color.All(char.IsAsciiHexDigit))
        {
            return Brushes.Transparent;
        }

        return new SolidColorBrush(Color.FromRgb(
            byte.Parse(color.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(color.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(color.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

using System.Windows;

namespace PCMode.Tray.UI.Components.Inputs;

/// <summary>
/// A right-aligned numeric-style text box with a unit suffix (dB, ms, %, Hz,
/// ...), replacing the hand-built "StackPanel of TextBox + TextBlock" that
/// recurred wherever a page needed a value field with a unit -- most often
/// as <see cref="InputContent.Trailing"/>.
/// </summary>
public sealed class UnitTextBox : System.Windows.Controls.Control
{
    static UnitTextBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(UnitTextBox),
            new FrameworkPropertyMetadata(typeof(UnitTextBox))
        );
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(UnitTextBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault)
    );

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit),
        typeof(string),
        typeof(UnitTextBox),
        new PropertyMetadata(string.Empty)
    );

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The unit suffix shown after the value (e.g. "dB", "ms", "%"). Hidden when empty.</summary>
    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }
}

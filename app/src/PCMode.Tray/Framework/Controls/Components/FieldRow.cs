using System.Windows;
using System.Windows.Controls;

namespace PCMode.Tray.Controls.Components;

public class FieldRow : ContentControl
{
    static FieldRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(FieldRow),
            new FrameworkPropertyMetadata(typeof(FieldRow)));
    }

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label),
            typeof(string),
            typeof(FieldRow),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(
            nameof(Hint),
            typeof(string),
            typeof(FieldRow),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty LabelVerticalAlignmentProperty =
        DependencyProperty.Register(
            nameof(LabelVerticalAlignment),
            typeof(VerticalAlignment),
            typeof(FieldRow),
            new PropertyMetadata(VerticalAlignment.Center));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public VerticalAlignment LabelVerticalAlignment
    {
        get => (VerticalAlignment)GetValue(LabelVerticalAlignmentProperty);
        set => SetValue(LabelVerticalAlignmentProperty, value);
    }
}

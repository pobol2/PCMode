using System.Windows;

namespace PCMode.Tray.Controls.Components;

public class StatusDot : System.Windows.Controls.Control
{
    static StatusDot()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(StatusDot),
            new FrameworkPropertyMetadata(typeof(StatusDot)));
    }

    public enum StatusDotState
    {
        Neutral,
        Ok,
        Warning,
        Error
    }

    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(
            nameof(State),
            typeof(StatusDotState),
            typeof(StatusDot),
            new PropertyMetadata(StatusDotState.Neutral));

    public StatusDotState State
    {
        get => (StatusDotState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }
}

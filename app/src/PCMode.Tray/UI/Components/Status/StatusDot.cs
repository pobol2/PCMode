using System.Windows;

namespace PCMode.Tray.UI.Components.Status;

public class StatusDot : System.Windows.Controls.Control
{
    static StatusDot()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusDot), new FrameworkPropertyMetadata(typeof(StatusDot)));
    }

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(StatusVisualState),
        typeof(StatusDot),
        new PropertyMetadata(StatusVisualState.Neutral)
    );

    public StatusVisualState State
    {
        get => (StatusVisualState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }
}

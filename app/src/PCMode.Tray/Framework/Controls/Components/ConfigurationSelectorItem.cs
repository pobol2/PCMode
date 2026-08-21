using System.Windows;

namespace PCMode.Tray.Controls.Components;

public class ConfigurationSelectorItem : System.Windows.Controls.Control
{
    static ConfigurationSelectorItem()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(ConfigurationSelectorItem),
            new FrameworkPropertyMetadata(typeof(ConfigurationSelectorItem)));
    }

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(ConfigurationSelectorItem),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty BadgeTextProperty =
        DependencyProperty.Register(
            nameof(BadgeText),
            typeof(string),
            typeof(ConfigurationSelectorItem),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsBadgeVisibleProperty =
        DependencyProperty.Register(
            nameof(IsBadgeVisible),
            typeof(bool),
            typeof(ConfigurationSelectorItem),
            new PropertyMetadata(false));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string BadgeText
    {
        get => (string)GetValue(BadgeTextProperty);
        set => SetValue(BadgeTextProperty, value);
    }

    public bool IsBadgeVisible
    {
        get => (bool)GetValue(IsBadgeVisibleProperty);
        set => SetValue(IsBadgeVisibleProperty, value);
    }
}
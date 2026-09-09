using System.Windows;
using System.Windows.Controls;

namespace PCMode.Tray.UI.Components;

public class PageSection : ContentControl
{
    static PageSection()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(PageSection),
            new FrameworkPropertyMetadata(typeof(PageSection))
        );
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(PageSection),
        new PropertyMetadata(string.Empty)
    );

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }
}

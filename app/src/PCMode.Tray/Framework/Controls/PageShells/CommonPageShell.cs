using System.Windows;
using System.Windows.Controls;

namespace PCMode.Tray.Controls.PageShells;

/// <summary>
/// Universal PCMode page contract. Feature pages should use one of the derived
/// page families rather than recreating page margins and headers themselves.
/// </summary>
public class CommonPageShell : ContentControl
{
    static CommonPageShell()
    {
        // Feature pages derive from the page-shell classes. WPF implicit styles
        // match exact types, so a stable default style key is required for derived pages.
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(CommonPageShell),
            new FrameworkPropertyMetadata(typeof(CommonPageShell)));
    }

    public static readonly DependencyProperty PageTitleProperty =
        DependencyProperty.Register(
            nameof(PageTitle),
            typeof(string),
            typeof(CommonPageShell),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PageDescriptionProperty =
        DependencyProperty.Register(
            nameof(PageDescription),
            typeof(string),
            typeof(CommonPageShell),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HeaderActionContentProperty =
        DependencyProperty.Register(
            nameof(HeaderActionContent),
            typeof(object),
            typeof(CommonPageShell),
            new PropertyMetadata(null));

    public static readonly DependencyProperty FooterContentProperty =
        DependencyProperty.Register(
            nameof(FooterContent),
            typeof(object),
            typeof(CommonPageShell),
            new PropertyMetadata(null));

    public string PageTitle
    {
        get => (string)GetValue(PageTitleProperty);
        set => SetValue(PageTitleProperty, value);
    }

    public string PageDescription
    {
        get => (string)GetValue(PageDescriptionProperty);
        set => SetValue(PageDescriptionProperty, value);
    }

    public object? HeaderActionContent
    {
        get => GetValue(HeaderActionContentProperty);
        set => SetValue(HeaderActionContentProperty, value);
    }

    public object? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }
}

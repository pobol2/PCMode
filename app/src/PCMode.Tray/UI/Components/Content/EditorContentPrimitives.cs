using System.Windows;
using System.Windows.Controls;
using WpfSize = System.Windows.Size;

namespace PCMode.Tray.UI.Components.Content;

/// <summary>
/// Ensures UIElement items receive a ContentPresenter container, allowing the
/// primitive to own item layout without replacing the child's own style.
/// </summary>
public abstract class ContentPrimitiveItemsControl : ItemsControl
{
    protected override bool IsItemItsOwnContainerOverride(object item) => false;

    protected override DependencyObject GetContainerForItemOverride() => new ContentPresenter();
}

public sealed class VerticalSpacingPanel : System.Windows.Controls.Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing),
        typeof(double),
        typeof(VerticalSpacingPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double spacing && double.IsFinite(spacing) && spacing >= 0
    );

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        double width = 0;
        double height = 0;
        int visibleCount = 0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new WpfSize(availableSize.Width, double.PositiveInfinity));
            if (child.Visibility == Visibility.Collapsed)
                continue;

            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
            visibleCount++;
        }

        if (visibleCount > 1)
            height += Spacing * (visibleCount - 1);

        return new WpfSize(width, height);
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        double top = 0;

        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(0, top, finalSize.Width, child.DesiredSize.Height));
            if (child.Visibility != Visibility.Collapsed)
                top += child.DesiredSize.Height + Spacing;
        }

        return finalSize;
    }
}

public sealed class HorizontalSpacingPanel : System.Windows.Controls.Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing),
        typeof(double),
        typeof(HorizontalSpacingPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double spacing && double.IsFinite(spacing) && spacing >= 0
    );

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        double width = 0;
        double height = 0;
        int visibleCount = 0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new WpfSize(double.PositiveInfinity, availableSize.Height));
            if (child.Visibility == Visibility.Collapsed)
                continue;

            height = Math.Max(height, child.DesiredSize.Height);
            width += child.DesiredSize.Width;
            visibleCount++;
        }

        if (visibleCount > 1)
            width += Spacing * (visibleCount - 1);

        return new WpfSize(width, height);
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        double left = 0;

        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(left, 0, child.DesiredSize.Width, finalSize.Height));
            if (child.Visibility != Visibility.Collapsed)
                left += child.DesiredSize.Width + Spacing;
        }

        return finalSize;
    }
}

public sealed class EditorBoxContent : ContentPrimitiveItemsControl { }

public sealed class InformationContent : ContentControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label),
        typeof(string),
        typeof(InformationContent),
        new PropertyMetadata(string.Empty)
    );

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
}

public sealed class InputContent : ContentControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label),
        typeof(string),
        typeof(InputContent),
        new PropertyMetadata(string.Empty)
    );

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint),
        typeof(string),
        typeof(InputContent),
        new PropertyMetadata(string.Empty)
    );

    public static readonly DependencyProperty TrailingProperty = DependencyProperty.Register(
        nameof(Trailing),
        typeof(object),
        typeof(InputContent)
    );

    public static readonly DependencyProperty IsTitleProperty = DependencyProperty.Register(
        nameof(IsTitle),
        typeof(bool),
        typeof(InputContent),
        new PropertyMetadata(false)
    );

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

    public object? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }

    public bool IsTitle
    {
        get => (bool)GetValue(IsTitleProperty);
        set => SetValue(IsTitleProperty, value);
    }
}

public sealed class ActionContent : ContentPrimitiveItemsControl { }

public sealed class CollectionContent : ContentPrimitiveItemsControl { }

public sealed class CustomContent : ContentControl { }

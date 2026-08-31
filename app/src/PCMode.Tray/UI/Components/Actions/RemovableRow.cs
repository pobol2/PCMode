using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace PCMode.Tray.UI.Components.Actions;

[ContentProperty(nameof(Content))]
[TemplatePart(Name = PartRemoveButton, Type = typeof(System.Windows.Controls.Button))]
public class RemovableRow : ContentControl
{
    private const string PartRemoveButton = "PART_RemoveButton";

    private System.Windows.Controls.Button? _removeButton;

    static RemovableRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(RemovableRow),
            new FrameworkPropertyMetadata(typeof(RemovableRow)));
    }

    public static readonly RoutedEvent RemoveClickEvent =
        EventManager.RegisterRoutedEvent(
            nameof(RemoveClick),
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(RemovableRow));

    public event RoutedEventHandler RemoveClick
    {
        add => AddHandler(RemoveClickEvent, value);
        remove => RemoveHandler(RemoveClickEvent, value);
    }

    public override void OnApplyTemplate()
    {
        if (_removeButton is not null)
            _removeButton.Click -= OnRemoveButtonClick;

        base.OnApplyTemplate();

        _removeButton = GetTemplateChild(PartRemoveButton) as System.Windows.Controls.Button;

        if (_removeButton is not null)
            _removeButton.Click += OnRemoveButtonClick;
    }

    private void OnRemoveButtonClick(object sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(RemoveClickEvent, this));
    }
}

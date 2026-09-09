using System.Windows;

namespace PCMode.Tray.UI.Components.Inputs;

[TemplatePart(Name = PartBrowseButton, Type = typeof(System.Windows.Controls.Button))]
public class BrowseFileRow : System.Windows.Controls.Control
{
    private const string PartBrowseButton = "PART_BrowseButton";

    private System.Windows.Controls.Button? _browseButton;

    static BrowseFileRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(BrowseFileRow),
            new FrameworkPropertyMetadata(typeof(BrowseFileRow))
        );
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(BrowseFileRow),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault)
    );

    public static readonly DependencyProperty ToolTipTextProperty = DependencyProperty.Register(
        nameof(ToolTipText),
        typeof(string),
        typeof(BrowseFileRow),
        new PropertyMetadata(string.Empty)
    );

    public static readonly RoutedEvent BrowseClickEvent = EventManager.RegisterRoutedEvent(
        nameof(BrowseClick),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(BrowseFileRow)
    );

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string ToolTipText
    {
        get => (string)GetValue(ToolTipTextProperty);
        set => SetValue(ToolTipTextProperty, value);
    }

    public event RoutedEventHandler BrowseClick
    {
        add => AddHandler(BrowseClickEvent, value);
        remove => RemoveHandler(BrowseClickEvent, value);
    }

    public override void OnApplyTemplate()
    {
        if (_browseButton is not null)
            _browseButton.Click -= OnBrowseButtonClick;

        base.OnApplyTemplate();

        _browseButton = GetTemplateChild(PartBrowseButton) as System.Windows.Controls.Button;

        if (_browseButton is not null)
            _browseButton.Click += OnBrowseButtonClick;
    }

    private void OnBrowseButtonClick(object sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(BrowseClickEvent, this));
    }
}

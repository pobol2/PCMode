using System.Windows;
using PCMode.Engine;

namespace PCMode.Tray.Features.Spaces;

/// <summary>One selectable source kind, in the words a user reads.</summary>
public sealed record SourceKindOption(SourceKind Kind, string Label);

/// <summary>Asks for the two things a Source needs before it can exist: a name, and what kind of thing it is.</summary>
public partial class NewSourceWindow : Window
{
    public NewSourceWindow()
    {
        InitializeComponent();

        KindBox.ItemsSource = (SourceKindOption[])
        [
            new(SourceKind.PcApplication, "Application on this PC"),
            new(SourceKind.ExternalDevice, "External device on an input"),
            new(SourceKind.TvApplication, "Application on the television"),
        ];

        KindBox.SelectedIndex = 0;
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>What the source is called.</summary>
    public string SourceName { get; private set; } = string.Empty;

    /// <summary>What kind of thing it is.</summary>
    public SourceKind Kind { get; private set; } = SourceKind.PcApplication;

    private void OnCreateClick(object sender, RoutedEventArgs e) => TryAccept();

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            TryAccept();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TryAccept()
    {
        string name = NameBox.Text.Trim();

        if (name.Length == 0)
        {
            ErrorText.Text = "Give it a name.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        SourceName = name;
        Kind = KindBox.SelectedItem is SourceKindOption option ? option.Kind : SourceKind.PcApplication;

        // Set last: assigning DialogResult closes the window immediately.
        DialogResult = true;
    }
}

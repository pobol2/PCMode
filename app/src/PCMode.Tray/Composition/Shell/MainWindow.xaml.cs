using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PCMode.Tray.Navigation;

namespace PCMode.Tray.Shell;

public partial class MainWindow : Window
{
    private readonly ShellPages _pages;

    public MainWindow(ShellPages pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        _pages = pages;
        InitializeComponent();

        IReadOnlyList<NavigationEntry> entries = NavigationEntry.Build(NavigationRegistry.Items);
        NavigationList.ItemsSource = entries;
        if (entries.Count > 0)
            NavigationList.SelectedIndex = 0;
    }

    /// <summary>Set only when the application has completed its exit decision.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Brings the window to the foreground, restoring it first if minimized or hidden.</summary>
    public void ShowAndActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavigationList.SelectedItem is NavigationEntry entry)
            PageHost.Content = _pages.Activate(entry.Page);
    }

    private void OnExpanderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NavigationEntry entry)
            entry.IsExpanded = !entry.IsExpanded;
    }
}

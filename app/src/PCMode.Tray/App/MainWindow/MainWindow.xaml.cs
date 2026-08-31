using System.ComponentModel;
using System.Windows;

namespace PCMode.Tray;

public partial class MainWindow : Window
{
    public MainWindow(object viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
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
}

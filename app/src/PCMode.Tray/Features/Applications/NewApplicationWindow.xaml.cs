using System.Windows;
using PCMode.Engine;

namespace PCMode.Tray.Features.Applications;

public partial class NewApplicationWindow : Window
{
    private readonly Func<string, bool, string, LaunchCommandEdit, Task<string?>> _create;

    public NewApplicationWindow(Func<string, bool, string, LaunchCommandEdit, Task<string?>> create)
    {
        ArgumentNullException.ThrowIfNull(create);

        InitializeComponent();
        _create = create;
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnProcessMonitoringChanged(object sender, RoutedEventArgs e)
    {
        if (ProcessRow is null)
        {
            return;
        }

        ProcessRow.Visibility = MonitorProcessOption.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnStartKindChanged(object sender, RoutedEventArgs e)
    {
        if (UriBox is null || ProgramRow is null)
        {
            return;
        }

        bool uri = UriOption.IsChecked == true;
        ProgramRow.Visibility = uri ? Visibility.Collapsed : Visibility.Visible;
        UriBox.Visibility = uri ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the program to launch",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            PathBox.Text = dialog.FileName;
        }
    }

    private async void OnCreateClick(object sender, RoutedEventArgs e) => await TryCreateAsync().ConfigureAwait(true);

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private async Task TryCreateAsync()
    {
        string name = NameBox.Text.Trim();
        bool monitorProcess = MonitorProcessOption.IsChecked == true;
        bool launchOnly = !monitorProcess;
        string process = ProcessBox.Text.Trim();
        bool uri = UriOption.IsChecked == true;
        string target = (uri ? UriBox.Text : PathBox.Text).Trim();

        if (name.Length == 0)
        {
            ShowError("Give it a name.");
            return;
        }

        if (target.Length == 0)
        {
            ShowError(uri ? "Give the URI to open." : "Give the program to launch, or browse to it.");
            return;
        }

        if (monitorProcess && process.Length == 0)
        {
            AdvancedExpander.IsExpanded = true;
            ShowError("Give the process name to monitor, without '.exe'.");
            ProcessBox.Focus();
            return;
        }

        LaunchCommandEdit start = uri
            ? new LaunchCommandEdit(null, [], target)
            : new LaunchCommandEdit(target, [], null);

        IsEnabled = false;

        string? problem;

        try
        {
            problem = await _create(name, launchOnly, process, start).ConfigureAwait(true);
        }
        finally
        {
            IsEnabled = true;
        }

        if (problem is null)
        {
            DialogResult = true;
            return;
        }

        ShowError(problem);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}

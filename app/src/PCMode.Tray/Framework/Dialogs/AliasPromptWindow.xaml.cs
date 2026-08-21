using System.Windows;
using System.Windows.Input;

namespace PCMode.Tray.Dialogs;

/// <summary>A modal for choosing a name and reporting validation errors.</summary>
public partial class AliasPromptWindow : Window
{
    private readonly Func<string, Task<string?>> _save;

    public AliasPromptWindow(string title, string message, string initialValue, Func<string, Task<string?>> save)
    {
        ArgumentNullException.ThrowIfNull(save);

        InitializeComponent();

        _save = save;
        Title = title;
        Prompt.Text = message;
        AliasBox.Text = initialValue;
        AliasBox.SelectAll();
        Loaded += (_, _) => AliasBox.Focus();
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await TrySaveAsync().ConfigureAwait(true);

    private async void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await TrySaveAsync().ConfigureAwait(true);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private async Task TrySaveAsync()
    {
        string alias = AliasBox.Text.Trim();

        if (alias.Length == 0)
        {
            ShowError("Give it a name.");
            return;
        }

        IsEnabled = false;

        string? problem;

        try
        {
            problem = await _save(alias).ConfigureAwait(true);
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

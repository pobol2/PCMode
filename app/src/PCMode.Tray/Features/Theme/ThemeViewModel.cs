using System.Collections.ObjectModel;
using PCMode.Engine;
using PCMode.Tray.Configuration;
using PCMode.Tray.Presentation;

namespace PCMode.Tray.Features.Theme;

/// <summary>One of the two fixed values <see cref="WindowsThemeProvider"/> accepts.</summary>
public sealed record ThemeChoiceItem(string Name, bool IsCurrent) : IConfigurationSelectorItem
{
    object IConfigurationSelectorItem.Key => Name;
}

/// <summary>
/// Switches the live Windows theme immediately, for testing what a Space's
/// "theme" resource would look like without requiring one.
/// </summary>
/// <remarks>
/// NOT A SAVED SETTING
///
/// <see cref="WindowsThemeProvider"/> has no named, creatable entries of its
/// own -- only the two fixed values Light and Dark -- and nothing here is
/// written to the configuration file. A Space that should always use a
/// particular theme still sets that on the Space itself; this page only
/// changes what Windows is using right now, the same way Cursor's Activate
/// and Rgb's Activate move the machine without touching a saved value. There
/// is deliberately no Save/Discard footer.
/// </remarks>
public sealed class ThemeViewModel : ConfigurationPageViewModelBase
{
    private const string Category = "Theme";

    public ThemeViewModel(IConfigurationWorkspace workspace) : base(workspace)
    {
    }

    public ObservableCollection<ThemeChoiceItem> Choices { get; } = [];

    public string? SelectedChoice
    {
        get => (string?)SelectedKey;
        set
        {
            if (Equals(SelectedKey, value))
            {
                return;
            }

            SelectedKey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedChoice));
            OnPropertyChanged(nameof(CanApplySelected));
        }
    }

    public override void SelectKey(object key) => SelectedChoice = key as string;

    public bool HasSelectedChoice => SelectedChoice is not null;

    public bool CanApplySelected => !IsBusy && HasSelectedChoice;

    public override bool CanSave => false;

    public override void Refresh()
    {
        (WindowsThemeProvider? provider, string? problem) = ReadProvider<WindowsThemeProvider>();

        Choices.Clear();

        if (provider is null)
        {
            StatusMessage = problem ?? "The configuration could not be read.";
            SelectedKey = null;
            OnPropertyChanged(nameof(SelectedChoice));
            OnPropertyChanged(nameof(HasSelectedChoice));
            OnPropertyChanged(nameof(CanApplySelected));
            return;
        }

        string? current = SafeRead(provider);

        Choices.Add(new ThemeChoiceItem(WindowsThemeProvider.Light, current == WindowsThemeProvider.Light));
        Choices.Add(new ThemeChoiceItem(WindowsThemeProvider.Dark, current == WindowsThemeProvider.Dark));

        StatusMessage = current switch
        {
            WindowsThemeProvider.Light => "Windows is currently set to Light.",
            WindowsThemeProvider.Dark => "Windows is currently set to Dark.",
            _ => "The current Windows theme could not be determined.",
        };

        SelectedKey = current ?? WindowsThemeProvider.Light;
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(HasSelectedChoice));
        OnPropertyChanged(nameof(CanApplySelected));
    }

    /// <summary>Sets the selected value through a freshly built provider, then verifies it.</summary>
    public async Task<string?> ApplySelectedAsync(CancellationToken cancellationToken = default)
    {
        ClearError();

        if (SelectedChoice is not { } value)
        {
            return SetError("Choose Light or Dark first.");
        }

        (WindowsThemeProvider? provider, string? problem) = ReadProvider<WindowsThemeProvider>();

        if (provider is null)
        {
            StatusMessage = problem ?? "The configuration could not be read.";
            return SetError(StatusMessage);
        }

        IsBusy = true;
        StatusMessage = $"Setting Windows theme to {value}...";

        try
        {
            await Task.Run(() => provider.Apply(value, cancellationToken), cancellationToken).ConfigureAwait(true);

            string? current = SafeRead(provider);

            if (string.Equals(current, value, StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = $"Windows theme set to {value}.";
                Workspace.Note(Category, StatusMessage);
                Refresh();
                return null;
            }

            StatusMessage = current is null
                ? $"{value} was applied, but the current theme could not be verified."
                : $"{value} was applied, but Windows currently reads back {current}.";
            Workspace.Warn(Category, StatusMessage);
            return SetError(StatusMessage);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not set the Windows theme to {value}. {exception.Message}";
            Workspace.Fault(Category, $"Could not set the Windows theme to {value}.", exception);
            return SetError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected override string? SaveChangesCore() => null;

    protected override void DiscardChangesCore() => Refresh();

    protected override void OnBusyStateChanged()
    {
        base.OnBusyStateChanged();
        OnPropertyChanged(nameof(CanApplySelected));
    }
}

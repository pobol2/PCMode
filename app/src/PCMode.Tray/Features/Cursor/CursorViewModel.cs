using System.Collections.ObjectModel;
using PCMode.Engine;
using PCMode.Tray.Configuration;
using PCMode.Tray.Presentation;

namespace PCMode.Tray.Features.Cursor;

/// <summary>The two ways an axis can be pinned, labelled precisely for the axis it applies to.</summary>
public static class CursorAxisMode
{
    public const string Percentage = "Percentage";
    public const string XFromRight = "From right";
    public const string YFromBottom = "From bottom";

    public static IReadOnlyList<string> XChoices { get; } = [Percentage, XFromRight];
    public static IReadOnlyList<string> YChoices { get; } = [Percentage, YFromBottom];
}

/// <summary>One configured cursor position, as shown in the selector.</summary>
public sealed record CursorPositionItem(string Name) : IConfigurationSelectorItem
{
    object IConfigurationSelectorItem.Key => Name;
}

/// <summary>
/// Edits named cursor positions graphically: a percentage or an edge distance
/// per axis, using exactly the rules <see cref="CursorPosition"/> already
/// supports.
/// </summary>
/// <remarks>
/// NO NEW SEMANTICS
///
/// X is a percentage across the display or a distance from the right edge; Y
/// is a percentage down the display or a distance from the bottom edge.
/// "Edge offset" is deliberately not used as a label anywhere here -- it would
/// suggest a left or top offset also exists, and it does not.
///
/// EDITING NEVER MOVES THE POINTER
///
/// Saving only changes 'providers.cursor.positions'. The explicit Activate
/// action applies the selected saved position through <see cref="CursorProvider"/>
/// without applying or changing a Space.
///
/// CREATE COMMITS IMMEDIATELY
///
/// Unlike the page this replaces, naming a new position saves it at once, at
/// the display's center, rather than holding an unsaved draft alongside the
/// selector's saved items -- the shared configuration-page contract expects
/// the selection to always name something real. Adjusting it afterward and
/// saving works exactly as it does for any other position.
/// </remarks>
public sealed class CursorViewModel : ConfigurationPageViewModelBase
{
    private const string Category = "Cursor";

    private string _xMode = CursorAxisMode.Percentage;
    private double _xValue = 50;
    private string _yMode = CursorAxisMode.Percentage;
    private double _yValue = 50;

    public CursorViewModel(IConfigurationWorkspace workspace) : base(workspace)
    {
    }

    public ObservableCollection<CursorPositionItem> Positions { get; } = [];

    public IReadOnlyList<string> XModeChoices => CursorAxisMode.XChoices;

    public IReadOnlyList<string> YModeChoices => CursorAxisMode.YChoices;

    public string? SelectedPosition
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
            OnPropertyChanged(nameof(HasSelectedPosition));
            OnPropertyChanged(nameof(CanActivateSelected));

            if (value is not null)
            {
                Load(value);
            }
            else
            {
                ClearForm();
            }
        }
    }

    public override void SelectKey(object key) => SelectedPosition = key as string;

    public bool HasSelectedPosition => SelectedPosition is not null;

    public bool CanActivateSelected => !IsBusy && HasSelectedPosition;

    public string XMode
    {
        get => _xMode;
        set
        {
            if (SetDirtyProperty(ref _xMode, value))
            {
                OnPropertyChanged(nameof(XUnit));
                RaiseValidation();
            }
        }
    }

    public double XValue
    {
        get => _xValue;
        set
        {
            if (SetDirtyProperty(ref _xValue, value))
            {
                RaiseValidation();
            }
        }
    }

    public string YMode
    {
        get => _yMode;
        set
        {
            if (SetDirtyProperty(ref _yMode, value))
            {
                OnPropertyChanged(nameof(YUnit));
                RaiseValidation();
            }
        }
    }

    public double YValue
    {
        get => _yValue;
        set
        {
            if (SetDirtyProperty(ref _yValue, value))
            {
                RaiseValidation();
            }
        }
    }

    public string XUnit => XMode == CursorAxisMode.Percentage ? "%" : "px";

    public string YUnit => YMode == CursorAxisMode.Percentage ? "%" : "px";

    public override string? ValidationProblem =>
        ValidateAxis("Horizontal position", XMode, XValue) ??
        ValidateAxis("Vertical position", YMode, YValue);

    public override bool CanSave =>
        HasSelectedPosition &&
        HasUnsavedChanges &&
        !IsBusy &&
        ValidationProblem is null;

    public override void Refresh()
    {
        string? keep = SelectedPosition;

        (CursorProvider? provider, string? problem) = ReadProvider<CursorProvider>();

        if (provider is null)
        {
            Positions.Clear();
            ClearForm();
            StatusMessage = problem ?? "The configuration could not be read.";
            return;
        }

        Positions.Clear();

        foreach (string name in provider.PositionNames)
        {
            Positions.Add(new CursorPositionItem(name));
        }

        StatusMessage = $"{Positions.Count} cursor position(s).";

        SelectedKey = keep is not null && Positions.Any(item => item.Name == keep)
            ? keep
            : Positions.FirstOrDefault()?.Name;
        OnPropertyChanged(nameof(SelectedPosition));
        OnPropertyChanged(nameof(CanDiscard));
        OnPropertyChanged(nameof(HasSelectedPosition));

        if (SelectedPosition is not null)
        {
            Load(SelectedPosition);
        }
        else
        {
            ClearForm();
        }
    }

    /// <summary>Updates the axis values from a point in the display preview.</summary>
    public void SetFromPreview(double xFraction, double yFraction, int displayWidth, int displayHeight)
    {
        xFraction = Math.Clamp(xFraction, 0, 1);
        yFraction = Math.Clamp(yFraction, 0, 1);

        XValue = XMode == CursorAxisMode.Percentage
            ? Math.Round(xFraction * 100, 1, MidpointRounding.AwayFromZero)
            : Math.Round((1 - xFraction) * Math.Max(1, displayWidth), MidpointRounding.AwayFromZero);

        YValue = YMode == CursorAxisMode.Percentage
            ? Math.Round(yFraction * 100, 1, MidpointRounding.AwayFromZero)
            : Math.Round((1 - yFraction) * Math.Max(1, displayHeight), MidpointRounding.AwayFromZero);
    }

    public double PreviewXFraction(int displayWidth) => XMode == CursorAxisMode.Percentage
        ? Math.Clamp(XValue / 100, 0, 1)
        : Math.Clamp((Math.Max(1, displayWidth) - XValue) / Math.Max(1, displayWidth), 0, 1);

    public double PreviewYFraction(int displayHeight) => YMode == CursorAxisMode.Percentage
        ? Math.Clamp(YValue / 100, 0, 1)
        : Math.Clamp((Math.Max(1, displayHeight) - YValue) / Math.Max(1, displayHeight), 0, 1);

    /// <summary>Creates a new position, saved at the display's center.</summary>
    public string? CreatePosition(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Give the position a name.";
        }

        string trimmed = name.Trim();

        if (Positions.Any(item => string.Equals(item.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{trimmed}' is already a configured cursor position.";
        }

        return Persist(trimmed, 50, null, 50, null, trimmed, $"'{trimmed}' created.");
    }

    /// <summary>Creates a full copy of <paramref name="sourceName"/> under a new name.</summary>
    public string? DuplicatePosition(string name, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Give the position a name.";
        }

        string trimmed = name.Trim();

        if (Positions.Any(item => string.Equals(item.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{trimmed}' is already a configured cursor position.";
        }

        (CursorProvider? provider, string? problem) = ReadProvider<CursorProvider>();

        if (provider is null)
        {
            return problem;
        }

        CursorPosition? source = provider.Positions.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, sourceName, StringComparison.OrdinalIgnoreCase));

        if (source is null)
        {
            return $"'{sourceName}' is not a configured cursor position.";
        }

        return Persist(
            trimmed, source.XPercent, source.XFromRight, source.YPercent, source.YFromBottom,
            trimmed, $"'{trimmed}' created from '{sourceName}'.");
    }

    /// <summary>
    /// Moves the pointer to the selected saved position without applying a
    /// mode, changing the current Space, or touching the configuration file.
    /// </summary>
    public string? ActivateSelected()
    {
        ClearError();

        if (SelectedPosition is null)
        {
            return SetError("Choose a cursor position first.");
        }

        (CursorProvider? provider, string? problem) = ReadProvider<CursorProvider>();

        if (provider is null)
        {
            StatusMessage = problem ?? "The configuration could not be read.";
            return SetError(StatusMessage);
        }

        string name = SelectedPosition;
        IsBusy = true;

        try
        {
            // Use the configured provider itself so this follows its primary
            // display resolution, edge-offset, clamping, and error behavior.
            provider.Apply(name, CancellationToken.None);
            return ReportSuccess($"Cursor moved to '{name}'.");
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not move the cursor to '{name}'. {exception.Message}";
            Workspace.Fault(Category, $"Could not move the cursor to '{name}'.", exception);
            return SetError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected override string? SaveChangesCore()
    {
        if (SelectedPosition is null)
        {
            return "Choose a cursor position first.";
        }

        if (ValidationProblem is string problem)
        {
            StatusMessage = problem;
            return problem;
        }

        double? xPercent = XMode == CursorAxisMode.Percentage ? XValue : null;
        int? xFromRight = xPercent is null ? (int)Math.Round(XValue, MidpointRounding.AwayFromZero) : null;
        double? yPercent = YMode == CursorAxisMode.Percentage ? YValue : null;
        int? yFromBottom = yPercent is null ? (int)Math.Round(YValue, MidpointRounding.AwayFromZero) : null;

        return Persist(SelectedPosition, xPercent, xFromRight, yPercent, yFromBottom, SelectedPosition, $"'{SelectedPosition}' saved.");
    }

    public string? RenameSelected(string newName)
    {
        if (SelectedPosition is null)
        {
            return "Choose a cursor position first.";
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            return "Give the position a name.";
        }

        string trimmed = newName.Trim();
        string oldName = SelectedPosition;

        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = CursorPositionConfigurationStore.Rename(Workspace.Root, oldName, trimmed);

            if (!outcome.Success)
            {
                StatusMessage = outcome.Problem ?? "That could not be saved.";
                Workspace.Warn(Category, StatusMessage);
                return outcome.Problem;
            }

            Refresh();
            SelectedPosition = trimmed;
            return ReportSuccess($"'{oldName}' renamed to '{trimmed}'.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public string? DeleteSelected()
    {
        ClearError();

        if (SelectedPosition is null)
        {
            return SetError("Choose a cursor position first.");
        }

        string name = SelectedPosition;
        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = CursorPositionConfigurationStore.Delete(Workspace.Root, name);

            if (!outcome.Success)
            {
                StatusMessage = outcome.Problem ?? "That could not be deleted.";
                Workspace.Warn(Category, StatusMessage);
                return SetError(StatusMessage);
            }

            Refresh();
            return ReportSuccess($"'{name}' deleted.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected override void DiscardChangesCore() => Refresh();

    private string? Persist(
        string name,
        double? xPercent, int? xFromRight,
        double? yPercent, int? yFromBottom,
        string selectAfter, string message)
    {
        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = CursorPositionConfigurationStore.Save(
                Workspace.Root, name, xPercent, xFromRight, yPercent, yFromBottom);

            if (!outcome.Success)
            {
                StatusMessage = outcome.Problem ?? "That could not be saved.";
                Workspace.Warn(Category, StatusMessage);
                return outcome.Problem;
            }

            Refresh();
            SelectedPosition = selectAfter;
            return ReportSuccess(message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Load(string name)
    {
        (CursorProvider? provider, string? problem) = ReadProvider<CursorProvider>();

        if (provider is null)
        {
            StatusMessage = problem ?? "The configuration could not be read.";
            return;
        }

        CursorPosition? position = provider.Positions.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

        RunWithoutDirtyTracking(() => ApplyToForm(position));
        ClearDirtyState();
        RaiseValidation();
    }

    private void ClearForm()
    {
        RunWithoutDirtyTracking(() => ApplyToForm(null));
        ClearDirtyState();
    }

    private void ApplyToForm(CursorPosition? position)
    {
        if (position?.XPercent is double xPercent)
        {
            XMode = CursorAxisMode.Percentage;
            XValue = xPercent;
        }
        else if (position?.XFromRight is int xFromRight)
        {
            XMode = CursorAxisMode.XFromRight;
            XValue = xFromRight;
        }
        else
        {
            XMode = CursorAxisMode.Percentage;
            XValue = 50;
        }

        if (position?.YPercent is double yPercent)
        {
            YMode = CursorAxisMode.Percentage;
            YValue = yPercent;
        }
        else if (position?.YFromBottom is int yFromBottom)
        {
            YMode = CursorAxisMode.YFromBottom;
            YValue = yFromBottom;
        }
        else
        {
            YMode = CursorAxisMode.Percentage;
            YValue = 50;
        }
    }

    private static string? ValidateAxis(string label, string mode, double value)
    {
        if (!double.IsFinite(value))
        {
            return $"{label} must be a number.";
        }

        if (mode == CursorAxisMode.Percentage && (value < 0 || value > 100))
        {
            return $"{label} must be between 0 and 100%.";
        }

        if (mode != CursorAxisMode.Percentage && (value < 0 || value > int.MaxValue))
        {
            return $"{label} must be a non-negative pixel distance.";
        }

        return null;
    }

    private string? ReportSuccess(string message)
    {
        StatusMessage = Workspace.Apply(message);
        return null;
    }

    private void RaiseValidation()
    {
        NotifyValidationChanged(
            nameof(ValidationProblem),
            nameof(CanSave));
    }

    protected override void OnBusyStateChanged()
    {
        base.OnBusyStateChanged();
        OnPropertyChanged(nameof(CanActivateSelected));
    }
}

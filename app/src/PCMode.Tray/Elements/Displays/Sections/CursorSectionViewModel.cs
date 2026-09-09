using System.Windows.Input;
using PCMode.Engine;
using PCMode.Tray.Elements;
using PCMode.Tray.Elements.Configuration;

namespace PCMode.Tray.Elements.Displays.Sections;

/// <summary>The two ways an axis can be pinned, labelled precisely for the axis it applies to.</summary>
public static class CursorAxisMode
{
    public const string Percentage = "Percentage";
    public const string XFromRight = "From right";
    public const string YFromBottom = "From bottom";

    public static IReadOnlyList<string> XChoices { get; } = [Percentage, XFromRight];
    public static IReadOnlyList<string> YChoices { get; } = [Percentage, YFromBottom];
}

/// <summary>
/// The cursor-position facet of a selected display layout: the X/Y of
/// whichever saved position is currently linked to that layout (see
/// <see cref="LayoutSectionViewModel.CursorPositionName"/>).
/// </summary>
/// <remarks>
/// A SEPARATE STORE FROM THE LAYOUT ITSELF
///
/// <see cref="LayoutSectionViewModel"/> owns WHICH position is linked --
/// written not into the layout's own document (a layout can never require the
/// cursor; see <see cref="DisplayLayoutCursorLink"/>) but as a 'cursor'
/// resource on whichever Space/Activity/Source sets 'display' to that layout.
/// This section owns the X/Y VALUES of that position, an entirely independent
/// 'providers.cursor.positions' entry saved through
/// <see cref="CursorPositionConfigurationStore"/> -- the same store the
/// standalone Cursor page used to write to directly. The two never touch each
/// other's store; they agree only by both reading the same hierarchy fresh
/// from <see cref="Configuration.IConfigurationWorkspace"/>, the "no second
/// source of truth" pattern every feature here already follows.
///
/// NOTHING TO EDIT UNTIL SOMETHING IS LINKED
///
/// With no position linked, this section has no save target -- editing X/Y
/// would have nowhere to go. <see cref="HasLinkedPosition"/> gates the editor;
/// linking (or creating and linking) a position is the Layout section's job.
/// </remarks>
/// <summary>
/// The four writes that change WHICH cursor position a layout uses.
/// </summary>
/// <remarks>
/// Supplied by whoever owns both halves of that -- the link is a resource on a
/// Space/Activity/Source, the position itself is its own store entry -- which
/// is <see cref="Displays.DisplaysViewModel"/>. This section owns the
/// commands, the prompts and the confirmations; it does not own the writes,
/// because neither of the two stores involved is its own.
/// </remarks>
public sealed record CursorPositionWrites(
    Func<string, string?> Create,
    Func<string, string?> Rename,
    Func<string, string?> Duplicate,
    Func<string?> Delete
);

public sealed class CursorSectionViewModel : ObservableObject, IConfigurationSection
{
    private readonly IConfigurationWorkspace _workspace;
    private readonly Action _onChanged;
    private readonly IConfigurationPrompts _prompts;
    private readonly CursorPositionWrites? _writes;

    private double _targetWidth = 1920;
    private double _targetHeight = 1080;

    private string? _linkedName;
    private string _xMode = CursorAxisMode.Percentage;
    private double _xValue = 50;
    private string _yMode = CursorAxisMode.Percentage;
    private double _yValue = 50;

    /// <param name="prompts">How this section asks for a position name and confirms a deletion; see <see cref="IConfigurationPrompts"/>.</param>
    /// <param name="writes">See <see cref="CursorPositionWrites"/>. Null leaves the four commands inert, which is what an unhosted construction wants.</param>
    public CursorSectionViewModel(
        IConfigurationWorkspace workspace,
        Action onChanged,
        IConfigurationPrompts? prompts = null,
        CursorPositionWrites? writes = null
    )
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(onChanged);

        _workspace = workspace;
        _onChanged = onChanged;
        _prompts = prompts ?? NoConfigurationPrompts.Instance;
        _writes = writes;

        CreateCommand = new RelayCommand(PromptCreate, () => _writes is not null);
        RenameCommand = new RelayCommand(PromptRename, () => HasLinkedPosition);
        DuplicateCommand = new RelayCommand(PromptDuplicate, () => HasLinkedPosition);
        DeleteCommand = new RelayCommand(ConfirmDelete, () => HasLinkedPosition);
    }

    /// <summary>Creates a position at the display's centre and links this layout to it.</summary>
    public ICommand CreateCommand { get; }

    public ICommand RenameCommand { get; }

    public ICommand DuplicateCommand { get; }

    public ICommand DeleteCommand { get; }

    /// <summary>
    /// The real pixel size of the display a from-right/from-bottom coordinate
    /// is measured against -- this layout's own primary monitor, pushed in by
    /// <see cref="Displays.DisplaysViewModel"/> rather than read here, since
    /// which monitor is primary belongs to the layout section.
    /// </summary>
    public double TargetWidth
    {
        get => _targetWidth;
        private set => SetProperty(ref _targetWidth, value);
    }

    public double TargetHeight
    {
        get => _targetHeight;
        private set => SetProperty(ref _targetHeight, value);
    }

    internal void SetTargetBounds(double width, double height)
    {
        TargetWidth = width > 0 ? width : 1920;
        TargetHeight = height > 0 ? height : 1080;
    }

    // These four skip the unsaved-changes guard the DDC/television actions
    // run, exactly as the page did before they became commands: creating,
    // renaming or duplicating a cursor position is part of the edit in
    // progress, not a departure from it.

    private void PromptCreate()
    {
        if (_writes is not { } writes)
        {
            return;
        }

        _prompts.PromptForName(
            "Create cursor position",
            "Name the new cursor position. It is created at the display's center and linked to this layout; adjust it below, then Save.",
            string.Empty,
            writes.Create
        );
    }

    private void PromptRename()
    {
        if (_writes is not { } writes || _linkedName is not string current)
        {
            return;
        }

        _prompts.PromptForName(
            "Rename cursor position",
            $"Rename '{current}'. Every layout that requires it, and any mode that names it directly, is updated to match.",
            current,
            writes.Rename
        );
    }

    private void PromptDuplicate()
    {
        if (_writes is not { } writes || _linkedName is not string current)
        {
            return;
        }

        _prompts.PromptForName(
            "Duplicate cursor position",
            $"Name the copy of '{current}'. This layout is linked to the copy; '{current}' itself is untouched.",
            $"{current} copy",
            writes.Duplicate
        );
    }

    private void ConfirmDelete()
    {
        if (_writes is not { } writes || _linkedName is not string current)
        {
            return;
        }

        if (
            _prompts.Confirm(
                $"Delete the cursor position '{current}'? This removes it outright, not just the link -- "
                    + "it is refused if a mode or another layout still uses it."
            )
        )
        {
            writes.Delete();
        }
    }

    public bool HasLinkedPosition => _linkedName is not null;

    public string? LinkedPositionName => _linkedName;

    public IReadOnlyList<string> XModeChoices => CursorAxisMode.XChoices;

    public IReadOnlyList<string> YModeChoices => CursorAxisMode.YChoices;

    public string XMode
    {
        get => _xMode;
        set
        {
            if (SetProperty(ref _xMode, value))
            {
                OnPropertyChanged(nameof(XUnit));
                _onChanged();
            }
        }
    }

    public double XValue
    {
        get => _xValue;
        set
        {
            if (SetProperty(ref _xValue, value))
            {
                _onChanged();
            }
        }
    }

    public string YMode
    {
        get => _yMode;
        set
        {
            if (SetProperty(ref _yMode, value))
            {
                OnPropertyChanged(nameof(YUnit));
                _onChanged();
            }
        }
    }

    public double YValue
    {
        get => _yValue;
        set
        {
            if (SetProperty(ref _yValue, value))
            {
                _onChanged();
            }
        }
    }

    public string XUnit => XMode == CursorAxisMode.Percentage ? "%" : "px";

    public string YUnit => YMode == CursorAxisMode.Percentage ? "%" : "px";

    public string? ValidationProblem =>
        !HasLinkedPosition
            ? null
            : ValidateAxis("Horizontal position", XMode, XValue) ?? ValidateAxis("Vertical position", YMode, YValue);

    /// <summary>Reads which position (if any) is linked to the named layout ON DISK, and loads its X/Y.</summary>
    /// <remarks>
    /// Correct for a first load, a reactivation, or a discard -- every case
    /// where nothing in memory has diverged from disk yet. It is NOT what
    /// re-syncs this section when <see cref="LayoutSectionViewModel.CursorPositionName"/>
    /// changes mid-edit: the link only moves to disk once
    /// <see cref="LayoutSectionViewModel.ApplyCursorLink"/> runs, after the
    /// layout itself has saved, so reading disk again immediately after
    /// picking a different position would just see the OLD link and silently
    /// undo the change. <see cref="Displays.DisplaysViewModel"/> calls
    /// <see cref="LoadLinkedPosition"/> for that case instead, with the name
    /// <see cref="LayoutSectionViewModel"/> already has in memory.
    /// </remarks>
    public void Load(object? entityKey)
    {
        string? layoutName = entityKey as string;

        string? linked = layoutName is null
            ? null
            : DisplayLayoutCursorLink.Find(_workspace.Read().Configuration.Spaces, layoutName);

        LoadLinkedPosition(linked);
    }

    /// <summary>Loads the named position's X/Y directly, without re-deriving the link from disk. See <see cref="Load"/>'s remarks.</summary>
    public void LoadLinkedPosition(string? linkedName)
    {
        _linkedName = linkedName;
        OnPropertyChanged(nameof(HasLinkedPosition));
        OnPropertyChanged(nameof(LinkedPositionName));

        (RenameCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DuplicateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();

        if (linkedName is null)
        {
            ApplyToForm(null);
            return;
        }

        (CursorProvider? cursor, _) = _workspace.ReadProvider<CursorProvider>();

        CursorPosition? position = cursor?.Positions.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, linkedName, StringComparison.OrdinalIgnoreCase)
        );

        ApplyToForm(position);
    }

    public string? Save()
    {
        if (_linkedName is not string name)
        {
            return null;
        }

        if (ValidationProblem is string problem)
        {
            return problem;
        }

        double? xPercent = XMode == CursorAxisMode.Percentage ? XValue : null;
        int? xFromRight = xPercent is null ? (int)Math.Round(XValue, MidpointRounding.AwayFromZero) : null;
        double? yPercent = YMode == CursorAxisMode.Percentage ? YValue : null;
        int? yFromBottom = yPercent is null ? (int)Math.Round(YValue, MidpointRounding.AwayFromZero) : null;

        ModeSaveOutcome outcome = CursorPositionConfigurationStore.Save(
            _workspace.Root,
            name,
            xPercent,
            xFromRight,
            yPercent,
            yFromBottom
        );

        return outcome.Success ? null : outcome.Problem ?? "The cursor position could not be saved.";
    }

    /// <summary>
    /// Saves the currently edited X/Y under a new, separate position name --
    /// for duplicating the linked position. Does not touch the link itself;
    /// the caller (<see cref="Displays.DisplaysViewModel"/>) links the new
    /// name once this succeeds.
    /// </summary>
    internal string? SaveAs(string newName)
    {
        if (ValidationProblem is string problem)
        {
            return problem;
        }

        double? xPercent = XMode == CursorAxisMode.Percentage ? XValue : null;
        int? xFromRight = xPercent is null ? (int)Math.Round(XValue, MidpointRounding.AwayFromZero) : null;
        double? yPercent = YMode == CursorAxisMode.Percentage ? YValue : null;
        int? yFromBottom = yPercent is null ? (int)Math.Round(YValue, MidpointRounding.AwayFromZero) : null;

        ModeSaveOutcome outcome = CursorPositionConfigurationStore.Save(
            _workspace.Root,
            newName,
            xPercent,
            xFromRight,
            yPercent,
            yFromBottom
        );

        return outcome.Success ? null : outcome.Problem ?? "The cursor position could not be created.";
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
}

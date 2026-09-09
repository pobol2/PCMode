using System.Globalization;
using PCMode.Engine;
using PCMode.Tray.Elements;

namespace PCMode.Tray.Elements.Displays.Sections;

/// <summary>The four words a placement can use, in the order offered on screen.</summary>
public static class LayoutDirectionChoice
{
    public const string LeftOf = "LeftOf";
    public const string RightOf = "RightOf";
    public const string Above = "Above";
    public const string Below = "Below";

    public static IReadOnlyList<string> All { get; } = [LeftOf, RightOf, Above, Below];

    public static IReadOnlyList<LayoutDirectionOption> Options { get; } =
    [new(LeftOf, "Left of"), new(RightOf, "Right of"), new(Above, "Above"), new(Below, "Below")];

    public static string LabelOf(string value) =>
        Options.FirstOrDefault(choice => string.Equals(choice.Value, value, StringComparison.Ordinal))?.Label ?? value;

    public static string OppositeOf(string value) =>
        value switch
        {
            LeftOf => RightOf,
            RightOf => LeftOf,
            Above => Below,
            _ => Above,
        };
}

/// <summary>A friendly label backed by the canonical word stored in configuration.</summary>
public sealed record LayoutDirectionOption(string Value, string Label);

/// <summary>The three named alignments a monitor may sit at along its shared edge.</summary>
public static class LayoutAlignmentChoice
{
    public const string Start = "Start";
    public const string Center = "Center";
    public const string End = "End";

    public static IReadOnlyList<LayoutDirectionOption> Options { get; } =
    [new(Start, "Start (near edge)"), new(Center, "Center"), new(End, "End (far edge)")];

    public static string LabelOf(string value) =>
        Options.FirstOrDefault(choice => string.Equals(choice.Value, value, StringComparison.Ordinal))?.Label ?? value;
}

/// <summary>One monitor the layout being edited includes.</summary>
/// <remarks>
/// Ported from legacy's <c>LayoutMonitorRowViewModel</c> -- presentation
/// geometry, not business logic. A row exists only for a monitor the layout
/// actually uses: membership in the owning <see cref="SelectionList{TRow}"/>
/// IS inclusion, so there is no separate included/enabled flag.
/// <see cref="MonitorName"/> is the row's <see cref="ISelectionRow.Selection"/>
/// -- the picker in its card header writes it -- and so, unlike the fixed
/// one-row-per-monitor model this replaced, it can change over the row's life.
/// </remarks>
public sealed class LayoutMonitorRow : ObservableObject, ISelectionRow
{
    private readonly Action<LayoutMonitorRow, string> _onChanged;
    private string _monitorName;
    private bool _primary;
    private bool _isSelected;
    private string _direction = LayoutDirectionChoice.RightOf;
    private string? _anchor;
    private string _alignment = LayoutAlignmentChoice.Start;
    private int _offset;
    private string _selectedRefresh = NoPreference;
    private int _currentX;
    private int _currentY;
    private int _currentWidth;
    private int _currentHeight;
    private double _currentRefreshHz;
    private string _friendlyName = "Not currently detected by Windows";
    private string _windowsIdentifier = "Not currently mapped to a Windows display";
    private string? _devicePath;
    private string _mappingState = "Unavailable";
    private double _previewLeft;
    private double _previewTop;
    private double _previewWidth = 160;
    private double _previewHeight = 90;

    /// <summary>Shown in the refresh list for "let Windows choose".</summary>
    public const string NoPreference = "Automatic (Windows default)";

    public LayoutMonitorRow(string monitorName, Action<LayoutMonitorRow, string> onChanged)
    {
        _monitorName = monitorName;
        _onChanged = onChanged;
    }

    /// <summary>
    /// Which configured monitor this row is. Set by the picker in the row's
    /// card header; the owner re-resolves the monitor's Windows details and
    /// refresh rates in response (see <c>LayoutSectionViewModel.OnRowChanged</c>).
    /// </summary>
    public string MonitorName
    {
        get => _monitorName;
        set
        {
            string next = value ?? string.Empty;

            if (string.Equals(_monitorName, next, StringComparison.Ordinal))
            {
                return;
            }

            _monitorName = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Selection));
            OnPropertyChanged(nameof(SavedPlacementText));
            _onChanged(this, nameof(MonitorName));
        }
    }

    public string? Selection
    {
        get => MonitorName;
        set => MonitorName = value ?? string.Empty;
    }

    /// <summary>The monitors this row's picker may still offer -- see <see cref="ISelectionRow.AvailableSelections"/>.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> AvailableSelections { get; } = [];

    /// <summary>Whether this monitor is the one whose controls are shown beneath the arrangement.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public bool Primary
    {
        get => _primary;
        set
        {
            if (!SetProperty(ref _primary, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SavedPlacementText));
            _onChanged(this, nameof(Primary));
        }
    }

    public IReadOnlyList<string> DirectionChoices => LayoutDirectionChoice.All;

    public IReadOnlyList<LayoutDirectionOption> DirectionOptions => LayoutDirectionChoice.Options;

    public string Direction
    {
        get => _direction;
        set
        {
            if (string.Equals(_direction, value, StringComparison.Ordinal))
            {
                return;
            }

            _direction = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SavedPlacementText));
            _onChanged(this, nameof(Direction));
        }
    }

    /// <summary>Other included monitors this one may be placed against. Recomputed by the owner.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> AnchorChoices { get; } = [];

    public string? Anchor
    {
        get => _anchor;
        set
        {
            if (string.Equals(_anchor, value, StringComparison.Ordinal))
            {
                return;
            }

            _anchor = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SavedPlacementText));
            _onChanged(this, nameof(Anchor));
        }
    }

    public IReadOnlyList<LayoutDirectionOption> AlignmentOptions => LayoutAlignmentChoice.Options;

    /// <summary>Where along the shared edge this monitor sits -- "Start", "Center" or "End".</summary>
    public string Alignment
    {
        get => _alignment;
        set
        {
            if (string.Equals(_alignment, value, StringComparison.Ordinal))
            {
                return;
            }

            _alignment = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SavedPlacementText));
            _onChanged(this, nameof(Alignment));
        }
    }

    /// <summary>A further pixel nudge past <see cref="Alignment"/> -- the precise, typed equivalent of a drag that does not land exactly on a named alignment.</summary>
    public int Offset
    {
        get => _offset;
        set
        {
            if (_offset == value)
            {
                return;
            }

            _offset = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SavedPlacementText));
            _onChanged(this, nameof(Offset));
        }
    }

    /// <summary>The automatic choice, a discovered rate, or a configured rate discovery can no longer see.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> RefreshChoices { get; } = [NoPreference];

    public string SelectedRefresh
    {
        get => _selectedRefresh;
        set
        {
            if (string.Equals(_selectedRefresh, value, StringComparison.Ordinal))
            {
                return;
            }

            _selectedRefresh = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConfiguredRefreshText));
            _onChanged(this, nameof(SelectedRefresh));
        }
    }

    public double? RefreshHz =>
        double.TryParse(SelectedRefresh, NumberStyles.Float, CultureInfo.InvariantCulture, out double hz) ? hz : null;

    public string FriendlyName => _friendlyName;

    public string WindowsIdentifier => _windowsIdentifier;

    /// <summary>
    /// The raw device path Windows currently reports for this monitor, or
    /// null while it is not detected -- distinct from <see cref="WindowsIdentifier"/>,
    /// which falls back to display text for that same case. This is the
    /// identity a monitor-scoped section (DDC/CI, television) matches against
    /// a configured device path with, e.g. <c>Sections.MonitorDdcSectionViewModel</c>.
    /// </summary>
    public string? DevicePath => _devicePath;

    public string MappingState => _mappingState;

    public string CurrentPositionText =>
        _currentWidth > 0 && _currentHeight > 0
            ? $"X {_currentX:N0}, Y {_currentY:N0} px"
            : "Unavailable while the monitor is inactive";

    public string CurrentResolutionText =>
        _currentWidth > 0 && _currentHeight > 0
            ? $"{_currentWidth:N0} × {_currentHeight:N0} px"
            : "Unavailable while the monitor is inactive";

    public string CurrentRefreshText =>
        _currentRefreshHz > 0 ? $"{FormatHz(_currentRefreshHz)} Hz" : "Unavailable while the monitor is inactive";

    public string ConfiguredRefreshText => RefreshHz is double configured ? $"{FormatHz(configured)} Hz" : NoPreference;

    public string SavedPlacementText =>
        Primary
            ? "Primary monitor (Windows origin)"
            : $"{LayoutDirectionChoice.LabelOf(Direction)} {Anchor}{AlignmentSuffix}";

    /// <summary>", centered" / ", offset +40px" / "" -- appended to <see cref="SavedPlacementText"/> only when there is something beyond the default (Start, no offset) worth saying.</summary>
    private string AlignmentSuffix
    {
        get
        {
            string alignment = string.Equals(_alignment, LayoutAlignmentChoice.Start, StringComparison.Ordinal)
                ? string.Empty
                : $", {LayoutAlignmentChoice.LabelOf(_alignment).ToLowerInvariant()}";

            string offset = _offset == 0 ? string.Empty : $", {(_offset > 0 ? "+" : string.Empty)}{_offset}px";

            return alignment + offset;
        }
    }

    public double PreviewLeft
    {
        get => _previewLeft;
        private set => SetPreview(ref _previewLeft, value);
    }

    public double PreviewTop
    {
        get => _previewTop;
        private set => SetPreview(ref _previewTop, value);
    }

    public double PreviewWidth
    {
        get => _previewWidth;
        private set => SetPreview(ref _previewWidth, value);
    }

    public double PreviewHeight
    {
        get => _previewHeight;
        private set => SetPreview(ref _previewHeight, value);
    }

    internal int LayoutWidth => _currentWidth > 0 ? _currentWidth : 1920;

    internal int LayoutHeight => _currentHeight > 0 ? _currentHeight : 1080;

    internal void SetDisplayDetails(DisplayMonitorInfo? monitor, DisplayMonitorMapping? mapping)
    {
        _friendlyName = monitor?.FriendlyName ?? "Not currently detected by Windows";
        _devicePath = mapping?.DevicePath ?? monitor?.DevicePath;
        _windowsIdentifier = _devicePath ?? "Not currently mapped to a Windows display";
        _mappingState = mapping?.State.ToString() ?? "Unavailable";
        _currentX = monitor?.X ?? 0;
        _currentY = monitor?.Y ?? 0;
        _currentWidth = monitor?.Width ?? 0;
        _currentHeight = monitor?.Height ?? 0;
        _currentRefreshHz = monitor?.RefreshHz ?? 0;

        OnPropertyChanged(nameof(FriendlyName));
        OnPropertyChanged(nameof(DevicePath));
        OnPropertyChanged(nameof(WindowsIdentifier));
        OnPropertyChanged(nameof(MappingState));
        OnPropertyChanged(nameof(CurrentPositionText));
        OnPropertyChanged(nameof(CurrentResolutionText));
        OnPropertyChanged(nameof(CurrentRefreshText));
    }

    internal void SetPreview(double left, double top, double width, double height)
    {
        PreviewLeft = left;
        PreviewTop = top;
        PreviewWidth = width;
        PreviewHeight = height;
    }

    internal void SetPlacement(
        string direction,
        string anchor,
        string alignment = LayoutAlignmentChoice.Start,
        int offset = 0
    )
    {
        Direction = direction;
        Anchor = anchor;
        Alignment = alignment;
        Offset = offset;
    }

    private static string FormatHz(double hz) => hz.ToString("0.###", CultureInfo.InvariantCulture);

    private void SetPreview(
        ref double field,
        double value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null
    )
    {
        if (Math.Abs(field - value) < 0.01)
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }
}

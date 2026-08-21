using System.Globalization;
using PCMode.Engine;
using PCMode.Tray.Presentation;

namespace PCMode.Tray.Features.Layouts;

/// <summary>The four words a placement can use, in the order offered on screen.</summary>
public static class LayoutDirectionChoice
{
    public const string LeftOf = "LeftOf";
    public const string RightOf = "RightOf";
    public const string Above = "Above";
    public const string Below = "Below";

    public static IReadOnlyList<string> All { get; } = [LeftOf, RightOf, Above, Below];

    public static IReadOnlyList<LayoutDirectionOption> Options { get; } =
    [
        new(LeftOf, "Left of"),
        new(RightOf, "Right of"),
        new(Above, "Above"),
        new(Below, "Below"),
    ];

    public static string LabelOf(string value) =>
        Options.FirstOrDefault(choice => string.Equals(choice.Value, value, StringComparison.Ordinal))?.Label ?? value;

    public static string OppositeOf(string value) => value switch
    {
        LeftOf => RightOf,
        RightOf => LeftOf,
        Above => Below,
        _ => Above,
    };
}

/// <summary>A friendly label backed by the canonical word stored in configuration.</summary>
public sealed record LayoutDirectionOption(string Value, string Label);

/// <summary>One entry in the layout selector, including whether Windows currently matches it.</summary>
public sealed class DisplayLayoutItem(string name) : ObservableObject, IConfigurationSelectorItem
{
    private bool _isCurrent;

    public string Name { get; } = name;

    object IConfigurationSelectorItem.Key => Name;

    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => SetProperty(ref _isCurrent, value);
    }
}

/// <summary>One configured monitor's row in the layout being edited.</summary>
/// <remarks>
/// Ported from legacy's <c>LayoutMonitorRowViewModel</c> -- presentation
/// geometry, not business logic. "Included" rather than "Enabled" because
/// membership in the layout's monitor list is precisely what this configures;
/// there is no separate on/off flag.
/// </remarks>
public sealed class LayoutMonitorRow : ObservableObject
{
    private readonly Action<LayoutMonitorRow, string> _onChanged;
    private bool _included;
    private bool _primary;
    private bool _isSelected;
    private string _direction = LayoutDirectionChoice.RightOf;
    private string? _anchor;
    private string _selectedRefresh = NoPreference;
    private int _currentX;
    private int _currentY;
    private int _currentWidth;
    private int _currentHeight;
    private double _currentRefreshHz;
    private string _friendlyName = "Not currently detected by Windows";
    private string _windowsIdentifier = "Not currently mapped to a Windows display";
    private string _mappingState = "Unavailable";
    private double _previewLeft;
    private double _previewTop;
    private double _previewWidth = 160;
    private double _previewHeight = 90;

    /// <summary>Shown in the refresh list for "let Windows choose".</summary>
    public const string NoPreference = "Automatic (Windows default)";

    public LayoutMonitorRow(string monitorName, Action<LayoutMonitorRow, string> onChanged)
    {
        MonitorName = monitorName;
        _onChanged = onChanged;
    }

    public string MonitorName { get; }

    /// <summary>Whether this monitor is the one whose controls are shown beneath the arrangement.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public bool Included
    {
        get => _included;
        set
        {
            if (!SetProperty(ref _included, value))
            {
                return;
            }

            if (!_included)
            {
                _primary = false;
                OnPropertyChanged(nameof(Primary));
            }

            OnPropertyChanged(nameof(DisplayState));
            OnPropertyChanged(nameof(SavedPlacementText));
            _onChanged(this, nameof(Included));
        }
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
            OnPropertyChanged(nameof(SecondaryInformation));
            OnPropertyChanged(nameof(ConfiguredRefreshText));
            _onChanged(this, nameof(SelectedRefresh));
        }
    }

    public double? RefreshHz =>
        double.TryParse(SelectedRefresh, NumberStyles.Float, CultureInfo.InvariantCulture, out double hz) ? hz : null;

    /// <summary>The simple state shown on the monitor tile. Included is the schema's enabled state.</summary>
    public string DisplayState => Included ? "Enabled" : "Disabled";

    /// <summary>Resolution and requested/current rate, kept secondary to the monitor alias.</summary>
    public string SecondaryInformation
    {
        get
        {
            string resolution = _currentWidth > 0 && _currentHeight > 0
                ? $"{_currentWidth}×{_currentHeight}"
                : "Resolution unavailable";

            string refresh = RefreshHz is double configured
                ? $"{FormatHz(configured)} Hz"
                : _currentRefreshHz > 0
                    ? $"{FormatHz(_currentRefreshHz)} Hz"
                    : "Windows default";

            return $"{resolution} · {refresh}";
        }
    }

    public string FriendlyName => _friendlyName;

    public string WindowsIdentifier => _windowsIdentifier;

    public string MappingState => _mappingState;

    public string CurrentPositionText => _currentWidth > 0 && _currentHeight > 0
        ? $"X {_currentX:N0}, Y {_currentY:N0} px"
        : "Unavailable while the monitor is inactive";

    public string CurrentResolutionText => _currentWidth > 0 && _currentHeight > 0
        ? $"{_currentWidth:N0} × {_currentHeight:N0} px"
        : "Unavailable while the monitor is inactive";

    public string CurrentRefreshText => _currentRefreshHz > 0
        ? $"{FormatHz(_currentRefreshHz)} Hz"
        : "Unavailable while the monitor is inactive";

    public string ConfiguredRefreshText => RefreshHz is double configured
        ? $"{FormatHz(configured)} Hz"
        : NoPreference;

    public string SavedPlacementText => !Included
        ? "Not part of this layout"
        : Primary
            ? "Primary monitor (Windows origin)"
            : $"{LayoutDirectionChoice.LabelOf(Direction)} {Anchor}";

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
        _windowsIdentifier = mapping?.DevicePath ?? monitor?.DevicePath ?? "Not currently mapped to a Windows display";
        _mappingState = mapping?.State.ToString() ?? "Unavailable";
        _currentX = monitor?.X ?? 0;
        _currentY = monitor?.Y ?? 0;
        _currentWidth = monitor?.Width ?? 0;
        _currentHeight = monitor?.Height ?? 0;
        _currentRefreshHz = monitor?.RefreshHz ?? 0;

        OnPropertyChanged(nameof(FriendlyName));
        OnPropertyChanged(nameof(WindowsIdentifier));
        OnPropertyChanged(nameof(MappingState));
        OnPropertyChanged(nameof(CurrentPositionText));
        OnPropertyChanged(nameof(CurrentResolutionText));
        OnPropertyChanged(nameof(CurrentRefreshText));
        OnPropertyChanged(nameof(SecondaryInformation));
    }

    internal void SetPreview(double left, double top, double width, double height)
    {
        PreviewLeft = left;
        PreviewTop = top;
        PreviewWidth = width;
        PreviewHeight = height;
    }

    internal void SetPlacement(string direction, string anchor)
    {
        Direction = direction;
        Anchor = anchor;
    }

    private static string FormatHz(double hz) => hz.ToString("0.###", CultureInfo.InvariantCulture);

    private void SetPreview(ref double field, double value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (Math.Abs(field - value) < 0.01)
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }
}

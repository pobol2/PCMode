using System.Collections.ObjectModel;
using PCMode.Engine;
using PCMode.Tray.Configuration;
using PCMode.Tray.Presentation;

namespace PCMode.Tray.Features.Layouts;

/// <summary>
/// Edits display layouts graphically: which configured monitors are included,
/// which is primary, how the others are placed, and what rate each asks for.
/// </summary>
/// <remarks>
/// SAME SCHEMA, SAME VALIDATION
///
/// A layout here is nothing but what <see cref="DisplayLayout"/> already
/// means -- monitors, a primary, placements and refresh rates -- and saving
/// goes through <see cref="LayoutConfigurationStore"/>, which validates
/// through the same production pipeline a real startup uses. This view model
/// only steers the user away from building something that pipeline would
/// refuse anyway: at most one primary, a placement naming another included
/// monitor, and a placement graph that actually reaches the primary.
///
/// EDITING DOES NOT TOUCH HARDWARE
///
/// <see cref="DisplayProvider.Discover"/> is called only to read which
/// refresh rates a monitor currently supports, for the drop-down. It answers
/// a question; it does not switch anything on. The explicit Activate action
/// applies the selected saved layout through a freshly read
/// <see cref="DisplayProvider"/>, same as every other page's Activate.
///
/// DIRTY TRACKING, ADAPTED
///
/// Legacy (predating this shared base) computes "has unsaved changes" as a
/// snapshot diff against the rows as last loaded. <see cref="ConfigurationPageViewModelBase"/>
/// already owns that flag itself (<c>MarkDirty()</c>/<c>ClearDirtyState()</c>),
/// so this port calls <c>MarkDirty()</c> from <see cref="OnRowChanged"/> and
/// once after building a fresh draft's rows, and <c>ClearDirtyState()</c>
/// everywhere legacy captured its snapshot. Behavior is identical; only the
/// bookkeeping moved.
/// </remarks>
public sealed class LayoutsViewModel : ConfigurationPageViewModelBase
{
    private const string Category = "Layouts";

    private string? _currentLayout;
    private string? _editingLayoutName;
    private string? _discardLayoutName;
    private LayoutMonitorRow? _selectedMonitor;
    private bool _suppressRecompute;

    public LayoutsViewModel(IConfigurationWorkspace workspace) : base(workspace)
    {
    }

    public ObservableCollection<string> Layouts { get; } = [];

    /// <summary>The selector projection, with current-layout state for presentation.</summary>
    public ObservableCollection<DisplayLayoutItem> LayoutItems { get; } = [];

    public ObservableCollection<LayoutMonitorRow> Rows { get; } = [];

    public LayoutMonitorRow? SelectedMonitor
    {
        get => _selectedMonitor;
        private set
        {
            if (ReferenceEquals(_selectedMonitor, value))
            {
                return;
            }

            if (_selectedMonitor is not null)
            {
                _selectedMonitor.IsSelected = false;
            }

            _selectedMonitor = value;

            if (_selectedMonitor is not null)
            {
                _selectedMonitor.IsSelected = true;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasMonitors));
        }
    }

    public bool HasMonitors => SelectedMonitor is not null;

    public string? SelectedLayout
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
            OnPropertyChanged(nameof(HasSelectedLayout));
            OnPropertyChanged(nameof(CanActivateSelected));

            if (value is not null)
            {
                Load(value);
            }
        }
    }

    public override void SelectKey(object key) => SelectedLayout = key as string;

    public bool HasSelectedLayout => SelectedLayout is not null;

    /// <summary>The layout Windows currently reports, or null while it is settling or unknown.</summary>
    public string? CurrentLayout
    {
        get => _currentLayout;
        private set
        {
            // LayoutItems can be recreated even when the actual current layout
            // name has not changed. Always reapply the selector badge state so
            // newly created items stay in sync.
            _currentLayout = value;
            OnPropertyChanged();

            foreach (DisplayLayoutItem item in LayoutItems)
            {
                item.IsCurrent = string.Equals(item.Name, value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>The existing or draft name shown in the editor and used by Save.</summary>
    public string? EditingLayoutName
    {
        get => _editingLayoutName;
        private set => SetProperty(ref _editingLayoutName, value);
    }

    public bool CanActivateSelected => HasSelectedLayout && !HasUnsavedChanges && !IsBusy;

    /// <summary>
    /// Why the current arrangement of <see cref="Rows"/> could not be saved,
    /// or null when it is a well-formed topology. Checked here so the problem
    /// shows immediately, rather than only after the store refuses it.
    /// </summary>
    public string? InstructionMessage => !string.IsNullOrEmpty(StatusMessage)
        ? null
        : EditingLayoutName is null
        ? "Select a layout or create one."
        : Rows.All(row => !row.Included)
            ? "Select at least one monitor."
            : null;

    public string? TopologyProblem => EditingLayoutName is null
        ? null
        : Validate(Rows);

    public override string? ValidationProblem => TopologyProblem;

    public override bool CanSave =>
        !string.IsNullOrWhiteSpace(EditingLayoutName) &&
        HasUnsavedChanges &&
        !IsBusy &&
        ValidationProblem is null;

    /// <summary>
    /// Refreshes only Windows-observed display state for the already-loaded
    /// editor rows. It never reloads configuration or rebuilds the layout
    /// form -- called on a timer while this page is open, by the page itself.
    /// </summary>
    public async Task RefreshLiveStatusAsync()
    {
        if (IsBusy)
        {
            return;
        }

        (DisplayProvider? display, _) = ReadProvider<DisplayProvider>();

        if (display is null)
        {
            return;
        }

        DisplaySnapshot? snapshot;

        try
        {
            snapshot = await Task.Run(display.Discover).ConfigureAwait(true);
        }
        catch
        {
            // Best-effort ambient observation. Keep the last good live state
            // and try again on the next tick.
            return;
        }

        CurrentLayout = snapshot.CurrentLayout is { } current &&
            Layouts.Any(name => string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
            ? current
            : null;

        foreach (LayoutMonitorRow row in Rows)
        {
            DisplayMonitorMapping? mapping = snapshot.Mappings.FirstOrDefault(candidate =>
                string.Equals(candidate.LogicalName, row.MonitorName, StringComparison.OrdinalIgnoreCase));

            DisplayMonitorInfo? monitorInfo = snapshot.Monitors.FirstOrDefault(candidate =>
                string.Equals(candidate.LogicalName, row.MonitorName, StringComparison.OrdinalIgnoreCase) ||
                (mapping is not null &&
                 string.Equals(candidate.DevicePath, mapping.DevicePath, StringComparison.OrdinalIgnoreCase)));

            row.SetDisplayDetails(monitorInfo, mapping);
        }
    }

    public override void Refresh()
    {
        string? keep = SelectedLayout;

        (DisplayProvider? display, string? problem) = ReadProvider<DisplayProvider>();

        if (display is null)
        {
            Layouts.Clear();
            LayoutItems.Clear();
            CurrentLayout = null;
            Rows.Clear();
            SelectedMonitor = null;
            EditingLayoutName = null;
            ClearDirtyState();
            SetError(problem ?? "The configuration could not be read.");
            NotifyEditingState();
            return;
        }

        Layouts.Clear();
        LayoutItems.Clear();

        foreach (string name in display.LayoutNames)
        {
            Layouts.Add(name);
            LayoutItems.Add(new DisplayLayoutItem(name));
        }

        SelectedKey = keep is not null && Layouts.Contains(keep) ? keep : Layouts.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedLayout));
        OnPropertyChanged(nameof(HasSelectedLayout));

        if (SelectedLayout is { } selected)
        {
            Load(selected);
        }
        else
        {
            EditingLayoutName = null;
            _discardLayoutName = null;
            BuildRows(display, null);
            ClearError();
            NotifyEditingState();
        }
    }

    private void Load(string name)
    {
        (DisplayProvider? display, string? problem) = ReadProvider<DisplayProvider>();

        if (display is null)
        {
            SetError(problem ?? "The configuration could not be read.");
            return;
        }

        DisplayLayout? layout = display.Layouts.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

        EditingLayoutName = name;
        _discardLayoutName = name;

        // A different layout can have an entirely different arrangement.
        // Start its editor on the primary monitor instead of carrying an
        // incidental selection over from the previous layout.
        SelectedMonitor = null;
        BuildRows(display, layout);
        ClearError();
        NotifyEditingState();
    }

    /// <summary>Starts a named, unsaved layout with no monitors selected yet.</summary>
    public string? StartNewLayout(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Give the layout a name.";
        }

        string trimmed = name.Trim();

        if (Layouts.Any(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{trimmed}' is already a configured display layout.";
        }

        string? restore = SelectedLayout;
        SelectedKey = null;
        OnPropertyChanged(nameof(SelectedLayout));
        OnPropertyChanged(nameof(HasSelectedLayout));

        (DisplayProvider? display, string? problem) = ReadProvider<DisplayProvider>();

        if (display is null)
        {
            return SetError(problem ?? "The configuration could not be read.");
        }

        EditingLayoutName = trimmed;
        _discardLayoutName = restore;
        BuildRows(display, null);
        MarkDirty();
        ClearError();
        NotifyEditingState();
        return null;
    }

    /// <summary>Starts a new draft with an available suggested name.</summary>
    public void StartNewLayout()
    {
        const string basis = "New display layout";
        string proposal = basis;
        int suffix = 2;

        while (Layouts.Any(name => string.Equals(name, proposal, StringComparison.OrdinalIgnoreCase)))
        {
            proposal = $"{basis} {suffix++}";
        }

        _ = StartNewLayout(proposal);
    }

    private void BuildRows(DisplayProvider display, DisplayLayout? layout)
    {
        string? keepMonitor = _selectedMonitor?.MonitorName;
        _suppressRecompute = true;
        Rows.Clear();

        DisplaySnapshot? snapshot = null;

        try
        {
            snapshot = display.Discover();
        }
        catch (Exception)
        {
            // Discovery is read-only and best-effort here: refresh rate
            // choices simply fall back to whatever was already configured.
        }

        CurrentLayout = snapshot?.CurrentLayout is { } current &&
            Layouts.Any(name => string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
            ? current
            : null;

        foreach (string monitor in display.MonitorNames)
        {
            var row = new LayoutMonitorRow(monitor, OnRowChanged);

            DisplayMonitorMapping? mapping = snapshot?.Mappings.FirstOrDefault(candidate =>
                string.Equals(candidate.LogicalName, monitor, StringComparison.OrdinalIgnoreCase));
            DisplayMonitorInfo? monitorInfo = snapshot?.Monitors.FirstOrDefault(candidate =>
                string.Equals(candidate.LogicalName, monitor, StringComparison.OrdinalIgnoreCase) ||
                (mapping is not null && string.Equals(candidate.DevicePath, mapping.DevicePath, StringComparison.OrdinalIgnoreCase)));

            row.SetDisplayDetails(monitorInfo, mapping);

            if (layout is not null && layout.Monitors.Any(m => string.Equals(m, monitor, StringComparison.OrdinalIgnoreCase)))
            {
                row.Included = true;
                row.Primary = string.Equals(layout.Primary, monitor, StringComparison.OrdinalIgnoreCase);

                if (layout.Placements.TryGetValue(monitor, out DisplayPlacement? placement))
                {
                    row.Direction = placement.Direction.ToString();
                    row.Anchor = placement.Anchor;
                }
            }

            double? configuredHz = layout?.Refresh.TryGetValue(monitor, out double hz) == true ? hz : null;

            PopulateRefreshChoices(row, snapshot, monitor, configuredHz);

            Rows.Add(row);
        }

        _suppressRecompute = false;
        RecomputeAnchorChoices();
        SelectedMonitor = Rows.FirstOrDefault(row =>
                string.Equals(row.MonitorName, keepMonitor, StringComparison.OrdinalIgnoreCase))
            ?? Rows.FirstOrDefault(row => row.Primary)
            ?? Rows.FirstOrDefault(row => row.Included)
            ?? Rows.FirstOrDefault();
        RecomputePreview();
        ClearDirtyState();
        OnPropertyChanged(nameof(HasMonitors));
        NotifyEditingState();
    }

    private static void PopulateRefreshChoices(
        LayoutMonitorRow row, DisplaySnapshot? snapshot, string monitor, double? configuredHz)
    {
        row.RefreshChoices.Clear();
        row.RefreshChoices.Add(LayoutMonitorRow.NoPreference);

        IReadOnlyList<int> discovered = snapshot?.Monitors
            .FirstOrDefault(info => string.Equals(info.LogicalName, monitor, StringComparison.OrdinalIgnoreCase))
            ?.SupportedRefreshHz ?? [];

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (int hz in discovered.OrderBy(hz => hz))
        {
            string text = hz.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (seen.Add(text))
            {
                row.RefreshChoices.Add(text);
            }
        }

        if (configuredHz is double configured)
        {
            string text = configured.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

            // Preserved even when discovery can no longer see it -- the
            // monitor might simply be switched off right now, and a saved
            // rate must not silently disappear from under it.
            if (seen.Add(text))
            {
                row.RefreshChoices.Add(text);
            }

            row.SelectedRefresh = text;
        }
        else
        {
            row.SelectedRefresh = LayoutMonitorRow.NoPreference;
        }
    }

    public void SelectMonitor(LayoutMonitorRow monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        if (Rows.Contains(monitor))
        {
            SelectedMonitor = monitor;
        }
    }

    /// <summary>
    /// Snaps a dragged enabled monitor to the closest of the four relationships
    /// the saved model understands. The drop point is preview-canvas geometry;
    /// only Direction and Anchor are changed.
    /// </summary>
    public bool TrySnapMonitor(LayoutMonitorRow monitor, double droppedLeft, double droppedTop)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        if (!Rows.Contains(monitor) || !monitor.Included)
        {
            StatusMessage = "Enable the monitor before arranging it.";
            NotifyEditingState();
            return false;
        }

        LayoutMonitorRow[] anchors =
        [
            .. Rows.Where(candidate =>
                candidate != monitor &&
                candidate.Included &&
                (monitor.Primary || !WouldCreateCycle(monitor, candidate)))
        ];

        if (anchors.Length == 0)
        {
            StatusMessage = "Enable at least two monitors to arrange them.";
            NotifyEditingState();
            return false;
        }

        (LayoutMonitorRow Anchor, string Direction, double Distance) best = anchors
            .SelectMany(anchor => CandidateDrops(monitor, anchor)
                .Select(candidate => (
                    Anchor: anchor,
                    candidate.Direction,
                    Distance: DistanceSquared(droppedLeft, droppedTop, candidate.Left, candidate.Top))))
            .MinBy(candidate => candidate.Distance);

        _suppressRecompute = true;

        if (monitor.Primary)
        {
            best.Anchor.SetPlacement(LayoutDirectionChoice.OppositeOf(best.Direction), monitor.MonitorName);
        }
        else
        {
            monitor.SetPlacement(best.Direction, best.Anchor.MonitorName);
        }

        _suppressRecompute = false;
        MarkDirty();
        RecomputeAnchorChoices();
        RecomputePreview();
        SelectMonitor(monitor);
        NotifyEditingState();
        StatusMessage =
            $"'{monitor.MonitorName}' snapped {LayoutDirectionChoice.LabelOf(best.Direction).ToLowerInvariant()} " +
            $"'{best.Anchor.MonitorName}'.";
        NotifyEditingState();
        return true;
    }

    private static IEnumerable<(string Direction, double Left, double Top)> CandidateDrops(
        LayoutMonitorRow monitor,
        LayoutMonitorRow anchor)
    {
        yield return (LayoutDirectionChoice.LeftOf, anchor.PreviewLeft - monitor.PreviewWidth, anchor.PreviewTop);
        yield return (LayoutDirectionChoice.RightOf, anchor.PreviewLeft + anchor.PreviewWidth, anchor.PreviewTop);
        yield return (LayoutDirectionChoice.Above, anchor.PreviewLeft, anchor.PreviewTop - monitor.PreviewHeight);
        yield return (LayoutDirectionChoice.Below, anchor.PreviewLeft, anchor.PreviewTop + anchor.PreviewHeight);
    }

    private bool WouldCreateCycle(LayoutMonitorRow monitor, LayoutMonitorRow anchor)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LayoutMonitorRow? current = anchor;

        while (current is not null && !current.Primary && visited.Add(current.MonitorName))
        {
            if (string.Equals(current.Anchor, monitor.MonitorName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            current = Rows.FirstOrDefault(row =>
                string.Equals(row.MonitorName, current.Anchor, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static double DistanceSquared(double left, double top, double candidateLeft, double candidateTop)
    {
        double x = left - candidateLeft;
        double y = top - candidateTop;
        return (x * x) + (y * y);
    }

    private void OnRowChanged(LayoutMonitorRow changed, string propertyName)
    {
        if (_suppressRecompute)
        {
            return;
        }

        MarkDirty();
        RecomputeAnchorChoices(
            propertyName == nameof(LayoutMonitorRow.Primary) && changed.Primary ? changed : null);
        RecomputePreview();
        StatusMessage = null;
        NotifyEditingState();
    }

    /// <summary>
    /// Keeps exactly one included monitor primary -- turning one on demotes
    /// any other, and turning the only primary off promotes the first
    /// remaining included monitor rather than leaving the layout without one
    /// -- and keeps every row's anchor choices limited to other included
    /// monitors, since an anchor naming anything else is refused downstream
    /// anyway.
    /// </summary>
    private void RecomputeAnchorChoices(LayoutMonitorRow? requestedPrimary = null)
    {
        _suppressRecompute = true;

        List<LayoutMonitorRow> included = [.. Rows.Where(row => row.Included)];
        List<LayoutMonitorRow> primaries = [.. included.Where(row => row.Primary)];

        if (requestedPrimary is not null)
        {
            foreach (LayoutMonitorRow row in included.Where(row => row != requestedPrimary && row.Primary))
            {
                row.Primary = false;
            }
        }
        else if (primaries.Count > 1)
        {
            foreach (LayoutMonitorRow extra in primaries.Skip(1))
            {
                extra.Primary = false;
            }
        }
        else if (primaries.Count == 0 && included.Count > 0)
        {
            included[0].Primary = true;
        }

        foreach (LayoutMonitorRow row in Rows)
        {
            IReadOnlyList<string> choices = row.Included
                ? [.. included.Where(other => other != row).Select(other => other.MonitorName)]
                : [];

            row.AnchorChoices.Clear();

            foreach (string choice in choices)
            {
                row.AnchorChoices.Add(choice);
            }

            if (row.Anchor is not null && !choices.Contains(row.Anchor, StringComparer.OrdinalIgnoreCase))
            {
                row.Anchor = choices.FirstOrDefault();
            }
            else if (row.Anchor is null)
            {
                row.Anchor = choices.FirstOrDefault();
            }
        }

        _suppressRecompute = false;
    }

    /// <summary>
    /// Projects the existing relative placement graph into a fixed logical canvas.
    /// Live monitor sizes set the aspect ratio when Windows reports them; otherwise
    /// a neutral 16:9 size keeps an offline configured monitor understandable.
    /// Disabled monitors sit in a separate shelf because the schema intentionally
    /// stores no position for monitors that are not part of a layout.
    /// </summary>
    private void RecomputePreview()
    {
        const double canvasWidth = 900;
        const double canvasHeight = 440;
        const double padding = 18;

        List<LayoutMonitorRow> included = [.. Rows.Where(row => row.Included)];
        List<LayoutMonitorRow> disabled = [.. Rows.Where(row => !row.Included)];
        double disabledHeight = disabled.Count switch
        {
            0 => 0,
            <= 5 => 108,
            _ => 190,
        };
        double arrangementHeight = canvasHeight - disabledHeight;

        if (included.Count > 0)
        {
            LayoutMonitorRow origin = included.FirstOrDefault(row => row.Primary) ?? included[0];
            var positions = new Dictionary<LayoutMonitorRow, (double X, double Y)>
            {
                [origin] = (0, 0),
            };
            List<LayoutMonitorRow> remaining = [.. included.Where(row => row != origin)];

            while (remaining.Count > 0)
            {
                bool progressed = false;

                foreach (LayoutMonitorRow row in remaining.ToList())
                {
                    LayoutMonitorRow? anchor = included.FirstOrDefault(candidate =>
                        string.Equals(candidate.MonitorName, row.Anchor, StringComparison.OrdinalIgnoreCase));

                    if (anchor is null || !positions.TryGetValue(anchor, out (double X, double Y) anchorPosition))
                    {
                        continue;
                    }

                    positions[row] = row.Direction switch
                    {
                        LayoutDirectionChoice.LeftOf => (anchorPosition.X - row.LayoutWidth, anchorPosition.Y),
                        LayoutDirectionChoice.Above => (anchorPosition.X, anchorPosition.Y - row.LayoutHeight),
                        LayoutDirectionChoice.Below => (anchorPosition.X, anchorPosition.Y + anchor.LayoutHeight),
                        _ => (anchorPosition.X + anchor.LayoutWidth, anchorPosition.Y),
                    };

                    remaining.Remove(row);
                    progressed = true;
                }

                if (!progressed)
                {
                    break;
                }
            }

            double fallbackX = positions.Count == 0
                ? 0
                : positions.Max(entry => entry.Value.X + entry.Key.LayoutWidth) + 160;

            foreach (LayoutMonitorRow row in remaining)
            {
                positions[row] = (fallbackX, 0);
                fallbackX += row.LayoutWidth + 160;
            }

            double minX = positions.Min(entry => entry.Value.X);
            double minY = positions.Min(entry => entry.Value.Y);
            double maxX = positions.Max(entry => entry.Value.X + entry.Key.LayoutWidth);
            double maxY = positions.Max(entry => entry.Value.Y + entry.Key.LayoutHeight);
            double scale = Math.Min(
                (canvasWidth - (padding * 2)) / Math.Max(1, maxX - minX),
                (arrangementHeight - (padding * 2)) / Math.Max(1, maxY - minY));
            scale = Math.Min(scale, 0.15);

            double contentWidth = (maxX - minX) * scale;
            double contentHeight = (maxY - minY) * scale;
            double offsetX = ((canvasWidth - contentWidth) / 2) - (minX * scale);
            double offsetY = ((arrangementHeight - contentHeight) / 2) - (minY * scale);

            foreach ((LayoutMonitorRow row, (double x, double y)) in positions)
            {
                row.SetPreview(
                    offsetX + (x * scale),
                    offsetY + (y * scale),
                    Math.Max(100, row.LayoutWidth * scale),
                    Math.Max(64, row.LayoutHeight * scale));
            }
        }

        if (disabled.Count == 0)
        {
            return;
        }

        int columns = Math.Min(5, disabled.Count);
        double gap = 10;
        double tileWidth = Math.Min(148, (canvasWidth - (padding * 2) - (gap * (columns - 1))) / columns);
        double tileHeight = Math.Min(84, tileWidth * 9 / 16);
        double shelfWidth = (columns * tileWidth) + ((columns - 1) * gap);
        double shelfLeft = (canvasWidth - shelfWidth) / 2;
        double shelfTop = arrangementHeight + 12;

        for (int index = 0; index < disabled.Count; index++)
        {
            int rowIndex = index / columns;
            int columnIndex = index % columns;
            disabled[index].SetPreview(
                shelfLeft + (columnIndex * (tileWidth + gap)),
                shelfTop + (rowIndex * (tileHeight + 8)),
                tileWidth,
                tileHeight);
        }
    }

    /// <summary>
    /// Mirrors the checks <see cref="DisplayLayout.TryParse"/> makes, so the
    /// UI can refuse to save before the store does. There is still only one
    /// authority on whether a layout is well formed: this exists to give an
    /// answer sooner, not a different one.
    /// </summary>
    private static string? Validate(IReadOnlyList<LayoutMonitorRow> rows)
    {
        List<LayoutMonitorRow> included = [.. rows.Where(row => row.Included)];

        if (included.Count == 0)
        {
            return "Include at least one monitor.";
        }

        List<LayoutMonitorRow> primaries = [.. included.Where(row => row.Primary)];

        if (primaries.Count != 1)
        {
            return "Exactly one included monitor must be primary.";
        }

        if (included.Count == 1)
        {
            return null;
        }

        var known = new HashSet<string>(included.Select(row => row.MonitorName), StringComparer.OrdinalIgnoreCase);

        foreach (LayoutMonitorRow row in included.Where(row => !row.Primary))
        {
            if (string.IsNullOrWhiteSpace(row.Anchor) || !known.Contains(row.Anchor))
            {
                return $"'{row.MonitorName}' needs a placement relative to another included monitor.";
            }

            if (string.Equals(row.Anchor, row.MonitorName, StringComparison.OrdinalIgnoreCase))
            {
                return $"'{row.MonitorName}' cannot be placed relative to itself.";
            }
        }

        // Walk outward from the primary, exactly as DisplayLayout.TryOrder
        // does, to catch a cycle or a chain that never reaches it.
        string primary = primaries[0].MonitorName;
        var settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primary };
        List<LayoutMonitorRow> remaining = [.. included.Where(row => !row.Primary)];

        while (remaining.Count > 0)
        {
            bool progressed = false;

            foreach (LayoutMonitorRow row in remaining.ToList())
            {
                if (settled.Contains(row.Anchor!))
                {
                    settled.Add(row.MonitorName);
                    remaining.Remove(row);
                    progressed = true;
                }
            }

            if (!progressed)
            {
                return
                    $"{string.Join(", ", remaining.Select(row => $"'{row.MonitorName}'"))} cannot be placed -- " +
                    $"their placements form a loop, or a chain that never reaches the primary ('{primary}').";
            }
        }

        return null;
    }

    /// <summary>
    /// Applies the selected saved layout through a freshly read provider,
    /// then re-reads what Windows actually settled on.
    /// </summary>
    public async Task<string?> ActivateSelectedAsync(CancellationToken cancellationToken = default)
    {
        ClearError();

        if (SelectedLayout is not { } name)
        {
            return SetError("Choose a display layout first.");
        }

        if (HasUnsavedChanges)
        {
            return SetError("Save or discard changes before activating a layout.");
        }

        (DisplayProvider? provider, string? problem) = ReadProvider<DisplayProvider>();

        if (provider is null)
        {
            StatusMessage = problem ?? "The configuration could not be read.";
            return SetError(StatusMessage);
        }

        IsBusy = true;
        StatusMessage = $"Activating '{name}'...";

        try
        {
            await Task.Run(() => provider.Apply(name, cancellationToken), cancellationToken).ConfigureAwait(true);
            DisplaySnapshot snapshot = await Task.Run(provider.Discover, cancellationToken).ConfigureAwait(true);
            CurrentLayout = snapshot.CurrentLayout;

            StatusMessage = string.Equals(CurrentLayout, name, StringComparison.OrdinalIgnoreCase)
                ? Workspace.Apply($"'{name}' activated.")
                : $"'{name}' requested. Windows is still settling.";
            return null;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Display layout activation was cancelled.";
            return SetError(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not activate '{name}'. {ex.Message}";
            Workspace.Fault(Category, StatusMessage, ex);
            return SetError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected override void DiscardChangesCore()
    {
        string? restore = _discardLayoutName ?? SelectedLayout;

        if (restore is not null && Layouts.Any(name =>
                string.Equals(name, restore, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedKey = Layouts.First(name => string.Equals(name, restore, StringComparison.OrdinalIgnoreCase));
            OnPropertyChanged(nameof(SelectedLayout));
            OnPropertyChanged(nameof(HasSelectedLayout));
            Load(SelectedLayout!);
            StatusMessage = "Changes discarded.";
            NotifyEditingState();
            return;
        }

        (DisplayProvider? display, string? problem) = ReadProvider<DisplayProvider>();

        if (display is null)
        {
            SetError(problem ?? "The configuration could not be read.");
            return;
        }

        SelectedKey = null;
        OnPropertyChanged(nameof(SelectedLayout));
        OnPropertyChanged(nameof(HasSelectedLayout));
        EditingLayoutName = null;
        _discardLayoutName = null;
        BuildRows(display, null);
        NotifyEditingState();
        StatusMessage = "Changes discarded.";
    }

    protected override string? SaveChangesCore() =>
        SaveNamed(EditingLayoutName ?? string.Empty, trimmed => $"'{trimmed}' saved.");

    /// <summary>
    /// Starts a named editing draft from the selected layout without writing it.
    /// The display provider deliberately rejects two names for an identical
    /// arrangement because Windows readback could not distinguish them, so the
    /// user changes the copied arrangement and saves it through the normal path.
    /// </summary>
    public string? DuplicateSelected(string newName)
    {
        if (SelectedLayout is null)
        {
            return "Choose a display layout first.";
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            return "Give the layout a name.";
        }

        string trimmed = newName.Trim();

        if (Layouts.Any(name => string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{trimmed}' is already a configured display layout.";
        }

        string source = SelectedLayout;
        SelectedKey = null;
        OnPropertyChanged(nameof(SelectedLayout));
        OnPropertyChanged(nameof(HasSelectedLayout));
        EditingLayoutName = trimmed;
        _discardLayoutName = source;
        MarkDirty();
        NotifyEditingState();
        StatusMessage =
            $"Copy of '{source}' ready as '{trimmed}'. Change at least one monitor setting, then Save.";
        return null;
    }

    private string? SaveNamed(string name, Func<string, string> successMessage)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Give the layout a name.";
        }

        string? problem = Validate(Rows);

        if (problem is not null)
        {
            StatusMessage = problem;
            return problem;
        }

        List<LayoutMonitorEdit> monitors =
        [
            .. Rows.Where(row => row.Included).Select(row => new LayoutMonitorEdit(
                row.MonitorName,
                row.Primary,
                row.Primary ? null : row.Direction,
                row.Primary ? null : row.Anchor,
                row.RefreshHz))
        ];

        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = LayoutConfigurationStore.Save(Workspace.Root, name.Trim(), monitors);

            if (!outcome.Success)
            {
                StatusMessage = outcome.Problem ?? "That could not be saved.";
                Workspace.Warn(Category, StatusMessage);
                return outcome.Problem;
            }

            Refresh();
            SelectedLayout = name.Trim();
            return ReportSuccess(successMessage(name.Trim()));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public string? RenameSelected(string newName)
    {
        if (SelectedLayout is null)
        {
            return "Choose a display layout first.";
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            return "Give the layout a name.";
        }

        string oldName = SelectedLayout;
        string trimmed = newName.Trim();

        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = LayoutConfigurationStore.Rename(Workspace.Root, oldName, trimmed);

            if (!outcome.Success)
            {
                StatusMessage = outcome.Problem ?? "That could not be saved.";
                Workspace.Warn(Category, StatusMessage);
                return outcome.Problem;
            }

            Refresh();
            SelectedLayout = trimmed;
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

        if (SelectedLayout is null)
        {
            return SetError("Choose a display layout first.");
        }

        string name = SelectedLayout;
        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = LayoutConfigurationStore.Delete(Workspace.Root, name);

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

    private void NotifyEditingState()
    {
        OnPropertyChanged(nameof(InstructionMessage));
        NotifyValidationChanged(nameof(TopologyProblem), nameof(ValidationProblem), nameof(CanSave));
        OnPropertyChanged(nameof(CanActivateSelected));
    }

    private string? ReportSuccess(string message)
    {
        StatusMessage = Workspace.Apply(message);
        return null;
    }

    protected override void OnBusyStateChanged()
    {
        base.OnBusyStateChanged();
        OnPropertyChanged(nameof(CanActivateSelected));
    }

    protected override void OnDirtyStateChanged()
    {
        base.OnDirtyStateChanged();
        OnPropertyChanged(nameof(CanActivateSelected));
    }
}

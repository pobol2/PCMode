using System.Collections.ObjectModel;
using PCMode.Engine;
using PCMode.Tray.Elements;
using PCMode.Tray.Elements.Configuration;

namespace PCMode.Tray.Elements.Displays.Sections;

/// <summary>
/// The monitor-arrangement facet of a selected display layout: which monitors
/// are included, which is primary, how the others are placed, and what rate
/// each asks for -- plus which saved cursor position (if any) this layout
/// requires.
/// </summary>
/// <remarks>
/// PORTED FROM THE STANDALONE LAYOUTS PAGE
///
/// This is the same schema and validation <c>LayoutsViewModel</c> had --
/// <see cref="DisplayLayout"/>, validated through <see cref="LayoutConfigurationStore"/>'s
/// production pipeline -- reshaped as an <see cref="IConfigurationSection"/>
/// instead of a full page. Owning which layout exists (create, rename,
/// duplicate, delete, activate) moved up to <see cref="Displays.DisplaysViewModel"/>,
/// since that is the shared selector every section on the Displays page
/// reads; this section owns only the arrangement for whichever layout is
/// currently selected.
///
/// THE CURSOR LINK LIVES HERE, NOT IN THE CURSOR SECTION
///
/// A layout can never require the cursor -- <see cref="CursorProvider"/>
/// always runs after the display, so a layout requiring it would need it
/// ordered both before and after itself, which the planner refuses; see
/// <see cref="DisplayLayout"/>'s 'requires' parsing. So a layout's linked
/// cursor position is not part of the layout document at all: it is written
/// as a plain 'cursor' resource on whichever Space, Activity or Source
/// already sets 'display' to this layout -- the same place a mode sets any
/// other resource, and the mechanism <see cref="TvPowerProvider"/>'s
/// safe-display cross-reference uses is deliberately NOT this one, since that
/// relationship runs the other way (see <see cref="DisplayLayoutCursorLink"/>).
/// Since that is a hierarchy edit rather than part of
/// <see cref="LayoutConfigurationStore.Save"/>'s own document, the field that
/// picks WHICH position is linked still belongs to whichever section owns the
/// picker -- this one -- but writing it is a separate step,
/// <see cref="ApplyCursorLink"/>, run once the layout itself has saved.
/// <c>Sections.CursorSectionViewModel</c> only edits the X/Y of whatever
/// position is currently linked; it never changes the link itself.
/// </remarks>
public sealed class LayoutSectionViewModel : ObservableObject, IConfigurationSection
{
    /// <summary>Shown in the cursor-position picker when nothing is linked.</summary>
    public const string NoCursorPosition = "(None)";

    private readonly IConfigurationWorkspace _workspace;
    private readonly Action _onChanged;
    private readonly Action _onCursorLinkChanged;
    private readonly Action<LayoutMonitorRow?> _onSelectedMonitorChanged;
    private readonly SelectionList<LayoutMonitorRow> _monitors;

    private bool _suppressRecompute;
    private LayoutMonitorRow? _selectedMonitor;
    private string _cursorPositionName = NoCursorPosition;

    /// <summary>
    /// The last discovery read, kept so a row that changes which monitor it is
    /// -- or a freshly added one -- can resolve that monitor's Windows details
    /// and refresh rates without a further round trip.
    /// </summary>
    private DisplaySnapshot? _snapshot;

    /// <summary>
    /// Real pixels per preview pixel, as last computed by <see cref="RecomputePreview"/>
    /// -- <see cref="ResolveAlignment"/> needs it to turn a raw drop point, which
    /// arrives in preview-canvas coordinates, into the real-pixel <see cref="LayoutMonitorRow.Offset"/>
    /// <see cref="DisplayLayout"/> actually stores.
    /// </summary>
    private double _previewScale = 1;

    /// <summary>
    /// What <see cref="CursorPositionName"/> was when it was last read from --
    /// or last written to -- disk. <see cref="ApplyCursorLink"/> writes only
    /// when this no longer matches, so saving a layout the user did not touch
    /// the cursor picker for never rewrites (and never clears) a link it does
    /// not own.
    /// </summary>
    private string _loadedCursorPositionName = NoCursorPosition;

    private IReadOnlyDictionary<string, string> _otherRequirements = new Dictionary<string, string>(
        StringComparer.OrdinalIgnoreCase
    );

    /// <param name="onCursorLinkChanged">
    /// Called whenever <see cref="CursorPositionName"/> changes to a
    /// different linked position (or to none) -- distinct from
    /// <paramref name="onChanged"/> because <c>Sections.CursorSectionViewModel</c>
    /// needs to be told the new name directly, in memory, rather than
    /// re-reading it from disk (see <c>CursorSectionViewModel.Load</c>'s
    /// remarks for why re-reading would see the old link until this layout
    /// itself is saved).
    /// </param>
    /// <param name="onSelectedMonitorChanged">
    /// Called whenever <see cref="SelectedMonitor"/> changes -- a click, or a
    /// fresh layout load rebuilding <see cref="Rows"/> -- so per-monitor
    /// sections such as <c>Sections.MonitorDdcSectionViewModel</c> and
    /// <c>Sections.MonitorTelevisionSectionViewModel</c> can load the newly
    /// selected physical display, the same way <paramref name="onCursorLinkChanged"/>
    /// keeps the cursor section in step with this section's own link field.
    /// </param>
    public LayoutSectionViewModel(
        IConfigurationWorkspace workspace,
        Action onChanged,
        Action onCursorLinkChanged,
        Action<LayoutMonitorRow?> onSelectedMonitorChanged
    )
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(onChanged);
        ArgumentNullException.ThrowIfNull(onCursorLinkChanged);
        ArgumentNullException.ThrowIfNull(onSelectedMonitorChanged);

        _workspace = workspace;
        _onChanged = onChanged;
        _onCursorLinkChanged = onCursorLinkChanged;
        _onSelectedMonitorChanged = onSelectedMonitorChanged;

        // minimumRows: 0 -- a layout with no monitors yet is the normal
        // starting state for a new draft; Validate is what refuses to save it.
        _monitors = new SelectionList<LayoutMonitorRow>(CreateRow, OnMembershipChanged, minimumRows: 0);
    }

    /// <summary>The monitors this layout includes -- one row per monitor, added and removed by the user.</summary>
    public ObservableCollection<LayoutMonitorRow> Rows => _monitors.Rows;

    public bool CanAddMonitor => _monitors.CanAdd;

    public bool CanRemoveMonitor => _monitors.CanRemove;

    /// <summary>Adds the first configured monitor this layout does not already use, and selects it.</summary>
    public void AddMonitor()
    {
        if (_monitors.Add() is { } added)
        {
            SelectedMonitor = added;
        }
    }

    public void RemoveMonitor(LayoutMonitorRow row)
    {
        if (!_monitors.Remove(row))
        {
            return;
        }

        if (ReferenceEquals(_selectedMonitor, row))
        {
            SelectedMonitor = Rows.FirstOrDefault(candidate => candidate.Primary) ?? Rows.FirstOrDefault();
        }
    }

    public ObservableCollection<string> CursorPositionChoices { get; } = [NoCursorPosition];

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
            _onSelectedMonitorChanged(_selectedMonitor);
        }
    }

    public bool HasMonitors => SelectedMonitor is not null;

    /// <summary>The saved cursor position this layout requires, or <see cref="NoCursorPosition"/>.</summary>
    public string CursorPositionName
    {
        get => _cursorPositionName;
        set
        {
            string next = string.IsNullOrWhiteSpace(value) ? NoCursorPosition : value;

            if (SetProperty(ref _cursorPositionName, next))
            {
                _onChanged();
                _onCursorLinkChanged();
            }
        }
    }

    /// <summary>
    /// Why the current arrangement of <see cref="Rows"/> could not be saved,
    /// or null when it is a well-formed topology. Checked here so the problem
    /// shows immediately, rather than only after the store refuses it.
    /// </summary>
    public string? TopologyProblem => Validate(Rows);

    public string? ValidationProblem => TopologyProblem;

    /// <summary>
    /// Refreshes only Windows-observed display state for the already-loaded
    /// rows. Never reloads configuration or rebuilds the arrangement -- called
    /// on a timer while the Displays page is open.
    /// </summary>
    public async Task RefreshLiveStatusAsync()
    {
        (DisplayProvider? display, _) = _workspace.ReadProvider<DisplayProvider>();

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
            return;
        }

        foreach (LayoutMonitorRow row in Rows)
        {
            DisplayMonitorMapping? mapping = snapshot.Mappings.FirstOrDefault(candidate =>
                string.Equals(candidate.LogicalName, row.MonitorName, StringComparison.OrdinalIgnoreCase)
            );

            DisplayMonitorInfo? monitorInfo = snapshot.Monitors.FirstOrDefault(candidate =>
                string.Equals(candidate.LogicalName, row.MonitorName, StringComparison.OrdinalIgnoreCase)
                || (
                    mapping is not null
                    && string.Equals(candidate.DevicePath, mapping.DevicePath, StringComparison.OrdinalIgnoreCase)
                )
            );

            row.SetDisplayDetails(monitorInfo, mapping);
        }

        // Windows may now report a different resolution than the arrangement
        // was last projected from -- a television that woke up, a mode change
        // outside PCMode -- so the preview reflows. Neither recompute reports
        // a change, so a live tick still never marks the page dirty.
        RefreshArrangement();
    }

    /// <summary>
    /// Re-projects the arrangement after the monitors' own geometry changed
    /// underneath the rows, rather than the rows themselves changing.
    /// </summary>
    internal void RefreshArrangement()
    {
        RecomputeAnchorChoices();
        RecomputePreview();
    }

    /// <summary>Loads the named layout's arrangement, or a blank draft when it does not exist yet (including when nothing is selected at all).</summary>
    public void Load(object? entityKey)
    {
        string? name = entityKey as string;

        (DisplayProvider? display, string? problem) = _workspace.ReadProvider<DisplayProvider>();

        RefreshCursorChoices();

        if (display is null)
        {
            // Nothing readable: no monitors to offer and none to show. Clearing
            // the pool too keeps CanAddMonitor honest rather than offering
            // monitors from a configuration that can no longer be read.
            _monitors.SetChoices([]);
            _monitors.Clear();
            _snapshot = null;
            SelectedMonitor = null;
            NotifyMembership();
            _otherRequirements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _cursorPositionName = NoCursorPosition;
            _loadedCursorPositionName = NoCursorPosition;
            OnPropertyChanged(nameof(CursorPositionName));
            return;
        }

        DisplayLayout? layout = display.Layouts.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)
        );

        LoadRequirements(layout);
        LoadCursorLink(name);
        BuildRows(display, layout);
    }

    /// <summary>Builds the edit for <see cref="LayoutConfigurationStore.Save"/> -- the monitor arrangement and this layout's own (non-cursor) 'requires'. The cursor link is written separately; see <see cref="ApplyCursorLink"/>.</summary>
    public (IReadOnlyList<LayoutMonitorEdit> Monitors, IReadOnlyDictionary<string, string> Requires) BuildEdit()
    {
        List<LayoutMonitorEdit> monitors =
        [
            .. Rows.Select(row => new LayoutMonitorEdit(
                row.MonitorName,
                row.Primary,
                row.Primary ? null : row.Direction,
                row.Primary ? null : row.Anchor,
                row.RefreshHz,
                row.Primary ? null : row.Alignment,
                row.Primary ? null : row.Offset
            )),
        ];

        return (monitors, _otherRequirements);
    }

    /// <summary>
    /// Writes <see cref="CursorPositionName"/> as the 'cursor' resource of
    /// every Space, Activity and Source that currently sets 'display' to
    /// <paramref name="layoutName"/> -- called once the layout itself has
    /// saved under that name. A no-op, deliberately, when the picker was
    /// never touched: writing on every save regardless would clear or
    /// overwrite a 'cursor' resource on levels this section never showed the
    /// user it was about to change. Null on success; a level nothing yet
    /// references this layout at is not an error, since there is then simply
    /// nowhere to write the link to yet.
    /// </summary>
    public string? ApplyCursorLink(string layoutName)
    {
        if (string.Equals(_cursorPositionName, _loadedCursorPositionName, StringComparison.Ordinal))
        {
            return null;
        }

        (ModeConfiguration configuration, _) = _workspace.Read();

        IReadOnlyList<NodeAddress> addresses = DisplayLayoutCursorLink.AddressesFor(configuration.Spaces, layoutName);

        if (addresses.Count == 0)
        {
            return string.Equals(_cursorPositionName, NoCursorPosition, StringComparison.Ordinal)
                ? null
                : $"'{layoutName}' is not yet set as the display for any Space, Activity or Source, so "
                    + $"'{_cursorPositionName}' could not be linked to it. Set '{layoutName}' as the display "
                    + "somewhere first, then link the cursor position again.";
        }

        var edit = new Dictionary<string, ResourceEdit>
        {
            [CursorProvider.ResourceKey] = string.Equals(
                _cursorPositionName,
                NoCursorPosition,
                StringComparison.Ordinal
            )
                ? ResourceEdit.Cleared
                : ResourceEdit.Of(_cursorPositionName, optional: true),
        };

        foreach (NodeAddress address in addresses)
        {
            ModeSaveOutcome outcome = HierarchyStore.SaveResources(_workspace.Root, address, edit);

            if (!outcome.Success)
            {
                return outcome.Problem;
            }
        }

        _loadedCursorPositionName = _cursorPositionName;
        return null;
    }

    public string? Save() =>
        // The actual write happens through Displays.DisplaysViewModel, which
        // also owns which layout name this is being saved as (new vs.
        // renamed-in-place) -- this section has no store call of its own to
        // make independently, unlike a section backed by its own resource.
        // Kept as a no-op success so IConfigurationSection's contract still
        // holds if a future page composes this section on its own.
        null;

    /// <summary>
    /// Creates a new saved cursor position, centered, and links this layout
    /// to it. The position's own X/Y are then edited through
    /// <c>Sections.CursorSectionViewModel</c>, the same as any other linked
    /// position.
    /// </summary>
    public string? CreateCursorPosition(string name)
    {
        string trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return "Give the cursor position a name.";
        }

        if (
            CursorPositionChoices.Any(existing =>
                !string.Equals(existing, NoCursorPosition, StringComparison.Ordinal)
                && string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return $"'{trimmed}' is already a configured cursor position.";
        }

        ModeSaveOutcome outcome = CursorPositionConfigurationStore.Save(
            _workspace.Root,
            trimmed,
            xPercent: 50,
            xFromRight: null,
            yPercent: 50,
            yFromBottom: null
        );

        if (!outcome.Success)
        {
            return outcome.Problem ?? "The cursor position could not be created.";
        }

        RefreshCursorChoices();
        CursorPositionName = trimmed;
        return null;
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

        if (!Rows.Contains(monitor))
        {
            return false;
        }

        LayoutMonitorRow[] anchors =
        [
            .. Rows.Where(candidate =>
                candidate != monitor && (monitor.Primary || !WouldCreateCycle(monitor, candidate))
            ),
        ];

        if (anchors.Length == 0)
        {
            return false;
        }

        (LayoutMonitorRow Anchor, string Direction, double Distance) best = anchors
            .SelectMany(anchor =>
                CandidateDrops(monitor, anchor)
                    .Select(candidate =>
                        (
                            Anchor: anchor,
                            candidate.Direction,
                            Distance: DistanceSquared(droppedLeft, droppedTop, candidate.Left, candidate.Top)
                        )
                    )
            )
            .MinBy(candidate => candidate.Distance);

        _suppressRecompute = true;

        if (monitor.Primary)
        {
            // Dragging the primary keeps it pinned at the origin -- see the
            // class remarks -- so what actually moves is the other monitor,
            // snapped flush with no alignment freedom: the raw pixel the
            // primary itself was dropped at is not an edge of the monitor
            // whose placement this call is actually writing.
            best.Anchor.SetPlacement(LayoutDirectionChoice.OppositeOf(best.Direction), monitor.MonitorName);
        }
        else
        {
            (string alignment, int offset) = ResolveAlignment(
                best.Direction,
                best.Anchor,
                monitor,
                droppedLeft,
                droppedTop
            );
            monitor.SetPlacement(best.Direction, best.Anchor.MonitorName, alignment, offset);
        }

        _suppressRecompute = false;
        _onChanged();
        RecomputeAnchorChoices();
        RecomputePreview();
        SelectMonitor(monitor);
        return true;
    }

    /// <summary>
    /// Turns a raw drop point into the named alignment it lands within a snap
    /// zone of, or -- when it does not land near any of the three -- a free
    /// pixel offset from the anchor's own start edge. The continuous
    /// counterpart to <see cref="CandidateDrops"/>'s four flush corners,
    /// along whichever axis <paramref name="direction"/> leaves free.
    /// </summary>
    private (string Alignment, int Offset) ResolveAlignment(
        string direction,
        LayoutMonitorRow anchor,
        LayoutMonitorRow monitor,
        double droppedLeft,
        double droppedTop
    )
    {
        const double snapPreviewPixels = 14;

        bool vertical = direction is LayoutDirectionChoice.LeftOf or LayoutDirectionChoice.RightOf;

        double anchorStart = vertical ? anchor.PreviewTop : anchor.PreviewLeft;
        double anchorSize = vertical ? anchor.PreviewHeight : anchor.PreviewWidth;
        double monitorSize = vertical ? monitor.PreviewHeight : monitor.PreviewWidth;
        double dropped = vertical ? droppedTop : droppedLeft;

        (string Alignment, double Position)[] candidates =
        [
            (LayoutAlignmentChoice.Start, anchorStart),
            (LayoutAlignmentChoice.Center, anchorStart + ((anchorSize - monitorSize) / 2)),
            (LayoutAlignmentChoice.End, anchorStart + anchorSize - monitorSize),
        ];

        (string Alignment, double Position) nearest = candidates.MinBy(candidate =>
            Math.Abs(candidate.Position - dropped)
        );

        if (Math.Abs(nearest.Position - dropped) <= snapPreviewPixels)
        {
            return (nearest.Alignment, 0);
        }

        double scale = _previewScale > 0 ? _previewScale : 1;
        int freeOffset = (int)Math.Round((dropped - anchorStart) / scale, MidpointRounding.AwayFromZero);

        return (LayoutAlignmentChoice.Start, freeOffset);
    }

    /// <summary>Sets the linked position's name without marking the page dirty -- for a rename that the store already committed on disk (the position itself, and every Space/Activity/Source resource naming it, via <c>LayoutConfigurationStore.RenameModeReferences</c>), so nothing further needs saving.</summary>
    internal void SetLinkedCursorPositionAfterRename(string name)
    {
        _cursorPositionName = name;
        _loadedCursorPositionName = name;
        OnPropertyChanged(nameof(CursorPositionName));
    }

    internal void RefreshCursorChoices()
    {
        (CursorProvider? cursor, _) = _workspace.ReadProvider<CursorProvider>();

        CursorPositionChoices.Clear();
        CursorPositionChoices.Add(NoCursorPosition);

        if (cursor is null)
        {
            return;
        }

        foreach (string name in cursor.PositionNames)
        {
            CursorPositionChoices.Add(name);
        }
    }

    private void LoadRequirements(DisplayLayout? layout)
    {
        var others = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 'cursor' can never appear here -- DisplayLayout.TryParse refuses it
        // when the document is read -- so every entry a layout has is a real,
        // non-cursor requirement (a television, say) to carry forward as-is.
        foreach (ResourceRequirement requirement in layout?.Requires ?? [])
        {
            others[requirement.Key] = requirement.Target;
        }

        _otherRequirements = others;
    }

    /// <summary>
    /// Reads the cursor position (if any) currently linked to
    /// <paramref name="layoutName"/> from whichever Space, Activity or Source
    /// sets 'display' to it -- see <see cref="DisplayLayoutCursorLink"/>. Also
    /// remembers it as the loaded baseline, so <see cref="ApplyCursorLink"/>
    /// can tell "the user left this alone" from "the user chose this again"
    /// and only write when something actually changed.
    /// </summary>
    private void LoadCursorLink(string? layoutName)
    {
        string linked = NoCursorPosition;

        if (layoutName is not null)
        {
            (ModeConfiguration configuration, _) = _workspace.Read();

            linked = DisplayLayoutCursorLink.Find(configuration.Spaces, layoutName) ?? NoCursorPosition;
        }

        _cursorPositionName = linked;
        _loadedCursorPositionName = linked;
        OnPropertyChanged(nameof(CursorPositionName));
    }

    private void BuildRows(DisplayProvider display, DisplayLayout? layout)
    {
        string? keepMonitor = _selectedMonitor?.MonitorName;
        _suppressRecompute = true;

        try
        {
            _snapshot = display.Discover();
        }
        catch (Exception)
        {
            // Discovery is read-only and best-effort here: refresh rate
            // choices simply fall back to whatever was already configured.
            _snapshot = null;
        }

        _monitors.SetChoices(display.MonitorNames);

        // A row exists only for a monitor this layout actually uses -- unlike
        // the fixed one-row-per-monitor model this replaced, where every
        // monitor had a row and an Included flag decided membership.
        _monitors.Load(layout?.Monitors ?? []);

        foreach (LayoutMonitorRow row in Rows)
        {
            row.Primary = string.Equals(layout?.Primary, row.MonitorName, StringComparison.OrdinalIgnoreCase);

            if (layout is not null && layout.Placements.TryGetValue(row.MonitorName, out DisplayPlacement? placement))
            {
                row.Direction = placement.Direction.ToString();
                row.Anchor = placement.Anchor;
                row.Alignment = placement.Alignment.ToString();
                row.Offset = placement.Offset;
            }

            double? configuredHz = layout?.Refresh.TryGetValue(row.MonitorName, out double hz) == true ? hz : null;

            PopulateRefreshChoices(row, _snapshot, row.MonitorName, configuredHz);
        }

        _suppressRecompute = false;
        RecomputeAnchorChoices();
        SelectedMonitor =
            Rows.FirstOrDefault(row => string.Equals(row.MonitorName, keepMonitor, StringComparison.OrdinalIgnoreCase))
            ?? Rows.FirstOrDefault(row => row.Primary)
            ?? Rows.FirstOrDefault();
        RecomputePreview();
        NotifyMembership();
    }

    /// <summary>
    /// Builds a row for a monitor the user just picked, resolved against the
    /// last discovery. The <see cref="SelectionList{TRow}"/> factory.
    /// </summary>
    private LayoutMonitorRow CreateRow(string? monitorName)
    {
        var row = new LayoutMonitorRow(monitorName ?? string.Empty, OnRowChanged);

        ResolveRow(row, configuredHz: null);

        return row;
    }

    /// <summary>
    /// Points a row's Windows details and refresh choices at whichever monitor
    /// it currently names -- on creation, and again whenever its picker changes
    /// which monitor that is.
    /// </summary>
    private void ResolveRow(LayoutMonitorRow row, double? configuredHz)
    {
        DisplayMonitorMapping? mapping = _snapshot?.Mappings.FirstOrDefault(candidate =>
            string.Equals(candidate.LogicalName, row.MonitorName, StringComparison.OrdinalIgnoreCase)
        );

        DisplayMonitorInfo? monitorInfo = _snapshot?.Monitors.FirstOrDefault(candidate =>
            string.Equals(candidate.LogicalName, row.MonitorName, StringComparison.OrdinalIgnoreCase)
            || (
                mapping is not null
                && string.Equals(candidate.DevicePath, mapping.DevicePath, StringComparison.OrdinalIgnoreCase)
            )
        );

        // DevicePath first: the monitor-scoped DDC/CI and television sections
        // re-resolve against it, so it has to already name the new monitor by
        // the time this row's change reaches them.
        row.SetDisplayDetails(monitorInfo, mapping);

        PopulateRefreshChoices(row, _snapshot, row.MonitorName, configuredHz);
    }

    /// <summary>A monitor was added or removed: everything derived from membership is stale.</summary>
    private void OnMembershipChanged()
    {
        _onChanged();
        RecomputeAnchorChoices();
        RecomputePreview();
        NotifyMembership();

        // The DDC/CI and television sections are one per row, so the page has
        // to rebuild them even though which row is selected did not change.
        _onSelectedMonitorChanged(_selectedMonitor);
    }

    private void NotifyMembership()
    {
        OnPropertyChanged(nameof(HasMonitors));
        OnPropertyChanged(nameof(CanAddMonitor));
        OnPropertyChanged(nameof(CanRemoveMonitor));
    }

    private static void PopulateRefreshChoices(
        LayoutMonitorRow row,
        DisplaySnapshot? snapshot,
        string monitor,
        double? configuredHz
    )
    {
        row.RefreshChoices.Clear();
        row.RefreshChoices.Add(LayoutMonitorRow.NoPreference);

        IReadOnlyList<int> discovered =
            snapshot
                ?.Monitors.FirstOrDefault(info =>
                    string.Equals(info.LogicalName, monitor, StringComparison.OrdinalIgnoreCase)
                )
                ?.SupportedRefreshHz
            ?? [];

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

    private static IEnumerable<(string Direction, double Left, double Top)> CandidateDrops(
        LayoutMonitorRow monitor,
        LayoutMonitorRow anchor
    )
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
                string.Equals(row.MonitorName, current.Anchor, StringComparison.OrdinalIgnoreCase)
            );
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

        if (propertyName == nameof(LayoutMonitorRow.MonitorName))
        {
            // This row is now a different monitor. Its Windows details and
            // refresh rates belong to that monitor, and every other row's
            // picker must stop offering the one just taken (and start
            // offering the one just released). Suppressed while resolving,
            // since PopulateRefreshChoices writes SelectedRefresh and would
            // otherwise re-enter here.
            _suppressRecompute = true;
            ResolveRow(changed, configuredHz: null);
            _suppressRecompute = false;

            _monitors.RefreshAvailableSelections();

            // The DDC/CI and television sections key off the row's DevicePath,
            // which ResolveRow just repointed.
            _onSelectedMonitorChanged(_selectedMonitor);
        }

        _onChanged();

        // A renamed row can leave others anchored to a monitor no longer in
        // the layout; RecomputeAnchorChoices repairs those the same way it
        // does after a Remove.
        RecomputeAnchorChoices(propertyName == nameof(LayoutMonitorRow.Primary) && changed.Primary ? changed : null);
        RecomputePreview();
    }

    /// <summary>
    /// Keeps exactly one monitor primary -- turning one on demotes any other,
    /// and removing the only primary promotes the first remaining monitor
    /// rather than leaving the layout without one -- and keeps every row's
    /// anchor choices limited to the layout's other monitors, since an anchor
    /// naming anything else is refused downstream anyway.
    /// </summary>
    private void RecomputeAnchorChoices(LayoutMonitorRow? requestedPrimary = null)
    {
        _suppressRecompute = true;

        List<LayoutMonitorRow> included = [.. Rows];
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
            IReadOnlyList<string> choices =
            [
                .. included.Where(other => other != row).Select(other => other.MonitorName),
            ];

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
    /// Every row is part of the layout, so the whole canvas is the arrangement --
    /// there is no shelf of excluded monitors to reserve room for, since a
    /// monitor not in the layout has no row at all.
    /// </summary>
    private void RecomputePreview()
    {
        const double canvasWidth = 900;
        const double arrangementHeight = 440;
        const double padding = 18;

        List<LayoutMonitorRow> included = [.. Rows];

        if (included.Count > 0)
        {
            LayoutMonitorRow origin = included.FirstOrDefault(row => row.Primary) ?? included[0];
            var positions = new Dictionary<LayoutMonitorRow, (double X, double Y)> { [origin] = (0, 0) };
            List<LayoutMonitorRow> remaining = [.. included.Where(row => row != origin)];

            while (remaining.Count > 0)
            {
                bool progressed = false;

                foreach (LayoutMonitorRow row in remaining.ToList())
                {
                    LayoutMonitorRow? anchor = included.FirstOrDefault(candidate =>
                        string.Equals(candidate.MonitorName, row.Anchor, StringComparison.OrdinalIgnoreCase)
                    );

                    if (anchor is null || !positions.TryGetValue(anchor, out (double X, double Y) anchorPosition))
                    {
                        continue;
                    }

                    double along(int anchorSize, int monitorSize) =>
                        row.Alignment switch
                        {
                            LayoutAlignmentChoice.Center => (anchorSize - monitorSize) / 2.0,
                            LayoutAlignmentChoice.End => anchorSize - monitorSize,
                            _ => 0,
                        } + row.Offset;

                    positions[row] = row.Direction switch
                    {
                        LayoutDirectionChoice.LeftOf => (
                            anchorPosition.X - row.LayoutWidth,
                            anchorPosition.Y + along(anchor.LayoutHeight, row.LayoutHeight)
                        ),
                        LayoutDirectionChoice.Above => (
                            anchorPosition.X + along(anchor.LayoutWidth, row.LayoutWidth),
                            anchorPosition.Y - row.LayoutHeight
                        ),
                        LayoutDirectionChoice.Below => (
                            anchorPosition.X + along(anchor.LayoutWidth, row.LayoutWidth),
                            anchorPosition.Y + anchor.LayoutHeight
                        ),
                        _ => (
                            anchorPosition.X + anchor.LayoutWidth,
                            anchorPosition.Y + along(anchor.LayoutHeight, row.LayoutHeight)
                        ),
                    };

                    remaining.Remove(row);
                    progressed = true;
                }

                if (!progressed)
                {
                    break;
                }
            }

            double fallbackX =
                positions.Count == 0 ? 0 : positions.Max(entry => entry.Value.X + entry.Key.LayoutWidth) + 160;

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
                (arrangementHeight - (padding * 2)) / Math.Max(1, maxY - minY)
            );
            scale = Math.Min(scale, 0.15);
            _previewScale = scale;

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
                    Math.Max(64, row.LayoutHeight * scale)
                );
            }
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
        List<LayoutMonitorRow> included = [.. rows];

        if (included.Count == 0)
        {
            return "Add at least one monitor.";
        }

        List<LayoutMonitorRow> primaries = [.. included.Where(row => row.Primary)];

        if (primaries.Count != 1)
        {
            return "Exactly one monitor must be primary.";
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
                return $"{string.Join(", ", remaining.Select(row => $"'{row.MonitorName}'"))} cannot be placed -- "
                    + $"their placements form a loop, or a chain that never reaches the primary ('{primary}').";
            }
        }

        return null;
    }
}

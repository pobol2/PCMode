using PCMode.Engine;
using PCMode.Tray.Elements.Displays;
using PCMode.Tray.Elements.Displays.Sections;
using PCMode.Tray.Elements;
using PCMode.Tray.Elements.Configuration;
using PCMode.Tray.Tests;

namespace PCMode.Tray.Tests.Features.Displays;

/// <summary>
/// <see cref="DisplaysViewModel"/>'s composition of <see cref="Displays.Sections.LayoutSectionViewModel"/>
/// and <see cref="Displays.Sections.CursorSectionViewModel"/> -- the page owns
/// the selected layout and its Save/Discard/dirty state; each section owns
/// only its own facet.
/// </summary>
/// <remarks>
/// A layout's monitors are a <c>SelectionList</c>: one row per monitor the
/// layout actually uses, added and removed by the user, each row's picker
/// choosing which configured monitor it is. There is no row for a monitor the
/// layout does not use, and so no Included flag -- membership IS inclusion.
/// Adding the first monitor promotes it to primary automatically, since a
/// layout without a primary is one <c>LayoutConfigurationStore.Save</c> would
/// throw on.
/// </remarks>
public sealed class DisplaysViewModelTests
{
    private const string TwoMonitorsConfiguration = """
        {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
          {"id":"desktop","name":"Desktop","resources":{}}]}],
         "providers":{"display":{"monitors":{"Left":"FAKE-PATH-1","Right":"FAKE-PATH-2"}}}}
        """;

    [Fact]
    public void A_new_layout_starts_with_no_monitors_and_cannot_be_saved()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));

        Assert.Null(viewModel.StartNewLayout("Test layout"));

        Assert.Empty(viewModel.LayoutSection.Rows);
        Assert.True(viewModel.HasUnsavedChanges);

        Assert.NotNull(viewModel.ValidationProblem);
        Assert.Contains("Add at least one monitor", viewModel.ValidationProblem, StringComparison.Ordinal);
        Assert.False(viewModel.CanSave);
    }

    [Fact]
    public void CanSave_becomes_true_once_a_monitor_is_added_and_promoted_to_primary()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));
        viewModel.StartNewLayout("Test layout");

        viewModel.LayoutSection.AddMonitor();

        LayoutMonitorRow added = Assert.Single(viewModel.LayoutSection.Rows);
        Assert.Equal("Left", added.MonitorName);
        Assert.True(added.Primary);

        Assert.Null(viewModel.ValidationProblem);
        Assert.True(viewModel.CanSave);
    }

    /// <summary>
    /// The pool shrinks as it is used: a monitor already picked by one row is
    /// not offered by any other, so the same monitor cannot be added twice.
    /// </summary>
    [Fact]
    public void Each_monitor_can_only_be_picked_once_and_Add_stops_when_the_pool_is_used_up()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));
        viewModel.StartNewLayout("Test layout");

        viewModel.LayoutSection.AddMonitor();
        viewModel.LayoutSection.AddMonitor();

        Assert.Equal(
            ["Left", "Right"],
            viewModel.LayoutSection.Rows.Select(row => row.MonitorName));

        // Each row still offers its own choice, but never the other row's.
        Assert.Equal(["Left"], viewModel.LayoutSection.Rows[0].AvailableSelections);
        Assert.Equal(["Right"], viewModel.LayoutSection.Rows[1].AvailableSelections);

        // Both configured monitors are now in use.
        Assert.False(viewModel.LayoutSection.CanAddMonitor);
        viewModel.LayoutSection.AddMonitor();
        Assert.Equal(2, viewModel.LayoutSection.Rows.Count);
    }

    [Fact]
    public void Removing_a_monitor_returns_it_to_the_pool_and_re_anchors_whatever_pointed_at_it()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));
        viewModel.StartNewLayout("Test layout");

        viewModel.LayoutSection.AddMonitor();
        viewModel.LayoutSection.AddMonitor();

        LayoutMonitorRow left = viewModel.LayoutSection.Rows[0];
        LayoutMonitorRow right = viewModel.LayoutSection.Rows[1];
        Assert.Equal("Left", right.Anchor);

        viewModel.LayoutSection.RemoveMonitor(left);

        LayoutMonitorRow remaining = Assert.Single(viewModel.LayoutSection.Rows);
        Assert.Same(right, remaining);

        // The only monitor left is promoted, so nothing is left anchored to a
        // monitor the layout no longer contains.
        Assert.True(remaining.Primary);
        Assert.True(viewModel.LayoutSection.CanAddMonitor);
        Assert.Equal(["Left", "Right"], remaining.AvailableSelections);
        Assert.Null(viewModel.ValidationProblem);
    }

    [Fact]
    public void Repointing_a_row_at_a_different_monitor_frees_the_one_it_released()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));
        viewModel.StartNewLayout("Test layout");

        viewModel.LayoutSection.AddMonitor();
        LayoutMonitorRow row = viewModel.LayoutSection.Rows[0];
        Assert.Equal("Left", row.MonitorName);

        row.MonitorName = "Right";

        Assert.Equal("Right", row.MonitorName);
        Assert.Equal(["Left", "Right"], row.AvailableSelections);
        Assert.True(viewModel.LayoutSection.CanAddMonitor);

        (IReadOnlyList<LayoutMonitorEdit> monitors, _) = viewModel.LayoutSection.BuildEdit();
        Assert.Equal(["Right"], monitors.Select(monitor => monitor.Monitor));
    }

    /// <summary>
    /// A cursor position links to a layout by being written onto whichever
    /// Space/Activity/Source already sets 'display' to that layout's name
    /// (see <see cref="DisplayLayoutCursorLink"/>) -- never into the layout's
    /// own document, which can never require the cursor (it always runs
    /// after the display; see <c>DisplayLayout.TryParse</c>). So every test
    /// below that links a position first saves the layout on its own, points
    /// this fixture's one activity at it the way the Spaces page would, and
    /// only then links -- there is nowhere to write a link before that.
    /// </summary>
    private static void SetActivityDisplay(string root, string layoutName) =>
        Assert.True(HierarchyStore.SaveResources(
            root,
            new NodeAddress("desk", "desktop"),
            new Dictionary<string, ResourceEdit> { ["display"] = ResourceEdit.Of(layoutName) }).Success);

    /// <summary>Starts a draft with one monitor -- the smallest saveable layout.</summary>
    private static void StartSaveableLayout(DisplaysViewModel viewModel, string name)
    {
        viewModel.StartNewLayout(name);
        viewModel.LayoutSection.AddMonitor();
    }

    private static IReadOnlyList<string> ReadLayoutNames(TemporaryConfigurationRoot root)
    {
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(root.Path, json);
        _ = ModeConfiguration.Parse(json, providers);
        return providers.OfType<DisplayProvider>().Single().LayoutNames;
    }

    /// <summary>
    /// Displays reuses <see cref="ConfigurationEditingSession{TKey, TEntity}"/>
    /// the same way Audio's targets do: renaming an already-saved entity
    /// retitles the selector only, and the actual
    /// <see cref="LayoutConfigurationStore.Rename"/> (with its mode-reference
    /// rewrite) does not run until the page's own Save.
    /// </summary>
    [Fact]
    public void Renaming_a_saved_layout_defers_the_write_to_disk_until_save()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));
        StartSaveableLayout(viewModel, "Test layout");
        Assert.True(viewModel.TrySaveChanges());

        string? problem = viewModel.RenameSelected("Renamed layout");

        Assert.Null(problem);
        Assert.Equal("Renamed layout", viewModel.EditingLayoutName);
        Assert.True(viewModel.HasSelectedLayout);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.Contains("Test layout", ReadLayoutNames(root));
        Assert.DoesNotContain("Renamed layout", ReadLayoutNames(root));

        Assert.True(viewModel.TrySaveChanges(), viewModel.ErrorMessage);

        Assert.DoesNotContain("Test layout", ReadLayoutNames(root));
        Assert.Contains("Renamed layout", ReadLayoutNames(root));
    }

    /// <summary>
    /// Before <see cref="ConfigurationEditingSession{TKey, TEntity}"/>, Create
    /// and Duplicate cleared <c>SelectedKey</c> while only
    /// <c>EditingLayoutName</c> held the draft, so the framework's own
    /// HasSelection-gated Essential settings/empty-state split would have
    /// hidden the very editor the draft needed. The session keeps a key
    /// selected throughout instead.
    /// </summary>
    [Fact]
    public void Creating_and_duplicating_a_layout_keep_it_selected_for_the_shared_selector()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));
        viewModel.Refresh();

        Assert.Null(viewModel.StartNewLayout("Draft"));
        Assert.True(viewModel.HasSelectedLayout);
        Assert.Equal("Draft", viewModel.SelectedLayout);

        viewModel.LayoutSection.AddMonitor();
        Assert.True(viewModel.TrySaveChanges(), viewModel.ErrorMessage);

        string? problem = viewModel.DuplicateSelected("Draft copy");

        Assert.Null(problem);
        Assert.True(viewModel.HasSelectedLayout);
        Assert.Equal("Draft copy", viewModel.SelectedLayout);

        // Duplicate keeps whatever arrangement was already open in the
        // editor rather than reloading it -- the copy starts identical.
        Assert.Equal(["Left"], viewModel.LayoutSection.Rows.Select(row => row.MonitorName));
    }

    [Fact]
    public void Saving_links_the_cursor_position_once_something_uses_the_layout()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));
        StartSaveableLayout(viewModel, "Test layout");

        Assert.True(viewModel.TrySaveChanges());
        SetActivityDisplay(root.Path, "Test layout");

        Assert.Null(viewModel.LayoutSection.CreateCursorPosition("Center"));
        Assert.Equal("Center", viewModel.LayoutSection.CursorPositionName);

        bool saved = viewModel.TrySaveChanges();

        Assert.True(saved, viewModel.ErrorMessage);
        Assert.Equal("Center", viewModel.CursorSection.LinkedPositionName);
        Assert.True(viewModel.CursorSection.HasLinkedPosition);
    }

    [Fact]
    public void Linking_before_anything_uses_the_layout_explains_why_it_could_not_write()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        // The mirror image of the test above: nothing sets 'display' to this
        // layout, so there is nowhere for the link to go. Not a crash and not
        // a silent no-op -- a specific, actionable refusal.
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));
        StartSaveableLayout(viewModel, "Test layout");
        Assert.True(viewModel.TrySaveChanges());

        Assert.Null(viewModel.LayoutSection.CreateCursorPosition("Center"));

        bool saved = viewModel.TrySaveChanges();

        Assert.False(saved);
        Assert.Contains("Test layout", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.False(viewModel.CursorSection.HasLinkedPosition);
    }

    [Fact]
    public void Renaming_the_linked_cursor_position_updates_the_link_without_leaving_the_page_dirty()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));
        StartSaveableLayout(viewModel, "Test layout");
        Assert.True(viewModel.TrySaveChanges());
        SetActivityDisplay(root.Path, "Test layout");
        viewModel.LayoutSection.CreateCursorPosition("Center");
        Assert.True(viewModel.TrySaveChanges());
        Assert.Equal("Center", viewModel.CursorSection.LinkedPositionName);

        string? problem = viewModel.RenameCursorPosition("Middle");

        Assert.Null(problem);
        Assert.Equal("Middle", viewModel.LayoutSection.CursorPositionName);
        Assert.Equal("Middle", viewModel.CursorSection.LinkedPositionName);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public void Duplicating_the_linked_cursor_position_links_the_copy_and_marks_the_page_dirty()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));
        StartSaveableLayout(viewModel, "Test layout");
        Assert.True(viewModel.TrySaveChanges());
        SetActivityDisplay(root.Path, "Test layout");
        viewModel.LayoutSection.CreateCursorPosition("Center");
        Assert.True(viewModel.TrySaveChanges());

        string? problem = viewModel.DuplicateCursorPosition("Center copy");

        Assert.Null(problem);
        Assert.Equal("Center copy", viewModel.LayoutSection.CursorPositionName);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.True(viewModel.TrySaveChanges());
        Assert.Equal("Center copy", viewModel.CursorSection.LinkedPositionName);
    }

    [Fact]
    public void Deleting_a_position_still_linked_to_this_layout_unlinks_first_and_explains_the_second_click()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));
        StartSaveableLayout(viewModel, "Test layout");
        Assert.True(viewModel.TrySaveChanges());
        SetActivityDisplay(root.Path, "Test layout");
        viewModel.LayoutSection.CreateCursorPosition("Center");
        Assert.True(viewModel.TrySaveChanges());

        // First click: the Space/Activity/Source this layout is linked
        // through still sets 'cursor' to "Center" on disk until this unlink
        // itself is saved, so the store refuses the real delete -- but the
        // unlink (a real edit) still happens.
        string? problem = viewModel.DeleteCursorPosition();

        Assert.Null(problem);
        Assert.Equal(LayoutSectionViewModel.NoCursorPosition, viewModel.LayoutSection.CursorPositionName);
        Assert.False(viewModel.CursorSection.HasLinkedPosition);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.Contains("unlinked", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deleting_a_position_nothing_on_disk_requires_yet_succeeds_immediately()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));
        StartSaveableLayout(viewModel, "Test layout");

        // Created and linked, but the layout itself was never saved, so
        // nothing on disk requires "Center" yet.
        Assert.Null(viewModel.LayoutSection.CreateCursorPosition("Center"));

        string? problem = viewModel.DeleteCursorPosition();

        Assert.Null(problem);
        Assert.Contains("deleted", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Center", viewModel.LayoutSection.CursorPositionChoices, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Selecting_a_different_layout_loads_only_that_layouts_own_monitors()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));
        viewModel.Refresh();

        StartSaveableLayout(viewModel, "First");
        Assert.Equal("Left", viewModel.LayoutSection.Rows[0].MonitorName);
        Assert.True(viewModel.TrySaveChanges());

        StartSaveableLayout(viewModel, "Second");
        viewModel.LayoutSection.Rows[0].MonitorName = "Right";
        Assert.True(viewModel.TrySaveChanges());

        viewModel.SelectedLayout = "First";

        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Equal(["Left"], viewModel.LayoutSection.Rows.Select(row => row.MonitorName));
    }

    /// <summary>
    /// Dragging is the primary way a layout's arrangement is now set --
    /// <see cref="LayoutSectionViewModel.TrySnapMonitor"/> turns a raw drop
    /// point into a named alignment when it lands within a snap zone of one,
    /// and a free pixel offset otherwise. These two tests exercise both
    /// branches directly against the preview geometry <c>RecomputePreview</c>
    /// actually produced for two differently-sized monitors, rather than
    /// against <see cref="DisplayLayout.Positions"/>'s own arithmetic, which
    /// <c>DisplayLayoutTests</c> already covers in isolation.
    /// </summary>
    private static (LayoutMonitorRow Left, LayoutMonitorRow Right) ArrangeTwoDifferentlySizedMonitors(
        DisplaysViewModel viewModel)
    {
        viewModel.StartNewLayout("Test layout");
        viewModel.LayoutSection.AddMonitor();
        viewModel.LayoutSection.AddMonitor();

        LayoutMonitorRow left = viewModel.LayoutSection.Rows[0];
        LayoutMonitorRow right = viewModel.LayoutSection.Rows[1];

        left.SetDisplayDetails(
            new DisplayMonitorInfo("Left", "FAKE-PATH-1", true, true, 0, 0, 2560, 1440, 60, [60], "Left"), null);
        right.SetDisplayDetails(
            new DisplayMonitorInfo("Right", "FAKE-PATH-2", true, false, 0, 0, 1920, 1080, 60, [60], "Right"), null);

        // The sizes above were written straight onto the rows, so the
        // arrangement has to be re-projected from them before the drop points
        // below mean anything.
        viewModel.LayoutSection.RefreshArrangement();

        return (left, right);
    }

    [Fact]
    public void Dropping_at_the_anchors_true_center_point_snaps_alignment_to_center_with_zero_offset()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));
        (LayoutMonitorRow left, LayoutMonitorRow right) = ArrangeTwoDifferentlySizedMonitors(viewModel);

        double centeredTop = left.PreviewTop + ((left.PreviewHeight - right.PreviewHeight) / 2);

        bool snapped = viewModel.LayoutSection.TrySnapMonitor(right, right.PreviewLeft, centeredTop);

        Assert.True(snapped);
        Assert.Equal(LayoutAlignmentChoice.Center, right.Alignment);
        Assert.Equal(0, right.Offset);
    }

    [Fact]
    public void Dropping_well_past_every_named_alignment_falls_back_to_a_free_offset_in_the_drop_direction()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));
        (LayoutMonitorRow left, LayoutMonitorRow right) = ArrangeTwoDifferentlySizedMonitors(viewModel);

        // Twice the anchor's own preview height is well past Start, Center and
        // End alike, whatever the canvas scale turned out to be.
        double farBelow = left.PreviewTop + (2 * left.PreviewHeight);

        bool snapped = viewModel.LayoutSection.TrySnapMonitor(right, right.PreviewLeft, farBelow);

        Assert.True(snapped);
        Assert.Equal(LayoutAlignmentChoice.Start, right.Alignment);
        Assert.True(right.Offset > 0, $"Expected a positive offset for a drop below the anchor; got {right.Offset}.");
    }

    [Fact]
    public void Selecting_a_monitor_keeps_the_shared_monitor_sections_and_does_not_mark_the_page_dirty()
    {
        using var root = new TemporaryConfigurationRoot(TwoMonitorsConfiguration);
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));
        viewModel.StartNewLayout("Test layout");
        viewModel.LayoutSection.AddMonitor();
        viewModel.LayoutSection.AddMonitor();
        bool dirtyAfterStarting = viewModel.HasUnsavedChanges;
        MonitorConfigurationSectionRow[] sections = [.. viewModel.MonitorSections];

        LayoutMonitorRow selected = viewModel.LayoutSection.Rows[1];
        viewModel.LayoutSection.SelectMonitor(selected);

        Assert.Equal(dirtyAfterStarting, viewModel.HasUnsavedChanges);
        Assert.Same(selected, viewModel.LayoutSection.SelectedMonitor);
        Assert.Equal(sections, viewModel.MonitorSections);
    }

    /// <summary>
    /// The monitor-scoped editors follow the layout's own monitor list, the
    /// same way Audio's per-route processing-profile editor follows its
    /// routes -- a monitor the layout does not use has no card.
    /// </summary>
    [Fact]
    public void Ddc_and_television_sections_contain_one_card_for_every_selected_monitor()
    {
        using var root = new TemporaryConfigurationRoot("""
            {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
              {"id":"desktop","name":"Desktop","resources":{}}]}],
             "providers":{
               "display":{"monitors":{"Left":"FAKE-PATH-1","Right":"FAKE-PATH-2"}},
               "ddcMonitors":{"Desk Left":"FAKE-PATH-1"},
               "lgTv":{"default":"Living Room","televisions":{"Living Room":{
                 "enabled":false,"displayMonitor":"FAKE-PATH-2"}}}}}
            """
        );
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));

        viewModel.StartNewLayout("Test layout");

        // Nothing selected yet, so nothing to configure.
        Assert.Empty(viewModel.MonitorSections);

        viewModel.LayoutSection.AddMonitor();
        viewModel.LayoutSection.AddMonitor();

        Assert.Equal(["Left", "Right"], viewModel.MonitorSections.Select(section => section.MonitorName));

        MonitorConfigurationSectionRow left = viewModel.MonitorSections[0];
        Assert.True(left.Ddc.HasBinding);
        Assert.False(left.Television.HasAssociation);
        Assert.Equal("Left", left.MonitorName);

        // The DDC/CI actions belong to a DDC/CI monitor and to no other kind.
        Assert.True(left.ShowDdc);
        Assert.False(left.ShowTelevision);

        MonitorConfigurationSectionRow right = viewModel.MonitorSections[1];
        Assert.False(right.Ddc.HasBinding);
        Assert.True(right.Television.HasAssociation);
        Assert.False(right.ShowDdc);
        Assert.True(right.ShowTelevision);

        right.Television.Host = "unsaved.example.test";
        Assert.Null(left.Ddc.RenameBinding("Renamed Left"));

        viewModel.ReloadMonitorSections();

        Assert.Equal("unsaved.example.test", right.Television.Host);
    }

    /// <summary>
    /// A monitor is either a DDC/CI-capable monitor or a network-controlled
    /// television, never both -- ControlType reflects whichever is actually
    /// bound, and CanChooseControlType locks the picker once one is.
    /// </summary>
    [Fact]
    public void Monitor_control_type_follows_whichever_binding_is_actually_configured()
    {
        using var root = new TemporaryConfigurationRoot("""
            {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
              {"id":"desktop","name":"Desktop","resources":{}}]}],
             "providers":{
               "display":{"monitors":{"Left":"FAKE-PATH-1","Right":"FAKE-PATH-2","Third":"FAKE-PATH-3"}},
               "ddcMonitors":{"Desk Left":"FAKE-PATH-1"},
               "lgTv":{"default":"Living Room","televisions":{"Living Room":{
                 "enabled":false,"displayMonitor":"FAKE-PATH-2"}}}}}
            """
        );
        var viewModel = new DisplaysViewModel(Workspaces.At(root.Path));

        viewModel.StartNewLayout("Test layout");
        viewModel.LayoutSection.AddMonitor();
        viewModel.LayoutSection.AddMonitor();
        viewModel.LayoutSection.AddMonitor();

        MonitorConfigurationSectionRow ddcBound = viewModel.MonitorSections[0];
        Assert.Equal(MonitorControlTypeChoice.Ddc, ddcBound.ControlType);
        Assert.False(ddcBound.CanChooseControlType);
        Assert.True(ddcBound.ShowDdc);
        Assert.False(ddcBound.ShowTelevision);
        Assert.False(ddcBound.Ddc.HasCapabilityBadge);
        Assert.True(viewModel.CanRescanMonitorControls);

        MonitorConfigurationSectionRow tvBound = viewModel.MonitorSections[1];
        Assert.Equal(MonitorControlTypeChoice.Television, tvBound.ControlType);
        Assert.False(tvBound.CanChooseControlType);
        Assert.False(tvBound.ShowDdc);
        Assert.True(tvBound.ShowTelevision);

        MonitorConfigurationSectionRow unbound = viewModel.MonitorSections[2];
        Assert.Equal(MonitorControlTypeChoice.None, unbound.ControlType);
        Assert.True(unbound.CanChooseControlType);
        Assert.False(unbound.ShowDdc);
        Assert.False(unbound.ShowTelevision);

        // A transient pick while unbound just decides which "Add..." prompt
        // shows, and does not touch either child section.
        unbound.ControlType = MonitorControlTypeChoice.Television;
        Assert.True(unbound.ShowTelevision);
        Assert.False(unbound.Television.HasAssociation);

        // Locked once bound: attempting to reassign a bound row's type is ignored.
        ddcBound.ControlType = MonitorControlTypeChoice.Television;
        Assert.Equal(MonitorControlTypeChoice.Ddc, ddcBound.ControlType);

        // Removing the binding unlocks the picker and resets it to None.
        Assert.Null(ddcBound.Ddc.RemoveBinding());
        Assert.Equal(MonitorControlTypeChoice.None, ddcBound.ControlType);
        Assert.True(ddcBound.CanChooseControlType);
        Assert.False(viewModel.CanRescanMonitorControls);
    }
}

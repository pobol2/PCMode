using PCMode.Engine;
using PCMode.Tray.Elements.Displays.Sections;
using PCMode.Tray.Tests;

namespace PCMode.Tray.Tests.Features.Displays;

/// <summary>
/// The two monitor-scoped sections that replaced the standalone DDC/CI and
/// Television pages: both address one physical display by its device path,
/// and both own the whole of what their old page could do.
/// </summary>
public sealed class MonitorSectionViewModelTests
{
    private const string DdcPath = "DEVICE-PATH-DDC";
    private const string TvPath = "DEVICE-PATH-TV";

    private const string ConfiguredDocument = """
        {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
          {"id":"desktop","name":"Desktop","resources":{}}]}],
         "providers":{
           "ddcMonitors":{"Left":"DEVICE-PATH-DDC"},
           "lgTv":{"default":"Living Room","televisions":{"Living Room":{
             "enabled":true,"host":"192.0.2.10","macAddress":"AA:BB:CC:DD:EE:FF",
             "displayMonitor":"DEVICE-PATH-TV"}}}}}
        """;

    private const string BareDocument = """
        {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
          {"id":"desktop","name":"Desktop","resources":{}}]}]}
        """;

    // ------------------------------------------------------------
    // DDC/CI
    // ------------------------------------------------------------

    [Fact]
    public void Ddc_section_finds_the_alias_bound_to_the_selected_monitors_device_path()
    {
        using var root = new TemporaryConfigurationRoot(ConfiguredDocument);
        var section = new MonitorDdcSectionViewModel(Workspaces.At(root.Path));

        section.Load(MonitorWithDevicePath("Left", DdcPath));

        Assert.True(section.HasBinding);
        Assert.Equal("Left", section.Alias);
    }

    [Fact]
    public void Ddc_section_has_no_binding_when_the_device_path_matches_nothing_configured()
    {
        using var root = new TemporaryConfigurationRoot(ConfiguredDocument);
        var section = new MonitorDdcSectionViewModel(Workspaces.At(root.Path));

        section.Load(MonitorWithDevicePath("Right", "SOME-OTHER-DEVICE"));

        Assert.True(section.HasMonitor);
        Assert.False(section.HasBinding);
        Assert.True(section.CanBind);
    }

    [Fact]
    public void Ddc_section_offers_nothing_to_bind_when_windows_reports_no_device_path()
    {
        using var root = new TemporaryConfigurationRoot(ConfiguredDocument);
        var section = new MonitorDdcSectionViewModel(Workspaces.At(root.Path));

        section.Load(null);

        Assert.False(section.HasMonitor);
        Assert.False(section.HasBinding);
        Assert.False(section.CanBind);
    }

    [Fact]
    public void Creating_a_ddc_binding_writes_it_against_the_selected_monitors_device_path()
    {
        using var root = new TemporaryConfigurationRoot(BareDocument);
        var section = new MonitorDdcSectionViewModel(Workspaces.At(root.Path));
        section.Load(MonitorWithDevicePath("Right", "SOME-OTHER-DEVICE"));

        Assert.Null(section.CreateBinding("Desk Right"));

        Assert.True(section.HasBinding);
        Assert.Equal("Desk Right", section.Alias);
        Assert.Equal("SOME-OTHER-DEVICE", DdcMonitorConfigurationStore.Read(root.Path)["Desk Right"]);
    }

    [Fact]
    public void Creating_a_ddc_binding_refuses_a_name_another_monitor_already_uses()
    {
        using var root = new TemporaryConfigurationRoot(ConfiguredDocument);
        var section = new MonitorDdcSectionViewModel(Workspaces.At(root.Path));
        section.Load(MonitorWithDevicePath("Right", "SOME-OTHER-DEVICE"));

        string? problem = section.CreateBinding("Left");

        Assert.NotNull(problem);
        Assert.Contains("already", problem, StringComparison.OrdinalIgnoreCase);
        Assert.False(section.HasBinding);
    }

    [Fact]
    public void Renaming_and_removing_a_ddc_binding_round_trip_through_the_store()
    {
        using var root = new TemporaryConfigurationRoot(ConfiguredDocument);
        var section = new MonitorDdcSectionViewModel(Workspaces.At(root.Path));
        section.Load(MonitorWithDevicePath("Left", DdcPath));

        Assert.Null(section.RenameBinding("Desk Left"));
        Assert.Equal("Desk Left", section.Alias);
        Assert.Equal(DdcPath, DdcMonitorConfigurationStore.Read(root.Path)["Desk Left"]);

        Assert.Null(section.RemoveBinding());
        Assert.False(section.HasBinding);
        Assert.Empty(DdcMonitorConfigurationStore.Read(root.Path));
    }

    // ------------------------------------------------------------
    // Television
    // ------------------------------------------------------------

    [Fact]
    public void Television_section_finds_and_loads_the_television_naming_this_display()
    {
        using var root = new TemporaryConfigurationRoot(ConfiguredDocument);
        var section = new MonitorTelevisionSectionViewModel(Workspaces.At(root.Path), () => { });

        section.Load(MonitorWithDevicePath("TV", TvPath));

        Assert.True(section.HasAssociation);
        Assert.Equal("Living Room", section.TelevisionName);
        Assert.True(section.Enabled);
        Assert.Equal("192.0.2.10", section.Host);
        Assert.Equal("AA:BB:CC:DD:EE:FF", section.MacAddress);
        Assert.True(section.IsDefaultTelevision);
    }

    [Fact]
    public void Television_section_has_no_association_when_the_device_path_matches_nothing_configured()
    {
        using var root = new TemporaryConfigurationRoot(ConfiguredDocument);
        var section = new MonitorTelevisionSectionViewModel(Workspaces.At(root.Path), () => { });

        section.Load(MonitorWithDevicePath("Left", DdcPath));

        Assert.True(section.HasMonitor);
        Assert.False(section.HasAssociation);
        Assert.Null(section.TelevisionName);
    }

    [Fact]
    public void Creating_a_television_links_it_to_this_display_and_starts_it_disabled()
    {
        using var root = new TemporaryConfigurationRoot(BareDocument);
        var section = new MonitorTelevisionSectionViewModel(Workspaces.At(root.Path), () => { });
        section.Load(MonitorWithDevicePath("TV", TvPath));

        Assert.Null(section.CreateTelevision("Living Room"));

        Assert.True(section.HasAssociation);
        Assert.Equal("Living Room", section.TelevisionName);
        Assert.False(section.Enabled);
        Assert.Equal(TvPath, LgTvConfigurationStore.Read(root.Path).SettingsFor("Living Room")!.DisplayMonitor);
    }

    [Fact]
    public void Editing_a_television_and_saving_the_section_persists_its_body()
    {
        using var root = new TemporaryConfigurationRoot(BareDocument);
        bool changed = false;
        var section = new MonitorTelevisionSectionViewModel(Workspaces.At(root.Path), () => changed = true);
        section.Load(MonitorWithDevicePath("TV", TvPath));
        Assert.Null(section.CreateTelevision("Living Room"));

        changed = false;
        section.Host = "192.0.2.31";
        section.MacAddress = "00:00:5E:00:53:31";
        section.Enabled = true;
        section.AddInput();
        section.Inputs[0].Name = "PC";
        section.Inputs[0].Input = "HDMI_4";

        Assert.True(changed);
        Assert.Null(section.ValidationProblem);
        Assert.Null(section.Save());

        LgTvSettings saved = LgTvConfigurationStore.Read(root.Path).SettingsFor("Living Room")!;
        Assert.True(saved.Enabled);
        Assert.Equal("192.0.2.31", saved.Host);
        Assert.Equal("HDMI_4", saved.InputIdFor("PC"));
        Assert.Equal(TvPath, saved.DisplayMonitor);
    }

    [Fact]
    public void An_enabled_television_with_no_address_is_refused_before_anything_is_written()
    {
        using var root = new TemporaryConfigurationRoot(BareDocument);
        var section = new MonitorTelevisionSectionViewModel(Workspaces.At(root.Path), () => { });
        section.Load(MonitorWithDevicePath("TV", TvPath));
        Assert.Null(section.CreateTelevision("Living Room"));

        section.Enabled = true;

        Assert.NotNull(section.ValidationProblem);
        Assert.Contains("address", section.ValidationProblem!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(section.ValidationProblem, section.Save());
    }

    [Fact]
    public void A_television_no_display_claims_is_offered_for_linking_and_adopts_this_one()
    {
        using var root = new TemporaryConfigurationRoot(
            """
            {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
              {"id":"desktop","name":"Desktop","resources":{}}]}],
             "providers":{"lgTv":{"default":"Spare","televisions":{"Spare":{
               "enabled":false,"host":"192.0.2.44","macAddress":"AA:BB:CC:DD:EE:01"}}}}}
            """
        );

        var section = new MonitorTelevisionSectionViewModel(Workspaces.At(root.Path), () => { });
        section.Load(MonitorWithDevicePath("TV", TvPath));

        Assert.False(section.HasAssociation);
        Assert.Contains("Spare", section.LinkableTelevisions);

        Assert.Null(section.LinkTelevision("Spare"));

        Assert.True(section.HasAssociation);
        Assert.Equal("Spare", section.TelevisionName);
        Assert.Equal("192.0.2.44", section.Host);
        Assert.Equal(TvPath, LgTvConfigurationStore.Read(root.Path).SettingsFor("Spare")!.DisplayMonitor);
        Assert.Empty(section.LinkableTelevisions);
    }

    [Fact]
    public void Unlinking_keeps_the_television_configured_and_offers_it_for_linking_again()
    {
        using var root = new TemporaryConfigurationRoot(ConfiguredDocument);
        var section = new MonitorTelevisionSectionViewModel(Workspaces.At(root.Path), () => { });
        section.Load(MonitorWithDevicePath("TV", TvPath));

        Assert.Null(section.UnlinkTelevision());

        Assert.False(section.HasAssociation);
        Assert.Contains("Living Room", section.LinkableTelevisions);

        LgTvSettings still = LgTvConfigurationStore.Read(root.Path).SettingsFor("Living Room")!;
        Assert.Null(still.DisplayMonitor);
        Assert.Equal("192.0.2.10", still.Host);
    }

    [Fact]
    public void Duplicating_leaves_the_copy_unlinked_so_two_televisions_never_claim_one_display()
    {
        using var root = new TemporaryConfigurationRoot(ConfiguredDocument);
        var section = new MonitorTelevisionSectionViewModel(Workspaces.At(root.Path), () => { });
        section.Load(MonitorWithDevicePath("TV", TvPath));

        Assert.Null(section.DuplicateTelevision("Bedroom"));

        // This display still means the original, and the copy carries the
        // settings without claiming any screen.
        Assert.Equal("Living Room", section.TelevisionName);
        Assert.Contains("Bedroom", section.LinkableTelevisions);

        LgTvCatalog catalog = LgTvConfigurationStore.Read(root.Path);
        Assert.Equal(TvPath, catalog.SettingsFor("Living Room")!.DisplayMonitor);
        Assert.Null(catalog.SettingsFor("Bedroom")!.DisplayMonitor);
        Assert.Equal("192.0.2.10", catalog.SettingsFor("Bedroom")!.Host);
    }

    [Fact]
    public void Renaming_and_deleting_a_television_round_trip_through_the_store()
    {
        using var root = new TemporaryConfigurationRoot(ConfiguredDocument);
        var section = new MonitorTelevisionSectionViewModel(Workspaces.At(root.Path), () => { });
        section.Load(MonitorWithDevicePath("TV", TvPath));

        Assert.Null(section.RenameTelevision("Lounge"));
        Assert.Equal("Lounge", section.TelevisionName);
        Assert.Equal(TvPath, LgTvConfigurationStore.Read(root.Path).SettingsFor("Lounge")!.DisplayMonitor);

        Assert.Null(section.DeleteTelevision());
        Assert.False(section.HasAssociation);
        Assert.Empty(LgTvConfigurationStore.Read(root.Path).Names);
    }

    [Fact]
    public void Toggling_enabled_commits_whatever_else_is_in_the_editor_with_it()
    {
        using var root = new TemporaryConfigurationRoot(BareDocument);
        var section = new MonitorTelevisionSectionViewModel(Workspaces.At(root.Path), () => { });
        section.Load(MonitorWithDevicePath("TV", TvPath));
        Assert.Null(section.CreateTelevision("Living Room"));

        section.Host = "192.0.2.31";
        section.MacAddress = "00:00:5E:00:53:31";

        Assert.Null(section.ToggleEnabled());

        LgTvSettings saved = LgTvConfigurationStore.Read(root.Path).SettingsFor("Living Room")!;
        Assert.True(saved.Enabled);
        Assert.Equal("192.0.2.31", saved.Host);
        Assert.True(section.Enabled);
    }

    /// <summary>
    /// Reloading is what the page's Discard reaches the television editor
    /// through, so it must overwrite the form rather than skip when the
    /// resolved television has not changed.
    /// </summary>
    [Fact]
    public void Loading_again_discards_an_unsaved_edit_but_a_quiet_tick_does_not()
    {
        using var root = new TemporaryConfigurationRoot(ConfiguredDocument);
        var section = new MonitorTelevisionSectionViewModel(Workspaces.At(root.Path), () => { });
        LayoutMonitorRow monitor = MonitorWithDevicePath("TV", TvPath);
        section.Load(monitor);

        section.Host = "10.0.0.9";

        section.ReResolve(monitor);
        Assert.Equal("10.0.0.9", section.Host);

        section.Load(monitor);
        Assert.Equal("192.0.2.10", section.Host);
    }

    [Fact]
    public void Television_power_messages_keep_missing_observations_distinct()
    {
        Assert.Equal(
            "Not paired. Use Pair.",
            MonitorTelevisionSectionViewModel.DescribePowerState(new LgTvObservation(LgTvObservationStatus.Unpaired))
        );
        Assert.StartsWith(
            "Unreachable.",
            MonitorTelevisionSectionViewModel.DescribePowerState(
                new LgTvObservation(LgTvObservationStatus.Unreachable, Problem: "No route.")
            ),
            StringComparison.Ordinal
        );
        Assert.StartsWith(
            "Power state unknown.",
            MonitorTelevisionSectionViewModel.DescribePowerState(
                new LgTvObservation(LgTvObservationStatus.Unknown, Problem: "Still changing.")
            ),
            StringComparison.Ordinal
        );
        Assert.StartsWith(
            "Could not read power state.",
            MonitorTelevisionSectionViewModel.DescribePowerState(
                new LgTvObservation(LgTvObservationStatus.Error, Problem: "Refused.")
            ),
            StringComparison.Ordinal
        );
        Assert.Equal(
            "Off.",
            MonitorTelevisionSectionViewModel.DescribePowerState(
                new LgTvObservation(LgTvObservationStatus.Observed, TvPowerProvider.Off)
            )
        );
    }

    private static LayoutMonitorRow MonitorWithDevicePath(string name, string devicePath)
    {
        var row = new LayoutMonitorRow(name, (_, _) => { });

        row.SetDisplayDetails(
            new DisplayMonitorInfo($"{name} Monitor", devicePath, true, false, 0, 0, 1920, 1080, 60.0, [], name),
            new DisplayMonitorMapping(name, devicePath, DisplayMappingState.Ready)
        );

        return row;
    }
}

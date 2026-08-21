namespace PCMode.Engine.Tests;

/// <summary>What writing a display layout to the real configuration file does and refuses to do.</summary>
public sealed class LayoutConfigurationStoreTests
{
    private const string Document = """
        {
          "schemaVersion": 1,
          "providers": { "display": { "monitors": { "Desk Left": "\\\\?\\DISPLAY#1", "Desk Right": "\\\\?\\DISPLAY#2" } } },
          "spaces": [ { "id": "space", "name": "Space", "activities": [
            { "id": "desktop", "name": "Desktop", "resources": {} }
          ] } ]
        }
        """;

    [Fact]
    public void Saves_a_well_formed_layout_that_DisplayProvider_then_reads_back()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ModeSaveOutcome outcome = LayoutConfigurationStore.Save(
            root.Path,
            "Dual",
            [
                new LayoutMonitorEdit("Desk Left", Primary: true, Direction: null, Anchor: null, RefreshHz: null),
                new LayoutMonitorEdit("Desk Right", Primary: false, Direction: "RightOf", Anchor: "Desk Left", RefreshHz: 144),
            ]);

        Assert.True(outcome.Success, outcome.Problem);

        var provider = new DisplayProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration.Parse(json, [provider]);

        Assert.Equal(["Dual"], provider.LayoutNames);
        DisplayLayout layout = Assert.Single(provider.Layouts);
        Assert.Equal("Desk Left", layout.Primary);
        Assert.Equal(144, layout.Refresh["Desk Right"]);
    }

    [Fact]
    public void Refuses_a_layout_whose_placement_never_reaches_the_primary_and_leaves_the_file_untouched()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        // "Desk Right" is included but not primary, and given no placement --
        // the same malformed shape DisplayLayout.TryParse refuses.
        ModeSaveOutcome outcome = LayoutConfigurationStore.Save(
            root.Path,
            "Broken",
            [
                new LayoutMonitorEdit("Desk Left", Primary: true, Direction: null, Anchor: null, RefreshHz: null),
                new LayoutMonitorEdit("Desk Right", Primary: false, Direction: null, Anchor: null, RefreshHz: null),
            ]);

        Assert.False(outcome.Success);

        var provider = new DisplayProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration.Parse(json, [provider]);

        Assert.Empty(provider.LayoutNames);
    }

    [Fact]
    public void Renaming_a_layout_updates_every_mode_that_names_it()
    {
        using var root = new TemporaryRoot();
        root.Write("""
            {
              "schemaVersion": 1,
              "providers": { "display": { "monitors": { "Desk Left": "\\\\?\\DISPLAY#1" }, "layouts": { "Solo": { "monitors": ["Desk Left"] } } } },
              "spaces": [ { "id": "space", "name": "Space", "activities": [
                { "id": "desktop", "name": "Desktop", "resources": { "display": "Solo" } }
              ] } ]
            }
            """);

        ModeSaveOutcome outcome = LayoutConfigurationStore.Rename(root.Path, "Solo", "Just Left");
        Assert.True(outcome.Success, outcome.Problem);

        var display = new DisplayProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration configuration = ModeConfiguration.Parse(json, [display]);

        Assert.Equal(["Just Left"], display.LayoutNames);
        Assert.Equal("Just Left", configuration.Find("Desktop")!.Resources.Single(r => r.Key == "display").Value);
    }

    [Fact]
    public void Deleting_a_layout_still_referenced_by_a_mode_is_refused()
    {
        using var root = new TemporaryRoot();
        root.Write("""
            {
              "schemaVersion": 1,
              "providers": { "display": { "monitors": { "Desk Left": "\\\\?\\DISPLAY#1" }, "layouts": { "Solo": { "monitors": ["Desk Left"] } } } },
              "spaces": [ { "id": "space", "name": "Space", "activities": [
                { "id": "desktop", "name": "Desktop", "resources": { "display": "Solo" } }
              ] } ]
            }
            """);

        ModeSaveOutcome outcome = LayoutConfigurationStore.Delete(root.Path, "Solo");

        Assert.False(outcome.Success);
        Assert.Contains("Desktop", outcome.Problem);

        var display = new DisplayProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration.Parse(json, [display]);
        Assert.Equal(["Solo"], display.LayoutNames);
    }
}

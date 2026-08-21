namespace PCMode.Engine.Tests;

/// <summary>What writing a cursor position to the real configuration file does and refuses to do.</summary>
public sealed class CursorPositionConfigurationStoreTests
{
    private const string Document = """
        {
          "schemaVersion": 1,
          "spaces": [ { "id": "space", "name": "Space", "activities": [
            { "id": "desktop", "name": "Desktop", "resources": {} }
          ] } ]
        }
        """;

    [Fact]
    public void Saves_a_position_that_CursorProvider_then_reads_back()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ModeSaveOutcome outcome = CursorPositionConfigurationStore.Save(
            root.Path, "Middle", xPercent: 50, xFromRight: null, yPercent: 50, yFromBottom: null);

        Assert.True(outcome.Success, outcome.Problem);

        var provider = new CursorProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration.Parse(json, [provider]);

        Assert.Equal(["Middle"], provider.PositionNames);
        CursorPosition position = Assert.Single(provider.Positions);
        Assert.Equal(50, position.XPercent);
        Assert.Equal(50, position.YPercent);
    }

    [Fact]
    public void Refuses_a_position_with_both_a_percentage_and_an_edge_offset_for_the_same_axis()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ModeSaveOutcome outcome = CursorPositionConfigurationStore.Save(
            root.Path, "Ambiguous", xPercent: 50, xFromRight: 10, yPercent: 50, yFromBottom: null);

        Assert.False(outcome.Success);

        var provider = new CursorProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration.Parse(json, [provider]);
        Assert.Empty(provider.PositionNames);
    }

    [Fact]
    public void Deleting_a_position_still_referenced_by_a_mode_is_refused()
    {
        using var root = new TemporaryRoot();
        root.Write("""
            {
              "schemaVersion": 1,
              "providers": { "cursor": { "positions": { "Corner": { "xFromRight": 5, "yFromBottom": 5 } } } },
              "spaces": [ { "id": "space", "name": "Space", "activities": [
                { "id": "desktop", "name": "Desktop", "resources": { "cursor": "Corner" } }
              ] } ]
            }
            """);

        ModeSaveOutcome outcome = CursorPositionConfigurationStore.Delete(root.Path, "Corner");

        Assert.False(outcome.Success);
        Assert.Contains("Desktop", outcome.Problem);

        var provider = new CursorProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration.Parse(json, [provider]);
        Assert.Equal(["Corner"], provider.PositionNames);
    }

    [Fact]
    public void Renaming_a_position_updates_every_mode_that_names_it()
    {
        using var root = new TemporaryRoot();
        root.Write("""
            {
              "schemaVersion": 1,
              "providers": { "cursor": { "positions": { "Corner": { "xFromRight": 5, "yFromBottom": 5 } } } },
              "spaces": [ { "id": "space", "name": "Space", "activities": [
                { "id": "desktop", "name": "Desktop", "resources": { "cursor": "Corner" } }
              ] } ]
            }
            """);

        ModeSaveOutcome outcome = CursorPositionConfigurationStore.Rename(root.Path, "Corner", "Bottom Right");
        Assert.True(outcome.Success, outcome.Problem);

        var provider = new CursorProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration configuration = ModeConfiguration.Parse(json, [provider]);

        Assert.Equal(["Bottom Right"], provider.PositionNames);
        Assert.Equal("Bottom Right", configuration.Find("Desktop")!.Resources.Single(r => r.Key == "cursor").Value);
    }
}

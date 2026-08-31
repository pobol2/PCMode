using System.Text.Json;

namespace PCMode.Engine.Tests;

/// <summary>What a display layout's placement graph parses to, and where it puts each monitor.</summary>
public sealed class DisplayLayoutTests
{
    private static readonly Dictionary<string, (int Width, int Height)> Sizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = (2560, 1440),
        ["B"] = (1920, 1080),
    };

    [Fact]
    public void An_entry_with_no_trailing_words_is_flush_at_the_start_edge_exactly_as_before()
    {
        DisplayLayout layout = Parse("""{"monitors":["A","B"],"placement":["B RightOf A"]}""");

        IReadOnlyDictionary<string, (int X, int Y)> positions = layout.Positions(Sizes);

        Assert.Equal((0, 0), positions["A"]);
        Assert.Equal((2560, 0), positions["B"]);
    }

    [Fact]
    public void Center_alignment_centers_the_smaller_monitor_on_the_shared_edge()
    {
        DisplayLayout layout = Parse("""{"monitors":["A","B"],"placement":["B RightOf A Center"]}""");

        IReadOnlyDictionary<string, (int X, int Y)> positions = layout.Positions(Sizes);

        // A is 1440 tall, B is 1080 tall: centering B leaves (1440-1080)/2 = 180 above it.
        Assert.Equal((2560, 180), positions["B"]);
    }

    [Fact]
    public void End_alignment_flushes_the_far_edges_together()
    {
        DisplayLayout layout = Parse("""{"monitors":["A","B"],"placement":["B RightOf A End"]}""");

        IReadOnlyDictionary<string, (int X, int Y)> positions = layout.Positions(Sizes);

        // A is 1440 tall, B is 1080 tall: flushing the bottoms leaves 1440-1080 = 360 above it.
        Assert.Equal((2560, 360), positions["B"]);
    }

    [Fact]
    public void An_explicit_offset_nudges_past_whichever_alignment_it_follows()
    {
        DisplayLayout layout = Parse("""{"monitors":["A","B"],"placement":["B RightOf A Center +40"]}""");

        IReadOnlyDictionary<string, (int X, int Y)> positions = layout.Positions(Sizes);

        Assert.Equal((2560, 220), positions["B"]);
    }

    [Fact]
    public void An_offset_with_no_alignment_word_is_relative_to_the_start_edge()
    {
        DisplayLayout layout = Parse("""{"monitors":["A","B"],"placement":["B RightOf A -25"]}""");

        IReadOnlyDictionary<string, (int X, int Y)> positions = layout.Positions(Sizes);

        Assert.Equal((2560, -25), positions["B"]);
    }

    [Fact]
    public void Above_and_below_align_on_the_horizontal_axis_instead_of_the_vertical_one()
    {
        // LeftOf/RightOf align vertically; Above/Below align horizontally --
        // checked once here so both branches of DisplayLayout.Positions' switch
        // over direction are covered, not just the vertical one every other
        // test in this file uses.
        DisplayLayout layout = Parse("""{"monitors":["A","B"],"placement":["B Below A End -100"]}""");

        IReadOnlyDictionary<string, (int X, int Y)> positions = layout.Positions(Sizes);

        // End-aligned horizontally: A is 2560 wide, B is 1920 wide -> 640, then -100.
        Assert.Equal((540, 1440), positions["B"]);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("Center")]
    [InlineData("END")]
    public void Alignment_words_are_matched_case_insensitively(string word)
    {
        DisplayLayout layout = Parse($$"""{"monitors":["A","B"],"placement":["B RightOf A {{word}}"]}""");

        Assert.Single(layout.Placements);
    }

    [Fact]
    public void Round_trips_through_the_configuration_store_unchanged_when_left_at_the_default()
    {
        using var root = new TemporaryRoot();
        root.Write("""
            {
              "schemaVersion": 1,
              "providers": { "display": { "monitors": { "A": "\\\\?\\DISPLAY#1", "B": "\\\\?\\DISPLAY#2" } } },
              "spaces": [ { "id": "space", "name": "Space", "activities": [
                { "id": "desktop", "name": "Desktop", "resources": {} }
              ] } ]
            }
            """);

        ModeSaveOutcome outcome = LayoutConfigurationStore.Save(
            root.Path,
            "Dual",
            [
                new LayoutMonitorEdit("A", Primary: true, Direction: null, Anchor: null, RefreshHz: null),
                new LayoutMonitorEdit("B", Primary: false, Direction: "RightOf", Anchor: "A", RefreshHz: null),
            ]);

        Assert.True(outcome.Success, outcome.Problem);

        var provider = new DisplayProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration.Parse(json, [provider]);

        DisplayLayout layout = Assert.Single(provider.Layouts);
        DisplayPlacement placement = layout.Placements["B"];
        Assert.Equal(DisplayAlignment.Start, placement.Alignment);
        Assert.Equal(0, placement.Offset);
    }

    [Fact]
    public void Round_trips_a_centered_offset_placement_through_the_configuration_store()
    {
        using var root = new TemporaryRoot();
        root.Write("""
            {
              "schemaVersion": 1,
              "providers": { "display": { "monitors": { "A": "\\\\?\\DISPLAY#1", "B": "\\\\?\\DISPLAY#2" } } },
              "spaces": [ { "id": "space", "name": "Space", "activities": [
                { "id": "desktop", "name": "Desktop", "resources": {} }
              ] } ]
            }
            """);

        ModeSaveOutcome outcome = LayoutConfigurationStore.Save(
            root.Path,
            "Dual",
            [
                new LayoutMonitorEdit("A", Primary: true, Direction: null, Anchor: null, RefreshHz: null),
                new LayoutMonitorEdit(
                    "B", Primary: false, Direction: "RightOf", Anchor: "A", RefreshHz: null,
                    Alignment: "Center", Offset: -15),
            ]);

        Assert.True(outcome.Success, outcome.Problem);

        var provider = new DisplayProvider();
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        ModeConfiguration.Parse(json, [provider]);

        DisplayLayout layout = Assert.Single(provider.Layouts);
        DisplayPlacement placement = layout.Placements["B"];
        Assert.Equal(DisplayAlignment.Center, placement.Alignment);
        Assert.Equal(-15, placement.Offset);
    }

    private static DisplayLayout Parse(string document)
    {
        using JsonDocument json = JsonDocument.Parse(document);

        Assert.True(DisplayLayout.TryParse("L", json.RootElement, out DisplayLayout? layout, out string? problem));
        Assert.NotNull(layout);
        return layout!;
    }
}

using PCMode.Tray.Elements.Configuration;

namespace PCMode.Tray.Tests.Presentation.Configuration;

/// <summary>
/// The "keep the selected key, rebuild the list, restore-or-fall-back"
/// skeleton <see cref="ConfigurationEntityList{TKey, TEntity}"/> replaces
/// across the flat-selector pages (Displays' layouts, Audio's targets,
/// RGB's scenes) each repeated by hand, tested once against a minimal
/// entity rather than once per feature.
/// </summary>
public sealed class ConfigurationEntityListTests
{
    private sealed record Sample(string Key, string Title);

    private static ConfigurationEntityList<string, Sample> BuildList() =>
        new(keyOf: sample => sample.Key, titleOf: sample => sample.Title);

    [Fact]
    public void Load_selects_the_first_entity_when_nothing_was_previously_selected()
    {
        var list = BuildList();

        list.Load([new Sample("a", "Alpha"), new Sample("b", "Beta")], keep: null);

        Assert.Equal("a", list.SelectedKey);
        Assert.True(list.HasSelection);
        Assert.Equal("Alpha", list.Selected?.Title);
        Assert.Equal(2, list.SelectorItems.Count);
    }

    [Fact]
    public void Load_restores_the_kept_key_when_it_still_exists()
    {
        var list = BuildList();
        list.Load([new Sample("a", "Alpha"), new Sample("b", "Beta")], keep: null);

        list.Load([new Sample("a", "Alpha"), new Sample("b", "Beta")], keep: "b");

        Assert.Equal("b", list.SelectedKey);
        Assert.Equal("Beta", list.Selected?.Title);
    }

    [Fact]
    public void Load_falls_back_to_the_first_entity_when_the_kept_key_is_gone()
    {
        var list = BuildList();

        list.Load([new Sample("a", "Alpha"), new Sample("b", "Beta")], keep: "deleted");

        Assert.Equal("a", list.SelectedKey);
    }

    [Fact]
    public void Load_with_no_entities_selects_nothing()
    {
        var list = BuildList();
        list.Load([new Sample("a", "Alpha")], keep: null);

        list.Load([], keep: "a");

        Assert.Null(list.SelectedKey);
        Assert.False(list.HasSelection);
        Assert.Null(list.Selected);
        Assert.Empty(list.SelectorItems);
    }

    [Fact]
    public void Select_changes_the_selected_key_without_touching_the_items()
    {
        var list = BuildList();
        list.Load([new Sample("a", "Alpha"), new Sample("b", "Beta")], keep: null);

        list.Select("b");

        Assert.Equal("b", list.SelectedKey);
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void SelectorItems_project_the_title_and_summary_for_the_selector_template()
    {
        var list = new ConfigurationEntityList<string, Sample>(
            keyOf: sample => sample.Key,
            titleOf: sample => sample.Title,
            summaryOf: sample => $"summary:{sample.Title}");

        list.Load([new Sample("a", "Alpha")], keep: null);

        ConfigurationEntityListItem<string> item = Assert.Single(list.SelectorItems);
        Assert.Equal("a", item.Key);
        Assert.Equal("Alpha", item.Title);
        Assert.Equal("summary:Alpha", item.Summary);
        Assert.Equal("a", ((IConfigurationSelectorItem)item).Key);
    }

    [Fact]
    public void Clear_empties_the_list_and_the_selection()
    {
        var list = BuildList();
        list.Load([new Sample("a", "Alpha")], keep: null);

        list.Clear();

        Assert.Empty(list.Items);
        Assert.Empty(list.SelectorItems);
        Assert.Null(list.SelectedKey);
    }
}

using PCMode.Tray.Elements.Configuration;

namespace PCMode.Tray.Tests.Presentation.Configuration;

/// <summary>
/// The "pick something, edit a working copy, persist only on Save" lifecycle
/// <see cref="ConfigurationEditingSession{TKey, TEntity}"/> gives a flat-list
/// configuration page whose Create/Rename/Duplicate must not touch disk until
/// Save (Audio's targets, first) -- tested once against a minimal entity
/// rather than once per feature.
/// </summary>
public sealed class ConfigurationEditingSessionTests
{
    private sealed record Sample(string Key, string Title);

    private static ConfigurationEditingSession<string, Sample> BuildSession() =>
        new(keyOf: sample => sample.Key, titleOf: sample => sample.Title);

    [Fact]
    public void BeginCreate_selects_an_unsaved_draft_with_no_persisted_backing()
    {
        var session = BuildSession();
        session.Load([new Sample("a", "Alpha")], keep: null);

        session.BeginCreate("new", "New one");

        Assert.Equal("new", session.SelectedKey);
        Assert.True(session.IsDraft);
        Assert.True(session.HasOverlay);
        Assert.Null(session.Selected);
        Assert.Contains(session.SelectorItems, item => item.Key == "new" && item.Title == "New one");
    }

    [Fact]
    public void Load_never_discards_an_active_draft_even_when_keep_names_something_else()
    {
        var session = BuildSession();
        session.Load([new Sample("a", "Alpha")], keep: null);
        session.BeginCreate("new", "New one");

        session.Load([new Sample("a", "Alpha"), new Sample("b", "Beta")], keep: "a");

        Assert.Equal("new", session.SelectedKey);
        Assert.True(session.IsDraft);
        Assert.Equal(2, session.Items.Count);
    }

    [Fact]
    public void RenameCurrent_on_a_persisted_entity_retitles_the_selector_without_touching_items()
    {
        var session = BuildSession();
        session.Load([new Sample("a", "Alpha")], keep: null);

        session.RenameCurrent("renamed", "Renamed");

        Assert.Equal("renamed", session.SelectedKey);
        Assert.False(session.IsDraft);
        Assert.True(session.HasOverlay);

        // Selected still resolves the original persisted entity by its origin
        // key -- nothing was written, so "a" is still what is on disk.
        Assert.Equal("Alpha", session.Selected?.Title);
        Assert.Single(session.Items);
        Assert.Equal("a", session.Items[0].Key);

        Assert.Contains(session.SelectorItems, item => item.Key == "renamed" && item.Title == "Renamed");
        Assert.DoesNotContain(session.SelectorItems, item => item.Key == "a");
    }

    [Fact]
    public void Commit_ends_overlay_mode_so_a_reload_can_pick_up_the_newly_persisted_entity()
    {
        var session = BuildSession();
        session.Load([new Sample("a", "Alpha")], keep: null);
        session.RenameCurrent("renamed", "Renamed");

        session.Commit();

        Assert.False(session.HasOverlay);

        // The page writes to disk and reloads at this point; simulate that reload.
        session.Load([new Sample("renamed", "Renamed")], keep: "renamed");

        Assert.Equal("renamed", session.SelectedKey);
        Assert.False(session.HasOverlay);
        Assert.Equal("Renamed", session.Selected?.Title);
    }

    [Fact]
    public void Discard_on_a_rename_falls_back_to_the_entity_it_was_renaming()
    {
        var session = BuildSession();
        session.Load([new Sample("a", "Alpha")], keep: null);
        session.RenameCurrent("renamed", "Renamed");

        session.Discard();

        Assert.Equal("a", session.SelectedKey);
        Assert.False(session.HasOverlay);
        Assert.Equal("Alpha", session.Selected?.Title);
        Assert.Contains(session.SelectorItems, item => item.Key == "a" && item.Title == "Alpha");
    }

    [Fact]
    public void Discard_on_a_fresh_draft_falls_back_to_the_first_remaining_entity()
    {
        var session = BuildSession();
        session.Load([new Sample("a", "Alpha")], keep: null);
        session.BeginCreate("new", "New one");

        session.Discard();

        Assert.Equal("a", session.SelectedKey);
        Assert.False(session.HasOverlay);
        Assert.False(session.IsDraft);
        Assert.DoesNotContain(session.SelectorItems, item => item.Key == "new");
    }

    [Fact]
    public void Select_drops_a_stale_overlay_that_belonged_to_a_different_key()
    {
        var session = BuildSession();
        session.Load([new Sample("a", "Alpha"), new Sample("b", "Beta")], keep: null);
        session.BeginCreate("new", "New one");

        session.Select("b");

        Assert.Equal("b", session.SelectedKey);
        Assert.False(session.HasOverlay);
        Assert.Equal("Beta", session.Selected?.Title);
    }

    [Fact]
    public void Clear_empties_everything_including_an_active_overlay()
    {
        var session = BuildSession();
        session.Load([new Sample("a", "Alpha")], keep: null);
        session.BeginCreate("new", "New one");

        session.Clear();

        Assert.Empty(session.Items);
        Assert.Empty(session.SelectorItems);
        Assert.Null(session.SelectedKey);
        Assert.False(session.HasOverlay);
    }
}

using System.Text.Json;

namespace PCMode.Engine.Tests;

/// <summary>
/// Creating, editing, ordering and deleting the hierarchy, through the one
/// store that writes it.
/// </summary>
/// <remarks>
/// Every operation goes through the real production validation before anything
/// is written, so these are also the tests that a refused change leaves the file
/// exactly as it was -- which is the property that makes an editor safe to use
/// on a configuration somebody depends on.
/// </remarks>
public sealed class HierarchyStoreTests
{
    private const string Document = """
        {
          "schemaVersion": 1,
          "application": {
            "picker": { "favorites": [
              { "direction": "Up", "target": "desk/work" },
              { "direction": "Right", "target": "desk/gaming" },
              { "direction": "Down", "target": "desk/gaming/steam" },
              { "direction": "Left", "target": "desk/work" } ] },
            "shortcuts": [
              { "target": "desk/gaming/steam", "key": "S" },
              { "target": "desk/work", "key": "W" }
            ],
            "startup": { "behavior": "switchToMode", "target": "desk/gaming/steam" }
          },
          "spaces": [
            { "id": "desk", "name": "Desk", "resources": { "theme": "Light" }, "activities": [
              { "id": "work", "name": "Work" },
              { "id": "gaming", "name": "Gaming", "resources": { "theme": "Dark" }, "sources": [
                { "id": "steam", "name": "Steam" } ] } ] }
          ]
        }
        """;

    private static ModeConfiguration Read(TemporaryRoot root)
    {
        (string json, string path) = ModeConfiguration.ReadOrCreate(root.Path);
        return ModeConfiguration.Parse(json, EngineBootstrap.ProductionProviders(root.Path, json), path);
    }

    private static TemporaryRoot Root()
    {
        var root = new TemporaryRoot();
        root.Write(Document);
        return root;
    }

    // ---- creating -----------------------------------------------------------

    [Fact]
    public void Creates_spaces_activities_and_sources_with_ids_derived_from_their_names()
    {
        using TemporaryRoot root = Root();

        Assert.True(HierarchyStore.CreateSpace(root.Path, "Sim Rig").Success);
        Assert.True(HierarchyStore.CreateActivity(root.Path, "sim-rig", "Racing").Success);
        Assert.True(
            HierarchyStore
                .CreateSource(root.Path, "sim-rig", "racing", "Assetto Corsa", SourceKind.PcApplication)
                .Success
        );

        Assert.Contains("sim-rig/racing/assetto-corsa", Read(root).Targets.Keys);
    }

    [Fact]
    public void Refuses_a_second_activity_of_the_same_name_in_the_same_space()
    {
        using TemporaryRoot root = Root();

        ModeSaveOutcome outcome = HierarchyStore.CreateActivity(root.Path, "desk", "gaming");

        Assert.False(outcome.Success);
        Assert.Contains("already an activity", outcome.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_same_activity_name_in_two_spaces_is_fine_and_stays_distinguishable()
    {
        using TemporaryRoot root = Root();

        Assert.True(HierarchyStore.CreateSpace(root.Path, "TV").Success);
        Assert.True(HierarchyStore.CreateActivity(root.Path, "tv", "Gaming").Success);

        ModeConfiguration configuration = Read(root);

        Assert.Contains("tv/gaming", configuration.Targets.Keys);
        Assert.Contains("desk/gaming", configuration.Targets.Keys);
        Assert.Null(configuration.Find("Gaming"));
        Assert.NotNull(configuration.Find("TV — Gaming"));
    }

    [Fact]
    public void Duplicating_an_activity_copies_its_resources_and_its_sources_under_new_ids()
    {
        using TemporaryRoot root = Root();

        Assert.True(HierarchyStore.CreateActivity(root.Path, "desk", "Gaming copy", duplicateOf: "gaming").Success);

        ModeConfiguration configuration = Read(root);
        ConfiguredActivity copy = configuration
            .Spaces.Single()
            .Activities.Single(activity => activity.Id == "gaming-copy");

        Assert.Equal("Dark", copy.Resources.Single().Value!);
        Assert.Equal("Steam", copy.Sources.Single().Name);
        Assert.Contains("desk/gaming-copy/steam", configuration.Targets.Keys);
        Assert.Contains("desk/gaming/steam", configuration.Targets.Keys);
    }

    [Fact]
    public void Duplicates_spaces_activities_and_sources_with_their_owned_configuration()
    {
        using TemporaryRoot root = Root();
        var source = new NodeAddress("desk", "gaming", "steam");

        Assert.True(HierarchyStore.SetSourceKind(root.Path, source, SourceKind.ExternalDevice).Success);
        Assert.True(
            HierarchyStore
                .SaveResources(
                    root.Path,
                    source,
                    new Dictionary<string, ResourceEdit> { ["theme"] = ResourceEdit.Cleared }
                )
                .Success
        );

        Assert.True(HierarchyStore.Duplicate(root.Path, new NodeAddress("desk"), "Desk copy").Success);
        Assert.True(HierarchyStore.Duplicate(root.Path, new NodeAddress("desk", "gaming"), "Gaming copy").Success);
        Assert.True(HierarchyStore.Duplicate(root.Path, source, "Steam copy").Success);

        ModeConfiguration configuration = Read(root);
        ConfiguredSpace spaceCopy = configuration.Spaces.Single(space => space.Id == "desk-copy");
        ConfiguredActivity activityCopy = configuration
            .Spaces.Single(space => space.Id == "desk")
            .Activities.Single(activity => activity.Id == "gaming-copy");
        ConfiguredSource sourceCopy = configuration
            .Spaces.Single(space => space.Id == "desk")
            .Activities.Single(activity => activity.Id == "gaming")
            .Sources.Single(candidate => candidate.Id == "steam-copy");

        Assert.Equal("Light", spaceCopy.Resources.Single(resource => resource.Key == "theme").Value);
        Assert.Equal(["Work", "Gaming"], spaceCopy.Activities.Select(activity => activity.Name));
        Assert.Equal(
            SourceKind.ExternalDevice,
            spaceCopy.Activities.Single(activity => activity.Name == "Gaming").Sources.Single().Kind
        );

        Assert.Equal("Dark", activityCopy.Resources.Single(resource => resource.Key == "theme").Value);
        Assert.Equal(SourceKind.ExternalDevice, activityCopy.Sources.Single().Kind);
        Assert.True(activityCopy.Sources.Single().Resources.Single(resource => resource.Key == "theme").Clears);

        Assert.Equal(SourceKind.ExternalDevice, sourceCopy.Kind);
        Assert.True(sourceCopy.Resources.Single(resource => resource.Key == "theme").Clears);
        Assert.Contains("desk-copy/gaming/steam", configuration.Targets.Keys);
        Assert.Contains("desk/gaming-copy/steam", configuration.Targets.Keys);
        Assert.Contains("desk/gaming/steam-copy", configuration.Targets.Keys);
    }

    // ---- renaming -----------------------------------------------------------

    /// <summary>
    /// The point of identities: a rename is a change of name and of nothing
    /// else, so nothing that points at the thing has to be found and updated.
    /// </summary>
    [Fact]
    public void A_rename_changes_the_name_and_leaves_every_reference_to_it_alone()
    {
        using TemporaryRoot root = Root();

        Assert.True(
            HierarchyStore.Rename(root.Path, new NodeAddress("desk", "gaming", "steam"), "Big Picture").Success
        );
        Assert.True(HierarchyStore.Rename(root.Path, new NodeAddress("desk"), "Study").Success);

        ModeConfiguration configuration = Read(root);

        Assert.Equal("Study — Gaming — Big Picture", configuration.Targets.Resolve("desk/gaming/steam")!.DisplayPath);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(root.ConfigurationFile));
        JsonElement application = document.RootElement.GetProperty("application");

        Assert.Equal("desk/gaming/steam", application.GetProperty("startup").GetProperty("target").GetString());

        Assert.Contains(
            application.GetProperty("picker").GetProperty("favorites").EnumerateArray(),
            favorite => favorite.GetProperty("target").GetString() == "desk/gaming/steam"
        );
    }

    [Fact]
    public void Refuses_a_rename_that_would_make_two_siblings_indistinguishable()
    {
        using TemporaryRoot root = Root();
        string before = File.ReadAllText(root.ConfigurationFile);

        ModeSaveOutcome outcome = HierarchyStore.Rename(root.Path, new NodeAddress("desk", "work"), "Gaming");

        Assert.False(outcome.Success);
        Assert.Equal(before, File.ReadAllText(root.ConfigurationFile));
    }

    // ---- enabled state -----------------------------------------------------

    [Fact]
    public void Disabling_a_target_preserves_it_its_resources_and_its_references()
    {
        using TemporaryRoot root = Root();
        var address = new NodeAddress("desk", "gaming");

        Assert.True(HierarchyStore.SetEnabled(root.Path, address, enabled: false).Success);

        ModeConfiguration disabled = Read(root);
        ConfiguredTarget target = disabled.Targets.Resolve("desk/gaming")!;

        Assert.False(target.Enabled);
        Assert.Equal("Dark", disabled.Find(target.Key)!.Resources.Single().Value);

        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(root.ConfigurationFile)))
        {
            JsonElement application = document.RootElement.GetProperty("application");
            Assert.Contains(
                application.GetProperty("picker").GetProperty("favorites").EnumerateArray(),
                favorite => favorite.GetProperty("target").GetString() == target.Key
            );
        }

        Assert.True(HierarchyStore.SetEnabled(root.Path, address, enabled: true).Success);
        Assert.True(Read(root).Targets.Resolve(target.Key)!.Enabled);
    }

    [Fact]
    public void A_space_cannot_be_enabled_or_disabled_because_it_is_not_a_target()
    {
        using TemporaryRoot root = Root();
        string before = File.ReadAllText(root.ConfigurationFile);

        ModeSaveOutcome outcome = HierarchyStore.SetEnabled(root.Path, new NodeAddress("desk"), enabled: false);

        Assert.False(outcome.Success);
        Assert.Equal(before, File.ReadAllText(root.ConfigurationFile));
    }

    // ---- deleting -----------------------------------------------------------

    [Fact]
    public void A_delete_that_would_orphan_a_favorite_is_refused_until_a_replacement_is_chosen()
    {
        using TemporaryRoot root = Root();
        string before = File.ReadAllText(root.ConfigurationFile);

        ModeSaveOutcome refused = HierarchyStore.Delete(root.Path, new NodeAddress("desk", "gaming", "steam"));

        Assert.False(refused.Success);
        Assert.Contains("Choose a replacement", refused.Problem!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(root.ConfigurationFile));

        ModeSaveOutcome accepted = HierarchyStore.Delete(
            root.Path,
            new NodeAddress("desk", "gaming", "steam"),
            new Dictionary<string, string> { ["Down"] = "desk/work" },
            startupReplacement: "desk/work"
        );

        Assert.True(accepted.Success, accepted.Problem);
        Assert.DoesNotContain("desk/gaming/steam", Read(root).Targets.Keys);

        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(root.ConfigurationFile));
        Assert.DoesNotContain(
            saved.RootElement.GetProperty("application").GetProperty("shortcuts").EnumerateArray(),
            shortcut => shortcut.GetProperty("target").GetString() == "desk/gaming/steam"
        );
    }

    [Fact]
    public void Deleting_a_space_takes_every_target_under_it_and_says_so_first()
    {
        using TemporaryRoot root = Root();

        ModeSaveOutcome outcome = HierarchyStore.Delete(
            root.Path,
            new NodeAddress("desk"),
            new Dictionary<string, string>
            {
                ["Up"] = "desk/work",
                ["Right"] = "desk/work",
                ["Down"] = "desk/work",
                ["Left"] = "desk/work",
            },
            startupReplacement: null
        );

        // Every replacement offered is itself about to be deleted, so the
        // candidate cannot be validated and nothing is written.
        Assert.False(outcome.Success);
        Assert.Equal(3, Read(root).Targets.Targets.Count);
    }

    [Fact]
    public void Deleting_the_recorded_target_forgets_it_rather_than_leaving_a_stale_name()
    {
        using TemporaryRoot root = Root();
        var state = new ModeStateStore(root.StateFile);
        Assert.True(state.TryWrite("desk/gaming/steam", DateTimeOffset.UnixEpoch, out _));

        Assert.True(
            HierarchyStore
                .Delete(
                    root.Path,
                    new NodeAddress("desk", "gaming", "steam"),
                    new Dictionary<string, string> { ["Down"] = "desk/work" },
                    startupReplacement: "desk/work"
                )
                .Success
        );

        Assert.Null(state.Read(out _));
    }

    // ---- ordering -----------------------------------------------------------

    [Fact]
    public void Moves_a_node_among_its_siblings_and_refuses_to_move_it_past_them()
    {
        using TemporaryRoot root = Root();

        Assert.True(HierarchyStore.Move(root.Path, new NodeAddress("desk", "gaming"), up: true).Success);
        Assert.Equal(["desk/gaming", "desk/gaming/steam", "desk/work"], Read(root).Targets.Keys);

        ModeSaveOutcome refused = HierarchyStore.Move(root.Path, new NodeAddress("desk", "gaming"), up: true);

        Assert.False(refused.Success);
        Assert.Contains("already first", refused.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- resources ----------------------------------------------------------

    [Fact]
    public void Saves_a_value_a_clear_and_an_inherit_as_three_different_statements()
    {
        using TemporaryRoot root = Root();

        Assert.True(
            HierarchyStore
                .SaveResources(
                    root.Path,
                    new NodeAddress("desk", "gaming", "steam"),
                    new Dictionary<string, ResourceEdit>
                    {
                        ["doNotDisturb"] = ResourceEdit.Of("On", optional: true),
                        ["theme"] = ResourceEdit.Cleared,
                    }
                )
                .Success
        );

        ModeConfiguration configuration = Read(root);
        ConfiguredMode steam = configuration.Find("desk/gaming/steam")!;

        Assert.Equal("On", steam.Resources.Single(resource => resource.Key == "doNotDisturb").Value);
        Assert.True(steam.Resources.Single(resource => resource.Key == "doNotDisturb").Optional);
        Assert.DoesNotContain(steam.Resources, resource => resource.Key == "theme");

        // The Activity above it is untouched, which is what makes clearing a
        // decision about one target rather than about the branch.
        Assert.Equal("Dark", configuration.Find("desk/gaming")!.Resources.Single().Value);

        Assert.True(
            HierarchyStore
                .SaveResources(
                    root.Path,
                    new NodeAddress("desk", "gaming", "steam"),
                    new Dictionary<string, ResourceEdit> { ["theme"] = ResourceEdit.Inherited }
                )
                .Success
        );

        Assert.Equal(
            "Dark",
            Read(root).Find("desk/gaming/steam")!.Resources.Single(resource => resource.Key == "theme").Value
        );
    }

    [Fact]
    public void Refuses_an_unusable_value_without_changing_the_file()
    {
        using TemporaryRoot root = Root();
        string before = File.ReadAllText(root.ConfigurationFile);

        ModeSaveOutcome outcome = HierarchyStore.SaveResources(
            root.Path,
            new NodeAddress("desk"),
            new Dictionary<string, ResourceEdit> { ["caseRgb"] = ResourceEdit.Of("NOPE00") }
        );

        Assert.False(outcome.Success);
        Assert.Equal(before, File.ReadAllText(root.ConfigurationFile));
    }

    [Fact]
    public void Refuses_an_edit_addressed_to_something_that_does_not_exist()
    {
        using TemporaryRoot root = Root();

        Assert.False(HierarchyStore.Rename(root.Path, new NodeAddress("lounge"), "Lounge").Success);
        Assert.False(HierarchyStore.Rename(root.Path, new NodeAddress("desk", "missing"), "Other").Success);
        Assert.False(HierarchyStore.Rename(root.Path, new NodeAddress("desk", "gaming", "missing"), "Other").Success);
    }

    /// <summary>
    /// A configuration that describes no 'spaces' array is not edited here --
    /// it says so rather than writing a hierarchy into a document that has none.
    /// </summary>
    [Fact]
    public void Refuses_to_edit_a_configuration_that_has_no_spaces()
    {
        using var root = new TemporaryRoot();
        root.Write("""{"schemaVersion":1,"modes":[{"name":"Desktop","resources":{"theme":"Light"}}]}""");

        ModeSaveOutcome outcome = HierarchyStore.CreateSpace(root.Path, "TV");

        Assert.False(outcome.Success);
        Assert.Contains("does not describe spaces", outcome.Problem!, StringComparison.OrdinalIgnoreCase);
    }
}

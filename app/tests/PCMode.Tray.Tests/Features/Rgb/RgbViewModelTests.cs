using PCMode.Engine;
using PCMode.Tray.Elements.Rgb;
using PCMode.Tray.Tests;

namespace PCMode.Tray.Tests.Features.Rgb;

/// <summary>
/// <see cref="RgbViewModel.CanSave"/> -- regression coverage for a bug found
/// during the Step 6.2 consolidation pass: <c>CanSave</c> omitted
/// <c>HasUnsavedChanges</c>, so the Save button was clickable, and a click
/// performed a needless write, the instant a scene was merely selected.
/// </summary>
public sealed class RgbViewModelTests
{
    private const string BaseConfiguration = """
        {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
          {"id":"desktop","name":"Desktop","resources":{}}]}]}
        """;

    [Fact]
    public void CanSave_is_false_until_the_selected_scene_is_actually_edited()
    {
        using var root = new TemporaryConfigurationRoot(BaseConfiguration);

        SaveScene(root.Path);

        var viewModel = new RgbViewModel(Workspaces.At(root.Path));
        viewModel.Refresh();

        Assert.True(viewModel.HasSelectedScene);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.False(viewModel.CanSave);

        viewModel.SelectedScene!.Devices[0].Color = "445566";

        Assert.True(viewModel.HasUnsavedChanges);
        Assert.True(viewModel.CanSave);
    }

    [Fact]
    public void Rename_and_duplicate_use_the_shared_unsaved_draft_lifecycle()
    {
        using var root = new TemporaryConfigurationRoot(BaseConfiguration);
        SaveScene(root.Path);

        var viewModel = new RgbViewModel(Workspaces.At(root.Path));
        viewModel.Refresh();

        Assert.Null(viewModel.RenameSelected("Lounge"));
        Assert.Equal("Lounge", viewModel.SelectedScene!.Name);
        Assert.Equal("Not yet saved", Assert.Single(viewModel.SelectorItems).Summary);
        Assert.Contains("Party", ReadScenes(root.Path).Keys);

        viewModel.DiscardChanges();
        Assert.Equal("Party", viewModel.SelectedScene!.Name);

        Assert.Null(viewModel.DuplicateSelected("Party copy"));
        Assert.Equal(2, viewModel.SelectorItems.Count);
        Assert.NotNull(viewModel.ValidationProblem);
        Assert.False(viewModel.CanSave);

        viewModel.SelectedScene!.Devices[0].Color = "445566";

        Assert.True(viewModel.CanSave);
        Assert.True(viewModel.TrySaveChanges());
        Assert.Equal(["Party", "Party copy"], ReadScenes(root.Path).Keys.Order());
    }

    [Fact]
    public void Atomic_save_replaces_the_edited_scene_and_preserves_the_rest_of_the_document()
    {
        using var root = new TemporaryConfigurationRoot(BaseConfiguration);

        ModeSaveOutcome outcome = CaseRgbConfigurationStore.Save(
            root.Path,
            new CaseRgbEdit(true, string.Empty, [new CaseRgbDeviceEdit(0, "Direct")], 4000)
            {
                Scenes =
                [
                    new CaseRgbSceneEdit("Calm", [new CaseRgbSceneDeviceEdit(0, "Static", "001122")]),
                    new CaseRgbSceneEdit("Party", [new CaseRgbSceneDeviceEdit(0, "Static", "112233")]),
                ],
            }
        );
        Assert.True(outcome.Success, outcome.Problem);

        var viewModel = new RgbViewModel(Workspaces.At(root.Path));
        viewModel.Refresh();
        viewModel.SelectKey("Party");

        Assert.Null(viewModel.RenameSelected("Lounge"));
        viewModel.SelectedScene!.Devices[0].Color = "AABBCC";
        Assert.True(viewModel.TrySaveChanges());

        IReadOnlyDictionary<string, CaseRgbScene> scenes = ReadScenes(root.Path);
        Assert.Equal(["Calm", "Lounge"], scenes.Keys.Order());
        Assert.Equal("001122", scenes["Calm"].Devices[0].Color);
        Assert.Equal("AABBCC", scenes["Lounge"].Devices[0].Color);
    }

    [Fact]
    public void Delete_uses_the_standard_immediate_entity_action_and_leaves_the_empty_state_selected()
    {
        using var root = new TemporaryConfigurationRoot(BaseConfiguration);
        SaveScene(root.Path);

        var viewModel = new RgbViewModel(Workspaces.At(root.Path));
        viewModel.Refresh();

        Assert.Null(viewModel.DeleteSelected());

        Assert.False(viewModel.HasSelectedScene);
        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Empty(viewModel.SelectorItems);
        Assert.Empty(ReadScenes(root.Path));
    }

    private static void SaveScene(string root)
    {
        ModeSaveOutcome outcome = CaseRgbConfigurationStore.Save(
            root,
            new CaseRgbEdit(true, string.Empty, [new CaseRgbDeviceEdit(0, "Direct")], 4000)
            {
                Scenes = [new CaseRgbSceneEdit("Party", [new CaseRgbSceneDeviceEdit(0, "Static", "112233")])],
            }
        );

        Assert.True(outcome.Success, outcome.Problem);
    }

    private static IReadOnlyDictionary<string, CaseRgbScene> ReadScenes(string root)
    {
        (string json, _) = ModeConfiguration.ReadOrCreate(root);
        IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(root, json);
        ModeConfiguration.Parse(json, providers);
        return providers.OfType<CaseRgbProvider>().Single().Scenes;
    }
}

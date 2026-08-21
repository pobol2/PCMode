using PCMode.Engine;
using PCMode.Tray.Features.Rgb;

namespace PCMode.Tray.Tests;

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

    private static void SaveScene(string root)
    {
        ModeSaveOutcome outcome = CaseRgbConfigurationStore.Save(
            root,
            new CaseRgbEdit(true, string.Empty, [new CaseRgbDeviceEdit(0, "Direct")], 4000)
            {
                Scenes = [new CaseRgbSceneEdit("Party", [new CaseRgbSceneDeviceEdit(0, "Static", "112233")])],
            });

        Assert.True(outcome.Success, outcome.Problem);
    }
}

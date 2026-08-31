using PCMode.Engine;
using PCMode.Tray.Elements.Applications;
using System.IO;
using System.Text.Json.Nodes;
using PCMode.Tray.Tests;

namespace PCMode.Tray.Tests.Features.Applications;

public sealed class ApplicationsViewModelTests
{
    private const string Configuration = """
        {
          "schemaVersion": 1,
          "providers": {
            "applications": {
              "Alpha": {
                "process": "pcmode-alpha-process",
                "start": "C:\\Tools\\Alpha.exe"
              },
              "Steam": {
                "process": "pcmode-test-process",
                "start": "C:\\Tools\\Steam.exe",
                "stop": "steam://exit",
                "reactivate": true,
                "requires": { "theme": "Dark" },
                "restartWhen": [ "theme" ],
                "ready": { "activate": true, "keys": "f", "within": 8000 },
                "startWithin": 3000,
                "stopWithin": 5000
              }
            }
          },
          "spaces": [ { "id": "desk", "name": "Desk", "activities": [
            { "id": "gaming", "name": "Gaming", "resources": { "Steam": "Running" } }
          ] } ]
        }
        """;

    [Fact]
    public void Create_is_an_unsaved_draft_until_the_standard_Save()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);
        var viewModel = new ApplicationsViewModel(Workspaces.At(root.Path));
        viewModel.Refresh();

        Assert.Null(viewModel.CreateApplication("Browser"));
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.Equal("Not yet saved", viewModel.SelectorItems.Single(item => item.Title == "Browser").Summary);
        Assert.DoesNotContain("Browser", ReadApplicationNames(root.Path));

        viewModel.IsLaunchOnly = true;
        viewModel.IsStartUri = true;
        viewModel.StartUri = "https://example.com";

        Assert.True(viewModel.TrySaveChanges());
        Assert.Equal(["Alpha", "Browser", "Steam"], ReadApplicationNames(root.Path).Order());
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public void Rename_is_discardable_and_only_updates_mode_resource_keys_on_Save()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);
        var viewModel = new ApplicationsViewModel(Workspaces.At(root.Path));
        viewModel.Refresh();
        viewModel.SelectKey("Steam");

        Assert.Null(viewModel.RenameSelected("Big Picture"));
        Assert.Contains("Steam", ReadApplicationNames(root.Path));

        viewModel.DiscardChanges();
        Assert.Equal("Steam", viewModel.SelectedApplication);

        Assert.Null(viewModel.RenameSelected("Big Picture"));
        Assert.True(viewModel.TrySaveChanges());

        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(root.Path, json);
        ModeConfiguration configuration = ModeConfiguration.Parse(json, providers);
        Assert.DoesNotContain("Steam", ReadApplicationNames(root.Path));
        Assert.Contains("Big Picture", ReadApplicationNames(root.Path));
        Assert.Contains(
            configuration.Find("Gaming")!.Resources,
            resource => resource.Key == "Big Picture" && resource.Value == ApplicationProvider.Running);
    }

    [Fact]
    public void Duplicate_preserves_application_specific_runtime_behavior()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);
        var viewModel = new ApplicationsViewModel(Workspaces.At(root.Path));
        viewModel.Refresh();
        viewModel.SelectKey("Steam");

        Assert.True(viewModel.HasStop);
        Assert.True(viewModel.Reactivate);
        Assert.True(viewModel.ReadyEnabled);
        Assert.True(viewModel.ReadyActivate);

        Assert.Null(viewModel.DuplicateSelected("Steam copy"));
        Assert.True(viewModel.HasStop);
        Assert.False(viewModel.IsLaunchOnly);
        Assert.True(viewModel.TrySaveChanges());

        string savedCopy = JsonNode.Parse(File.ReadAllText(root.ConfigurationFile))!["providers"]!["applications"]!["Steam copy"]!.ToJsonString();
        Assert.Contains("\"stop\"", savedCopy, StringComparison.Ordinal);
        ApplicationDefinition copy = ReadApplications(root.Path).Single(provider => provider.Key == "Steam copy").Definition;
        Assert.Equal("steam://exit", copy.Stop!.Uri);
        Assert.True(copy.Reactivate);
        Assert.Equal([new ResourceRequirement("theme", "Dark")], copy.Requires);
        Assert.Equal(["theme"], copy.RestartWhen);
        Assert.True(copy.Ready!.Activate);
        Assert.Equal("f", copy.Ready.Keys);
    }

    private static IReadOnlyList<string> ReadApplicationNames(string root)
        => [.. ReadApplications(root).Select(provider => provider.Key)];

    private static IReadOnlyList<ApplicationProvider> ReadApplications(string root)
    {
        (string json, _) = ModeConfiguration.ReadOrCreate(root);
        IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(root, json);
        ModeConfiguration.Parse(json, providers);
        return [.. providers.OfType<ApplicationProvider>()];
    }
}

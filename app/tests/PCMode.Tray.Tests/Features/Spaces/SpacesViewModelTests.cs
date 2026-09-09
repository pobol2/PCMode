using PCMode.Engine;
using PCMode.Tray.Elements.Configuration;
using PCMode.Tray.Elements.Spaces;
using PCMode.Tray.Tests;

namespace PCMode.Tray.Tests.Features.Spaces;

/// <summary>
/// <see cref="SpacesViewModel"/> groups a level's resources the same way the
/// top-level navigation splits them across pages -- see
/// <see cref="SpacesViewModel"/>'s <c>CategoryOf</c> -- so a Space with an
/// application resource and an audio resource should show two separate,
/// correctly-named groups rather than one flat list.
/// </summary>
public sealed class SpacesViewModelTests
{
    private const string Configuration = """
        {
          "schemaVersion": 1,
          "providers": {
            "audio": { "devices": { "Speakers": "Test Speakers" } },
            "applications": { "Steam": { "process": "steam", "start": "steam://open/bigpicture" } }
          },
          "spaces": [
            {
              "id": "desk",
              "name": "Desk",
              "resources": {},
              "activities": [
                {
                  "id": "gaming",
                  "name": "Gaming",
                  "resources": { "audio": "Speakers", "Steam": "Running" }
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Configured_settings_are_grouped_by_the_page_each_resource_belongs_to()
    {
        using var root = new TemporaryConfigurationRoot(Configuration);

        var viewModel = new SpacesViewModel(Workspaces.At(root.Path));
        viewModel.Refresh();

        HierarchyNodeViewModel activity = Assert.Single(viewModel.Nodes, node => node.Level == NodeLevel.Activity);

        viewModel.SelectKey(activity.Address);

        EditableResourceGroup applications = Assert.Single(
            viewModel.ConfiguredSettings,
            group => group.Name == "Applications"
        );
        Assert.Equal(["Steam"], applications.Items.Select(item => item.DisplayName));

        EditableResourceGroup audio = Assert.Single(
            viewModel.ConfiguredSettings,
            group => group.Name == "Audio output"
        );
        Assert.Equal(["Audio output"], audio.Items.Select(item => item.DisplayName));

        // Resources this activity has not configured (display, case lighting,
        // theme, etc.) still land in their own page-named groups among the
        // addable settings, not one undifferentiated list.
        Assert.Contains(viewModel.AddableSettings, group => group.Name == "Displays");
        Assert.Contains(viewModel.AddableSettings, group => group.Name == "Case lighting");
        Assert.Contains(viewModel.AddableSettings, group => group.Name == "Other");
    }

    private const string ConfigurationWithTelevision = """
        {
          "schemaVersion": 1,
          "providers": {
            "lgTv": { "host": "192.0.2.31", "macAddress": "00:00:5E:00:53:31",
              "inputs": { "PC": "HDMI_4", "Console": "HDMI_3" } }
          },
          "spaces": [
            {
              "id": "living-room",
              "name": "Living Room",
              "resources": {},
              "activities": [
                {
                  "id": "gaming",
                  "name": "Gaming",
                  "resources": {},
                  "sources": [
                    { "id": "console", "name": "Console", "kind": "externalDevice",
                      "resources": { "tvInput": "Console" } }
                  ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// The one thing an External Source actually needs configuring is which
    /// television and which of its inputs -- see <see cref="TvInputProvider"/>
    /// and <see cref="TvPowerProvider"/> -- and that has to be a real choice
    /// from what the television is configured to have, not free text somebody
    /// has to spell exactly right.
    /// </summary>
    [Fact]
    public void A_televisions_input_offers_its_own_configured_input_names_as_choices()
    {
        using var root = new TemporaryConfigurationRoot(ConfigurationWithTelevision);

        var viewModel = new SpacesViewModel(Workspaces.At(root.Path));
        viewModel.Refresh();

        HierarchyNodeViewModel source = Assert.Single(viewModel.Nodes, node => node.Level == NodeLevel.Source);

        viewModel.SelectKey(source.Address);

        EditableResourceGroup displays = Assert.Single(viewModel.ConfiguredSettings, group => group.Name == "Displays");

        EditableResourceViewModel input = Assert.Single(displays.Items, item => item.Key == "tvInput");

        Assert.Equal(["Console", "PC"], input.Choices);
        Assert.Equal("Console", input.Selected);

        EditableResourceViewModel power = Assert.Single(
            viewModel.AddableSettings.Single(group => group.Name == "Displays").Items,
            item => item.Key == "tvPower"
        );

        Assert.Equal([TvPowerProvider.On, TvPowerProvider.Off], power.Choices);
    }

    private const string ConfigurationWithDisplayLayouts = """
        {
          "schemaVersion": 1,
          "providers": {
            "display": {
              "monitors": { "Desk1": "\\\\?\\DISPLAY#1", "Desk2": "\\\\?\\DISPLAY#2" },
              "layouts": {
                "Desktop": { "monitors": ["Desk1", "Desk2"], "primary": "Desk1", "placement": ["Desk2 RightOf Desk1"] },
                "TVOnly": { "monitors": ["Desk1"] }
              }
            }
          },
          "spaces": [
            {
              "id": "desk",
              "name": "Desk",
              "resources": {},
              "activities": [
                {
                  "id": "work",
                  "name": "Work",
                  "resources": { "display": "Desktop" }
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// Regression test for the Spaces "Display layout" dropdown coming up
    /// empty: <c>SpacesViewModel.ChoicesFor</c> had no case for
    /// <see cref="DisplayProvider"/>, so it fell through to the empty
    /// default no matter how many layouts <c>DisplayProvider.LayoutNames</c>
    /// reported.
    /// </summary>
    [Fact]
    public void A_display_resource_offers_every_configured_layout_name_as_a_choice()
    {
        using var root = new TemporaryConfigurationRoot(ConfigurationWithDisplayLayouts);

        var viewModel = new SpacesViewModel(Workspaces.At(root.Path));
        viewModel.Refresh();

        HierarchyNodeViewModel activity = Assert.Single(viewModel.Nodes, node => node.Level == NodeLevel.Activity);

        viewModel.SelectKey(activity.Address);

        EditableResourceGroup displays = Assert.Single(viewModel.ConfiguredSettings, group => group.Name == "Displays");

        EditableResourceViewModel display = Assert.Single(
            displays.Items,
            item => item.Key == DisplayProvider.ResourceKey
        );

        Assert.Equal(["Desktop", "TVOnly"], display.Choices);
        Assert.Equal("Desktop", display.Selected);
    }

    [Fact]
    public async Task Activation_uses_the_shared_runtime_and_marks_the_target_current_immediately()
    {
        const string document = """
            {
              "schemaVersion": 1,
              "spaces": [
                {
                  "id": "room",
                  "name": "Room",
                  "activities": [
                    { "id": "watch", "name": "Watch", "resources": { "test": "On" } }
                  ]
                }
              ]
            }
            """;

        using var root = new TemporaryConfigurationRoot();
        var provider = new RecordingProvider();
        ModeConfiguration configuration = ModeConfiguration.Parse(document, [provider]);
        var engine = new ModeEngine(configuration, new ModeStateStore(PCModePaths.StateFile(root.Path)), [provider]);
        using var runtime = new EngineRuntime(engine, root.Path);
        var workspace = new FakeWorkspace((configuration, [provider]));
        var viewModel = new SpacesViewModel(workspace, runtime);

        viewModel.Refresh();
        HierarchyNodeViewModel activity = Assert.Single(viewModel.Nodes, node => node.Level == NodeLevel.Activity);
        viewModel.SelectKey(activity.Address);

        Assert.True(viewModel.CanActivateSelected);
        Assert.Null(await viewModel.ActivateSelectedAsync());

        Assert.Equal(1, provider.Applies);
        Assert.True(activity.IsCurrent);
        Assert.Equal("room/watch", engine.ReadCurrentTargetKey(out _));
    }

    [Fact]
    public async Task Space_activity_and_source_are_all_activatable_for_now()
    {
        using var root = new TemporaryConfigurationRoot();

        const string document = """
            {
              "schemaVersion": 1,
              "spaces": [
                {
                  "id": "room",
                  "name": "Room",
                  "activities": [
                    {
                      "id": "watch",
                      "name": "Watch",
                      "resources": { "test": "On" },
                      "sources": [
                        {
                          "id": "console",
                          "name": "Console",
                          "kind": "externalDevice",
                          "resources": { "test": "Off" }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        var provider = new RecordingProvider();
        ModeConfiguration configuration = ModeConfiguration.Parse(document, [provider]);
        var engine = new ModeEngine(configuration, new ModeStateStore(PCModePaths.StateFile(root.Path)), [provider]);
        using var runtime = new EngineRuntime(engine, root.Path);
        var viewModel = new SpacesViewModel(new FakeWorkspace((configuration, [provider])), runtime);

        viewModel.Refresh();

        foreach (NodeLevel level in Enum.GetValues<NodeLevel>())
        {
            HierarchyNodeViewModel node = Assert.Single(viewModel.Nodes, candidate => candidate.Level == level);
            viewModel.SelectKey(node.Address);

            Assert.True(viewModel.CanActivateSelected);
            Assert.Null(await viewModel.ActivateSelectedAsync());
        }

        Assert.Equal("room/watch/console", engine.ReadCurrentTargetKey(out _));
    }

    private sealed class RecordingProvider : IModeResourceProvider
    {
        private string _current = "Off";

        public string Key => "test";

        public string DisplayName => "Test";

        public int Applies { get; private set; }

        public bool IsValidValue(string value, out string? problem)
        {
            problem = null;
            return true;
        }

        public string? Read() => _current;

        public void Apply(string value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Applies++;
            _current = value;
        }
    }
}

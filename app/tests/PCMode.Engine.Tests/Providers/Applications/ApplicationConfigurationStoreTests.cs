using System.IO;
using System.Text.Json.Nodes;

namespace PCMode.Engine.Tests;

/// <summary>What writing an application to the real configuration file does, and refuses to do.</summary>
public sealed class ApplicationConfigurationStoreTests
{
    private const string Document = """
        {
          "schemaVersion": 1,
          "providers": {
            "applications": {
              "Steam": { "process": "steam", "start": "C:\\Program Files (x86)\\Steam\\steam.exe" }
            }
          },
          "spaces": [ { "id": "space", "name": "Space", "activities": [
            { "id": "desktop", "name": "Desktop", "resources": {} },
            { "id": "gaming", "name": "Gaming", "resources": { "Steam": "Running" } }
          ] } ]
        }
        """;

    private static ApplicationEdit BasicEdit(
        string name,
        string process = "media",
        string executable = @"C:\Media\media.exe"
    ) =>
        new(
            name,
            ApplicationObservationKind.Process,
            process,
            new LaunchCommandEdit(executable, [], null),
            Stop: null,
            Reactivate: false,
            Requires: [],
            RestartWhen: [],
            Ready: null,
            StartWithinMs: 3000,
            StopWithinMs: 5000
        );

    [Fact]
    public void Creates_an_application_that_ApplicationCatalog_then_reads_back()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ModeSaveOutcome outcome = ApplicationConfigurationStore.SaveApplication(
            root.Path,
            previousName: null,
            BasicEdit("Media Player")
        );

        Assert.True(outcome.Success, outcome.Problem);

        ApplicationDefinition definition = ReadDefinition(root, "Media Player");
        Assert.Equal("media", definition.ProcessName);
        Assert.Equal(@"C:\Media\media.exe", definition.Start.Executable);
        Assert.Null(definition.Start.Uri);
    }

    [Fact]
    public void Writes_a_uri_start_command_and_reads_it_back_without_an_executable()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ApplicationEdit edit = new(
            "Big Picture",
            ApplicationObservationKind.Process,
            "steam",
            new LaunchCommandEdit(null, [], "steam://open/bigpicture"),
            Stop: new LaunchCommandEdit(null, [], "steam://exit"),
            Reactivate: true,
            Requires: [],
            RestartWhen: [],
            Ready: null,
            StartWithinMs: 3000,
            StopWithinMs: 5000
        );

        Assert.True(ApplicationConfigurationStore.SaveApplication(root.Path, null, edit).Success);

        ApplicationDefinition definition = ReadDefinition(root, "Big Picture");
        Assert.Equal("steam://open/bigpicture", definition.Start.Uri);
        Assert.Null(definition.Start.Executable);
        Assert.Equal("steam://exit", definition.Stop!.Uri);
        Assert.True(definition.Reactivate);
    }

    [Fact]
    public void Writes_arguments_only_in_the_object_form_and_the_plain_form_otherwise()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ApplicationEdit withArguments = BasicEdit("Emulator") with
        {
            Start = new LaunchCommandEdit(@"C:\Emu\emu.exe", ["-fullscreen", "-rom", "game.iso"], null),
        };

        Assert.True(ApplicationConfigurationStore.SaveApplication(root.Path, null, withArguments).Success);

        string saved = File.ReadAllText(root.ConfigurationFile);
        Assert.Contains("\"arguments\"", saved, StringComparison.Ordinal);

        ApplicationDefinition definition = ReadDefinition(root, "Emulator");
        Assert.Equal(["-fullscreen", "-rom", "game.iso"], definition.Start.Arguments);

        // A plain path, with no arguments, is written the short way -- the same
        // form almost every hand-written entry already uses.
        Assert.True(ApplicationConfigurationStore.SaveApplication(root.Path, null, BasicEdit("Simple")).Success);
        string savedAfter = File.ReadAllText(root.ConfigurationFile);
        Assert.Contains("\"start\": \"C:\\\\Media\\\\media.exe\"", savedAfter, StringComparison.Ordinal);
    }

    [Fact]
    public void Writes_requires_restartWhen_and_ready()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ApplicationEdit edit = BasicEdit("Media Player") with
        {
            Requires = [new ApplicationRequirementEdit("display", "TVOnly")],
            RestartWhen = ["display"],
            Ready = new ReadinessEdit(true, "f", 8000),
        };

        Assert.True(ApplicationConfigurationStore.SaveApplication(root.Path, null, edit).Success);

        ApplicationDefinition definition = ReadDefinition(root, "Media Player");
        Assert.Equal([new ResourceRequirement("display", "TVOnly")], definition.Requires);
        Assert.Equal(["display"], definition.RestartWhen);
        Assert.True(definition.Ready!.Activate);
        Assert.Equal("f", definition.Ready.Keys);
        Assert.Equal(TimeSpan.FromSeconds(8), definition.Ready.Within);
    }

    [Fact]
    public void Editing_an_existing_application_replaces_it_in_place()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ApplicationEdit edit = new(
            "Steam",
            ApplicationObservationKind.Process,
            "steam",
            new LaunchCommandEdit(null, [], "steam://open/bigpicture"),
            Stop: null,
            Reactivate: false,
            Requires: [],
            RestartWhen: [],
            Ready: null,
            StartWithinMs: 3000,
            StopWithinMs: 5000
        );

        Assert.True(ApplicationConfigurationStore.SaveApplication(root.Path, "Steam", edit).Success);

        ApplicationDefinition definition = ReadDefinition(root, "Steam");
        Assert.Equal("steam://open/bigpicture", definition.Start.Uri);

        // Still one application named Steam, and 'Gaming' still asks for it.
        Assert.Equal(["Steam"], ApplicationCatalog.NamesIn(File.ReadAllText(root.ConfigurationFile)));
    }

    [Fact]
    public void Renaming_moves_the_resource_key_in_every_mode_that_had_an_opinion_about_it()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ApplicationEdit edit = new(
            "Big Picture",
            ApplicationObservationKind.Process,
            "steam",
            new LaunchCommandEdit(null, [], "steam://open/bigpicture"),
            Stop: null,
            Reactivate: false,
            Requires: [],
            RestartWhen: [],
            Ready: null,
            StartWithinMs: 3000,
            StopWithinMs: 5000
        );

        ModeSaveOutcome outcome = ApplicationConfigurationStore.SaveApplication(root.Path, "Steam", edit);
        Assert.True(outcome.Success, outcome.Problem);

        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ApplicationProviders(
            json,
            new FakeProcessHost(),
            []
        );
        ModeConfiguration configuration = ModeConfiguration.Parse(json, providers);

        Assert.Equal(["Big Picture"], providers.Select(provider => provider.Key));

        ConfiguredResource gaming = configuration
            .Find("Gaming")!
            .Resources.Single(resource => resource.Key == "Big Picture");
        Assert.Equal("Running", gaming.Value);

        Assert.DoesNotContain(configuration.Find("Gaming")!.Resources, resource => resource.Key == "Steam");
    }

    [Fact]
    public void Saves_a_launch_only_application_without_process_dependent_fields()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ApplicationEdit edit = new(
            "Browser Tab",
            ApplicationObservationKind.None,
            ProcessName: string.Empty,
            new LaunchCommandEdit(null, [], "https://example.com"),
            Stop: null,
            Reactivate: false,
            Requires: [],
            RestartWhen: [],
            Ready: null,
            StartWithinMs: 3000,
            StopWithinMs: 5000
        );

        ModeSaveOutcome outcome = ApplicationConfigurationStore.SaveApplication(root.Path, previousName: null, edit);
        Assert.True(outcome.Success, outcome.Problem);

        JsonNode document = JsonNode.Parse(File.ReadAllText(root.ConfigurationFile))!;
        string savedEntry = document["providers"]![ApplicationCatalog.SectionKey]!["Browser Tab"]!.ToJsonString();
        Assert.DoesNotContain("\"process\"", savedEntry, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stop\"", savedEntry, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reactivate\"", savedEntry, StringComparison.Ordinal);
        Assert.DoesNotContain("\"restartWhen\"", savedEntry, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ready\"", savedEntry, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stopWithin\"", savedEntry, StringComparison.Ordinal);

        ApplicationDefinition definition = ReadDefinition(root, "Browser Tab");
        Assert.False(definition.CanObserve);
        Assert.False(definition.CanStop);
        Assert.False(definition.CanRestart);
        Assert.Equal("https://example.com", definition.Start.Uri);
    }

    [Fact]
    public void Refuses_a_launch_only_edit_with_an_unrecognized_observation_kind()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ApplicationEdit edit = BasicEdit("Media Player") with
        {
            ObservationKind = (ApplicationObservationKind)99,
            ProcessName = string.Empty,
        };

        ModeSaveOutcome outcome = ApplicationConfigurationStore.SaveApplication(root.Path, null, edit);

        Assert.False(outcome.Success);
        Assert.Contains("detect this application", outcome.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "media", "a name")]
    [InlineData("Media Player", "", "process name")]
    public void Refuses_a_blank_name_or_process_and_leaves_the_file_untouched(
        string name,
        string process,
        string expected
    )
    {
        using var root = new TemporaryRoot();
        root.Write(Document);
        string before = File.ReadAllText(root.ConfigurationFile);

        ModeSaveOutcome outcome = ApplicationConfigurationStore.SaveApplication(
            root.Path,
            null,
            BasicEdit(name, process)
        );

        Assert.False(outcome.Success);
        Assert.Contains(expected, outcome.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(root.ConfigurationFile));
    }

    [Fact]
    public void Refuses_a_command_with_neither_a_program_nor_a_web_address()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ApplicationEdit edit = BasicEdit("Media Player") with { Start = new LaunchCommandEdit(null, [], null) };
        ModeSaveOutcome outcome = ApplicationConfigurationStore.SaveApplication(root.Path, null, edit);

        Assert.False(outcome.Success);
        Assert.Contains("program to run or a web address", outcome.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refuses_a_command_with_both_a_program_and_a_web_address()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ApplicationEdit edit = BasicEdit("Media Player") with
        {
            Start = new LaunchCommandEdit(@"C:\Media\media.exe", [], "media://open"),
        };

        ModeSaveOutcome outcome = ApplicationConfigurationStore.SaveApplication(root.Path, null, edit);

        Assert.False(outcome.Success);
        Assert.Contains("not both", outcome.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refuses_arguments_given_alongside_a_web_address()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ApplicationEdit edit = BasicEdit("Media Player") with
        {
            Start = new LaunchCommandEdit(null, ["-x"], "media://open"),
        };

        ModeSaveOutcome outcome = ApplicationConfigurationStore.SaveApplication(root.Path, null, edit);

        Assert.False(outcome.Success);
        Assert.Contains("cannot take arguments", outcome.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refuses_creating_an_application_that_already_exists()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ModeSaveOutcome outcome = ApplicationConfigurationStore.SaveApplication(root.Path, null, BasicEdit("Steam"));

        Assert.False(outcome.Success);
        Assert.Contains("already", outcome.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refuses_a_name_that_collides_with_a_built_in_resource_without_writing_anything()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);
        string before = File.ReadAllText(root.ConfigurationFile);

        ModeSaveOutcome outcome = ApplicationConfigurationStore.SaveApplication(root.Path, null, BasicEdit("theme"));

        Assert.False(outcome.Success);
        Assert.Equal(before, File.ReadAllText(root.ConfigurationFile));
    }

    [Fact]
    public void Removes_an_application_no_mode_has_an_opinion_about()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        Assert.True(ApplicationConfigurationStore.SaveApplication(root.Path, null, BasicEdit("Unused")).Success);

        ModeSaveOutcome outcome = ApplicationConfigurationStore.RemoveApplication(root.Path, "Unused");

        Assert.True(outcome.Success, outcome.Problem);
        Assert.DoesNotContain("Unused", ApplicationCatalog.NamesIn(File.ReadAllText(root.ConfigurationFile)));
    }

    [Fact]
    public void Refuses_to_remove_an_application_a_mode_still_has_an_opinion_about()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ModeSaveOutcome outcome = ApplicationConfigurationStore.RemoveApplication(root.Path, "Steam");

        Assert.False(outcome.Success);
        Assert.Contains("'Space — Gaming'", outcome.Problem, StringComparison.Ordinal);
        Assert.Contains("Steam", ApplicationCatalog.NamesIn(File.ReadAllText(root.ConfigurationFile)));
    }

    [Fact]
    public void Refuses_to_remove_an_application_nothing_defines()
    {
        using var root = new TemporaryRoot();
        root.Write(Document);

        ModeSaveOutcome outcome = ApplicationConfigurationStore.RemoveApplication(root.Path, "Nothing");

        Assert.False(outcome.Success);
    }

    private static ApplicationDefinition ReadDefinition(TemporaryRoot root, string name)
    {
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ApplicationProviders(
            json,
            new FakeProcessHost(),
            []
        );
        ModeConfiguration.Parse(json, providers);

        return (
            (ApplicationProvider)
                providers.Single(provider => string.Equals(provider.Key, name, StringComparison.OrdinalIgnoreCase))
        ).Definition;
    }
}

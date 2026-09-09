using PCMode.Engine;
using PCMode.Tray.Elements.Configuration;
using PCMode.Tray.Platform.Logging;
using PCMode.Tray.Tests;

namespace PCMode.Tray.Tests.Services;

/// <summary>
/// <see cref="ConfigurationWorkspace.Apply"/> -- the piece that
/// lets a page's save reach the running engine -- and its wiring to a real
/// <see cref="EngineRuntime.Reload"/>, the exact pattern <c>App.xaml.cs</c>
/// uses. <see cref="EngineRuntime.Reload"/>'s own activation rules (deferred
/// while busy, refused when invalid, etc.) are covered in
/// <c>PCMode.Engine.Tests</c>; what belongs here is the boundary this project
/// actually owns: what <see cref="IConfigurationWorkspace.Apply"/> does with
/// whatever the activation callback reports.
/// </summary>
public sealed class ConfigurationWorkspaceApplyTests
{
    [Fact]
    public void Apply_returns_the_message_unchanged_when_there_is_nothing_to_activate()
    {
        IConfigurationWorkspace workspace = ConfigurationWorkspace.For(root: null);

        string result = workspace.Apply("Saved.");

        Assert.Equal("Saved.", result);
    }

    [Fact]
    public void Apply_returns_the_message_unchanged_when_activation_succeeds()
    {
        IConfigurationWorkspace workspace = ConfigurationWorkspace.For(
            root: null,
            activate: () => RuntimeActivationOutcome.Ok("The updated configuration is now active.")
        );

        string result = workspace.Apply("Saved.");

        Assert.Equal("Saved.", result);
    }

    [Fact]
    public void Apply_appends_the_reason_when_activation_is_deferred()
    {
        IConfigurationWorkspace workspace = ConfigurationWorkspace.For(
            root: null,
            activate: () => RuntimeActivationOutcome.Deferred("A transition is running.")
        );

        string result = workspace.Apply("Saved.");

        Assert.Equal("Saved. A transition is running.", result);
    }

    [Fact]
    public void Apply_appends_the_reason_when_activation_fails()
    {
        IConfigurationWorkspace workspace = ConfigurationWorkspace.For(
            root: null,
            activate: () => RuntimeActivationOutcome.Failed("The saved configuration could not be activated.")
        );

        string result = workspace.Apply("Saved.");

        Assert.Equal("Saved. The saved configuration could not be activated.", result);
    }

    [Fact]
    public void Apply_logs_the_final_message_through_the_shared_log()
    {
        using var root = new TemporaryConfigurationRoot();
        var log = new LocalLog(root.Path);
        IConfigurationWorkspace workspace = ConfigurationWorkspace.For(
            root.Path,
            log,
            activate: () => RuntimeActivationOutcome.Deferred("Busy.")
        );

        workspace.Apply("Saved.");

        Assert.Contains(log.ReadRecentLines(), line => line.Contains("Saved. Busy.", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_reloads_a_real_engine_runtime_the_same_way_the_composition_root_wires_it()
    {
        using var root = new TemporaryConfigurationRoot(
            """
            {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
              {"id":"desktop","name":"Desktop","resources":{}}]}]}
            """
        );

        ModeEngine initial = EngineBootstrap.Create(root.Path);
        var runtime = new EngineRuntime(initial, root.Path);

        // Exactly App.xaml.cs's own wiring: ConfigurationWorkspace.For(..., activate: runtime.Reload).
        IConfigurationWorkspace workspace = ConfigurationWorkspace.For(root.Path, activate: runtime.Reload);

        Assert.Equal(["desk/desktop"], runtime.Engine.TargetKeys);

        root.Write(
            """
            {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
              {"id":"desktop","name":"Desktop","resources":{}},
              {"id":"movie","name":"Movie","resources":{}}]}]}
            """
        );

        string result = workspace.Apply("'Desk â€” Movie' saved.");

        Assert.Equal("'Desk â€” Movie' saved.", result);
        Assert.NotSame(initial, runtime.Engine);
        Assert.Equal(["desk/desktop", "desk/movie"], runtime.Engine.TargetKeys);
    }

    [Fact]
    public void Apply_reports_the_reason_when_the_saved_configuration_does_not_validate()
    {
        using var root = new TemporaryConfigurationRoot(
            """
            {"schemaVersion":1,"spaces":[{"id":"desk","name":"Desk","activities":[
              {"id":"desktop","name":"Desktop","resources":{}}]}]}
            """
        );

        ModeEngine initial = EngineBootstrap.Create(root.Path);
        var runtime = new EngineRuntime(initial, root.Path);
        IConfigurationWorkspace workspace = ConfigurationWorkspace.For(root.Path, activate: runtime.Reload);

        // Corrupted after the runtime was built with a good one, exactly what a
        // save that raced with something else would leave.
        root.Write("{ not valid json");

        string result = workspace.Apply("Saved.");

        Assert.StartsWith("Saved. The saved configuration could not be activated", result, StringComparison.Ordinal);
        Assert.Same(initial, runtime.Engine);
    }
}

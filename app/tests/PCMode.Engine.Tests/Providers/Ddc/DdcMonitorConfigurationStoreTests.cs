namespace PCMode.Engine.Tests;

/// <summary>What the Displays page's DDC/CI section does to 'providers.ddcMonitors'.</summary>
public sealed class DdcMonitorConfigurationStoreTests
{
    private const string Document = """
        {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{}}]}]}
        """;

    [Fact]
    public void Binds_an_alias_to_a_device_path_and_reads_it_back()
    {
        using var root = new TemporaryRoot(Document);

        Assert.True(DdcMonitorConfigurationStore.Save(root.Path, "Desk Left", @"\\?\DISPLAY#1").Success);

        Assert.Equal(@"\\?\DISPLAY#1", DdcMonitorConfigurationStore.Read(root.Path)["Desk Left"]);
    }

    /// <summary>
    /// An empty 'providers.ddcMonitors' builds no providers, so nothing reads
    /// it -- and a settings section nothing reads is refused. Without removing
    /// the section along with its last entry, the last monitor would be the one
    /// that could never be removed.
    /// </summary>
    [Fact]
    public void Removing_the_last_monitor_removes_the_section_with_it()
    {
        using var root = new TemporaryRoot(Document);
        Assert.True(DdcMonitorConfigurationStore.Save(root.Path, "Desk Left", @"\\?\DISPLAY#1").Success);

        ModeSaveOutcome outcome = DdcMonitorConfigurationStore.Delete(root.Path, "Desk Left");

        Assert.True(outcome.Success, outcome.Problem);
        Assert.Empty(DdcMonitorConfigurationStore.Read(root.Path));
    }

    [Fact]
    public void Removing_one_of_several_monitors_leaves_the_others_bound()
    {
        using var root = new TemporaryRoot(Document);
        Assert.True(DdcMonitorConfigurationStore.Save(root.Path, "Desk Left", @"\\?\DISPLAY#1").Success);
        Assert.True(DdcMonitorConfigurationStore.Save(root.Path, "Desk Right", @"\\?\DISPLAY#2").Success);

        Assert.True(DdcMonitorConfigurationStore.Delete(root.Path, "Desk Left").Success);

        IReadOnlyDictionary<string, string> remaining = DdcMonitorConfigurationStore.Read(root.Path);
        Assert.Equal(@"\\?\DISPLAY#2", Assert.Single(remaining).Value);
    }

    [Fact]
    public void Renaming_keeps_the_device_path_the_alias_was_bound_to()
    {
        using var root = new TemporaryRoot(Document);
        Assert.True(DdcMonitorConfigurationStore.Save(root.Path, "Desk Left", @"\\?\DISPLAY#1").Success);

        Assert.True(DdcMonitorConfigurationStore.Rename(root.Path, "Desk Left", "Left Monitor").Success);

        IReadOnlyDictionary<string, string> monitors = DdcMonitorConfigurationStore.Read(root.Path);
        Assert.Equal(@"\\?\DISPLAY#1", monitors["Left Monitor"]);
        Assert.False(monitors.ContainsKey("Desk Left"));
    }
}

using System.IO;
using PCMode.Engine;
using Xunit;

namespace PCMode.Engine.Tests;

/// <summary>What the case lighting settings screen does with 'providers.caseRgb'.</summary>
public sealed class CaseRgbConfigurationStoreTests
{
    [Fact]
    public void Saves_and_reads_back_a_configured_device_list()
    {
        using var root = new TemporaryRoot(
            """{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{}}]}]}"""
        );

        ModeSaveOutcome outcome = CaseRgbConfigurationStore.Save(
            root.Path,
            new CaseRgbEdit(
                true,
                @"C:\OpenRGB\OpenRGB.exe",
                [new CaseRgbDeviceEdit(0, "Direct"), new CaseRgbDeviceEdit(1, "Static")],
                6000
            )
        );

        Assert.True(outcome.Success, outcome.Problem);

        CaseRgbProvider provider = Load(root);

        Assert.True(provider.Enabled);
        Assert.Equal(@"C:\OpenRGB\OpenRGB.exe", provider.Executable);
        Assert.Equal(6000, provider.WithinMs);
        Assert.Equal(
            [new KeyValuePair<int, string>(0, "Direct"), new KeyValuePair<int, string>(1, "Static")],
            provider.Devices
        );
    }

    [Fact]
    public void Allows_switching_it_on_without_the_legacy_executable_path()
    {
        using var root = new TemporaryRoot(
            """{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{}}]}]}"""
        );

        ModeSaveOutcome outcome = CaseRgbConfigurationStore.Save(
            root.Path,
            new CaseRgbEdit(true, string.Empty, [new CaseRgbDeviceEdit(0, "Direct")], 4000)
        );

        Assert.True(outcome.Success, outcome.Problem);

        CaseRgbProvider provider = Load(root);
        Assert.True(provider.Enabled);
        Assert.Null(provider.Executable);
        Assert.Equal([new KeyValuePair<int, string>(0, "Direct")], provider.Devices);
    }

    [Fact]
    public void Refuses_switching_it_on_with_no_devices()
    {
        ModeSaveOutcome outcome = CaseRgbConfigurationStore.Save(
            NewRoot(out TemporaryRoot root),
            new CaseRgbEdit(true, string.Empty, [], 4000)
        );

        root.Dispose();
        Assert.False(outcome.Success);
        Assert.Contains("device", outcome.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refuses_a_device_listed_twice()
    {
        using var root = new TemporaryRoot(
            """{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{}}]}]}"""
        );

        ModeSaveOutcome outcome = CaseRgbConfigurationStore.Save(
            root.Path,
            new CaseRgbEdit(
                true,
                string.Empty,
                [new CaseRgbDeviceEdit(0, "Direct"), new CaseRgbDeviceEdit(0, "Static")],
                4000
            )
        );

        Assert.False(outcome.Success);
        Assert.Contains("more than once", outcome.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_it_off_keeps_the_device_list()
    {
        using var root = new TemporaryRoot(
            """{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{}}]}]}"""
        );

        Assert.True(
            CaseRgbConfigurationStore
                .Save(root.Path, new CaseRgbEdit(false, string.Empty, [new CaseRgbDeviceEdit(0, "Direct")], 4000))
                .Success
        );

        CaseRgbProvider provider = Load(root);
        Assert.False(provider.Enabled);
        Assert.Equal([new KeyValuePair<int, string>(0, "Direct")], provider.Devices);
    }

    private static string NewRoot(out TemporaryRoot root)
    {
        root = new TemporaryRoot(
            """{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"desktop","name":"Desktop","resources":{}}]}]}"""
        );
        return root.Path;
    }

    private static CaseRgbProvider Load(TemporaryRoot root)
    {
        (string json, _) = ModeConfiguration.ReadOrCreate(root.Path);
        IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(root.Path, json);
        ModeConfiguration.Parse(json, providers);

        return providers.OfType<CaseRgbProvider>().Single();
    }
}

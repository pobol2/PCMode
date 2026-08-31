using System.Text.Json.Nodes;

namespace PCMode.Engine.Tests;

/// <summary>
/// Which DDC/CI resources a document produces, and which monitor each of them
/// commands.
/// </summary>
/// <remarks>
/// The rules here are the ones that cannot be checked by looking at either
/// provider on its own: that one configured alias produces exactly two
/// resources correctly keyed and both configured, that two aliases stay
/// independent, and that a document naming no monitors produces no DDC/CI
/// resources at all -- unlike the television, DDC/CI has no historical shape
/// to stay compatible with.
/// </remarks>
public sealed class DdcMonitorProviderSetTests
{
    private const string LeftPath = @"\\?\DISPLAY#GSM5B09#5&39b3a707&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string RightPath = @"\\?\DISPLAY#GSM5B0A#5&39b3a707&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    [Fact]
    public void A_document_naming_no_monitors_produces_no_DDC_resources()
    {
        IReadOnlyList<IModeResourceProvider> providers = Providers(section: null);

        Assert.Empty(providers);
    }

    [Fact]
    public void One_configured_monitor_is_one_connection_and_two_resources()
    {
        IReadOnlyList<IModeResourceProvider> providers = Providers(Section(("Left", LeftPath)));

        Assert.Equal(
            ["ddc.Left.brightness", "ddc.Left.input"],
            providers.Select(provider => provider.Key).Order(StringComparer.OrdinalIgnoreCase));

        var brightness = Assert.IsType<DdcBrightnessProvider>(
            providers.Single(provider => provider.Key == "ddc.Left.brightness"));
        var input = Assert.IsType<DdcInputProvider>(
            providers.Single(provider => provider.Key == "ddc.Left.input"));

        // Both resources are configured (no "does not define monitor" refusal),
        // which is only possible if they share a connection that actually
        // resolved 'Left'.
        Assert.NotEqual(AvailabilityState.Disabled, brightness.AvailabilityOf("50").State);
        Assert.NotEqual(AvailabilityState.Disabled, input.AvailabilityOf("HDMI1").State);
    }

    [Fact]
    public void Two_configured_monitors_are_two_independent_connections()
    {
        IReadOnlyList<IModeResourceProvider> providers = Providers(
            Section(("Left", LeftPath), ("Right", RightPath)));

        Assert.Equal(
            ["ddc.Left.brightness", "ddc.Left.input", "ddc.Right.brightness", "ddc.Right.input"],
            providers.Select(provider => provider.Key).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_malformed_monitor_entry_refuses_the_whole_document()
    {
        var document = new JsonObject { ["schemaVersion"] = 1 };
        document["providers"] = new JsonObject
        {
            ["ddcMonitors"] = new JsonObject { ["Left"] = "" },
        };
        document["spaces"] = EmptySpaces();

        string json = document.ToJsonString();

        IReadOnlyList<IModeResourceProvider> providers =
            EngineBootstrap.DdcMonitorProviders(json, new FakeDdcMonitors());

        ModeConfigurationException failure =
            Assert.Throws<ModeConfigurationException>(() => ModeConfiguration.Parse(json, providers));

        Assert.Contains("is empty", failure.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<IModeResourceProvider> Providers(JsonObject? section)
    {
        var document = new JsonObject { ["schemaVersion"] = 1 };

        if (section is not null)
        {
            document["providers"] = new JsonObject { ["ddcMonitors"] = section };
        }

        document["spaces"] = EmptySpaces();

        string json = document.ToJsonString();

        IReadOnlyList<IModeResourceProvider> providers =
            EngineBootstrap.DdcMonitorProviders(json, new FakeDdcMonitors());

        ModeConfiguration.Parse(json, providers);

        return providers;
    }

    private static JsonArray EmptySpaces() =>
    [
        new JsonObject
        {
            ["id"] = "space",
            ["name"] = "Space",
            ["activities"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "desktop",
                    ["name"] = "Desktop",
                    ["resources"] = new JsonObject(),
                },
            },
        },
    ];

    private static JsonObject Section(params (string Alias, string DevicePath)[] monitors)
    {
        var map = new JsonObject();

        foreach ((string alias, string devicePath) in monitors)
        {
            map[alias] = devicePath;
        }

        return map;
    }
}

using System.Text.Json;

namespace PCMode.Engine.Tests;

/// <summary>
/// What the Displays category reports, built from the same
/// <see cref="DisplayProvider"/> a transition applies layouts through.
/// </summary>
public sealed class DisplayDiscovererTests
{
    /// <summary>A configured provider, optionally with monitor aliases.</summary>
    private static DisplayProvider Provider(FakeDisplayDevices devices, string? monitorsJson = null)
    {
        var provider = new DisplayProvider(devices);

        string settings = monitorsJson is null ? "{}" : $$"""{ "monitors": {{monitorsJson}} }""";
        using JsonDocument document = JsonDocument.Parse(settings);

        Assert.True(provider.TryConfigure(document.RootElement.Clone(), out string? problem), problem);
        return provider;
    }

    [Fact]
    public void Reports_an_active_monitor_with_its_picture()
    {
        var devices = new FakeDisplayDevices()
            .Add(new FakeMonitor { Path = @"\\?\DISPLAY#1", Name = "Dell U2720Q", Active = true, Width = 2560, Height = 1440 });

        HardwareItem item = Assert.Single(new DisplayDiscoverer(Provider(devices)).Discover());

        Assert.Equal("Dell U2720Q", item.Name);
        Assert.Equal(HardwareItemStatus.Ok, item.Status);
        Assert.Contains("2560", item.Summary);
        Assert.Equal(@"\\?\DISPLAY#1", item.Identifier);
        Assert.Null(item.Alias);
        Assert.Contains(item.Details, detail => detail.Label == "Device path" && detail.Value == @"\\?\DISPLAY#1");
    }

    [Fact]
    public void Reports_a_known_but_dark_monitor_as_not_detected_rather_than_an_error()
    {
        // Windows keeps a slot for a television it has ever seen, whether or
        // not it is switched on right now -- that is an ordinary state, not a
        // fault, and must not read as one.
        var devices = new FakeDisplayDevices()
            .Add(new FakeMonitor { Path = @"\\?\DISPLAY#TV", Name = "Living Room TV", Active = false });

        HardwareItem item = Assert.Single(new DisplayDiscoverer(Provider(devices)).Discover());

        Assert.Equal(HardwareItemStatus.NotDetected, item.Status);
        Assert.DoesNotContain("error", item.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Shows_the_configured_alias_and_marks_the_item_as_configured()
    {
        var devices = new FakeDisplayDevices()
            .Add(new FakeMonitor { Path = @"\\?\DISPLAY#1", Name = "Dell U2720Q", Active = true });

        HardwareItem item = Assert.Single(
            new DisplayDiscoverer(Provider(devices, """{ "Desk Left": "\\\\?\\DISPLAY#1" } """)).Discover());

        Assert.Equal("Desk Left", item.Alias);
        Assert.True(item.IsConfigured);
        Assert.True(item.SupportsAlias);

        // The raw name is still there for anyone who wants it.
        Assert.Equal("Dell U2720Q", item.Name);
    }

    [Fact]
    public void An_unconfigured_monitor_supports_an_alias_but_does_not_have_one()
    {
        var devices = new FakeDisplayDevices()
            .Add(new FakeMonitor { Path = @"\\?\DISPLAY#1", Name = "Dell U2720Q", Active = true });

        HardwareItem item = Assert.Single(new DisplayDiscoverer(Provider(devices)).Discover());

        Assert.True(item.SupportsAlias);
        Assert.False(item.IsConfigured);
    }

    [Fact]
    public void Numbers_two_monitors_that_report_the_same_friendly_name()
    {
        // Two units of the same model report the identical EDID name. The
        // device path is what actually tells them apart, and it stays
        // available as the identifier and in the details -- but the headline
        // must not show the two as if they were one entry twice.
        var devices = new FakeDisplayDevices()
            .Add(new FakeMonitor { Path = @"\\?\DISPLAY#A", Name = "Dell U2720Q", Active = true })
            .Add(new FakeMonitor { Path = @"\\?\DISPLAY#B", Name = "Dell U2720Q", Active = true });

        IReadOnlyList<HardwareItem> items = new DisplayDiscoverer(Provider(devices)).Discover();

        Assert.Equal(2, items.Count);
        Assert.Equal(["Dell U2720Q (#1)", "Dell U2720Q (#2)"], items.Select(item => item.Name));
        Assert.Equal(@"\\?\DISPLAY#A", items[0].Identifier);
        Assert.Equal(@"\\?\DISPLAY#B", items[1].Identifier);
    }

    [Fact]
    public void A_monitor_with_no_friendly_name_still_gets_one()
    {
        var devices = new FakeDisplayDevices()
            .Add(new FakeMonitor { Path = @"\\?\DISPLAY#X", Name = string.Empty, Active = false });

        HardwareItem item = Assert.Single(new DisplayDiscoverer(Provider(devices)).Discover());

        Assert.Equal("Display 1", item.Name);
    }

    [Fact]
    public void Displays_being_unreadable_is_left_to_the_caller_to_report()
    {
        var devices = new FakeDisplayDevices { Fails = new InvalidOperationException("Windows could not read the monitors.") };

        Assert.Throws<InvalidOperationException>(() => new DisplayDiscoverer(Provider(devices)).Discover());
    }
}

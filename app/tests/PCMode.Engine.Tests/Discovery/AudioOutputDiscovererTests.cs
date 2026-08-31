using System.Text.Json;

namespace PCMode.Engine.Tests;

/// <summary>
/// What the Audio outputs category reports, built from the same
/// <see cref="AudioProvider"/> a transition switches the default device through.
/// </summary>
public sealed class AudioOutputDiscovererTests
{
    /// <summary>A configured provider, optionally with device aliases.</summary>
    private static AudioProvider Provider(FakeAudioEndpoints endpoints, string? devicesJson = null)
    {
        var provider = new AudioProvider(endpoints);

        string settings = devicesJson is null ? "{}" : $$"""{ "devices": {{devicesJson}} }""";
        using JsonDocument document = JsonDocument.Parse(settings);

        Assert.True(provider.TryConfigure(document.RootElement.Clone(), out string? problem), problem);
        return provider;
    }

    [Fact]
    public void Marks_the_current_default_and_leaves_the_others_plain()
    {
        var endpoints = new FakeAudioEndpoints("Speakers (Realtek)", "Living Room TV (NVIDIA)");
        endpoints.Default = endpoints.IdOf(1);

        IReadOnlyList<HardwareItem> items = new AudioOutputDiscoverer(Provider(endpoints)).Discover();

        Assert.Equal(2, items.Count);
        Assert.Equal(HardwareItemStatus.Ok, items[0].Status);
        Assert.Equal("Available.", items[0].Summary);
        Assert.Equal("Default Windows output.", items[1].Summary);
        Assert.Equal("No", items[0].Details.Single(detail => detail.Label == "Default").Value);
        Assert.Equal("Yes", items[1].Details.Single(detail => detail.Label == "Default").Value);
    }

    [Fact]
    public void An_empty_output_list_is_not_a_problem()
    {
        IReadOnlyList<HardwareItem> items = new AudioOutputDiscoverer(Provider(new FakeAudioEndpoints())).Discover();

        Assert.Empty(items);
    }

    [Fact]
    public void Shows_the_configured_alias_and_marks_the_item_as_configured()
    {
        var endpoints = new FakeAudioEndpoints("Speakers (Realtek)");

        HardwareItem item = Assert.Single(
            new AudioOutputDiscoverer(Provider(endpoints, """{ "Headphones": "Speakers (Realtek)" }""")).Discover());

        Assert.Equal("Headphones", item.Alias);
        Assert.True(item.IsConfigured);
        Assert.Equal("Speakers (Realtek)", item.Identifier);
    }

    [Fact]
    public void Numbers_two_endpoints_that_share_a_name_and_leaves_neither_aliasable()
    {
        // Windows does not enforce unique friendly names, and AudioProvider
        // matches by name alone -- an alias here could not say which of the two
        // was meant, so neither is offered one.
        var endpoints = new FakeAudioEndpoints("Speakers", "Speakers");

        IReadOnlyList<HardwareItem> items = new AudioOutputDiscoverer(Provider(endpoints)).Discover();

        Assert.Equal(["Speakers (#1)", "Speakers (#2)"], items.Select(item => item.Name));
        Assert.All(items, item => Assert.False(item.SupportsAlias));
    }

    [Fact]
    public void Core_Audio_being_unreachable_is_left_to_the_caller_to_report()
    {
        var endpoints = new FakeAudioEndpoints { Fails = new InvalidOperationException("Windows Core Audio could not be reached.") };

        Assert.Throws<InvalidOperationException>(() => new AudioOutputDiscoverer(Provider(endpoints)).Discover());
    }
}

using System.Text.Json;
using PCMode.Engine;
using Xunit;

namespace PCMode.Engine.Tests;

/// <summary>An OpenRGB SDK server that exists only inside the test.</summary>
internal sealed class FakeOpenRgbClient : IOpenRgbClient
{
    /// <summary>Every SDK colour write, in the order it was requested.</summary>
    public List<(IReadOnlyList<KeyValuePair<int, string>> Devices, string Rgb, TimeSpan Within)> Writes { get; } = [];

    /// <summary>Every server probe timeout requested by the provider.</summary>
    public List<TimeSpan> Probes { get; } = [];

    /// <summary>What the next probe throws, if anything.</summary>
    public Exception? ProbeException { get; set; }

    /// <summary>What the next colour write throws, if anything.</summary>
    public Exception? WriteException { get; set; }

    public void Probe(TimeSpan within)
    {
        Probes.Add(within);

        if (ProbeException is not null)
        {
            throw ProbeException;
        }
    }

    public void SetColor(
        IReadOnlyList<KeyValuePair<int, string>> devices,
        string rgb,
        TimeSpan within,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (WriteException is not null)
        {
            throw WriteException;
        }

        Writes.Add(([.. devices], rgb, within));
    }
}

/// <summary>
/// Case lighting: optional, checked before it is sent, and honest about having
/// nothing to read back.
/// </summary>
public sealed class CaseRgbTests
{
    private const string LegacyOpenRgb = @"C:\OpenRGB\OpenRGB.exe";

    private static (CaseRgbProvider Provider, FakeOpenRgbClient Client) Configured(string? settings)
    {
        var client = new FakeOpenRgbClient();
        var provider = new CaseRgbProvider(client);

        if (settings is null)
        {
            Assert.True(provider.TryConfigure(null, out string? nothing), nothing);
            return (provider, client);
        }

        using JsonDocument document = JsonDocument.Parse(settings);

        Assert.True(provider.TryConfigure(document.RootElement.Clone(), out string? problem), problem);

        return (provider, client);
    }

    private static (CaseRgbProvider Provider, FakeOpenRgbClient Client) Lighting() =>
        Configured(
            $$"""
            {
              "enabled": true,
              "executable": "{{LegacyOpenRgb.Replace("\\", "\\\\")}}",
              "devices": { "1": "Static", "0": "Direct" }
            }
            """
        );

    [Fact]
    public void Is_switched_off_on_a_machine_that_never_asked_for_it()
    {
        (CaseRgbProvider dark, FakeOpenRgbClient client) = Configured(null);

        Assert.False(dark.Enabled);
        Assert.Equal(AvailabilityState.Disabled, dark.AvailabilityOf("0020FF").State);
        Assert.Throws<InvalidOperationException>(() => dark.Apply("0020FF", CancellationToken.None));
        Assert.Empty(client.Probes);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public void Is_unavailable_when_the_external_OpenRGB_SDK_server_cannot_be_reached()
    {
        (CaseRgbProvider provider, FakeOpenRgbClient client) = Lighting();
        client.ProbeException = new InvalidOperationException("connection refused");

        ResourceAvailability availability = provider.AvailabilityOf("0020FF");

        Assert.Equal(AvailabilityState.Unavailable, availability.State);
        Assert.Contains("localhost:6742", availability.Explain(), StringComparison.Ordinal);
        Assert.Contains("connection refused", availability.Explain(), StringComparison.Ordinal);
        Assert.Single(client.Probes);
    }

    [Fact]
    public void Sends_every_device_in_number_order_through_the_SDK_client()
    {
        // The provider sorts configured controller indices so the same file
        // produces the same SDK write every time. Modes remain per-device.
        (CaseRgbProvider provider, FakeOpenRgbClient client) = Lighting();

        provider.Apply("0020FF", CancellationToken.None);

        (IReadOnlyList<KeyValuePair<int, string>> devices, string rgb, TimeSpan within) = Assert.Single(client.Writes);

        Assert.Equal("0020FF", rgb);
        Assert.Equal(TimeSpan.FromSeconds(4), within);
        Assert.Equal([new KeyValuePair<int, string>(0, "Direct"), new KeyValuePair<int, string>(1, "Static")], devices);
    }

    [Fact]
    public void Legacy_executable_setting_remains_accepted_but_is_not_used_for_transport()
    {
        (CaseRgbProvider provider, FakeOpenRgbClient client) = Lighting();

        Assert.Equal(LegacyOpenRgb, provider.Executable);

        provider.Apply("0020FF", CancellationToken.None);

        Assert.Single(client.Writes);
    }

    [Theory]
    [InlineData("0020FF")]
    [InlineData("ffffff")]
    public void Takes_six_hex_digits(string colour)
    {
        (CaseRgbProvider provider, _) = Lighting();

        Assert.True(provider.IsValidValue(colour, out _));
    }

    [Theory]
    [InlineData("#0020FF", "#")]
    [InlineData("0020F", "six-digit")]
    [InlineData("blue", "six-digit")]
    [InlineData("", "no colour")]
    public void Refuses_malformed_RGB_targets(string colour, string mentions)
    {
        (CaseRgbProvider provider, _) = Lighting();

        Assert.False(provider.IsValidValue(colour, out string? problem));
        Assert.Contains(mentions, problem);
    }

    [Fact]
    public void Verifies_by_reading_back_through_the_SDK_client()
    {
        // The SDK client can read a controller's current mode and colour, so the
        // provider verifies against it rather than trusting that Apply returned.
        // This fake does not implement ReadColor, so a fresh read still comes
        // back null -- that is what "cannot be read reliably" looks like.
        (CaseRgbProvider provider, _) = Lighting();

        provider.Apply("0020FF", CancellationToken.None);

        Assert.Null(provider.Read());
        Assert.True(provider.PolicyFor("0020FF").Verify);
        Assert.Equal(1, provider.PolicyFor("0020FF").MaxAttempts);
    }

    [Fact]
    public void Reports_an_SDK_write_failure_with_the_requested_colour_and_original_problem()
    {
        (CaseRgbProvider provider, FakeOpenRgbClient client) = Lighting();
        client.WriteException = new InvalidOperationException("no such controller");

        Exception failure = Assert.Throws<InvalidOperationException>(() =>
            provider.Apply("0020FF", CancellationToken.None)
        );

        Assert.Contains("0020FF", failure.Message, StringComparison.Ordinal);
        Assert.Contains("no such controller", failure.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(failure.InnerException);
    }

    [Fact]
    public void Preserves_cancellation_instead_of_wrapping_it_as_an_OpenRGB_failure()
    {
        (CaseRgbProvider provider, _) = Lighting();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => provider.Apply("0020FF", cancellation.Token));
    }

    [Fact]
    public void Refuses_being_switched_on_with_nothing_to_colour()
    {
        var provider = new CaseRgbProvider(new FakeOpenRgbClient());

        using JsonDocument document = JsonDocument.Parse("""{ "enabled": true, "devices": {} }""");

        Assert.False(provider.TryConfigure(document.RootElement.Clone(), out string? problem));
        Assert.Contains("no devices", problem);
    }

    [Fact]
    public void Leaves_a_half_written_section_alone_while_it_is_switched_off()
    {
        // A disabled optional provider should tolerate an incomplete device list.
        (CaseRgbProvider provider, _) = Configured("""{ "enabled": false, "devices": {} }""");

        Assert.False(provider.Enabled);
    }
}

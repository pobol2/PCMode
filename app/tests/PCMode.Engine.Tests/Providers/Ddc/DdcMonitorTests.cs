using System.Text.Json;

namespace PCMode.Engine.Tests;

/// <summary>
/// DDC/CI brightness and input: bounded, honest about unsupported controls, and
/// never left to guess when a monitor did not answer.
/// </summary>
public sealed class DdcMonitorTests
{
    private const string DevicePath =
        @"\\?\DISPLAY#GSM5B09#5&39b3a707&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string Alias = "Left";

    private static readonly DdcCapabilities Both = new(
        new Dictionary<DdcVcpCode, DdcVcpCapability>
        {
            [DdcVcpCode.Brightness] = new(DdcVcpCode.Brightness, []),
            [DdcVcpCode.InputSource] = new(DdcVcpCode.InputSource, [0x0F, 0x11, 0x12]),
        }
    );

    private static readonly DdcCapabilities BrightnessOnly = new(
        new Dictionary<DdcVcpCode, DdcVcpCapability> { [DdcVcpCode.Brightness] = new(DdcVcpCode.Brightness, []) }
    );

    // ---- configuration -------------------------------------------------------

    [Fact]
    public void A_provider_that_has_never_been_configured_is_disabled_and_never_touches_the_bus()
    {
        var monitors = new FakeDdcMonitors();
        var provider = new DdcBrightnessProvider(new DdcMonitorConnection(monitors, Alias));

        ResourceAvailability availability = provider.AvailabilityOf("50");

        Assert.Equal(AvailabilityState.Disabled, availability.State);
        Assert.Contains($"no DDC/CI monitor '{Alias}'", availability.Explain(), StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => provider.Apply("50", CancellationToken.None));
        Assert.Empty(monitors.Writes);
    }

    [Fact]
    public void Configuring_with_no_settings_at_all_refuses_by_name_rather_than_going_quietly_disabled()
    {
        // Reachable only by constructing a connection directly, never through
        // EngineBootstrap: a provider built there exists only because its alias
        // was already found in the same document TryConfigure is about to read,
        // so the two can never disagree. A caller that manages to ask anyway
        // gets a clear refusal rather than a silently disabled resource that
        // would look identical to one deliberately left unconfigured.
        var monitors = new FakeDdcMonitors();
        var provider = new DdcBrightnessProvider(new DdcMonitorConnection(monitors, Alias));

        Assert.False(provider.TryConfigure(null, out string? problem));
        Assert.Contains($"does not define monitor '{Alias}'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_alias_missing_from_the_configured_map_is_refused_by_name()
    {
        var monitors = new FakeDdcMonitors();
        var provider = new DdcBrightnessProvider(new DdcMonitorConnection(monitors, Alias));

        JsonElement settings = Section(("Right", DevicePath));

        Assert.False(provider.TryConfigure(settings, out string? problem));
        Assert.Contains($"does not define monitor '{Alias}'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_section_is_refused_rather_than_silently_ignored()
    {
        var monitors = new FakeDdcMonitors();
        var provider = new DdcBrightnessProvider(new DdcMonitorConnection(monitors, Alias));

        using JsonDocument document = JsonDocument.Parse("""{ "Left": "" }""");

        Assert.False(provider.TryConfigure(document.RootElement.Clone(), out string? problem));
        Assert.Contains("is empty", problem, StringComparison.Ordinal);
    }

    // ---- availability: capability-driven, not just configuration-driven ------

    [Fact]
    public void A_configured_monitor_that_never_answers_is_unavailable_not_unknown_forever()
    {
        var monitors = new FakeDdcMonitors { TimeOutCapabilities = true };
        DdcBrightnessProvider provider = Configured(monitors);

        ResourceAvailability availability = provider.AvailabilityOf("50");

        Assert.Equal(AvailabilityState.Unavailable, availability.State);
        Assert.Contains("did not answer", availability.Explain(), StringComparison.Ordinal);
        Assert.Contains("asleep, disconnected", availability.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_monitor_present_but_not_reporting_brightness_hides_the_control()
    {
        var monitors = new FakeDdcMonitors();
        monitors.AddMonitor(DevicePath, DdcCapabilities.None);

        DdcBrightnessProvider provider = Configured(monitors);

        ResourceAvailability availability = provider.AvailabilityOf("50");

        Assert.Equal(AvailabilityState.Unavailable, availability.State);
        Assert.Contains("does not report DDC/CI brightness", availability.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_monitor_that_reports_brightness_is_available()
    {
        var monitors = new FakeDdcMonitors();
        monitors.AddMonitor(DevicePath, BrightnessOnly);

        DdcBrightnessProvider provider = Configured(monitors);

        Assert.Equal(AvailabilityState.Available, provider.AvailabilityOf("50").State);
    }

    [Fact]
    public void Input_is_unavailable_on_a_monitor_that_only_reports_brightness()
    {
        var monitors = new FakeDdcMonitors();
        monitors.AddMonitor(DevicePath, BrightnessOnly);

        DdcInputProvider provider = ConfiguredInput(monitors);

        ResourceAvailability availability = provider.AvailabilityOf("HDMI1");

        Assert.Equal(AvailabilityState.Unavailable, availability.State);
        Assert.Contains("does not report DDC/CI input-select", availability.Explain(), StringComparison.Ordinal);
    }

    // ---- brightness: validation, apply, read, timeout -------------------------

    [Theory]
    [InlineData("0")]
    [InlineData("100")]
    [InlineData("50")]
    public void Accepts_whole_numbers_from_0_to_100(string value)
    {
        var monitors = new FakeDdcMonitors();
        DdcBrightnessProvider provider = Configured(monitors);

        Assert.True(provider.IsValidValue(value, out _));
    }

    [Theory]
    [InlineData("101")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("5.5")]
    public void Refuses_anything_outside_0_to_100(string value)
    {
        var monitors = new FakeDdcMonitors();
        DdcBrightnessProvider provider = Configured(monitors);

        Assert.False(provider.IsValidValue(value, out string? problem));
        Assert.Contains("0 to 100", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_writes_the_value_and_read_reports_it_back()
    {
        var monitors = new FakeDdcMonitors();
        monitors.AddMonitor(DevicePath, BrightnessOnly);

        DdcBrightnessProvider provider = Configured(monitors);

        provider.Apply("62", CancellationToken.None);

        Assert.Equal((DevicePath, DdcVcpCode.Brightness, 62u), Assert.Single(monitors.Writes));
        Assert.Equal("62", provider.Read());
    }

    [Fact]
    public void A_read_from_a_monitor_that_does_not_answer_is_null_not_a_throw()
    {
        var monitors = new FakeDdcMonitors { TimeOutReads = true };
        monitors.AddMonitor(DevicePath, BrightnessOnly);

        DdcBrightnessProvider provider = Configured(monitors);

        Assert.Null(provider.Read());
    }

    [Fact]
    public void A_monitor_that_silently_clamps_a_write_reads_back_the_clamped_value_not_the_requested_one()
    {
        // Some panels accept an out-of-range or oddly-stepped brightness command
        // without complaint and simply store the nearest value they actually
        // support. Apply must not assume the write landed exactly as sent: Read
        // is what says what actually happened.
        var monitors = new FakeDdcMonitors();
        monitors.AddMonitor(DevicePath, BrightnessOnly);
        monitors.ClampsWritesTo[(DevicePath, DdcVcpCode.Brightness)] = 80;

        DdcBrightnessProvider provider = Configured(monitors);

        provider.Apply("95", CancellationToken.None);

        Assert.Equal((DevicePath, DdcVcpCode.Brightness, 95u), Assert.Single(monitors.Writes));
        Assert.Equal("80", provider.Read());
    }

    [Fact]
    public void Apply_to_a_monitor_that_does_not_answer_throws_with_the_reason()
    {
        var monitors = new FakeDdcMonitors { TimeOutWrites = true };
        monitors.AddMonitor(DevicePath, BrightnessOnly);

        DdcBrightnessProvider provider = Configured(monitors);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            provider.Apply("50", CancellationToken.None)
        );

        Assert.Contains("did not respond", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Convergence_policy_verifies_by_readback()
    {
        var monitors = new FakeDdcMonitors();
        DdcBrightnessProvider provider = Configured(monitors);

        ConvergencePolicy policy = provider.PolicyFor("50");

        Assert.True(policy.Verify);
        Assert.True(policy.IsUsable(out _));
    }

    // ---- input: names, raw codes, and the monitor getting the last word -------

    [Theory]
    [InlineData("HDMI1", 0x11u)]
    [InlineData("hdmi2", 0x12u)]
    [InlineData("DisplayPort1", 0x0Fu)]
    [InlineData("0x11", 0x11u)]
    [InlineData("17", 0x11u)]
    public void Resolves_known_names_and_raw_codes(string value, uint expected)
    {
        var monitors = new FakeDdcMonitors();
        monitors.AddMonitor(DevicePath, Both);

        DdcInputProvider provider = ConfiguredInput(monitors);

        Assert.True(provider.IsValidValue(value, out _));

        provider.Apply(value, CancellationToken.None);

        Assert.Equal((DevicePath, DdcVcpCode.InputSource, expected), Assert.Single(monitors.Writes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ethernet")]
    [InlineData("0x100")]
    [InlineData("0")]
    public void Refuses_values_that_are_not_a_name_or_a_byte_sized_code(string value)
    {
        var monitors = new FakeDdcMonitors();
        DdcInputProvider provider = ConfiguredInput(monitors);

        Assert.False(provider.IsValidValue(value, out string? problem));
        Assert.Contains("is not a DDC/CI input", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_refuses_a_value_the_monitor_did_not_enumerate_as_supported()
    {
        var monitors = new FakeDdcMonitors();
        monitors.AddMonitor(DevicePath, Both); // enumerates 0x0F, 0x11, 0x12 only

        DdcInputProvider provider = ConfiguredInput(monitors);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            provider.Apply("DVI1", CancellationToken.None)
        ); // 0x03, not in the list

        Assert.Contains("does not list", failure.Message, StringComparison.Ordinal);
        Assert.Empty(monitors.Writes);
    }

    [Fact]
    public void Apply_proceeds_when_the_monitor_declares_the_feature_but_names_no_values()
    {
        // vcp(60) with no parenthesised list: supported, but which values are
        // real is not knowable from the capability string. Uncertain is not the
        // same as refused.
        var monitors = new FakeDdcMonitors();

        monitors.AddMonitor(
            DevicePath,
            new DdcCapabilities(
                new Dictionary<DdcVcpCode, DdcVcpCapability>
                {
                    [DdcVcpCode.InputSource] = new(DdcVcpCode.InputSource, []),
                }
            )
        );

        DdcInputProvider provider = ConfiguredInput(monitors);

        provider.Apply("HDMI1", CancellationToken.None);

        Assert.Single(monitors.Writes);
    }

    [Fact]
    public void Read_names_a_known_code_and_falls_back_to_hex_for_an_unnamed_one()
    {
        var monitors = new FakeDdcMonitors();
        monitors.AddMonitor(DevicePath, Both);
        monitors.SetCurrentValue(DevicePath, DdcVcpCode.InputSource, 0x11);

        DdcInputProvider provider = ConfiguredInput(monitors);

        Assert.Equal("HDMI1", provider.Read());

        monitors.SetCurrentValue(DevicePath, DdcVcpCode.InputSource, 0x1B);

        Assert.Equal("0x1B", provider.Read());
    }

    // ---- health diagnostics ----------------------------------------------------

    [Fact]
    public void Health_detail_reports_support_even_when_it_is_false()
    {
        var monitors = new FakeDdcMonitors();
        monitors.AddMonitor(DevicePath, DdcCapabilities.None);

        DdcBrightnessProvider provider = Configured(monitors);

        Assert.Contains("supported: False", provider.HealthDetail("50"), StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------

    private static DdcBrightnessProvider Configured(FakeDdcMonitors monitors)
    {
        var provider = new DdcBrightnessProvider(new DdcMonitorConnection(monitors, Alias));
        Assert.True(provider.TryConfigure(Section((Alias, DevicePath)), out string? problem), problem);
        return provider;
    }

    private static DdcInputProvider ConfiguredInput(FakeDdcMonitors monitors)
    {
        var provider = new DdcInputProvider(new DdcMonitorConnection(monitors, Alias));
        Assert.True(provider.TryConfigure(Section((Alias, DevicePath)), out string? problem), problem);
        return provider;
    }

    private static JsonElement Section(params (string Alias, string DevicePath)[] monitors)
    {
        var body = new Dictionary<string, string>();

        foreach ((string alias, string devicePath) in monitors)
        {
            body[alias] = devicePath;
        }

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(body));
        return document.RootElement.Clone();
    }
}

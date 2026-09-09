using System.Text.Json;

namespace PCMode.Engine.Tests;

/// <summary>A notification setting nobody's machine has to lend us.</summary>
internal sealed class FakeNotificationSetting : INotificationSetting
{
    /// <summary>Whether toasts are allowed, or null for a lookup that failed.</summary>
    public bool? Allowed { get; set; } = true;

    /// <summary>Every value written, in order.</summary>
    public List<bool> Writes { get; } = [];

    public int Refreshes { get; private set; }

    /// <summary>Thrown from Refresh, when set.</summary>
    public Exception? RefreshFails { get; set; }

    public bool? ToastsAllowed() => Allowed;

    public void AllowToasts(bool allowed)
    {
        Writes.Add(allowed);
        Allowed = allowed;
    }

    public void Refresh()
    {
        Refreshes++;

        if (RefreshFails is not null)
        {
            throw RefreshFails;
        }
    }
}

/// <summary>
/// Do Not Disturb: opt-in, and the right way round.
/// </summary>
public sealed class DoNotDisturbTests
{
    private static (DoNotDisturbProvider Provider, FakeNotificationSetting Setting) Configured(string? settings)
    {
        var setting = new FakeNotificationSetting();
        var provider = new DoNotDisturbProvider(setting);

        if (settings is null)
        {
            Assert.True(provider.TryConfigure(null, out string? nothing), nothing);
            return (provider, setting);
        }

        using JsonDocument document = JsonDocument.Parse(settings);

        Assert.True(provider.TryConfigure(document.RootElement.Clone(), out string? problem), problem);

        return (provider, setting);
    }

    private static (DoNotDisturbProvider Provider, FakeNotificationSetting Setting) On() =>
        Configured("""{ "enabled": true }""");

    [Fact]
    public void Is_switched_off_on_a_machine_that_never_asked_for_it()
    {
        // It writes a Windows setting Microsoft does not document. A resource
        // that did that on every machine that installed this would be making a
        // change nobody consented to, somewhere nobody would think to look.
        (DoNotDisturbProvider quiet, FakeNotificationSetting setting) = Configured(null);

        Assert.False(quiet.Enabled);
        Assert.Equal(AvailabilityState.Disabled, quiet.AvailabilityOf("On").State);

        // And it does not even look. A disabled resource that read the setting
        // anyway would be inspecting a machine that told it not to touch this.
        Assert.Null(quiet.Read());
        Assert.Throws<InvalidOperationException>(() => quiet.Apply("On", CancellationToken.None));
        Assert.Empty(setting.Writes);
    }

    [Fact]
    public void Is_available_once_the_machine_has_opted_in()
    {
        (DoNotDisturbProvider loud, _) = On();

        Assert.True(loud.Enabled);
        Assert.Equal(AvailabilityState.Available, loud.AvailabilityOf("On").State);
    }

    [Fact]
    public void On_means_suppressing_notifications_and_not_the_reverse()
    {
        // The setting says whether toasts are allowed; Do Not Disturb is the
        // opposite of it. One inversion, in one place: two places to flip a
        // boolean is one place to get it backwards, and backwards here means the
        // mode that asked for quiet turning notifications on.
        (DoNotDisturbProvider provider, FakeNotificationSetting setting) = On();

        provider.Apply("On", CancellationToken.None);

        Assert.Equal([false], setting.Writes);
        Assert.Equal("On", provider.Read());

        provider.Apply("Off", CancellationToken.None);

        Assert.Equal([false, true], setting.Writes);
        Assert.Equal("Off", provider.Read());
    }

    [Fact]
    public void A_near_miss_writes_nothing_at_all()
    {
        // 'Onn' used to mean "not On", which wrote the opposite of what was meant
        // and left the machine noisy in the one mode that asked for silence.
        (DoNotDisturbProvider provider, FakeNotificationSetting setting) = On();

        Assert.False(provider.IsValidValue("Onn", out _));
        Assert.Throws<ArgumentException>(() => provider.Apply("Onn", CancellationToken.None));
        Assert.Empty(setting.Writes);
    }

    [Fact]
    public void A_setting_that_was_never_written_is_the_Windows_default()
    {
        (DoNotDisturbProvider provider, FakeNotificationSetting setting) = On();

        setting.Allowed = null;

        // Null here is a lookup that failed, not an absence -- the implementation
        // answers true where nothing has ever been written. So this is "cannot
        // say", which the engine reads as "apply it anyway".
        Assert.Null(provider.Read());
    }

    [Fact]
    public void A_nudge_that_failed_does_not_fail_a_write_that_worked()
    {
        (DoNotDisturbProvider provider, FakeNotificationSetting setting) = On();

        setting.RefreshFails = new UnauthorizedAccessException("not permitted.");

        provider.Apply("On", CancellationToken.None);

        Assert.Equal([false], setting.Writes);
        Assert.Equal(1, setting.Refreshes);
        Assert.Equal("On", provider.Read());
    }

    [Fact]
    public void Leaves_the_setting_alone_when_asked_not_to_nudge_it()
    {
        (DoNotDisturbProvider provider, FakeNotificationSetting setting) = Configured(
            """{ "enabled": true, "refresh": false }"""
        );

        provider.Apply("On", CancellationToken.None);

        Assert.Equal([false], setting.Writes);
        Assert.Equal(0, setting.Refreshes);
    }

    [Fact]
    public void Refuses_a_settings_block_it_does_not_understand()
    {
        // Ignored is worse than refused: a dropped field is a setting the user
        // believes they have written and has not.
        var provider = new DoNotDisturbProvider(new FakeNotificationSetting());

        using JsonDocument document = JsonDocument.Parse("""{ "enabled": true, "enbaled": true }""");

        Assert.False(provider.TryConfigure(document.RootElement.Clone(), out string? problem));
        Assert.Contains("enbaled", problem);
    }
}

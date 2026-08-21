namespace PCMode.Engine.Tests;

/// <summary>
/// The first real provider, exercised through its store rather than through the
/// registry of the machine running the tests.
/// </summary>
public sealed class WindowsThemeProviderTests
{
    [Theory]
    [InlineData("Light")]
    [InlineData("dark")]
    [InlineData("  Dark  ")]
    public void Takes_a_theme_however_it_was_typed(string value)
    {
        Assert.True(new WindowsThemeProvider(new FakeStore()).IsValidValue(value, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Purple")]
    [InlineData("1")]
    public void Refuses_anything_that_is_not_a_theme(string value)
    {
        var provider = new WindowsThemeProvider(new FakeStore());

        Assert.False(provider.IsValidValue(value, out string? problem));

        // Names both alternatives: the closed list is two words long, so
        // sending somebody to look it up would be gratuitous.
        Assert.Contains("Light", problem!, StringComparison.Ordinal);
        Assert.Contains("Dark", problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_the_theme_the_machine_is_in()
    {
        Assert.Equal("Light", new WindowsThemeProvider(new FakeStore { Light = true }).Read());
        Assert.Equal("Dark", new WindowsThemeProvider(new FakeStore { Light = false }).Read());
    }

    [Fact]
    public void Says_nothing_rather_than_guessing_when_it_cannot_read()
    {
        // Null is what the engine takes as "apply it anyway".
        Assert.Null(new WindowsThemeProvider(new FakeStore { Light = null }).Read());
    }

    [Fact]
    public void Sets_the_theme_it_was_given()
    {
        var store = new FakeStore { Light = true };

        new WindowsThemeProvider(store).Apply("Dark", CancellationToken.None);

        Assert.Equal(false, store.Light);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public void Is_reversible_by_applying_the_other_value()
    {
        // The reason this was chosen as the first real provider: undoing it is
        // the same operation with the other word, with nothing left behind.
        var store = new FakeStore { Light = true };
        var provider = new WindowsThemeProvider(store);

        provider.Apply("Dark", CancellationToken.None);
        provider.Apply("Light", CancellationToken.None);

        Assert.Equal(true, store.Light);
    }

    [Fact]
    public void Refuses_to_write_a_value_it_was_never_validated_for()
    {
        // Unreachable through the engine, which validates first. A provider
        // that trusted its caller would write something meaningless the one
        // time somebody called it directly.
        var store = new FakeStore();

        Assert.Throws<ArgumentException>(() => new WindowsThemeProvider(store).Apply("Purple", CancellationToken.None));

        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void Answers_to_the_key_the_configuration_uses()
    {
        // The seeded configuration names "theme". A rename here without one
        // there would give a fresh machine a configuration it refuses.
        Assert.Equal("theme", new WindowsThemeProvider(new FakeStore()).Key);
    }

    [Theory]
    [InlineData(true, true, true)]      // both light
    [InlineData(false, false, false)]   // both dark
    [InlineData(true, false, null)]     // light windows on a dark taskbar
    [InlineData(false, true, null)]     // and the other way round
    [InlineData(true, null, null)]      // one of them could not be read
    [InlineData(null, null, null)]
    public void Calls_a_half_written_theme_no_theme_at_all(bool? apps, bool? system, bool? expected)
    {
        // The write sets both values, so the readback has to check both. Reading
        // only the application preference would report a desktop with a dark
        // taskbar and light windows as a finished theme -- and being a readback,
        // that is also what would tell the engine the transition had converged.
        //
        // Disagreement answers null rather than picking a side, because null
        // already means "not there yet", which is exactly what a half-written
        // preference is.
        Assert.Equal(expected, RegistryThemeStore.Agreed(apps, system));
    }

    [Fact]
    public async Task Applies_a_theme_again_when_only_half_of_it_took()
    {
        // The failure this catches is not a slow one. Something put half the
        // preference back, and a provider that only ever writes cannot see that
        // at all.
        using var root = new TemporaryRoot();

        root.Write(
            """{"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[{"id":"movie","name":"Movie","resources":{"theme":"Dark"}}]}]}""");

        var store = new HalfTakingStore();

        IReadOnlyList<IModeResourceProvider> providers = [new WindowsThemeProvider(store)];

        var engine = new ModeEngine(
            ModeConfiguration.LoadOrCreate(root.Path, providers),
            new ModeStateStore(root.StateFile),
            providers,
            new FakeTime());

        TransitionOutcome outcome = await engine.ApplyAsync("Movie");

        Assert.Equal(TransitionStatus.Succeeded, outcome.Status);
        Assert.Equal(2, Assert.Single(outcome.Steps).Attempts);
        Assert.Equal(2, store.Writes);
    }

    /// <summary>
    /// A machine on which the first write lands on only one of the two values.
    /// </summary>
    /// <remarks>
    /// Which reads back as no theme at all, because that is what a preference
    /// disagreeing with itself is. A provider that read only the application
    /// half would have called this converged after the first write, with the
    /// taskbar still the other colour.
    /// </remarks>
    private sealed class HalfTakingStore : IThemeStore
    {
        private bool? _agreed = true;

        public int Writes { get; private set; }

        public bool? ReadUsesLightTheme() => _agreed;

        public void WriteUsesLightTheme(bool light)
        {
            Writes++;
            _agreed = Writes >= 2 ? light : null;
        }
    }

    private sealed class FakeStore : IThemeStore
    {
        public bool? Light { get; set; } = true;

        public int Writes { get; private set; }

        public bool? ReadUsesLightTheme() => Light;

        public void WriteUsesLightTheme(bool light)
        {
            Light = light;
            Writes++;
        }
    }
}

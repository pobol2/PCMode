using System.Text.Json;

namespace PCMode.Engine.Tests;

/// <summary>
/// Where the pointer ends up, and everything the provider refuses to guess.
/// </summary>
/// <remarks>
/// Nothing here touches the real pointer or the real screen. Both are behind
/// seams for exactly that reason: a test that moved the mouse would take it away
/// from whoever was using the machine, and a test that measured the real display
/// would pass or fail depending on the monitor it happened to run on.
/// </remarks>
public sealed class CursorProviderTests
{
    private static readonly DisplayRect Screen = new(0, 0, 1920, 1080);

    private const string TwoPositions = """
        {
          "positions": {
            "Center": { "xPercent": 50, "yPercent": 50 },
            "Corner": { "xFromRight": 2, "yFromBottom": 200 }
          }
        }
        """;

    // ---- where the pointer goes --------------------------------------------

    [Fact]
    public void Resolves_a_percentage_against_the_display()
    {
        var cursor = new FakeCursor();

        Provider(TwoPositions, cursor).Apply("Center", default);

        Assert.Equal([(960, 540)], cursor.Moves);
    }

    [Fact]
    public void Resolves_an_edge_offset_from_the_far_edge()
    {
        // The reason both rules exist. "Two pixels in from the right" has to
        // mean the same thing on a 1080p television and a 4K one, which a
        // percentage cannot express.
        var cursor = new FakeCursor();

        Provider(TwoPositions, cursor).Apply("Corner", default);

        Assert.Equal([(1918, 880)], cursor.Moves);
    }

    [Fact]
    public void Measures_from_the_display_it_was_given()
    {
        // The offsets are carried rather than assumed to be zero. A rectangle
        // that does not start at the origin is the ordinary case for every
        // monitor except the primary one, and arithmetic that ignored it would
        // put the pointer on a different screen entirely.
        var cursor = new FakeCursor();

        Provider(TwoPositions, cursor, new DisplayRect(-1920, 200, 1920, 1080)).Apply("Center", default);

        Assert.Equal([(-960, 740)], cursor.Moves);
    }

    [Theory]
    [InlineData("""{ "xPercent": 100, "yPercent": 100 }""", 1919, 1079)]
    [InlineData("""{ "xFromRight": 5000, "yFromBottom": 5000 }""", 0, 0)]
    public void Keeps_the_pointer_on_the_display(string rules, int x, int y)
    {
        // One pixel past the edge is not a near miss. On a desktop with a second
        // monitor it is on the second monitor, where the position looks wrong
        // rather than out of range -- and the user has no way to tell those
        // apart from where the pointer ended up.
        var cursor = new FakeCursor();

        Provider($$"""{ "positions": { "Edge": {{rules}} } }""", cursor).Apply("Edge", default);

        Assert.Equal([(x, y)], cursor.Moves);
    }

    // ---- what it refuses to guess -------------------------------------------

    [Theory]
    [InlineData("""{ "yPercent": 50 }""", "xPercent or xFromRight")]
    [InlineData("""{ "xPercent": 50 }""", "yPercent or yFromBottom")]
    [InlineData("{ }", "xPercent or xFromRight")]
    public void Refuses_a_position_that_is_missing_a_rule(string rules, string expected)
    {
        // The defect this exists to stop: a position with no X rule quietly
        // becomes zero, which is the left edge, which looks exactly like
        // somebody meaning it. It can sit in a configuration being wrong for
        // months without ever looking wrong.
        string? problem = Configure(
            new CursorProvider(new FakeCursor(), new FakeDisplay(Screen)),
            $$"""{ "positions": { "Half": {{rules}} } }"""
        );

        Assert.NotNull(problem);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
        Assert.Contains("Half", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "xPercent": 50, "xFromRight": 2, "yPercent": 50 }""", "xPercent and xFromRight")]
    [InlineData("""{ "xPercent": 50, "yPercent": 50, "yFromBottom": 2 }""", "yPercent and yFromBottom")]
    public void Refuses_two_rules_for_one_axis(string rules, string expected)
    {
        // One of the two is being ignored and nothing says which. Picking a
        // winner silently is how a configuration comes to mean something other
        // than what it says.
        string? problem = Configure(
            new CursorProvider(new FakeCursor(), new FakeDisplay(Screen)),
            $$"""{ "positions": { "Both": {{rules}} } }"""
        );

        Assert.NotNull(problem);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "xPecent": 50, "yPercent": 50 }""", "xPecent")]
    [InlineData("""{ "xPercent": 101, "yPercent": 50 }""", "101")]
    [InlineData("""{ "xPercent": -1, "yPercent": 50 }""", "-1")]
    [InlineData("""{ "xFromRight": -2, "yPercent": 50 }""", "-2")]
    [InlineData("""{ "xPercent": "50", "yPercent": 50 }""", "not a number")]
    public void Refuses_a_rule_it_cannot_turn_into_a_coordinate(string rules, string expected)
    {
        // A misspelled rule is the interesting one: without this it is simply
        // absent, and the position it belonged to silently becomes an edge.
        string? problem = Configure(
            new CursorProvider(new FakeCursor(), new FakeDisplay(Screen)),
            $$"""{ "positions": { "Odd": {{rules}} } }"""
        );

        Assert.NotNull(problem);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_mode_that_names_a_position_that_does_not_exist()
    {
        CursorProvider provider = Provider(TwoPositions);

        Assert.False(provider.IsValidValue("Middle", out string? problem));

        // Naming the ones that do. The answer is a short list the user wrote
        // themselves, and making them go and find it is unkind.
        Assert.Contains("Center", problem!, StringComparison.Ordinal);
        Assert.Contains("Corner", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_position_when_none_have_been_defined()
    {
        var provider = new CursorProvider(new FakeCursor(), new FakeDisplay(Screen));

        Assert.True(provider.TryConfigure(settings: null, out string? configured));
        Assert.Null(configured);

        // Missing settings are not a failure by themselves -- a user who wants
        // no cursor behaviour writes no positions. It only becomes a problem
        // when a mode asks for one, and then the mode is what gets blamed.
        Assert.False(provider.IsValidValue("Center", out string? problem));
        Assert.Contains(CursorProvider.SettingsPath, problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_two_positions_that_differ_only_in_case()
    {
        // A mode looks a position up case-insensitively, so one of these could
        // never be selected.
        string? problem = Configure(
            new CursorProvider(new FakeCursor(), new FakeDisplay(Screen)),
            """
            {
              "positions": {
                "Corner": { "xPercent": 1, "yPercent": 1 },
                "corner": { "xPercent": 9, "yPercent": 9 }
              }
            }
            """
        );

        Assert.NotNull(problem);
        Assert.Contains("more than once", problem, StringComparison.Ordinal);
    }

    // ---- once, and then left alone -----------------------------------------

    [Fact]
    public void Never_reports_where_the_pointer_is()
    {
        // Not an omission. A cursor position read back is true for an instant:
        // the user moves the mouse a moment later, entirely correctly, and the
        // resource would look unsatisfied. Anything built on that answer would
        // end up fighting the person holding the mouse.
        Assert.Null(Provider(TwoPositions).Read());
    }

    [Fact]
    public async Task Parks_the_pointer_once_per_transition_and_does_not_defend_it()
    {
        using var root = new TemporaryRoot();
        var cursor = new FakeCursor();

        ModeEngine engine = Engine(root, cursor);

        TransitionOutcome first = await engine.ApplyAsync("Movie");

        Assert.True(first.Succeeded);
        Assert.Equal([(1918, 880)], cursor.Moves);

        // The user then moves the mouse, which nothing here notices or undoes.
        cursor.Moves.Clear();

        // Applying the same mode again dispatches again, because the provider
        // never claims the pointer is already correct. One move per transition,
        // and no move in between.
        TransitionOutcome second = await engine.ApplyAsync("Movie");

        Assert.True(second.Succeeded);
        Assert.Equal([(1918, 880)], cursor.Moves);
    }

    [Fact]
    public async Task Reports_the_cursor_as_a_step_of_its_own()
    {
        using var root = new TemporaryRoot();
        // A real resource, with its own line in the report. A pointer that moved
        // as a side effect of the theme would be a change the transition never
        // mentioned.

        ModeEngine engine = Engine(root, new FakeCursor());

        TransitionOutcome outcome = await engine.ApplyAsync("Movie");

        TransitionStep step = Assert.Single(outcome.Steps, step => step.Resource == CursorProvider.ResourceKey);

        Assert.Equal(TransitionStepStatus.Applied, step.Status);
        Assert.Equal("Corner", step.Target);
        Assert.Equal("Cursor position", step.ResourceName);

        // Second of the two, so the theme is already right when the pointer is
        // parked rather than the desktop repainting under a moved cursor.
        Assert.Equal(["theme", CursorProvider.ResourceKey], outcome.Steps.Select(step => step.Resource));
    }

    [Fact]
    public async Task Fails_the_transition_when_the_pointer_cannot_be_moved()
    {
        using var root = new TemporaryRoot();

        var cursor = new FakeCursor { Fails = new InvalidOperationException("Windows refused to move the pointer.") };

        TransitionOutcome outcome = await Engine(root, cursor).ApplyAsync("Movie");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Contains("refused to move the pointer", outcome.Message, StringComparison.Ordinal);

        // And nothing is recorded, because the machine is not in the mode.
        Assert.False(outcome.StateRecorded);
        Assert.False(File.Exists(root.StateFile));
    }

    [Fact]
    public async Task Fails_the_transition_when_the_display_cannot_be_measured()
    {
        using var root = new TemporaryRoot();

        var cursor = new FakeCursor();
        var display = new FakeDisplay(Screen) { Fails = new InvalidOperationException("no primary display") };

        var provider = new CursorProvider(cursor, display);

        TransitionOutcome outcome = await Engine(root, provider).ApplyAsync("Movie");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Contains("no primary display", outcome.Message, StringComparison.Ordinal);

        // Refused before the pointer went anywhere, rather than parked at a
        // coordinate calculated from a screen size nobody could read.
        Assert.Empty(cursor.Moves);
    }

    // ---- helpers ------------------------------------------------------------

    private const string Document = """
        {
          "schemaVersion": 1,
          "providers": {
            "cursor": { "positions": { "Corner": { "xFromRight": 2, "yFromBottom": 200 } } }
          },
          "spaces": [
            { "id": "space", "name": "Space", "activities": [
              { "id": "movie", "name": "Movie", "resources": { "theme": "Dark", "cursor": "Corner" } }
            ] }
          ]
        }
        """;

    private static ModeEngine Engine(TemporaryRoot root, FakeCursor cursor) =>
        Engine(root, new CursorProvider(cursor, new FakeDisplay(Screen)));

    private static ModeEngine Engine(TemporaryRoot root, CursorProvider provider)
    {
        root.Write(Document);

        IReadOnlyList<IModeResourceProvider> providers =
        [
            new FakeProvider("theme", "Light") { DisplayName = "Windows theme" },
            provider,
        ];

        return new ModeEngine(
            ModeConfiguration.LoadOrCreate(root.Path, providers),
            new ModeStateStore(root.StateFile),
            providers,
            new FakeTime()
        );
    }

    private static CursorProvider Provider(string settings, FakeCursor? cursor = null, DisplayRect? bounds = null)
    {
        var provider = new CursorProvider(cursor ?? new FakeCursor(), new FakeDisplay(bounds ?? Screen));

        Assert.Null(Configure(provider, settings));

        return provider;
    }

    /// <summary>The refusal, or null when the settings were accepted.</summary>
    private static string? Configure(CursorProvider provider, string settings)
    {
        using JsonDocument document = JsonDocument.Parse(settings);

        bool accepted = provider.TryConfigure(document.RootElement, out string? problem);

        Assert.Equal(accepted, problem is null);

        return problem;
    }
}

/// <summary>A pointer that records where it was sent instead of going there.</summary>
internal sealed class FakeCursor : ICursorDevice
{
    public List<(int X, int Y)> Moves { get; } = [];

    public Exception? Fails { get; init; }

    public void MoveTo(int x, int y)
    {
        if (Fails is not null)
        {
            throw Fails;
        }

        Moves.Add((x, y));
    }
}

/// <summary>A screen of a fixed size, so a test does not depend on the monitor.</summary>
internal sealed class FakeDisplay(DisplayRect bounds) : IDisplaySurface
{
    public Exception? Fails { get; init; }

    public DisplayRect PrimaryBounds() => Fails is not null ? throw Fails : bounds;
}

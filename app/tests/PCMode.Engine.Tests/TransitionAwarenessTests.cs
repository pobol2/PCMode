namespace PCMode.Engine.Tests;

/// <summary>
/// What a provider is told about the rest of the transition.
/// </summary>
/// <remarks>
/// The engine's half of the rule an application depends on. Whether a running
/// program is stale is a question about the transition rather than about the
/// machine -- nothing about a process says which screen its window was placed on
/// -- so the fact has to be carried, and these are the guarantees it is carried
/// under.
/// </remarks>
public sealed class TransitionAwarenessTests
{
    private const string Document =
        """
        {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
          {"id":"movie","name":"Movie","resources":{"display":"TVOnly","player":"Running"}}]}]}
        """;

    [Fact]
    public async Task A_resource_is_told_what_the_transition_has_already_changed()
    {
        var display = new FakeProvider("display", "Desktop") { Allowed = ["Desktop", "TVOnly"] };
        var player = new Listener("player", "Stopped");

        using var root = new TemporaryRoot();

        TransitionOutcome outcome = await Run(root, display, player);

        Assert.True(outcome.Succeeded, outcome.Message);

        // Once, before its own step, and naming the display -- which did move.
        Assert.Equal(["display"], Assert.Single(player.Heard));
    }

    [Fact]
    public async Task A_resource_that_was_already_correct_is_not_reported_as_a_change()
    {
        using var root = new TemporaryRoot();
        // Otherwise every re-assert of a mode would restart every application in
        // it, because nothing in the mode would ever look untouched.
        var display = new FakeProvider("display", "TVOnly") { Allowed = ["Desktop", "TVOnly"] };
        var player = new Listener("player", "Stopped");


        TransitionOutcome outcome = await Run(root, display, player);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Empty(Assert.Single(player.Heard));
    }

    [Fact]
    public async Task A_dry_run_says_that_nothing_has_changed_because_nothing_has()
    {
        using var root = new TemporaryRoot();
        var display = new FakeProvider("display", "Desktop") { Allowed = ["Desktop", "TVOnly"] };
        var player = new Listener("player", "Stopped");


        TransitionOutcome outcome = await Run(root, display, player, dryRun: true);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Empty(Assert.Single(player.Heard));
        Assert.Empty(display.Applied);
        Assert.Empty(player.Applied);
    }

    [Fact]
    public async Task A_resource_never_carries_an_answer_from_one_transition_into_the_next()
    {
        using var root = new TemporaryRoot();
        // Told before every step, including the first, so a provider has nothing
        // to reset and cannot be left believing that a display which moved half
        // an hour ago has just moved.
        var display = new FakeProvider("display", "Desktop") { Allowed = ["Desktop", "TVOnly"] };
        var player = new Listener("player", "Stopped");


        ModeEngine engine = Build(root, display, player);

        Assert.True((await engine.ApplyAsync("Movie")).Succeeded);
        Assert.True((await engine.ApplyAsync("Movie")).Succeeded);

        Assert.Equal(2, player.Heard.Count);
        Assert.Equal(["display"], player.Heard[0]);
        Assert.Empty(player.Heard[1]);
    }

    [Fact]
    public async Task A_resource_that_refuses_to_hear_it_fails_rather_than_deciding_without_it()
    {
        using var root = new TemporaryRoot();
        // The one thing worse than restarting an application unnecessarily is
        // silently declining to restart one that needed it. There is no defined
        // meaning for "was not told", so the step fails with the machine no
        // further along.
        var display = new FakeProvider("display", "Desktop") { Allowed = ["Desktop", "TVOnly"] };

        var player = new Listener("player", "Stopped")
        {
            Deaf = new InvalidOperationException("it would not listen."),
        };


        TransitionOutcome outcome = await Run(root, display, player);

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Contains("it would not listen.", outcome.Message);
        Assert.Empty(player.Applied);
    }

    private static async Task<TransitionOutcome> Run(
        TemporaryRoot root,
        FakeProvider display,
        Listener player,
        bool dryRun = false) =>
        await Build(root, display, player).ApplyAsync("Movie", dryRun);

    private static ModeEngine Build(TemporaryRoot root, FakeProvider display, Listener player)
    {
        root.Write(Document);

        IReadOnlyList<IModeResourceProvider> providers = [display, player];

        return new ModeEngine(
            ModeConfiguration.LoadOrCreate(root.Path, providers),
            new ModeStateStore(root.StateFile),
            providers);
    }

    /// <summary>A resource that writes down what it was told, and when.</summary>
    private sealed class Listener(string key, string? current) : ITransitionAwareProvider
    {
        private readonly List<string> _applied = [];

        public string Key { get; } = key;

        public string DisplayName => Key;

        /// <summary>What it was told, once per step, in order.</summary>
        public List<IReadOnlyList<string>> Heard { get; } = [];

        public IReadOnlyList<string> Applied => _applied;

        public string? Current { get; set; } = current;

        /// <summary>Thrown from Preceded, when set.</summary>
        public Exception? Deaf { get; init; }

        public void Preceded(IReadOnlySet<string> changed)
        {
            Heard.Add([.. changed.Order(StringComparer.Ordinal)]);

            if (Deaf is not null)
            {
                throw Deaf;
            }
        }

        public bool IsValidValue(string value, out string? problem)
        {
            problem = null;
            return true;
        }

        public string? Read() => Current;

        public void Apply(string value, CancellationToken cancellationToken)
        {
            _applied.Add(value);
            Current = value;
        }
    }
}

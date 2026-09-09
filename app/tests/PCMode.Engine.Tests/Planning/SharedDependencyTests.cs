namespace PCMode.Engine.Tests;

/// <summary>
/// What a refusal says when more than one resource needed the thing that was not
/// available.
/// </summary>
/// <remarks>
/// A shared dependency is the ordinary case as soon as a television is involved:
/// the sound and the display layout both need it, and one of them is usually
/// optional. The explanation has to name the branch that actually caused the
/// refusal, because the whole purpose of the sentence is to tell somebody which
/// line to edit.
/// </remarks>
public sealed class SharedDependencyTests
{
    [Fact]
    public void Blames_the_branch_that_actually_needed_the_missing_resource()
    {
        // The optional sound asks for the television first, so it is the parent
        // that gets recorded. The required layout asks for it second. Only the
        // second one is why this mode cannot happen -- the first would simply have
        // been left out -- so naming the sound would send somebody to delete a
        // line that was never the problem.
        string problem = Rejected(
            Mode("Film", ("audio", "TV", true), ("display", "TVOnly", false)),
            new Needy("audio") { Needs = [new ResourceRequirement("tv", "On")] },
            new Needy("display") { Needs = [new ResourceRequirement("tv", "On")] },
            new Needy("tv", ResourceAvailability.Disabled("it is switched off in the settings."))
        );

        Assert.Contains("display", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("audio", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_the_whole_chain_back_to_the_line_that_was_written()
    {
        // Two hops: the mode sets the sound, the sound needs the layout, the
        // layout needs the television. Somebody reading this has to be able to get
        // from the resource that failed to the line they actually wrote.
        string problem = Rejected(
            Mode("Film", ("audio", "TV", false)),
            new Needy("audio") { Needs = [new ResourceRequirement("display", "TVOnly")] },
            new Needy("display") { Needs = [new ResourceRequirement("tv", "On")] },
            new Needy("tv", ResourceAvailability.Unavailable("there is no television on this machine."))
        );

        Assert.Contains("needed by display", problem, StringComparison.Ordinal);
        Assert.Contains("which is needed by audio", problem, StringComparison.Ordinal);
        Assert.Contains("'Film' sets", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Says_the_mode_set_it_when_the_mode_set_it()
    {
        // A resource the mode named itself needs no chain, even though something
        // else in the mode also happens to require it.
        string problem = Rejected(
            Mode("Film", ("tv", "On", false), ("audio", "TV", false)),
            new Needy("audio") { Needs = [new ResourceRequirement("tv", "On")] },
            new Needy("tv", ResourceAvailability.Disabled("it is switched off in the settings."))
        );

        Assert.Contains("'Film' sets it.", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_optional_branch_alone_leaves_a_shared_dependency_out_with_it()
    {
        // The mirror image of the first test. Nothing required needs the
        // television, so nothing is refused -- the sound goes, and the television
        // goes with it because nothing left has any use for it.
        TransitionPlan plan = Plan(
            Mode("Film", ("theme", "Dark", false), ("audio", "TV", true)),
            new Needy("theme"),
            new Needy("audio") { Needs = [new ResourceRequirement("tv", "On")] },
            new Needy("tv", ResourceAvailability.Disabled("it is switched off in the settings."))
        );

        Assert.Equal(["theme"], plan.Steps.Select(step => step.Key));
        Assert.Equal(["audio", "tv"], plan.Omissions.Select(omitted => omitted.Key).Order());
    }

    [Fact]
    public void A_shared_dependency_survives_when_one_branch_of_two_is_left_out()
    {
        // The television is available; the sound is not. The layout still needs
        // the television, so pruning the sound's branch must not take it along.
        TransitionPlan plan = Plan(
            Mode("Film", ("audio", "TV", true), ("display", "TVOnly", false)),
            new Needy("audio", ResourceAvailability.Unavailable("that sound device is not plugged in."))
            {
                Needs = [new ResourceRequirement("tv", "On")],
            },
            new Needy("display") { Needs = [new ResourceRequirement("tv", "On")] },
            new Needy("tv")
        );

        Assert.Equal(["tv", "display"], plan.Steps.Select(step => step.Key));
        Assert.Equal("audio", Assert.Single(plan.Omissions).Key);

        // And it is required now, because a required branch needs it -- not
        // optional merely because the first resource that asked for it was.
        Assert.All(plan.Steps, step => Assert.False(step.Optional));
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>A resource that needs others and may not be available.</summary>
    private sealed class Needy(string key, ResourceAvailability? availability = null)
        : IAvailableProvider,
            IDependentProvider
    {
        public string Key { get; } = key;

        public string DisplayName => Key;

        public IReadOnlyList<ResourceRequirement> Needs { get; init; } = [];

        public IReadOnlyList<ResourceRequirement> RequirementsFor(string target) => Needs;

        public ResourceAvailability AvailabilityOf(string target) => availability ?? ResourceAvailability.Available;

        public bool IsValidValue(string value, out string? problem)
        {
            problem = null;
            return true;
        }

        public string? Read() => null;

        public void Apply(string value, CancellationToken cancellationToken) { }
    }

    private static ConfiguredMode Mode(string name, params (string Key, string Value, bool Optional)[] resources) =>
        new(
            name,
            [.. resources.Select(r => new ConfiguredResource(r.Key, r.Value, r.Optional))],
            new ConfiguredTarget
            {
                Key = name,
                DisplayName = name,
                DisplayPath = name,
                Identity = new TargetIdentity("space", name.ToLowerInvariant(), null),
            }
        );

    private static TransitionPlan Plan(ConfiguredMode mode, params IModeResourceProvider[] providers)
    {
        bool made = TransitionPlan.TryCreate(mode, Index(providers), out TransitionPlan? plan, out string? problem);

        Assert.True(made, problem);

        return plan!;
    }

    private static string Rejected(ConfiguredMode mode, params IModeResourceProvider[] providers)
    {
        bool made = TransitionPlan.TryCreate(mode, Index(providers), out TransitionPlan? plan, out string? problem);

        Assert.False(made);
        Assert.Null(plan);

        return problem!;
    }

    private static Dictionary<string, IModeResourceProvider> Index(IModeResourceProvider[] providers) =>
        providers.ToDictionary(provider => provider.Key, StringComparer.OrdinalIgnoreCase);
}

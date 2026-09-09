namespace PCMode.Engine;

/// <summary>
/// One resource's standing, in the terms a person deciding what to do next
/// needs -- not the mechanics of how a transition got there.
/// </summary>
/// <remarks>
/// Deliberately smaller than <see cref="TransitionStepStatus"/>. That enum
/// records what a transition's dispatch loop did; this one answers "is this
/// thing right now" for a screen nobody wants to read a log to understand.
/// </remarks>
public enum ResourceHealthState
{
    /// <summary>Reads as its target. Nothing to do.</summary>
    Correct,

    /// <summary>
    /// Not yet reached -- a transition stopped before it got there, is still
    /// under way, or was cancelled. Not a fault by itself.
    /// </summary>
    Pending,

    /// <summary>
    /// An optional resource that failed. The mode still works without it, so
    /// this is worth showing and not worth treating as a failed activation.
    /// </summary>
    Degraded,

    /// <summary>A required resource that failed.</summary>
    Failed,

    /// <summary>
    /// Left out because this machine cannot do it right now -- the ordinary
    /// shape of an optional resource on hardware that does not have it.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The operation was intentionally not verifiable because the resource has no
    /// trustworthy observable state. Distinct from <see cref="Unknown"/>, where a
    /// state should have been observable but could not be determined.
    /// </summary>
    Unverified,

    /// <summary>Nobody has checked, or the check itself could not be answered.</summary>
    Unknown,
}

/// <summary>One resource's health, as of one observation.</summary>
/// <param name="Detail">
/// The plain-language reason behind <paramref name="State"/> -- a failure
/// message, an omission reason, or null when the state speaks for itself.
/// </param>
/// <param name="Diagnostics">
/// Provider-supplied technical detail for a "Details" view, kept apart from
/// <paramref name="Detail"/> because the two answer different questions: one is
/// what a user needs to decide whether to act, the other is what support needs
/// to diagnose why. See <see cref="IHealthReportingProvider"/>.
/// </param>
public sealed record ResourceHealth(
    string Key,
    string ResourceName,
    ResourceHealthState State,
    string Target,
    string? Current,
    string? Detail,
    string? Diagnostics,
    string? RequiredBy,
    bool Optional
);

/// <summary>
/// A mode's health as a whole: an overall verdict and every resource behind it.
/// </summary>
/// <remarks>
/// Built from a <see cref="TransitionOutcome"/> -- either the last transition
/// that actually ran, or a dry run taken purely to observe the machine without
/// changing it. Either way this is a snapshot, not a subscription: devices move
/// between one observation and the next, and a value that claimed to stay
/// current would be wrong without saying when.
/// </remarks>
/// <param name="Mode">The target's key.</param>
/// <param name="Target">
/// Which Space, Activity and Source this health belongs to, so a screen can say
/// what it is describing rather than showing a key. Null for a refusal that
/// could not be resolved to a target at all.
/// </param>
public sealed record ModeHealth(
    string Mode,
    ResourceHealthState Overall,
    IReadOnlyList<ResourceHealth> Resources,
    DateTimeOffset ObservedUtc,
    string Summary,
    ConfiguredTarget? Target = null
)
{
    /// <summary>What to call the target this describes.</summary>
    public string DisplayName => Target?.DisplayPath ?? Mode;

    /// <summary>Resources that need a person's attention.</summary>
    public IReadOnlyList<ResourceHealth> Concerns =>
        [.. Resources.Where(resource => resource.State is ResourceHealthState.Degraded or ResourceHealthState.Failed)];

    /// <summary>
    /// Reads a transition outcome as a health snapshot.
    /// </summary>
    /// <remarks>
    /// One mapping, used whether the outcome came from a real transition, a
    /// retry, a repair, or a dry run taken only to observe. Two mappings would
    /// be two chances for the main UI and a diagnostics view to disagree about
    /// what "degraded" means.
    /// </remarks>
    public static ModeHealth From(TransitionOutcome outcome, DateTimeOffset observedUtc)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var resources = new List<ResourceHealth>(outcome.Steps.Count);

        foreach (TransitionStep step in outcome.Steps)
        {
            resources.Add(
                new ResourceHealth(
                    step.Resource,
                    step.ResourceName,
                    StateOf(step),
                    step.Target,
                    step.Observed ?? step.Current,
                    DetailOf(step),
                    Diagnostics: null,
                    step.RequiredBy,
                    step.Optional
                )
            );
        }

        ResourceHealthState overall =
            resources.Count == 0
                ? ResourceHealthState.Unknown
                : resources
                    .Select(resource => resource.State)
                    .Aggregate(
                        ResourceHealthState.Correct,
                        (worst, state) => Severity(state) > Severity(worst) ? state : worst
                    );

        return new ModeHealth(outcome.Mode, overall, resources, observedUtc, outcome.Message, outcome.Target);
    }

    /// <summary>
    /// Reads a transition outcome as health, then fills in provider diagnostics
    /// for whichever resources have something to add.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="From(TransitionOutcome, DateTimeOffset)"/>
    /// because asking a provider for diagnostics is an extra call this class
    /// should not make silently -- a caller that only wants the outcome mapped
    /// should not pay for it, and one running many transitions in a loop should
    /// be able to skip it.
    /// </remarks>
    public static ModeHealth From(
        TransitionOutcome outcome,
        DateTimeOffset observedUtc,
        IReadOnlyDictionary<string, IModeResourceProvider> providers
    )
    {
        ArgumentNullException.ThrowIfNull(providers);

        ModeHealth health = From(outcome, observedUtc);

        List<ResourceHealth> withDiagnostics =
        [
            .. health.Resources.Select(resource =>
                providers.TryGetValue(resource.Key, out IModeResourceProvider? provider)
                && provider is IHealthReportingProvider reporting
                    ? resource with
                    {
                        Diagnostics = SafeDetail(reporting, resource.Target),
                    }
                    : resource
            ),
        ];

        return health with
        {
            Resources = withDiagnostics,
        };
    }

    private static string? SafeDetail(IHealthReportingProvider provider, string target)
    {
        try
        {
            return provider.HealthDetail(target);
        }
        catch (Exception exception)
        {
            // A diagnostics call is not load-bearing for anything: a provider
            // whose extra detail throws should lose that detail, not the health
            // page it was meant to enrich.
            return $"Diagnostics unavailable: {exception.Message}";
        }
    }

    private static ResourceHealthState StateOf(TransitionStep step) =>
        step.Status switch
        {
            TransitionStepStatus.AlreadyCorrect or TransitionStepStatus.Applied => ResourceHealthState.Correct,
            TransitionStepStatus.Failed => step.Optional ? ResourceHealthState.Degraded : ResourceHealthState.Failed,
            TransitionStepStatus.Omitted => ResourceHealthState.Unavailable,
            TransitionStepStatus.NotReached => ResourceHealthState.Pending,

            // Planned belongs to a dry run only, and ModeEngine reports it exactly
            // when the reading it took does not already match the target -- so
            // "would change" reads as pending, the same as anything else not yet
            // where it should be.
            TransitionStepStatus.Planned => ResourceHealthState.Pending,

            _ => ResourceHealthState.Unknown,
        };

    private static string? DetailOf(TransitionStep step) =>
        step.Status switch
        {
            TransitionStepStatus.Failed => step.Problem,
            TransitionStepStatus.Omitted => step.Problem,
            TransitionStepStatus.NotReached => "Not reached before the transition stopped.",
            _ => null,
        };

    /// <summary>
    /// Worst first. <see cref="ResourceHealthState.Unavailable"/> ranks level
    /// with <see cref="ResourceHealthState.Correct"/> rather than above it: it
    /// is the ordinary shape of an optional resource on hardware this machine
    /// does not have, not a fault, so a mode built entirely of present,
    /// correct resources plus a few sensibly-omitted ones still reads as
    /// healthy overall.
    /// </summary>
    private static int Severity(ResourceHealthState state) =>
        state switch
        {
            ResourceHealthState.Correct => 0,
            ResourceHealthState.Unavailable => 0,
            ResourceHealthState.Unverified => 1,
            ResourceHealthState.Pending => 2,
            ResourceHealthState.Unknown => 3,
            ResourceHealthState.Degraded => 4,
            ResourceHealthState.Failed => 5,
            _ => 3,
        };
}

/// <summary>
/// A provider that can explain itself further than "here" and "not here".
/// </summary>
/// <remarks>
/// For a resource whose ordinary Read/Apply answers are too narrow for a health
/// page to be worth much -- an audio target's route states, endpoint states,
/// formats, delays, clock offsets, drift corrections, underruns and last failure
/// are not expressible as a single current value, and asking <see cref="AudioProvider"/>
/// to fold all of that into <see cref="IModeResourceProvider.Read"/> would ruin
/// the one thing <c>Read</c> is for: a short answer convergence can compare
/// against a target.
///
/// Optional, like every other capability interface in this engine: a provider
/// with nothing more to say does not implement it.
/// </remarks>
public interface IHealthReportingProvider : IModeResourceProvider
{
    /// <summary>
    /// Technical detail about reaching <paramref name="target"/>, for a
    /// "Details" view rather than the main health line.
    /// </summary>
    string? HealthDetail(string target);
}

namespace PCMode.Engine;

/// <summary>One resource of a plan, why it is in it, and what it will take.</summary>
/// <remarks>
/// <see cref="RequiredBy"/> is null for something the mode asked for and holds
/// the asking resource's key for something the planner added. A step nobody can
/// account for is worse than no step: a dry run that lists a display change for
/// a mode which never mentions displays has to be able to say whose idea it was.
///
/// <see cref="Policy"/> is resolved here rather than at dispatch, so that a
/// provider which cannot describe how to reach a target refuses the transition
/// instead of stalling one that is already under way.
/// </remarks>
/// <param name="Optional">
/// Whether the mode still means something without this. Resolved across the whole
/// graph rather than taken from the file: a resource written as optional that
/// something required needs is required, because the alternative is a transition
/// that leaves out the display and then sends sound to a television nobody
/// switched on.
/// </param>
/// <param name="Availability">
/// What the provider said about this target when the transition was planned.
/// Available or unknown by the time it reaches here -- a definite no would have
/// omitted or refused it -- and carried through so a report can say that a
/// resource was attempted despite nobody being sure it would work.
/// </param>
public sealed record PlannedResource(
    IModeResourceProvider Provider,
    string Target,
    string? RequiredBy,
    ConvergencePolicy Policy,
    bool Optional,
    ResourceAvailability Availability
)
{
    public string Key => Provider.Key;

    public string ResourceName => Provider.DisplayName;

    /// <summary>Whether the mode named this itself.</summary>
    public bool WasRequested => RequiredBy is null;
}

/// <summary>A resource that was planned for and then left out, and why.</summary>
/// <remarks>
/// Reported rather than silently dropped. A mode that quietly does less than it
/// says on one machine than on another, with nothing to show for the difference,
/// is how somebody spends an evening looking for a fault in their configuration.
/// </remarks>
public sealed record OmittedResource(
    string Key,
    string ResourceName,
    string Target,
    string? RequiredBy,
    ResourceAvailability Availability,
    string Reason
);

/// <summary>
/// The whole of one transition, decided before any of it happens.
/// </summary>
/// <remarks>
/// WHY A PLAN IS A THING RATHER THAN A LOOP
///
/// The rule this application keeps is that a mode with one unusable resource
/// changes nothing at all. That rule is only as good as the amount that is known
/// before the first write, and once resources can need each other, walking the
/// mode's list in order stops being enough to know it: a conflict between the
/// first resource and the fourth is discoverable only by resolving all four.
/// Resolving them produces something, and that something is this.
///
/// It is immutable, and it is the same object a dry run walks and a real
/// transition applies. Two paths that each worked out what to do would be two
/// chances to disagree, and the one that disagreed would be the one nobody had
/// watched.
///
/// PRESENTATION-NEUTRAL
///
/// A list of steps with names, targets and provenance. Nothing here knows
/// whether it will be shown, logged or executed.
///
/// ORDER
///
/// Dependencies first, and configuration order for everything else. The order
/// resources were written in is a tie-breaker and nothing more -- it decides
/// between steps that have no reason to care, and never overrides one that does.
/// A user who reorders two independent lines gets that order; a user who
/// reorders two dependent ones gets the order that works.
///
/// WHAT IS DECIDED IN WHAT ORDER, AND WHY IT CANNOT BE REARRANGED
///
/// Everything the mode implies is resolved first -- requirements, conflicts,
/// ordering, cycles -- on the whole graph, including branches that are about to
/// be left out. Only then is anything omitted. Deciding it the other way round
/// would let an unplugged television hide a contradiction or a loop, so a mode
/// would be refused on the days the television was on and accepted on the days
/// it was not, which is the least useful possible time to find out.
/// </remarks>
public sealed class TransitionPlan
{
    private TransitionPlan(
        ConfiguredMode mode,
        IReadOnlyList<PlannedResource> steps,
        IReadOnlyList<OmittedResource> omissions
    )
    {
        Mode = mode.Name;
        DisplayName = mode.DisplayName;
        Target = mode.Target;
        Steps = steps;
        Omissions = omissions;
    }

    /// <summary>The key of the target this plan would put the machine in.</summary>
    public string Mode { get; }

    /// <summary>What to call that target in something a person reads.</summary>
    public string DisplayName { get; }

    /// <summary>Which Space, Activity and Source it is.</summary>
    public ConfiguredTarget Target { get; }

    /// <summary>Every resource, in the order it will be applied.</summary>
    public IReadOnlyList<PlannedResource> Steps { get; }

    /// <summary>Resources the planner added that the mode did not name.</summary>
    public IReadOnlyList<PlannedResource> Added => [.. Steps.Where(step => !step.WasRequested)];

    /// <summary>What this transition will not be doing, and why.</summary>
    public IReadOnlyList<OmittedResource> Omissions { get; }

    /// <summary>
    /// Whether the mode asked for things and every one of them was left out.
    /// </summary>
    /// <remarks>
    /// Not a failure and not a success. Nothing will be changed, so above all
    /// nothing may be recorded: a mode name written after a transition that did
    /// nothing would be a claim with nothing behind it.
    /// </remarks>
    public bool NothingLeft => Steps.Count == 0;

    /// <summary>
    /// Resolves a mode into an ordered plan, or explains why it cannot be one.
    /// </summary>
    /// <remarks>
    /// Every refusal here happens with the machine untouched, which is the only
    /// reason the checks are worth making in this order. Providers are looked up
    /// again even though the configuration was validated when it was loaded: a
    /// provider can be present at load and absent at run, and validation that
    /// happens once is a rule about a moment rather than about a transition.
    ///
    /// Every question put to a provider while planning -- is this value usable,
    /// what does it need, what does it follow, what will it take, can it be done
    /// at all -- is asked through a wrapper that turns a thrown answer into
    /// something the caller can read. A provider that throws while being planned
    /// is a defect in that provider, and the caller of a transition should be
    /// told which resource and shown a message, not handed an exception out of a
    /// method whose entire contract is that it decides before it changes
    /// anything.
    /// </remarks>
    public static bool TryCreate(
        ConfiguredMode mode,
        IReadOnlyDictionary<string, IModeResourceProvider> providers,
        out TransitionPlan? plan,
        out string? problem
    )
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(providers);

        plan = null;
        problem = null;

        List<Node> nodes = [];
        Dictionary<string, int> byKey = new(StringComparer.OrdinalIgnoreCase);

        // ---- what the mode asked for, in the order it was written ----------
        foreach (ConfiguredResource resource in mode.Resources)
        {
            if (!providers.TryGetValue(resource.Key, out IModeResourceProvider? provider))
            {
                problem = $"'{mode.DisplayName}' sets '{resource.Key}', which nothing here knows how to apply.";
                return false;
            }

            if (!TryValidate(provider, resource.Value, out string? invalid, out problem))
            {
                problem = $"'{mode.DisplayName}': {problem}";
                return false;
            }

            if (invalid is not null)
            {
                // An unusable value is a mistake in the file, so it is refused
                // whether or not the resource is optional. Optional is about
                // what this machine can do, never about what the document means.
                problem = $"'{mode.DisplayName}': {invalid}";
                return false;
            }

            if (!byKey.TryAdd(provider.Key, nodes.Count))
            {
                // Two values for one resource is a mode with no single meaning,
                // and whichever ran last would silently be the one that counted.
                problem = $"'{mode.DisplayName}' sets '{provider.Key}' more than once.";
                return false;
            }

            if (!TryPolicy(provider, resource.Value, out ConvergencePolicy? policy, out problem))
            {
                problem = $"'{mode.DisplayName}': {problem}";
                return false;
            }

            nodes.Add(new Node(provider, resource.Value, requiredBy: null, policy!, resource.Optional));
        }

        if (nodes.Count == 0)
        {
            // An empty plan is not a successful transition, and above all must
            // not record a mode: nothing about the machine would have changed,
            // so the recorded name would be a claim with nothing behind it.
            problem = $"'{mode.DisplayName}' sets nothing.";
            return false;
        }

        // ---- what those resources need, added when the mode omits them -----
        //
        // The list grows while it is walked, so a resource brought in to satisfy
        // a requirement has its own requirements resolved too. It terminates
        // because nothing is ever added twice for the same key, and there are
        // only so many providers.
        for (int index = 0; index < nodes.Count; index++)
        {
            if (nodes[index].Provider is not IDependentProvider dependent)
            {
                continue;
            }

            if (
                !TryAsk(
                    () => dependent.RequirementsFor(nodes[index].Target),
                    $"'{mode.DisplayName}': {nodes[index].ResourceName} could not say what '{nodes[index].Target}' needs.",
                    out IReadOnlyList<ResourceRequirement>? required,
                    out problem
                )
            )
            {
                return false;
            }

            foreach (ResourceRequirement requirement in required ?? [])
            {
                if (!TryRequire(mode, providers, nodes, byKey, nodes[index], requirement, out problem))
                {
                    return false;
                }
            }
        }

        // ---- run-after, for resources that are in the plan already ---------
        foreach (Node node in nodes)
        {
            if (node.Provider is not IDependentProvider dependent)
            {
                continue;
            }

            if (
                !TryAsk(
                    () => dependent.RunsAfter,
                    $"'{mode.DisplayName}': {node.ResourceName} could not say what it runs after.",
                    out IReadOnlyList<string>? after,
                    out problem
                )
            )
            {
                return false;
            }

            foreach (string key in after ?? [])
            {
                // Absent means unwanted, not missing. This is the rule that
                // stops a preference about order from dragging a resource into
                // a mode that deliberately leaves it alone.
                if (
                    !string.IsNullOrWhiteSpace(key)
                    && byKey.ContainsKey(key)
                    && !string.Equals(key, node.Key, StringComparison.OrdinalIgnoreCase)
                )
                {
                    node.After.Add(key);
                }
            }
        }

        PromoteRequiredness(nodes, byKey);

        if (!TryOrder(mode, nodes, out List<Node>? ordered, out problem))
        {
            return false;
        }

        return TryOmit(mode, ordered!, out plan, out problem);
    }

    /// <summary>
    /// Works out which resources the mode genuinely cannot do without.
    /// </summary>
    /// <remarks>
    /// A resource is required if the mode named it without saying otherwise, or
    /// if anything required needs it. That second half is the whole point: an
    /// optional television and a required soundtrack that only exists on the
    /// television are a contradiction, and resolving it in favour of the
    /// soundtrack is the only reading under which the mode still works.
    ///
    /// Only concrete requirements propagate. Wanting to run after the displays
    /// says nothing about needing them -- that is exactly the difference between
    /// the two relationships -- so an ordering preference can never promote
    /// anything.
    /// </remarks>
    private static void PromoteRequiredness(List<Node> nodes, Dictionary<string, int> byKey)
    {
        var pending = new Queue<Node>();

        foreach (Node node in nodes)
        {
            // Only what the mode itself named can start out required. Everything
            // else is here because something asked for it, and inherits its
            // standing from whatever did the asking.
            if (node.WasRequested && !node.DeclaredOptional)
            {
                node.Required = true;
                pending.Enqueue(node);
            }
        }

        while (pending.Count > 0)
        {
            Node node = pending.Dequeue();

            foreach (string key in node.Requires)
            {
                if (!byKey.TryGetValue(key, out int index))
                {
                    continue;
                }

                Node needed = nodes[index];

                if (!needed.Required)
                {
                    needed.Required = true;
                    pending.Enqueue(needed);
                }
            }
        }
    }

    /// <summary>
    /// Asks what is actually possible, and removes what is not.
    /// </summary>
    /// <remarks>
    /// Run last, on an order that is already settled, so that nothing here can
    /// change what a transition would have consisted of -- only how much of it
    /// survives. Availability is asked once per resource and in plan order, so a
    /// report reads in the same order twice running.
    ///
    /// Only a definite no counts. Unknown is planned and attempted, because
    /// convergence is the mechanism for a resource that may be about to work and
    /// a planner that dropped everything it was unsure of would drop the
    /// television every time it was still waking up.
    /// </remarks>
    private static bool TryOmit(ConfiguredMode mode, List<Node> ordered, out TransitionPlan? plan, out string? problem)
    {
        plan = null;
        problem = null;

        var nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);

        foreach (Node node in ordered)
        {
            node.Availability = Ask(node);
            nodes[node.Key] = node;
        }

        foreach (Node node in ordered)
        {
            if (!node.Availability.IsDefinitelyOut)
            {
                continue;
            }

            if (node.Required)
            {
                problem =
                    $"'{mode.DisplayName}' cannot be applied: {node.ResourceName} '{node.Target}' "
                    + $"is not available -- {node.Availability.Explain()} {Provenance(mode, nodes, node)}";

                return false;
            }

            node.Omission = node.Availability.Explain();
        }

        // An optional resource whose requirement has gone cannot work either,
        // and saying so is how the branch leaves together rather than in pieces.
        // A required resource can never reach this: promotion made everything it
        // needs required too, so an unavailable one would already have refused.
        for (bool spreading = true; spreading; )
        {
            spreading = false;

            foreach (Node node in ordered)
            {
                if (node.Omission is not null)
                {
                    continue;
                }

                foreach (string key in node.Requires)
                {
                    if (nodes.TryGetValue(key, out Node? needed) && needed.Omission is not null)
                    {
                        node.Omission = $"it needs {needed.ResourceName} '{needed.Target}', which was left out.";
                        spreading = true;
                        break;
                    }
                }
            }
        }

        // What is left of the mode's own resources holds up everything else. A
        // requirement whose only reason for being here has gone is removed with
        // it; one that something surviving also needs stays exactly where it was.
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reachable = new Queue<Node>();

        foreach (Node node in ordered)
        {
            if (node.WasRequested && node.Omission is null && kept.Add(node.Key))
            {
                reachable.Enqueue(node);
            }
        }

        while (reachable.Count > 0)
        {
            foreach (string key in reachable.Dequeue().Requires)
            {
                if (nodes.TryGetValue(key, out Node? needed) && needed.Omission is null && kept.Add(needed.Key))
                {
                    reachable.Enqueue(needed);
                }
            }
        }

        var steps = new List<PlannedResource>(ordered.Count);
        var omissions = new List<OmittedResource>();

        foreach (Node node in ordered)
        {
            if (node.Omission is null && kept.Contains(node.Key))
            {
                steps.Add(
                    new PlannedResource(
                        node.Provider,
                        node.Target,
                        node.RequiredBy,
                        node.Policy,
                        !node.Required,
                        node.Availability
                    )
                );

                continue;
            }

            omissions.Add(
                new OmittedResource(
                    node.Key,
                    node.ResourceName,
                    node.Target,
                    node.RequiredBy,
                    node.Availability,
                    node.Omission ?? $"nothing left in '{mode.DisplayName}' needs it."
                )
            );
        }

        plan = new TransitionPlan(mode, steps, omissions);
        return true;
    }

    /// <summary>
    /// Why a resource is in a transition at all, followed back to the mode.
    /// </summary>
    /// <remarks>
    /// The sentence that makes a refusal actionable. "The television is off" is
    /// not an explanation of why a mode about sound was refused; "the television
    /// is off, and the sound you asked for only exists on it" is.
    /// </remarks>
    private static string Provenance(ConfiguredMode mode, Dictionary<string, Node> nodes, Node node)
    {
        if (node.WasRequested)
        {
            return $"'{mode.DisplayName}' sets it.";
        }

        // Breadth-first up the parents, so the chain offered is the shortest true
        // one rather than whichever happened to be recorded first.
        //
        // Restricted to required parents when the resource itself is required. A
        // shared dependency is routinely needed by an optional branch as well, and
        // that branch is not why the transition is being refused -- following it
        // would produce a sentence that is individually true and, as an
        // explanation of the refusal, wrong. Turning off the restriction for an
        // optional resource costs nothing, because then any parent will do.
        var discoveredFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { node.Key };
        var queue = new Queue<Node>();

        queue.Enqueue(node);

        Node? root = null;

        while (queue.Count > 0 && root is null)
        {
            Node walking = queue.Dequeue();

            foreach (string key in walking.Parents)
            {
                if (!nodes.TryGetValue(key, out Node? parent) || (node.Required && !parent.Required) || !seen.Add(key))
                {
                    continue;
                }

                discoveredFrom[key] = walking.Key;

                if (parent.WasRequested)
                {
                    root = parent;
                    break;
                }

                queue.Enqueue(parent);
            }
        }

        if (root is null)
        {
            // Unreachable: promotion gives every required resource a required
            // chain back to something the mode named. Worth a sentence rather
            // than an exception, because this one is only ever used to explain a
            // refusal, and a refusal that could not be phrased must still happen.
            return $"'{mode.DisplayName}' needs it.";
        }

        var chain = new List<string>();

        for (
            string at = root.Key;
            !string.Equals(at, node.Key, StringComparison.OrdinalIgnoreCase);
            at = discoveredFrom[at]
        )
        {
            chain.Add($"{nodes[at].ResourceName} '{nodes[at].Target}'");
        }

        // Collected from the mode inwards; read from the resource outwards.
        chain.Reverse();

        return $"It is needed by {string.Join(", which is needed by ", chain)}, which '{mode.DisplayName}' sets.";
    }

    /// <summary>
    /// Satisfies one requirement, by finding it, adding it, or refusing.
    /// </summary>
    private static bool TryRequire(
        ConfiguredMode mode,
        IReadOnlyDictionary<string, IModeResourceProvider> providers,
        List<Node> nodes,
        Dictionary<string, int> byKey,
        Node needer,
        ResourceRequirement requirement,
        out string? problem
    )
    {
        problem = null;

        if (
            requirement is null
            || string.IsNullOrWhiteSpace(requirement.Key)
            || string.IsNullOrWhiteSpace(requirement.Target)
        )
        {
            problem =
                $"'{mode.DisplayName}': {needer.ResourceName} '{needer.Target}' declares a requirement "
                + "with no resource or no target.";

            return false;
        }

        if (byKey.TryGetValue(requirement.Key, out int existing))
        {
            Node already = nodes[existing];

            if (
                string.Equals(already.Target, requirement.Target, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(requirement.Key, needer.Key, StringComparison.OrdinalIgnoreCase)
                && !already.Parents.Contains(needer.Key, StringComparer.OrdinalIgnoreCase)
            )
            {
                // A second resource needing what the first already brought in.
                // The node is not added again, so this is the only place the
                // second reason for its being here can be written down.
                already.Parents.Add(needer.Key);
            }

            if (!string.Equals(already.Target, requirement.Target, StringComparison.OrdinalIgnoreCase))
            {
                // Both readings of the request are wrong. Applying the mode's
                // value breaks the resource that needs the other one; applying
                // the requirement quietly overrides something the user wrote.
                //
                // Refused whether or not either resource is optional: a
                // contradiction is a fact about the document, and letting an
                // unavailable television conceal one would mean the same mode
                // being refused or accepted depending on what was switched on.
                problem = already.WasRequested
                    ? $"'{mode.DisplayName}' sets {already.ResourceName} to '{already.Target}', but "
                        + $"{needer.ResourceName} '{needer.Target}' needs '{requirement.Target}'."
                    : $"'{mode.DisplayName}' would need {already.ResourceName} at both '{already.Target}' "
                        + $"(for '{already.RequiredBy}') and '{requirement.Target}' (for '{needer.Key}').";

                return false;
            }
        }
        else
        {
            if (!providers.TryGetValue(requirement.Key, out IModeResourceProvider? provider))
            {
                problem =
                    $"'{mode.DisplayName}' needs '{requirement.Key}' for {needer.ResourceName} "
                    + $"'{needer.Target}', and nothing here knows how to apply it.";

                return false;
            }

            if (!TryValidate(provider, requirement.Target, out string? invalid, out problem))
            {
                problem = $"'{mode.DisplayName}': {problem}";
                return false;
            }

            if (invalid is not null)
            {
                problem =
                    $"'{mode.DisplayName}' needs {provider.DisplayName} '{requirement.Target}' for "
                    + $"{needer.ResourceName} '{needer.Target}'. {invalid}";

                return false;
            }

            if (!TryPolicy(provider, requirement.Target, out ConvergencePolicy? policy, out problem))
            {
                problem = $"'{mode.DisplayName}': {problem}";
                return false;
            }

            byKey.Add(provider.Key, nodes.Count);

            // Optionality is not declared for something nobody wrote down. It is
            // worked out afterwards, from whether anything required needs it.
            nodes.Add(new Node(provider, requirement.Target, needer.Key, policy!, declaredOptional: true));
        }

        if (!string.Equals(requirement.Key, needer.Key, StringComparison.OrdinalIgnoreCase))
        {
            // A resource requiring its own current target is a tautology rather
            // than a cycle, so it contributes no edge.
            needer.Requires.Add(requirement.Key);
            needer.After.Add(requirement.Key);
        }

        return true;
    }

    /// <summary>
    /// Orders the resolved resources, or names the cycle that stops it.
    /// </summary>
    /// <remarks>
    /// Each pass takes the earliest-discovered resource whose dependencies have
    /// all been placed. Taking the earliest rather than any available one is
    /// what makes the plan the same every time it is built from the same
    /// configuration; anything else would make an order that has no reason to
    /// vary vary anyway, and a transition nobody can reproduce.
    ///
    /// Ordered whole, including branches that are about to be left out, so that
    /// a loop is a loop on every machine rather than on the ones where the
    /// television happened to be plugged in.
    /// </remarks>
    private static bool TryOrder(ConfiguredMode mode, List<Node> nodes, out List<Node>? ordered, out string? problem)
    {
        ordered = null;
        problem = null;

        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = new List<Node>(nodes);
        var order = new List<Node>(nodes.Count);

        while (remaining.Count > 0)
        {
            int next = remaining.FindIndex(node => node.After.All(placed.Contains));

            if (next < 0)
            {
                // Every resource left is waiting for another one that is also
                // left. There is no order, so there is no transition -- and
                // saying so now costs nothing, whereas discovering it half way
                // through would cost whatever had already been changed.
                problem =
                    $"'{mode.DisplayName}' cannot be ordered: "
                    + $"{string.Join(", ", remaining.Select(node => $"'{node.Key}'"))} depend on each other.";

                return false;
            }

            Node node = remaining[next];

            remaining.RemoveAt(next);
            placed.Add(node.Key);
            order.Add(node);
        }

        ordered = order;
        return true;
    }

    /// <summary>
    /// Asks a provider whether a target is possible, and never lets the asking
    /// itself decide anything.
    /// </summary>
    /// <remarks>
    /// A provider that throws has not said no. It has said nothing, which is
    /// <see cref="AvailabilityState.Unknown"/> -- so the resource stays in the
    /// plan and convergence decides, exactly as it would for a television that
    /// has not answered yet. Letting the exception escape would refuse a whole
    /// transition over a broken query, and swallowing it into an omission would
    /// silently shrink a mode because a provider had a bug.
    /// </remarks>
    private static ResourceAvailability Ask(Node node)
    {
        if (node.Provider is not IAvailableProvider available)
        {
            return ResourceAvailability.Available;
        }

        try
        {
            ResourceAvailability? answer = available.AvailabilityOf(node.Target);

            // A provider that answers with nothing has not said no either.
            return answer ?? ResourceAvailability.Unknown($"{node.ResourceName} gave no answer about '{node.Target}'.");
        }
        catch (Exception exception)
        {
            return ResourceAvailability.Unknown(
                $"{node.ResourceName} could not say whether '{node.Target}' is available. {exception.Message}"
            );
        }
    }

    /// <summary>
    /// Asks a provider something while planning, or refuses the transition.
    /// </summary>
    /// <remarks>
    /// The whole point of planning is that it decides before it changes
    /// anything. A provider throwing out of one of these questions would send an
    /// exception through <see cref="ModeEngine.ApplyAsync"/> instead of a
    /// refusal, which is the one shape of answer its callers are not written to
    /// handle.
    /// </remarks>
    private static bool TryAsk<T>(Func<T> ask, string what, out T? answer, out string? problem)
    {
        try
        {
            answer = ask();
            problem = null;
            return true;
        }
        catch (Exception exception)
        {
            answer = default;
            problem = $"{what} {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// Asks whether a provider accepts a value.
    /// </summary>
    /// <remarks>
    /// Two out parameters because there are two different answers: the provider
    /// said no and explained why (<paramref name="invalid"/>), or the provider
    /// could not be asked at all (<paramref name="problem"/>). The callers word
    /// the first one differently depending on whether the mode named the value
    /// or something else required it, so it is handed back rather than phrased
    /// here.
    /// </remarks>
    private static bool TryValidate(
        IModeResourceProvider provider,
        string value,
        out string? invalid,
        out string? problem
    )
    {
        invalid = null;

        if (
            !TryAsk(
                () =>
                {
                    bool usable = provider.IsValidValue(value, out string? said);
                    return usable ? null : said ?? $"'{value}' was refused without a reason.";
                },
                $"{provider.DisplayName} could not say whether '{value}' is usable.",
                out string? answer,
                out problem
            )
        )
        {
            return false;
        }

        invalid = answer;
        return true;
    }

    /// <summary>
    /// Resolves how a resource expects to reach a target, and refuses one that
    /// could never get there.
    /// </summary>
    private static bool TryPolicy(
        IModeResourceProvider provider,
        string target,
        out ConvergencePolicy? policy,
        out string? problem
    )
    {
        if (provider is not IConvergentProvider convergent)
        {
            // One attempt, no readback: what the engine did before any of this
            // existed, and still the right answer for a provider with nothing
            // to add.
            policy = ConvergencePolicy.DispatchOnce;
            problem = null;
            return true;
        }

        if (
            !TryAsk(
                () => convergent.PolicyFor(target),
                $"{provider.DisplayName} could not say what reaching '{target}' takes.",
                out policy,
                out problem
            )
        )
        {
            return false;
        }

        if (policy is null)
        {
            problem = $"{provider.DisplayName} gave no policy for reaching '{target}'.";
            return false;
        }

        if (!policy.IsUsable(out string? unusable))
        {
            problem = $"{provider.DisplayName} cannot reach '{target}': {unusable}";
            policy = null;
            return false;
        }

        return true;
    }

    /// <summary>One resource while the plan is still being worked out.</summary>
    private sealed class Node(
        IModeResourceProvider provider,
        string target,
        string? requiredBy,
        ConvergencePolicy policy,
        bool declaredOptional
    )
    {
        public IModeResourceProvider Provider { get; } = provider;

        public string Target { get; } = target;

        /// <summary>
        /// The first resource that asked for this one, or null when the mode
        /// named it.
        /// </summary>
        /// <remarks>
        /// Kept because a step has to say whose idea it was in one short phrase,
        /// and the first asker is a stable, reproducible choice for that. It is
        /// not the whole truth once a resource has several parents, which is what
        /// <see cref="Parents"/> is for.
        /// </remarks>
        public string? RequiredBy { get; } = requiredBy;

        /// <summary>
        /// Every resource that needs this one, in the order they asked.
        /// </summary>
        /// <remarks>
        /// A shared dependency has more than one, and which of them matters
        /// depends on the question. The television is needed by the sound and by
        /// the display layout; if the sound is optional and the layout is not,
        /// then the layout is the reason the television cannot be left out, and an
        /// explanation naming the sound would send somebody to edit the wrong
        /// line. Recording only the first asker made that a coin toss decided by
        /// configuration order.
        /// </remarks>
        public List<string> Parents { get; } = requiredBy is null ? [] : [requiredBy];

        public ConvergencePolicy Policy { get; } = policy;

        /// <summary>What the file said, before anything else was taken into account.</summary>
        public bool DeclaredOptional { get; } = declaredOptional;

        /// <summary>Whether the mode cannot do without this, once the graph is resolved.</summary>
        public bool Required { get; set; }

        public ResourceAvailability Availability { get; set; } = ResourceAvailability.Available;

        /// <summary>Why this was left out, or null while it is still in.</summary>
        public string? Omission { get; set; }

        public bool WasRequested => RequiredBy is null;

        public string Key => Provider.Key;

        public string ResourceName => Provider.DisplayName;

        /// <summary>
        /// Resource keys this genuinely cannot work without.
        /// </summary>
        /// <remarks>
        /// A subset of <see cref="After"/>, kept separately because the two
        /// answer different questions. Requirements decide what a branch
        /// consists of and therefore what leaves with it; ordering decides only
        /// what goes first, and must never remove or promote anything.
        /// </remarks>
        public HashSet<string> Requires { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Resource keys that must be applied before this one.</summary>
        public HashSet<string> After { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

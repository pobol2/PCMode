namespace PCMode.Engine;

/// <summary>How a transition turned out.</summary>
public enum TransitionStatus
{
    /// <summary>Every resource reached its target.</summary>
    Succeeded,

    /// <summary>
    /// Nothing was dispatched, and the machine is untouched. An unknown mode,
    /// an invalid target, or another transition already running.
    /// </summary>
    Refused,

    /// <summary>
    /// A resource threw after others had already been applied, so the machine
    /// sits between two modes.
    /// </summary>
    Failed,

    /// <summary>
    /// The outcome is not known: cancelled part way, or converged and then
    /// could not be recorded. Deliberately not folded into
    /// <see cref="Failed"/> -- "that did not work" and "nobody knows what state
    /// this is in" call for different things from a user.
    /// </summary>
    Uncertain,
}

/// <summary>What happened to one resource.</summary>
public enum TransitionStepStatus
{
    /// <summary>A dry run: this is what would have been done.</summary>
    Planned,

    /// <summary>The machine was already there, so nothing was dispatched.</summary>
    AlreadyCorrect,

    Applied,

    /// <summary>Dispatched successfully, but no trustworthy readback exists.</summary>
    AppliedUnverified,

    /// <summary>Intentionally not re-dispatched because this resource cannot be observed.</summary>
    Unverified,

    Failed,

    /// <summary>Never reached, because something before it failed or was cancelled.</summary>
    NotReached,

    /// <summary>
    /// Left out, because this machine definitely cannot do it and the mode said
    /// it could manage without.
    /// </summary>
    /// <remarks>
    /// Not a failure and not a success: nothing was attempted. Reported all the
    /// same, because a mode that quietly does less on one machine than on another
    /// with nothing to show for the difference is how somebody spends an evening
    /// looking for a fault in their configuration.
    /// </remarks>
    Omitted,
}

/// <summary>One resource's part in a transition.</summary>
/// <param name="Current">
/// What was read before anything was dispatched -- the value a report describes
/// the resource as coming from.
/// </param>
/// <param name="RequiredBy">
/// The resource that needed this one, when the mode did not name it itself.
/// Null for everything the mode asked for.
/// </param>
/// <param name="Observed">
/// The last value read back, which for a verified resource is its target and for
/// a failed one is where it actually got to. Kept apart from
/// <paramref name="Current"/> because "it was Light" and "it is still Light"
/// are different claims, and a report that could only make the first one cannot
/// say what went wrong.
/// </param>
/// <param name="Attempts">
/// How many times the command was issued. Zero when the machine was already
/// there, when the step was never reached, and when it was left out.
/// </param>
/// <param name="Optional">
/// Whether the mode could manage without this. Only an optional resource can be
/// left out; an optional one that is present is applied and allowed to fail like
/// any other.
/// </param>
public sealed record TransitionStep(
    string Resource,
    string ResourceName,
    string Target,
    string? Current,
    TransitionStepStatus Status,
    string? Problem,
    string? RequiredBy = null,
    string? Observed = null,
    int Attempts = 0,
    bool Optional = false);

/// <summary>The whole result of one request.</summary>
/// <param name="Mode">
/// The target's key -- what was recorded, and what a retry would ask for again.
/// </param>
/// <param name="Mutated">
/// Whether anything that could have changed the machine was started. True from
/// before the first command, including one that threw -- so a caller can tell a
/// transition that changed nothing from one that may have changed something and
/// then gone wrong. When this is true and <paramref name="StateRecorded"/> is
/// not, the machine's mode is genuinely unknown and must be shown as such.
/// </param>
/// <param name="Target">
/// Which Space, Activity and Source this was, when the request named one that
/// exists. Null for a refusal that could not be resolved to a target at all.
/// </param>
public sealed record TransitionOutcome(
    string Mode,
    TransitionStatus Status,
    bool DryRun,
    string Message,
    IReadOnlyList<TransitionStep> Steps,
    bool StateRecorded,
    string? StateProblem,
    bool Mutated = false,
    ConfiguredTarget? Target = null)
{
    public bool Succeeded => Status == TransitionStatus.Succeeded;

    /// <summary>How many resources were actually dispatched.</summary>
    public int AppliedCount => Steps.Count(step =>
        step.Status is TransitionStepStatus.Applied or TransitionStepStatus.AppliedUnverified);

    /// <summary>What this transition left out, and why.</summary>
    public IReadOnlyList<TransitionStep> Omitted =>
        [.. Steps.Where(step => step.Status == TransitionStepStatus.Omitted)];
}

/// <summary>
/// The application service: mode lookup, validation, execution, state.
/// </summary>
/// <remarks>
/// PRESENTATION-NEUTRAL
///
/// Nothing here knows about a window, a tray icon or a picker. The tray, the
/// wheel, the hotkeys and the controller all reach the machine through this one
/// object, so there is exactly one place where a transition can start and
/// exactly one set of rules about what a transition may do.
///
/// THE ORDER IS THE CONTRACT
///
/// Lookup, then planning, then execution -- and nothing is dispatched until the
/// whole plan has been resolved. A mode with one good resource and one bad one
/// changes nothing at all: a transition that gets half way before noticing a
/// typo leaves the machine somewhere nobody asked for.
///
/// The plan is <see cref="TransitionPlan"/>, and working out what a transition
/// consists of belongs to it rather than here. This class owns when a
/// transition may run, what happens when one goes wrong, and what is recorded
/// afterwards; the plan owns what it is made of and what order it is in.
///
/// ONE AT A TIME, AND THE NEWEST ONE WINS
///
/// Two transitions never run alongside each other: interleaving their writes
/// would produce a machine in neither mode. But a second request while one is
/// running is not simply refused either -- it supersedes the first, which is
/// cancelled at its next safe boundary (between resources, never mid hardware
/// command) so the newer request can start as soon as that boundary arrives. A
/// plain queue would mean a button press taking effect minutes later, on top of
/// obsolete work nobody wants finished; refusing outright would mean the last
/// press losing to whichever press happened to land first. If a third request
/// arrives while the second is still waiting for the first to yield, the second
/// is superseded in turn and never dispatches anything -- only the newest
/// request that exists at the moment the gate is free gets to run.
///
/// CONVERGENCE, NOT DISPATCH
///
/// A resource is finished when it can be shown to be, not when the call that
/// commanded it returned. What that takes is <see cref="ConvergencePolicy"/>,
/// the provider says which one applies to which target, and the mechanics live
/// in <see cref="ResourceConvergence"/>. This class stays responsible for what a
/// finished step means for the ones after it.
///
/// STATE: FORGOTTEN FIRST, WRITTEN LAST
///
/// The recorded mode is cleared immediately before the first command that could
/// change anything, and written again only when every resource that was not left
/// out has reached its target in a real run. The order is the point. A recorded
/// name stops being true the moment the first resource moves, and a transition
/// that is cancelled, times out, fails half way, or is killed along with the
/// machine may never reach a moment at which anything could tidy up afterwards.
/// Clearing first means the answer to "what mode is this machine in" is
/// "nobody knows" for exactly as long as that is the truth.
///
/// Nothing is cleared for a dry run, for a refusal, or for a transition that had
/// nothing left to do -- none of them can have moved anything, so the previous
/// answer is still as good as it was.
/// </remarks>
public sealed class ModeEngine
{
    /// <summary>
    /// How long a whole transition may take before it is given up on.
    /// </summary>
    /// <remarks>
    /// Comfortably more than the sum of what the shipped providers allow
    /// themselves, so that reaching it means something has genuinely gone wrong
    /// rather than that a television was slow. It is a backstop against a
    /// transition that would otherwise wait all day, not a budget anything is
    /// expected to spend.
    ///
    /// The number moved when the television gained a full chain. Waking one,
    /// waiting for it to join the network, switching its input, renegotiating the
    /// HDMI link, moving the desktop onto it and then selecting a picture mode is
    /// a minute of real work on a slow evening, and each step is allowed to retry.
    /// A backstop that the ordinary worst case can reach is not a backstop -- it
    /// is a transition that fails on the nights the television is slowest.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    private readonly ModeConfiguration _configuration;
    private readonly ModeStateStore _state;
    private readonly Dictionary<string, IModeResourceProvider> _providers;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Ever only one transition running at once. See
    /// <see cref="ApplyAsync(string, bool, IProgress{TransitionStep}?, CancellationToken)"/>
    /// for how a newer request supersedes rather than queues behind an older one.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Guards <see cref="_generation"/> and <see cref="_running"/> together.</summary>
    private readonly Lock _superviseLock = new();

    /// <summary>
    /// Bumped by every request that passes validation, before it contends for
    /// the gate. What makes a request the newest is holding the highest number
    /// when it is finally its turn to run, not the order it arrived in.
    /// </summary>
    private long _generation;

    /// <summary>The cancellation source the currently running transition is watching, if any.</summary>
    private CancellationTokenSource? _running;

    private readonly Lock _outcomeLock = new();
    private TransitionOutcome? _lastOutcome;
    private string? _lastAttemptedMode;
    private readonly HashSet<string> _successfulUnverifiedDispatches = new(StringComparer.OrdinalIgnoreCase);

    private enum RecoveryKind
    {
        None,
        Retry,
        Repair,
    }

    /// <param name="time">
    /// The clock every wait and every deadline is measured against. Injected so
    /// that tests of retrying, polling, settling and timing out cost no real
    /// seconds.
    /// </param>
    /// <param name="timeout">
    /// How long one transition may take. See <see cref="DefaultTimeout"/>.
    /// </param>
    public ModeEngine(
        ModeConfiguration configuration,
        ModeStateStore state,
        IReadOnlyList<IModeResourceProvider> providers,
        TimeProvider? time = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(providers);

        if (timeout is TimeSpan given && given <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout), given, "A transition needs some time to happen in.");
        }

        _configuration = configuration;
        _state = state;
        _time = time ?? TimeProvider.System;
        _timeout = timeout ?? DefaultTimeout;
        _providers = providers.ToDictionary(provider => provider.Key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every target this configuration offers, in configuration order: Spaces,
    /// then their Activities, then each Activity's Sources.
    /// </summary>
    public IReadOnlyList<ConfiguredTarget> Targets => _configuration.Targets.Targets;

    /// <summary>Every target's key, in configuration order.</summary>
    public IReadOnlyList<string> TargetKeys => _configuration.Targets.Keys;

    /// <summary>
    /// The targets and the one rule for resolving a reference to one, for
    /// whatever else in the application holds a saved reference -- picker
    /// favorites, the startup target -- so that it judges one the same way an
    /// activation will.
    /// </summary>
    public TargetCatalog Catalog => _configuration.Targets;

    /// <summary>
    /// The target a reference names, or null when it names none or more than
    /// one. The same resolution <see cref="ApplyAsync"/> uses, so nothing else
    /// has to guess at what a saved reference will turn out to mean.
    /// </summary>
    public ConfiguredTarget? ResolveTarget(string? reference) => _configuration.Targets.Resolve(reference);

    /// <summary>
    /// The resource providers this engine was built with.
    /// </summary>
    /// <remarks>
    /// Exists for hardware discovery, which wants to ask an
    /// <see cref="ApplicationProvider"/> what it knows about an application
    /// without re-reading the configuration document a second way. Nothing
    /// about a transition reaches into this: <see cref="ApplyAsync"/> and
    /// <see cref="ReadCurrentMode"/> still go through the provider dictionary
    /// this class already owns.
    /// </remarks>
    public IReadOnlyList<IModeResourceProvider> Providers => [.. _providers.Values];

    /// <summary>Whether a transition is running.</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>The recorded current target's key, or null if there is not one.</summary>
    public string? ReadCurrentTargetKey(out string? problem) => _state.Read(out problem)?.Mode;

    /// <summary>
    /// The most recent transition's outcome -- real, retried, repaired, or a dry
    /// run taken to observe -- or null before anything has run.
    /// </summary>
    /// <remarks>
    /// Kept so a health page can show something the moment it opens, rather than
    /// only after the next transition. A caller that wants a fresher answer than
    /// whatever last happened to run should take a dry run itself; this is a
    /// cache of the last thing this engine actually did, not a live probe.
    /// </remarks>
    public TransitionOutcome? LastOutcome
    {
        get { lock (_outcomeLock) { return _lastOutcome; } }
    }

    /// <summary>
    /// <see cref="LastOutcome"/>, read as per-resource health -- including
    /// whatever diagnostic detail a provider has to add. Null before anything
    /// has run.
    /// </summary>
    public ModeHealth? LastHealth
    {
        get
        {
            TransitionOutcome? outcome = LastOutcome;

            return outcome is null
                ? null
                : ModeHealth.From(outcome, _time.GetUtcNow(), _providers);
        }
    }

    /// <summary>
    /// Applies a target, or walks it without changing anything.
    /// </summary>
    /// <param name="mode">
    /// The target's key, its full display path, or an unambiguous name -- see
    /// <see cref="TargetCatalog"/>.
    /// </param>
    /// <param name="progress">
    /// Told about each step as it is decided, so a UI can show a long transition
    /// making headway rather than only its result.
    /// </param>
    public Task<TransitionOutcome> ApplyAsync(
        string mode,
        bool dryRun = false,
        IProgress<TransitionStep>? progress = null,
        CancellationToken cancellationToken = default) =>
        ApplyCoreAsync(mode, dryRun, progress, cancellationToken, RecoveryKind.None, retryHandled: null);

    private async Task<TransitionOutcome> ApplyCoreAsync(
        string mode,
        bool dryRun,
        IProgress<TransitionStep>? progress,
        CancellationToken cancellationToken,
        RecoveryKind recovery,
        IReadOnlySet<string>? retryHandled)
    {
        // Lookup and validation happen before the guard is taken, so a request
        // that was never going to run cannot make the engine look busy.
        if (!_configuration.Targets.TryResolve(mode, out ConfiguredTarget? target, out string? unresolved))
        {
            return Refused(mode?.Trim() ?? string.Empty, unresolved!);
        }

        if (!target!.Enabled)
        {
            return Refused(
                target.Key,
                $"'{target.DisplayPath}' is disabled. Enable it in Spaces before switching to it.",
                target: target);
        }

        ConfiguredMode configured = _configuration.Find(target.Key)!;

        if (!TransitionPlan.TryCreate(configured, _providers, out TransitionPlan? plan, out string? problem))
        {
            return Refused(configured.Name, problem!, target: target);
        }

        // Stamped and signalled before contending for the gate, so that
        // whichever transition is either running now or about to start is told
        // to stop at its next safe boundary. See the class remarks.
        long generation;

        lock (_superviseLock)
        {
            generation = ++_generation;
            _running?.Cancel();
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        CancellationTokenSource? linked = null;

        try
        {
            TransitionOutcome outcome;

            lock (_superviseLock)
            {
                if (_generation != generation)
                {
                    // A newer request arrived while this one waited for the
                    // gate. Nothing was ever dispatched for it, so nothing needs
                    // undoing -- it simply never happened.
                    outcome = Refused(
                        configured.Name,
                        $"'{configured.DisplayName}' was superseded by a newer request before it started.",
                        target: target);

                    RecordOutcome(configured.Name, outcome, recovery);
                    return outcome;
                }

                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _running = linked;
            }

            // Task.Run because a provider's Apply is a synchronous call into
            // whatever it talks to, and the caller is usually a UI thread. The
            // waits inside are asynchronous, but the work between them is not.
            // RunAsync reports every cancellation -- the caller's or this
            // engine's own supersede signal -- as an outcome, never as a thrown
            // OperationCanceledException, so nothing here needs to catch one.
            outcome = await Task.Run(
                () => RunAsync(plan!, dryRun, progress, linked.Token, recovery, retryHandled),
                CancellationToken.None).ConfigureAwait(false);

            // Attached once, here, rather than at each of the outcomes RunAsync
            // builds: which target this was is decided before the run starts and
            // cannot change during it.
            outcome = outcome with { Target = target };

            if (linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The linked token fired and it was not the caller's own: it can
                // only have been this engine cancelling the run to let a newer
                // request through. Said plainly, rather than leaving this read
                // as an unexplained cancellation.
                outcome = outcome with
                {
                    Message = $"{outcome.Message} Superseded by a newer request.",
                };
            }

            RecordOutcome(configured.Name, outcome, recovery);
            return outcome;
        }
        finally
        {
            lock (_superviseLock)
            {
                if (ReferenceEquals(_running, linked))
                {
                    _running = null;
                }
            }

            linked?.Dispose();
            _gate.Release();
        }
    }

    /// <summary>Remembers the last outcome, for <see cref="LastOutcome"/> and a retry.</summary>
    private void RecordOutcome(string mode, TransitionOutcome outcome, RecoveryKind recovery)
    {
        lock (_outcomeLock)
        {
            _lastOutcome = outcome;

            // A dry run and a request superseded before it started are not
            // attempts on the machine, so they do not become what a retry
            // targets -- retrying either of them would either change nothing on
            // purpose or repeat a request that was already abandoned.
            if (!outcome.DryRun && outcome.Status != TransitionStatus.Refused)
            {
                _lastAttemptedMode = mode;

                // A normal activation starts a new retry chain. Repair also
                // establishes a fresh basis for its target: every intentionally
                // unobservable resource it skipped belongs to a mode already
                // recorded as current, so retrying the repair must not launch it.
                if (recovery != RecoveryKind.Retry)
                {
                    _successfulUnverifiedDispatches.Clear();
                }

                foreach (TransitionStep step in outcome.Steps)
                {
                    bool repairKeepsOpenLoopHandled =
                        recovery == RecoveryKind.Repair &&
                        _providers.TryGetValue(step.Resource, out IModeResourceProvider? provider) &&
                        provider is IResourceObservabilityProvider observability &&
                        !observability.CanObserve(step.Target);

                    if (step.Status == TransitionStepStatus.AppliedUnverified || repairKeepsOpenLoopHandled)
                    {
                        _successfulUnverifiedDispatches.Add(UnverifiedDispatchKey(step.Resource, step.Target));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Retries the mode most recently applied for real, picking up wherever it
    /// is now.
    /// </summary>
    /// <remarks>
    /// Not a replay of exactly what failed: it is a fresh convergence of the
    /// same mode. Observable resources are read again and left alone when they
    /// are already correct. An intentionally unobservable resource is the one
    /// exception: when the previous attempt already dispatched it successfully,
    /// Retry carries that fact forward rather than launching it a second time.
    /// </remarks>
    /// <returns>
    /// The outcome, or null when nothing has been applied for real yet.
    /// Harmless to call after a clean success, too -- it re-verifies rather
    /// than re-applying, and reports nothing needed to move.
    /// </returns>
    public async Task<TransitionOutcome?> RetryAsync(
        IProgress<TransitionStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? mode;
        HashSet<string> retryHandled;

        lock (_outcomeLock)
        {
            mode = _lastAttemptedMode;
            retryHandled = new HashSet<string>(_successfulUnverifiedDispatches, StringComparer.OrdinalIgnoreCase);
        }

        if (mode is null)
        {
            return null;
        }

        return await ApplyCoreAsync(
            mode,
            dryRun: false,
            progress,
            cancellationToken,
            RecoveryKind.Retry,
            retryHandled).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-applies whichever mode is currently recorded, reading it and touching
    /// only what has drifted. Intentionally unobservable resources are reported
    /// as unverified and skipped because Repair has no evidence that they drifted.
    /// </summary>
    /// <remarks>
    /// Different from <see cref="RetryAsync"/> in what it targets: a retry
    /// chases the last request, whether or not it is the mode that is actually
    /// recorded as current; a repair chases whatever the machine is supposed to
    /// be right now, whether or not anything was recently applied at all. A
    /// mode that succeeded an hour ago and has since lost an endpoint has
    /// nothing to retry and everything to repair.
    /// </remarks>
    /// <returns>
    /// The outcome, or null when no mode is currently recorded -- there is
    /// nothing this machine is supposed to be, so there is nothing to repair it
    /// toward.
    /// </returns>
    public async Task<TransitionOutcome?> RepairAsync(
        IProgress<TransitionStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? mode = ReadCurrentTargetKey(out _);

        if (mode is null)
        {
            return null;
        }

        return await ApplyCoreAsync(
            mode,
            dryRun: false,
            progress,
            cancellationToken,
            RecoveryKind.Repair,
            retryHandled: null).ConfigureAwait(false);
    }

    /// <summary>
    /// Walks a resolved plan, or reports what it would have done.
    /// </summary>
    /// <remarks>
    /// One method for both, so a dry run cannot describe a transition that a
    /// real one would not carry out. A dry run takes the one read every step
    /// begins with and stops there: it never dispatches, never retries, never
    /// waits, never settles and never records.
    ///
    /// A step is only left behind once it has converged, which is what makes
    /// the plan's order mean something. A resource that runs after the displays
    /// runs after they are verified and have stopped moving, not merely after
    /// the call that commanded them returned.
    /// </remarks>
    private async Task<TransitionOutcome> RunAsync(
        TransitionPlan plan,
        bool dryRun,
        IProgress<TransitionStep>? progress,
        CancellationToken cancellationToken,
        RecoveryKind recovery,
        IReadOnlySet<string>? retryHandled)
    {
        // The key is what is recorded and retried; the display path is what
        // every message says. Keeping them apart is the whole reason a target
        // can be renamed without a reference to it breaking.
        string mode = plan.Mode;
        string name = plan.DisplayName;
        IReadOnlyList<PlannedResource> resources = plan.Steps;

        var steps = new List<TransitionStep>(resources.Count + plan.Omissions.Count);

        // Reported first, because they were decided first: this is what the
        // transition turned out not to be, before any of what it is.
        foreach (OmittedResource omitted in plan.Omissions)
        {
            steps.Add(Report(progress, Describe(omitted)));
        }

        if (plan.NothingLeft)
        {
            // Every resource the mode asked for is unavailable. Nothing is
            // changed, so nothing is cleared and nothing is recorded -- the mode
            // this machine was in is still the mode it is in.
            return Refused(
                mode,
                $"'{name}' changed nothing: none of the {plan.Omissions.Count} resource(s) it sets " +
                "are available on this machine.",
                steps);
        }

        // Fixed at the start, so that a transition's budget is the transition's
        // and not something each slow resource extends by being slow.
        var budget = new TransitionBudget(_time, _timeout);

        bool mutated = false;
        string? clearProblem = null;

        // What this transition has actually moved, in the order it moved it.
        // Only resources that were changed: one found already correct has not
        // disturbed anything that was standing on it. See
        // ITransitionAwareProvider.
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Handed to each resource and invoked at the last instant before that
        // resource could change anything. The engine, not the convergence loop,
        // decides what "something may have moved" means for the state file.
        //
        // Returning a message stops the dispatch. Forgetting the old mode is not
        // housekeeping that can be skipped when it is inconvenient: it is the
        // whole of what makes a half-applied machine admit to being one. A
        // transition that could not do it and went anyway would leave a file
        // confidently naming the mode the machine was being dragged out of, which
        // is worse than the transition not happening -- and a transition that has
        // not happened costs nothing but a message.
        string? BeforeMutating()
        {
            if (mutated)
            {
                return null;
            }

            if (!_state.TryClear(out clearProblem))
            {
                return clearProblem;
            }

            mutated = true;
            return null;
        }

        for (int index = 0; index < resources.Count; index++)
        {
            PlannedResource planned = resources[index];

            if (cancellationToken.IsCancellationRequested)
            {
                // Between steps, never during one. A resource that has started
                // is allowed to finish: killing it half way is precisely the
                // damage cancellation is supposed to avoid.
                AddRemaining(steps, resources, index, progress);

                return mutated
                    ? new TransitionOutcome(
                        mode,
                        TransitionStatus.Uncertain,
                        dryRun,
                        $"'{name}' was cancelled after {steps.Count(IsApplied)} of {resources.Count} resource(s).",
                        steps,
                        StateRecorded: false,
                        clearProblem,
                        Mutated: true)
                    : Refused(mode, $"'{name}' was cancelled before anything was changed.", steps);
            }

            // Before the read, because for a provider that cares what has already
            // moved, the read is one of the answers that depends on it.
            if (planned.Provider is ITransitionAwareProvider aware &&
                !TryTell(aware, changed, out string? confused))
            {
                steps.Add(Report(progress, Describe(
                    planned, current: null, TransitionStepStatus.Failed, confused)));

                AddRemaining(steps, resources, index + 1, progress);

                return new TransitionOutcome(
                    mode,
                    TransitionStatus.Failed,
                    dryRun,
                    $"'{name}' failed at {planned.ResourceName}. {confused}",
                    steps,
                    StateRecorded: false,
                    clearProblem,
                    mutated);
            }

            bool intentionallyUnobservable = IsIntentionallyUnobservable(planned);

            if (intentionallyUnobservable && dryRun)
            {
                steps.Add(Report(progress, Describe(
                    planned,
                    current: null,
                    TransitionStepStatus.Unverified,
                    "Runtime state is intentionally not observable; nothing was dispatched by this dry run.")));
                continue;
            }

            if (intentionallyUnobservable && recovery == RecoveryKind.Repair)
            {
                steps.Add(Report(progress, Describe(
                    planned,
                    current: null,
                    TransitionStepStatus.Unverified,
                    "Runtime state is intentionally not observable; Repair does not relaunch it without evidence that it is wrong.")));
                continue;
            }

            if (intentionallyUnobservable && recovery == RecoveryKind.Retry &&
                WasSuccessfullyHandled(retryHandled, planned))
            {
                steps.Add(Report(progress, Describe(
                    planned,
                    current: null,
                    TransitionStepStatus.Unverified,
                    "It was already dispatched successfully by the previous attempt; Retry does not launch it again.")));
                continue;
            }

            if (dryRun)
            {
                string? current = SafeRead(planned.Provider);

                // A resource that already reads as its target would not
                // actually be commanded by a real run -- unless the policy
                // insists on being dispatched regardless, in which case saying
                // "already correct" here would preview a step that would not
                // happen. Same rule ResourceConvergence applies for real; a dry
                // run that used a different one could show a clean pass for a
                // mode that would still touch the machine.
                steps.Add(Report(progress, Describe(
                    planned,
                    current,
                    Matches(current, planned.Target) && !planned.Policy.DispatchAtLeastOnce
                        ? TransitionStepStatus.AlreadyCorrect
                        : TransitionStepStatus.Planned)));

                continue;
            }

            if (budget.Expired)
            {
                AddRemaining(steps, resources, index, progress);

                return new TransitionOutcome(
                    mode,
                    TransitionStatus.Failed,
                    DryRun: false,
                    $"'{name}' ran out of time after {index} of {resources.Count} resource(s).",
                    steps,
                    StateRecorded: false,
                    clearProblem,
                    mutated);
            }

            ConvergenceResult result = await ResourceConvergence
                .RunAsync(planned, budget, BeforeMutating, cancellationToken)
                .ConfigureAwait(false);

            if (result.Status is ConvergenceStatus.AlreadyCorrect or ConvergenceStatus.Converged)
            {
                if (result.Status is ConvergenceStatus.Converged)
                {
                    changed.Add(planned.Key);
                }

                // AlreadyCorrect covers a resource the planner added, too. A
                // requirement that is already satisfied is satisfied; adding it
                // to the plan was never a decision to change it.
                steps.Add(Report(progress, Describe(
                    planned,
                    result.Before,
                    result.Status is ConvergenceStatus.AlreadyCorrect
                        ? TransitionStepStatus.AlreadyCorrect
                        : intentionallyUnobservable
                            ? TransitionStepStatus.AppliedUnverified
                            : TransitionStepStatus.Applied,
                    observed: result.Observed,
                    attempts: result.Attempts)));

                continue;
            }

            steps.Add(Report(progress, Describe(
                planned,
                result.Before,
                // Blocked is not a failure of this resource. Nothing was
                // dispatched for it, so it is reported as what it is: a step the
                // transition never got to.
                result.Status is ConvergenceStatus.Blocked
                    ? TransitionStepStatus.NotReached
                    : TransitionStepStatus.Failed,
                result.Problem,
                result.Observed,
                result.Attempts)));

            // Sequential execution stops at the first resource that did not get
            // there, whatever stopped it. Carrying on would apply the rest of a
            // mode that is already not going to be reached, spreading the mess
            // rather than containing it.
            AddRemaining(steps, resources, index + 1, progress);

            return result.Status switch
            {
                // Refused, because that is exactly what it is: the machine is
                // untouched and the mode it was in is still the mode it is in, so
                // the recorded name that could not be cleared is still true.
                ConvergenceStatus.Blocked => new TransitionOutcome(
                    mode,
                    TransitionStatus.Refused,
                    DryRun: false,
                    $"'{name}' was not started, and nothing was changed. {result.Problem}",
                    steps,
                    StateRecorded: false,
                    clearProblem,
                    Mutated: false),

                ConvergenceStatus.Cancelled => new TransitionOutcome(
                    mode,
                    TransitionStatus.Uncertain,
                    DryRun: false,
                    $"'{name}' was cancelled while {planned.ResourceName} was being changed.",
                    steps,
                    StateRecorded: false,
                    clearProblem,
                    mutated),

                ConvergenceStatus.TimedOut => new TransitionOutcome(
                    mode,
                    TransitionStatus.Failed,
                    DryRun: false,
                    $"'{name}' ran out of time at {planned.ResourceName}. {result.Problem}",
                    steps,
                    StateRecorded: false,
                    clearProblem,
                    mutated),

                _ => new TransitionOutcome(
                    mode,
                    TransitionStatus.Failed,
                    DryRun: false,
                    $"'{name}' failed at {planned.ResourceName}. {result.Problem}",
                    steps,
                    StateRecorded: false,
                    clearProblem,
                    mutated),
            };
        }

        if (dryRun)
        {
            // Nothing was changed, so nothing is cleared and nothing is recorded.
            return new TransitionOutcome(
                mode,
                TransitionStatus.Succeeded,
                DryRun: true,
                $"Dry run of '{name}' walked {resources.Count} resource(s)" +
                Mentioning(plan.Omissions) + ". Nothing was changed.",
                steps,
                StateRecorded: false,
                StateProblem: null);
        }

        bool recorded = _state.TryWrite(mode, _time.GetUtcNow(), out string? stateProblem);

        return new TransitionOutcome(
            mode,
            recorded ? TransitionStatus.Succeeded : TransitionStatus.Uncertain,
            DryRun: false,
            recorded
                ? $"'{name}' applied{Mentioning(plan.Omissions)}{MentioningUnverified(steps)}."
                : $"'{name}' was applied, but could not be recorded, so the current mode is unknown.",
            steps,
            recorded,
            stateProblem ?? clearProblem,
            mutated);
    }

    /// <summary>The tail of a message that has omissions to admit to.</summary>
    private static string Mentioning(IReadOnlyList<OmittedResource> omissions) =>
        omissions.Count == 0 ? string.Empty : $", without {string.Join(", ", omissions.Select(Name))}";

    private static string MentioningUnverified(IReadOnlyList<TransitionStep> steps)
    {
        int count = steps.Count(step =>
            step.Status is TransitionStepStatus.AppliedUnverified or TransitionStepStatus.Unverified);

        return count == 0
            ? string.Empty
            : $", with {count} unverified resource{(count == 1 ? string.Empty : "s")}";
    }

    private static string Name(OmittedResource omitted) => $"{omitted.ResourceName} '{omitted.Target}'";

    private static bool IsApplied(TransitionStep step) =>
        step.Status is TransitionStepStatus.Applied or TransitionStepStatus.AppliedUnverified;

    private static bool IsIntentionallyUnobservable(PlannedResource planned) =>
        planned.Provider is IResourceObservabilityProvider observability &&
        !observability.CanObserve(planned.Target);

    private static bool WasSuccessfullyHandled(IReadOnlySet<string>? handled, PlannedResource planned) =>
        handled?.Contains(UnverifiedDispatchKey(planned.Key, planned.Target)) == true;

    private static string UnverifiedDispatchKey(string resource, string target) =>
        $"{resource}\u001F{target}";

    private static bool Matches(string? current, string target) =>
        current is not null && string.Equals(current, target, StringComparison.OrdinalIgnoreCase);

    private static void AddRemaining(
        List<TransitionStep> steps,
        IReadOnlyList<PlannedResource> resources,
        int from,
        IProgress<TransitionStep>? progress)
    {
        for (int index = from; index < resources.Count; index++)
        {
            steps.Add(Report(progress, Describe(resources[index], current: null, TransitionStepStatus.NotReached)));
        }
    }

    /// <summary>
    /// One planned resource, reported as it stands.
    /// </summary>
    /// <remarks>
    /// The single place a step is built, so that whatever the plan knows about a
    /// resource -- not least which other resource asked for it -- reaches the
    /// caller whether it was applied, skipped, failed or never reached.
    /// </remarks>
    private static TransitionStep Describe(
        PlannedResource planned,
        string? current,
        TransitionStepStatus status,
        string? problem = null,
        string? observed = null,
        int attempts = 0) =>
        new(
            planned.Key,
            planned.ResourceName,
            planned.Target,
            current,
            status,
            problem,
            planned.RequiredBy,
            observed,
            attempts,
            planned.Optional);

    /// <summary>
    /// A resource that will not be happening, reported as a step so that nothing
    /// downstream has to learn a second shape in order to mention it.
    /// </summary>
    private static TransitionStep Describe(OmittedResource omitted) =>
        new(
            omitted.Key,
            omitted.ResourceName,
            omitted.Target,
            Current: null,
            TransitionStepStatus.Omitted,
            omitted.Reason,
            omitted.RequiredBy,
            Observed: null,
            Attempts: 0,
            Optional: true);

    private static TransitionStep Report(IProgress<TransitionStep>? progress, TransitionStep step)
    {
        progress?.Report(step);
        return step;
    }

    /// <summary>
    /// Tells a provider what has already moved, or reports why it could not be
    /// told.
    /// </summary>
    /// <remarks>
    /// Not swallowed, unlike a failed read. A read that cannot be taken has a
    /// defined meaning -- "apply it anyway" -- and this has none: a provider that
    /// refused to hear what the transition has done is about to decide what to do
    /// on the strength of not knowing, and the one thing worse than restarting an
    /// application unnecessarily is silently declining to restart one that needed
    /// it. So the step fails, with the machine no further along than it was.
    /// </remarks>
    private static bool TryTell(
        ITransitionAwareProvider provider,
        IReadOnlySet<string> changed,
        out string? problem)
    {
        try
        {
            provider.Preceded(changed);
            problem = null;
            return true;
        }
        catch (Exception exception)
        {
            problem = $"It could not be told what the transition had already changed. {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// A provider that cannot say where the machine is does not stop a
    /// transition: null simply means "apply it anyway".
    /// </summary>
    private static string? SafeRead(IModeResourceProvider provider)
    {
        try
        {
            return provider.Read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static TransitionOutcome Refused(
        string mode,
        string message,
        IReadOnlyList<TransitionStep>? steps = null,
        ConfiguredTarget? target = null) =>
        new(
            mode,
            TransitionStatus.Refused,
            DryRun: false,
            message,
            steps ?? [],
            StateRecorded: false,
            StateProblem: null,
            Mutated: false,
            target);
}

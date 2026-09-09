namespace PCMode.Engine;

/// <summary>How one resource's part of a transition ended.</summary>
internal enum ConvergenceStatus
{
    /// <summary>
    /// The machine was already there and stayed there, so nothing was
    /// dispatched.
    /// </summary>
    AlreadyCorrect,

    /// <summary>Dispatched, and -- where it can be checked -- confirmed.</summary>
    Converged,

    /// <summary>Every attempt allowed by the policy has been used up.</summary>
    Failed,

    /// <summary>The transition's own deadline arrived first.</summary>
    TimedOut,

    /// <summary>Cancelled while being applied, or while being waited for.</summary>
    Cancelled,

    /// <summary>
    /// Refused at the threshold: something that had to be true before the first
    /// command was not, so no command was issued.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Failed"/> because the machine is untouched. The
    /// only case today is a recorded mode that could not be forgotten -- see
    /// <see cref="ModeEngine"/>. Proceeding would leave a state file naming a mode
    /// the machine was about to be dragged out of, which is worse than not going.
    /// </remarks>
    Blocked,
}

/// <summary>What became of one resource, and the evidence for saying so.</summary>
/// <param name="Before">What was read before anything was dispatched.</param>
/// <param name="Observed">
/// The last value actually read back, or null when nothing was read back after
/// the command. A dispatch-only resource always reports null here: it was not
/// checked, and a value in this field would say that it was.
/// </param>
/// <param name="Attempts">How many times the command was actually issued.</param>
/// <param name="Mutated">
/// Whether a call that could have changed the machine was entered. True from the
/// instant before the first <see cref="IModeResourceProvider.Apply"/>, including
/// when that call threw: a command that failed may still have done half of what
/// it was asked.
/// </param>
internal sealed record ConvergenceResult(
    ConvergenceStatus Status,
    string? Before,
    string? Observed,
    int Attempts,
    bool Mutated,
    string? Problem
);

/// <summary>
/// Getting one resource to one target, and knowing when it is there.
/// </summary>
/// <remarks>
/// SEPARATE FROM THE ENGINE ON PURPOSE
///
/// <see cref="ModeEngine"/> owns when a transition may run, what a failure does
/// to the ones after it, and what is recorded. This owns the inside of a single
/// step: dispatch, readback, settling, and the bounds on all three. Keeping them
/// apart is what stops the engine's loop from becoming a place where a change to
/// retry behaviour can quietly change what gets written to the state file.
///
/// WHAT THE DEADLINE CAN AND CANNOT DO
///
/// It bounds waiting, not working. Every wait here is cancellable and clipped to
/// whichever boundary comes first, and the deadline is rechecked immediately
/// after every call into a provider -- but <see cref="IModeResourceProvider.Read"/>
/// and <see cref="IModeResourceProvider.Apply"/> are synchronous calls into
/// whatever the provider talks to, and nothing here can cut one short. What this
/// can do, and does, is refuse to act on their answers afterwards: a readback
/// that arrives after the deadline is not accepted as verification, and no new
/// command is issued once the time is gone. A provider whose call can hang is
/// responsible for its own timeout; that is why the theme's broadcast is sent
/// with one.
/// </remarks>
internal static class ResourceConvergence
{
    /// <summary>How a bounded wait ended.</summary>
    private enum WaitOutcome
    {
        Elapsed,
        TimedOut,
        Cancelled,
    }

    /// <summary>What watching the readback concluded.</summary>
    private enum Verification
    {
        /// <summary>Read back as the target, and held there for the settle period.</summary>
        Verified,

        /// <summary>Not there, within the time it was given.</summary>
        NotYet,

        TimedOut,
        Cancelled,
    }

    /// <param name="beforeMutating">
    /// Invoked at the last instant before this resource could change anything, and
    /// able to stop it: a message returned here refuses the dispatch and nothing
    /// is commanded. It is a veto rather than a notification because what it does
    /// -- forgetting the recorded mode -- is the thing that keeps a partly applied
    /// machine from claiming to be in a mode, and doing it "best effort" would
    /// leave exactly the stale state it exists to prevent.
    /// </param>
    public static async Task<ConvergenceResult> RunAsync(
        PlannedResource planned,
        TransitionBudget budget,
        Func<string?>? beforeMutating,
        CancellationToken cancellationToken
    )
    {
        IModeResourceProvider provider = planned.Provider;
        string target = planned.Target;
        ConvergencePolicy policy = planned.Policy;

        string? before = SafeRead(provider);

        if (cancellationToken.IsCancellationRequested)
        {
            return Stopped(ConvergenceStatus.Cancelled, before, "Cancelled before it was applied.");
        }

        if (budget.Expired)
        {
            return Stopped(ConvergenceStatus.TimedOut, before, "The transition ran out of time before it started.");
        }

        string? observed = before;

        // A resource that insists on being commanded is not asked whether it needs
        // to be. Its readback answers a narrower question than the command asks,
        // so agreement is not evidence that there is nothing to do.
        if (Matches(before, target) && !policy.DispatchAtLeastOnce)
        {
            // Already there. Whether that is worth anything depends on whether it
            // stays there: a resource with a settle period has one because its
            // readback is known to wobble, and a single agreeing sample taken
            // during a wobble is exactly the answer that period exists to reject.
            if (!policy.Verify || policy.Settle <= TimeSpan.Zero)
            {
                return new ConvergenceResult(
                    ConvergenceStatus.AlreadyCorrect,
                    before,
                    observed,
                    Attempts: 0,
                    Mutated: false,
                    Problem: null
                );
            }

            (Verification held, observed) = await WatchAsync(
                    provider,
                    target,
                    policy,
                    budget,
                    window: TimeSpan.Zero,
                    before,
                    cancellationToken
                )
                .ConfigureAwait(false);

            switch (held)
            {
                case Verification.Verified:
                    return new ConvergenceResult(
                        ConvergenceStatus.AlreadyCorrect,
                        before,
                        observed,
                        Attempts: 0,
                        Mutated: false,
                        Problem: null
                    );

                case Verification.Cancelled:
                    return Stopped(ConvergenceStatus.Cancelled, before, "Cancelled before it was applied.", observed);

                case Verification.TimedOut:
                    return Stopped(
                        ConvergenceStatus.TimedOut,
                        before,
                        $"The transition ran out of time while checking that it was still '{target}'.",
                        observed
                    );

                default:
                    // It drifted. It was never where it looked, so it is applied
                    // like anything else that is in the wrong place.
                    break;
            }
        }

        bool mutated = false;

        for (int attempt = 1; ; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // No new command is issued after a cancellation, exactly as none
                // is issued after the deadline. A resource already under way is
                // allowed to finish -- that is what makes cancelling safe -- but
                // starting another one would be beginning work that has just been
                // called off.
                return new ConvergenceResult(
                    ConvergenceStatus.Cancelled,
                    before,
                    observed,
                    attempt - 1,
                    mutated,
                    mutated ? "Cancelled between attempts." : "Cancelled before it was applied."
                );
            }

            if (budget.Expired)
            {
                // Nothing new is started once the time is gone, however much of
                // the policy's allowance is left. The allowance was always the
                // smaller of the two.
                return new ConvergenceResult(
                    ConvergenceStatus.TimedOut,
                    before,
                    observed,
                    attempt - 1,
                    mutated,
                    $"The transition ran out of time before it could be applied again; "
                        + $"it reads '{Describe(observed)}' rather than '{target}'."
                );
            }

            if (!mutated)
            {
                // The last moment at which nothing can have happened yet. What
                // the engine does here -- forgetting the mode it has recorded --
                // has to happen before the machine moves, not after it is known
                // to have moved, because a command that throws half way through
                // has still moved it.
                //
                // And it has to have worked. If the old mode could not be
                // forgotten, then dispatching anyway would move the machine while
                // a file went on naming where it used to be, which is the one
                // outcome none of this is allowed to produce. Nothing is
                // commanded, and Mutated stays false because nothing was.
                if (beforeMutating?.Invoke() is string refused)
                {
                    return new ConvergenceResult(
                        ConvergenceStatus.Blocked,
                        before,
                        observed,
                        Attempts: 0,
                        Mutated: false,
                        refused
                    );
                }

                mutated = true;
            }

            try
            {
                provider.Apply(target, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new ConvergenceResult(
                    ConvergenceStatus.Cancelled,
                    before,
                    observed,
                    attempt,
                    mutated,
                    "Cancelled while it was being applied."
                );
            }
            catch (Exception exception)
            {
                // What a failed command means depends on what else has happened
                // while it was running. A provider that throws because the socket
                // it was using was torn down by a cancellation has not diagnosed
                // anything, and reporting its message as the reason a mode failed
                // would send somebody looking for a fault in the television. The
                // same call taking longer than the whole transition had left is a
                // timeout whatever it eventually said.
                if (cancellationToken.IsCancellationRequested)
                {
                    return new ConvergenceResult(
                        ConvergenceStatus.Cancelled,
                        before,
                        observed,
                        attempt,
                        mutated,
                        "Cancelled while it was being applied."
                    );
                }

                if (budget.Expired)
                {
                    return new ConvergenceResult(
                        ConvergenceStatus.TimedOut,
                        before,
                        observed,
                        attempt,
                        mutated,
                        $"The transition ran out of time while it was being applied. "
                            + $"Last failure: {exception.Message}"
                    );
                }

                if (attempt >= policy.MaxAttempts)
                {
                    return new ConvergenceResult(
                        ConvergenceStatus.Failed,
                        before,
                        observed,
                        attempt,
                        mutated,
                        exception.Message
                    );
                }

                // The command itself refused -- the endpoint is not there yet,
                // the display did not take. Pausing before repeating it is the
                // only thing that makes repeating it worth doing.
                WaitOutcome pause = await WaitAsync(policy.RetryInterval, budget, cancellationToken)
                    .ConfigureAwait(false);

                if (pause is not WaitOutcome.Elapsed)
                {
                    return new ConvergenceResult(
                        pause is WaitOutcome.Cancelled ? ConvergenceStatus.Cancelled : ConvergenceStatus.TimedOut,
                        before,
                        observed,
                        attempt,
                        mutated,
                        pause is WaitOutcome.Cancelled
                            ? "Cancelled between attempts."
                            : $"The transition ran out of time. Last failure: {exception.Message}"
                    );
                }

                continue;
            }

            // The call returned, which says only that it returned. Everything
            // between here and a verdict is about how much that is worth.
            if (cancellationToken.IsCancellationRequested)
            {
                return new ConvergenceResult(
                    ConvergenceStatus.Cancelled,
                    before,
                    observed,
                    attempt,
                    mutated,
                    "Cancelled while it was being applied."
                );
            }

            if (!policy.Verify)
            {
                // Dispatch-only. Nothing is read back, and above all the read
                // taken before the command is not repurposed as though it were:
                // Observed stays null, so a report cannot imply a confirmation
                // that nobody made.
                if (policy.PostDispatchDelay > TimeSpan.Zero)
                {
                    WaitOutcome spread = await WaitAsync(policy.PostDispatchDelay, budget, cancellationToken)
                        .ConfigureAwait(false);

                    if (spread is not WaitOutcome.Elapsed)
                    {
                        return new ConvergenceResult(
                            spread is WaitOutcome.Cancelled ? ConvergenceStatus.Cancelled : ConvergenceStatus.TimedOut,
                            before,
                            Observed: null,
                            attempt,
                            mutated,
                            spread is WaitOutcome.Cancelled
                                ? "Cancelled while it was taking effect."
                                : "The transition ran out of time while it was taking effect."
                        );
                    }
                }
                else if (budget.Expired)
                {
                    // The command itself outlasted the transition. It may well
                    // have worked, but everything after it has run out of time.
                    return new ConvergenceResult(
                        ConvergenceStatus.TimedOut,
                        before,
                        Observed: null,
                        attempt,
                        mutated,
                        "The transition ran out of time while it was being applied."
                    );
                }

                return new ConvergenceResult(
                    ConvergenceStatus.Converged,
                    before,
                    Observed: null,
                    attempt,
                    mutated,
                    Problem: null
                );
            }

            observed = SafeRead(provider);

            (Verification verification, observed) = await WatchAsync(
                    provider,
                    target,
                    policy,
                    budget,
                    policy.RetryInterval,
                    observed,
                    cancellationToken
                )
                .ConfigureAwait(false);

            switch (verification)
            {
                case Verification.Verified:
                    return new ConvergenceResult(
                        ConvergenceStatus.Converged,
                        before,
                        observed,
                        attempt,
                        mutated,
                        Problem: null
                    );

                case Verification.Cancelled:
                    return new ConvergenceResult(
                        ConvergenceStatus.Cancelled,
                        before,
                        observed,
                        attempt,
                        mutated,
                        "Cancelled while it was settling."
                    );

                case Verification.TimedOut:
                    return new ConvergenceResult(
                        ConvergenceStatus.TimedOut,
                        before,
                        observed,
                        attempt,
                        mutated,
                        $"The transition ran out of time; it still reads "
                            + $"'{Describe(observed)}' rather than '{target}'."
                    );

                default:
                    if (attempt >= policy.MaxAttempts)
                    {
                        return new ConvergenceResult(
                            ConvergenceStatus.Failed,
                            before,
                            observed,
                            attempt,
                            mutated,
                            $"Applied {attempt} time(s), but it still reads "
                                + $"'{Describe(observed)}' rather than '{target}'."
                        );
                    }

                    // Straight back round without a further pause: the readback
                    // loop has just spent this attempt's whole interval waiting.
                    break;
            }
        }
    }

    /// <summary>
    /// Watches the readback until it agrees and stays agreed, or until the time
    /// it was given runs out.
    /// </summary>
    /// <param name="window">
    /// How long the resource is given to START agreeing. Zero for a resource that
    /// was already agreeing when it was first read, where the only question is
    /// whether it keeps doing so.
    /// </param>
    /// <param name="observed">
    /// The sample the caller has already taken. Passed in rather than read again
    /// here so that neither path takes two readings of the same instant.
    /// </param>
    /// <remarks>
    /// Once agreement has begun, the settle period is allowed to finish past the
    /// window -- bounded, like everything else, by the transition's deadline.
    /// Re-issuing a display topology that is a half-second away from being
    /// confirmed would throw away the work and start the negotiation again.
    ///
    /// Every wait is clipped to whichever of the four boundaries comes first: the
    /// end of this attempt, the end of the settle period, the transition's
    /// deadline, or a cancellation. Sleeping past a boundary and noticing
    /// afterwards would make the poll interval, which is only meant to say how
    /// often to look, into the resolution of every deadline in the system.
    /// </remarks>
    private static async Task<(Verification Result, string? Observed)> WatchAsync(
        IModeResourceProvider provider,
        string target,
        ConvergencePolicy policy,
        TransitionBudget budget,
        TimeSpan window,
        string? observed,
        CancellationToken cancellationToken
    )
    {
        long attemptStart = budget.Mark();
        long? agreeingSince = null;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return (Verification.Cancelled, observed);
            }

            if (budget.Expired)
            {
                // A reading that arrives after the deadline is a reading nobody
                // is entitled to act on, however welcome it would have been.
                return (Verification.TimedOut, observed);
            }

            if (Matches(observed, target))
            {
                agreeingSince ??= budget.Mark();

                if (budget.Since(agreeingSince.Value) >= policy.Settle)
                {
                    return (Verification.Verified, observed);
                }
            }
            else
            {
                // Continuously, or not at all. A value that agreed and then
                // stopped agreeing was passed through rather than reached.
                agreeingSince = null;
            }

            if (agreeingSince is null && budget.Since(attemptStart) >= window)
            {
                return (Verification.NotYet, observed);
            }

            TimeSpan wait = Clip(policy.PollInterval, Remaining(budget, attemptStart, window));

            if (agreeingSince is long since)
            {
                wait = Clip(wait, Remaining(budget, since, policy.Settle));
            }

            WaitOutcome waited = await WaitAsync(wait, budget, cancellationToken).ConfigureAwait(false);

            if (waited is WaitOutcome.Cancelled)
            {
                return (Verification.Cancelled, observed);
            }

            if (waited is WaitOutcome.TimedOut)
            {
                return (Verification.TimedOut, observed);
            }

            observed = SafeRead(provider);
        }
    }

    /// <summary>
    /// Waits, but never past the deadline and never through a cancellation.
    /// </summary>
    /// <remarks>
    /// A wait that would cross the deadline is served to the deadline and then
    /// reported as a timeout, rather than being cut short or overrun. The
    /// resource gets every moment it was actually promised, and not one after.
    /// </remarks>
    private static async Task<WaitOutcome> WaitAsync(
        TimeSpan interval,
        TransitionBudget budget,
        CancellationToken cancellationToken
    )
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return WaitOutcome.Cancelled;
        }

        TimeSpan remaining = budget.Remaining;

        if (remaining <= TimeSpan.Zero)
        {
            return WaitOutcome.TimedOut;
        }

        bool clipped = interval >= remaining;
        TimeSpan wait = clipped ? remaining : interval;

        if (wait > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(wait, budget.Clock, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return WaitOutcome.Cancelled;
            }
        }

        return clipped ? WaitOutcome.TimedOut : WaitOutcome.Elapsed;
    }

    /// <summary>How long is left of a period that started at a mark.</summary>
    private static TimeSpan Remaining(TransitionBudget budget, long from, TimeSpan length)
    {
        TimeSpan left = length - budget.Since(from);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>The shorter of two waits, ignoring one that has already run out.</summary>
    private static TimeSpan Clip(TimeSpan wait, TimeSpan boundary) =>
        boundary > TimeSpan.Zero && boundary < wait ? boundary : wait;

    private static ConvergenceResult Stopped(
        ConvergenceStatus status,
        string? before,
        string problem,
        string? observed = null
    ) => new(status, before, observed ?? before, Attempts: 0, Mutated: false, problem);

    private static bool Matches(string? current, string target) =>
        current is not null && string.Equals(current, target, StringComparison.OrdinalIgnoreCase);

    private static string Describe(string? observed) => observed ?? "unknown";

    /// <summary>
    /// A provider that cannot say where the machine is does not stop a
    /// transition: null simply means "not there yet".
    /// </summary>
    /// <remarks>
    /// A read that threw is therefore indistinguishable here from one that
    /// answered "I do not know", and deliberately so -- but that makes it the
    /// caller's job to notice that the reason it threw was a cancellation or an
    /// expired deadline. Every call site is followed immediately by both checks:
    /// the first read by the two at the top of <see cref="RunAsync"/>, and every
    /// later one by the top of <see cref="WatchAsync"/>'s loop. A read swallowed
    /// here can delay a verdict by one poll interval and cannot change it.
    /// </remarks>
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
}

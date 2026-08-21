namespace PCMode.Tray.Runtime;

/// <summary>What a user chose to do about an operation still in flight.</summary>
public enum ExitChoice
{
    /// <summary>Stay running. Nothing about the operation changes.</summary>
    Stay,

    /// <summary>Keep the shell alive until the operation finishes, then exit.</summary>
    WaitThenExit,

    /// <summary>
    /// Exit now. PCMode is left running and will finish on its own -- this
    /// shell simply will not be here to see how it turned out.
    /// </summary>
    ExitAndLeaveItRunning,
}

/// <summary>
/// Ends the shell without ending a transition.
/// </summary>
/// <remarks>
/// Ported from legacy PCMode.Tray's <c>ExitCoordinator</c>, simplified against
/// <see cref="PCMode.Engine.ModeEngine.IsBusy"/> directly rather than a
/// <c>ShellViewModel</c> -- Target has no shell-wide status/health surface yet
/// (that is the Diagnostics page's job), so "is anything running" is asked of
/// the engine itself, and "wait for it to finish" polls the same question
/// instead of awaiting a richer view model's own idle signal.
///
/// THE THING THIS EXISTS TO PREVENT
///
/// A transition takes real time -- it is switching monitors, audio devices and
/// a television -- so the window in which somebody can click Exit mid-transition
/// is seconds wide and entirely ordinary. Killing the process would stop
/// convergence part way through, leaving the machine in neither mode; exiting
/// and abandoning it silently is dishonest by omission, since the transition
/// really does keep going. So the choice is offered, in those terms, and
/// nothing here ever cancels the running operation.
/// </remarks>
public sealed class ExitCoordinator
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly Func<bool> _isIdle;
    private readonly Func<ExitChoice> _ask;
    private readonly Action _shutdown;

    private int _exiting;

    /// <param name="isIdle">Whether the engine is free to exit through right now.</param>
    /// <param name="ask">
    /// Puts the question to the user. Called only when an operation is
    /// actually in flight.
    /// </param>
    /// <param name="shutdown">Ends the application.</param>
    public ExitCoordinator(Func<bool> isIdle, Func<ExitChoice> ask, Action shutdown)
    {
        ArgumentNullException.ThrowIfNull(isIdle);
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(shutdown);

        _isIdle = isIdle;
        _ask = ask;
        _shutdown = shutdown;
    }

    /// <summary>Whether a shutdown has been decided on and is now waiting.</summary>
    public bool IsExiting => Volatile.Read(ref _exiting) != 0;

    /// <summary>Handles a user's request to close the shell.</summary>
    public async Task RequestExitAsync()
    {
        if (_isIdle())
        {
            // Claimed even here, so that a second click during WPF's own
            // shutdown does not run any of this again.
            if (Interlocked.CompareExchange(ref _exiting, 1, 0) == 0)
            {
                _shutdown();
            }

            return;
        }

        // Asked before the guard is claimed: somebody who chooses Stay has not
        // decided to exit, and must be able to ask again later.
        if (IsExiting)
        {
            // Already waiting for the operation to finish. Asking a second time
            // would put up a second dialog about a decision already made.
            return;
        }

        ExitChoice choice = _ask();

        if (choice == ExitChoice.Stay)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _exiting, 1, 0) != 0)
        {
            return;
        }

        if (choice == ExitChoice.ExitAndLeaveItRunning)
        {
            _shutdown();
            return;
        }

        // The child transition is not touched. It finishes convergence and
        // this shell outlives it by however long that takes.
        while (!_isIdle())
        {
            await Task.Delay(PollInterval).ConfigureAwait(true);
        }

        _shutdown();
    }
}

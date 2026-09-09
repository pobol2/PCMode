namespace PCMode.Engine;

/// <summary>
/// The one place the television's asynchronous world meets the synchronous
/// provider contract.
/// </summary>
/// <remarks>
/// WHY THE PROVIDER INTERFACE WAS NOT MADE ASYNCHRONOUS INSTEAD
///
/// Because every other provider would have paid for it. The theme writes two
/// registry values, the cursor moves a pointer, the display calls a Win32
/// function -- all genuinely synchronous, all currently readable as what they are.
/// Turning the interface asynchronous would have rewritten four working providers
/// and every fake in the tests so that one of them could await a socket.
///
/// So the boundary is here, and it is deliberately three lines. Network work stays
/// asynchronous, bounded and cancellable all the way down through
/// <see cref="LgTvConnection"/>; it is only made to look synchronous at the last
/// possible moment, in the one provider that needs it.
///
/// WHY Task.Run AND NOT A BARE GetResult
///
/// A blocking wait on a task whose continuation wants the caller's context
/// deadlocks. Providers are called from a thread pool thread today, where there is
/// no such context and a bare wait would be safe -- but "safe because of where it
/// happens to be called from" is a property that survives exactly until somebody
/// calls it from somewhere else. Starting the work on the pool removes the
/// question rather than answering it.
/// </remarks>
internal static class LgTvBridge
{
    /// <summary>
    /// Reads, and answers null rather than throwing.
    /// </summary>
    /// <remarks>
    /// <see cref="IModeResourceProvider.Read"/> promises null when it cannot read,
    /// and everything that can go wrong on the way to a television means exactly
    /// that: it is off, it is not answering, it has never been paired with, the
    /// network is busy. None of them is a reason to stop a transition, and the
    /// engine treats null as "apply it anyway", which is the right answer to all
    /// of them.
    /// </remarks>
    public static string? Wait(Func<CancellationToken, Task<string?>> read) => Wait(read, whenItCannot: null);

    /// <summary>
    /// Reads, and answers <paramref name="whenItCannot"/> rather than throwing.
    /// </summary>
    /// <remarks>
    /// For a read whose answer is not a string. The television's power is the one
    /// today: it has more states than a mode can name -- see
    /// <see cref="LgTvPower"/> -- and which of them the engine should be told about
    /// is <see cref="TvPowerProvider"/>'s decision to make, not this boundary's.
    /// </remarks>
    public static T Wait<T>(Func<CancellationToken, Task<T>> read, T whenItCannot)
    {
        try
        {
            return Task.Run(() => read(CancellationToken.None)).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return whenItCannot;
        }
    }

    /// <summary>
    /// Commands, and lets whatever went wrong reach the caller.
    /// </summary>
    /// <remarks>
    /// The opposite rule to reading, and for the opposite reason: this is
    /// <see cref="IModeResourceProvider.Apply"/>, whose contract is to throw on
    /// failure so that the message can be shown to somebody. A cancellation
    /// arrives as the exception convergence is written to recognise, because
    /// blocking on a task rethrows what it actually failed with rather than
    /// wrapping it.
    /// </remarks>
    public static void Wait(Func<CancellationToken, Task> command, CancellationToken cancellationToken)
    {
        Task.Run(() => command(cancellationToken), cancellationToken).GetAwaiter().GetResult();
    }
}

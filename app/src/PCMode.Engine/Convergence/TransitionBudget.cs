namespace PCMode.Engine;

/// <summary>
/// How much of a transition's time is left, measured by a clock that only ever
/// goes forwards.
/// </summary>
/// <remarks>
/// WHY NOT THE WALL CLOCK
///
/// <see cref="TimeProvider.GetUtcNow"/> is what the time is, and what the time is
/// can change while a transition is running: a clock synchronisation, a daylight
/// saving boundary, or somebody correcting the date. A deadline expressed as a
/// wall-clock instant would then arrive early, arrive late, or -- if the
/// correction went backwards far enough -- not arrive at all, and the resource
/// waiting on it would be the display with the black screens.
///
/// <see cref="TimeProvider.GetTimestamp"/> answers a different question: how far
/// along a monotonic counter the machine is. It is meaningless as a date and
/// exactly right as a stopwatch, which is the only thing durations here need.
///
/// The one place the wall clock is still used is the timestamp written into the
/// state file, which genuinely is a date somebody may read.
/// </remarks>
internal readonly struct TransitionBudget
{
    private readonly TimeProvider _time;
    private readonly long _start;

    public TransitionBudget(TimeProvider time, TimeSpan total)
    {
        _time = time;
        _start = time.GetTimestamp();
        Total = total;
    }

    /// <summary>The whole of what this transition was given.</summary>
    public TimeSpan Total { get; }

    /// <summary>
    /// The clock a wait is scheduled against, which is the same one measured
    /// here so that a wait and the deadline bounding it cannot disagree.
    /// </summary>
    public TimeProvider Clock => _time;

    /// <summary>How much of it has gone.</summary>
    public TimeSpan Elapsed => Between(_time, _start, _time.GetTimestamp());

    /// <summary>How much is left, never negative.</summary>
    public TimeSpan Remaining
    {
        get
        {
            TimeSpan remaining = Total - Elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>Whether there is no time left at all.</summary>
    public bool Expired => Elapsed >= Total;

    /// <summary>A reading of the monotonic counter, for measuring against later.</summary>
    public long Mark() => _time.GetTimestamp();

    /// <summary>How long ago <paramref name="mark"/> was taken.</summary>
    public TimeSpan Since(long mark) => Between(_time, mark, _time.GetTimestamp());

    /// <summary>
    /// The distance between two readings, in the units anybody thinks in.
    /// </summary>
    /// <remarks>
    /// Computed here rather than through
    /// <see cref="TimeProvider.GetElapsedTime(long, long)"/> so that the
    /// conversion depends only on the frequency the provider reports when it is
    /// asked, which is what a substituted clock in a test can actually control.
    /// </remarks>
    private static TimeSpan Between(TimeProvider time, long from, long to) =>
        TimeSpan.FromTicks((long)((to - from) * ((double)TimeSpan.TicksPerSecond / time.TimestampFrequency)));
}

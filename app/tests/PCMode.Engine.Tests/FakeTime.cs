namespace PCMode.Engine.Tests;

/// <summary>
/// A clock that costs nothing to wait on.
/// </summary>
/// <remarks>
/// WHY NOT JUST USE SHORT INTERVALS
///
/// Because the intervals under test are the shipped ones. A test that proved
/// convergence works when a display settles for five milliseconds would have
/// proved something about a policy nobody runs, and the arithmetic that actually
/// matters -- does an eight second window and a five hundred millisecond poll
/// leave room for a second attempt inside a ninety second transition? -- would
/// never be exercised at all.
///
/// HOW IT WORKS
///
/// Time moves when, and only when, something asks to wait. A request to wait two
/// seconds moves the clock two seconds and then completes almost immediately, so
/// every deadline, settle period and retry interval is measured against the
/// duration that was genuinely asked for, while the suite runs in milliseconds.
///
/// The callback is queued rather than invoked in place. <see cref="Task.Delay"/>
/// creates its timer and then stores it, and a timer that fired inside its own
/// constructor would be completing a promise that has not finished being built.
/// </remarks>
internal sealed class FakeTime : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private long _elapsedTicks;

    /// <summary>How much time every wait so far has asked for, in total.</summary>
    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks));

    public override DateTimeOffset GetUtcNow() => Start + Elapsed;

    /// <summary>
    /// The same fiction, told as a stopwatch.
    /// </summary>
    /// <remarks>
    /// Durations are measured against this rather than against the date, so it
    /// has to move with it. Reported in ticks so that the conversion between a
    /// reading and a <see cref="TimeSpan"/> is the identity, and a test that
    /// says two seconds is testing two seconds rather than a rounding.
    /// </remarks>
    public override long GetTimestamp() => Interlocked.Read(ref _elapsedTicks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        return new ImmediateTimer(this, callback, state, dueTime);
    }

    /// <summary>
    /// Spends time that nobody asked to wait for.
    /// </summary>
    /// <remarks>
    /// Waiting is not the only way a transition loses time. A synchronous call
    /// into a provider can take longer than the whole transition had left, and
    /// nothing can cut one short -- so a test needs to be able to say "this call
    /// took a minute" in order to check what happens to its answer afterwards.
    /// </remarks>
    public void Advance(TimeSpan by)
    {
        if (by > TimeSpan.Zero)
        {
            Interlocked.Add(ref _elapsedTicks, by.Ticks);
        }
    }

    /// <summary>A timer whose due time is spent rather than waited out.</summary>
    private sealed class ImmediateTimer : ITimer
    {
        private readonly FakeTime _time;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        private volatile bool _disposed;

        public ImmediateTimer(FakeTime time, TimerCallback callback, object? state, TimeSpan dueTime)
        {
            _time = time;
            _callback = callback;
            _state = state;

            Start(dueTime);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Start(dueTime);
            return true;
        }

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private void Start(TimeSpan dueTime)
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                return;
            }

            _time.Advance(dueTime);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!_disposed)
                {
                    _callback(_state);
                }
            });
        }
    }
}

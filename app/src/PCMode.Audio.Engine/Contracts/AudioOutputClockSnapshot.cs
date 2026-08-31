namespace PCMode.Audio.Engine.Contracts;

public readonly record struct AudioOutputClockSnapshot
{
    public double PlayedFramePosition { get; }

    public long SubmittedFramePosition { get; }

    public ulong QpcPosition100Nanoseconds { get; }

    public double QueuedFrameCount =>
        SubmittedFramePosition -
        PlayedFramePosition;

    public AudioOutputClockSnapshot(
        double playedFramePosition,
        long submittedFramePosition,
        ulong qpcPosition100Nanoseconds = 0)
    {
        if (!double.IsFinite(
                playedFramePosition) ||
            playedFramePosition < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(playedFramePosition));
        }

        if (submittedFramePosition < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(submittedFramePosition));
        }

        PlayedFramePosition =
            playedFramePosition;

        SubmittedFramePosition =
            submittedFramePosition;

        QpcPosition100Nanoseconds =
            qpcPosition100Nanoseconds;
    }
}
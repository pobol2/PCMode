namespace PCMode.Engine;

/// <summary>What to mirror, from where, to where, and how.</summary>
/// <param name="ReferenceId">
/// The Core Audio endpoint ID of the reference output -- the one Windows is
/// playing through, and the one being captured back. Resolved from a configured
/// name immediately before starting, and used as identity for the whole session:
/// an ID that stops naming a device is a device that has gone, which is exactly
/// the failure Party mode has to notice.
/// </param>
/// <param name="MirrorId">The endpoint ID of the delayed copy.</param>
/// <param name="Delay">How far behind the reference the mirror should run.</param>
/// <param name="Gain">Linear gain on the mirrored stream. 1.0 is unchanged.</param>
public sealed record AudioMirrorRequest(
    string ReferenceId,
    string ReferenceName,
    string MirrorId,
    string MirrorName,
    TimeSpan Delay,
    double Gain);

/// <summary>
/// How one endpoint's stream was actually opened.
/// </summary>
/// <param name="RequestedMs">
/// The buffer duration or engine period PCMode asked Windows for.
/// </param>
/// <param name="ActualMs">
/// What Windows actually granted, read back from the opened stream's buffer
/// size. Almost always different from <see cref="RequestedMs"/>: shared-mode
/// buffers are rounded to the endpoint's own period, and this is the number
/// that matters for latency, not the one that was asked for.
/// </param>
/// <param name="DevicePeriodMs">
/// The endpoint's own default servicing period, independent of which mode
/// opened it. How often Windows is willing to wake the stream at all.
/// </param>
/// <param name="LowLatencyEngine">
/// Whether this endpoint was opened through <c>IAudioClient3</c>'s low-latency
/// shared engine rather than the ordinary <c>IAudioClient</c> fallback.
/// </param>
public sealed record AudioMirrorBuffer(double RequestedMs, double ActualMs, double DevicePeriodMs, bool LowLatencyEngine);

/// <summary>What a running mirror is actually doing.</summary>
/// <param name="ReferenceBuffer">How the reference endpoint's capture stream was opened.</param>
/// <param name="MirrorBuffer">How the mirror endpoint's render stream was opened.</param>
/// <param name="CushionMs">
/// The safety margin added on top of the configured delay so the render thread
/// always has something ready, sized from the two endpoints' actual buffers
/// rather than a fixed guess.
/// </param>
/// <param name="BaselineLatencyMs">
/// The delay this mirror could not avoid even with a configured delay of zero:
/// roughly the capture side's buffering, the render side's buffering, and the
/// cushion between them. Never claimed as zero, because the buffering that
/// produces it is real and audible on a close listen.
/// </param>
/// <param name="EffectiveLatencyMs">
/// <see cref="BaselineLatencyMs"/> plus the delay actually configured -- the
/// total time between a sound leaving the reference endpoint and the mirrored
/// copy leaving the other one.
/// </param>
/// <param name="ClockOffsetMs">
/// How far the buffered audio is from where it should be, in milliseconds. Two
/// endpoints run off two crystals, so this wanders; what matters is that it stays
/// small and does not trend.
/// </param>
/// <param name="DriftCorrections">
/// How many render periods have been resampled slightly off nominal to hold the
/// offset down. A number that keeps climbing is normal -- correction is
/// continuous -- and one that never moves means the two clocks happen to agree.
/// </param>
/// <param name="Resyncs">
/// How many times the offset left its bounds and the mirror was re-aligned by
/// jumping rather than by resampling. Audible, and therefore worth counting
/// separately: a session with more than a couple has something wrong with it.
/// </param>
/// <param name="Underruns">
/// How many render periods went out as silence because nothing had been captured
/// for them.
/// </param>
/// <param name="EventDriven">
/// Whether the capture side is woken by Windows or polled. Loopback capture is
/// event-driven where the endpoint allows it and polled where it does not; both
/// work, and which one is in use belongs in a fault report.
/// </param>
public sealed record AudioMirrorHealth(
    bool Capturing,
    bool Rendering,
    string ReferenceFormat,
    string MirrorFormat,
    int DelayMs,
    double Gain,
    double ClockOffsetMs,
    long DriftCorrections,
    long Resyncs,
    long Underruns,
    long FramesMirrored,
    bool EventDriven,
    AudioMirrorBuffer ReferenceBuffer,
    AudioMirrorBuffer MirrorBuffer,
    double CushionMs,
    double BaselineLatencyMs,
    double EffectiveLatencyMs,
    string? LastFailure)
{
    /// <summary>Whether both halves are still running and nothing has failed.</summary>
    public bool Healthy => Capturing && Rendering && LastFailure is null;
}

/// <summary>
/// One live capture-and-render pair.
/// </summary>
/// <remarks>
/// The seam the automated tests replace. Everything about deciding whether Party
/// mode may start, which endpoints it uses, what happens when it is asked for
/// twice and what stops it is above this line and testable without a sound card;
/// everything below it moves samples between two real endpoints and can only be
/// proved by listening. See <see cref="PartyAudioHost"/>.
/// </remarks>
public interface IAudioMirror : IDisposable
{
    /// <summary>What was asked for, so the owner can tell whether it still is.</summary>
    AudioMirrorRequest Request { get; }

    /// <summary>A snapshot. Safe to call from any thread at any time.</summary>
    AudioMirrorHealth Health { get; }

    /// <summary>
    /// Changes the delay and gain of a running mirror, without a gap.
    /// </summary>
    /// <remarks>
    /// Live rather than restart-to-apply because aligning two speakers is done by
    /// ear, in small steps, while music is playing -- and a change that stopped
    /// and restarted the stream would silence the thing being listened to at
    /// exactly the moment it needs to be heard.
    /// </remarks>
    void Adjust(TimeSpan delay, double gain);
}

/// <summary>Starts a mirror, or explains why it cannot.</summary>
public interface IAudioMirrorFactory
{
    /// <exception cref="InvalidOperationException">
    /// Either endpoint could not be opened, or the pair cannot be streamed
    /// between. Thrown rather than reported, and never fudged into a working
    /// mirror on some other device: Party mode failing loudly is the whole
    /// difference between it and sound coming out of the wrong speakers.
    /// </exception>
    IAudioMirror Start(AudioMirrorRequest request);
}

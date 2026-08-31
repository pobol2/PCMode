using PCMode.Audio.Core.Models;
using PCMode.Audio.Protocol.Messages;

namespace PCMode.Engine;

/// <summary>
/// What the audio runtime is doing, as far as PCMode can see it.
/// </summary>
/// <remarks>
/// Deliberately the Host's own models rather than a copy of them. Health is
/// live runtime state -- endpoint states, formats, clock offsets, underruns,
/// route failures -- and re-describing it in engine-shaped types here would be
/// a second vocabulary that drifts from the one the Host actually reports.
/// </remarks>
/// <param name="Reachable">
/// Whether a Host answered. False is an ordinary answer on a machine where the
/// PCMode audio subsystem is not installed, not a fault in itself: device
/// aliases keep working without it.
/// </param>
/// <param name="Problem">Why the Host could not be reached, when it could not.</param>
public sealed record AudioRuntimeStatus(
    bool Reachable,
    string? Problem,
    AudioHostStatus? Host,
    AudioEngineHealth? Health)
{
    /// <summary>Nothing answered, and this is why.</summary>
    public static AudioRuntimeStatus Unreachable(string problem) => new(false, problem, null, null);

    /// <summary>The target the Host is holding, or null when it is holding none.</summary>
    public string? ActiveTargetId => Host?.ActiveTargetId;

    /// <summary>
    /// Whether that named target is the one actually running right now.
    /// </summary>
    /// <remarks>
    /// Both halves, and for the same reason a Party group used to need both: a
    /// Host still holding a target it failed to start is not the machine being
    /// in that state, and reporting it as such would make an unnoticed fault
    /// look like a mode somebody selected.
    /// </remarks>
    public bool IsRunning(string targetId) =>
        Host is { EngineState: AudioEngineRuntimeState.Running } host &&
        string.Equals(host.ActiveTargetId, targetId, StringComparison.Ordinal);
}

/// <summary>
/// The one live audio runtime, behind a seam so tests never start a Host.
/// </summary>
/// <remarks>
/// WHERE THE LINE IS
///
/// Everything above this interface is desired state: which target a mode wants,
/// whether its outputs exist, what to do when one does not. Everything below it
/// is the audio runtime -- a separate process owning the driver transport, the
/// route graph, DSP, buffering, resampling, synchronisation and their Health.
/// PCMode never holds an audio buffer, a WASAPI stream, a driver handle or a
/// route clock, and this interface is what keeps that true.
///
/// The methods are synchronous because <see cref="IModeResourceProvider.Apply"/>
/// is: a transition step either happened or it threw. The asynchrony below is
/// the pipe's, and it belongs to the implementation.
/// </remarks>
public interface IAudioRuntime : IDisposable
{
    /// <summary>
    /// Converges the runtime on <paramref name="target"/>, starting a Host if
    /// there is not one.
    /// </summary>
    /// <remarks>
    /// Idempotent, because the engine re-asserts a target on every verification
    /// pass and a user presses the same button twice: applying the equivalent
    /// target the Host is already running healthily changes nothing and restarts
    /// nothing.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The runtime could not be reached or the target could not be applied. The
    /// message is shown to the user.
    /// </exception>
    void Apply(AudioTarget target, CancellationToken cancellationToken);

    /// <summary>
    /// Changes one running route's gain, mute, endpoint volume, parametric EQ
    /// and delay, without reapplying the whole target.
    /// </summary>
    /// <remarks>
    /// Gain and EQ take effect immediately, with a short click-safe ramp.
    /// Delay cannot change without rebuilding that one route's output stream
    /// -- see <c>AudioEngine.Recalibrate</c> -- so it is briefly interrupted
    /// while every other route, and the target as a whole, keeps playing.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The runtime could not be reached, no target is running, or the
    /// endpoint named is not one of the running target's routes.
    /// </exception>
    void Recalibrate(
        AudioEndpointId endpointId,
        float volume,
        bool muted,
        TimeSpan delay,
        AudioProcessingProfile processing,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stops whatever the runtime is playing, and does nothing when there is no
    /// Host.
    /// </summary>
    /// <remarks>
    /// Never starts one. Applying a plain device alias calls this, and a machine
    /// with no PCMode audio subsystem installed must not launch a process in
    /// order to be told it has nothing to stop.
    /// </remarks>
    void Stop();

    /// <summary>The last answer, without going near the Host.</summary>
    /// <remarks>
    /// For anything on a redraw path. <see cref="Refresh"/> opens a pipe and
    /// waits on another process, which is not something a list should do while
    /// it is being painted.
    /// </remarks>
    AudioRuntimeStatus Status { get; }

    /// <summary>Asks the Host what it is doing, without ever starting one.</summary>
    AudioRuntimeStatus Refresh();

    /// <summary>
    /// A runtime that is not there, and says so immediately.
    /// </summary>
    /// <remarks>
    /// What an <see cref="AudioProvider"/> built without one gets. Two kinds of
    /// caller build a provider without a runtime: something validating a
    /// document, and a test. Neither should reach for a pipe -- the first because
    /// checking a file must not touch the machine, the second because a
    /// connection attempt that has to time out turns every unrelated test into a
    /// two-second one.
    ///
    /// Device aliases go on working through it, which is the whole point: an
    /// engine with no audio Host is an engine that cannot run audio targets, not
    /// one that cannot choose a playback device.
    /// </remarks>
    static IAudioRuntime None { get; } = new DisconnectedAudioRuntime();
}

/// <summary>The runtime that is not there. See <see cref="IAudioRuntime.None"/>.</summary>
internal sealed class DisconnectedAudioRuntime : IAudioRuntime
{
    private static readonly AudioRuntimeStatus Absent =
        AudioRuntimeStatus.Unreachable("This engine has no connection to the PCMode audio Host.");

    public AudioRuntimeStatus Status => Absent;

    public AudioRuntimeStatus Refresh() => Absent;

    public void Apply(AudioTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        throw new InvalidOperationException(
            $"'{target.Name}' cannot play: this engine has no connection to the PCMode audio Host.");
    }

    public void Recalibrate(
        AudioEndpointId endpointId,
        float volume,
        bool muted,
        TimeSpan delay,
        AudioProcessingProfile processing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(
            processing);

        throw new InvalidOperationException(
            "This engine has no connection to the PCMode audio Host, so nothing can be calibrated.");
    }

    public void Stop()
    {
        // Nothing is running, because nothing is connected. Silent rather than
        // throwing: applying a device alias calls this on its way past, and that
        // still has to happen.
    }

    public void Dispose()
    {
    }
}

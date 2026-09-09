namespace PCMode.Engine;

/// <summary>How a call to <see cref="EngineRuntime.Reload"/> turned out.</summary>
public enum RuntimeActivationStatus
{
    /// <summary>The configuration on disk was rebuilt, validated, and is now the one in charge.</summary>
    Activated,

    /// <summary>
    /// A transition was running, so nothing was rebuilt into. The previous
    /// configuration is untouched and still in charge.
    /// </summary>
    Deferred,

    /// <summary>
    /// The configuration on disk could not be rebuilt into a working engine.
    /// The previous configuration is untouched and still in charge.
    /// </summary>
    Failed,
}

/// <summary>Whether a reload took effect, and why not when it did not.</summary>
public sealed record RuntimeActivationOutcome(RuntimeActivationStatus Status, string Message)
{
    public bool Activated => Status == RuntimeActivationStatus.Activated;

    public static RuntimeActivationOutcome Ok(string message) => new(RuntimeActivationStatus.Activated, message);

    public static RuntimeActivationOutcome Deferred(string message) => new(RuntimeActivationStatus.Deferred, message);

    public static RuntimeActivationOutcome Failed(string message) => new(RuntimeActivationStatus.Failed, message);
}

/// <summary>
/// The one place the shell, tray, picker, diagnostics, and settings reach for
/// the running configuration.
/// </summary>
/// <remarks>
/// ONE ENGINE, SHARED RATHER THAN COPIED
///
/// Every part of the shell that needs the live engine reads <see cref="Engine"/>
/// here, and a save that calls <see cref="Reload"/> is what every one of them
/// then sees, without any of them rebuilding their own copy.
///
/// REBUILT AND VALIDATED WHILE IDLE, NEVER SWAPPED IN OVER A RUNNING ONE
///
/// A candidate is read from disk and fully validated -- through the same
/// <see cref="EngineBootstrap.Create"/> a real startup uses -- before it is
/// allowed to replace anything, and it replaces nothing at all if the engine
/// it would replace is in the middle of a transition. Two engine instances
/// both able to command hardware for the same machine is exactly the hazard a
/// single <see cref="ModeEngine"/>'s own busy guard exists to prevent, and
/// swapping the reference out from under a live transition would reopen it
/// from the outside. A reload that arrives while one is running is deferred,
/// not queued and not forced: the caller is told, and the transition that was
/// already running keeps running on the engine instance it started on.
///
/// A CANDIDATE THAT DOES NOT VALIDATE CHANGES NOTHING
///
/// <see cref="Reload"/> either activates a fully working replacement or
/// leaves the previous one exactly as it was -- there is no partial swap. A
/// caller can therefore always keep using <see cref="Engine"/> after a failed
/// reload with the same confidence it had before calling it.
/// </remarks>
public sealed class EngineRuntime : IDisposable
{
    private readonly string? _root;
    private readonly IAudioRuntime? _audio;
    private ModeEngine _engine;

    /// <param name="audio">
    /// The audio Host connection's owner, handed to every engine this builds. It
    /// has to outlive them: a reload replaces the audio provider, and a Host
    /// launched by the replaced one would be a process nothing left could steer.
    /// </param>
    public EngineRuntime(ModeEngine engine, string? root = null, IAudioRuntime? audio = null)
    {
        ArgumentNullException.ThrowIfNull(engine);

        _engine = engine;
        _root = root;
        _audio = audio;
    }

    /// <summary>The engine every operation should read and apply modes through right now.</summary>
    public ModeEngine Engine => Volatile.Read(ref _engine);

    /// <summary>
    /// Rebuilds and validates the configuration on disk, and activates it if
    /// nothing is running and it checks out.
    /// </summary>
    public RuntimeActivationOutcome Reload()
    {
        ModeEngine previous = Engine;

        if (previous.IsBusy)
        {
            return RuntimeActivationOutcome.Deferred(
                "A transition is running. The previous configuration is still active; try again once it finishes."
            );
        }

        ModeEngine candidate;

        try
        {
            candidate = EngineBootstrap.Create(_root, _audio);
        }
        catch (ModeConfigurationException exception)
        {
            return RuntimeActivationOutcome.Failed(
                "The saved configuration could not be activated, so the previous configuration is still "
                    + $"running. {exception.Message}"
            );
        }

        // Checked again immediately before the swap. This narrows the window
        // between the first check and the exchange below; it cannot close it
        // completely, and does not need to: a transition that manages to start
        // in between still runs to completion on the engine instance it
        // started on. The only consequence is that this reload's callers see
        // the new engine a moment later than they might have.
        if (previous.IsBusy)
        {
            return RuntimeActivationOutcome.Deferred(
                "A transition started while the configuration was being validated. The previous "
                    + "configuration is still active."
            );
        }

        Interlocked.Exchange(ref _engine, candidate);

        return RuntimeActivationOutcome.Ok("The updated configuration is now active.");
    }

    /// <summary>
    /// Closes PCMode's side of the audio Host connection.
    /// </summary>
    /// <remarks>
    /// The Host is deliberately left running. While an audio target is active
    /// the PCMode virtual endpoint is the machine's playback device, and a shell
    /// that killed the Host on its way out would leave Windows playing into an
    /// endpoint with nothing behind it -- silence, caused by closing a tray icon.
    /// See <see cref="AudioHostLink"/>.
    /// </remarks>
    public void Dispose()
    {
        _audio?.Dispose();
    }
}

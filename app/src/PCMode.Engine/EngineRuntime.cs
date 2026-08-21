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
/// The one place the shell, the tray, the picker, diagnostics, settings and
/// hardware discovery all reach for "the running configuration".
/// </summary>
/// <remarks>
/// ONE ENGINE, SHARED RATHER THAN COPIED
///
/// Before this existed, a hardware alias save rebuilt an engine only to hand
/// its discoverers to the Hardware window and throw the engine itself away --
/// so the shell, the tray and Diagnostics went on running the configuration
/// from before the save until the process restarted. This is what closes that
/// gap: every part of the shell that needs "the engine" or "the hardware
/// discoverers" reads <see cref="Engine"/> or <see cref="HardwareDiscoverers"/>
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
    private readonly Func<IGamepadCapabilitiesSource> _controllersFactory;
    private readonly PartyAudioHost? _party;
    private IGamepadCapabilitiesSource? _controllers;
    private ModeEngine _engine;

    /// <param name="controllersFactory">
    /// How to obtain the controller capabilities source, called at most once
    /// and cached -- so a caller that never opens Hardware or Diagnostics never
    /// pays for loading XInput. Defaults to the real one.
    /// </param>
    /// <param name="party">
    /// The Party mirror's owner, handed to every engine this builds. It has to
    /// outlive them: a reload replaces the audio provider, and a mirror owned by
    /// the replaced one would keep two threads and two endpoints with nothing
    /// left able to stop them.
    /// </param>
    public EngineRuntime(
        ModeEngine engine,
        string? root = null,
        Func<IGamepadCapabilitiesSource>? controllersFactory = null,
        PartyAudioHost? party = null)
    {
        ArgumentNullException.ThrowIfNull(engine);

        _engine = engine;
        _root = root;
        _party = party;
        _controllersFactory = controllersFactory ?? XInputCapabilitiesSource.Load;
    }

    /// <summary>The engine every operation should read and apply modes through right now.</summary>
    public ModeEngine Engine => Volatile.Read(ref _engine);

    /// <summary>
    /// The discoverers a hardware scan should use right now, built fresh from
    /// the current engine on every read.
    /// </summary>
    public IReadOnlyList<IHardwareDiscoverer> HardwareDiscoverers =>
        HardwareDiscoveryBootstrap.ProductionDiscoverers(Engine, _controllers ??= _controllersFactory());

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
                "A transition is running. The previous configuration is still active; try again once it finishes.");
        }

        ModeEngine candidate;

        try
        {
            candidate = EngineBootstrap.Create(_root, _party);
        }
        catch (ModeConfigurationException exception)
        {
            return RuntimeActivationOutcome.Failed(
                "The saved configuration could not be activated, so the previous configuration is still " +
                $"running. {exception.Message}");
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
                "A transition started while the configuration was being validated. The previous " +
                "configuration is still active.");
        }

        Interlocked.Exchange(ref _engine, candidate);

        return RuntimeActivationOutcome.Ok("The updated configuration is now active.");
    }

    /// <summary>
    /// Releases the controller capabilities source, if one was ever loaded, and
    /// stops the Party mirror if one is running.
    /// </summary>
    /// <remarks>
    /// The mirror is the reason this matters beyond tidiness. It is two threads
    /// holding two endpoints, and a shell that exited without stopping them would
    /// leave audio being duplicated by a process the user has just closed.
    /// </remarks>
    public void Dispose()
    {
        _party?.Dispose();

        if (_controllers is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

namespace PCMode.Engine;

/// <summary>
/// What to do to an application once it has a window, and how long that is worth
/// waiting for.
/// </summary>
/// <remarks>
/// The least certain thing in this application, and deliberately the smallest.
/// Bringing a window forward and typing at it is the only way some players are
/// told to go full screen, and there is no readback for a keystroke: nothing can
/// confirm it landed, or that it landed where it was aimed.
///
/// So it is bounded on both sides. It happens only behind a window that actually
/// exists, only within the time it was given, and -- see
/// <see cref="ApplicationProvider.Apply"/> -- only after a start this transition
/// actually performed. A readiness action that fired on every re-assert would
/// press the full-screen key at an application that is already full screen,
/// which on most players means leaving full screen.
/// </remarks>
/// <param name="Activate">Whether to bring the main window to the front first.</param>
/// <param name="Keys">The characters to type, or null to type nothing.</param>
/// <param name="Within">How long the window is worth waiting for.</param>
public sealed record ReadinessAction(bool Activate, string? Keys, TimeSpan Within);

/// <summary>How PCMode can observe an application's runtime state.</summary>
public enum ApplicationObservationKind
{
    /// <summary>The application is observed by a Windows process name.</summary>
    Process,

    /// <summary>No reliable runtime signal is available.</summary>
    None,
}

/// <summary>
/// The runtime signal used to decide whether an application is already in its
/// desired state.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="ApplicationDefinition.Start"/> deliberately:
/// a URI or launcher may still be closed-loop when it ultimately produces a
/// process PCMode can identify. <see cref="None"/> is an explicit statement that
/// no trustworthy observer exists; it is not a failed or unknown process read.
/// </remarks>
public sealed record ApplicationObservation
{
    private ApplicationObservation(ApplicationObservationKind kind, string? processName)
    {
        Kind = kind;
        ProcessName = processName;
    }

    /// <summary>The kind of runtime signal available.</summary>
    public ApplicationObservationKind Kind { get; }

    /// <summary>The normalized Windows process name when <see cref="Kind"/> is Process.</summary>
    public string? ProcessName { get; }

    /// <summary>Creates process-based observation, preserving today's closed-loop behavior.</summary>
    public static ApplicationObservation ForProcess(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);

        string normalized = processName.Trim();

        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("The process name must contain a name other than '.exe'.", nameof(processName));
        }

        return new ApplicationObservation(ApplicationObservationKind.Process, normalized);
    }

    /// <summary>Explicitly represents a launch target with no reliable runtime observer.</summary>
    public static ApplicationObservation None { get; } =
        new(ApplicationObservationKind.None, processName: null);
}

/// <summary>
/// One configured application: how to find it, start it, stop it, and what it
/// needs before any of that means anything.
/// </summary>
/// <remarks>
/// ENTIRELY CONFIGURATION
///
/// Nothing here is knowledge the engine has about any particular program. That
/// Steam is asked to open Big Picture with a URI, that a player needs a display
/// before its window will land in the right place, that one of them wants a key
/// pressed once it appears -- all of it is written down on the machine it is true
/// about. Adding an application is an entry in a file.
/// </remarks>
public sealed record ApplicationDefinition
{
    /// <summary>The name the modes use, which is also its resource key.</summary>
    public required string Name { get; init; }

    private ApplicationObservation _observation = ApplicationObservation.None;

    /// <summary>
    /// How PCMode observes this application's runtime state, independently from
    /// how it is launched.
    /// </summary>
    /// <remarks>
    /// Process observation remains the preferred/default closed-loop path.
    /// <see cref="ApplicationObservation.None"/> is explicit open-loop behavior
    /// for launch targets that have no trustworthy runtime signal; it is not a
    /// failed or unknown process read.
    /// </remarks>
    public ApplicationObservation Observation
    {
        get => _observation;
        init => _observation = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The process name Windows reports, without ".exe", when process observation
    /// is configured. Empty when no process observer exists.
    /// </summary>
    /// <remarks>
    /// This remains as a compatibility projection for existing process-based code.
    /// Setting it selects process observation, so older construction code keeps
    /// exactly the behavior it had.
    /// </remarks>
    public string ProcessName
    {
        get => Observation.ProcessName ?? string.Empty;
        init => _observation = ApplicationObservation.ForProcess(value);
    }

    /// <summary>Whether PCMode has a trustworthy runtime signal for this application.</summary>
    public bool CanObserve => Observation.Kind != ApplicationObservationKind.None;

    /// <summary>Whether PCMode can identify what to stop safely.</summary>
    public bool CanStop => CanObserve;

    /// <summary>Whether PCMode can identify a running instance well enough to restart it.</summary>
    public bool CanRestart => CanObserve;

    /// <summary>How to start it.</summary>
    public required LaunchCommand Start { get; init; }

    /// <summary>
    /// How to ask it to stop, or null to end the process.
    /// </summary>
    /// <remarks>
    /// A URI like steam://exit asks an application to close itself, which is what
    /// a user would have done, and lets it save whatever it was holding. Ending
    /// the process is what is left for an application that offers no such thing.
    /// </remarks>
    public LaunchCommand? Stop { get; init; }

    /// <summary>
    /// Whether the start command is issued even when it is already running.
    /// </summary>
    /// <remarks>
    /// For an application whose "running" says less than the mode is asking for.
    /// Steam is running whether or not it is in Big Picture, so a mode that wants
    /// Big Picture has to say so again -- see
    /// <see cref="ApplicationProvider.PolicyFor"/>, which is where that becomes a
    /// dispatch.
    ///
    /// Toward Running and never toward Stopped. Re-sending a stop URI at an
    /// application that is not running makes Windows start it purely to hand it
    /// the URI, at which point it reads "exit" and closes: the application
    /// visibly opens and shuts for no reason anybody watching could explain.
    /// </remarks>
    public bool Reactivate { get; init; }

    /// <summary>What must be settled before it may be started.</summary>
    public IReadOnlyList<ResourceRequirement> Requires { get; init; } = [];

    /// <summary>
    /// Resources whose changing leaves a running copy stale.
    /// </summary>
    /// <remarks>
    /// A window is placed when it opens. An application started while the desktop
    /// was on the monitors, on a machine that has since switched to the
    /// television, is running correctly on a screen that is no longer there -- and
    /// nothing about the process says so. The only evidence is that the display
    /// moved after it started, which is what
    /// <see cref="ITransitionAwareProvider"/> carries.
    ///
    /// Named rather than assumed. Whether an application survives a display change
    /// is a fact about that application, and restarting every one of them on every
    /// display change would close the game somebody was in the middle of.
    /// </remarks>
    public IReadOnlyList<string> RestartWhen { get; init; } = [];

    /// <summary>What to do once it has a window, if anything.</summary>
    public ReadinessAction? Ready { get; init; }

    /// <summary>How long one attempt at starting it is given.</summary>
    public TimeSpan StartWithin { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long it is given to go away once asked.</summary>
    public TimeSpan StopWithin { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The scheme of the start URI, when it is one.</summary>
    public string? StartScheme => Start.Uri is string uri ? SchemeOf(uri) : null;

    /// <summary>
    /// The scheme of a URI, or null when it has none.
    /// </summary>
    /// <remarks>
    /// A scheme is at least two characters. A single letter followed by a colon is
    /// a drive, and reading C:\Program Files\... as a URI in the "c" scheme would
    /// declare every absolute path on Windows unavailable.
    /// </remarks>
    public static string? SchemeOf(string value)
    {
        int colon = value.IndexOf(':', StringComparison.Ordinal);

        if (colon < 2)
        {
            return null;
        }

        for (int index = 0; index < colon; index++)
        {
            char character = value[index];

            bool usable = index == 0
                ? char.IsAsciiLetter(character)
                : char.IsAsciiLetterOrDigit(character) || character is '+' or '-' or '.';

            if (!usable)
            {
                return null;
            }
        }

        return value[..colon].ToLowerInvariant();
    }
}

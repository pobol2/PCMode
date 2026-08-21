namespace PCMode.Engine;

/// <summary>
/// One thing to start, or to ask to stop.
/// </summary>
/// <remarks>
/// TWO SHAPES BECAUSE WINDOWS HAS TWO
///
/// An executable is a file this machine has, run with arguments. A URI is handed
/// to the shell, which decides what handles it -- and the two fail differently,
/// are checked differently, and are available differently, so folding them into
/// one string would mean guessing which had been meant every time either was
/// used. The guess is made once, where the configuration is read, and never
/// again.
///
/// ARGUMENTS ARE A LIST, AND NEVER A LINE
///
/// One element per argument, passed through as separate values. Nothing here
/// builds a command line, so nothing here can be made to run a second command by
/// a path with a quote in it. A configuration that wants two arguments writes
/// two.
/// </remarks>
public sealed record LaunchCommand
{
    /// <summary>The program to run, expanded, or null when this is a URI.</summary>
    public string? Executable { get; init; }

    /// <summary>One element per argument. Never a joined command line.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>What to hand to the shell, or null when this is a program.</summary>
    public string? Uri { get; init; }

    /// <summary>Whether this is handed to the shell rather than run directly.</summary>
    public bool IsUri => Uri is not null;

    /// <summary>What to call this in a message.</summary>
    public string Describe() => Uri ?? Executable ?? "nothing";
}

/// <summary>
/// Starting, stopping and nudging programs, and nothing else.
/// </summary>
/// <remarks>
/// WHY THIS IS AN INTERFACE
///
/// Because the alternative is a test suite that starts real applications on a
/// real desktop. Every rule worth having here -- that an application is not
/// launched twice, that a stale one is stopped before it is started again, that a
/// readiness action fires once and only after a start, that nothing is dispatched
/// during a dry run -- is a rule about decisions, and a decision can be checked
/// without a process ever existing.
///
/// NARROW ON PURPOSE
///
/// By name, never by handle. What the resources here need to know is "is
/// something called this running", and process identities are a detail of the
/// implementation that would otherwise have to be modelled by every fake.
///
/// The waits belong here rather than in the provider for the same reason: they
/// are the part that genuinely needs a clock and a real process to wait on, and
/// keeping them behind the boundary is what lets a test answer them instantly.
/// </remarks>
public interface IProcessHost
{
    /// <summary>Whether anything of that name is running.</summary>
    bool IsRunning(string processName);

    /// <summary>
    /// Starts a program, or hands a URI to the shell.
    /// </summary>
    /// <remarks>
    /// Returns as soon as it has been asked for. Whether the application actually
    /// arrived is a question for the readback, which is the only thing that can
    /// answer it honestly.
    /// </remarks>
    void Start(LaunchCommand command);

    /// <summary>Ends every process of that name outright.</summary>
    void Kill(string processName);

    /// <summary>
    /// Waits for everything of that name to be gone, and says whether it is.
    /// </summary>
    bool WaitUntilStopped(string processName, TimeSpan within, CancellationToken cancellationToken);

    /// <summary>
    /// Waits for a process of that name to have a main window, and says whether
    /// it got one.
    /// </summary>
    /// <remarks>
    /// The gate every readiness action is behind. Activating or typing at an
    /// application that has started but has not drawn anything yet sends the
    /// keystroke to whatever was in front of it, which on this desktop is
    /// whatever the user was doing.
    /// </remarks>
    bool WaitForWindow(string processName, TimeSpan within, CancellationToken cancellationToken);

    /// <summary>
    /// Brings that application's main window to the front, and says whether it
    /// went.
    /// </summary>
    bool Activate(string processName);

    /// <summary>
    /// Types <paramref name="keys"/> at that application.
    /// </summary>
    /// <remarks>
    /// Plain printable characters only: the configuration refuses anything else
    /// when it is read. A key vocabulary with modifiers and chords would be
    /// a small scripting language, and a small scripting language that types at
    /// whatever is in front is not a small feature.
    /// </remarks>
    void SendKeys(string processName, string keys);

    /// <summary>Whether that file is there.</summary>
    bool FileExists(string path);

    /// <summary>
    /// Whether anything on this machine handles that URI scheme.
    /// </summary>
    /// <remarks>
    /// The same question the shell asks. Without it a URI-launched application
    /// that is not installed is indistinguishable from one that is slow: the
    /// launch appears to succeed, nothing ever arrives, and the mode fails on a
    /// timeout rather than saying that Steam is not installed.
    /// </remarks>
    bool HandlesUriScheme(string scheme);
}

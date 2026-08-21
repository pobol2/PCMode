namespace PCMode.Engine.Tests;

/// <summary>
/// A desktop with programs on it, none of them real.
/// </summary>
/// <remarks>
/// Every rule worth having about applications is a rule about decisions -- do not
/// launch a second copy, stop a stale one before starting it again, type only
/// behind a window, dispatch nothing during a dry run -- and every one of them
/// can be checked without a process ever existing. Which is fortunate, because
/// the alternative is a test suite that opens Steam on somebody's desktop.
///
/// WHAT A LAUNCH TURNS INTO IS THE TEST'S TO SAY
///
/// The fake has no idea which process a command produces, and it must not guess:
/// that a URI ends up as a process called "steam" is exactly the knowledge the
/// configuration exists to supply, and a fake that inferred it would be checking
/// its own inference. So a test states it -- <see cref="Produces"/> and
/// <see cref="Ends"/> -- in the same terms the real machine states it in.
/// </remarks>
internal sealed class FakeProcessHost : IProcessHost
{
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _did = [];

    /// <summary>Everything that was done, in order, as short readable phrases.</summary>
    public IReadOnlyList<string> Did => _did;

    /// <summary>Every command handed to Start, in order.</summary>
    public List<LaunchCommand> Started { get; } = [];

    /// <summary>Everything typed, in order.</summary>
    public List<string> Typed { get; } = [];

    /// <summary>Files this machine has.</summary>
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>URI schemes something on this machine opens.</summary>
    public HashSet<string> Schemes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What each launch turns into: what was launched, and what appears.</summary>
    public Dictionary<string, string> Produces { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What each launch closes, for a stop command like steam://exit.</summary>
    public Dictionary<string, string> Ends { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a launched program actually turns up.</summary>
    public bool StartingWorks { get; set; } = true;

    /// <summary>Whether a program asked to stop actually goes.</summary>
    public bool StoppingWorks { get; set; } = true;

    /// <summary>Whether a running program has a window to act on.</summary>
    public bool WindowAppears { get; set; } = true;

    /// <summary>Whether Windows lets the foreground be moved.</summary>
    public bool ActivationWorks { get; set; } = true;

    /// <summary>Thrown from IsRunning, when set.</summary>
    public Exception? LookingFails { get; set; }

    /// <summary>Puts a program on the desktop before the test begins.</summary>
    public FakeProcessHost AlreadyRunning(params string[] processNames)
    {
        foreach (string name in processNames)
        {
            _running.Add(name);
        }

        return this;
    }

    /// <summary>Puts a file on the machine, so an application looks installed.</summary>
    public FakeProcessHost Installed(params string[] paths)
    {
        foreach (string path in paths)
        {
            Files.Add(path);
        }

        return this;
    }

    /// <summary>Registers a URI scheme, so a URI-launched application looks installed.</summary>
    public FakeProcessHost Handles(params string[] schemes)
    {
        foreach (string scheme in schemes)
        {
            Schemes.Add(scheme);
        }

        return this;
    }

    /// <summary>Says that launching <paramref name="what"/> makes <paramref name="process"/> appear.</summary>
    public FakeProcessHost Launching(string what, string process)
    {
        Produces[what] = process;
        return this;
    }

    /// <summary>Says that launching <paramref name="what"/> closes <paramref name="process"/>.</summary>
    public FakeProcessHost Closing(string what, string process)
    {
        Ends[what] = process;
        return this;
    }

    public bool IsRunning(string processName) =>
        LookingFails is not null ? throw LookingFails : _running.Contains(processName);

    public void Start(LaunchCommand command)
    {
        Started.Add(command);
        _did.Add($"start {command.Describe()}");

        if (Ends.TryGetValue(command.Describe(), out string? closed) && StoppingWorks)
        {
            _running.Remove(closed);
            return;
        }

        if (Produces.TryGetValue(command.Describe(), out string? opened) && StartingWorks)
        {
            _running.Add(opened);
        }
    }

    public void Kill(string processName)
    {
        _did.Add($"kill {processName}");

        if (StoppingWorks)
        {
            _running.Remove(processName);
        }
    }

    public bool WaitUntilStopped(string processName, TimeSpan within, CancellationToken cancellationToken)
    {
        _did.Add($"wait for {processName} to stop");
        return !_running.Contains(processName);
    }

    public bool WaitForWindow(string processName, TimeSpan within, CancellationToken cancellationToken)
    {
        _did.Add($"wait for {processName}'s window");
        return WindowAppears && _running.Contains(processName);
    }

    public bool Activate(string processName)
    {
        _did.Add($"activate {processName}");
        return ActivationWorks && _running.Contains(processName);
    }

    public void SendKeys(string processName, string keys)
    {
        _did.Add($"type '{keys}' at {processName}");
        Typed.Add(keys);
    }

    public bool FileExists(string path) => Files.Contains(path);

    public bool HandlesUriScheme(string scheme) => Schemes.Contains(scheme);
}

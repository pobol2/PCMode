using System.Text.Json;

namespace PCMode.Engine;

/// <summary>
/// One configured application, either process-observed or explicitly launch-only.
/// </summary>
/// <remarks>
/// ONE PROVIDER PER APPLICATION
///
/// Because a mode sets them one at a time: Movie runs a player and stops a game,
/// Gaming does the reverse, and every mode has an opinion about all of them. A
/// single resource holding the whole set would need a vocabulary for "these and
/// not those", and would have to re-implement the ordering, the readback and the
/// optionality that the planner already gives each resource for nothing.
///
/// Which applications exist is therefore decided before the providers are built.
/// See <see cref="EngineBootstrap"/>.
///
/// CLOSED LOOP WHEN THERE IS SOMETHING TO WATCH
///
/// Process-observed applications verify both start and stop by readback: an
/// application that started has a process, and one that was asked to close stops
/// having one. A launch-only application explicitly opts out of that claim. It is
/// dispatched once and reported as unverified rather than inventing a process
/// state that does not exist.
///
/// WHAT IS DELIBERATELY NOT DONE HERE
///
/// No waiting, except the short bounded kind a readiness action needs. The
/// convergence loop already owns "keep looking until it is there or the time is
/// gone", it does it against the transition's deadline rather than a second one
/// of this provider's own, and it can be cancelled. A provider that waited as
/// well would be spending the same seconds twice.
/// </remarks>
public sealed class ApplicationProvider
    : IConfigurableProvider,
        IDependentProvider,
        IConvergentProvider,
        IAvailableProvider,
        ITransitionAwareProvider,
        IResourceObservabilityProvider
{
    /// <summary>The value a mode writes to have this application running.</summary>
    public const string Running = ApplicationCatalog.Running;

    /// <summary>The value a mode writes to have this application stopped.</summary>
    public const string Stopped = ApplicationCatalog.Stopped;

    private readonly IProcessHost _host;

    /// <summary>What the transition has changed so far. See <see cref="Preceded"/>.</summary>
    private IReadOnlySet<string> _changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this step has started the application itself.
    /// </summary>
    /// <remarks>
    /// What stops a restart from becoming a loop. Staleness is a fact about a copy
    /// that was already running when the display moved; a copy this step started
    /// afterwards is by definition running against what is there now. Without
    /// this, the readback that follows a restart would find the application stale
    /// all over again -- because the display did still change -- and the resource
    /// would restart it until it ran out of attempts.
    ///
    /// Cleared at the start of every step, which is what <see cref="Preceded"/>
    /// already marks.
    /// </remarks>
    private bool _startedHere;

    private ApplicationDefinition _definition;

    /// <param name="definition">
    /// What this application is, as the document that built it said. Replaced
    /// when the document is read properly -- see <see cref="TryConfigure"/> --
    /// which is the pass that checks it.
    /// </param>
    public ApplicationProvider(ApplicationDefinition definition, IProcessHost host)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(host);

        _definition = definition;
        _host = host;
    }

    /// <summary>The application's name, which is what a mode writes.</summary>
    public string Key => _definition.Name;

    public string DisplayName => _definition.Name;

    /// <summary>Shared with every other application. One list, one section.</summary>
    public string SettingsKey => ApplicationCatalog.SectionKey;

    /// <summary>What this application is, as configured.</summary>
    public ApplicationDefinition Definition => _definition;

    public bool TryConfigure(JsonElement? settings, out string? problem)
    {
        if (
            !ApplicationCatalog.TryRead(
                settings,
                out IReadOnlyDictionary<string, ApplicationDefinition> applications,
                out problem
            )
        )
        {
            return false;
        }

        if (!applications.TryGetValue(_definition.Name, out ApplicationDefinition? mine))
        {
            // Unreachable through the ordinary path, which builds these from the
            // same document. Reachable by handing an engine a different one, and
            // an application that quietly kept settings from a document nobody is
            // using any more would be the hardest kind of wrong to see.
            problem =
                $"'{ApplicationCatalog.SettingsPath}' no longer defines '{_definition.Name}', "
                + "which this build was started with.";

            return false;
        }

        _definition = mine;
        problem = null;
        return true;
    }

    /// <summary>
    /// What starting it needs. Nothing at all is needed to stop it.
    /// </summary>
    /// <remarks>
    /// The asymmetry is the point, and it is why requirements are keyed by target.
    /// A player needs the television showing a picture before its window will open
    /// in the right place; closing that same player needs nothing, and a mode that
    /// dragged a television awake in order to shut something down would be doing
    /// the opposite of what it was asked.
    /// </remarks>
    public IReadOnlyList<ResourceRequirement> RequirementsFor(string target) =>
        IsRunning(target) ? _definition.Requires : [];

    /// <summary>
    /// Observable applications are watched and retried; launch-only applications
    /// are dispatched once because there is nothing trustworthy to poll.
    /// </summary>
    /// <remarks>
    /// Toward Running for a process-observed application, the dispatch is repeated only if the process never
    /// appears -- and <see cref="Apply"/> checks before every launch, so a repeat
    /// cannot produce a second copy of something that was merely slow.
    ///
    /// <see cref="ConvergencePolicy.DispatchAtLeastOnce"/> for an application that
    /// asked to be reactivated, and only toward Running. Its readback answers a
    /// narrower question than the command asks: Steam is running whether or not it
    /// is in Big Picture, so "it is already running" is true and is not a reason
    /// to skip. Toward Stopped it would fire the stop command at an application
    /// that is not running, which for a URI means Windows starting it purely to
    /// hand it "exit".
    /// </remarks>
    public ConvergencePolicy PolicyFor(string target)
    {
        if (!_definition.CanObserve)
        {
            // A launch-only application has no state worth polling. One successful
            // dispatch is the entire claim PCMode can honestly make; repeating it
            // because nothing can confirm it would only launch duplicates.
            return ConvergencePolicy.DispatchOnce;
        }

        return IsRunning(target)
            ? new ConvergencePolicy
            {
                Verify = true,
                MaxAttempts = 3,
                RetryInterval = _definition.StartWithin,
                PollInterval = TimeSpan.FromMilliseconds(250),
                DispatchAtLeastOnce = _definition.Reactivate,
            }
            : new ConvergencePolicy
            {
                Verify = true,
                MaxAttempts = 2,
                RetryInterval = _definition.StopWithin,
                PollInterval = TimeSpan.FromMilliseconds(250),
            };
    }

    /// <summary>
    /// Whether this target has a trustworthy runtime signal. A false answer is
    /// intentional open-loop behavior, not a transient read failure.
    /// </summary>
    public bool CanObserve(string target) => _definition.CanObserve;

    /// <summary>
    /// Whether this machine has the application at all.
    /// </summary>
    /// <remarks>
    /// Only ever asked about starting it. An application that is not installed is
    /// already stopped, so a mode asking for that has nothing to be unable to
    /// do -- and reporting it unavailable would make every mode that tidies up
    /// after itself refuse to run on a machine that has one fewer program.
    ///
    /// A URI is answered the way the shell answers it: is anything registered for
    /// the scheme. Without that, a missing URI-launched application looks exactly
    /// like a slow one -- the launch appears to succeed, nothing ever arrives, and
    /// the mode fails on a timeout instead of saying that Steam is not installed.
    /// </remarks>
    public ResourceAvailability AvailabilityOf(string target)
    {
        if (!IsRunning(target))
        {
            return ResourceAvailability.Available;
        }

        LaunchCommand start = _definition.Start;

        try
        {
            if (start.Uri is not null)
            {
                string scheme = _definition.StartScheme ?? string.Empty;

                return _host.HandlesUriScheme(scheme)
                    ? ResourceAvailability.Available
                    : ResourceAvailability.Unavailable(
                        $"nothing on this machine opens '{scheme}:' links, so {_definition.Name} does not look installed."
                    );
            }

            return _host.FileExists(start.Executable!)
                ? ResourceAvailability.Available
                : ResourceAvailability.Unavailable($"'{start.Executable}' is not there.");
        }
        catch (Exception exception)
        {
            // A lookup that failed has not established that the application is
            // missing, and only a definite no may drop a resource from a mode.
            return ResourceAvailability.Unknown(
                $"whether {_definition.Name} is installed could not be checked. {exception.Message}"
            );
        }
    }

    public bool IsValidValue(string value, out string? problem)
    {
        if (IsRunning(value))
        {
            problem = null;
            return true;
        }

        if (IsStopped(value))
        {
            if (_definition.CanStop)
            {
                problem = null;
                return true;
            }

            problem =
                $"{_definition.Name} is launch-only. PCMode cannot stop it because no reliable runtime identity is configured.";
            return false;
        }

        problem = _definition.CanStop
            ? $"'{value}' is not something {_definition.Name} can be. "
                + $"Use {ApplicationCatalog.Running} or {ApplicationCatalog.Stopped}."
            : $"'{value}' is not something {_definition.Name} can be. "
                + $"Launch-only applications support {ApplicationCatalog.Running} only.";

        return false;
    }

    /// <summary>
    /// The resources this transition has already changed.
    /// </summary>
    /// <remarks>
    /// Kept only for the length of one step, and replaced before the next. See
    /// <see cref="ITransitionAwareProvider"/>.
    /// </remarks>
    public void Preceded(IReadOnlySet<string> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);

        _changed = changed;
        _startedHere = false;
    }

    /// <summary>
    /// Whether it is running, and whether that is worth anything.
    /// </summary>
    /// <remarks>
    /// A RUNNING APPLICATION IS NOT ALWAYS AT 'RUNNING'
    ///
    /// A window is placed when it opens. One that opened while the desktop was on
    /// the monitors, on a machine that has since switched to the television, is
    /// running perfectly on a screen that is no longer there. The process says
    /// nothing about this, so neither can this method: the answer is null, which
    /// the engine reads as "apply it anyway", and <see cref="Apply"/> then stops
    /// it and starts it where it now belongs.
    ///
    /// Null rather than 'Stopped', which would be a claim about the machine that
    /// is simply untrue, and would make a dry run report a stale application as
    /// not running at all.
    ///
    /// Only for the resources the application named, and only when they were
    /// actually changed. A display that was already correct has not moved
    /// anything, and counting it would restart every application every time a
    /// mode was re-asserted.
    /// </remarks>
    public string? Read()
    {
        if (!_definition.CanObserve)
        {
            // Deliberate, not an error. There is no reliable signal to ask, so
            // returning Running or Stopped here would manufacture state.
            return null;
        }

        bool running;

        try
        {
            running = _host.IsRunning(_definition.ProcessName);
        }
        catch (Exception)
        {
            // Nothing was established. Null is the answer for a question that
            // could not be asked, and it is the honest one here: reporting
            // 'Stopped' because a process query failed would start a second copy
            // of whatever is already open.
            return null;
        }

        if (!running)
        {
            return ApplicationCatalog.Stopped;
        }

        return IsStale() ? null : ApplicationCatalog.Running;
    }

    public void Apply(string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsStopped(value))
        {
            if (!_definition.CanStop)
            {
                throw new InvalidOperationException(
                    $"{_definition.Name} is launch-only. PCMode cannot stop it because no reliable runtime identity is configured."
                );
            }

            Stop(cancellationToken);
            return;
        }

        if (!IsRunning(value))
        {
            // Unreachable through the engine, which validates first. Kept because
            // a provider that trusted its caller would read a typo as one of the
            // two things it can do, and closing somebody's game because a mode
            // said "Runnning" is not a failure anybody would connect to the cause.
            throw new ArgumentException($"'{value}' is not something {_definition.Name} can be.", nameof(value));
        }

        if (!_definition.CanObserve)
        {
            // No process/window identity exists to inspect or prepare. The shell
            // dispatch itself is the complete open-loop action.
            _host.Start(_definition.Start);
            return;
        }

        Start(cancellationToken);
    }

    /// <summary>
    /// Gets an observable application running, restarting it first if what it is
    /// running against has moved.
    /// </summary>
    private void Start(CancellationToken cancellationToken)
    {
        bool running = _host.IsRunning(_definition.ProcessName);

        if (running && IsStale())
        {
            // It is running against something that is no longer there. Stopping it
            // first is the whole of the restart: what follows is an ordinary start.
            Stop(cancellationToken);

            if (!_host.WaitUntilStopped(_definition.ProcessName, _definition.StopWithin, cancellationToken))
            {
                _host.Kill(_definition.ProcessName);

                if (!_host.WaitUntilStopped(_definition.ProcessName, _definition.StopWithin, cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"{_definition.Name} needed restarting because "
                            + $"{string.Join(" and ", _definition.RestartWhen)} changed, and it would not close."
                    );
                }
            }

            running = false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (running)
        {
            // Already there. Only an application that asked to be reactivated has
            // anything left to say to it, and even then not the readiness action:
            // that belongs to a start, and pressing a full-screen key at something
            // already full screen takes it back out again.
            if (_definition.Reactivate)
            {
                _host.Start(_definition.Start);
            }

            return;
        }

        _host.Start(_definition.Start);
        _startedHere = true;

        MakeReady(cancellationToken);
    }

    /// <summary>Asks it to close, or ends it.</summary>
    private void Stop(CancellationToken cancellationToken)
    {
        if (!_host.IsRunning(_definition.ProcessName))
        {
            // Nothing to do, and above all no stop command. Sending a stop URI at
            // an application that is not running makes Windows start it purely to
            // hand it the URI, which it reads as "exit" -- the application opens
            // and shuts again for no reason anybody watching could explain.
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_definition.Stop is LaunchCommand asked)
        {
            _host.Start(asked);
            return;
        }

        _host.Kill(_definition.ProcessName);
    }

    /// <summary>
    /// Does whatever the application asked for once it has a window.
    /// </summary>
    /// <remarks>
    /// Bounded, behind a window that actually exists, and silent when the window
    /// never turns up: the readback is what decides whether the application
    /// started, and failing the whole resource because a keystroke had nowhere to
    /// go would report a launch that worked as a launch that did not.
    /// </remarks>
    private void MakeReady(CancellationToken cancellationToken)
    {
        if (_definition.Ready is not ReadinessAction ready)
        {
            return;
        }

        if (!_host.WaitForWindow(_definition.ProcessName, ready.Within, cancellationToken))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (ready.Activate && !_host.Activate(_definition.ProcessName))
        {
            // Nothing was typed, because there is nothing to type at. Windows
            // refuses to move the foreground for a process the user is not
            // interacting with, and a keystroke sent anyway would land in
            // whatever they were doing instead.
            return;
        }

        if (ready.Keys is string keys)
        {
            _host.SendKeys(_definition.ProcessName, keys);
        }
    }

    /// <summary>
    /// Whether a running copy was left behind by something this transition moved.
    /// </summary>
    private bool IsStale()
    {
        if (_startedHere)
        {
            return false;
        }

        foreach (string key in _definition.RestartWhen)
        {
            if (_changed.Contains(key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRunning(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(value.Trim(), ApplicationCatalog.Running, StringComparison.OrdinalIgnoreCase);

    private static bool IsStopped(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(value.Trim(), ApplicationCatalog.Stopped, StringComparison.OrdinalIgnoreCase);
}

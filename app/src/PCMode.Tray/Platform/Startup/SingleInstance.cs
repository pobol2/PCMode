namespace PCMode.Tray.Platform.Startup;

/// <summary>
/// Makes sure only one shell is running, and gives a second launch a way to
/// wake the first one instead of becoming a rival.
/// </summary>
/// <remarks>
/// WHY THIS IS NOT COSMETIC
///
/// A second tray icon would be untidy. A second INPUT OWNER would not be
/// diagnosable: two processes registering the same hotkey means one of them
/// silently loses the registration, and which one depends on start order.
/// The same goes for controller polling, where both would answer the same
/// button press. Target has neither yet, but the accident this prevents is an
/// ordinary one regardless: somebody double-clicks the executable while it is
/// already in the notification area, where by design it is easy to miss.
///
/// HOW THE SECOND ONE TALKS TO THE FIRST
///
/// A named mutex decides who owns the session, and a named auto-reset event
/// carries the one message there is to send: "somebody tried to start me
/// again, show yourself". No window messages, no broadcast, no pipe. The
/// second process sets the event and exits; the owner's wait thread raises
/// <see cref="ActivationRequested"/> on a background thread, so a handler that
/// touches the UI has to marshal -- which is why the host does.
///
/// Both handles are named per-session (Local\), not machine-wide. Two users
/// logged into the same machine each get their own shell, which is right: a
/// tray icon and a hotkey both belong to a session.
///
/// Named distinctly from legacy PCMode.Tray's own instance so the two builds
/// can run side by side during migration instead of fighting over one lock.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The default name, unique to this application and this session.</summary>
    public const string DefaultName = @"Local\PCMode.Shell.SingleInstance";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly ManualResetEvent _stopping = new(initialState: false);
    private readonly Thread? _listener;

    private bool _disposed;

    private SingleInstance(string name, bool isOwner)
    {
        IsOwner = isOwner;

        _mutex = new Mutex(initiallyOwned: false, name, out _);
        _activate = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, $"{name}.Activate", out _);

        if (!isOwner)
        {
            return;
        }

        _listener = new Thread(WaitForActivationRequests)
        {
            IsBackground = true,
            Name = "PCMode single-instance listener",
        };

        _listener.Start();
    }

    /// <summary>
    /// Raised on a background thread when another launch asked this instance to
    /// show itself. Never raised on a non-owner.
    /// </summary>
    public event EventHandler? ActivationRequested;

    /// <summary>Whether this process is the one that gets to run.</summary>
    public bool IsOwner { get; }

    /// <summary>
    /// Claims the session for this process, or reports that somebody else has
    /// it.
    /// </summary>
    /// <remarks>
    /// The mutex is never waited on and never released, only created. Its
    /// existence is the whole signal, and that is deliberate: an owner that
    /// crashes has its handles closed by the kernel, the last handle going away
    /// destroys the object, and the next launch legitimately becomes the owner.
    /// Waiting on it instead would mean inheriting WaitOne's abandoned-mutex
    /// exception as a startup failure mode, for no benefit.
    /// </remarks>
    public static SingleInstance Acquire(string name = DefaultName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Held only long enough to answer the question. The instance below
        // opens its own handle, so the object survives this one closing.
        using var probe = new Mutex(initiallyOwned: false, name, out bool createdNew);

        return new SingleInstance(name, isOwner: createdNew);
    }

    /// <summary>
    /// Asks the instance that owns the session to show itself.
    /// </summary>
    /// <returns>
    /// False if this process IS the owner, in which case there is nobody to
    /// ask.
    /// </returns>
    public bool RequestActivation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsOwner)
        {
            return false;
        }

        _activate.Set();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _stopping.Set();

        // Bounded: the listener is a background thread, so a wait that somehow
        // does not finish must not be what stops the process from exiting.
        _listener?.Join(TimeSpan.FromSeconds(2));

        _stopping.Dispose();
        _activate.Dispose();
        _mutex.Dispose();
    }

    private void WaitForActivationRequests()
    {
        WaitHandle[] handles = [_activate, _stopping];

        while (true)
        {
            int signalled;

            try
            {
                signalled = WaitHandle.WaitAny(handles);
            }
            catch (ObjectDisposedException)
            {
                // Disposed underneath us during shutdown. Nothing to report.
                return;
            }

            // Index 1 is _stopping.
            if (signalled != 0)
            {
                return;
            }

            ActivationRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}

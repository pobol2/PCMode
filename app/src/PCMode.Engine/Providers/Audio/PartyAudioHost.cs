namespace PCMode.Engine;

/// <summary>
/// The one running Party mirror, and the only thing allowed to own one.
/// </summary>
/// <remarks>
/// WHY OWNERSHIP IS ITS OWN OBJECT
///
/// A capture-and-render pair is not a setting: it is two threads holding two
/// endpoints, and a second one for the same pair is not a harmless duplicate --
/// it is the same audio arriving twice, slightly apart, which sounds like a
/// fault in the hardware. So there is exactly one place it can live, and asking
/// for Party mode twice reaches the same place both times.
///
/// A <see cref="AudioProvider"/> cannot be that place. Providers are rebuilt
/// whenever the configuration is re-read -- a hardware alias saved from the
/// Hardware window rebuilds the whole engine -- and a runtime owned by the
/// instance that started it would be abandoned still running, with the newly
/// built provider knowing nothing about it. This object outlives those rebuilds
/// because the shell creates it once and hands it to every engine it builds.
///
/// RESTART ONLY WHEN THE PAIR CHANGED
///
/// Applying Party mode over a Party mode that is already running the same two
/// endpoints adjusts it, and does not stop and start anything. That is what
/// makes re-applying a mode -- which the engine does on every verification pass,
/// and a user does whenever they press the same button twice -- silent instead
/// of a gap in the music.
/// </remarks>
public sealed class PartyAudioHost : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Func<IAudioMirrorFactory> _factory;

    private IAudioMirrorFactory? _mirrors;
    private IAudioMirror? _running;
    private string? _lastFailure;
    private bool _disposed;

    /// <param name="factory">
    /// How to obtain the thing that starts a mirror, called at most once and only
    /// when one is actually started. Deferred because this object is created on
    /// every engine build, including the ones that exist only to validate a
    /// document, and none of those should load Core Audio.
    /// </param>
    public PartyAudioHost(Func<IAudioMirrorFactory>? factory = null) =>
        _factory = factory ?? (static () => new WasapiAudioMirrorFactory());

    /// <summary>A host wired to one particular factory, for tests.</summary>
    public PartyAudioHost(IAudioMirrorFactory factory)
        : this(() => factory ?? throw new ArgumentNullException(nameof(factory)))
    {
    }

    /// <summary>Whether a mirror is running and has not failed.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return Live() is not null;
            }
        }
    }

    /// <summary>What is running, or null.</summary>
    public AudioMirrorRequest? Current
    {
        get
        {
            lock (_gate)
            {
                return Live()?.Request;
            }
        }
    }

    /// <summary>
    /// The running mirror's own report, or null when nothing is running.
    /// </summary>
    /// <remarks>
    /// A mirror that has failed still answers, and that is the point: "it stopped
    /// forty minutes ago because the soundbar was unplugged" is the sentence a
    /// diagnostics page exists to be able to say.
    /// </remarks>
    public AudioMirrorHealth? Health
    {
        get
        {
            lock (_gate)
            {
                return _running?.Health;
            }
        }
    }

    /// <summary>Why the last attempt to start one failed, if it did.</summary>
    public string? LastFailure
    {
        get
        {
            lock (_gate)
            {
                return _running?.Health.LastFailure ?? _lastFailure;
            }
        }
    }

    /// <summary>
    /// Starts the mirror, adjusts the running one, or replaces it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The mirror could not be started. Nothing is left running: a half-started
    /// Party group is worse than none, because the reference endpoint is already
    /// the Windows default and the room sounds like an ordinary single output
    /// that somebody believes is a pair.
    /// </exception>
    public void Start(AudioMirrorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (Live() is IAudioMirror live)
            {
                if (SamePair(live.Request, request))
                {
                    live.Adjust(request.Delay, request.Gain);
                    _lastFailure = null;
                    return;
                }

                // A different pair. The old one goes first and completely:
                // starting the new one first would put two capture streams on
                // whichever endpoint they have in common.
                StopLocked();
            }
            else
            {
                // Either nothing, or a failed runtime whose threads have already
                // stopped. Either way it is released before another is made.
                StopLocked();
            }

            try
            {
                _mirrors ??= _factory();
                _running = _mirrors.Start(request);
                _lastFailure = null;
            }
            catch (Exception exception)
            {
                _running = null;
                _lastFailure = exception.Message;
                throw;
            }
        }
    }

    /// <summary>
    /// Stops the mirror if there is one, and does nothing if there is not.
    /// </summary>
    /// <remarks>
    /// Called whenever a normal single output is applied, and when the shell
    /// exits. Idempotent because both of those happen more than once and neither
    /// is in a position to know whether it is the first.
    /// </remarks>
    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    /// <summary>Adjusts a running mirror, and reports whether there was one.</summary>
    public bool Adjust(TimeSpan delay, double gain)
    {
        lock (_gate)
        {
            if (Live() is not IAudioMirror live)
            {
                return false;
            }

            live.Adjust(delay, gain);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            StopLocked();
        }
    }

    /// <summary>The runtime, if there is one and it is still working.</summary>
    private IAudioMirror? Live() =>
        _running is not null && _running.Health.Healthy ? _running : null;

    private void StopLocked()
    {
        IAudioMirror? stopping = _running;
        _running = null;

        if (stopping is null)
        {
            return;
        }

        try
        {
            stopping.Dispose();
        }
        catch (Exception exception)
        {
            // A mirror that will not shut down cleanly has already stopped being
            // the thing anybody is waiting on -- the endpoints are released when
            // the process is, at the latest. Recorded, not thrown: this is called
            // from the middle of applying some other audio target, and that
            // target still needs to happen.
            _lastFailure = $"The Party mirror did not stop cleanly. {exception.Message}";
        }
    }

    /// <summary>
    /// Whether two requests are the same pair of endpoints, whatever their delay
    /// and gain.
    /// </summary>
    private static bool SamePair(AudioMirrorRequest running, AudioMirrorRequest wanted) =>
        string.Equals(running.ReferenceId, wanted.ReferenceId, StringComparison.Ordinal) &&
        string.Equals(running.MirrorId, wanted.MirrorId, StringComparison.Ordinal);
}

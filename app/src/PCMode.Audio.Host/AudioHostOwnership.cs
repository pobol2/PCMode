namespace PCMode.Audio.Host;

/// <summary>
/// The claim that makes this process the one Audio Host for the session.
/// </summary>
/// <remarks>
/// WHY THE PIPE IS NOT ENOUGH
///
/// A named pipe server created with more than one allowed instance can be
/// created again by another process, and both of them then accept connections on
/// the same name. Two Hosts sharing a pipe is the worst available outcome: PCMode
/// applies a target to whichever answered, a second Host holds the driver's PCM
/// transport, and the control plane and the data plane end up owned by different
/// processes.
///
/// So ownership is claimed explicitly, before the runtime is built and before
/// the server is opened. A second Host discovers immediately that it is a
/// second Host, says so, and exits without touching the driver or the endpoint.
/// The launcher on the PCMode side reads that as "one is already running" and
/// connects to it -- see <c>AudioHostLink</c>.
///
/// LOCAL, BECAUSE THE HOST IS PER-USER
///
/// The claim is session-local. The Host renders into a user's own audio session
/// and is not a service; two users signed in at once are two independent Hosts,
/// and a global claim would let the first of them lock out the second.
///
/// A SEMAPHORE, NOT A MUTEX
///
/// A mutex belongs to the thread that took it, and <c>Main</c> here is async: the
/// thread that started the process finishes at the first await and the
/// continuation runs on another. A mutex owned by a thread that has ended is an
/// abandoned mutex, which a second Host would be free to take while this one is
/// still running -- exactly the state this class exists to prevent. A named
/// semaphore has no thread affinity, so the claim is held by the process for as
/// long as the handle is open.
/// </remarks>
public sealed class AudioHostOwnership : IDisposable
{
    private readonly Semaphore? _claim;

    private bool _disposed;

    private AudioHostOwnership(Semaphore? claim, bool owned, string? heldBy)
    {
        _claim = claim;
        Owned = owned;
        HeldBy = heldBy;
    }

    /// <summary>Whether this process is the session's Audio Host.</summary>
    public bool Owned { get; }

    /// <summary>Why it is not, when it is not.</summary>
    public string? HeldBy { get; }

    /// <summary>
    /// Claims the session's Audio Host ownership for the given pipe.
    /// </summary>
    /// <remarks>
    /// Keyed by pipe name so that a Host deliberately started on its own pipe --
    /// which is how the hardware tests avoid disturbing a real one -- is a
    /// separate claim rather than a rival for the production Host's.
    /// </remarks>
    public static AudioHostOwnership Claim(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        // Kernel object names may not contain a backslash, and pipe names may.
        string name = $"Local\\PCMode.Audio.Host.{pipeName.Replace('\\', '_')}";

        var claim = new Semaphore(1, 1, name);

        if (claim.WaitOne(TimeSpan.Zero))
        {
            return new AudioHostOwnership(claim, owned: true, heldBy: null);
        }

        claim.Dispose();

        return new AudioHostOwnership(
            null,
            owned: false,
            $"Another PCMode.Audio.Host already owns the pipe '{pipeName}' in this session.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_claim is null)
        {
            return;
        }

        try
        {
            _claim.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already released. Closing the handle below is what actually frees
            // the claim, so there is nothing here to recover from.
        }

        _claim.Dispose();
    }
}

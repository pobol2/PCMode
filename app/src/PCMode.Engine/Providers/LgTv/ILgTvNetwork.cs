namespace PCMode.Engine;

/// <summary>
/// One open conversation with a television.
/// </summary>
/// <remarks>
/// Deliberately below the protocol. This moves text frames and knows nothing
/// about registration, power or inputs -- all of which live in
/// <see cref="LgTvConnection"/>, where they can be tested against a fake that
/// answers the way a television does.
///
/// The split is where it is because of what each half is worth testing. A
/// television's socket is a TLS handshake and a WebSocket upgrade: real, and
/// impossible to exercise without a real television. What is on top of it --
/// pairing, request matching, what an error means, what silence means, when a key
/// may be written -- is where the rules are, and none of it needs hardware.
/// </remarks>
public interface ILgTvSocket : IAsyncDisposable
{
    /// <summary>
    /// The SHA-256 of the certificate the television presented, in upper-case
    /// hexadecimal.
    /// </summary>
    /// <remarks>
    /// Reported so it can be pinned. A webOS television signs its own
    /// certificate, so there is no authority to check it against and the first
    /// connection has nothing to compare it with. What makes pinning it honest is
    /// that the first connection is also the one the user physically approves on
    /// the television's own screen: that prompt is the out-of-band confirmation,
    /// and the fingerprint recorded at that moment is the one every later
    /// connection has to present.
    /// </remarks>
    string CertificateFingerprint { get; }

    /// <summary>Sends one text frame.</summary>
    Task SendAsync(string message, CancellationToken cancellationToken);

    /// <summary>Waits for the next text frame.</summary>
    Task<string> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// How the television is reached: a socket, and a way to wake it.
/// </summary>
/// <remarks>
/// The reason the television providers can be tested at all. A fake answers the
/// way webOS does, so pairing, retrying, giving up, refusing to pair during a dry
/// run and never writing a key when nothing asked to can all be exercised without
/// a television in the room -- and without switching somebody's television on and
/// off while a test suite runs.
/// </remarks>
public interface ILgTvNetwork
{
    /// <summary>
    /// Opens a secure socket to the television, or throws if it cannot.
    /// </summary>
    /// <param name="pinnedFingerprint">
    /// The certificate this television is known to present, or null when there is
    /// no record of it yet. A mismatch must refuse the connection rather than
    /// report it: something is answering on the television's address that is not
    /// the television this machine paired with, and handing it a client key would
    /// be handing it to whatever that is.
    /// </param>
    /// <remarks>
    /// Failure is ordinary. A television that is switched off does not answer, and
    /// the exception that says so is not a fault to be reported to anybody -- it
    /// is how a television that is off looks.
    /// </remarks>
    Task<ILgTvSocket> ConnectAsync(
        string host,
        int port,
        string? pinnedFingerprint,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends a Wake-on-LAN packet to the television.
    /// </summary>
    /// <remarks>
    /// The only way to switch a webOS television on from the network: one that is
    /// off is not listening on its socket to be asked politely. It is fire and
    /// forget -- nothing acknowledges a magic packet -- so whether it worked is
    /// answered afterwards by reading the power state, not by this returning.
    /// </remarks>
    Task WakeAsync(string host, string macAddress, CancellationToken cancellationToken);
}

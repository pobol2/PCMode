using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace PCMode.Engine;

/// <summary>
/// The real television, on the real network.
/// </summary>
/// <remarks>
/// Everything in here is a socket. There is no protocol, no policy and no
/// decision worth testing: what to send, when pairing is allowed and what silence
/// means all live in <see cref="LgTvConnection"/>, against a fake. This is the
/// part that cannot be tested without a television in the room, and it is
/// deliberately the smallest it can be.
/// </remarks>
public sealed class WebSocketLgTvNetwork : ILgTvNetwork
{
    /// <summary>
    /// The largest message that will be accepted from the television.
    /// </summary>
    /// <remarks>
    /// A bound on something on the network, not on anything expected. The answers
    /// this asks for are a few hundred bytes; a device that streams megabytes at a
    /// tray application must not be able to exhaust its memory by doing so.
    /// </remarks>
    private const int MaximumMessageBytes = 256 * 1024;

    public async Task<ILgTvSocket> ConnectAsync(
        string host,
        int port,
        string? pinnedFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var socket = new ClientWebSocket();
        string? presented = null;

        socket.Options.Proxy = null;

        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate is null)
            {
                return false;
            }

            presented = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

            // A webOS television signs its own certificate, so the chain can never
            // validate and checking it would only ever refuse everything. What is
            // checked instead is that it is the SAME certificate as last time.
            //
            // The first connection has nothing to compare against and accepts what
            // it is given -- but the first connection is also the one that puts a
            // prompt on the television's own screen for somebody to accept, and
            // that is what makes it trustworthy. The fingerprint recorded at that
            // moment is what every later connection has to match, so something
            // else answering on the television's address afterwards is refused
            // rather than handed a client key.
            return pinnedFingerprint is null ||
                   string.Equals(pinnedFingerprint, presented, StringComparison.OrdinalIgnoreCase);
        };

        try
        {
            await socket
                .ConnectAsync(new UriBuilder(Uri.UriSchemeWss, host, port, "/").Uri, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            socket.Dispose();
            throw;
        }

        if (string.IsNullOrWhiteSpace(presented))
        {
            // Unreachable over TLS, and worth refusing rather than assuming: a
            // connection whose certificate was never seen cannot be pinned, and
            // pinning is the whole of what authenticates this television.
            socket.Dispose();

            throw new InvalidOperationException(
                $"The television at {host} did not present a certificate.");
        }

        return new WebSocketSession(socket, presented!);
    }

    /// <summary>
    /// Sends a magic packet, by every route it might need to take.
    /// </summary>
    /// <remarks>
    /// Wake-on-LAN is a broadcast that nothing acknowledges, so the only way to
    /// improve the odds is to send it more than one way and more than once. The
    /// limited broadcast reaches the local segment; the subnet broadcast is what
    /// some routers actually forward; the unicast works when the television's
    /// address is still in the ARP table, which it usually is shortly after it was
    /// switched off. Ports 9 and 7 are both conventional and neither is universal.
    ///
    /// Bound to the interface that shares the television's subnet where one can be
    /// found. On a machine with a VPN or a virtual switch -- which is most of
    /// them -- an unbound broadcast leaves by whichever interface Windows likes
    /// best, and that is regularly not the one the television is on.
    /// </remarks>
    public async Task WakeAsync(string host, string macAddress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(macAddress);

        byte[] mac = Convert.FromHexString(macAddress.Replace(":", string.Empty, StringComparison.Ordinal));
        byte[] packet = new byte[6 + (16 * mac.Length)];

        packet.AsSpan(0, 6).Fill(0xFF);

        for (int repeat = 0; repeat < 16; repeat++)
        {
            mac.CopyTo(packet, 6 + (repeat * mac.Length));
        }

        IPAddress? television = IPAddress.TryParse(host, out IPAddress? parsed) &&
                                parsed.AddressFamily == AddressFamily.InterNetwork
            ? parsed
            : null;

        (IPAddress Local, IPAddress Broadcast)? route = television is null ? null : LocalRouteTo(television);

        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };

        if (television is not null)
        {
            _ = targets.Add(television);
        }

        if (route is not null)
        {
            _ = targets.Add(route.Value.Broadcast);
        }

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };

        if (route is not null)
        {
            udp.Client.Bind(new IPEndPoint(route.Value.Local, 0));
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (IPAddress target in targets)
            {
                _ = await udp.SendAsync(packet, new IPEndPoint(target, 9), cancellationToken).ConfigureAwait(false);
                _ = await udp.SendAsync(packet, new IPEndPoint(target, 7), cancellationToken).ConfigureAwait(false);
            }

            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The local address that shares a subnet with the television, and that
    /// subnet's broadcast address.
    /// </summary>
    private static (IPAddress Local, IPAddress Broadcast)? LocalRouteTo(IPAddress television)
    {
        byte[] wanted = television.GetAddressBytes();

        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask is null)
                {
                    continue;
                }

                byte[] local = address.Address.GetAddressBytes();
                byte[] mask = address.IPv4Mask.GetAddressBytes();
                byte[] broadcast = new byte[4];
                bool shared = true;

                for (int part = 0; part < 4; part++)
                {
                    shared &= (wanted[part] & mask[part]) == (local[part] & mask[part]);
                    broadcast[part] = (byte)(local[part] | ~mask[part]);
                }

                if (shared)
                {
                    return (address.Address, new IPAddress(broadcast));
                }
            }
        }

        return null;
    }

    private sealed class WebSocketSession(ClientWebSocket socket, string fingerprint) : ILgTvSocket
    {
        public string CertificateFingerprint { get; } = fingerprint;

        public Task SendAsync(string message, CancellationToken cancellationToken) =>
            socket.SendAsync(
                Encoding.UTF8.GetBytes(message),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);

        public async Task<string> ReceiveAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[4096];
            using var message = new MemoryStream();

            while (true)
            {
                WebSocketReceiveResult received = await socket
                    .ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                    .ConfigureAwait(false);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException(
                        $"The television closed the connection. {received.CloseStatusDescription}");
                }

                message.Write(buffer, 0, received.Count);

                if (message.Length > MaximumMessageBytes)
                {
                    throw new InvalidOperationException("The television sent more than will be read.");
                }

                if (received.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    // Given a moment to close politely, and no more. A television
                    // that has just been told to switch off will not answer, and
                    // waiting for it to would make every power-off take as long as
                    // the timeout.
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));

                    await socket
                        .CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, closing.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Closing is a courtesy. Whatever it says, the socket is finished
                // with, and an exception here would replace whatever real result
                // the caller was about to get.
            }
            finally
            {
                socket.Dispose();
            }
        }
    }
}

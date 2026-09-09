using System.IO.Pipes;
using PCMode.Audio.Protocol;
using PCMode.Audio.Protocol.Messages;

namespace PCMode.Audio.Client;

public sealed class AudioHostConnection : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;

    private readonly int _protocolVersion;

    private bool _disposed;

    private AudioHostConnection(NamedPipeClientStream pipe, int protocolVersion)
    {
        _pipe = pipe;

        _protocolVersion = protocolVersion;
    }

    public static async Task<AudioHostConnection> ConnectAsync(
        string pipeName,
        int protocolVersion = HostProtocolVersion.Current,
        int timeoutMilliseconds = 2000,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(timeoutMilliseconds, cancellationToken);

            var connection = new AudioHostConnection(pipe, protocolVersion);

            AudioHostCommand hello = AudioHostCommand.Create(
                AudioHostCommandKind.Hello,
                protocolVersion: protocolVersion
            );

            AudioHostResponse response = await connection.SendAsync(hello, cancellationToken);

            if (!response.Success)
            {
                throw CreateException(response);
            }

            if (!HostProtocolVersion.IsCompatible(response.ProtocolVersion))
            {
                throw new AudioHostProtocolException(
                    $"Host protocol version "
                        + $"{response.ProtocolVersion} "
                        + $"is incompatible with Client version "
                        + $"{protocolVersion}."
                );
            }

            return connection;
        }
        catch
        {
            pipe.Dispose();

            throw;
        }
    }

    public async Task<AudioHostResponse> SendAsync(
        AudioHostCommand command,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (command.ProtocolVersion != _protocolVersion)
        {
            throw new InvalidOperationException(
                "Command protocol version does not match the negotiated connection version."
            );
        }

        await AudioHostWireProtocol.WriteCommandAsync(_pipe, command, cancellationToken);

        AudioHostResponse response =
            await AudioHostWireProtocol.ReadResponseAsync(_pipe, cancellationToken)
            ?? throw new EndOfStreamException("Audio Host disconnected before sending a response.");

        if (response.RequestId != command.RequestId)
        {
            throw new InvalidDataException("Audio Host returned a response for the wrong request.");
        }

        return response;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;

        _pipe.Dispose();

        return ValueTask.CompletedTask;
    }

    internal static Exception CreateException(AudioHostResponse response)
    {
        if (string.Equals(response.ErrorCode, "IncompatibleProtocol", StringComparison.Ordinal))
        {
            return new AudioHostProtocolException(response.ErrorMessage ?? "Audio Host protocol is incompatible.");
        }

        return new AudioHostRequestException(
            response.ErrorCode ?? "HostError",
            response.ErrorMessage ?? "Audio Host request failed."
        );
    }
}

public sealed class AudioHostProtocolException : Exception
{
    public AudioHostProtocolException(string message)
        : base(message) { }
}

public sealed class AudioHostRequestException : Exception
{
    public string ErrorCode { get; }

    public AudioHostRequestException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }
}

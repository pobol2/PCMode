using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCMode.Audio.Protocol.Messages;

namespace PCMode.Audio.Protocol;

public static class AudioHostWireProtocol
{
    private const int MaximumMessageBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static Task WriteCommandAsync(
        Stream stream,
        AudioHostCommand command,
        CancellationToken cancellationToken = default
    )
    {
        return WriteAsync(stream, command, cancellationToken);
    }

    public static Task<AudioHostCommand?> ReadCommandAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        return ReadAsync<AudioHostCommand>(stream, cancellationToken);
    }

    public static Task WriteResponseAsync(
        Stream stream,
        AudioHostResponse response,
        CancellationToken cancellationToken = default
    )
    {
        return WriteAsync(stream, response, cancellationToken);
    }

    public static Task<AudioHostResponse?> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken = default
    )
    {
        return ReadAsync<AudioHostResponse>(stream, cancellationToken);
    }

    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC message exceeds the maximum allowed size.");
        }

        byte[] header = new byte[sizeof(int)];

        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        await stream.WriteAsync(header, cancellationToken);

        await stream.WriteAsync(payload, cancellationToken);

        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[sizeof(int)];

        int headerBytes = await ReadExactOrEofAsync(stream, header, cancellationToken);

        if (headerBytes == 0)
        {
            return default;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);

        if (length <= 0 || length > MaximumMessageBytes)
        {
            throw new InvalidDataException($"Invalid IPC message size: {length}.");
        }

        byte[] payload = new byte[length];

        await ReadExactAsync(stream, payload, cancellationToken);

        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
            ?? throw new InvalidDataException("IPC message could not be deserialized.");
    }

    private static async Task<int> ReadExactOrEofAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        int total = 0;

        while (total < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer[total..], cancellationToken);

            if (count == 0)
            {
                if (total == 0)
                {
                    return 0;
                }

                throw new EndOfStreamException("IPC stream ended inside a message header.");
            }

            total += count;
        }

        return total;
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;

        while (total < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer[total..], cancellationToken);

            if (count == 0)
            {
                throw new EndOfStreamException("IPC stream ended inside a message.");
            }

            total += count;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

            PropertyNameCaseInsensitive = true,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        return options;
    }
}

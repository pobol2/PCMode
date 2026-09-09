using System.Buffers.Binary;
using System.Diagnostics;
using PCMode.Audio.Core.Models;
using PCMode.Audio.DriverClient.Protocol;
using PCMode.Audio.Engine.Contracts;

namespace PCMode.Audio.DriverClient.Transport;

public sealed class DriverAudioInput : IAudioInputStream, IAudioInputDiscontinuitySource
{
    private const int ReconnectDelayMilliseconds = 100;

    private readonly IDriverConnection _connection;

    private readonly bool _disposeConnection;

    private readonly DriverSharedBuffer _staging;

    private readonly float[] _conversionBuffer;

    private bool _started;

    private bool _disposed;

    private long _nextReconnectTick;

    private ulong? _lastPacketSequence;

    private ulong? _lastPacketQpc;

    private int _lastPacketFrames;

    private long _discontinuityGeneration;

    public AudioFormat Format { get; }

    public long DiscontinuityGeneration => Interlocked.Read(ref _discontinuityGeneration);

    public DriverAudioInput()
        : this(new DriverConnection(claimPcmTransport: true), disposeConnection: true) { }

    internal DriverAudioInput(IDriverConnection connection, bool disposeConnection = false)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;

        _disposeConnection = disposeConnection;

        _connection.Connect();

        Format = _connection.PreferredFormat;

        int maximumSamples = checked(_connection.MaximumPacketBytes / sizeof(short));

        if (maximumSamples <= 0)
        {
            throw new DriverProtocolException("Driver reported an invalid maximum packet size.");
        }

        _conversionBuffer = new float[maximumSamples];

        _staging = new DriverSharedBuffer(maximumSamples);
    }

    public void Start()
    {
        ThrowIfDisposed();

        if (_started)
        {
            return;
        }

        if (!_connection.IsConnected)
        {
            _connection.Connect();
        }

        _staging.Clear();

        ResetPacketTimeline();

        _started = true;
    }

    public int Read(Span<float> destination)
    {
        ThrowIfDisposed();

        if (!_started)
        {
            throw new InvalidOperationException("Driver audio input is not started.");
        }

        if (destination.Length % Format.ChannelCount != 0)
        {
            throw new ArgumentException("Destination must contain complete audio frames.", nameof(destination));
        }

        if (destination.IsEmpty)
        {
            return 0;
        }

        int bufferedSamples = _staging.Read(destination);

        if (bufferedSamples > 0)
        {
            return bufferedSamples / Format.ChannelCount;
        }

        if (!_connection.IsConnected)
        {
            TryReconnect();

            if (!_connection.IsConnected)
            {
                Thread.Sleep(2);

                return 0;
            }
        }

        try
        {
            if (!_connection.TryReadPacket(out DriverPacket packet))
            {
                Thread.Sleep(1);

                return 0;
            }

            int sampleCount = ConvertPacket(packet);

            ObservePacketTimeline(packet, sampleCount / Format.ChannelCount);

            _staging.Replace(_conversionBuffer.AsSpan(0, sampleCount));

            int copiedSamples = _staging.Read(destination);

            return copiedSamples / Format.ChannelCount;
        }
        catch (IOException)
        {
            ReportConnectionDiscontinuity();

            _connection.Disconnect();

            TryReconnect();

            Thread.Sleep(1);

            return 0;
        }
    }

    public void Stop()
    {
        ThrowIfDisposed();

        if (!_started)
        {
            return;
        }

        _started = false;

        _staging.Clear();

        ResetPacketTimeline();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_started)
        {
            Stop();
        }

        _connection.Disconnect();

        if (_disposeConnection)
        {
            _connection.Dispose();
        }

        _disposed = true;
    }

    private int ConvertPacket(DriverPacket packet)
    {
        AudioFormat packetFormat = DriverProtocol.ToAudioFormat(packet.Format);

        if (packetFormat != Format)
        {
            throw new DriverProtocolException(
                $"Driver packet format changed from {Format.SampleRate} Hz / {Format.ChannelCount} channels to {packetFormat.SampleRate} Hz / {packetFormat.ChannelCount} channels."
            );
        }

        int sampleCount = packet.Format.SampleFormat switch
        {
            DriverSampleFormat.Float32 => ConvertFloat32(packet.Payload.Span),

            DriverSampleFormat.Pcm16 => ConvertPcm16(packet.Payload.Span),

            _ => throw new DriverProtocolException($"Unsupported driver sample format '{packet.Format.SampleFormat}'."),
        };

        if (sampleCount % Format.ChannelCount != 0)
        {
            throw new DriverProtocolException("Driver packet does not contain complete audio frames.");
        }

        return sampleCount;
    }

    private int ConvertFloat32(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new DriverProtocolException("Float32 packet has an invalid byte count.");
        }

        int count = bytes.Length / sizeof(float);

        EnsureConversionCapacity(count);

        for (int i = 0; i < count; ++i)
        {
            int bits = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i * 4, 4));

            _conversionBuffer[i] = BitConverter.Int32BitsToSingle(bits);
        }

        return count;
    }

    private int ConvertPcm16(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(short) != 0)
        {
            throw new DriverProtocolException("PCM16 packet has an invalid byte count.");
        }

        int count = bytes.Length / sizeof(short);

        EnsureConversionCapacity(count);

        const float scale = 1.0f / 32768.0f;

        for (int i = 0; i < count; ++i)
        {
            short value = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2));

            _conversionBuffer[i] = value * scale;
        }

        return count;
    }

    private void EnsureConversionCapacity(int sampleCount)
    {
        if (sampleCount > _conversionBuffer.Length)
        {
            throw new DriverProtocolException("Driver packet exceeded the negotiated bounded buffer.");
        }
    }

    private void ObservePacketTimeline(DriverPacket packet, int packetFrames)
    {
        bool sequenceGap = _lastPacketSequence.HasValue && packet.Sequence != unchecked(_lastPacketSequence.Value + 1);

        bool invalidQpcOrder = _lastPacketQpc.HasValue && packet.Qpc <= _lastPacketQpc.Value;

        bool qpcGap = false;

        if (_lastPacketQpc.HasValue && !invalidQpcOrder)
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(checked((long)_lastPacketQpc.Value), checked((long)packet.Qpc));

            TimeSpan expected = TimeSpan.FromSeconds(_lastPacketFrames / (double)Format.SampleRate);

            TimeSpan maximumContinuousGap = TimeSpan.FromTicks(
                Math.Max(TimeSpan.FromMilliseconds(25).Ticks, expected.Ticks * 6)
            );

            qpcGap = elapsed > maximumContinuousGap;
        }

        if (sequenceGap || invalidQpcOrder || qpcGap)
        {
            Interlocked.Increment(ref _discontinuityGeneration);
        }

        _lastPacketSequence = packet.Sequence;

        _lastPacketQpc = packet.Qpc;

        _lastPacketFrames = packetFrames;
    }

    private void ReportConnectionDiscontinuity()
    {
        if (_lastPacketSequence.HasValue)
        {
            Interlocked.Increment(ref _discontinuityGeneration);
        }

        ResetPacketTimeline();

        _staging.Clear();
    }

    private void ResetPacketTimeline()
    {
        _lastPacketSequence = null;

        _lastPacketQpc = null;

        _lastPacketFrames = 0;
    }

    private void TryReconnect()
    {
        long now = Environment.TickCount64;

        if (now < _nextReconnectTick)
        {
            return;
        }

        _nextReconnectTick = now + ReconnectDelayMilliseconds;

        try
        {
            _connection.Connect();

            if (_connection.PreferredFormat != Format)
            {
                _connection.Disconnect();

                throw new DriverProtocolException("Driver reconnected using a different nominal format.");
            }

            _staging.Clear();
        }
        catch (DriverUnavailableException) { }
        catch (IOException) { }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

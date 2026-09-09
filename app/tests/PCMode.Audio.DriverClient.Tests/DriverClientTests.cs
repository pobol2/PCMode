using System.Diagnostics;
using System.Runtime.InteropServices;
using PCMode.Audio.Core.Models;
using PCMode.Audio.DriverClient.Diagnostics;
using PCMode.Audio.DriverClient.Protocol;
using PCMode.Audio.DriverClient.Transport;
using Xunit;

namespace PCMode.Audio.DriverClient.Tests;

public sealed class DriverClientTests
{
    [Fact]
    public void Protocol_major_mismatch_is_rejected()
    {
        Assert.Throws<DriverProtocolException>(() =>
            DriverProtocol.Validate(
                DriverProtocol.MajorVersion + 1,
                DriverProtocol.MinimumMinorVersion,
                DriverProtocol.RequiredCapabilities
            )
        );
    }

    [Fact]
    public void Protocol_missing_capability_is_rejected()
    {
        DriverCapabilities capabilities = DriverProtocol.RequiredCapabilities & ~DriverCapabilities.SingleConsumer;

        Assert.Throws<DriverProtocolException>(() =>
            DriverProtocol.Validate(DriverProtocol.MajorVersion, DriverProtocol.MinimumMinorVersion, capabilities)
        );
    }

    [Fact]
    public void Driver_unavailable_fails_input_creation()
    {
        var connection = new FakeDriverConnection { ConnectFailure = new DriverUnavailableException("not installed") };

        Assert.Throws<DriverUnavailableException>(() => new DriverAudioInput(connection));
    }

    [Fact]
    public void Start_and_stop_are_idempotent()
    {
        var connection = new FakeDriverConnection();

        using var input = new DriverAudioInput(connection);

        input.Start();
        input.Start();

        input.Stop();
        input.Stop();

        Assert.Equal(1, connection.ConnectCount);
    }

    [Fact]
    public void Connection_loss_reconnects_same_input()
    {
        var connection = new FakeDriverConnection();

        connection.EnqueueFailure(new IOException("connection lost"));

        using var input = new DriverAudioInput(connection);

        input.Start();

        Span<float> destination = stackalloc float[960];

        Assert.Equal(0, input.Read(destination));

        Assert.Equal(2, connection.ConnectCount);
    }

    [Fact]
    public void Clean_reconnect_reads_new_packet()
    {
        var connection = new FakeDriverConnection();

        connection.EnqueueFailure(new IOException("connection lost"));

        connection.EnqueuePacket(CreateFloatPacket(48000, 0.25f, -0.25f, 0.5f, -0.5f));

        using var input = new DriverAudioInput(connection);

        input.Start();

        Span<float> destination = stackalloc float[4];

        Assert.Equal(0, input.Read(destination));

        Assert.Equal(2, input.Read(destination));

        Assert.Equal(0.25f, destination[0]);

        Assert.Equal(-0.25f, destination[1]);

        Assert.Equal(0.5f, destination[2]);

        Assert.Equal(-0.5f, destination[3]);
    }

    [Fact]
    public void Packet_at_a_different_sample_rate_than_the_connection_s_format_is_rejected()
    {
        // The driver now advertises only 48 kHz to Windows (see RenderCircuit.cpp),
        // so this should be unreachable in production -- but it is the one guard
        // that stands between a future format-list regression and DriverAudioInput
        // silently reinterpreting a 44.1 kHz packet's bytes as 48 kHz audio. Kept as
        // defense in depth: it must fail loudly, not play the wrong pitch.
        var connection = new FakeDriverConnection();

        connection.EnqueuePacket(CreateFloatPacket(44100, 0.25f, -0.25f));

        using var input = new DriverAudioInput(connection);

        input.Start();

        DriverProtocolException exception = Assert.Throws<DriverProtocolException>(() => input.Read(new float[2]));

        Assert.Contains("format changed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bounded_buffer_rejects_oversized_packet()
    {
        var buffer = new DriverSharedBuffer(4);

        buffer.Replace(new float[] { 1, 2, 3, 4 });

        Assert.Equal(4, buffer.Count);

        Assert.Throws<InvalidOperationException>(() => buffer.Replace(new float[5]));

        Assert.Equal(4, buffer.CapacitySamples);
    }

    [Fact]
    public void One_input_does_not_create_duplicate_connections()
    {
        var connection = new FakeDriverConnection();

        using (var input = new DriverAudioInput(connection))
        {
            input.Start();
            input.Start();

            Assert.Equal(1, connection.ConnectCount);

            input.Stop();

            input.Start();

            Assert.Equal(1, connection.ConnectCount);
        }

        Assert.Equal(1, connection.DisconnectCount);
    }

    [Fact]
    public void Packet_sequence_gap_is_reported_as_a_timeline_discontinuity()
    {
        var connection = new FakeDriverConnection();

        connection.EnqueuePacket(CreateFloatPacket(sequence: 10, qpc: 1_000, sampleRate: 48_000, 0.25f, -0.25f));

        connection.EnqueuePacket(
            CreateFloatPacket(
                sequence: 12,
                qpc: 1_000 + (ulong)Stopwatch.Frequency / 100,
                sampleRate: 48_000,
                0.5f,
                -0.5f
            )
        );

        using var input = new DriverAudioInput(connection);

        input.Start();

        var destination = new float[2];

        input.Read(destination);

        Assert.Equal(0, input.DiscontinuityGeneration);

        input.Read(destination);

        Assert.Equal(1, input.DiscontinuityGeneration);
    }

    [Fact]
    public void Packet_qpc_pause_is_reported_even_when_sequence_is_contiguous()
    {
        var connection = new FakeDriverConnection();

        connection.EnqueuePacket(CreateFloatPacket(sequence: 20, qpc: 1_000, sampleRate: 48_000, 0.25f, -0.25f));

        connection.EnqueuePacket(
            CreateFloatPacket(
                sequence: 21,
                qpc: 1_000 + (ulong)Stopwatch.Frequency / 10,
                sampleRate: 48_000,
                0.5f,
                -0.5f
            )
        );

        using var input = new DriverAudioInput(connection);

        input.Start();

        var destination = new float[2];

        input.Read(destination);
        input.Read(destination);

        Assert.Equal(1, input.DiscontinuityGeneration);
    }

    private static DriverPacket CreateFloatPacket(uint sampleRate, params float[] samples)
    {
        return CreateFloatPacket(sequence: 1, qpc: 1, sampleRate, samples);
    }

    private static DriverPacket CreateFloatPacket(ulong sequence, ulong qpc, uint sampleRate, params float[] samples)
    {
        byte[] bytes = new byte[samples.Length * sizeof(float)];

        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        NativeDriverFormat format = new()
        {
            Size = checked((uint)Marshal.SizeOf<NativeDriverFormat>()),

            SampleRate = sampleRate,

            Channels = 2,

            SampleFormat = DriverSampleFormat.Float32,

            BitsPerSample = 32,

            BlockAlign = 8,
        };

        return new DriverPacket(sequence, qpc, format, bytes);
    }

    private sealed class FakeDriverConnection : IDriverConnection
    {
        private readonly Queue<object> _reads = new();

        public AudioFormat PreferredFormat => new(48000, 2);

        public int MaximumPacketBytes => 32768;

        public bool IsConnected { get; private set; }

        public int ConnectCount { get; private set; }

        public int DisconnectCount { get; private set; }

        public Exception? ConnectFailure { get; init; }

        public void EnqueuePacket(DriverPacket packet)
        {
            _reads.Enqueue(packet);
        }

        public void EnqueueFailure(Exception exception)
        {
            _reads.Enqueue(exception);
        }

        public void Connect()
        {
            ConnectCount++;

            if (ConnectFailure is not null)
            {
                throw ConnectFailure;
            }

            IsConnected = true;
        }

        public void Disconnect()
        {
            if (!IsConnected)
            {
                return;
            }

            DisconnectCount++;

            IsConnected = false;
        }

        public bool TryReadPacket(out DriverPacket packet)
        {
            if (_reads.Count == 0)
            {
                packet = default;

                return false;
            }

            object action = _reads.Dequeue();

            if (action is Exception exception)
            {
                IsConnected = false;

                throw exception;
            }

            packet = (DriverPacket)action;

            return true;
        }

        public DriverStatus GetStatus()
        {
            return new DriverStatus(
                true,
                true,
                false,
                IsConnected,
                0,
                DriverProtocol.MajorVersion,
                DriverProtocol.MinimumMinorVersion,
                DriverProtocol.RequiredCapabilities,
                PreferredFormat,
                null,
                0,
                0,
                0,
                0,
                null
            );
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}

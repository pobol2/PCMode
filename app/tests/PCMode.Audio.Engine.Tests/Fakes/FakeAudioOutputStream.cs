using PCMode.Audio.Core.Buffers;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Engine.Contracts;

namespace PCMode.Audio.Engine.Tests.Fakes;

internal sealed class FakeAudioOutputStream :
    IAudioOutputStream
{
    private readonly float[] _samples;

    private readonly bool _failOnStart;

    private readonly bool _autoAdvanceClock;

    private readonly int _initialQueuedFrames;

    private int _writtenSampleCount;

    private double _playedFramePosition;

    private long _submittedFramePosition;

    private ulong _qpcPosition100Nanoseconds;

    public AudioEndpointId EndpointId { get; }

    public AudioFormat Format { get; }

    public bool IsStarted { get; private set; }

    public bool IsDisposed { get; private set; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public long LastFramePosition { get; private set; }

    public bool FailNextWrite { get; set; }

    public long RecoveryFadeCount { get; set; }

    public long EndpointDryCount { get; set; }

    public double LongestSubmitGapMilliseconds { get; set; }

    public double LongestWriteMilliseconds { get; set; }

    public double LongestBufferWaitMilliseconds { get; set; }

    public FakeAudioOutputStream(
        AudioEndpointId endpointId,
        AudioFormat format,
        int sampleCapacity = 100_000,
        bool failOnStart = false,
        bool autoAdvanceClock = true,
        int initialQueuedFrames = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            sampleCapacity);

        ArgumentOutOfRangeException.ThrowIfNegative(
            initialQueuedFrames);

        EndpointId =
            endpointId;

        Format =
            format;

        _samples =
            new float[
                sampleCapacity];

        _failOnStart =
            failOnStart;

        _autoAdvanceClock =
            autoAdvanceClock;

        _initialQueuedFrames =
            initialQueuedFrames;
    }

    public FakeAudioOutputStream(
        AudioFormat format,
        int sampleCapacity = 100_000)
        : this(
            new AudioEndpointId(
                "fake-output"),
            format,
            sampleCapacity)
    {
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(
            IsDisposed,
            this);

        StartCount++;

        if (_failOnStart)
        {
            throw new InvalidOperationException(
                $"Simulated startup failure for '{EndpointId}'.");
        }

        _playedFramePosition =
            0;

        _submittedFramePosition =
            _initialQueuedFrames;

        _qpcPosition100Nanoseconds =
            0;

        IsStarted =
            true;
    }

    public AudioOutputClockSnapshot
        GetClockSnapshot()
    {
        ObjectDisposedException.ThrowIf(
            IsDisposed,
            this);

        if (!IsStarted)
        {
            throw new InvalidOperationException(
                "Fake output is not started.");
        }

        return new AudioOutputClockSnapshot(
            _playedFramePosition,
            _submittedFramePosition,
            _qpcPosition100Nanoseconds);
    }

    public AudioOutputDiagnostics
        GetDiagnostics()
    {
        return new AudioOutputDiagnostics(
            RecoveryFadeCount,
            EndpointDryCount,
            LongestSubmitGapMilliseconds,
            LongestWriteMilliseconds,
            LongestBufferWaitMilliseconds);
    }

    public void AdvanceClock(
        double playedFrames)
    {
        if (!double.IsFinite(
                playedFrames) ||
            playedFrames < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(playedFrames));
        }

        _playedFramePosition +=
            playedFrames;

        double seconds =
            playedFrames /
            Format.SampleRate;

        _qpcPosition100Nanoseconds +=
            checked(
                (ulong)Math.Round(
                    seconds *
                    10_000_000.0));
    }

    public void Write(
        AudioBlock block)
    {
        ObjectDisposedException.ThrowIf(
            IsDisposed,
            this);

        if (!IsStarted)
        {
            throw new InvalidOperationException(
                "Fake output is not started.");
        }

        if (block.Format !=
            Format)
        {
            throw new InvalidOperationException(
                "Fake output received the wrong format.");
        }

        if (FailNextWrite)
        {
            FailNextWrite =
                false;

            throw new InvalidOperationException(
                $"Simulated write failure for '{EndpointId}'.");
        }

        if (_writtenSampleCount +
            block.Samples.Length >
            _samples.Length)
        {
            throw new InvalidOperationException(
                "Fake output sample capacity exceeded.");
        }

        block.Samples.CopyTo(
            _samples.AsSpan(
                _writtenSampleCount));

        _writtenSampleCount +=
            block.Samples.Length;

        _submittedFramePosition +=
            block.FrameCount;

        if (_autoAdvanceClock)
        {
            AdvanceClock(
                block.FrameCount);
        }

        LastFramePosition =
            block.FramePosition;
    }

    public float[] GetWrittenSamples()
    {
        return _samples
            .AsSpan(
                0,
                _writtenSampleCount)
            .ToArray();
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(
            IsDisposed,
            this);

        if (!IsStarted)
        {
            return;
        }

        StopCount++;

        IsStarted =
            false;

        _playedFramePosition =
            0;

        _submittedFramePosition =
            0;

        _qpcPosition100Nanoseconds =
            0;
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsStarted =
            false;

        IsDisposed =
            true;
    }
}

internal sealed class FakeAudioOutputStreamFactory :
    IAudioOutputStreamFactory
{
    private readonly AudioFormat? _forcedFormat;

    private readonly int _sampleCapacity;

    private readonly HashSet<AudioEndpointId>
        _failOnStartEndpoints;

    private readonly bool _autoAdvanceClock;

    private readonly int _initialQueuedFrames;

    private readonly List<FakeAudioOutputStream>
        _streams =
        [];

    public IReadOnlyList<FakeAudioOutputStream> Streams =>
        _streams;

    public int CreateCount { get; private set; }

    public AudioEndpointId? LastEndpointId { get; private set; }

    public AudioFormat? LastRequestedFormat { get; private set; }

    public FakeAudioOutputStream? LastStream { get; private set; }

    public FakeAudioOutputStreamFactory(
        AudioFormat? forcedFormat = null,
        int sampleCapacity = 100_000,
        IEnumerable<AudioEndpointId>? failOnStartEndpoints = null,
        bool autoAdvanceClock = true,
        int initialQueuedFrames = 0)
    {
        _forcedFormat =
            forcedFormat;

        _sampleCapacity =
            sampleCapacity;

        _failOnStartEndpoints =
            failOnStartEndpoints is null
                ? []
                : new HashSet<AudioEndpointId>(
                    failOnStartEndpoints);

        _autoAdvanceClock =
            autoAdvanceClock;

        _initialQueuedFrames =
            initialQueuedFrames;
    }

    public IAudioOutputStream Create(
        AudioEndpointId endpointId,
        AudioFormat requestedFormat)
    {
        CreateCount++;

        LastEndpointId =
            endpointId;

        LastRequestedFormat =
            requestedFormat;

        var stream =
            new FakeAudioOutputStream(
                endpointId,
                _forcedFormat ??
                    requestedFormat,
                _sampleCapacity,
                _failOnStartEndpoints.Contains(
                    endpointId),
                _autoAdvanceClock,
                _initialQueuedFrames);

        _streams.Add(
            stream);

        LastStream =
            stream;

        return stream;
    }

    public void SetFailOnStart(
        AudioEndpointId endpointId,
        bool fail)
    {
        if (fail)
        {
            _failOnStartEndpoints.Add(
                endpointId);
        }
        else
        {
            _failOnStartEndpoints.Remove(
                endpointId);
        }
    }

    public FakeAudioOutputStream GetStream(
        AudioEndpointId endpointId)
    {
        return _streams.Single(
            stream =>
                stream.EndpointId ==
                endpointId);
    }

    public FakeAudioOutputStream GetLatestStream(
        AudioEndpointId endpointId)
    {
        return _streams.Last(
            stream =>
                stream.EndpointId ==
                endpointId);
    }
}

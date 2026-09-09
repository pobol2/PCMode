using System.Diagnostics;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Engine.Contracts;
using PCMode.Audio.Engine.Runtime;
using Xunit;

namespace PCMode.Audio.Engine.Tests.Runtime;

public sealed class JitterBufferedAudioInputTests
{
    private static readonly AudioFormat Stereo48k = new(sampleRate: 48_000, channelCount: 2);

    [Fact]
    public void Start_primes_the_cushion_so_early_reads_are_not_counted_as_underruns()
    {
        // An inner stream that never produces anything: every frame the
        // wrapper ever returns has to come from the priming silence alone.
        using var inner = new NeverProducesAudioInputStream(Stereo48k);

        using var jitterBuffered = new JitterBufferedAudioInput(inner, cushionMilliseconds: 100);

        jitterBuffered.Start();

        const int blockFrames = 480;

        int cushionFrames = Stereo48k.SampleRate * 100 / 1000;

        int primedBlocks = cushionFrames / blockFrames;

        var destination = new float[blockFrames * Stereo48k.ChannelCount];

        for (int i = 0; i < primedBlocks; i++)
        {
            int frames = jitterBuffered.Read(destination);

            Assert.Equal(blockFrames, frames);
        }

        Assert.Equal(0, jitterBuffered.UnderrunCount);

        // The prime is now exhausted and the inner stream has never
        // produced anything, so the next read has nothing real left to
        // draw on.
        jitterBuffered.Read(destination);

        Assert.True(jitterBuffered.UnderrunCount > 0, "Expected an underrun once the primed cushion ran out.");
    }

    [Fact]
    public void Read_always_returns_a_full_block_even_when_partially_silence()
    {
        using var inner = new NeverProducesAudioInputStream(Stereo48k);

        using var jitterBuffered = new JitterBufferedAudioInput(inner, cushionMilliseconds: 10);

        jitterBuffered.Start();

        var destination = new float[480 * Stereo48k.ChannelCount];

        // Read well past the tiny 10ms cushion. Every call must still
        // report a full block: a caller advancing a frame-position counter
        // by whatever this returns must never fall behind real time just
        // because the cushion ran dry.
        for (int i = 0; i < 10; i++)
        {
            int frames = jitterBuffered.Read(destination);

            Assert.Equal(480, frames);
        }
    }

    [Fact]
    public void Pump_thread_delivers_real_samples_from_a_bursty_inner_stream()
    {
        const float marker = 0.42f;

        // Bursts of 25ms worth of audio every 50ms: half the time the inner
        // stream has nothing available at all, the same shape as the
        // PCMode driver's own packet cadence relative to a steady consumer.
        using var inner = new BurstyAudioInputStream(
            Stereo48k,
            burstFrames: 48_000 * 25 / 1000,
            value: marker,
            burstInterval: TimeSpan.FromMilliseconds(50)
        );

        using var jitterBuffered = new JitterBufferedAudioInput(inner, cushionMilliseconds: 150);

        jitterBuffered.Start();

        var destination = new float[480 * Stereo48k.ChannelCount];

        bool sawRealAudio = false;

        var clock = Stopwatch.StartNew();

        // Consume for well over a second of simulated real-time playback,
        // pacing reads the way a real 10ms output block would, so the pump
        // thread has to keep up under realistic back-pressure.
        while (clock.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            int frames = jitterBuffered.Read(destination);

            Assert.Equal(480, frames);

            if (destination[0] == marker)
            {
                sawRealAudio = true;
            }

            Thread.Sleep(10);
        }

        Assert.True(
            sawRealAudio,
            "Expected at least one block of real (non-silence) audio to arrive through the jitter buffer."
        );

        // Lifecycle: Stop/Dispose must actually join the pump thread rather
        // than leaking it, and must complete within a bounded time.
        var stopwatch = Stopwatch.StartNew();

        jitterBuffered.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            "Stop() did not return promptly; the pump thread may not have been joined."
        );
    }

    [Fact]
    public void Audio_returning_after_an_underrun_is_ramped_from_silence()
    {
        var format = new AudioFormat(sampleRate: 1_000, channelCount: 1);

        using var inner = new OneShotAudioInputStream(format, frameCount: 4, value: 1.0f);

        using var jitterBuffered = new JitterBufferedAudioInput(inner, cushionMilliseconds: 4);

        jitterBuffered.Start();

        var block = new float[4];

        // Consume the four primed silent frames, then one empty block so the
        // wrapper observes the topology-like input interruption.
        jitterBuffered.Read(block);

        jitterBuffered.Read(block);

        inner.ReleaseAudio();

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);

        do
        {
            Thread.Sleep(5);

            jitterBuffered.Read(block);
        } while (block.All(sample => sample == 0.0f) && DateTime.UtcNow < deadline);

        Assert.Equal(0.0f, block[0]);

        Assert.True(block[1] > block[0]);

        Assert.InRange(block[1], 0.0f, 0.02f);
    }

    [Fact]
    public void Timeline_discontinuity_discards_stale_audio_and_primes_fresh_audio_safely()
    {
        var format = new AudioFormat(sampleRate: 1_000, channelCount: 1);

        using var inner = new DiscontinuousAudioInputStream(format, frameCount: 100);

        using var jitterBuffered = new JitterBufferedAudioInput(inner, cushionMilliseconds: 100);

        jitterBuffered.Start();

        Assert.True(
            inner.WaitForInitialAudio(TimeSpan.FromSeconds(2)),
            "The initial stale packet was not delivered to the jitter buffer."
        );

        var startupSilence = new float[100];

        jitterBuffered.Read(startupSilence);

        Assert.All(startupSilence, sample => Assert.Equal(0.0f, sample));

        inner.ReleaseFreshAudio();

        Assert.True(
            SpinWait.SpinUntil(() => jitterBuffered.DiscontinuityCount == 1, TimeSpan.FromSeconds(2)),
            "The source discontinuity was not observed by the jitter buffer."
        );

        var recovered = new float[125];

        jitterBuffered.Read(recovered);

        Assert.All(recovered.AsSpan(0, 25).ToArray(), sample => Assert.Equal(0.0f, sample));

        Assert.Equal(0.0f, recovered[25]);

        Assert.InRange(recovered[26], 0.0f, 0.02f);

        Assert.True(recovered[^1] > 0.25f, "Expected fresh audio to ramp above the stale packet's marker value.");
    }

    [Fact]
    public void Recovery_ramp_stays_pending_through_genuine_silence_after_a_discontinuity()
    {
        var format = new AudioFormat(sampleRate: 1_000, channelCount: 1);

        // Simulates the real-hardware failure: the source signals a
        // discontinuity (a topology change) and then keeps handing back
        // genuine zero-valued PCM -- not "nothing available" -- for well
        // longer than RecoveryRampFrameCount before real audio returns, the
        // same shape as ~300ms of driver-reported silence after a display
        // transition.
        using var inner = new DiscontinuityThenSilenceInputStream(
            format,
            initialFrameCount: 100,
            silentFrameCount: 400,
            audioValue: 1.0f
        );

        using var jitterBuffered = new JitterBufferedAudioInput(inner, cushionMilliseconds: 100);

        jitterBuffered.Start();

        Assert.True(
            inner.WaitForInitialAudio(TimeSpan.FromSeconds(2)),
            "The initial stale packet was not delivered to the jitter buffer."
        );

        var startupSilence = new float[100];

        jitterBuffered.Read(startupSilence);

        inner.ReleaseFreshAudio();

        Assert.True(
            SpinWait.SpinUntil(() => jitterBuffered.DiscontinuityCount == 1, TimeSpan.FromSeconds(2)),
            "The source discontinuity was not observed by the jitter buffer."
        );

        // Read past the silent stretch and well into where the recovery
        // ramp should be running, recording every sample in arrival order.
        var recorded = new List<float>();

        var block = new float[50];

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (recorded.Count < 700 && DateTime.UtcNow < deadline)
        {
            jitterBuffered.Read(block);

            recorded.AddRange(block);

            Thread.Sleep(1);
        }

        int firstNonZero = recorded.FindIndex(sample => sample != 0.0f);

        Assert.True(
            firstNonZero >= 0 && firstNonZero + 250 < recorded.Count,
            "Expected the recording window to capture both the silent stretch and enough of the recovery ramp that follows it."
        );

        // The bug this guards against: without a started latch, the ramp
        // fully expires while consuming the driver's genuine zero PCM, so
        // real audio resumes at full scale with no protection at all --
        // exactly the near-full-scale step the hardware test measured.
        Assert.True(
            recorded[firstNonZero] < 0.1f,
            $"Expected the first real sample after the silent stretch to be ramped from near zero, but it was {recorded[firstNonZero]}."
        );

        Assert.True(
            recorded[firstNonZero + 250] > 0.9f,
            "Expected the ramp to have climbed back to full scale well within its 256-frame window."
        );
    }

    /// <summary>
    /// Self-paces the same way the real driver and loopback inputs do (a
    /// short wait before reporting "nothing yet"), so a test using it never
    /// busy-spins the pump thread at 100% CPU while waiting.
    /// </summary>
    private sealed class NeverProducesAudioInputStream : IAudioInputStream
    {
        public AudioFormat Format { get; }

        public NeverProducesAudioInputStream(AudioFormat format)
        {
            Format = format;
        }

        public void Start() { }

        public int Read(Span<float> destination)
        {
            Thread.Sleep(1);

            return 0;
        }

        public void Stop() { }

        public void Dispose() { }
    }

    private sealed class BurstyAudioInputStream : IAudioInputStream
    {
        private readonly int _burstFrames;

        private readonly float _value;

        private readonly TimeSpan _burstInterval;

        private readonly Stopwatch _clock = new();

        private TimeSpan _nextBurstAt;

        public AudioFormat Format { get; }

        public BurstyAudioInputStream(AudioFormat format, int burstFrames, float value, TimeSpan burstInterval)
        {
            Format = format;

            _burstFrames = burstFrames;

            _value = value;

            _burstInterval = burstInterval;
        }

        public void Start()
        {
            _clock.Restart();

            _nextBurstAt = _burstInterval;
        }

        public int Read(Span<float> destination)
        {
            if (_clock.Elapsed < _nextBurstAt)
            {
                Thread.Sleep(1);

                return 0;
            }

            _nextBurstAt += _burstInterval;

            int frames = Math.Min(_burstFrames, destination.Length / Format.ChannelCount);

            int sampleCount = frames * Format.ChannelCount;

            destination.Slice(0, sampleCount).Fill(_value);

            if (sampleCount < destination.Length)
            {
                destination.Slice(sampleCount).Clear();
            }

            return frames;
        }

        public void Stop() { }

        public void Dispose() { }
    }

    private sealed class OneShotAudioInputStream : IAudioInputStream
    {
        private readonly ManualResetEventSlim _release = new(false);

        private readonly int _frameCount;

        private readonly float _value;

        private int _delivered;

        public AudioFormat Format { get; }

        public OneShotAudioInputStream(AudioFormat format, int frameCount, float value)
        {
            Format = format;

            _frameCount = frameCount;

            _value = value;
        }

        public void Start() { }

        public int Read(Span<float> destination)
        {
            if (!_release.IsSet || Interlocked.Exchange(ref _delivered, 1) != 0)
            {
                Thread.Sleep(1);

                return 0;
            }

            int frames = Math.Min(_frameCount, destination.Length / Format.ChannelCount);

            destination.Slice(0, frames * Format.ChannelCount).Fill(_value);

            return frames;
        }

        public void ReleaseAudio()
        {
            _release.Set();
        }

        public void Stop() { }

        public void Dispose()
        {
            _release.Dispose();
        }
    }

    private sealed class DiscontinuousAudioInputStream : IAudioInputStream, IAudioInputDiscontinuitySource
    {
        private readonly ManualResetEventSlim _releaseFresh = new(false);

        private readonly ManualResetEventSlim _initialDelivered = new(false);

        private readonly int _frameCount;

        private int _readNumber;

        public AudioFormat Format { get; }

        public long DiscontinuityGeneration { get; private set; }

        public DiscontinuousAudioInputStream(AudioFormat format, int frameCount)
        {
            Format = format;

            _frameCount = frameCount;
        }

        public void Start() { }

        public int Read(Span<float> destination)
        {
            int readNumber = Interlocked.CompareExchange(ref _readNumber, 0, 0);

            if (readNumber == 0 && Interlocked.CompareExchange(ref _readNumber, 1, 0) == 0)
            {
                destination.Slice(0, _frameCount).Fill(0.25f);

                _initialDelivered.Set();

                return _frameCount;
            }

            if (!_releaseFresh.IsSet || Interlocked.CompareExchange(ref _readNumber, 2, 1) != 1)
            {
                Thread.Sleep(1);

                return 0;
            }

            DiscontinuityGeneration++;

            destination.Slice(0, _frameCount).Fill(1.0f);

            return _frameCount;
        }

        public bool WaitForInitialAudio(TimeSpan timeout)
        {
            return _initialDelivered.Wait(timeout);
        }

        public void ReleaseFreshAudio()
        {
            _releaseFresh.Set();
        }

        public void Stop() { }

        public void Dispose()
        {
            _releaseFresh.Dispose();
            _initialDelivered.Dispose();
        }
    }

    /// <summary>
    /// Delivers one initial marker packet, then -- once released -- signals a
    /// single discontinuity and keeps returning genuine zero-valued PCM for
    /// <see cref="_silentFrameCount"/> frames before switching permanently to
    /// real (non-silent) audio. Models the PCMode driver continuing to hand
    /// back actual silence for a stretch after a topology-change
    /// discontinuity, rather than the discontinuity being immediately
    /// followed by real signal.
    /// </summary>
    private sealed class DiscontinuityThenSilenceInputStream : IAudioInputStream, IAudioInputDiscontinuitySource
    {
        private readonly ManualResetEventSlim _releaseFresh = new(false);

        private readonly ManualResetEventSlim _initialDelivered = new(false);

        private readonly int _initialFrameCount;

        private readonly int _silentFrameCount;

        private readonly float _audioValue;

        private int _state;

        private int _silentFramesDelivered;

        public AudioFormat Format { get; }

        public long DiscontinuityGeneration { get; private set; }

        public DiscontinuityThenSilenceInputStream(
            AudioFormat format,
            int initialFrameCount,
            int silentFrameCount,
            float audioValue
        )
        {
            Format = format;

            _initialFrameCount = initialFrameCount;

            _silentFrameCount = silentFrameCount;

            _audioValue = audioValue;
        }

        public void Start() { }

        public int Read(Span<float> destination)
        {
            int state = Interlocked.CompareExchange(ref _state, 0, 0);

            if (state == 0 && Interlocked.CompareExchange(ref _state, 1, 0) == 0)
            {
                destination.Slice(0, _initialFrameCount).Fill(0.25f);

                _initialDelivered.Set();

                return _initialFrameCount;
            }

            if (state == 1)
            {
                if (!_releaseFresh.IsSet)
                {
                    Thread.Sleep(1);

                    return 0;
                }

                Interlocked.CompareExchange(ref _state, 2, 1);

                DiscontinuityGeneration++;

                state = 2;
            }

            if (state == 2)
            {
                int remaining = _silentFrameCount - _silentFramesDelivered;

                if (remaining <= 0)
                {
                    Interlocked.CompareExchange(ref _state, 3, 2);

                    state = 3;
                }
                else
                {
                    int frames = Math.Min(remaining, destination.Length / Format.ChannelCount);

                    destination.Slice(0, frames * Format.ChannelCount).Fill(0.0f);

                    _silentFramesDelivered += frames;

                    return frames;
                }
            }

            int realFrames = destination.Length / Format.ChannelCount;

            destination.Slice(0, realFrames * Format.ChannelCount).Fill(_audioValue);

            return realFrames;
        }

        public bool WaitForInitialAudio(TimeSpan timeout)
        {
            return _initialDelivered.Wait(timeout);
        }

        public void ReleaseFreshAudio()
        {
            _releaseFresh.Set();
        }

        public void Stop() { }

        public void Dispose()
        {
            _releaseFresh.Dispose();
            _initialDelivered.Dispose();
        }
    }
}

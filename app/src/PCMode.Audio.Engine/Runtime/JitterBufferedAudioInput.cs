using PCMode.Audio.Core.Models;
using PCMode.Audio.Engine.Contracts;
using PCMode.Audio.Engine.Timing;

namespace PCMode.Audio.Engine.Runtime;

/// <summary>
/// Decouples a bursty input stream from the steady-cadence consumer reading
/// it, by pumping the inner stream on its own thread into a ring buffer with
/// a genuine cushion of frames, rather than letting the consumer read the
/// inner stream directly.
/// </summary>
/// <remarks>
/// The PCMode virtual audio driver delivers PCM in bursts on its own
/// schedule (its ACX render-timer packet interval, tens of milliseconds
/// apart), not in the small steady increments a real playback device
/// expects. Previously, the Host's single audio thread pulled directly from
/// the driver and then immediately wrote what it got to the real output on
/// the same call -- so any delay waiting for the next driver burst delayed
/// the write to the real device by the same amount, which is exactly what a
/// listener hears as stutter.
///
/// This wrapper fixes that by putting a real cushion between the two: a
/// background thread reads the inner stream as fast as it becomes available
/// and writes into <see cref="AudioRingBuffer"/>, while <see cref="Read"/>
/// -- called by the Host's existing processing loop -- always drains a full,
/// steady-sized block from that buffer, zero-filling (and counting as an
/// underrun) only if the cushion has been fully exhausted. The buffer is
/// primed with silence up front, which is the standard jitter-buffer
/// trade-off: it adds a small, fixed startup delay in exchange for
/// absorbing the input's burstiness afterward.
/// </remarks>
public sealed class JitterBufferedAudioInput : IAudioInputStream
{
    public const int RecoveryRampFrameCount = 256;

    public const int DiscontinuityPrimeMilliseconds = 25;

    /// <summary>
    /// Comfortably more than one PCMode driver packet interval (observed
    /// around 53ms for the negotiated RT packet size), so a single missed or
    /// delayed burst -- or an incidental GC pause or scheduling hiccup on
    /// the pump thread -- does not run the cushion dry.
    /// </summary>
    public const int DefaultCushionMilliseconds = 150;

    private readonly IAudioInputStream _inner;

    private readonly int _cushionFrames;

    private readonly object _syncRoot = new();

    private AudioRingBuffer? _buffer;

    private float[]? _pumpBuffer;

    private Thread? _pumpThread;

    private CancellationTokenSource? _pumpCancellation;

    private volatile Exception? _pumpFailure;

    private int _recoveryRampFramesRemaining;

    private bool _recoveryRampStarted;

    private int _recoverySilenceFramesRemaining;

    private long _bufferDiscontinuityGeneration;

    private long _observedDiscontinuityGeneration;

    private bool _started;

    private bool _disposed;

    public AudioFormat Format => _inner.Format;

    /// <summary>
    /// How many times <see cref="Read"/> had to zero-fill part of a block
    /// because the cushion ran out. A healthy system stays at, or very near,
    /// zero after startup; a climbing count under sustained playback means
    /// the cushion is too small for how this machine is actually behaving,
    /// not a one-off.
    /// </summary>
    public long UnderrunCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _buffer?.UnderrunCount ?? 0;
            }
        }
    }

    public long DiscontinuityCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _bufferDiscontinuityGeneration;
            }
        }
    }

    public JitterBufferedAudioInput(IAudioInputStream inner, int cushionMilliseconds = DefaultCushionMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(inner);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cushionMilliseconds);

        _inner = inner;

        _cushionFrames = checked(inner.Format.SampleRate * cushionMilliseconds / 1000);
    }

    public void Start()
    {
        ThrowIfDisposed();

        if (_started)
        {
            return;
        }

        _inner.Start();

        // Sized well past the cushion so the producer thread has real room
        // to run ahead of the consumer, not just exactly enough for one
        // priming fill.
        _buffer = new AudioRingBuffer(checked(_cushionFrames * 4), Format.ChannelCount);

        _buffer.PrimeSilence(_cushionFrames);

        _pumpBuffer = new float[checked(_cushionFrames * Format.ChannelCount)];

        _pumpFailure = null;

        _recoveryRampFramesRemaining = 0;

        _recoveryRampStarted = false;

        _recoverySilenceFramesRemaining = 0;

        _bufferDiscontinuityGeneration = 0;

        _observedDiscontinuityGeneration = 0;

        _pumpCancellation = new CancellationTokenSource();

        _pumpThread = new Thread(PumpMain) { IsBackground = true, Name = "pcmode-jitter-buffer-pump" };

        _pumpThread.Start();

        _started = true;
    }

    public int Read(Span<float> destination)
    {
        ThrowIfDisposed();

        if (!_started)
        {
            throw new InvalidOperationException("Jitter-buffered input is not started.");
        }

        if (destination.IsEmpty)
        {
            return 0;
        }

        // Surfaced here, on the consumer's own call, rather than left to
        // silently under-deliver: a pump thread that stopped running is a
        // real failure of the input stream, not an underrun to paper over.
        Exception? failure = _pumpFailure;

        if (failure is not null)
        {
            throw new InvalidOperationException("The jitter buffer's pump thread failed.", failure);
        }

        // AudioRingBuffer.Read always writes the full destination -- real
        // samples, or zero-padding past whatever the cushion could supply
        // -- but its return value counts only the real portion, undercounting
        // on a shortfall. That distinction is exactly what an underrun
        // metric needs, but it is wrong for a frame count returned to the
        // caller: this wrapper's whole purpose is a steady block every
        // call, so the caller must see the full block advance the timeline
        // even when part of it was silence.
        int availableFrames;

        long discontinuityGeneration;

        lock (_syncRoot)
        {
            availableFrames = _buffer!.Read(destination);

            discontinuityGeneration = _bufferDiscontinuityGeneration;
        }

        int requestedFrames = destination.Length / Format.ChannelCount;

        if (discontinuityGeneration != _observedDiscontinuityGeneration)
        {
            _observedDiscontinuityGeneration = discontinuityGeneration;

            _recoverySilenceFramesRemaining = GetDiscontinuityPrimeFrames();

            _recoveryRampFramesRemaining = RecoveryRampFrameCount;

            _recoveryRampStarted = false;
        }

        int recoverySilenceFrames = Math.Min(availableFrames, _recoverySilenceFramesRemaining);

        _recoverySilenceFramesRemaining -= recoverySilenceFrames;

        ApplyRecoveryRamp(
            destination.Slice(checked(recoverySilenceFrames * Format.ChannelCount)),
            availableFrames - recoverySilenceFrames
        );

        if (availableFrames < requestedFrames)
        {
            ApplyUnderrunFadeOut(destination, availableFrames);

            _recoveryRampFramesRemaining = RecoveryRampFrameCount;

            _recoveryRampStarted = false;
        }

        return requestedFrames;
    }

    public void Stop()
    {
        ThrowIfDisposed();

        if (!_started)
        {
            return;
        }

        _pumpCancellation!.Cancel();

        _pumpThread!.Join(TimeSpan.FromSeconds(2));

        _pumpCancellation.Dispose();

        _pumpCancellation = null;

        _pumpThread = null;

        _inner.Stop();

        lock (_syncRoot)
        {
            _buffer = null;
        }

        _pumpBuffer = null;

        _recoveryRampFramesRemaining = 0;

        _recoveryRampStarted = false;

        _recoverySilenceFramesRemaining = 0;

        _started = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_started)
        {
            try
            {
                Stop();
            }
            catch { }
        }

        _inner.Dispose();

        _disposed = true;
    }

    private void PumpMain()
    {
        CancellationToken token = _pumpCancellation!.Token;

        float[] pumpBuffer = _pumpBuffer!;

        IAudioInputDiscontinuitySource? discontinuitySource = _inner as IAudioInputDiscontinuitySource;

        long observedSourceGeneration = discontinuitySource?.DiscontinuityGeneration ?? 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                int frames = _inner.Read(pumpBuffer);

                long sourceGeneration = discontinuitySource?.DiscontinuityGeneration ?? observedSourceGeneration;

                bool discontinuity = sourceGeneration != observedSourceGeneration;

                observedSourceGeneration = sourceGeneration;

                if (frames <= 0 && !discontinuity)
                {
                    // The inner stream's own Read already paces itself
                    // (the driver client sleeps briefly while waiting for
                    // the next packet); nothing productive to do here but
                    // try again.
                    continue;
                }

                int sampleCount = checked(frames * Format.ChannelCount);

                lock (_syncRoot)
                {
                    AudioRingBuffer? buffer = _buffer;

                    if (buffer is null)
                    {
                        return;
                    }

                    if (discontinuity)
                    {
                        buffer.Reset();

                        buffer.PrimeSilence(GetDiscontinuityPrimeFrames());

                        _bufferDiscontinuityGeneration++;
                    }

                    if (frames <= 0)
                    {
                        continue;
                    }

                    if (buffer.FreeFrames < frames)
                    {
                        // The cushion is already full because the consumer
                        // has fallen behind for a sustained stretch, not a
                        // single burst. There is nowhere to put this chunk
                        // without either overflowing or evicting audio the
                        // consumer has not read yet; dropping it here is
                        // the same trade-off the driver's own transport
                        // ring already makes under sustained overload.
                        continue;
                    }

                    buffer.Write(pumpBuffer.AsSpan(0, sampleCount));
                }
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            _pumpFailure = ex;
        }
    }

    private int GetDiscontinuityPrimeFrames()
    {
        return Math.Min(_cushionFrames, checked(Format.SampleRate * DiscontinuityPrimeMilliseconds / 1000));
    }

    // Below this a sample is treated as driver-provided silence rather than
    // signal, tight enough that a genuine zero crossing in real audio still
    // reads as "started" once the ramp has already begun (see the started
    // latch below), but loose enough to absorb float round-trip noise.
    private const float RecoverySilenceEpsilon = 1e-6f;

    private void ApplyRecoveryRamp(Span<float> samples, int availableFrames)
    {
        if (_recoveryRampFramesRemaining == 0 || availableFrames == 0)
        {
            return;
        }

        int channels = Format.ChannelCount;

        for (int frame = 0; frame < availableFrames && _recoveryRampFramesRemaining > 0; frame++)
        {
            // The ramp exists to protect the transition when real audio
            // resumes after a discontinuity. While the source is still
            // handing back driver-provided zero PCM, there is nothing to
            // protect yet -- consuming ramp frames here would let the whole
            // ramp expire during that silence, leaving the actual resume
            // unprotected. Stay pending until the first frame that carries
            // real signal, then run the ramp normally (including through
            // any later zero crossings, which the started latch does not
            // re-pause for).
            if (!_recoveryRampStarted && IsSilentFrame(samples, frame, channels))
            {
                continue;
            }

            _recoveryRampStarted = true;

            float level = 1.0f - (_recoveryRampFramesRemaining / (float)RecoveryRampFrameCount);

            ScaleFrame(samples, frame, channels, level);

            _recoveryRampFramesRemaining--;
        }
    }

    private static bool IsSilentFrame(Span<float> samples, int frame, int channels)
    {
        int sampleStart = frame * channels;

        for (int channel = 0; channel < channels; channel++)
        {
            if (MathF.Abs(samples[sampleStart + channel]) > RecoverySilenceEpsilon)
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyUnderrunFadeOut(Span<float> samples, int availableFrames)
    {
        if (availableFrames == 0)
        {
            return;
        }

        int channels = Format.ChannelCount;

        int fadeFrames = Math.Min(RecoveryRampFrameCount, availableFrames);

        int firstFadeFrame = availableFrames - fadeFrames;

        for (int frame = 0; frame < fadeFrames; frame++)
        {
            float level = 1.0f - ((frame + 1) / (float)fadeFrames);

            ScaleFrame(samples, firstFadeFrame + frame, channels, level);
        }
    }

    private static void ScaleFrame(Span<float> samples, int frame, int channels, float level)
    {
        int sampleStart = frame * channels;

        for (int channel = 0; channel < channels; channel++)
        {
            samples[sampleStart + channel] *= level;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

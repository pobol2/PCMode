using System.Diagnostics;
using System.Runtime.InteropServices;
using PCMode.Audio.Core.Buffers;
using PCMode.Audio.Core.Interop;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Engine.Contracts;
using PCMode.Audio.Windows.Formats;
using PCMode.Audio.Windows.Interop;

namespace PCMode.Audio.Windows.Streams;

public sealed class WasapiRenderOutput : IAudioOutputStream
{
    /// <summary>
    /// A little over five milliseconds at 48 kHz. Long enough that the
    /// resumed audio does not sound like a volume change, short enough that
    /// it disappears inside one <see cref="Write"/> call's block for any
    /// ordinary block size this Host uses.
    /// </summary>
    private const int RecoveryRampFrameCount = 256;

    /// <summary>
    /// Floor for the wall-clock backstop, which is only a backstop:
    /// <see cref="IAudioClient.GetCurrentPadding"/> reporting zero is the
    /// primary signal that the endpoint ran dry, and it needs no threshold
    /// at all. A threshold is still kept for the case where a gap somehow
    /// closes again before the padding can be read, and it is deliberately
    /// far longer than any legitimate pacing gap, because a real-hardware
    /// regression showed what happens when an ordinary wait is mistaken for
    /// a discontinuity: the recovery fade armed constantly during perfectly
    /// normal playback, repeatedly pulling the signal down to silence and
    /// back for no reason and making steady-state audio measurably quieter
    /// and glitchier than with no fade logic at all.
    /// </summary>
    private static readonly TimeSpan MinimumRecoveryWait = TimeSpan.FromMilliseconds(50);

    private readonly IMMDevice _device;

    private readonly IAudioClient _audioClient;

    private readonly IAudioRenderClient _renderClient;

    private readonly IAudioClock _audioClock;

    private readonly AutoResetEvent _bufferEvent;

    private readonly WaveFormatInfo _streamFormat;

    private readonly uint _bufferFrames;

    private readonly ulong _clockFrequency;

    /// <summary>
    /// How long the endpoint buffer takes to play out when full, and so how
    /// long the device can keep going without hearing from this stream
    /// before it has nothing left to play. The wall-clock backstop is scaled
    /// from this rather than from a fixed guess: what counts as "too long
    /// since audio last reached the device" depends entirely on how much
    /// that device had queued in the meantime, which is a property of the
    /// endpoint, not a constant.
    /// </summary>
    private readonly TimeSpan _endpointBufferDuration;

    private readonly TimeSpan _maximumContinuousSubmitGap;

    private long _submittedFramePosition;

    private long _recoveryFadeCount;

    private long _endpointDryCount;

    private double _longestSubmitGapMilliseconds;

    private double _longestWriteMilliseconds;

    private double _longestBufferWaitMilliseconds;

    private int _fadeInFramesRemaining;

    /// <summary>
    /// When a chunk of audio last actually reached the device -- updated
    /// only after a submitted <see cref="Write"/> chunk succeeds, never by
    /// <see cref="GetClockSnapshot"/> (updating it there would erase the
    /// evidence that GetClockSnapshot's own call was what was slow, since
    /// the very next check would then measure from the moment it just
    /// finished rather than from whenever contact was last confirmed before
    /// it). Checked before each chunk this stream is about to submit. A
    /// first attempt at this fix only timed the explicit
    /// <see cref="_bufferEvent"/> wait and, against real hardware, never
    /// actually caught the interruption it was meant to -- the delay was
    /// not reliably inside that specific call. Measuring the gap since audio
    /// was last confirmed delivered instead catches a stall wherever it
    /// actually happens: inside GetCurrentPadding, the wait, GetBuffer, a
    /// slow GetClockSnapshot call made by the caller just before this Write,
    /// or the caller itself having been delayed before calling Write at all.
    /// </summary>
    private long _lastActivityTimestamp;

    private bool _started;

    private bool _disposed;

    public AudioFormat Format { get; }

    public AudioFormat EndpointMixFormat { get; }

    public AudioEndpointId EndpointId { get; }

    public WasapiRenderOutput(AudioEndpointId endpointId, AudioFormat requestedFormat)
    {
        if (endpointId == default)
        {
            throw new ArgumentException("An output endpoint is required.", nameof(endpointId));
        }

        EndpointId = endpointId;

        IMMDevice? device = null;

        IAudioClient? audioClient = null;

        IAudioRenderClient? renderClient = null;

        IAudioClock? audioClock = null;

        AutoResetEvent? bufferEvent = null;

        ulong clockFrequency = 0;

        IntPtr mixFormatPointer = IntPtr.Zero;

        IntPtr streamFormatPointer = IntPtr.Zero;

        try
        {
            IMMDeviceEnumerator enumerator = CoreAudioInterop.CreateEnumerator();

            try
            {
                WasapiError.ThrowIfFailed(
                    enumerator.GetDevice(endpointId.Value, out device),
                    "IMMDeviceEnumerator.GetDevice"
                );
            }
            finally
            {
                CoreAudioInterop.Release(enumerator);
            }

            Guid audioClientId = typeof(IAudioClient).GUID;

            WasapiError.ThrowIfFailed(
                device!.Activate(ref audioClientId, ClsCtx.All, IntPtr.Zero, out object? audioClientObject),
                "IMMDevice.Activate(IAudioClient)"
            );

            audioClient = (IAudioClient)audioClientObject!;

            WasapiError.ThrowIfFailed(audioClient.GetMixFormat(out mixFormatPointer), "IAudioClient.GetMixFormat");

            WaveFormatInfo endpointMix = WaveFormatAdapter.Read(mixFormatPointer);

            EndpointMixFormat = new AudioFormat(endpointMix.SampleRate, endpointMix.ChannelCount);

            streamFormatPointer = WaveFormatAdapter.AllocateFloat32(requestedFormat);

            _streamFormat = WaveFormatAdapter.Read(streamFormatPointer);

            Format = requestedFormat;

            bufferEvent = new AutoResetEvent(false);

            WasapiError.ThrowIfFailed(
                audioClient.Initialize(
                    AudioClientShareMode.Shared,
                    AudioClientStreamFlags.EventCallback
                        | AudioClientStreamFlags.AutoConvertPcm
                        | AudioClientStreamFlags.SrcDefaultQuality,
                    0,
                    0,
                    streamFormatPointer,
                    IntPtr.Zero
                ),
                "IAudioClient.Initialize(render)"
            );

            WasapiError.ThrowIfFailed(
                audioClient.SetEventHandle(bufferEvent.SafeWaitHandle.DangerousGetHandle()),
                "IAudioClient.SetEventHandle"
            );

            WasapiError.ThrowIfFailed(audioClient.GetBufferSize(out _bufferFrames), "IAudioClient.GetBufferSize");

            Guid renderClientId = typeof(IAudioRenderClient).GUID;

            WasapiError.ThrowIfFailed(
                audioClient.GetService(ref renderClientId, out object renderObject),
                "IAudioClient.GetService(IAudioRenderClient)"
            );

            renderClient = (IAudioRenderClient)renderObject;

            Guid audioClockId = typeof(IAudioClock).GUID;

            WasapiError.ThrowIfFailed(
                audioClient.GetService(ref audioClockId, out object clockObject),
                "IAudioClient.GetService(IAudioClock)"
            );

            audioClock = (IAudioClock)clockObject;

            WasapiError.ThrowIfFailed(audioClock.GetFrequency(out clockFrequency), "IAudioClock.GetFrequency");

            if (clockFrequency == 0)
            {
                throw new InvalidOperationException("WASAPI returned a zero audio-clock frequency.");
            }

            _device = device;

            _audioClient = audioClient;

            _renderClient = renderClient;

            _audioClock = audioClock;

            _bufferEvent = bufferEvent;

            _clockFrequency = clockFrequency;

            _endpointBufferDuration = TimeSpan.FromSeconds(_bufferFrames / (double)Format.SampleRate);

            TimeSpan scaledSubmitGap = _endpointBufferDuration * 4;

            _maximumContinuousSubmitGap = scaledSubmitGap > MinimumRecoveryWait ? scaledSubmitGap : MinimumRecoveryWait;

            device = null;

            audioClient = null;

            renderClient = null;

            audioClock = null;

            bufferEvent = null;
        }
        catch
        {
            bufferEvent?.Dispose();

            CoreAudioInterop.Release(audioClock);

            CoreAudioInterop.Release(renderClient);

            CoreAudioInterop.Release(audioClient);

            CoreAudioInterop.Release(device);

            throw;
        }
        finally
        {
            if (mixFormatPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(mixFormatPointer);
            }

            if (streamFormatPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(streamFormatPointer);
            }
        }
    }

    public void Start()
    {
        ThrowIfDisposed();

        if (_started)
        {
            return;
        }

        WasapiError.ThrowIfFailed(
            _renderClient.GetBuffer(_bufferFrames, out _),
            "IAudioRenderClient.GetBuffer(initial)"
        );

        WasapiError.ThrowIfFailed(
            _renderClient.ReleaseBuffer(_bufferFrames, AudioClientBufferFlags.Silent),
            "IAudioRenderClient.ReleaseBuffer(initial)"
        );

        // Start follows a fresh Initialize or Stop/Reset. The endpoint clock
        // returns to that stream-local origin too, so carrying the old
        // submission position forward would look like a large false queue to
        // the synchronizer and force an aggressive resync.
        _submittedFramePosition = _bufferFrames;

        _fadeInFramesRemaining = 0;

        _lastActivityTimestamp = Stopwatch.GetTimestamp();

        WasapiError.ThrowIfFailed(_audioClient.Start(), "IAudioClient.Start(render)");

        _started = true;
    }

    public AudioOutputClockSnapshot GetClockSnapshot()
    {
        ThrowIfDisposed();

        if (!_started)
        {
            throw new InvalidOperationException("Render output is not started.");
        }

        WasapiError.ThrowIfFailed(
            _audioClock.GetPosition(out ulong devicePosition, out ulong qpcPosition),
            "IAudioClock.GetPosition"
        );

        double elapsedSeconds = (double)devicePosition / _clockFrequency;

        double playedFramePosition = elapsedSeconds * Format.SampleRate;

        return new AudioOutputClockSnapshot(playedFramePosition, _submittedFramePosition, qpcPosition);
    }

    public AudioOutputDiagnostics GetDiagnostics()
    {
        return new AudioOutputDiagnostics(
            Interlocked.Read(ref _recoveryFadeCount),
            Interlocked.Read(ref _endpointDryCount),
            Volatile.Read(ref _longestSubmitGapMilliseconds),
            Volatile.Read(ref _longestWriteMilliseconds),
            Volatile.Read(ref _longestBufferWaitMilliseconds)
        );
    }

    public void Write(AudioBlock block)
    {
        ThrowIfDisposed();

        if (!_started)
        {
            throw new InvalidOperationException("Render output is not started.");
        }

        if (block.Format != Format)
        {
            throw new NotSupportedException("Render block format does not match the configured client stream format.");
        }

        int remainingFrames = block.FrameCount;

        int sourceFrameOffset = 0;

        // Timed end to end, and reported next to the gap since audio last
        // reached the device, because the two together say which side of
        // this call a stall was on: a long write means the route was blocked
        // here while the endpoint starved, whereas a quick write after a
        // long gap means the route was never called at all for that stretch
        // and the audio was already missing before it arrived.
        long writeStartTimestamp = Stopwatch.GetTimestamp();

        while (remainingFrames > 0)
        {
            WasapiError.ThrowIfFailed(
                _audioClient.GetCurrentPadding(out uint paddingFrames),
                "IAudioClient.GetCurrentPadding"
            );

            // Zero padding means every frame ever handed to this endpoint has
            // already been consumed: the device ran dry, and the engine has
            // been rendering silence in place of the audio that should have
            // been there. Whatever goes in next is therefore the first audio
            // after a break in the sound, however briefly the break lasted
            // and whichever call above was the slow one.
            //
            // This is the point of checking padding rather than a clock.
            // Three successive wall-clock heuristics -- timing the buffer
            // wait, then timing the whole Write, then timing the gap since
            // the last successful submission -- all failed against real
            // hardware, because the stall they were looking for is not
            // reliably longer than any threshold they could safely use: set
            // low enough to catch it, they also fire on ordinary pacing and
            // wreck steady-state audio. Padding needs no threshold. It is
            // WASAPI stating that the discontinuity happened, rather than
            // this code inferring one from how long something took.
            if (paddingFrames == 0)
            {
                _endpointDryCount++;

                ArmRecoveryFade();
            }

            uint availableFrames = _bufferFrames - paddingFrames;

            if (availableFrames == 0)
            {
                long waitStartTimestamp = Stopwatch.GetTimestamp();

                bool signalled = _bufferEvent.WaitOne(1000);

                _longestBufferWaitMilliseconds = Math.Max(
                    _longestBufferWaitMilliseconds,
                    Stopwatch.GetElapsedTime(waitStartTimestamp).TotalMilliseconds
                );

                if (!signalled)
                {
                    throw new TimeoutException("Timed out waiting for the WASAPI render endpoint.");
                }

                continue;
            }

            int framesToWrite = Math.Min(remainingFrames, checked((int)availableFrames));

            WasapiError.ThrowIfFailed(
                _renderClient.GetBuffer(checked((uint)framesToWrite), out IntPtr destination),
                "IAudioRenderClient.GetBuffer"
            );

            bool acquired = true;

            try
            {
                int sampleOffset = checked(sourceFrameOffset * Format.ChannelCount);

                int sampleCount = checked(framesToWrite * Format.ChannelCount);

                Span<float> samples = block.Samples.Slice(sampleOffset, sampleCount);

                // Backstop to the padding check above, for a gap that opened
                // and closed again between two reads of the padding -- long
                // enough for the device to have run dry and then been
                // refilled by an intervening chunk, so that no single read
                // ever saw zero. Scaled to the endpoint's own buffer rather
                // than to a fixed number of milliseconds, and set several
                // times longer than that buffer can possibly last, so that
                // ordinary pacing never reaches it.
                TimeSpan submitGap = Stopwatch.GetElapsedTime(_lastActivityTimestamp);

                _longestSubmitGapMilliseconds = Math.Max(_longestSubmitGapMilliseconds, submitGap.TotalMilliseconds);

                if (submitGap > _maximumContinuousSubmitGap)
                {
                    ArmRecoveryFade();
                }

                ApplyRecoveryFade(samples, framesToWrite);

                WaveFormatAdapter.FloatToNative(samples, destination, framesToWrite, _streamFormat);

                WasapiError.ThrowIfFailed(
                    _renderClient.ReleaseBuffer(checked((uint)framesToWrite), AudioClientBufferFlags.None),
                    "IAudioRenderClient.ReleaseBuffer"
                );

                acquired = false;

                _lastActivityTimestamp = Stopwatch.GetTimestamp();

                _submittedFramePosition = checked(_submittedFramePosition + framesToWrite);
            }
            finally
            {
                if (acquired)
                {
                    _renderClient.ReleaseBuffer(0, AudioClientBufferFlags.None);
                }
            }

            sourceFrameOffset += framesToWrite;

            remainingFrames -= framesToWrite;
        }

        _longestWriteMilliseconds = Math.Max(
            _longestWriteMilliseconds,
            Stopwatch.GetElapsedTime(writeStartTimestamp).TotalMilliseconds
        );
    }

    /// <summary>
    /// Arms the fade, unless one is already running. Both triggers can fire
    /// for a single interruption, and either can fire repeatedly across the
    /// chunks one <see cref="Write"/> call is split into; re-arming a ramp
    /// already in progress would snap its level back to zero partway up and
    /// introduce a downward step of exactly the kind this exists to remove.
    /// </summary>
    private void ArmRecoveryFade()
    {
        if (_fadeInFramesRemaining != 0)
        {
            return;
        }

        _fadeInFramesRemaining = RecoveryRampFrameCount;

        _recoveryFadeCount++;
    }

    private void ApplyRecoveryFade(Span<float> samples, int frameCount)
    {
        if (_fadeInFramesRemaining == 0)
        {
            return;
        }

        int channels = Format.ChannelCount;

        for (int frame = 0; frame < frameCount && _fadeInFramesRemaining > 0; frame++)
        {
            int sampleStart = frame * channels;

            // Silent frames pass through without spending ramp on them.
            // Scaling silence achieves nothing, and a route recovering from
            // a stall submits a stretch of silence first -- a reset delay
            // buffer replays its priming silence ahead of the audio behind
            // it -- so letting that consume the ramp would leave the audio
            // that follows starting at full level again, which is the
            // failure this whole path exists to prevent.
            bool frameIsSilent = true;

            for (int channel = 0; channel < channels; channel++)
            {
                if (samples[sampleStart + channel] != 0.0f)
                {
                    frameIsSilent = false;

                    break;
                }
            }

            if (frameIsSilent)
            {
                continue;
            }

            float level = 1.0f - (_fadeInFramesRemaining / (float)RecoveryRampFrameCount);

            for (int channel = 0; channel < channels; channel++)
            {
                samples[sampleStart + channel] *= level;
            }

            _fadeInFramesRemaining--;
        }
    }

    public void Stop()
    {
        ThrowIfDisposed();

        if (!_started)
        {
            return;
        }

        WasapiError.ThrowIfFailed(_audioClient.Stop(), "IAudioClient.Stop(render)");

        _started = false;

        // IAudioClient.Stop leaves queued samples and clock state intact.
        // Clear both before this stream can be started again so stale audio is
        // never replayed after an endpoint/topology interruption.
        WasapiError.ThrowIfFailed(_audioClient.Reset(), "IAudioClient.Reset(render)");

        _submittedFramePosition = 0;

        _fadeInFramesRemaining = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Exception? stopFailure = null;

        try
        {
            if (_started)
            {
                try
                {
                    Stop();
                }
                catch (Exception ex)
                {
                    stopFailure = ex;
                }
            }
        }
        finally
        {
            _started = false;

            _bufferEvent.Dispose();

            CoreAudioInterop.Release(_audioClock);

            CoreAudioInterop.Release(_renderClient);

            CoreAudioInterop.Release(_audioClient);

            CoreAudioInterop.Release(_device);

            _disposed = true;
        }

        if (stopFailure is not null)
        {
            throw new InvalidOperationException("WASAPI render output failed to stop during disposal.", stopFailure);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

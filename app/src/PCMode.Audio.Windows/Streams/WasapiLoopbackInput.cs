using System.Runtime.InteropServices;
using PCMode.Audio.Core.Interop;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Engine.Contracts;
using PCMode.Audio.Windows.Formats;
using PCMode.Audio.Windows.Interop;

namespace PCMode.Audio.Windows.Streams;

public sealed class WasapiLoopbackInput : IAudioInputStream
{
    private readonly IMMDevice _device;
    private readonly IAudioClient _audioClient;
    private readonly IAudioCaptureClient _captureClient;
    private readonly AutoResetEvent _bufferEvent;
    private readonly WaveFormatInfo _nativeFormat;
    private readonly float[] _packetSamples;

    private int _pendingSampleOffset;
    private int _pendingSampleCount;

    private bool _started;
    private bool _disposed;

    public AudioFormat Format { get; }

    public AudioEndpointId EndpointId { get; }

    /// <summary>
    /// Packets WASAPI delivered with its data-discontinuity flag set,
    /// meaning audio that existed on the endpoint never reached this client
    /// and the packet handed over does not continue the previous one.
    /// </summary>
    /// <remarks>
    /// Reading the flag matters because nothing in the returned samples
    /// shows the loss: this class hands back a packet at a time and a caller
    /// concatenating them sees one unbroken buffer, with a dropped stretch
    /// appearing as an instantaneous jump from wherever the waveform was to
    /// wherever it had got to by the time capture resumed. On a signal that
    /// is analysed for discontinuities, a capture-side drop is therefore
    /// indistinguishable from a genuine glitch at the endpoint unless this
    /// flag is consulted.
    /// </remarks>
    public long DiscontinuityCount { get; private set; }

    /// <summary>
    /// Packets whose reported device position did not continue where the
    /// previous packet ended, meaning frames were lost between them.
    /// </summary>
    /// <remarks>
    /// The authoritative gap detector, and the reason
    /// <see cref="DiscontinuityCount"/> alone is not enough: the
    /// data-discontinuity flag is advisory and is not reliably set on every
    /// loss, whereas the device position is the endpoint's own frame counter
    /// and cannot disagree with itself. A capture that misses this reports a
    /// lost stretch as an instantaneous jump in an otherwise continuous
    /// waveform -- audio that was never rendered that way.
    /// </remarks>
    public long PositionGapCount { get; private set; }

    /// <summary>
    /// Total frames known to be missing from this capture, summed across
    /// every gap. What says whether a capture is fit to draw conclusions
    /// from at all: a few milliseconds lost over a long run is noise to be
    /// stepped around, and a large figure means the recording is not a
    /// faithful account of what the endpoint rendered.
    /// </summary>
    public long MissingFrameCount { get; private set; }

    private ulong _expectedDevicePosition;

    private bool _hasDevicePosition;

    public WasapiLoopbackInput(AudioEndpointId endpointId)
    {
        if (endpointId == default)
        {
            throw new ArgumentException("A loopback endpoint is required.", nameof(endpointId));
        }

        EndpointId = endpointId;

        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        AutoResetEvent? bufferEvent = null;

        IntPtr formatPointer = IntPtr.Zero;

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

            WasapiError.ThrowIfFailed(audioClient.GetMixFormat(out formatPointer), "IAudioClient.GetMixFormat");

            _nativeFormat = WaveFormatAdapter.Read(formatPointer);

            Format = WaveFormatAdapter.ToAudioFormat(_nativeFormat);

            bufferEvent = new AutoResetEvent(false);

            WasapiError.ThrowIfFailed(
                audioClient.Initialize(
                    AudioClientShareMode.Shared,
                    AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
                    0,
                    0,
                    formatPointer,
                    IntPtr.Zero
                ),
                "IAudioClient.Initialize(loopback)"
            );

            WasapiError.ThrowIfFailed(
                audioClient.SetEventHandle(bufferEvent.SafeWaitHandle.DangerousGetHandle()),
                "IAudioClient.SetEventHandle"
            );

            WasapiError.ThrowIfFailed(audioClient.GetBufferSize(out uint bufferFrames), "IAudioClient.GetBufferSize");

            Guid captureClientId = typeof(IAudioCaptureClient).GUID;

            WasapiError.ThrowIfFailed(
                audioClient.GetService(ref captureClientId, out object captureObject),
                "IAudioClient.GetService(IAudioCaptureClient)"
            );

            captureClient = (IAudioCaptureClient)captureObject;

            _packetSamples = new float[checked((int)bufferFrames * Format.ChannelCount)];

            _device = device;
            _audioClient = audioClient;
            _captureClient = captureClient;
            _bufferEvent = bufferEvent;

            device = null;
            audioClient = null;
            captureClient = null;
            bufferEvent = null;
        }
        catch
        {
            bufferEvent?.Dispose();

            CoreAudioInterop.Release(captureClient);

            CoreAudioInterop.Release(audioClient);

            CoreAudioInterop.Release(device);

            throw;
        }
        finally
        {
            if (formatPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(formatPointer);
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

        _hasDevicePosition = false;

        WasapiError.ThrowIfFailed(_audioClient.Start(), "IAudioClient.Start(loopback)");

        _started = true;
    }

    public int Read(Span<float> destination)
    {
        ThrowIfDisposed();

        if (!_started)
        {
            throw new InvalidOperationException("Loopback capture is not started.");
        }

        if (destination.Length % Format.ChannelCount != 0)
        {
            throw new ArgumentException("Destination must contain complete frames.", nameof(destination));
        }

        if (destination.IsEmpty)
        {
            return 0;
        }

        if (_pendingSampleCount > _pendingSampleOffset)
        {
            return CopyPending(destination);
        }

        WasapiError.ThrowIfFailed(
            _captureClient.GetNextPacketSize(out uint packetFrames),
            "IAudioCaptureClient.GetNextPacketSize"
        );

        if (packetFrames == 0)
        {
            // Bounded wait is intentional. It prevents
            // an idle loopback source from making clean
            // shutdown impossible with the existing
            // synchronous Engine contract.
            if (!_bufferEvent.WaitOne(100))
            {
                return 0;
            }

            WasapiError.ThrowIfFailed(
                _captureClient.GetNextPacketSize(out packetFrames),
                "IAudioCaptureClient.GetNextPacketSize"
            );

            if (packetFrames == 0)
            {
                return 0;
            }
        }

        IntPtr data = IntPtr.Zero;
        uint acquiredFrames = 0;
        bool acquired = false;

        try
        {
            WasapiError.ThrowIfFailed(
                _captureClient.GetBuffer(
                    out data,
                    out acquiredFrames,
                    out AudioClientBufferFlags flags,
                    out ulong devicePosition,
                    out _
                ),
                "IAudioCaptureClient.GetBuffer"
            );

            if (_hasDevicePosition && devicePosition > _expectedDevicePosition)
            {
                PositionGapCount++;

                MissingFrameCount += checked((long)(devicePosition - _expectedDevicePosition));
            }

            _expectedDevicePosition = devicePosition + acquiredFrames;

            _hasDevicePosition = true;

            acquired = acquiredFrames != 0;

            int sampleCount = checked((int)acquiredFrames * Format.ChannelCount);

            if (sampleCount > _packetSamples.Length)
            {
                throw new InvalidOperationException("WASAPI returned a packet larger than its endpoint buffer.");
            }

            bool silent = (flags & AudioClientBufferFlags.Silent) != 0;

            if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
            {
                DiscontinuityCount++;
            }

            WaveFormatAdapter.NativeToFloat(data, _packetSamples, checked((int)acquiredFrames), _nativeFormat, silent);

            _pendingSampleOffset = 0;
            _pendingSampleCount = sampleCount;
        }
        finally
        {
            if (acquired)
            {
                WasapiError.ThrowIfFailed(
                    _captureClient.ReleaseBuffer(acquiredFrames),
                    "IAudioCaptureClient.ReleaseBuffer"
                );
            }
        }

        return CopyPending(destination);
    }

    public void Stop()
    {
        ThrowIfDisposed();

        if (!_started)
        {
            return;
        }

        WasapiError.ThrowIfFailed(_audioClient.Stop(), "IAudioClient.Stop(loopback)");

        _started = false;

        _pendingSampleOffset = 0;
        _pendingSampleCount = 0;
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

            _pendingSampleOffset = 0;

            _pendingSampleCount = 0;

            _bufferEvent.Dispose();

            CoreAudioInterop.Release(_captureClient);

            CoreAudioInterop.Release(_audioClient);

            CoreAudioInterop.Release(_device);

            _disposed = true;
        }

        if (stopFailure is not null)
        {
            throw new InvalidOperationException("WASAPI loopback input failed to stop during disposal.", stopFailure);
        }
    }

    private int CopyPending(Span<float> destination)
    {
        int availableSamples = _pendingSampleCount - _pendingSampleOffset;

        int sampleCount = Math.Min(destination.Length, availableSamples);

        _packetSamples.AsSpan(_pendingSampleOffset, sampleCount).CopyTo(destination);

        _pendingSampleOffset += sampleCount;

        if (_pendingSampleOffset == _pendingSampleCount)
        {
            _pendingSampleOffset = 0;
            _pendingSampleCount = 0;
        }

        return sampleCount / Format.ChannelCount;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

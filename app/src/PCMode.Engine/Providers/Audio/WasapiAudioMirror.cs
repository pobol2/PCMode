using System.Globalization;
using System.Runtime.InteropServices;
using static PCMode.Engine.Wasapi;

namespace PCMode.Engine;

/// <summary>The real mirror: two endpoints, two threads, shared mode.</summary>
internal sealed class WasapiAudioMirrorFactory : IAudioMirrorFactory
{
    public IAudioMirror Start(AudioMirrorRequest request) => WasapiAudioMirror.Start(request);
}

/// <summary>
/// Loopback capture from the reference endpoint, delayed and rendered to the
/// mirror endpoint.
/// </summary>
/// <remarks>
/// THE SHAPE OF IT
///
/// One thread captures. Windows is already mixing everything the machine plays
/// into the reference endpoint, and shared-mode loopback hands that mix back as
/// it goes out. Each packet is converted to float, mapped onto the mirror's
/// channel count, and written into a ring buffer.
///
/// A second thread renders. It reads the ring at a position deliberately held
/// behind the write position -- that distance IS the delay -- resamples to the
/// mirror endpoint's rate, applies gain, and writes to its buffer whenever
/// Windows asks for more.
///
/// TWO CLOCKS, WHICH IS THE WHOLE DIFFICULTY
///
/// The endpoints run off separate crystals. "48 kHz" on a soundbar and "48 kHz"
/// on a sound card differ by tens of parts per million, so a mirror that
/// resampled at exactly the nominal ratio would drift by tens of milliseconds an
/// hour and turn into an echo. The render side therefore measures how much audio
/// is buffered ahead of it and nudges its resampling ratio -- by hundredths of a
/// percent, far below anything audible as pitch -- to hold that distance where
/// it was put. A one-hour session ends where it started.
///
/// Correction has bounds. If the buffered distance leaves them at all -- a
/// stalled capture, a machine that slept, a driver that reset -- the read
/// position is moved outright and the gain is faded back in, because a splice
/// under a fade is a soft blip and a splice at full level is a click.
///
/// FAILURE IS LOUD AND FINAL
///
/// Any HRESULT from either endpoint stops both halves and is kept. There is no
/// reconnection and no second choice of device: an endpoint that went away means
/// the room is no longer what the user configured, and quietly carrying on with
/// one speaker is exactly the "where is the sound coming from" failure Party
/// mode is supposed to make impossible.
/// </remarks>
internal sealed unsafe class WasapiAudioMirror : IAudioMirror
{
    /// <summary>100 ns units, the unit every WASAPI duration and period is in.</summary>
    private const long TicksPerMs = 10_000;

    /// <summary>
    /// The least the cushion is ever allowed to be.
    /// </summary>
    /// <remarks>
    /// Even a driver reporting an implausibly small period is given this much,
    /// because the render thread genuinely cannot produce a sample it has not
    /// been handed yet, and a cushion of zero would underrun on the first
    /// scheduling jitter of any kind.
    /// </remarks>
    private static readonly TimeSpan MinCushion = TimeSpan.FromMilliseconds(3);

    /// <summary>
    /// The most the cushion is ever allowed to be.
    /// </summary>
    /// <remarks>
    /// A sanity clamp against a driver reporting a period far larger than any
    /// real shared-mode endpoint uses, not a design target -- ordinary periods
    /// measure in single-digit milliseconds and land well under this.
    /// </remarks>
    private static readonly TimeSpan MaxCushion = TimeSpan.FromMilliseconds(40);

    /// <summary>How far the buffered distance may wander before it is spliced.</summary>
    private static readonly TimeSpan MaxDrift = TimeSpan.FromMilliseconds(150);

    /// <summary>How long a gain change, a start or a splice takes.</summary>
    private static readonly TimeSpan Ramp = TimeSpan.FromMilliseconds(20);

    private readonly ManualResetEventSlim _ready = new(false);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _adjusting = new();

    private readonly AutoResetEvent _captureReady = new(false);
    private readonly AutoResetEvent _renderReady = new(false);

    private Exception? _startFailure;

    // Set once during activation, read by both loops and by Health afterwards.
    private WaveFormat _referenceFormat = new(0, 0, 0, false, 0);
    private WaveFormat _mirrorFormat = new(0, 0, 0, false, 0);
    private bool _eventDriven = true;

    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _referenceDevice;
    private IMMDevice? _mirrorDevice;
    private IAudioClient? _captureClient;
    private IAudioClient? _renderClient;
    private IAudioCaptureClient? _capture;
    private IAudioRenderClient? _render;
    private int _renderBufferFrames;

    private AudioMirrorBuffer _referenceBuffer = new(0, 0, 0, false);
    private AudioMirrorBuffer _mirrorBuffer = new(0, 0, 0, false);
    private double _cushionMs = MaxCushion.TotalMilliseconds;

    private Thread? _worker;
    private Thread? _renderThread;
    private int _disposed;

    // ---- the ring, written by capture and read by render ---------------------

    private float[] _ring = [];
    private int _ringFrames;
    private int _channels;
    private long _written;
    private double _readPos;
    private bool _primed;
    private bool _reseek;

    /// <summary>
    /// The first captured frame the render side is allowed to treat as current.
    /// </summary>
    /// <remarks>
    /// Loopback capture stops delivering entirely while the machine is playing
    /// nothing -- the endpoint's audio engine goes idle, and there is no silence
    /// to hand back. The delay line therefore still holds whatever was playing
    /// when the room went quiet, and re-aligning against it after the gap would
    /// replay that. This is where the audio starts being new again.
    /// </remarks>
    private long _primeFrom;

    /// <summary>What had been captured at the previous render period.</summary>
    private long _lastWritten;

    private double _nominalRatio = 1;
    private float _gain;

    // ---- what Health reports -------------------------------------------------

    private int _delayFrames;
    private double _targetGain = 1;
    private int _targetDelayMs;
    private volatile bool _capturing;
    private volatile bool _rendering;
    private volatile string? _lastFailure;
    private long _underruns;
    private long _driftCorrections;
    private long _resyncs;
    private long _framesMirrored;
    private double _offsetMs;
    private volatile bool _fadeOut;

    private WasapiAudioMirror(AudioMirrorRequest request)
    {
        Request = request;
        _targetGain = request.Gain;
        _targetDelayMs = (int)Math.Round(request.Delay.TotalMilliseconds);
    }

    public AudioMirrorRequest Request { get; }

    public AudioMirrorHealth Health
    {
        get
        {
            int delayMs = Volatile.Read(ref _targetDelayMs);

            // Computed fresh on every read rather than cached, so a health
            // report always reflects the delay actually in effect right now --
            // including the moment right after a live Adjust, before the render
            // thread has picked the new value up.
            double baseline = _cushionMs + _referenceBuffer.ActualMs + _mirrorBuffer.ActualMs;

            return new(
                _capturing,
                _rendering,
                _referenceFormat.ToString(),
                _mirrorFormat.ToString(),
                delayMs,
                Volatile.Read(ref _targetGain),
                Math.Round(Volatile.Read(ref _offsetMs), 2),
                Interlocked.Read(ref _driftCorrections),
                Interlocked.Read(ref _resyncs),
                Interlocked.Read(ref _underruns),
                Interlocked.Read(ref _framesMirrored),
                _eventDriven,
                _referenceBuffer,
                _mirrorBuffer,
                Math.Round(_cushionMs, 2),
                Math.Round(baseline, 2),
                Math.Round(baseline + delayMs, 2),
                _lastFailure);
        }
    }

    /// <summary>
    /// Opens both endpoints and starts streaming, or throws having released
    /// everything it opened.
    /// </summary>
    public static IAudioMirror Start(AudioMirrorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mirror = new WasapiAudioMirror(request);

        // A thread of its own, explicitly MTA, because everything below is COM
        // and the caller is whatever thread a transition happens to be on. A
        // stream activated on an STA thread would be marshalled on every call
        // from the loops that use it, at audio rates.
        var worker = new Thread(mirror.Run)
        {
            IsBackground = true,
            Name = "PCMode Party capture",
        };

        worker.SetApartmentState(ApartmentState.MTA);
        mirror._worker = worker;
        worker.Start();

        if (!mirror._ready.Wait(TimeSpan.FromSeconds(10)))
        {
            mirror.Dispose();

            throw new InvalidOperationException(
                $"Party mode could not start within ten seconds. '{request.ReferenceName}' or " +
                $"'{request.MirrorName}' did not respond.");
        }

        if (mirror._startFailure is Exception failure)
        {
            mirror.Dispose();
            throw failure;
        }

        return mirror;
    }

    public void Adjust(TimeSpan delay, double gain)
    {
        lock (_adjusting)
        {
            int wanted = (int)Math.Round(Math.Clamp(delay.TotalMilliseconds, 0, PartySettings.MaxDelayMs));

            Volatile.Write(ref _targetGain, Math.Clamp(gain, 0, PartySettings.MaxGain));

            if (wanted == Volatile.Read(ref _targetDelayMs))
            {
                return;
            }

            Volatile.Write(ref _targetDelayMs, wanted);
            Volatile.Write(ref _delayFrames, (int)((long)wanted * _referenceFormat.SampleRate / 1000));

            // Taken at the next render period, under a fade. Sliding to a new
            // alignment by resampling would take tens of seconds at a rate slow
            // enough not to shift pitch, and somebody aligning two speakers by
            // ear needs to hear the change they just made.
            Volatile.Write(ref _reseek, true);
        }
    }

    public void Dispose()
    {
        // Guards against a double dispose, not against the run loop having
        // already cancelled itself: Run()'s finally cancels _stopping on any
        // capture/render failure too, and that must not make this return
        // early -- it would skip disposing the wait handles below on every
        // endpoint-dropout, which is exactly when the host is about to call
        // this.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!_stopping.IsCancellationRequested)
        {
            // Asked to fade first. The stream is torn down either way a moment
            // later; this is the difference between the music ending and a click.
            _fadeOut = true;

            SpinWait.SpinUntil(() => Volatile.Read(ref _gain) <= 0.0001f || !_rendering, TimeSpan.FromMilliseconds(80));

            _stopping.Cancel();
        }

        _captureReady.Set();
        _renderReady.Set();

        // Joined, not abandoned. Both threads hold COM references to endpoints
        // that the next Party start is about to open again, and a capture stream
        // still running on the reference endpoint is precisely the duplicate the
        // host exists to prevent.
        if (_worker is null || _worker.Join(TimeSpan.FromSeconds(3)))
        {
            // Only once nothing can still be signalling them. A thread that
            // outlived its join is a thread that would fault on a disposed
            // handle, which turns a shutdown problem into a crash.
            _ready.Dispose();
            _stopping.Dispose();
            _captureReady.Dispose();
            _renderReady.Dispose();
        }
    }

    // ---- activation ---------------------------------------------------------

    private void Run()
    {
        try
        {
            Activate();
        }
        catch (Exception exception)
        {
            _startFailure = exception;
            Release();
            _ready.Set();
            return;
        }

        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "PCMode Party render",
            Priority = ThreadPriority.Highest,
        };

        _renderThread.SetApartmentState(ApartmentState.MTA);
        _renderThread.Start();

        _ready.Set();

        try
        {
            CaptureLoop();
        }
        finally
        {
            _capturing = false;
            _stopping.Cancel();
            _renderReady.Set();
            _renderThread.Join(TimeSpan.FromSeconds(3));
            Release();
        }
    }

    private void Activate()
    {
        _enumerator = CreateEnumerator();

        _referenceDevice = OpenDevice(_enumerator, Request.ReferenceId, $"'{Request.ReferenceName}'");
        _mirrorDevice = OpenDevice(_enumerator, Request.MirrorId, $"'{Request.MirrorName}'");

        _captureClient = ActivateClient(_referenceDevice, $"'{Request.ReferenceName}'");
        _renderClient = ActivateClient(_mirrorDevice, $"'{Request.MirrorName}'");

        (_referenceFormat, _referenceBuffer) = InitializeCapture(_captureClient, $"'{Request.ReferenceName}'");
        (_mirrorFormat, _mirrorBuffer) = InitializeRender(_renderClient, $"'{Request.MirrorName}'");

        // Sized from what the two endpoints actually granted, not from a fixed
        // guess: the render thread's real risk of running dry is set by how big
        // its own buffer is and by how bursty the reference's capture delivery
        // is, and both of those are now known.
        _cushionMs = Math.Clamp(
            (_referenceBuffer.DevicePeriodMs + _mirrorBuffer.ActualMs) * 1.5,
            MinCushion.TotalMilliseconds,
            MaxCushion.TotalMilliseconds);

        Guid captureIid = AudioCaptureClientIid;
        Check(_captureClient.GetService(ref captureIid, out object? captureService), $"'{Request.ReferenceName}'");
        _capture = captureService as IAudioCaptureClient
            ?? throw new InvalidOperationException($"'{Request.ReferenceName}' did not provide a capture stream.");

        Guid renderIid = AudioRenderClientIid;
        Check(_renderClient.GetService(ref renderIid, out object? renderService), $"'{Request.MirrorName}'");
        _render = renderService as IAudioRenderClient
            ?? throw new InvalidOperationException($"'{Request.MirrorName}' did not provide a render stream.");

        Check(_renderClient.GetBufferSize(out _renderBufferFrames), $"'{Request.MirrorName}'");

        BuildRing();

        Check(_captureClient.Start(), $"'{Request.ReferenceName}'");
        _capturing = true;

        Check(_renderClient.Start(), $"'{Request.MirrorName}'");
        _rendering = true;
    }

    /// <summary>
    /// Opens loopback capture at the lowest stable period the endpoint offers,
    /// event-driven where it allows one.
    /// </summary>
    /// <remarks>
    /// Capture tries two things in order, falling back only when the first fails:
    ///
    /// 1. Ordinary <c>IAudioClient.Initialize</c>, event-driven, requested at the
    ///    endpoint's own default period rather than an arbitrary fixed one.
    /// 2. The same, polled, for the endpoint that will not take an event handle
    ///    at all. Which one is in use is reported, because it is the first thing
    ///    worth knowing about an endpoint that stutters.
    ///
    /// An initialized client cannot be re-initialized, so each fallback opens a
    /// fresh one; the failed attempt's client is released before trying again.
    /// </remarks>
    private (WaveFormat Format, AudioMirrorBuffer Buffer) InitializeCapture(IAudioClient client, string what)
    {
        const int flags = StreamFlagsLoopback | StreamFlagsEventCallback | StreamFlagsNoPersist;

        Check(client.GetMixFormat(out nint format), what);

        try
        {
            WaveFormat read = WaveFormat.Read(format, what);

            // Attempt 1: ordinary IAudioClient, event-driven, at the endpoint's
            // own default period rather than an arbitrary fixed one.
            long requestedDefault = DefaultPeriod(client, what);

            if (client.Initialize(ShareModeShared, flags, requestedDefault, 0, format, nint.Zero) == 0)
            {
                _captureClient = client;
                Check(client.SetEventHandle(_captureReady.SafeWaitHandle.DangerousGetHandle()), what);
                _eventDriven = true;
                return (read, ReadOpenedBuffer(client, what, read.SampleRate, requestedDefault, lowLatency: false));
            }

            // Attempt 2: the endpoint will not take an event handle at all.
            // Polled, at half the device period.
            client = Reactivate(_referenceDevice!, what, ref _captureClient);
            _eventDriven = false;

            long requestedPolled = DefaultPeriod(client, what);

            Check(
                client.Initialize(ShareModeShared, StreamFlagsLoopback | StreamFlagsNoPersist, requestedPolled, 0, format, nint.Zero),
                what);

            return (read, ReadOpenedBuffer(client, what, read.SampleRate, requestedPolled, lowLatency: false));
        }
        finally
        {
            CoTaskMemFree(format);
        }
    }

    private (WaveFormat Format, AudioMirrorBuffer Buffer) InitializeRender(IAudioClient client, string what)
    {
        const int flags = StreamFlagsEventCallback | StreamFlagsNoPersist;

        Check(client.GetMixFormat(out nint format), what);

        try
        {
            WaveFormat read = WaveFormat.Read(format, what);

            if (CanUseLowLatencySharedStream(flags) &&
                client is IAudioClient3 engine &&
                engine.GetSharedModeEnginePeriod(format, out _, out _, out int minFrames, out _) == 0 &&
                minFrames > 0 &&
                engine.InitializeSharedAudioStream(
                    LowLatencySharedStreamFlags(flags),
                    minFrames,
                    format,
                    nint.Zero) == 0)
            {
                _renderClient = client = (IAudioClient)engine;
                Check(client.SetEventHandle(_renderReady.SafeWaitHandle.DangerousGetHandle()), what);

                double requestedMs = minFrames * 1000.0 / read.SampleRate;
                return (read, ReadOpenedBuffer(client, what, read.SampleRate, (long)(requestedMs * TicksPerMs), lowLatency: true));
            }

            client = Reactivate(_mirrorDevice!, what, ref _renderClient);

            long requestedDefault = DefaultPeriod(client, what);

            // Render always takes an event handle in shared mode; unlike
            // loopback capture there is no documented reason a render endpoint
            // would refuse one, so a failure here is real rather than a case to
            // fall back further from.
            Check(client.Initialize(ShareModeShared, flags, requestedDefault, 0, format, nint.Zero), what);
            _renderClient = client;
            Check(client.SetEventHandle(_renderReady.SafeWaitHandle.DangerousGetHandle()), what);

            return (read, ReadOpenedBuffer(client, what, read.SampleRate, requestedDefault, lowLatency: false));
        }
        finally
        {
            CoTaskMemFree(format);
        }
    }

    /// <summary>Discards a client that has failed to initialize and activates a fresh one.</summary>
    /// <remarks>
    /// An <c>IAudioClient</c> that has been offered to <c>Initialize</c> or
    /// <c>InitializeSharedAudioStream</c> and refused is documented as unusable
    /// afterwards, successful attempt or not -- the next thing tried needs an
    /// object that has never been offered anything.
    /// </remarks>
    private static IAudioClient Reactivate(IMMDevice device, string what, ref IAudioClient? field)
    {
        if (field is not null)
        {
            try
            {
                Marshal.ReleaseComObject(field);
            }
            catch (Exception)
            {
            }
        }

        IAudioClient client = ActivateClient(device, what);
        field = client;
        return client;
    }

    /// <summary>
    /// The endpoint's own default servicing period -- the lowest duration
    /// <c>IAudioClient.Initialize</c> itself is documented as reliable at,
    /// unlike the fixed value this mirror used to request regardless of what
    /// any particular endpoint actually needed.
    /// </summary>
    private static long DefaultPeriod(IAudioClient client, string what)
    {
        // Falls back to 10 ms -- comfortably inside what any shared-mode
        // endpoint has been observed to need -- only if the endpoint will not
        // even answer GetDevicePeriod, which would itself be unusual enough to
        // show up in the health report as an implausibly small actual buffer.
        return client.GetDevicePeriod(out long defaultPeriod, out _) == 0 && defaultPeriod > 0
            ? defaultPeriod
            : 10 * TicksPerMs;
    }

    /// <summary>
    /// Reads back what an already-initialized client actually got: its buffer
    /// as a duration, and the endpoint's own servicing period.
    /// </summary>
    /// <remarks>
    /// The buffer size is what matters for latency and is deliberately what is
    /// reported as <see cref="AudioMirrorBuffer.ActualMs"/> -- not
    /// <c>GetStreamLatency</c>, which folds in driver-internal delay that
    /// varies by hardware in ways this mirror has no way to act on and no
    /// business guessing at.
    /// </remarks>
    private static AudioMirrorBuffer ReadOpenedBuffer(IAudioClient client, string what, double sampleRate, long requestedTicks, bool lowLatency)
    {
        Check(client.GetBufferSize(out int frames), what);

        double actualMs = frames * 1000.0 / sampleRate;
        double periodMs = client.GetDevicePeriod(out long defaultPeriod, out _) == 0
            ? defaultPeriod / (double)TicksPerMs
            : actualMs;

        return new AudioMirrorBuffer(requestedTicks / (double)TicksPerMs, actualMs, periodMs, lowLatency);
    }

    /// <summary>
    /// Sizes the delay line: the longest delay allowed, plus the cushion, plus
    /// room for the drift bound.
    /// </summary>
    private void BuildRing()
    {
        _channels = _mirrorFormat.Channels;

        int rate = _referenceFormat.SampleRate;

        _ringFrames = (int)((PartySettings.MaxDelayMs + _cushionMs + (MaxDrift.TotalMilliseconds * 2) + 500)
            * rate / 1000);

        _ring = new float[(long)_ringFrames * _channels];

        _nominalRatio = (double)rate / _mirrorFormat.SampleRate;
        Volatile.Write(ref _delayFrames, (int)((long)Volatile.Read(ref _targetDelayMs) * rate / 1000));
    }

    // ---- capture ------------------------------------------------------------

    private void CaptureLoop()
    {
        int index = 0;
        nint task = AvSetMmThreadCharacteristics("Pro Audio", ref index);

        // Half the buffer, so a missed wake-up is noticed long before anything
        // is lost. In event-driven mode this timeout is only ever reached when
        // the machine is silent, which Windows does not always raise an event
        // for.
        var wait = TimeSpan.FromMilliseconds(10);

        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                if (_eventDriven)
                {
                    _captureReady.WaitOne(wait);
                }
                else
                {
                    Thread.Sleep(5);
                }

                if (_stopping.IsCancellationRequested)
                {
                    return;
                }

                if (!Drain())
                {
                    return;
                }
            }
        }
        finally
        {
            if (task != nint.Zero)
            {
                AvRevertMmThreadCharacteristics(task);
            }
        }
    }

    /// <summary>Takes everything the endpoint has, and says whether it may continue.</summary>
    private bool Drain()
    {
        IAudioCaptureClient capture = _capture!;

        while (true)
        {
            int hr = capture.GetNextPacketSize(out int available);

            if (hr != 0)
            {
                return Fail(hr, Request.ReferenceName);
            }

            if (available == 0)
            {
                return true;
            }

            hr = capture.GetBuffer(out nint data, out int frames, out int flags, out _, out _);

            if (hr != 0)
            {
                return Fail(hr, Request.ReferenceName);
            }

            try
            {
                if (frames > 0)
                {
                    Write(data, frames, (flags & BufferFlagsSilent) != 0);
                }
            }
            finally
            {
                hr = capture.ReleaseBuffer(frames);
            }

            if (hr != 0)
            {
                return Fail(hr, Request.ReferenceName);
            }
        }
    }

    /// <summary>Converts one captured packet into the ring.</summary>
    private void Write(nint data, int frames, bool silent)
    {
        int sourceChannels = _referenceFormat.Channels;
        int channels = _channels;
        long written = Volatile.Read(ref _written);

        float[] ring = _ring;
        byte* source = (byte*)data;

        int bytes = _referenceFormat.BytesPerSample;
        int stride = _referenceFormat.BlockAlign > 0 ? _referenceFormat.BlockAlign : bytes * sourceChannels;

        for (int frame = 0; frame < frames; frame++)
        {
            int slot = (int)((written + frame) % _ringFrames) * channels;

            for (int channel = 0; channel < channels; channel++)
            {
                if (silent)
                {
                    ring[slot + channel] = 0;
                    continue;
                }

                // Fewer captured channels than the mirror has: the extra ones go
                // out silent, except that a mono reference is sent to both sides
                // rather than to the left speaker only.
                int from = channel < sourceChannels
                    ? channel
                    : sourceChannels == 1 ? 0 : -1;

                ring[slot + channel] = from < 0
                    ? 0
                    : ReadSample(source + (frame * stride) + (from * bytes));
            }
        }

        // Published last. The render thread reads this to decide what it is
        // allowed to touch, so it must not see frames it has not been given.
        Volatile.Write(ref _written, written + frames);
    }

    private float ReadSample(byte* at)
    {
        if (_referenceFormat.IsFloat)
        {
            return *(float*)at;
        }

        return _referenceFormat.Bits switch
        {
            16 => *(short*)at / 32768f,
            24 => ((at[0] | (at[1] << 8) | ((sbyte)at[2] << 16)) / 8388608f),
            _ => *(int*)at / 2147483648f,
        };
    }

    // ---- render -------------------------------------------------------------

    private void RenderLoop()
    {
        int index = 0;
        nint task = AvSetMmThreadCharacteristics("Pro Audio", ref index);

        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                _renderReady.WaitOne(TimeSpan.FromMilliseconds(200));

                if (_stopping.IsCancellationRequested)
                {
                    return;
                }

                int hr = _renderClient!.GetCurrentPadding(out int padding);

                if (hr != 0)
                {
                    Fail(hr, Request.MirrorName);
                    return;
                }

                int frames = _renderBufferFrames - padding;

                if (frames <= 0)
                {
                    continue;
                }

                hr = _render!.GetBuffer(frames, out nint buffer);

                if (hr != 0)
                {
                    Fail(hr, Request.MirrorName);
                    return;
                }

                bool audible = Fill(buffer, frames);

                hr = _render.ReleaseBuffer(frames, audible ? 0 : BufferFlagsSilent);

                if (hr != 0)
                {
                    Fail(hr, Request.MirrorName);
                    return;
                }
            }
        }
        finally
        {
            _rendering = false;

            if (task != nint.Zero)
            {
                AvRevertMmThreadCharacteristics(task);
            }
        }
    }

    /// <summary>
    /// Fills one render buffer, and says whether anything was actually put in it.
    /// </summary>
    private bool Fill(nint buffer, int frames)
    {
        int channels = _channels;
        int bytes = _mirrorFormat.BytesPerSample;
        int stride = _mirrorFormat.BlockAlign > 0 ? _mirrorFormat.BlockAlign : bytes * channels;
        byte* destination = (byte*)buffer;

        long written = Volatile.Read(ref _written);
        double target = TargetLag();

        if (Volatile.Read(ref _reseek))
        {
            Volatile.Write(ref _reseek, false);
            _primed = false;
        }

        if (!_primed)
        {
            // Only from audio captured since the gap. Anything older is what was
            // playing before it, and the delay line has no idea it is stale.
            if (written - _primeFrom < target + 2)
            {
                Silence(destination, frames * stride);
                return false;
            }

            _readPos = written - target;
            _primed = true;

            // From nothing, every time. A stream that resumed at whatever gain it
            // had would announce itself at full volume through a soundbar.
            Volatile.Write(ref _gain, 0);
        }

        double lag = written - _readPos;
        double maxDrift = MaxDrift.TotalSeconds * _referenceFormat.SampleRate;

        if (Math.Abs(lag - target) > maxDrift)
        {
            _readPos = written - target;
            lag = target;
            Interlocked.Increment(ref _resyncs);
            Volatile.Write(ref _gain, 0);
        }

        Volatile.Write(ref _offsetMs, (lag - target) * 1000 / _referenceFormat.SampleRate);

        // Proportional, and deliberately feeble. The correction only has to
        // outrun the difference between two crystals, which is tens of parts per
        // million; anything strong enough to be heard as pitch would be a
        // hundred times more than the job needs.
        double correction = Math.Clamp((lag - target) / Math.Max(target, 1) * 0.02, -0.0005, 0.0005);

        if (Math.Abs(correction) > 0.00005)
        {
            Interlocked.Increment(ref _driftCorrections);
        }

        double ratio = _nominalRatio * (1 + correction);
        double position = _readPos;

        float gain = Volatile.Read(ref _gain);
        float wanted = _fadeOut ? 0f : (float)Volatile.Read(ref _targetGain);
        float step = (float)(1.0 / Math.Max(1, Ramp.TotalSeconds * _mirrorFormat.SampleRate));

        float[] ring = _ring;
        int produced = 0;
        bool audible = false;

        for (int frame = 0; frame < frames; frame++)
        {
            if (position + 1 >= written)
            {
                Silence(destination + (frame * stride), (frames - frame) * stride);

                // Counted only when the capture side is still delivering and
                // simply did not deliver enough. A reference endpoint that has
                // handed over nothing at all since the last period is not
                // starving this thread: it is a machine playing nothing, and
                // counting that as a fault would fill the report with one
                // underrun per render period for every quiet passage.
                if (written != _lastWritten)
                {
                    Interlocked.Increment(ref _underruns);
                }

                // Re-aligned against fresh audio when it resumes, which is what
                // makes the mirror come back in step after a gap rather than a
                // delay's worth of stale sound behind.
                _primed = false;
                _primeFrom = written;
                break;
            }

            if (gain < wanted)
            {
                gain = Math.Min(wanted, gain + step);
            }
            else if (gain > wanted)
            {
                gain = Math.Max(wanted, gain - step);
            }

            long index = (long)position;
            float fraction = (float)(position - index);

            int first = (int)(index % _ringFrames) * channels;
            int second = (int)((index + 1) % _ringFrames) * channels;

            byte* frameStart = destination + (frame * stride);

            for (int channel = 0; channel < channels; channel++)
            {
                float sample = ((ring[first + channel] * (1 - fraction)) + (ring[second + channel] * fraction)) * gain;

                // Hard-limited rather than allowed to wrap. A mix already near
                // full scale plus any gain above unity would otherwise come out
                // of a soundbar as a burst of noise.
                sample = Math.Clamp(sample, -1f, 1f);

                WriteSample(frameStart + (channel * bytes), sample);

                if (sample != 0)
                {
                    audible = true;
                }
            }

            position += ratio;
            produced++;
        }

        _readPos = position;
        _lastWritten = written;
        Volatile.Write(ref _gain, gain);
        Interlocked.Add(ref _framesMirrored, produced);

        return audible;
    }

    private void WriteSample(byte* at, float sample)
    {
        if (_mirrorFormat.IsFloat)
        {
            *(float*)at = sample;
            return;
        }

        switch (_mirrorFormat.Bits)
        {
            case 16:
                *(short*)at = (short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue);
                break;

            case 24:
                int value = (int)Math.Clamp(sample * 8388607f, -8388608f, 8388607f);
                at[0] = (byte)value;
                at[1] = (byte)(value >> 8);
                at[2] = (byte)(value >> 16);
                break;

            default:
                *(int*)at = (int)Math.Clamp((double)sample * 2147483647.0, int.MinValue, int.MaxValue);
                break;
        }
    }

    private static void Silence(byte* at, int bytes) => new Span<byte>(at, bytes).Clear();

    /// <summary>How far behind the capture the render should be, in capture frames.</summary>
    private double TargetLag() =>
        Volatile.Read(ref _delayFrames) + (_cushionMs / 1000.0 * _referenceFormat.SampleRate);

    // ---- failure and cleanup ------------------------------------------------

    private bool Fail(int hr, string endpoint)
    {
        _lastFailure ??= Explain(hr, $"'{endpoint}'");
        _stopping.Cancel();
        _renderReady.Set();
        return false;
    }

    /// <summary>Stops both clients and releases every COM reference held.</summary>
    /// <remarks>
    /// On the worker thread, always, and after the render thread has been joined.
    /// A COM object released while another thread is calling it is the one
    /// failure here that would not produce a message.
    /// </remarks>
    private void Release()
    {
        Stop(_captureClient);
        Stop(_renderClient);

        _capturing = false;
        _rendering = false;

        ReleaseCom(ref _capture);
        ReleaseCom(ref _render);
        ReleaseCom(ref _captureClient);
        ReleaseCom(ref _renderClient);
        ReleaseCom(ref _referenceDevice);
        ReleaseCom(ref _mirrorDevice);
        ReleaseCom(ref _enumerator);

        _ring = [];

        static void Stop(IAudioClient? client)
        {
            try
            {
                _ = client?.Stop();
            }
            catch (Exception)
            {
                // Already gone. There is nothing left to stop and nothing useful
                // to say about it.
            }
        }

        static void ReleaseCom<T>(ref T? reference)
            where T : class
        {
            if (reference is not null)
            {
                try
                {
                    Marshal.ReleaseComObject(reference);
                }
                catch (Exception)
                {
                }

                reference = null;
            }
        }
    }

    /// <summary>
    /// Asks Windows to schedule this thread as audio.
    /// </summary>
    /// <remarks>
    /// Documented, and the supported way to say that a thread missing its
    /// deadline is audible. Failure is ignored on purpose: without it the loops
    /// still work and merely become more sensitive to a busy machine.
    /// </remarks>
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint AvSetMmThreadCharacteristics(string task, ref int taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(nint handle);
}

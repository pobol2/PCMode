using System.Runtime.InteropServices;
using PCMode.Audio.Core.Interop;
using static PCMode.Engine.Wasapi;

namespace PCMode.Engine;

/// <summary>
/// A recognisable noise, played through one endpoint until it is stopped.
/// </summary>
/// <remarks>
/// WHAT IT IS FOR
///
/// Party mode can only be proved by ear, and proving it needs a sound whose
/// alignment and level are obvious from across a room. Music is not that sound:
/// two speakers a few milliseconds apart sound like a slightly wide mix, and a
/// listener cannot tell which of them is late. A repeating click does exactly
/// that job -- a pair of clicks is unmistakably two, a single click is
/// unmistakably aligned -- and two tones a fifth apart make it obvious that both
/// outputs are carrying the same programme rather than one of them buzzing.
///
/// It is not started by anything a mode does. The hardware check drives it, and
/// a calibration screen later would drive the same thing rather than inventing
/// its own.
/// </remarks>
internal sealed unsafe class WasapiTestSignal : IDisposable
{
    /// <summary>The pattern repeats every two seconds.</summary>
    private static readonly (double Start, double End, double Frequency)[] Pattern =
    [
        (0.00, 0.04, 1500), // click
        (0.50, 0.54, 1500), // click
        (1.00, 1.35, 440), // A
        (1.45, 1.80, 660), // and a fifth above it
    ];

    /// <summary>About -18 dBFS. Clearly audible, and not startling.</summary>
    private const float Level = 0.125f;

    private readonly string _endpointId;
    private readonly string _name;
    private readonly AutoResetEvent _ready = new(false);
    private readonly ManualResetEventSlim _started = new(false);
    private readonly CancellationTokenSource _stopping = new();

    private Exception? _failure;
    private Thread? _thread;
    private int _disposed;

    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioClient? _client;
    private IAudioRenderClient? _render;
    private WaveFormat _format = new(0, 0, 0, false, 0);
    private int _bufferFrames;
    private long _position;

    private WasapiTestSignal(string endpointId, string name)
    {
        _endpointId = endpointId;
        _name = name;
    }

    /// <summary>What the endpoint is playing it at.</summary>
    public string Format => _format.ToString();

    public static WasapiTestSignal Start(string endpointId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        var signal = new WasapiTestSignal(endpointId, name);

        var thread = new Thread(signal.Run)
        {
            IsBackground = true,
            Name = "PCMode test signal",
            Priority = ThreadPriority.AboveNormal,
        };

        thread.SetApartmentState(ApartmentState.MTA);
        signal._thread = thread;
        thread.Start();

        if (!signal._started.Wait(TimeSpan.FromSeconds(10)))
        {
            signal.Dispose();
            throw new InvalidOperationException($"The test signal did not start on '{name}' within ten seconds.");
        }

        if (signal._failure is Exception failure)
        {
            signal.Dispose();
            throw failure;
        }

        return signal;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopping.Cancel();
        _ready.Set();
        _thread?.Join(TimeSpan.FromSeconds(3));

        _ready.Dispose();
        _started.Dispose();
        _stopping.Dispose();
    }

    private void Run()
    {
        try
        {
            _enumerator = CreateEnumerator();
            _device = OpenDevice(_enumerator, _endpointId, $"'{_name}'");
            _client = ActivateClient(_device, $"'{_name}'");

            Check(_client.GetMixFormat(out nint format), $"'{_name}'");

            try
            {
                _format = WaveFormat.Read(format, $"'{_name}'");

                Check(
                    _client.Initialize(
                        ShareModeShared,
                        StreamFlagsEventCallback | StreamFlagsNoPersist,
                        100 * 10_000,
                        0,
                        format,
                        nint.Zero
                    ),
                    $"'{_name}'"
                );
            }
            finally
            {
                CoTaskMemFree(format);
            }

            Check(_client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle()), $"'{_name}'");

            Guid iid = AudioRenderClientIid;
            Check(_client.GetService(ref iid, out object? service), $"'{_name}'");
            _render =
                service as IAudioRenderClient
                ?? throw new InvalidOperationException($"'{_name}' did not provide a render stream.");

            Check(_client.GetBufferSize(out _bufferFrames), $"'{_name}'");
            Check(_client.Start(), $"'{_name}'");
        }
        catch (Exception exception)
        {
            _failure = exception;
            Release();
            _started.Set();
            return;
        }

        _started.Set();

        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                _ready.WaitOne(TimeSpan.FromMilliseconds(200));

                if (_stopping.IsCancellationRequested || _client!.GetCurrentPadding(out int padding) != 0)
                {
                    return;
                }

                int frames = _bufferFrames - padding;

                if (frames <= 0 || _render!.GetBuffer(frames, out nint buffer) != 0)
                {
                    continue;
                }

                Generate(buffer, frames);

                if (_render.ReleaseBuffer(frames, 0) != 0)
                {
                    return;
                }
            }
        }
        finally
        {
            Release();
        }
    }

    private void Generate(nint buffer, int frames)
    {
        byte* destination = (byte*)buffer;
        int bytes = _format.BytesPerSample;
        int stride = _format.BlockAlign > 0 ? _format.BlockAlign : bytes * _format.Channels;
        int rate = _format.SampleRate;
        long cycle = rate * 2;

        for (int frame = 0; frame < frames; frame++)
        {
            double at = (double)((_position + frame) % cycle) / rate;
            float sample = 0;

            foreach ((double start, double end, double frequency) in Pattern)
            {
                if (at < start || at >= end)
                {
                    continue;
                }

                // Faded in and out over five milliseconds. A tone that starts and
                // stops at full amplitude is a click of its own, which would make
                // the deliberate clicks harder to hear rather than easier.
                double into = at - start;
                double envelope = Math.Min(1, Math.Min(into, end - at) / 0.005);

                sample = (float)(Math.Sin(2 * Math.PI * frequency * into) * envelope * Level);
                break;
            }

            byte* start2 = destination + (frame * stride);

            for (int channel = 0; channel < _format.Channels; channel++)
            {
                Write(start2 + (channel * bytes), sample);
            }
        }

        _position += frames;
    }

    private void Write(byte* at, float sample)
    {
        if (_format.IsFloat)
        {
            *(float*)at = sample;
            return;
        }

        switch (_format.Bits)
        {
            case 16:
                *(short*)at = (short)(sample * 32767f);
                break;

            case 24:
                int value = (int)(sample * 8388607f);
                at[0] = (byte)value;
                at[1] = (byte)(value >> 8);
                at[2] = (byte)(value >> 16);
                break;

            default:
                *(int*)at = (int)((double)sample * 2147483647.0);
                break;
        }
    }

    private void Release()
    {
        try
        {
            _ = _client?.Stop();
        }
        catch (Exception) { }

        foreach (object? reference in new object?[] { _render, _client, _device, _enumerator })
        {
            if (reference is not null)
            {
                try
                {
                    Marshal.ReleaseComObject(reference);
                }
                catch (Exception) { }
            }
        }

        _render = null;
        _client = null;
        _device = null;
        _enumerator = null;
    }
}

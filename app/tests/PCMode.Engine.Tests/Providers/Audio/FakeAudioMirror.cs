namespace PCMode.Engine.Tests;

/// <summary>
/// A Party mirror that moves no samples.
/// </summary>
/// <remarks>
/// The reason <see cref="IAudioMirrorFactory"/> exists. Everything worth testing
/// about Party mode -- that it will not start without both endpoints, that
/// asking for it twice does not produce two of them, that leaving it stops it,
/// that an endpoint going away is reported rather than routed around -- is a
/// decision made above the samples, and none of it can be arranged on the
/// machine running the tests. Nothing here opens an endpoint, so the test suite
/// never plays anything through anybody's speakers.
/// </remarks>
internal sealed class FakeAudioMirrorFactory : IAudioMirrorFactory
{
    /// <summary>Thrown instead of starting, when set.</summary>
    public Exception? Fails { get; set; }

    /// <summary>Every mirror ever started, running or not, in order.</summary>
    public List<FakeAudioMirror> Started { get; } = [];

    /// <summary>The ones that have not been disposed.</summary>
    public IReadOnlyList<FakeAudioMirror> Live => [.. Started.Where(mirror => !mirror.Disposed)];

    public IAudioMirror Start(AudioMirrorRequest request)
    {
        if (Fails is not null)
        {
            throw Fails;
        }

        var mirror = new FakeAudioMirror(request);
        Started.Add(mirror);

        return mirror;
    }
}

internal sealed class FakeAudioMirror(AudioMirrorRequest request) : IAudioMirror
{
    public AudioMirrorRequest Request { get; } = request;

    public bool Disposed { get; private set; }

    /// <summary>Set to simulate an endpoint disappearing mid-session.</summary>
    public string? Failure { get; set; }

    /// <summary>Every live adjustment, in order.</summary>
    public List<(TimeSpan Delay, double Gain)> Adjustments { get; } = [];

    public TimeSpan Delay { get; private set; } = request.Delay;

    public double Gain { get; private set; } = request.Gain;

    public AudioMirrorHealth Health => new(
        Capturing: !Disposed && Failure is null,
        Rendering: !Disposed && Failure is null,
        "48000 Hz, 2 ch, 32-bit float",
        "44100 Hz, 2 ch, 32-bit float",
        (int)Delay.TotalMilliseconds,
        Gain,
        ClockOffsetMs: 0.4,
        DriftCorrections: 12,
        Resyncs: 0,
        Underruns: 0,
        FramesMirrored: 480000,
        EventDriven: true,
        ReferenceBuffer: new AudioMirrorBuffer(RequestedMs: 3, ActualMs: 3, DevicePeriodMs: 3, LowLatencyEngine: true),
        MirrorBuffer: new AudioMirrorBuffer(RequestedMs: 3, ActualMs: 3, DevicePeriodMs: 3, LowLatencyEngine: true),
        CushionMs: 4.5,
        BaselineLatencyMs: 10.5,
        EffectiveLatencyMs: 10.5 + Delay.TotalMilliseconds,
        Failure);

    public void Adjust(TimeSpan delay, double gain)
    {
        Delay = delay;
        Gain = gain;
        Adjustments.Add((delay, gain));
    }

    public void Dispose() => Disposed = true;
}

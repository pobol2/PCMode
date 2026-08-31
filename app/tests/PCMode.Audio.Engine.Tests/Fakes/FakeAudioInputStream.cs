using PCMode.Audio.Core.Models;
using PCMode.Audio.Engine.Contracts;

namespace PCMode.Audio.Engine.Tests.Fakes;

internal sealed class FakeAudioInputStream :
    IAudioInputStream
{
    private readonly float[] _samples;
    private int _samplePosition;

    public AudioFormat Format { get; }

    public bool IsStarted { get; private set; }

    public bool IsDisposed { get; private set; }

    public FakeAudioInputStream(
        AudioFormat format,
        IEnumerable<float> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        Format = format;
        _samples = samples.ToArray();

        if (_samples.Length % format.ChannelCount != 0)
        {
            throw new ArgumentException(
                "Fake input must contain complete audio frames.",
                nameof(samples));
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(
            IsDisposed,
            this);

        IsStarted = true;
    }

    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(
            IsDisposed,
            this);

        if (!IsStarted)
        {
            throw new InvalidOperationException(
                "The fake input is not started.");
        }

        int remainingSamples =
            _samples.Length - _samplePosition;

        int sampleCount =
            Math.Min(destination.Length, remainingSamples);

        sampleCount -=
            sampleCount % Format.ChannelCount;

        if (sampleCount == 0)
        {
            return 0;
        }

        _samples
            .AsSpan(_samplePosition, sampleCount)
            .CopyTo(destination);

        _samplePosition += sampleCount;

        return sampleCount / Format.ChannelCount;
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(
            IsDisposed,
            this);

        IsStarted = false;
    }

    public void Dispose()
    {
        IsStarted = false;
        IsDisposed = true;
    }
}

internal sealed class FakeAudioInputStreamFactory :
    IAudioInputStreamFactory
{
    private readonly AudioFormat _format;
    private readonly float[] _samples;

    public int CreateCount { get; private set; }

    public FakeAudioInputStream? LastStream { get; private set; }

    public FakeAudioInputStreamFactory(
        AudioFormat format,
        params float[] samples)
    {
        _format = format;
        _samples = samples;
    }

    public IAudioInputStream Create()
    {
        CreateCount++;

        LastStream = new FakeAudioInputStream(
            _format,
            _samples);

        return LastStream;
    }
}
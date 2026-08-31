namespace PCMode.Audio.Engine.Timing;

public sealed class AudioRingBuffer
{
    private readonly float[] _samples;
    private readonly int _channelCount;

    private int _readSampleIndex;
    private int _writeSampleIndex;
    private int _sampleCount;

    public int CapacityFrames { get; }

    public int CountFrames =>
        _sampleCount /
        _channelCount;

    public int FreeFrames =>
        CapacityFrames -
        CountFrames;

    public long UnderrunCount { get; private set; }

    public long UnderrunFrameCount { get; private set; }

    public AudioRingBuffer(
        int capacityFrames,
        int channelCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            capacityFrames);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            channelCount);

        CapacityFrames =
            capacityFrames;

        _channelCount =
            channelCount;

        _samples =
            new float[
                checked(
                    capacityFrames *
                    channelCount)];
    }

    public void Write(
        ReadOnlySpan<float> samples)
    {
        ValidateCompleteFrames(
            samples.Length,
            nameof(samples));

        int frameCount =
            samples.Length /
            _channelCount;

        if (frameCount > FreeFrames)
        {
            throw new InvalidOperationException(
                $"Audio ring buffer overflow: requested {frameCount} frame(s), " +
                $"but only {FreeFrames} frame(s) are free.");
        }

        CopyIntoRing(
            samples);

        _sampleCount +=
            samples.Length;
    }

    public int Read(
        Span<float> destination)
    {
        ValidateCompleteFrames(
            destination.Length,
            nameof(destination));

        int requestedFrames =
            destination.Length /
            _channelCount;

        int availableFrames =
            Math.Min(
                requestedFrames,
                CountFrames);

        int availableSamples =
            checked(
                availableFrames *
                _channelCount);

        CopyFromRing(
            destination.Slice(
                0,
                availableSamples));

        _sampleCount -=
            availableSamples;

        if (availableFrames <
            requestedFrames)
        {
            int missingFrames =
                requestedFrames -
                availableFrames;

            destination
                .Slice(
                    availableSamples)
                .Clear();

            UnderrunCount++;

            UnderrunFrameCount +=
                missingFrames;
        }

        return availableFrames;
    }

    public void Reset()
    {
        Array.Clear(
            _samples);

        _readSampleIndex =
            0;

        _writeSampleIndex =
            0;

        _sampleCount =
            0;

        UnderrunCount =
            0;

        UnderrunFrameCount =
            0;
    }

    public void PrimeSilence(
        int frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(
            frameCount);

        if (frameCount > FreeFrames)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameCount),
                frameCount,
                "Silence priming exceeds the free ring-buffer capacity.");
        }

        int sampleCount =
            checked(
                frameCount *
                _channelCount);

        int remaining =
            sampleCount;

        while (remaining > 0)
        {
            int contiguous =
                Math.Min(
                    remaining,
                    _samples.Length -
                    _writeSampleIndex);

            _samples
                .AsSpan(
                    _writeSampleIndex,
                    contiguous)
                .Clear();

            _writeSampleIndex =
                (_writeSampleIndex +
                 contiguous) %
                _samples.Length;

            remaining -=
                contiguous;
        }

        _sampleCount +=
            sampleCount;
    }

    private void CopyIntoRing(
        ReadOnlySpan<float> source)
    {
        int remaining =
            source.Length;

        int sourceIndex =
            0;

        while (remaining > 0)
        {
            int contiguous =
                Math.Min(
                    remaining,
                    _samples.Length -
                    _writeSampleIndex);

            source
                .Slice(
                    sourceIndex,
                    contiguous)
                .CopyTo(
                    _samples.AsSpan(
                        _writeSampleIndex,
                        contiguous));

            _writeSampleIndex =
                (_writeSampleIndex +
                 contiguous) %
                _samples.Length;

            sourceIndex +=
                contiguous;

            remaining -=
                contiguous;
        }
    }

    private void CopyFromRing(
        Span<float> destination)
    {
        int remaining =
            destination.Length;

        int destinationIndex =
            0;

        while (remaining > 0)
        {
            int contiguous =
                Math.Min(
                    remaining,
                    _samples.Length -
                    _readSampleIndex);

            _samples
                .AsSpan(
                    _readSampleIndex,
                    contiguous)
                .CopyTo(
                    destination.Slice(
                        destinationIndex,
                        contiguous));

            _readSampleIndex =
                (_readSampleIndex +
                 contiguous) %
                _samples.Length;

            destinationIndex +=
                contiguous;

            remaining -=
                contiguous;
        }
    }

    private void ValidateCompleteFrames(
        int sampleCount,
        string parameterName)
    {
        if (sampleCount %
            _channelCount != 0)
        {
            throw new ArgumentException(
                "Sample count must contain complete audio frames.",
                parameterName);
        }
    }
}
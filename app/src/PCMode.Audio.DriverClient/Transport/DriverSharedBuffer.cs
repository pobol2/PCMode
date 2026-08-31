namespace PCMode.Audio.DriverClient.Transport;

internal sealed class DriverSharedBuffer
{
    private readonly float[] _samples;

    private int _readOffset;

    private int _count;

    public int CapacitySamples =>
        _samples.Length;

    public int Count =>
        _count;

    public DriverSharedBuffer(
        int capacitySamples)
    {
        ArgumentOutOfRangeException
            .ThrowIfNegativeOrZero(
                capacitySamples);

        _samples =
            new float[
                capacitySamples];
    }

    public void Clear()
    {
        _readOffset =
            0;

        _count =
            0;
    }

    public void Replace(
        ReadOnlySpan<float> samples)
    {
        if (samples.Length >
            _samples.Length)
        {
            throw new InvalidOperationException(
                "Driver packet exceeds the bounded staging buffer.");
        }

        samples.CopyTo(
            _samples);

        _readOffset =
            0;

        _count =
            samples.Length;
    }

    public int Read(
        Span<float> destination)
    {
        if (_count == 0 ||
            destination.IsEmpty)
        {
            return 0;
        }

        int count =
            Math.Min(
                destination.Length,
                _count);

        _samples
            .AsSpan(
                _readOffset,
                count)
            .CopyTo(
                destination);

        _readOffset +=
            count;

        _count -=
            count;

        if (_count == 0)
        {
            _readOffset =
                0;
        }

        return count;
    }
}
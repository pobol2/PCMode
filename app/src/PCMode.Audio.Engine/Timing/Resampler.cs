namespace PCMode.Audio.Engine.Timing;

public sealed class Resampler
{
    public const double MinimumSupportedRatio =
        0.95;

    public const double MaximumSupportedRatio =
        1.05;

    private readonly int _channelCount;

    private readonly float[] _previousFrame;

    private bool _initialized;

    private long _previousSourceFramePosition;

    private double _nextSourcePosition;

    public Resampler(
        int channelCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            channelCount);

        _channelCount =
            channelCount;

        _previousFrame =
            new float[channelCount];
    }

    public int Process(
        ReadOnlySpan<float> source,
        double outputPerInputRatio,
        Span<float> destination)
    {
        if (source.Length %
            _channelCount != 0)
        {
            throw new ArgumentException(
                "Source must contain complete audio frames.",
                nameof(source));
        }

        if (destination.Length %
            _channelCount != 0)
        {
            throw new ArgumentException(
                "Destination must contain complete audio frames.",
                nameof(destination));
        }

        if (!double.IsFinite(
                outputPerInputRatio) ||
            outputPerInputRatio <
                MinimumSupportedRatio ||
            outputPerInputRatio >
                MaximumSupportedRatio)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputPerInputRatio),
                outputPerInputRatio,
                $"Resampling ratio must be between " +
                $"{MinimumSupportedRatio:F3} and " +
                $"{MaximumSupportedRatio:F3}.");
        }

        int sourceFrameCount =
            source.Length /
            _channelCount;

        if (sourceFrameCount == 0)
        {
            return 0;
        }

        int destinationFrameCapacity =
            destination.Length /
            _channelCount;

        int outputFrameCount =
            0;

        int firstSourceFrame =
            0;

        if (!_initialized)
        {
            if (destinationFrameCapacity == 0)
            {
                throw new ArgumentException(
                    "Destination has no frame capacity.",
                    nameof(destination));
            }

            ReadOnlySpan<float> firstFrame =
                source.Slice(
                    0,
                    _channelCount);

            firstFrame.CopyTo(
                destination.Slice(
                    0,
                    _channelCount));

            firstFrame.CopyTo(
                _previousFrame);

            outputFrameCount =
                1;

            _previousSourceFramePosition =
                0;

            _nextSourcePosition =
                1.0 /
                outputPerInputRatio;

            firstSourceFrame =
                1;

            _initialized =
                true;
        }

        double sourceStep =
            1.0 /
            outputPerInputRatio;

        for (int sourceFrameIndex =
                 firstSourceFrame;
             sourceFrameIndex <
                 sourceFrameCount;
             sourceFrameIndex++)
        {
            long currentSourcePosition =
                _previousSourceFramePosition +
                1;

            ReadOnlySpan<float> currentFrame =
                source.Slice(
                    sourceFrameIndex *
                    _channelCount,
                    _channelCount);

            while (_nextSourcePosition <=
                   currentSourcePosition)
            {
                if (outputFrameCount >=
                    destinationFrameCapacity)
                {
                    throw new InvalidOperationException(
                        "Resampler destination capacity is too small.");
                }

                double interpolation =
                    _nextSourcePosition -
                    (currentSourcePosition -
                     1);

                interpolation =
                    Math.Clamp(
                        interpolation,
                        0.0,
                        1.0);

                Span<float> outputFrame =
                    destination.Slice(
                        outputFrameCount *
                        _channelCount,
                        _channelCount);

                for (int channel = 0;
                     channel < _channelCount;
                     channel++)
                {
                    float previous =
                        _previousFrame[
                            channel];

                    float current =
                        currentFrame[
                            channel];

                    outputFrame[channel] =
                        previous +
                        (current -
                         previous) *
                        (float)interpolation;
                }

                outputFrameCount++;

                _nextSourcePosition +=
                    sourceStep;
            }

            currentFrame.CopyTo(
                _previousFrame);

            _previousSourceFramePosition =
                currentSourcePosition;
        }

        return outputFrameCount;
    }

    public void Reset()
    {
        Array.Clear(
            _previousFrame);

        _initialized =
            false;

        _previousSourceFramePosition =
            0;

        _nextSourcePosition =
            0.0;
    }

    public static int GetMaximumOutputFrameCount(
        int inputFrameCount,
        double maximumRatio)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            inputFrameCount);

        if (!double.IsFinite(
                maximumRatio) ||
            maximumRatio <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRatio));
        }

        return checked(
            (int)Math.Ceiling(
                inputFrameCount *
                maximumRatio) +
            2);
    }
}
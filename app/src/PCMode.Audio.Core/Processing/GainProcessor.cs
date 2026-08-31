using PCMode.Audio.Core.Buffers;

namespace PCMode.Audio.Core.Processing;

public sealed class GainProcessor : IAudioProcessor
{
    /// <summary>
    /// Frames a live <see cref="Reconfigure"/> takes to reach its new gain, at
    /// whatever sample rate the route runs at. Short enough to feel immediate
    /// to a person nudging a slider, long enough that the step from one linear
    /// gain to another is inaudible as a click rather than a fade.
    /// </summary>
    public const int RampFrameCount =
        256;

    private float _currentLinearGain;

    private float _rampStartGain;

    private float _rampTargetGain;

    private int _rampFramesRemaining;

    public float GainDb { get; private set; }

    public float Volume { get; private set; }

    public bool Muted { get; private set; }

    public GainProcessor(
        float gainDb)
        : this(
            gainDb,
            1.0f,
            false)
    {
    }

    public GainProcessor(
        float gainDb,
        float volume,
        bool muted)
    {
        float linear =
            ComputeLinearGain(
                gainDb,
                volume,
                muted);

        GainDb =
            gainDb;

        Volume =
            volume;

        Muted =
            muted;

        // Nothing has played through this instance yet, so there is no prior
        // level to ramp away from -- the starting gain is simply the target.
        _currentLinearGain =
            linear;

        _rampTargetGain =
            linear;

        _rampFramesRemaining =
            0;
    }

    /// <summary>
    /// Changes this route's gain while it is running, without a click.
    /// </summary>
    /// <remarks>
    /// Never applied instantaneously: <see cref="Process"/> glides the linear
    /// gain from wherever it currently is to the new target over
    /// <see cref="RampFrameCount"/> frames. Calling this again mid-ramp simply
    /// retargets from the gain the ramp has reached so far, rather than
    /// restarting from the original value.
    /// </remarks>
    public void Reconfigure(
        float gainDb,
        float volume,
        bool muted)
    {
        float linear =
            ComputeLinearGain(
                gainDb,
                volume,
                muted);

        GainDb =
            gainDb;

        Volume =
            volume;

        Muted =
            muted;

        _rampStartGain =
            _currentLinearGain;

        _rampTargetGain =
            linear;

        _rampFramesRemaining =
            RampFrameCount;
    }

    public void Process(
        AudioBlock block)
    {
        Span<float> samples =
            block.Samples;

        int channels =
            block.Format.ChannelCount;

        for (int frameStart = 0;
             frameStart < samples.Length;
             frameStart += channels)
        {
            float gain;

            if (_rampFramesRemaining > 0)
            {
                float progress =
                    1.0f -
                    (_rampFramesRemaining /
                     (float)RampFrameCount);

                gain =
                    _rampStartGain +
                    (_rampTargetGain -
                     _rampStartGain) *
                    progress;

                _rampFramesRemaining--;

                _currentLinearGain =
                    _rampFramesRemaining == 0
                        ? _rampTargetGain
                        : gain;
            }
            else
            {
                gain =
                    _currentLinearGain;
            }

            for (int channel = 0;
                 channel < channels;
                 channel++)
            {
                samples[
                    frameStart +
                    channel] *=
                    gain;
            }
        }
    }

    /// <summary>
    /// Clears an in-flight live-calibration ramp and restores the configured
    /// target gain. A restarted audio stream has no valid continuity with the
    /// samples that preceded it, so carrying the old ramp position across the
    /// restart would manufacture one.
    /// </summary>
    public void Reset()
    {
        float linear =
            ComputeLinearGain(
                GainDb,
                Volume,
                Muted);

        _currentLinearGain =
            linear;

        _rampStartGain =
            linear;

        _rampTargetGain =
            linear;

        _rampFramesRemaining =
            0;
    }

    private static float ComputeLinearGain(
        float gainDb,
        float volume,
        bool muted)
    {
        if (!float.IsFinite(gainDb))
        {
            throw new ArgumentOutOfRangeException(
                nameof(gainDb),
                gainDb,
                "Gain must be finite.");
        }

        if (!float.IsFinite(volume) ||
            volume < 0.0f ||
            volume > 1.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(volume),
                volume,
                "Volume must be a finite value between 0.0 and 1.0.");
        }

        float profileGain =
            MathF.Pow(
                10.0f,
                gainDb / 20.0f);

        if (!float.IsFinite(profileGain))
        {
            throw new ArgumentOutOfRangeException(
                nameof(gainDb),
                gainDb,
                "Gain is too large to represent.");
        }

        return muted
            ? 0.0f
            : profileGain * volume;
    }
}

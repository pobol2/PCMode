using PCMode.Audio.Core.Buffers;
using PCMode.Audio.Core.Models;

namespace PCMode.Audio.Core.Processing;

public sealed class ParametricEqProcessor : IAudioProcessor
{
    public const int ReconfigurationFadeFrames = 128;

    private readonly AudioFormat _format;

    private ProcessingState _state;

    private ProcessingState? _pendingState;

    private ProcessingState? _transitionState;

    private int _transitionFramesRemaining;

    public ParametricEqProfile Profile =>
        Volatile.Read(ref _pendingState)?.Profile ?? _transitionState?.Profile ?? Volatile.Read(ref _state).Profile;

    public ParametricEqProcessor(ParametricEqProfile profile, AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _format = format;

        _state = BuildState(profile, format);
    }

    public void Process(AudioBlock block)
    {
        if (block.Format != _format)
        {
            throw new ArgumentException(
                "Audio block format must match the format used to configure the parametric EQ.",
                nameof(block)
            );
        }

        ProcessingState? pending = Interlocked.Exchange(ref _pendingState, null);

        if (pending is not null)
        {
            _transitionState = pending;

            _transitionFramesRemaining = ReconfigurationFadeFrames;
        }

        ProcessingState current = Volatile.Read(ref _state);

        ProcessingState? transition = _transitionState;

        Span<float> samples = block.Samples;

        int channels = _format.ChannelCount;

        for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex += channels)
        {
            if (transition is null)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    samples[sampleIndex + channel] = ProcessSample(current, channel, samples[sampleIndex + channel]);
                }

                continue;
            }

            float blend = 1.0f - (_transitionFramesRemaining / (float)ReconfigurationFadeFrames);

            for (int channel = 0; channel < channels; channel++)
            {
                float input = samples[sampleIndex + channel];

                float oldSample = ProcessSample(current, channel, input);

                float newSample = ProcessSample(transition, channel, input);

                samples[sampleIndex + channel] = oldSample + (newSample - oldSample) * blend;
            }

            _transitionFramesRemaining--;

            if (_transitionFramesRemaining == 0)
            {
                current = transition;

                Volatile.Write(ref _state, transition);

                _transitionState = null;

                transition = null;
            }
        }
    }

    public void Reconfigure(ParametricEqProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        ProcessingState replacement = BuildState(profile, _format);

        Volatile.Write(ref _pendingState, replacement);
    }

    public void Reset()
    {
        ParametricEqProfile profile = Profile;

        Volatile.Write(ref _pendingState, null);

        _transitionState = null;

        _transitionFramesRemaining = 0;

        Volatile.Write(ref _state, BuildState(profile, _format));
    }

    private static float ProcessSample(ProcessingState state, int channel, float sample)
    {
        sample *= state.PreampLinear;

        Biquad[] filters = state.FiltersByChannel[channel];

        for (int bandIndex = 0; bandIndex < filters.Length; bandIndex++)
        {
            sample = filters[bandIndex].ProcessSample(sample);
        }

        return sample;
    }

    private static ProcessingState BuildState(ParametricEqProfile profile, AudioFormat format)
    {
        float preampLinear = MathF.Pow(10.0f, profile.PreampGainDb / 20.0f);

        if (!float.IsFinite(preampLinear))
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile),
                profile.PreampGainDb,
                "Preamp gain is too large to represent."
            );
        }

        var filtersByChannel = new Biquad[format.ChannelCount][];

        for (int channel = 0; channel < format.ChannelCount; channel++)
        {
            var filters = new Biquad[profile.Bands.Count];

            for (int bandIndex = 0; bandIndex < profile.Bands.Count; bandIndex++)
            {
                filters[bandIndex] = Biquad.Create(profile.Bands[bandIndex], format.SampleRate);
            }

            filtersByChannel[channel] = filters;
        }

        return new ProcessingState(profile, preampLinear, filtersByChannel);
    }

    private sealed class ProcessingState
    {
        public ParametricEqProfile Profile { get; }

        public float PreampLinear { get; }

        public Biquad[][] FiltersByChannel { get; }

        public ProcessingState(ParametricEqProfile profile, float preampLinear, Biquad[][] filtersByChannel)
        {
            Profile = profile;

            PreampLinear = preampLinear;

            FiltersByChannel = filtersByChannel;
        }
    }
}

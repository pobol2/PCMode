using PCMode.Audio.Core.Buffers;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Core.Processing;
using Xunit;

namespace PCMode.Audio.Core.Tests.Processing;

public sealed class AudioProcessingChainTests
{
    [Fact]
    public void Processors_run_in_declared_order()
    {
        var samples = new[]
        {
            1.0f
        };

        var chain = new AudioProcessingChain(
            new AddProcessor(1.0f),
            new MultiplyProcessor(2.0f));

        var block = new AudioBlock(
            samples,
            new AudioFormat(48_000, 1));

        chain.Process(block);

        Assert.Equal(4.0f, samples[0]);
    }

    [Fact]
    public void Empty_chain_is_a_no_op()
    {
        var samples = new[]
        {
            0.25f,
            -0.5f
        };

        var chain = new AudioProcessingChain();

        var block = new AudioBlock(
            samples,
            new AudioFormat(48_000, 2));

        chain.Process(block);

        Assert.Equal(0.25f, samples[0]);
        Assert.Equal(-0.5f, samples[1]);
    }

    private sealed class AddProcessor : IAudioProcessor
    {
        private readonly float _value;

        public AddProcessor(float value)
        {
            _value = value;
        }

        public void Process(AudioBlock block)
        {
            Span<float> samples = block.Samples;

            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] += _value;
            }
        }
    }

    private sealed class MultiplyProcessor : IAudioProcessor
    {
        private readonly float _value;

        public MultiplyProcessor(float value)
        {
            _value = value;
        }

        public void Process(AudioBlock block)
        {
            Span<float> samples = block.Samples;

            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= _value;
            }
        }
    }
}
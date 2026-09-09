using PCMode.Audio.Core.Models;
using PCMode.Audio.Engine.Runtime;
using PCMode.Audio.Engine.Tests.Fakes;
using Xunit;

namespace PCMode.Audio.Engine.Tests.Runtime;

public sealed class AudioRouteBufferingTests
{
    [Fact]
    public void Each_route_has_independent_configured_buffering()
    {
        var format = new AudioFormat(sampleRate: 1_000, channelCount: 1);

        var inputFactory = new FakeAudioInputStreamFactory(format, 1.0f, 2.0f, 3.0f, 4.0f);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory, blockFrameCapacity: 4);

        AudioEndpointId outputA = new("output-a");

        AudioEndpointId outputB = new("output-b");

        engine.Apply(
            new AudioTarget(
                "buffering",
                "Buffering",
                new AudioRoute(outputA, delay: TimeSpan.Zero),
                new AudioRoute(outputB, delay: TimeSpan.FromMilliseconds(2))
            )
        );

        engine.ProcessNextBlock();

        Assert.Equal(new[] { 1.0f, 2.0f, 3.0f, 4.0f }, outputFactory.GetStream(outputA).GetWrittenSamples());

        Assert.Equal(new[] { 0.0f, 0.0f, 1.0f, 2.0f }, outputFactory.GetStream(outputB).GetWrittenSamples());
    }

    [Fact]
    public void Buffering_on_one_route_does_not_change_other_route_processing()
    {
        var format = new AudioFormat(sampleRate: 1_000, channelCount: 1);

        var inputFactory = new FakeAudioInputStreamFactory(format, 10.0f, 20.0f, 30.0f, 40.0f);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory, blockFrameCapacity: 4);

        AudioEndpointId immediateOutput = new("immediate");

        AudioEndpointId delayedOutput = new("delayed");

        engine.Apply(
            new AudioTarget(
                "independent",
                "Independent",
                new AudioRoute(immediateOutput, delay: TimeSpan.Zero),
                new AudioRoute(delayedOutput, delay: TimeSpan.FromMilliseconds(3))
            )
        );

        engine.ProcessNextBlock();

        Assert.Equal(
            new[] { 10.0f, 20.0f, 30.0f, 40.0f },
            outputFactory.GetStream(immediateOutput).GetWrittenSamples()
        );

        Assert.Equal(new[] { 0.0f, 0.0f, 0.0f, 10.0f }, outputFactory.GetStream(delayedOutput).GetWrittenSamples());
    }
}

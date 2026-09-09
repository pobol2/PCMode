using PCMode.Audio.Core.Models;
using Xunit;

namespace PCMode.Audio.Core.Tests.Models;

public sealed class AudioModelTests
{
    [Fact]
    public void AudioFormat_stores_sample_rate_and_channel_count()
    {
        var format = new AudioFormat(48_000, 2);

        Assert.Equal(48_000, format.SampleRate);
        Assert.Equal(2, format.ChannelCount);
        Assert.True(format.IsValid);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(-1, 2)]
    [InlineData(48_000, 0)]
    [InlineData(48_000, -1)]
    public void AudioFormat_rejects_non_positive_values(int sampleRate, int channelCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFormat(sampleRate, channelCount));
    }

    [Fact]
    public void AudioEndpointId_has_value_equality()
    {
        var first = new AudioEndpointId("endpoint-a");
        var second = new AudioEndpointId("endpoint-a");
        var other = new AudioEndpointId("endpoint-b");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Equal("endpoint-a", first.ToString());
    }

    [Fact]
    public void AudioTarget_supports_multiple_routes()
    {
        var headphones = new AudioRoute(new AudioEndpointId("headphones"));

        var soundbar = new AudioRoute(
            new AudioEndpointId("soundbar"),
            requirement: AudioRouteRequirement.Optional,
            volume: 0.75f,
            muted: true,
            delay: TimeSpan.FromMilliseconds(25),
            processing: new AudioProcessingProfile(-3.0f)
        );

        var target = new AudioTarget("party", "Party", headphones, soundbar);

        Assert.Equal("party", target.Id);
        Assert.Equal("Party", target.Name);
        Assert.Equal(2, target.Routes.Count);

        Assert.Equal(AudioRouteRequirement.Required, target.Routes[0].Requirement);

        Assert.Equal(AudioRouteRequirement.Optional, target.Routes[1].Requirement);

        Assert.Equal(0.75f, target.Routes[1].Volume);
        Assert.True(target.Routes[1].Muted);
        Assert.Equal(TimeSpan.FromMilliseconds(25), target.Routes[1].Delay);

        Assert.Equal(-3.0f, target.Routes[1].Processing.GainDb);
    }

    [Fact]
    public void AudioTarget_requires_at_least_one_route()
    {
        Assert.Throws<ArgumentException>(() => new AudioTarget("empty", "Empty"));
    }

    [Fact]
    public void AudioRoute_rejects_invalid_volume()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioRoute(new AudioEndpointId("endpoint"), volume: 1.1f));
    }
}

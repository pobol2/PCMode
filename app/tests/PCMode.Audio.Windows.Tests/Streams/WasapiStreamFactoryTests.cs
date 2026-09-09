using PCMode.Audio.Core.Models;
using PCMode.Audio.Windows.Streams;
using Xunit;

namespace PCMode.Audio.Windows.Tests.Streams;

public sealed class WasapiStreamFactoryTests
{
    [Fact]
    public void Construction_does_not_touch_hardware()
    {
        var id = new AudioEndpointId("test-source");

        var factory = new WasapiStreamFactory(id);

        Assert.Equal(id, factory.LoopbackSourceId);
    }

    [Fact]
    public void Default_source_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new WasapiStreamFactory(default));
    }
}

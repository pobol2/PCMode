using PCMode.Audio.Host;
using Xunit;

namespace PCMode.Audio.Integration.Tests;

public sealed class Step11HostModeTests
{
    [Fact]
    public void Production_driver_input_is_default()
    {
        HostOptions options =
            HostOptions.Parse(
                Array.Empty<string>());

        Assert.False(
            options.UsesDevelopmentInput);

        Assert.Null(
            options
                .DevelopmentSourceEndpointId);
    }

    [Fact]
    public void Development_loopback_requires_explicit_argument()
    {
        HostOptions options =
            HostOptions.Parse(
                new[]
                {
                    "--development-source-id",
                    "fake-source"
                });

        Assert.True(
            options.UsesDevelopmentInput);

        Assert.Equal(
            "fake-source",
            options
                .DevelopmentSourceEndpointId);
    }

    [Fact]
    public void Legacy_source_argument_is_rejected()
    {
        Assert.Throws<
            ArgumentException>(
                () =>
                    HostOptions.Parse(
                        new[]
                        {
                            "--source-id",
                            "fake-source"
                        }));
    }
}
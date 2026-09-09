using PCMode.Audio.Core.Models;
using PCMode.Audio.Protocol;
using PCMode.Audio.Protocol.Messages;
using Xunit;

namespace PCMode.Audio.Protocol.Tests;

public sealed class ProtocolCompatibilityTests
{
    [Fact]
    public void Current_protocol_is_compatible()
    {
        Assert.True(HostProtocolVersion.IsCompatible(HostProtocolVersion.Current));
    }

    [Fact]
    public void Older_and_future_protocols_are_rejected()
    {
        Assert.False(HostProtocolVersion.IsCompatible(HostProtocolVersion.MinimumCompatible - 1));

        Assert.False(HostProtocolVersion.IsCompatible(HostProtocolVersion.Current + 1));
    }

    [Fact]
    public void Audio_target_round_trips_through_protocol_dto()
    {
        var target = new AudioTarget(
            "test",
            "Test Target",
            new AudioRoute(
                new AudioEndpointId("endpoint-a"),
                delay: TimeSpan.FromMilliseconds(25),
                processing: new AudioProcessingProfile(
                    -3.0f,
                    new ParametricEqProfile(-2.0f, new EqBand(EqFilterType.Peak, 1000.0f, 4.0f, 1.2f))
                )
            )
        );

        ApplyAudioTargetRequest request = ApplyAudioTargetRequest.FromModel(target);

        AudioTarget reconstructed = request.Target.ToModel();

        Assert.Equal(target.Id, reconstructed.Id);

        Assert.Equal(target.Name, reconstructed.Name);

        Assert.Single(reconstructed.Routes);

        Assert.Equal(25.0, reconstructed.Routes[0].Delay.TotalMilliseconds);

        Assert.Equal(-3.0f, reconstructed.Routes[0].Processing.GainDb);

        Assert.Single(reconstructed.Routes[0].Processing.ParametricEq!.Bands);
    }

    [Fact]
    public void Health_round_trip_preserves_virtual_input_state()
    {
        var inputFailure = new AudioFailure("Driver", "transport reconnecting", -1, DateTimeOffset.UtcNow);

        var health = new AudioEngineHealth(
            AudioEngineRuntimeState.Running,
            "target",
            "Target",
            [],
            null,
            new AudioInputHealth(
                AudioInputKind.VirtualEndpointDriver,
                AudioInputHealthState.Reconnecting,
                new AudioFormat(48_000, 2),
                inputFailure
            )
        );

        AudioEngineHealth reconstructed = AudioEngineHealthDto.FromModel(health).ToModel();

        Assert.NotNull(reconstructed.Input);

        Assert.Equal(AudioInputKind.VirtualEndpointDriver, reconstructed.Input!.Kind);

        Assert.Equal(AudioInputHealthState.Reconnecting, reconstructed.Input.State);

        Assert.Equal(new AudioFormat(48_000, 2), reconstructed.Input.Format);

        Assert.Equal("transport reconnecting", reconstructed.Input.LastFailure?.Message);
    }
}

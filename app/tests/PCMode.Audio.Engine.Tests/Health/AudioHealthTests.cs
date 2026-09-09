using PCMode.Audio.Core.Models;
using PCMode.Audio.Engine.Runtime;
using PCMode.Audio.Engine.Tests.Fakes;
using Xunit;

namespace PCMode.Audio.Engine.Tests.Health;

public sealed class AudioHealthTests
{
    [Fact]
    public void Waveform_is_idle_before_processing_and_reflects_audio_after()
    {
        AudioFormat format = new(1_000, 1);

        var input = new FakeAudioInputStreamFactory(format, 1.0f, -1.0f, 0.5f, -0.5f);

        var outputs = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(input, outputs, blockFrameCapacity: 4);

        AudioEndpointId endpoint = new("waveform");

        engine.Apply(new AudioTarget("target", "Target", new AudioRoute(endpoint)));

        AudioRouteWaveform beforeProcessing = Assert.Single(engine.GetWaveform());

        Assert.True(beforeProcessing.Available);

        Assert.All(
            beforeProcessing.Buckets,
            sample =>
            {
                Assert.Equal(0f, sample.Min);

                Assert.Equal(0f, sample.Max);
            }
        );

        engine.ProcessNextBlock();

        AudioRouteWaveform afterProcessing = Assert.Single(engine.GetWaveform());

        Assert.True(afterProcessing.Available);

        Assert.Contains(afterProcessing.Buckets, sample => sample.Max > 0f);

        Assert.Contains(afterProcessing.Buckets, sample => sample.Min < 0f);
    }

    [Fact]
    public void Healthy_route_reports_healthy_state()
    {
        AudioFormat format = new(1_000, 1);

        var input = new FakeAudioInputStreamFactory(format, 1.0f, 2.0f, 3.0f, 4.0f);

        var outputs = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(input, outputs, blockFrameCapacity: 4);

        AudioEndpointId endpoint = new("healthy");

        engine.Apply(new AudioTarget("target", "Target", new AudioRoute(endpoint)));

        AudioEngineHealth health = engine.GetHealth();

        Assert.Equal(AudioHealthState.Healthy, health.State);

        Assert.Equal(AudioEngineRuntimeState.Running, health.EngineState);

        AudioRouteHealth route = Assert.Single(health.Routes);

        Assert.Equal(AudioRouteHealthState.Healthy, route.State);

        Assert.Equal(AudioEndpointState.Active, route.EndpointState);

        Assert.Equal(format, route.Format);

        Assert.Null(route.LastFailure);
    }

    [Fact]
    public void Required_route_failure_fails_overall_health()
    {
        AudioFormat format = new(1_000, 1);

        AudioEndpointId failed = new("required");

        var input = new FakeAudioInputStreamFactory(format, 1.0f);

        var outputs = new FakeAudioOutputStreamFactory(failOnStartEndpoints: [failed]);

        using var engine = new AudioEngine(input, outputs);

        Assert.ThrowsAny<Exception>(() =>
            engine.Apply(new AudioTarget("target", "Target", new AudioRoute(failed, AudioRouteRequirement.Required)))
        );

        AudioEngineHealth health = engine.GetHealth();

        Assert.Equal(AudioHealthState.Failed, health.State);

        Assert.Equal(AudioEngineRuntimeState.Failed, health.EngineState);

        AudioRouteHealth route = Assert.Single(health.Routes);

        Assert.Equal(AudioRouteRequirement.Required, route.Requirement);

        Assert.NotNull(route.LastFailure);

        Assert.NotNull(health.LastFailure);
    }

    [Fact]
    public void Optional_route_failure_degrades_without_destroying_required_route()
    {
        AudioFormat format = new(1_000, 1);

        AudioEndpointId required = new("required");

        AudioEndpointId optional = new("optional");

        var input = new FakeAudioInputStreamFactory(format, 1.0f, 2.0f, 3.0f, 4.0f);

        var outputs = new FakeAudioOutputStreamFactory(failOnStartEndpoints: [optional]);

        using var engine = new AudioEngine(input, outputs, blockFrameCapacity: 4);

        engine.Apply(
            new AudioTarget(
                "target",
                "Target",
                new AudioRoute(required, AudioRouteRequirement.Required),
                new AudioRoute(optional, AudioRouteRequirement.Optional)
            )
        );

        AudioEngineHealth health = engine.GetHealth();

        Assert.Equal(AudioEngineState.Running, engine.State);

        Assert.Equal(AudioHealthState.Degraded, health.State);

        AudioRouteHealth requiredHealth = health.Routes.Single(route => route.EndpointId == required);

        AudioRouteHealth optionalHealth = health.Routes.Single(route => route.EndpointId == optional);

        Assert.Equal(AudioRouteHealthState.Healthy, requiredHealth.State);

        Assert.Equal(AudioRouteHealthState.Unavailable, optionalHealth.State);

        Assert.NotNull(optionalHealth.LastFailure);

        engine.ProcessNextBlock();

        Assert.True(outputs.GetStream(required).GetWrittenSamples().Length > 0);
    }

    [Fact]
    public void Underrun_and_resync_counters_surface_in_health()
    {
        AudioFormat format = new(1_000, 1);

        float[] samples = Enumerable.Range(0, 30).Select(value => (float)value).ToArray();

        var input = new FakeAudioInputStreamFactory(format, samples);

        var outputs = new FakeAudioOutputStreamFactory(autoAdvanceClock: false, initialQueuedFrames: 100);

        using var engine = new AudioEngine(input, outputs, blockFrameCapacity: 10);

        AudioEndpointId endpoint = new("output");

        engine.Apply(new AudioTarget("target", "Target", new AudioRoute(endpoint)));

        engine.ProcessNextBlock();

        FakeAudioOutputStream stream = outputs.GetStream(endpoint);

        stream.AdvanceClock(110.0);

        engine.ProcessNextBlock();

        AudioRouteHealth health = Assert.Single(engine.GetHealth().Routes);

        Assert.True(health.UnderrunCount >= 1);

        Assert.True(health.ResyncCount >= 1);
    }

    [Fact]
    public void Last_failure_is_preserved_across_health_reads()
    {
        AudioFormat format = new(1_000, 1);

        AudioEndpointId optional = new("optional");

        AudioEndpointId required = new("required");

        var input = new FakeAudioInputStreamFactory(format, 1.0f);

        var outputs = new FakeAudioOutputStreamFactory(failOnStartEndpoints: [optional]);

        using var engine = new AudioEngine(input, outputs);

        engine.Apply(
            new AudioTarget(
                "target",
                "Target",
                new AudioRoute(required),
                new AudioRoute(optional, AudioRouteRequirement.Optional)
            )
        );

        AudioFailure first = engine.GetHealth().Routes.Single(route => route.EndpointId == optional).LastFailure!;

        AudioFailure second = engine.GetHealth().Routes.Single(route => route.EndpointId == optional).LastFailure!;

        Assert.Equal(first, second);

        Assert.Contains("Simulated startup failure", second.Message);
    }

    [Fact]
    public async Task Health_reads_are_safe_during_processing()
    {
        AudioFormat format = new(1_000, 1);

        float[] samples = Enumerable.Range(0, 10_000).Select(value => (float)value).ToArray();

        var input = new FakeAudioInputStreamFactory(format, samples);

        var outputs = new FakeAudioOutputStreamFactory(sampleCapacity: 100_000);

        using var engine = new AudioEngine(input, outputs, blockFrameCapacity: 10);

        engine.Apply(new AudioTarget("target", "Target", new AudioRoute(new AudioEndpointId("output"))));

        Task reader = Task.Run(() =>
        {
            for (int i = 0; i < 10_000; i++)
            {
                AudioEngineHealth health = engine.GetHealth();

                Assert.NotNull(health);
            }
        });

        for (int i = 0; i < 500; i++)
        {
            engine.ProcessNextBlock();
        }

        await reader.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

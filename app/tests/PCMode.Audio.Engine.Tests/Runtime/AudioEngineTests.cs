using PCMode.Audio.Core.Models;
using PCMode.Audio.Engine.Runtime;
using PCMode.Audio.Engine.Tests.Fakes;
using Xunit;

namespace PCMode.Audio.Engine.Tests.Runtime;

public sealed class AudioEngineTests
{
    [Fact]
    public void One_input_one_route_runs_through_gain_to_output()
    {
        var format = new AudioFormat(48_000, 2);

        var inputFactory = new FakeAudioInputStreamFactory(format, 1.0f, -1.0f, 0.5f, -0.5f);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory, blockFrameCapacity: 2);

        var route = new AudioRoute(new AudioEndpointId("headphones"), processing: new AudioProcessingProfile(-6.0206f));

        var target = new AudioTarget("desktop", "Desktop", route);

        engine.Apply(target);

        Assert.Equal(AudioEngineState.Running, engine.State);

        Assert.Same(target, engine.ActiveTarget);

        Assert.Equal(1, inputFactory.CreateCount);

        Assert.Equal(1, outputFactory.CreateCount);

        Assert.Equal(new AudioEndpointId("headphones"), outputFactory.LastEndpointId);

        int frameCount = engine.ProcessNextBlock();

        Assert.Equal(2, frameCount);

        FakeAudioOutputStream output = Assert.IsType<FakeAudioOutputStream>(outputFactory.LastStream);

        float[] written = output.GetWrittenSamples();

        Assert.Equal(4, written.Length);

        Assert.InRange(written[0], 0.49999f, 0.50001f);

        Assert.InRange(written[1], -0.50001f, -0.49999f);

        Assert.InRange(written[2], 0.24999f, 0.25001f);

        Assert.InRange(written[3], -0.25001f, -0.24999f);

        Assert.Equal(0, engine.ProcessNextBlock());

        engine.Stop();

        Assert.Equal(AudioEngineState.Stopped, engine.State);

        Assert.Null(engine.ActiveTarget);
    }

    [Fact]
    public void Reapplying_equivalent_target_does_not_recreate_streams()
    {
        var format = new AudioFormat(48_000, 2);

        var inputFactory = new FakeAudioInputStreamFactory(format);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory);

        AudioTarget first = CreateTarget("headphones");

        AudioTarget second = CreateTarget("headphones");

        engine.Apply(first);
        engine.Apply(second);

        Assert.Equal(1, inputFactory.CreateCount);

        Assert.Equal(1, outputFactory.CreateCount);

        Assert.Same(second, engine.ActiveTarget);
    }

    [Fact]
    public void Multiple_routes_are_supported()
    {
        var format = new AudioFormat(48_000, 2);

        var inputFactory = new FakeAudioInputStreamFactory(format, 1.0f, -1.0f);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory, blockFrameCapacity: 1);

        AudioEndpointId outputA = new("output-a");

        AudioEndpointId outputB = new("output-b");

        var target = new AudioTarget("multi", "Multi", new AudioRoute(outputA), new AudioRoute(outputB));

        engine.Apply(target);

        Assert.Equal(AudioEngineState.Running, engine.State);

        Assert.Equal(1, inputFactory.CreateCount);

        Assert.Equal(2, outputFactory.CreateCount);

        Assert.Equal(2, outputFactory.Streams.Count);
    }

    [Fact]
    public void Output_stream_contract_mismatch_fails_instead_of_silently_converting()
    {
        var inputFormat = new AudioFormat(48_000, 2);

        var outputFormat = new AudioFormat(44_100, 2);

        var inputFactory = new FakeAudioInputStreamFactory(inputFormat);

        var outputFactory = new FakeAudioOutputStreamFactory(forcedFormat: outputFormat);

        using var engine = new AudioEngine(inputFactory, outputFactory);

        Assert.Throws<NotSupportedException>(() => engine.Apply(CreateTarget("headphones")));

        Assert.Equal(AudioEngineState.Failed, engine.State);

        Assert.Null(engine.ActiveTarget);
    }

    [Fact]
    public void Stop_disposes_the_active_streams()
    {
        var format = new AudioFormat(48_000, 2);

        var inputFactory = new FakeAudioInputStreamFactory(format);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory);

        engine.Apply(CreateTarget("headphones"));

        FakeAudioInputStream input = Assert.IsType<FakeAudioInputStream>(inputFactory.LastStream);

        FakeAudioOutputStream output = Assert.IsType<FakeAudioOutputStream>(outputFactory.LastStream);

        engine.Stop();

        Assert.True(input.IsDisposed);
        Assert.True(output.IsDisposed);

        Assert.Equal(AudioEngineState.Stopped, engine.State);
    }

    private static AudioTarget CreateTarget(string endpoint)
    {
        return new AudioTarget(
            "desktop",
            "Desktop",
            new AudioRoute(new AudioEndpointId(endpoint), processing: new AudioProcessingProfile(0.0f))
        );
    }

    [Fact]
    public void One_input_feeds_two_routes()
    {
        var format = new AudioFormat(48_000, 2);

        var inputFactory = new FakeAudioInputStreamFactory(format, 1.0f, -1.0f, 0.5f, -0.5f);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory, blockFrameCapacity: 2);

        AudioEndpointId outputA = new("output-a");

        AudioEndpointId outputB = new("output-b");

        var target = new AudioTarget("multi", "Multi", new AudioRoute(outputA), new AudioRoute(outputB));

        engine.Apply(target);

        engine.ProcessNextBlock();

        Assert.Equal(1, inputFactory.CreateCount);

        Assert.Equal(2, outputFactory.CreateCount);

        Assert.Equal(new[] { 1.0f, -1.0f, 0.5f, -0.5f }, outputFactory.GetStream(outputA).GetWrittenSamples());

        Assert.Equal(new[] { 1.0f, -1.0f, 0.5f, -0.5f }, outputFactory.GetStream(outputB).GetWrittenSamples());
    }

    [Fact]
    public void Routes_have_independent_peq_state()
    {
        var format = new AudioFormat(48_000, 1);

        var inputFactory = new FakeAudioInputStreamFactory(format, 1.0f, 0.0f, 0.0f, 0.0f);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory, blockFrameCapacity: 4);

        AudioEndpointId outputA = new("output-a");

        AudioEndpointId outputB = new("output-b");

        var peq = new ParametricEqProfile(0.0f, new EqBand(EqFilterType.LowPass, 1_000.0f, q: 0.70710677f));

        var target = new AudioTarget(
            "multi-peq",
            "Multi PEQ",
            new AudioRoute(outputA, processing: new AudioProcessingProfile(0.0f, peq)),
            new AudioRoute(outputB, processing: new AudioProcessingProfile(0.0f, peq))
        );

        engine.Apply(target);

        engine.ProcessNextBlock();

        float[] samplesA = outputFactory.GetStream(outputA).GetWrittenSamples();

        float[] samplesB = outputFactory.GetStream(outputB).GetWrittenSamples();

        Assert.Equal(samplesA, samplesB);
    }

    [Fact]
    public void Failure_of_one_route_fails_apply_and_cleans_every_stream()
    {
        var format = new AudioFormat(48_000, 2);

        AudioEndpointId outputA = new("output-a");

        AudioEndpointId outputB = new("broken-output");

        var inputFactory = new FakeAudioInputStreamFactory(format);

        var outputFactory = new FakeAudioOutputStreamFactory(failOnStartEndpoints: [outputB]);

        using var engine = new AudioEngine(inputFactory, outputFactory);

        var target = new AudioTarget("multi", "Multi", new AudioRoute(outputA), new AudioRoute(outputB));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => engine.Apply(target));

        Assert.Contains("broken-output", ex.ToString());

        Assert.Equal(AudioEngineState.Failed, engine.State);

        Assert.Null(engine.ActiveTarget);

        Assert.All(outputFactory.Streams, stream => Assert.True(stream.IsDisposed));
    }

    [Fact]
    public void Stop_closes_all_output_routes()
    {
        var format = new AudioFormat(48_000, 2);

        var inputFactory = new FakeAudioInputStreamFactory(format);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory);

        engine.Apply(
            new AudioTarget(
                "multi",
                "Multi",
                new AudioRoute(new AudioEndpointId("a")),
                new AudioRoute(new AudioEndpointId("b"))
            )
        );

        engine.Stop();

        Assert.Equal(2, outputFactory.Streams.Count);

        Assert.All(outputFactory.Streams, stream => Assert.True(stream.IsDisposed));

        Assert.Equal(AudioEngineState.Stopped, engine.State);
    }

    [Fact]
    public void Applying_equivalent_multi_route_target_is_idempotent()
    {
        var format = new AudioFormat(48_000, 2);

        var inputFactory = new FakeAudioInputStreamFactory(format);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory);

        AudioTarget CreateTarget() =>
            new("multi", "Multi", new AudioRoute(new AudioEndpointId("a")), new AudioRoute(new AudioEndpointId("b")));

        engine.Apply(CreateTarget());

        engine.Apply(CreateTarget());

        Assert.Equal(1, inputFactory.CreateCount);

        Assert.Equal(2, outputFactory.CreateCount);

        Assert.Equal(2, outputFactory.Streams.Count);
    }

    [Fact]
    public void One_route_target_still_behaves_normally()
    {
        var format = new AudioFormat(48_000, 2);

        var inputFactory = new FakeAudioInputStreamFactory(format, 1.0f, -1.0f);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory, blockFrameCapacity: 1);

        AudioEndpointId output = new("output");

        engine.Apply(new AudioTarget("single", "Single", new AudioRoute(output)));

        engine.ProcessNextBlock();

        Assert.Equal(1, inputFactory.CreateCount);

        Assert.Equal(1, outputFactory.CreateCount);

        Assert.Equal(new[] { 1.0f, -1.0f }, outputFactory.GetStream(output).GetWrittenSamples());
    }

    [Fact]
    public void Route_volume_and_mute_are_applied_independently()
    {
        var format = new AudioFormat(48_000, 1);

        var inputFactory = new FakeAudioInputStreamFactory(format, 1.0f);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory, blockFrameCapacity: 1);

        AudioEndpointId quiet = new("quiet");

        AudioEndpointId muted = new("muted");

        engine.Apply(
            new AudioTarget(
                "gain-routes",
                "Gain Routes",
                new AudioRoute(quiet, volume: 0.5f, processing: new AudioProcessingProfile(-6.0206f)),
                new AudioRoute(muted, muted: true)
            )
        );

        engine.ProcessNextBlock();

        Assert.InRange(Assert.Single(outputFactory.GetStream(quiet).GetWrittenSamples()), 0.24999f, 0.25001f);

        Assert.Equal(0.0f, Assert.Single(outputFactory.GetStream(muted).GetWrittenSamples()));
    }

    [Fact]
    public void Equivalent_target_recovers_optional_route_without_restarting_healthy_streams()
    {
        var format = new AudioFormat(48_000, 2);

        AudioEndpointId required = new("required");

        AudioEndpointId optional = new("optional");

        var inputFactory = new FakeAudioInputStreamFactory(format);

        var outputFactory = new FakeAudioOutputStreamFactory(failOnStartEndpoints: [optional]);

        using var engine = new AudioEngine(inputFactory, outputFactory);

        var target = new AudioTarget(
            "recover-optional",
            "Recover Optional",
            new AudioRoute(required),
            new AudioRoute(optional, AudioRouteRequirement.Optional)
        );

        engine.Apply(target);

        FakeAudioOutputStream requiredStream = outputFactory.GetStream(required);

        Assert.True(engine.NeedsRecovery);

        outputFactory.SetFailOnStart(optional, false);

        engine.Apply(target);

        Assert.False(engine.NeedsRecovery);

        Assert.Equal(1, inputFactory.CreateCount);

        Assert.Same(requiredStream, outputFactory.GetStream(required));

        Assert.False(requiredStream.IsDisposed);

        Assert.True(outputFactory.GetLatestStream(optional).IsStarted);
    }

    [Fact]
    public void Switching_one_to_two_to_one_routes_disposes_obsolete_graphs()
    {
        var format = new AudioFormat(48_000, 2);

        var inputFactory = new FakeAudioInputStreamFactory(format);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory);

        AudioEndpointId a = new("a");

        AudioEndpointId b = new("b");

        engine.Apply(new AudioTarget("single-a", "Single A", new AudioRoute(a)));

        FakeAudioOutputStream firstA = outputFactory.GetLatestStream(a);

        engine.Apply(new AudioTarget("dual", "Dual", new AudioRoute(a), new AudioRoute(b)));

        Assert.True(firstA.IsDisposed);

        FakeAudioOutputStream dualA = outputFactory.GetLatestStream(a);

        FakeAudioOutputStream dualB = outputFactory.GetLatestStream(b);

        engine.Apply(new AudioTarget("single-a-2", "Single A 2", new AudioRoute(a)));

        Assert.True(dualA.IsDisposed);

        Assert.True(dualB.IsDisposed);

        Assert.Equal(3, inputFactory.CreateCount);
    }

    [Fact]
    public void Required_route_failure_keeps_healthy_route_running_and_only_rebuilds_failed_route()
    {
        var format = new AudioFormat(48_000, 1);

        var inputFactory = new FakeAudioInputStreamFactory(format, 1.0f, 2.0f);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory, blockFrameCapacity: 1);

        AudioEndpointId healthy = new("healthy");

        AudioEndpointId recovering = new("recovering");

        var target = new AudioTarget(
            "timeline-recovery",
            "Timeline Recovery",
            new AudioRoute(healthy),
            new AudioRoute(recovering)
        );

        engine.Apply(target);

        FakeAudioOutputStream healthyStream = outputFactory.GetStream(healthy);

        FakeAudioOutputStream failedStream = outputFactory.GetStream(recovering);

        failedStream.FailNextWrite = true;

        Assert.Equal(1, engine.ProcessNextBlock());

        Assert.Equal(0, healthyStream.LastFramePosition);

        Assert.Equal(AudioEngineState.Running, engine.State);

        Assert.True(engine.NeedsRecovery);

        outputFactory.SetFailOnStart(recovering, true);

        engine.Apply(target);

        Assert.Equal(AudioEngineState.Running, engine.State);

        Assert.True(engine.NeedsRecovery);

        Assert.Same(healthyStream, outputFactory.GetLatestStream(healthy));

        Assert.False(healthyStream.IsDisposed);

        outputFactory.SetFailOnStart(recovering, false);

        engine.Apply(target);

        engine.ProcessNextBlock();

        Assert.Equal(1, healthyStream.LastFramePosition);

        Assert.Equal(1, inputFactory.CreateCount);

        Assert.Same(healthyStream, outputFactory.GetLatestStream(healthy));

        Assert.False(healthyStream.IsDisposed);

        Assert.True(failedStream.IsDisposed);

        Assert.Equal(0.0f, Assert.Single(outputFactory.GetLatestStream(recovering).GetWrittenSamples()));
    }

    [Fact]
    public void Recalibrate_changes_gain_live_without_recreating_any_output_or_touching_other_routes()
    {
        var format = new AudioFormat(48_000, 2);

        AudioEndpointId calibrated = new("calibrated");

        AudioEndpointId untouched = new("untouched");

        var inputFactory = new FakeAudioInputStreamFactory(format);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory);

        var target = new AudioTarget(
            "recalibrate-gain",
            "Recalibrate Gain",
            new AudioRoute(calibrated, processing: new AudioProcessingProfile(0.0f)),
            new AudioRoute(untouched)
        );

        engine.Apply(target);

        FakeAudioOutputStream calibratedStream = outputFactory.GetStream(calibrated);

        FakeAudioOutputStream untouchedStream = outputFactory.GetStream(untouched);

        engine.Recalibrate(
            calibrated,
            volume: 0.5f,
            muted: false,
            gainDb: -6.0206f,
            delay: TimeSpan.Zero,
            parametricEq: null
        );

        // No route was rebuilt: same two output streams, nothing disposed.
        Assert.Equal(2, outputFactory.CreateCount);
        Assert.Same(calibratedStream, outputFactory.GetStream(calibrated));
        Assert.Same(untouchedStream, outputFactory.GetStream(untouched));
        Assert.False(calibratedStream.IsDisposed);
        Assert.False(untouchedStream.IsDisposed);

        AudioRoute recalibrated = Assert.Single(engine.ActiveTarget!.Routes, route => route.EndpointId == calibrated);

        Assert.Equal(0.5f, recalibrated.Volume);
        Assert.Equal(-6.0206f, recalibrated.Processing.GainDb);

        AudioRouteHealth health = Assert.Single(engine.GetHealth().Routes, route => route.EndpointId == calibrated);

        Assert.Equal(-6.0206f, health.Processing.GainDb);
    }

    [Fact]
    public void Recalibrate_changing_delay_rebuilds_only_that_route()
    {
        var format = new AudioFormat(48_000, 2);

        AudioEndpointId calibrated = new("calibrated");

        AudioEndpointId untouched = new("untouched");

        var inputFactory = new FakeAudioInputStreamFactory(format);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory);

        var target = new AudioTarget(
            "recalibrate-delay",
            "Recalibrate Delay",
            new AudioRoute(calibrated),
            new AudioRoute(untouched)
        );

        engine.Apply(target);

        FakeAudioOutputStream originalCalibratedStream = outputFactory.GetStream(calibrated);

        FakeAudioOutputStream untouchedStream = outputFactory.GetStream(untouched);

        engine.Recalibrate(
            calibrated,
            volume: 1.0f,
            muted: false,
            gainDb: 0.0f,
            delay: TimeSpan.FromMilliseconds(40),
            parametricEq: null
        );

        // The recalibrated route alone was rebuilt -- one new output stream,
        // the old one for that same endpoint disposed -- while the untouched
        // route's stream is exactly the one that was already running.
        Assert.Equal(3, outputFactory.CreateCount);
        Assert.True(originalCalibratedStream.IsDisposed);

        FakeAudioOutputStream newCalibratedStream = outputFactory.GetLatestStream(calibrated);

        Assert.NotSame(originalCalibratedStream, newCalibratedStream);
        Assert.False(newCalibratedStream.IsDisposed);

        Assert.Same(untouchedStream, outputFactory.GetStream(untouched));
        Assert.False(untouchedStream.IsDisposed);

        AudioRoute recalibrated = Assert.Single(engine.ActiveTarget!.Routes, route => route.EndpointId == calibrated);

        Assert.Equal(TimeSpan.FromMilliseconds(40), recalibrated.Delay);
    }

    [Fact]
    public void Recalibrate_throws_when_no_target_is_running()
    {
        var inputFactory = new FakeAudioInputStreamFactory(new AudioFormat(48_000, 2));

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory);

        Assert.Throws<InvalidOperationException>(() =>
            engine.Recalibrate(new AudioEndpointId("headphones"), 1.0f, false, 0.0f, TimeSpan.Zero, null)
        );
    }

    [Fact]
    public void Recalibrate_throws_for_an_endpoint_the_running_target_does_not_use()
    {
        var format = new AudioFormat(48_000, 2);

        var inputFactory = new FakeAudioInputStreamFactory(format);

        var outputFactory = new FakeAudioOutputStreamFactory();

        using var engine = new AudioEngine(inputFactory, outputFactory);

        engine.Apply(CreateTarget("headphones"));

        Assert.Throws<InvalidOperationException>(() =>
            engine.Recalibrate(new AudioEndpointId("some-other-endpoint"), 1.0f, false, 0.0f, TimeSpan.Zero, null)
        );
    }
}

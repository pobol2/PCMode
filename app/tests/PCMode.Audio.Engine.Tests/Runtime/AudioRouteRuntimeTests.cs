using PCMode.Audio.Core.Buffers;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Core.Processing;
using PCMode.Audio.Engine.Runtime;
using PCMode.Audio.Engine.Tests.Fakes;
using PCMode.Audio.Engine.Timing;
using Xunit;

namespace PCMode.Audio.Engine.Tests.Runtime;

public sealed class AudioRouteRuntimeTests
{
    [Fact]
    public void Process_applies_route_processing_before_output()
    {
        var format = new AudioFormat(48_000, 2);

        var output = new FakeAudioOutputStream(format);

        var route = new AudioRoute(new AudioEndpointId("output"));

        var gain = new GainProcessor(-6.0206f);

        var parametricEq = new ParametricEqProcessor(ParametricEqProfile.Flat, format);

        var processing = new AudioProcessingChain(parametricEq, gain);

        using var runtime = new AudioRouteRuntime(route, output, processing, gain, parametricEq, blockFrameCapacity: 2);

        runtime.Start();

        float[] sourceSamples = [1.0f, -1.0f, 0.5f, -0.5f];

        var source = new AudioBlock(sourceSamples, format, framePosition: 100);

        runtime.Process(source);

        float[] written = output.GetWrittenSamples();

        Assert.Equal(4, written.Length);

        Assert.InRange(written[0], 0.49999f, 0.50001f);

        Assert.InRange(written[1], -0.50001f, -0.49999f);

        Assert.InRange(written[2], 0.24999f, 0.25001f);

        Assert.InRange(written[3], -0.25001f, -0.24999f);

        Assert.Equal(100, output.LastFramePosition);

        Assert.Equal(1.0f, sourceSamples[0]);
    }

    [Fact]
    public void Configured_delay_is_converted_to_frames_and_samples()
    {
        var format = new AudioFormat(sampleRate: 48_000, channelCount: 2);

        var output = new FakeAudioOutputStream(format);

        var route = new AudioRoute(new AudioEndpointId("output"), delay: TimeSpan.FromMilliseconds(30));

        using var runtime = new AudioRouteRuntime(
            route,
            output,
            new AudioProcessingChain(),
            new GainProcessor(0f),
            new ParametricEqProcessor(ParametricEqProfile.Flat, format),
            blockFrameCapacity: 480
        );

        Assert.Equal(1_440, runtime.DelayFrameCount);

        Assert.Equal(2_880, runtime.DelaySampleCount);

        int maximumOutputFrames = Resampler.GetMaximumOutputFrameCount(480, RouteSynchronizer.MaximumResyncRatio);

        Assert.Equal(1_440 + maximumOutputFrames, runtime.BufferCapacityFrames);
    }

    [Fact]
    public void Configured_delay_produces_expected_frame_offset()
    {
        var format = new AudioFormat(sampleRate: 1_000, channelCount: 1);

        var output = new FakeAudioOutputStream(format);

        var route = new AudioRoute(new AudioEndpointId("output"), delay: TimeSpan.FromMilliseconds(2));

        using var runtime = new AudioRouteRuntime(
            route,
            output,
            new AudioProcessingChain(),
            new GainProcessor(0f),
            new ParametricEqProcessor(ParametricEqProfile.Flat, format),
            blockFrameCapacity: 4
        );

        runtime.Start();

        float[] sourceSamples = [1.0f, 2.0f, 3.0f, 4.0f];

        runtime.Process(new AudioBlock(sourceSamples, format));

        Assert.Equal(new[] { 0.0f, 0.0f, 1.0f, 2.0f }, output.GetWrittenSamples());

        Assert.Equal(2, runtime.BufferedFrameCount);

        Assert.Equal(0, runtime.UnderrunCount);
    }

    [Fact]
    public void Zero_delay_outputs_current_block_without_added_silence()
    {
        var format = new AudioFormat(sampleRate: 1_000, channelCount: 1);

        var output = new FakeAudioOutputStream(format);

        var route = new AudioRoute(new AudioEndpointId("output"), delay: TimeSpan.Zero);

        using var runtime = new AudioRouteRuntime(
            route,
            output,
            new AudioProcessingChain(),
            new GainProcessor(0f),
            new ParametricEqProcessor(ParametricEqProfile.Flat, format),
            blockFrameCapacity: 4
        );

        runtime.Start();

        runtime.Process(new AudioBlock(new float[] { 1.0f, 2.0f, 3.0f, 4.0f }, format));

        Assert.Equal(new[] { 1.0f, 2.0f, 3.0f, 4.0f }, output.GetWrittenSamples());

        Assert.Equal(0, runtime.BufferedFrameCount);

        Assert.Equal(0, runtime.UnderrunCount);
    }

    [Fact]
    public void ResetBuffer_clears_prior_audio_and_restores_configured_delay()
    {
        var format = new AudioFormat(sampleRate: 1_000, channelCount: 1);

        var output = new FakeAudioOutputStream(format);

        var route = new AudioRoute(new AudioEndpointId("output"), delay: TimeSpan.FromMilliseconds(2));

        using var runtime = new AudioRouteRuntime(
            route,
            output,
            new AudioProcessingChain(),
            new GainProcessor(0f),
            new ParametricEqProcessor(ParametricEqProfile.Flat, format),
            blockFrameCapacity: 4
        );

        runtime.Start();

        runtime.Process(new AudioBlock(new float[] { 1.0f, 2.0f, 3.0f, 4.0f }, format));

        runtime.ResetBuffer();

        runtime.Process(new AudioBlock(new float[] { 9.0f, 10.0f, 11.0f, 12.0f }, format, framePosition: 4));

        float[] written = output.GetWrittenSamples();

        Assert.Equal(new[] { 0.0f, 0.0f, 1.0f, 2.0f, 0.0f, 0.0f, 9.0f, 10.0f }, written);

        Assert.DoesNotContain(3.0f, written[4..]);

        Assert.DoesNotContain(4.0f, written[4..]);
    }

    [Fact]
    public void Restart_resets_dsp_history_and_fades_in_from_silence()
    {
        var format = new AudioFormat(sampleRate: 48_000, channelCount: 1);

        var output = new FakeAudioOutputStream(format);

        var parametricEq = new ParametricEqProcessor(
            new ParametricEqProfile(0.0f, new EqBand(EqFilterType.Peak, 1_000.0f, 12.0f, 0.7f)),
            format
        );

        var gain = new GainProcessor(0.0f);

        using var runtime = new AudioRouteRuntime(
            new AudioRoute(new AudioEndpointId("output")),
            output,
            new AudioProcessingChain(parametricEq, gain),
            gain,
            parametricEq,
            blockFrameCapacity: 4
        );

        runtime.Start();

        runtime.Process(new AudioBlock(new float[] { 1.0f, 0.0f, 0.0f, 0.0f }, format));

        runtime.Stop();
        runtime.Start();

        runtime.Process(new AudioBlock(new float[] { 0.0f, 0.0f, 0.0f, 0.0f }, format, framePosition: 4));

        Assert.All(output.GetWrittenSamples()[4..], sample => Assert.Equal(0.0f, sample));

        runtime.Stop();
        runtime.Start();

        runtime.Process(new AudioBlock(new float[] { 1.0f, 1.0f, 1.0f, 1.0f }, format, framePosition: 8));

        float[] restarted = output.GetWrittenSamples()[8..];

        Assert.Equal(0.0f, restarted[0]);

        Assert.True(restarted[1] > restarted[0]);

        Assert.InRange(restarted[1], 0.0f, 0.02f);
    }

    [Fact]
    public void A_large_wall_clock_gap_between_calls_resets_pipeline_state_without_re_fading_amplitude()
    {
        var format = new AudioFormat(sampleRate: 1_000, channelCount: 1);

        var output = new FakeAudioOutputStream(format);

        using var runtime = new AudioRouteRuntime(
            new AudioRoute(new AudioEndpointId("output")),
            output,
            new AudioProcessingChain(),
            new GainProcessor(0f),
            new ParametricEqProcessor(ParametricEqProfile.Flat, format),
            blockFrameCapacity: 4
        );

        runtime.Start();

        runtime.Process(new AudioBlock(new float[] { 1.0f, 1.0f, 1.0f, 1.0f }, format));

        Assert.Equal(0, runtime.StallRecoveryCount);

        // Simulates the render loop itself having been blocked for far
        // longer than this 4ms block -- e.g. inside a slow physical output
        // write -- entirely without going through Stop()/Start(). This is
        // exactly the shape real-hardware testing found during a display
        // topology change: no exception, no reported underrun, IsOperational
        // stays true throughout, yet real time passed with nothing written.
        Thread.Sleep(200);

        runtime.Process(new AudioBlock(new float[] { 1.0f, 1.0f, 1.0f, 1.0f }, format, framePosition: 4));

        Assert.Equal(1, runtime.StallRecoveryCount);

        // Real-hardware testing found this route's own next block is
        // normally an ordinary, uninterrupted continuation of the source
        // waveform -- nothing about this route's own processing was ever
        // paused, only the physical write several layers below it. Fading
        // this block down to silence anyway, as an earlier version of this
        // fix did, forced a fresh discontinuity into audio that was already
        // fine. The block that actually needs fading is whichever one is
        // in flight inside the output stream's own Write() when the wait
        // happens (see WasapiRenderOutput's own recovery fade) -- this
        // route only needs to stop trusting its resampler/synchronizer's
        // pre-stall drift tracking, which BufferedFrameCount returning to
        // its steady-state value here confirms actually happened.
        Assert.All(output.GetWrittenSamples()[4..], sample => Assert.Equal(1.0f, sample));

        Assert.Equal(0, runtime.BufferedFrameCount);
    }

    [Fact]
    public void Excessive_delay_is_rejected_clearly()
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AudioRoute(
                new AudioEndpointId("output"),
                delay: TimeSpan.FromMilliseconds(AudioRoute.MaximumDelayMilliseconds + 1)
            )
        );

        Assert.Contains(AudioRoute.MaximumDelayMilliseconds.ToString(), ex.Message);
    }
}

using System.Diagnostics;
using PCMode.Audio.Client;
using PCMode.Audio.Core.Models;
using PCMode.Audio.DriverClient.Control;
using PCMode.Audio.DriverClient.Diagnostics;
using PCMode.Audio.Engine.Contracts;
using PCMode.Audio.Core.Buffers;
using PCMode.Audio.Protocol.Messages;
using PCMode.Audio.Windows.Endpoints;
using PCMode.Audio.Windows.Streams;
using Xunit;

namespace PCMode.Audio.HardwareTests;

public sealed class VirtualEndpointHardwareTests
{
    [HardwareFact]
    public async Task
        Windows_playback_reaches_host_and_physical_output()
    {
        string hostPath =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_HOST_PATH");

        string outputId =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_OUTPUT_ID");

        string pipeName =
            $"PCMode.Audio.Hardware.{Guid.NewGuid():N}";

        var client =
            new AudioHostClient(
                pipeName,
                connectTimeoutMilliseconds:
                    10000);

        var launcher =
            new AudioHostLauncher(
                hostPath,
                pipeName);

        using Process process =
            await launcher
                .LaunchAndWaitUntilReadyAsync(
                    client,
                    TimeSpan.FromSeconds(
                        10));

        try
        {
            using var control =
                new VirtualEndpointControlClient();

            DriverStatus before =
                control.GetStatus();

            Assert.True(
                before.Available);

            var target =
                new AudioTarget(
                    "virtual-endpoint-hardware",
                    "Virtual Endpoint Hardware",
                    new AudioRoute(
                        new AudioEndpointId(
                            outputId)));

            await client
                .ApplyTargetAsync(
                    target);

            //
            // Play a known tone into the virtual endpoint and measure what
            // actually comes out of the physical device.
            //
            // Counters alone cannot carry this assertion. The driver's render
            // timer free-runs once the stream is running, so PacketsRendered
            // climbs whether the buffer holds audio or silence -- a test that
            // watches only that number passes against a completely silent
            // system.
            //
            using var tone =
                new ToneSource(
                    ToneAmplitude);

            tone.Start();

            //
            // The contract this test exists to prove: PCMode.Audio.DriverClient
            // fixes its accepted format from the driver's single nominal
            // PreferredFormat (see UserModeTransport.cpp's FillPreferredFormat)
            // and rejects any packet at a different sample rate (see
            // DriverAudioInput.ConvertPacket and DriverClientTests). The render
            // circuit must therefore never let Windows negotiate anything else
            // for ordinary shared-mode playback -- which is exactly what
            // IAudioClient.GetMixFormat reports here, the same format an
            // ordinary application opened.
            //
            Assert.Equal(
                48000,
                tone.Format.SampleRate);

            // Let the engine reach steady state before measuring.
            await Task.Delay(
                TimeSpan.FromSeconds(
                    2));

            ToneMeasurement measured =
                MeasureLoopback(
                    new AudioEndpointId(
                        outputId),
                    TimeSpan.FromSeconds(
                        5));

            tone.Stop();

            AudioHostStatus hostStatus =
                await client
                    .GetStatusAsync();

            AudioEngineHealth health =
                await client
                    .GetHealthAsync();

            DriverStatus after =
                control.GetStatus();

            Assert.Equal(
                AudioEngineRuntimeState.Running,
                hostStatus.EngineState);

            Assert.True(
                after.Available);

            Assert.True(
                after.PcmConsumerConnected);

            Assert.True(
                after.PacketsRendered >
                before.PacketsRendered,
                "No Windows PCM reached the PCMode Audio driver during the test.");

            Assert.True(
                measured.TotalFrames > 0,
                "Loopback capture of the physical output returned no frames at all.");

            Assert.True(
                measured.Peak > SilenceFloor,
                $"The physical output stayed silent (peak {measured.Peak:F5}). Audio never " +
                "made it through the virtual endpoint, the driver transport, the Host, or " +
                "the output stream.");

            Assert.True(
                measured.NonSilentFrames >
                    measured.TotalFrames * 0.9,
                $"Only {measured.NonSilentFrames} of {measured.TotalFrames} frames carried " +
                "audio, so playback reached the physical output but was heavily gapped.");

            //
            // Gain-independent shape check: RMS/peak is 1/sqrt(2) for a sine
            // regardless of how the system volume scales it. It separates the
            // tone actually arriving from noise, DC, or a clipped mess that
            // would still clear the non-silence bar above.
            //
            double crest =
                measured.Rms /
                measured.Peak;

            Assert.True(
                crest > 0.60 &&
                crest < 0.78,
                $"The physical output carried signal, but not the tone that was played: " +
                $"RMS/peak was {crest:F3}, expected about {1.0 / Math.Sqrt(2.0):F3} for a sine.");

            AudioRouteHealth route =
                Assert.Single(
                    health.Routes);

            Assert.DoesNotContain(
                route.State,
                new[]
                {
                    AudioRouteHealthState
                        .Unavailable,

                    AudioRouteHealthState
                        .Failed
                });
        }
        finally
        {
            try
            {
                await client
                    .ShutdownAsync();
            }
            catch
            {
            }

            process.WaitForExit(
                5000);

            KillIfStillRunning(
                process);
        }
    }

    [HardwareFact]
    public async Task
        Display_topology_change_does_not_spike_unaffected_output()
    {
        string hostPath =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_HOST_PATH");

        string outputId =
            ResolveTopologyTestOutputId();

        string pipeName =
            $"PCMode.Audio.DisplayTopology.{Guid.NewGuid():N}";

        var client =
            new AudioHostClient(
                pipeName,
                connectTimeoutMilliseconds:
                    10000);

        using Process process =
            await new AudioHostLauncher(
                    hostPath,
                    pipeName)
                .LaunchAndWaitUntilReadyAsync(
                    client,
                    TimeSpan.FromSeconds(10));

        try
        {
            using var control =
                new VirtualEndpointControlClient();

            await client.ApplyTargetAsync(
                new AudioTarget(
                    "display-topology-hardware",
                    "Display Topology Hardware",
                    new AudioRoute(
                        new AudioEndpointId(
                            outputId))));

            using var tone =
                new ToneSource(
                    ToneAmplitude);

            tone.Start();

            await Task.Delay(
                TimeSpan.FromSeconds(2));

            DriverStatus beforeTransition =
                control.GetStatus();

            ToneMeasurement baseline =
                MeasureLoopback(
                    new AudioEndpointId(
                        outputId),
                    TimeSpan.FromSeconds(3));

            Assert.True(
                baseline.Peak >
                SilenceFloor,
                "The baseline tone did not reach the physical output.");

            // A "before" reading for the same route-health fields printed
            // after the transition, so a jump in ResyncCount/ClockOffsetFrames
            // there can be judged against what this machine's normal,
            // undisturbed steady state actually looks like rather than an
            // assumed baseline of zero.
            AudioEngineHealth baselineHealth =
                await client.GetHealthAsync();

            AudioRouteHealth baselineRoute =
                Assert.Single(
                    baselineHealth.Routes);

            Console.WriteLine(
                $"Route health after baseline: state={baselineRoute.State}, " +
                $"underrun-count={baselineRoute.UnderrunCount}, " +
                $"resync-count={baselineRoute.ResyncCount}, " +
                $"clock-offset-frames={baselineRoute.ClockOffsetFrames:F2}, " +
                $"drift-correction-ppm={baselineRoute.DriftCorrectionPpm:F2}, " +
                $"stall-recovery-count={baselineRoute.StallRecoveryCount}, " +
                $"output-dry-count={baselineRoute.OutputEndpointDryCount}, " +
                $"output-recovery-fades={baselineRoute.OutputRecoveryFadeCount}, " +
                $"longest-submit-gap={baselineRoute.LongestOutputSubmitGapMilliseconds:F1}ms.");

            Console.WriteLine(
                "Change Windows display topology now (connect/disconnect a display or press Win+P). " +
                "Leave the PCMODE_AUDIO_OUTPUT_ID endpoint connected; capture runs for 20 seconds.");

            ToneMeasurement transition =
                MeasureLoopback(
                    new AudioEndpointId(
                        outputId),
                    TimeSpan.FromSeconds(20));

            DriverStatus afterTransition =
                control.GetStatus();

            // Captured immediately after the transition window, before any
            // assertion can throw and skip past it -- a failed or restarted
            // route (UnderrunCount/ResyncCount climbing, State leaving
            // Healthy, LastFailure set) points at the Host's own route
            // recovery path; a route that stayed Healthy throughout with
            // flat counters means the interruption never became visible to
            // this code at all, i.e. it happened in Windows' own audio
            // engine or the physical device, not in anything PCMode wrote.
            AudioEngineHealth health =
                await client.GetHealthAsync();

            AudioRouteHealth route =
                Assert.Single(
                    health.Routes);

            Console.WriteLine(
                $"Route health after transition: state={route.State}, " +
                $"endpoint-state={route.EndpointState}, " +
                $"underrun-count={route.UnderrunCount}, " +
                $"resync-count={route.ResyncCount}, " +
                $"is-resynchronizing={route.IsResynchronizing}, " +
                $"clock-offset-frames={route.ClockOffsetFrames:F2}, " +
                $"drift-correction-ppm={route.DriftCorrectionPpm:F2}, " +
                $"resample-ratio={route.ResampleRatio:F6}, " +
                $"last-failure={route.LastFailure?.Message ?? "(none)"}.");

            // The decisive question: did AudioRouteRuntime.Process ever
            // actually observe an abnormal wall-clock gap between calls and
            // re-arm its fade-in ramp? A nonzero StallRecoveryCount means the
            // fix engaged -- if the waveform-jump assertion still fails, the
            // ramp wasn't enough and needs a closer look. A zero count means
            // the interruption never showed up as a gap between Process
            // calls at all -- i.e. it is happening somewhere this code
            // cannot see (most likely inside Windows' own audio engine, after
            // a successful WASAPI write), and no route-level fix can reach
            // it.
            Console.WriteLine(
                $"Route stall recovery: count={route.StallRecoveryCount}, " +
                $"longest-process-gap={route.LongestProcessGapMilliseconds:F1}ms.");

            // The output stream's own account, which answers a different
            // question from the route's and is the one that actually
            // corresponds to silence in the captured waveform. output-dry
            // counts the times WASAPI reported the endpoint with nothing
            // left queued -- i.e. the device was being fed silence in place
            // of this route's audio -- and recovery-fades counts the times
            // the stream therefore ramped the next chunk in instead of
            // handing over full amplitude at an arbitrary phase.
            //
            // Read against out-of-silence below, these settle what three
            // earlier attempts could only guess at. Nonzero dry count with a
            // matching fade count and a small out-of-silence means the fix
            // engaged and worked. Nonzero dry count with out-of-silence
            // still near full amplitude means the ramp is reaching the
            // device but is not what the capture is picking up. A dry count
            // of zero, with out-of-silence still high, means the silence in
            // the capture was never a gap in what this Host submitted, and
            // no fade at this layer can ever reach it. Subtract the baseline
            // figures printed above: these counters are cumulative over the
            // route's life, not per window.
            Console.WriteLine(
                $"Output stream: dry-count={route.OutputEndpointDryCount}, " +
                $"recovery-fades={route.OutputRecoveryFadeCount}, " +
                $"longest-submit-gap={route.LongestOutputSubmitGapMilliseconds:F1}ms, " +
                $"longest-write={route.LongestOutputWriteMilliseconds:F1}ms, " +
                $"longest-buffer-wait={route.LongestOutputBufferWaitMilliseconds:F1}ms " +
                $"(baseline dry-count={baselineRoute.OutputEndpointDryCount}, " +
                $"recovery-fades={baselineRoute.OutputRecoveryFadeCount}, " +
                $"longest-submit-gap={baselineRoute.LongestOutputSubmitGapMilliseconds:F1}ms, " +
                $"longest-write={baselineRoute.LongestOutputWriteMilliseconds:F1}ms, " +
                $"longest-buffer-wait=" +
                $"{baselineRoute.LongestOutputBufferWaitMilliseconds:F1}ms).");

            // Whether the driver's own transport ring overflowed because the
            // Host wasn't reading fast enough -- distinguishes a Host-thread
            // stall wide enough to affect the input side too, from a stall
            // confined to the physical output write.
            Console.WriteLine(
                $"Driver packets dropped: before={beforeTransition.PacketsDropped}, " +
                $"after={afterTransition.PacketsDropped}, " +
                $"delta={afterTransition.PacketsDropped - beforeTransition.PacketsDropped}.");

            // How many separate silent stretches occurred, not just the
            // longest one -- one big gap and many small ones look identical
            // in the aggregate stats above but point at different causes.
            string gapSummary =
                transition.SilentGapFrameCounts.Count == 0
                    ? "none"
                    : string.Join(
                        ", ",
                        transition.SilentGapFrameCounts
                            .OrderByDescending(
                                frames =>
                                    frames)
                            .Take(10)
                            .Select(
                                frames =>
                                    $"{frames * 1000.0 / transition.SampleRate:F1}ms"));

            Console.WriteLine(
                $"Silent gaps during transition: count={transition.SilentGapFrameCounts.Count}, " +
                $"total={transition.SilentGapFrameCounts.Sum() * 1000.0 / transition.SampleRate:F1}ms, " +
                $"longest 10={gapSummary}.");

            Console.WriteLine(
                $"Baseline: peak={baseline.Peak:F5}, rms={baseline.Rms:F5}, " +
                $"max-step={baseline.MaximumSampleStep:F5}, " +
                $"longest-silence={baseline.LongestSilentRunFrames * 1000.0 / baseline.SampleRate:F1}ms, " +
                $"longest-read-gap={baseline.LongestReadGap.TotalMilliseconds:F1}ms, " +
                $"into-silence={baseline.MaximumStepIntoSilence:F5}, " +
                $"out-of-silence={baseline.MaximumStepOutOfSilence:F5}.");

            Console.WriteLine(
                $"Transition: peak={transition.Peak:F5}, rms={transition.Rms:F5}, " +
                $"max-step={transition.MaximumSampleStep:F5}, " +
                $"longest-silence={transition.LongestSilentRunFrames * 1000.0 / transition.SampleRate:F1}ms, " +
                $"longest-read-gap={transition.LongestReadGap.TotalMilliseconds:F1}ms.");

            // Which boundary the worst jump actually happened at. The
            // route-level fade-in only ever touches the resume side (silence
            // -> audio); if step-into-silence is the larger of the two, that
            // fix cannot be the answer to the failing max-step assertion no
            // matter how it's tuned, because it never runs on that boundary.
            Console.WriteLine(
                $"Step by boundary: into-silence={transition.MaximumStepIntoSilence:F5}, " +
                $"out-of-silence={transition.MaximumStepOutOfSilence:F5}.");

            // Both boundary figures sitting below the overall maximum means
            // the worst jump happened between two audible samples, so
            // neither the fade-in nor anything else that acts on silence can
            // be what it needs. These are the samples either side of it.
            // Whether the jumps being measured are even present in what the
            // endpoint rendered. This capture concatenates WASAPI packets
            // without regard to the data-discontinuity flag, so audio the
            // capture itself lost shows up as an instantaneous phase jump
            // with clean, full-amplitude signal either side of it -- exactly
            // the shape printed below -- and is indistinguishable from a
            // real glitch unless the flag and the seam positions are
            // checked. Discontinuities during baseline, when nothing is
            // being disturbed, are the control.
            Console.WriteLine(
                $"Capture integrity: discontinuity-packets={transition.CaptureDiscontinuityCount} " +
                $"(baseline {baseline.CaptureDiscontinuityCount}), " +
                $"worst-step-on-packet-seam={transition.MaximumStepAtPacketBoundary}, " +
                $"worst-out-of-silence-on-packet-seam=" +
                $"{transition.MaximumStepOutOfSilenceAtPacketBoundary}, " +
                $"seams-excluded-from-step-analysis={transition.ExcludedSeamCount} " +
                $"(baseline {baseline.ExcludedSeamCount}), " +
                $"position-gaps={transition.CapturePositionGapCount} " +
                $"(baseline {baseline.CapturePositionGapCount}), " +
                $"frames-lost-by-capture=" +
                $"{transition.CaptureMissingFrameCount * 1000.0 / transition.SampleRate:F1}ms " +
                $"(baseline " +
                $"{baseline.CaptureMissingFrameCount * 1000.0 / baseline.SampleRate:F1}ms).");

            Console.WriteLine(
                $"Worst step at {transition.MaximumStepSeconds:F3}s into the " +
                $"capture, surrounding samples: " +
                string.Join(
                    " ",
                    transition.MaximumStepNeighbourhood.Select(
                        sample =>
                            sample.ToString("+0.000;-0.000"))));

            // How many times the driver's own render timer fell behind real
            // time and signaled the Host to re-prime (see RenderStream.cpp's
            // SkipMissedRenderSlices) during exactly this baseline/transition
            // window -- not the whole process lifetime -- so a nonzero delta
            // here during the transition, and not during baseline, points at
            // the driver's own timer rather than the physical output route.
            Console.WriteLine(
                $"Driver discontinuity signals: before={beforeTransition.DiscontinuityCount}, " +
                $"after={afterTransition.DiscontinuityCount}, " +
                $"delta={afterTransition.DiscontinuityCount - beforeTransition.DiscontinuityCount}.");

            tone.Stop();

            // The producer's own account, which is the half the Host cannot
            // see. Read against the Host figures above: a submit gap here
            // close to the Host's longest-process-gap means the tone simply
            // stopped being produced and every silent frame downstream is
            // correct behaviour -- there was nothing to play. A small gap
            // here with a large one there means the audio was produced but
            // did not reach the route, which puts the loss in the driver
            // transport between them.
            Console.WriteLine(
                $"Tone source: longest-submit-gap=" +
                $"{tone.Diagnostics.LongestSubmitGapMilliseconds:F1}ms, " +
                $"virtual-endpoint-dry-count={tone.Diagnostics.EndpointDryCount}, " +
                $"recovery-fades={tone.Diagnostics.RecoveryFadeCount}.");

            // Stepping around lost frames is only legitimate while the
            // capture remains a faithful account of the endpoint. Past a
            // point it is not a recording with a few seams in it any more,
            // and every figure derived from it -- silence, steps, the lot --
            // is describing the recording rather than the output. Fail
            // loudly there instead of quietly excluding more and more.
            double captureLossMilliseconds =
                transition.CaptureMissingFrameCount *
                1000.0 /
                transition.SampleRate;

            Assert.True(
                captureLossMilliseconds <=
                transition.TotalFrames *
                1000.0 /
                transition.SampleRate *
                0.01,
                $"The loopback capture itself lost {captureLossMilliseconds:F1}ms of " +
                $"audio, too much for the waveform analysis below to describe what " +
                $"the endpoint actually rendered.");

            float maximumExpectedPeak =
                Math.Min(
                    1.0f,
                    baseline.Peak * 2.0f +
                    0.01f);

            Assert.True(
                transition.Peak <=
                maximumExpectedPeak,
                $"Display topology change produced a peak of {transition.Peak:F5}; " +
                $"baseline was {baseline.Peak:F5} and the allowed ceiling was {maximumExpectedPeak:F5}.");

            Assert.True(
                transition.NonSilentFrames >
                transition.TotalFrames * 0.85,
                "The unaffected output was heavily interrupted during display endpoint churn.");

            float maximumExpectedStep =
                baseline.MaximumSampleStep *
                    3.0f +
                0.005f;

            Assert.True(
                transition.MaximumSampleStep <=
                maximumExpectedStep,
                $"The output waveform jumped by {transition.MaximumSampleStep:F5}; " +
                $"baseline maximum was {baseline.MaximumSampleStep:F5} and the allowed ceiling was {maximumExpectedStep:F5}.");

            long maximumSilentRunFrames =
                Math.Max(
                    transition.SampleRate /
                        10,
                    baseline.LongestSilentRunFrames *
                        3 +
                    transition.SampleRate /
                        100);

            Assert.True(
                transition.LongestSilentRunFrames <=
                maximumSilentRunFrames,
                $"The output was continuously silent for " +
                $"{transition.LongestSilentRunFrames * 1000.0 / transition.SampleRate:F1}ms; " +
                $"the allowed ceiling was {maximumSilentRunFrames * 1000.0 / transition.SampleRate:F1}ms.");

            TimeSpan maximumReadGap =
                TimeSpan.FromMilliseconds(
                    Math.Max(
                        100,
                        baseline.LongestReadGap.TotalMilliseconds *
                            3 +
                        20));

            Assert.True(
                transition.LongestReadGap <=
                maximumReadGap,
                $"Loopback delivery stalled for {transition.LongestReadGap.TotalMilliseconds:F1}ms; " +
                $"the allowed ceiling was {maximumReadGap.TotalMilliseconds:F1}ms.");

            Assert.DoesNotContain(
                route.State,
                new[]
                {
                    AudioRouteHealthState.Unavailable,
                    AudioRouteHealthState.Failed
                });
        }
        finally
        {
            try
            {
                await client.ShutdownAsync();
            }
            catch
            {
            }

            process.WaitForExit(
                5000);

            KillIfStillRunning(
                process);
        }
    }

    private const float ToneAmplitude =
        0.2f;

    /// <summary>
    /// Below this the capture is indistinguishable from a silent device.
    /// Well under <see cref="ToneAmplitude"/> so that system volume, or a
    /// route configured quieter than unity, does not fail the test.
    /// </summary>
    private const float SilenceFloor =
        0.005f;

    private readonly record struct
        ToneMeasurement(
            long TotalFrames,
            long NonSilentFrames,
            float Peak,
            double Rms,
            float MaximumSampleStep,
            float MaximumStepIntoSilence,
            float MaximumStepOutOfSilence,
            double MaximumStepSeconds,
            IReadOnlyList<float> MaximumStepNeighbourhood,
            bool MaximumStepAtPacketBoundary,
            bool MaximumStepOutOfSilenceAtPacketBoundary,
            long CaptureDiscontinuityCount,
            long CapturePositionGapCount,
            long CaptureMissingFrameCount,
            long ExcludedSeamCount,
            long LongestSilentRunFrames,
            TimeSpan LongestReadGap,
            int SampleRate,
            IReadOnlyList<long> SilentGapFrameCounts);

    /// <summary>
    /// A 440 Hz sine played into the PCMode virtual endpoint the same way any
    /// ordinary shared-mode application would play it.
    /// </summary>
    private sealed class ToneSource :
        IDisposable
    {
        private readonly float _amplitude;

        private readonly CancellationTokenSource
            _stop = new();

        private Thread? _thread;

        private Exception? _failure;

        private readonly ManualResetEventSlim
            _ready = new(false);

        public ToneSource(
            float amplitude)
        {
            _amplitude = amplitude;
        }

        /// <summary>
        /// The format Windows actually negotiated for this shared-mode stream,
        /// valid once <see cref="Start"/> returns.
        /// </summary>
        public AudioFormat Format { get; private set; }

        /// <summary>
        /// What this tone's own client saw while feeding the virtual
        /// endpoint, valid once <see cref="Stop"/> returns. The Host's route
        /// can only render audio that reached it, so a silent stretch at the
        /// physical output means either that this producer stopped feeding
        /// the virtual endpoint or that what it fed never arrived -- and
        /// nothing downstream of the driver can tell those apart. This can:
        /// a large <c>LongestSubmitGapMilliseconds</c> here says the sound
        /// stopped at the source, before any of the Host's routing was
        /// involved, and a large one with <c>EndpointDryCount</c> also
        /// raised says this thread was the part that stalled rather than the
        /// driver having stopped consuming from it.
        /// </summary>
        public AudioOutputDiagnostics Diagnostics { get; private set; }

        public void Start()
        {
            CoreAudioEndpointSnapshot endpoint =
                new CoreAudioEndpointCatalog()
                    .GetActiveRenderEndpoints()
                    .FirstOrDefault(e =>
                        e.FriendlyName.Contains(
                            "PCMode",
                            StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    "The PCMode virtual endpoint is not present as an active render device.");

            _thread = new Thread(() =>
            {
                try
                {
                    using var output =
                        new WasapiRenderOutput(
                            endpoint.Id,
                            endpoint.Format);

                    output.Start();

                    Format =
                        output.Format;

                    _ready.Set();

                    int rate =
                        output.Format.SampleRate;

                    int channels =
                        output.Format.ChannelCount;

                    int framesPerBlock =
                        rate / 100;

                    var samples =
                        new float[
                            framesPerBlock *
                            channels];

                    double phase = 0;

                    double step =
                        2 * Math.PI * 440.0 / rate;

                    while (!_stop.IsCancellationRequested)
                    {
                        for (int f = 0; f < framesPerBlock; f++)
                        {
                            float v =
                                (float)(Math.Sin(phase) *
                                    _amplitude);

                            phase += step;

                            for (int c = 0; c < channels; c++)
                            {
                                samples[f * channels + c] = v;
                            }
                        }

                        output.Write(
                            new AudioBlock(
                                samples,
                                output.Format));
                    }

                    Diagnostics =
                        output.GetDiagnostics();

                    output.Stop();
                }
                catch (Exception ex)
                {
                    _failure = ex;
                    _ready.Set();
                }
            })
            {
                IsBackground = true,
                Name = "hardware-test-tone"
            };

            _thread.Start();

            if (!_ready.Wait(
                    TimeSpan.FromSeconds(10)))
            {
                throw new InvalidOperationException(
                    "The tone source did not start streaming to the virtual endpoint within 10s.");
            }

            if (_failure is not null)
            {
                throw new InvalidOperationException(
                    "The tone source could not open the virtual endpoint.",
                    _failure);
            }
        }

        public void Stop()
        {
            _stop.Cancel();
            _thread?.Join(2000);
        }

        public void Dispose()
        {
            Stop();
            _stop.Dispose();
            _ready.Dispose();
        }
    }

    private static ToneMeasurement
        MeasureLoopback(
            AudioEndpointId outputId,
            TimeSpan duration)
    {
        using var loopback =
            new WasapiLoopbackInput(
                outputId);

        loopback.Start();

        var buffer =
            new float[
                4800 *
                loopback.Format.ChannelCount];

        long totalFrames = 0;
        long nonSilentFrames = 0;
        float peak = 0;
        double sumOfSquares = 0;
        long sampleCount = 0;
        float maximumSampleStep = 0;
        long silentRunFrames = 0;
        long longestSilentRunFrames = 0;
        TimeSpan longestReadGap =
            TimeSpan.Zero;

        // Splits the single MaximumSampleStep figure by which boundary it
        // occurred at: audio going silent (onset) versus silence resuming
        // audio (resume). The route-level fade-in this test exists to
        // validate only ever touches the resume side -- if the real jump is
        // at onset instead, that fix structurally cannot be the answer, and
        // this is the only way to tell the two apart from the loopback
        // capture alone.
        float maximumStepIntoSilence = 0;
        float maximumStepOutOfSilence = 0;
        bool previousFrameWasSilent = false;

        // The worst jump is no longer at a silence boundary -- both boundary
        // figures came back well below the overall maximum, which means the
        // waveform is being spliced mid-tone, with no silence on either side
        // of the cut. There is nothing left to infer that from: this records
        // the actual samples either side of whichever jump turns out to be
        // the worst, so its shape can be read directly. A cut that steps
        // across zero between two full-amplitude samples is a discarded or
        // repeated stretch of audio; one that falls to zero and climbs back
        // over a few dozen frames is a fade doing its job; one that simply
        // stops dead is the endpoint starving.
        const int NeighbourhoodFrames = 24;

        var precedingFrames =
            new float[NeighbourhoodFrames];

        int precedingCount = 0;
        int precedingHead = 0;

        var maximumStepNeighbourhood =
            new List<float>();

        int neighbourhoodTailRemaining = 0;
        long maximumStepFrameIndex = -1;

        // Whether the worst jumps land on the first frame of a captured
        // packet. They should not: a discontinuity in what the endpoint
        // actually rendered has no reason to align with where this client
        // happens to slice the stream, so if the worst step repeatedly falls
        // exactly on a packet seam it is the capture that lost audio, not
        // the endpoint that glitched -- and the Host cannot be at fault for
        // a break that only exists in the recording of it.
        bool maximumStepAtPacketBoundary = false;
        bool maximumStepOutOfSilenceAtPacketBoundary = false;

        // Every completed silent run, not just the longest one. A handful of
        // short gaps and one long gap look identical in the aggregate
        // NonSilentFrames/LongestSilentRunFrames numbers alone, but point at
        // very different causes -- this distinguishes "one bad moment at the
        // topology change" from "sustained, repeated glitching for a while
        // afterward".
        var silentGapFrameCounts =
            new List<long>();

        var previousSamples =
            new float[
                loopback.Format.ChannelCount];

        bool hasPreviousFrame =
            false;

        long previousReadTimestamp =
            0;

        var clock =
            Stopwatch.StartNew();

        long excludedSeamCount = 0;

        while (clock.Elapsed < duration)
        {
            long discontinuitiesBeforeRead =
                loopback.DiscontinuityCount;

            long positionGapsBeforeRead =
                loopback.PositionGapCount;

            int frames =
                loopback.Read(
                    buffer);

            if (frames <= 0)
            {
                Thread.Sleep(1);
                continue;
            }

            // WASAPI flagged the packet this read begins as not continuing
            // the previous one: audio existed on the endpoint that this
            // client never received. There is no waveform step to measure
            // across that seam, because the two samples either side of it
            // were never adjacent -- measuring one anyway reports a jump
            // that the endpoint never rendered, which is precisely what a
            // capture-side drop looks like and is indistinguishable from a
            // real glitch in the samples alone. Dropping the previous-frame
            // state makes the first frame after the seam start a fresh
            // comparison instead.
            if (loopback.DiscontinuityCount >
                    discontinuitiesBeforeRead ||
                loopback.PositionGapCount >
                    positionGapsBeforeRead)
            {
                excludedSeamCount++;

                hasPreviousFrame =
                    false;

                precedingCount =
                    0;
            }

            long readTimestamp =
                Stopwatch.GetTimestamp();

            if (previousReadTimestamp != 0)
            {
                TimeSpan readGap =
                    Stopwatch.GetElapsedTime(
                        previousReadTimestamp,
                        readTimestamp);

                if (readGap >
                    longestReadGap)
                {
                    longestReadGap =
                        readGap;
                }
            }

            previousReadTimestamp =
                readTimestamp;

            totalFrames += frames;

            int samples =
                frames *
                loopback.Format.ChannelCount;

            for (int frame = 0;
                 frame < frames;
                 frame++)
            {
                float framePeak =
                    0;

                float frameStep =
                    0;

                float maximumStepBeforeFrame =
                    maximumSampleStep;

                for (int channel = 0;
                     channel < loopback.Format.ChannelCount;
                     channel++)
                {
                    int sampleIndex =
                        frame *
                            loopback.Format.ChannelCount +
                        channel;

                    float sample =
                        buffer[sampleIndex];

                    float magnitude =
                        Math.Abs(sample);

                    framePeak =
                        Math.Max(
                            framePeak,
                            magnitude);

                    if (hasPreviousFrame)
                    {
                        float step =
                            Math.Abs(
                                sample -
                                previousSamples[channel]);

                        maximumSampleStep =
                            Math.Max(
                                maximumSampleStep,
                                step);

                        frameStep =
                            Math.Max(
                                frameStep,
                                step);
                    }

                    previousSamples[channel] =
                        sample;

                    sumOfSquares +=
                        sample *
                        (double)sample;
                }

                float leadingSample =
                    buffer[
                        frame *
                        loopback.Format.ChannelCount];

                if (neighbourhoodTailRemaining > 0)
                {
                    maximumStepNeighbourhood.Add(
                        leadingSample);

                    neighbourhoodTailRemaining--;
                }

                if (hasPreviousFrame &&
                    frameStep >
                    maximumStepBeforeFrame)
                {
                    maximumStepFrameIndex =
                        totalFrames -
                        frames +
                        frame;

                    maximumStepAtPacketBoundary =
                        frame == 0;

                    maximumStepNeighbourhood.Clear();

                    for (int i = 0;
                         i < precedingCount;
                         i++)
                    {
                        maximumStepNeighbourhood.Add(
                            precedingFrames[
                                (precedingHead -
                                 precedingCount +
                                 i +
                                 (2 * NeighbourhoodFrames)) %
                                NeighbourhoodFrames]);
                    }

                    maximumStepNeighbourhood.Add(
                        leadingSample);

                    neighbourhoodTailRemaining =
                        NeighbourhoodFrames;
                }

                precedingFrames[precedingHead] =
                    leadingSample;

                precedingHead =
                    (precedingHead + 1) %
                    NeighbourhoodFrames;

                precedingCount =
                    Math.Min(
                        precedingCount + 1,
                        NeighbourhoodFrames);

                bool frameIsSilent =
                    framePeak <=
                    SilenceFloor;

                if (hasPreviousFrame)
                {
                    if (previousFrameWasSilent &&
                        !frameIsSilent)
                    {
                        if (frameStep >
                            maximumStepOutOfSilence)
                        {
                            maximumStepOutOfSilenceAtPacketBoundary =
                                frame == 0;
                        }

                        maximumStepOutOfSilence =
                            Math.Max(
                                maximumStepOutOfSilence,
                                frameStep);
                    }
                    else if (!previousFrameWasSilent &&
                             frameIsSilent)
                    {
                        maximumStepIntoSilence =
                            Math.Max(
                                maximumStepIntoSilence,
                                frameStep);
                    }
                }

                previousFrameWasSilent =
                    frameIsSilent;

                hasPreviousFrame =
                    true;

                peak =
                    Math.Max(
                        peak,
                        framePeak);

                if (!frameIsSilent)
                {
                    nonSilentFrames++;

                    if (silentRunFrames > 0)
                    {
                        silentGapFrameCounts.Add(
                            silentRunFrames);
                    }

                    silentRunFrames =
                        0;
                }
                else
                {
                    silentRunFrames++;
                    longestSilentRunFrames =
                        Math.Max(
                            longestSilentRunFrames,
                            silentRunFrames);
                }
            }

            sampleCount += samples;
        }

        if (silentRunFrames > 0)
        {
            silentGapFrameCounts.Add(
                silentRunFrames);
        }

        loopback.Stop();

        double rms =
            sampleCount > 0
                ? Math.Sqrt(
                    sumOfSquares / sampleCount)
                : 0;

        return new ToneMeasurement(
            totalFrames,
            nonSilentFrames,
            peak,
            rms,
            maximumSampleStep,
            maximumStepIntoSilence,
            maximumStepOutOfSilence,
            maximumStepFrameIndex < 0
                ? 0.0
                : maximumStepFrameIndex /
                  (double)loopback.Format.SampleRate,
            maximumStepNeighbourhood,
            maximumStepAtPacketBoundary,
            maximumStepOutOfSilenceAtPacketBoundary,
            loopback.DiscontinuityCount,
            loopback.PositionGapCount,
            loopback.MissingFrameCount,
            excludedSeamCount,
            longestSilentRunFrames,
            longestReadGap,
            loopback.Format.SampleRate,
            silentGapFrameCounts);
    }

    /// <summary>
    /// The last resort after a graceful shutdown attempt and a
    /// <see cref="Process.WaitForExit(int)"/>: without this, a Host process
    /// that failed to shut down leaks past the test and keeps the driver's
    /// single-consumer handle busy, failing every hardware test that runs
    /// after it for a reason that has nothing to do with their own bodies.
    /// </summary>
    private static void KillIfStillRunning(
        Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(
                entireProcessTree: true);

            process.WaitForExit(
                5_000);
        }
        catch
        {
        }
    }

    private static string
        RequireEnvironmentVariable(
            string name)
    {
        string? value =
            Environment.GetEnvironmentVariable(
                name);

        if (string.IsNullOrWhiteSpace(
                value))
        {
            throw new InvalidOperationException(
                $"Environment variable {name} is required.");
        }

        return value;
    }

    private static string
        ResolveTopologyTestOutputId()
    {
        string? configured =
            Environment.GetEnvironmentVariable(
                "PCMODE_AUDIO_OUTPUT_ID");

        if (!string.IsNullOrWhiteSpace(
                configured))
        {
            return configured;
        }

        CoreAudioEndpointSnapshot[] realtek =
        [
            .. new CoreAudioEndpointCatalog()
                .GetActiveRenderEndpoints()
                .Where(
                    endpoint =>
                        endpoint.FriendlyName.Contains(
                            "Realtek",
                            StringComparison.OrdinalIgnoreCase))
        ];

        CoreAudioEndpointSnapshot[]
            secondOutputs =
            [
                .. realtek.Where(
                    endpoint =>
                        endpoint.FriendlyName.Contains(
                            "HD Audio 2nd output",
                            StringComparison.OrdinalIgnoreCase))
            ];

        if (secondOutputs.Length == 1)
        {
            return secondOutputs[0].Id.Value;
        }

        if (realtek.Length == 1)
        {
            return realtek[0].Id.Value;
        }

        CoreAudioEndpointSnapshot[] speakers =
        [
            .. realtek.Where(
                endpoint =>
                    endpoint.FriendlyName.Contains(
                        "Speakers",
                        StringComparison.OrdinalIgnoreCase))
        ];

        if (speakers.Length == 1)
        {
            return speakers[0].Id.Value;
        }

        string candidates =
            string.Join(
                Environment.NewLine,
                realtek.Select(
                    endpoint =>
                        $"  {endpoint.FriendlyName}: {endpoint.Id.Value}"));

        throw new InvalidOperationException(
            "Set PCMODE_AUDIO_OUTPUT_ID to the unaffected physical output endpoint. " +
            $"Automatic Realtek discovery found {realtek.Length} active matches:" +
            Environment.NewLine +
            candidates);
    }
}

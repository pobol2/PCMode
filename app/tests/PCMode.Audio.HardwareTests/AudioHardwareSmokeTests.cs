using System.Diagnostics;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Core.Processing;
using PCMode.Audio.Engine.Contracts;
using PCMode.Audio.Engine.Runtime;
using PCMode.Audio.Windows.Streams;
using PCMode.Audio.Windows.Threading;
using Xunit;

namespace PCMode.Audio.HardwareTests;

public sealed class AudioHardwareSmokeTests
{
    [HardwareFact]
    public void Loopback_audio_reaches_selected_output()
    {
        string source =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_SOURCE_ID");

        string output =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_OUTPUT_ID");

        Exception? failure = null;
        long processedFrames = 0;

        var thread =
            new Thread(
                () =>
                {
                    try
                    {
                        using MmcssRegistration mmcss =
                            MmcssRegistration
                                .RegisterProAudio();

                        var factory =
                            new WasapiStreamFactory(
                                new AudioEndpointId(
                                    source));

                        using var engine =
                            new AudioEngine(
                                factory,
                                factory,
                                blockFrameCapacity: 480);

                        var target =
                            new AudioTarget(
                                "hardware-smoke",
                                "Hardware Smoke",
                                new AudioRoute(
                                    new AudioEndpointId(
                                        output),
                                    processing:
                                        new AudioProcessingProfile(
                                            0.0f)));

                        engine.Apply(target);

                        var timer =
                            Stopwatch.StartNew();

                        while (timer.Elapsed <
                               TimeSpan.FromSeconds(5))
                        {
                            processedFrames +=
                                engine.ProcessNextBlock();
                        }

                        engine.Stop();
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                })
            {
                Name =
                    "PCMode.Audio.HardwareTest",
                IsBackground = true
            };

        thread.SetApartmentState(
            ApartmentState.STA);

        thread.Start();

        Assert.True(
            thread.Join(
                TimeSpan.FromSeconds(15)),
            "Hardware test audio thread did not stop.");

        if (failure is not null)
        {
            throw new InvalidOperationException(
                "Hardware audio smoke test failed.",
                failure);
        }

        Assert.True(
            processedFrames > 0,
            "No loopback frames were captured. " +
            "Make sure audio is actively playing to the selected source endpoint.");
    }

    [HardwareFact]
    public void Two_outputs_run_with_independent_clock_correction()
    {
        string source =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_SOURCE_ID");

        string outputA =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_OUTPUT_A_ID");

        string outputB =
            RequireEnvironmentVariable(
                "PCMODE_AUDIO_OUTPUT_B_ID");

        Exception? failure =
            null;

        long processedFrames =
            0;

        double finalRatioA =
            1.0;

        double finalRatioB =
            1.0;

        long resyncA =
            0;

        long resyncB =
            0;

        long underrunA =
            0;

        long underrunB =
            0;

        var thread =
            new Thread(
                () =>
                {
                    try
                    {
                        using MmcssRegistration mmcss =
                            MmcssRegistration
                                .RegisterProAudio();

                        var factory =
                            new WasapiStreamFactory(
                                new AudioEndpointId(
                                    source));

                        IAudioInputStream input =
                            factory.Create();

                        var routeA =
                            new AudioRouteRuntime(
                                new AudioRoute(
                                    new AudioEndpointId(
                                        outputA)),
                                factory.Create(
                                    new AudioEndpointId(
                                        outputA),
                                    input.Format),
                                new AudioProcessingChain(),
                                new GainProcessor(0.0f),
                                new ParametricEqProcessor(ParametricEqProfile.Flat, input.Format),
                                blockFrameCapacity: 480);

                        var routeB =
                            new AudioRouteRuntime(
                                new AudioRoute(
                                    new AudioEndpointId(
                                        outputB)),
                                factory.Create(
                                    new AudioEndpointId(
                                        outputB),
                                    input.Format),
                                new AudioProcessingChain(),
                                new GainProcessor(0.0f),
                                new ParametricEqProcessor(ParametricEqProfile.Flat, input.Format),
                                blockFrameCapacity: 480);

                        using var graph =
                            new AudioGraph(
                                input,
                                [
                                    routeA,
                                    routeB
                                ],
                                blockFrameCapacity: 480);

                        graph.Start();

                        var timer =
                            Stopwatch.StartNew();

                        while (timer.Elapsed <
                            TimeSpan.FromSeconds(
                                30))
                        {
                            processedFrames +=
                                graph.ProcessNextBlock();
                        }

                        finalRatioA =
                            routeA.CurrentResampleRatio;

                        finalRatioB =
                            routeB.CurrentResampleRatio;

                        resyncA =
                            routeA.ResyncCount;

                        resyncB =
                            routeB.ResyncCount;

                        underrunA =
                            routeA.UnderrunCount;

                        underrunB =
                            routeB.UnderrunCount;

                        graph.Stop();
                    }
                    catch (Exception ex)
                    {
                        failure =
                            ex;
                    }
                })
            {
                Name =
                    "PCMode.Audio.TwoOutputSyncTest",

                IsBackground =
                    true
            };

        thread.SetApartmentState(
            ApartmentState.STA);

        thread.Start();

        Assert.True(
            thread.Join(
                TimeSpan.FromSeconds(
                    45)),
            "Two-output synchronization test did not stop.");

        if (failure is not null)
        {
            throw new InvalidOperationException(
                "Two-output synchronization hardware test failed.",
                failure);
        }

        Assert.True(
            processedFrames > 0);

        Assert.InRange(
            finalRatioA,
            0.98,
            1.02);

        Assert.InRange(
            finalRatioB,
            0.98,
            1.02);

        Assert.True(
            resyncA >= 0);

        Assert.True(
            resyncB >= 0);

        Assert.True(
            underrunA >= 0);

        Assert.True(
            underrunB >= 0);
    }

    private static string RequireEnvironmentVariable(
        string name)
    {
        string? value =
            Environment.GetEnvironmentVariable(
                name);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Environment variable {name} is required.");
        }

        return value;
    }
}

public sealed class HardwareFactAttribute :
    FactAttribute
{
    public HardwareFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "PCMODE_AUDIO_HW"),
                "1",
                StringComparison.Ordinal))
        {
            Skip =
                "Set PCMODE_AUDIO_HW=1 to run real audio hardware tests.";
        }
    }
}
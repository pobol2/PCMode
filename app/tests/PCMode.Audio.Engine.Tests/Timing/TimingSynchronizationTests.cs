using PCMode.Audio.Engine.Contracts;
using PCMode.Audio.Engine.Timing;
using Xunit;

namespace PCMode.Audio.Engine.Tests.Timing;

public sealed class TimingSynchronizationTests
{
    [Fact]
    public void Resampler_ratio_one_preserves_samples()
    {
        var resampler =
            new Resampler(
                channelCount: 1);

        float[] source =
        [
            1.0f,
            2.0f,
            3.0f,
            4.0f
        ];

        float[] destination =
            new float[8];

        int frames =
            resampler.Process(
                source,
                1.0,
                destination);

        Assert.Equal(
            4,
            frames);

        Assert.Equal(
            source,
            destination[..frames]);
    }

    [Fact]
    public void Matching_clocks_require_no_correction()
    {
        var synchronizer =
            new RouteSynchronizer(
                sampleRate: 1_000,
                estimationWindowFrames: 100);

        long sourcePosition =
            0;

        double playedPosition =
            0.0;

        long submittedPosition =
            100;

        RouteSyncDecision decision =
            default;

        for (int i = 0;
             i < 100;
             i++)
        {
            decision =
                synchronizer.Update(
                    sourcePosition,
                    new AudioOutputClockSnapshot(
                        playedPosition,
                        submittedPosition));

            sourcePosition +=
                10;

            playedPosition +=
                10.0;

            submittedPosition +=
                10;
        }

        Assert.InRange(
            decision.EstimatedDriftPpm,
            -0.001,
            0.001);

        Assert.InRange(
            decision.CorrectionPpm,
            -0.001,
            0.001);

        Assert.InRange(
            decision.ResampleRatio,
            0.999999,
            1.000001);

        Assert.False(
            decision.IsResynchronizing);
    }

    [Fact]
    public void Positive_drift_produces_bounded_positive_correction()
    {
        var controller =
            new DriftController(
                sampleRate: 48_000);

        double correction =
            0.0;

        for (int i = 0;
             i < 100;
             i++)
        {
            correction =
                controller.Update(
                    estimatedDriftPpm: 800.0,
                    timingErrorFrames: 0.0);
        }

        Assert.Equal(
            DriftController
                .DefaultMaximumCorrectionPpm,
            correction);
    }

    [Fact]
    public void Negative_drift_produces_bounded_negative_correction()
    {
        var controller =
            new DriftController(
                sampleRate: 48_000);

        double correction =
            0.0;

        for (int i = 0;
             i < 100;
             i++)
        {
            correction =
                controller.Update(
                    estimatedDriftPpm: -800.0,
                    timingErrorFrames: 0.0);
        }

        Assert.Equal(
            -DriftController
                .DefaultMaximumCorrectionPpm,
            correction);
    }

    [Fact]
    public void Large_timing_error_enters_controlled_resync()
    {
        var synchronizer =
            new RouteSynchronizer(
                sampleRate: 1_000,
                estimationWindowFrames: 10);

        synchronizer.Update(
            0,
            new AudioOutputClockSnapshot(
                playedFramePosition: 0.0,
                submittedFramePosition: 100));

        RouteSyncDecision decision =
            synchronizer.Update(
                10,
                new AudioOutputClockSnapshot(
                    playedFramePosition: 10.0,
                    submittedFramePosition: 140));

        Assert.True(
            decision.IsResynchronizing);

        Assert.Equal(
            1,
            synchronizer.ResyncCount);

        Assert.True(
            decision.CorrectionPpm < 0.0);

        Assert.InRange(
            decision.ResampleRatio,
            0.98,
            1.02);
    }

    [Fact]
    public void Underruns_and_resyncs_are_counted()
    {
        var synchronizer =
            new RouteSynchronizer(
                sampleRate: 1_000,
                estimationWindowFrames: 10);

        synchronizer.Update(
            0,
            new AudioOutputClockSnapshot(
                0.0,
                100));

        synchronizer.Update(
            10,
            new AudioOutputClockSnapshot(
                110.0,
                110));

        Assert.Equal(
            1,
            synchronizer.UnderrunCount);

        Assert.Equal(
            1,
            synchronizer.ResyncCount);
    }

    [Fact]
    public void Two_routes_correct_independently()
    {
        var routeA =
            new RouteSynchronizer(
                sampleRate: 1_000,
                estimationWindowFrames: 100);

        var routeB =
            new RouteSynchronizer(
                sampleRate: 1_000,
                estimationWindowFrames: 100);

        long sourcePosition =
            0;

        double playedA =
            0.0;

        double playedB =
            0.0;

        long submittedA =
            100;

        long submittedB =
            100;

        double remainderA =
            0.0;

        double remainderB =
            0.0;

        RouteSyncDecision decisionA =
            default;

        RouteSyncDecision decisionB =
            default;

        const int blockFrames =
            10;

        for (int i = 0;
             i < 5_000;
             i++)
        {
            decisionA =
                routeA.Update(
                    sourcePosition,
                    new AudioOutputClockSnapshot(
                        playedA,
                        submittedA));

            decisionB =
                routeB.Update(
                    sourcePosition,
                    new AudioOutputClockSnapshot(
                        playedB,
                        submittedB));

            remainderA +=
                blockFrames *
                decisionA.ResampleRatio;

            remainderB +=
                blockFrames *
                decisionB.ResampleRatio;

            int producedA =
                (int)Math.Floor(
                    remainderA);

            int producedB =
                (int)Math.Floor(
                    remainderB);

            remainderA -=
                producedA;

            remainderB -=
                producedB;

            submittedA +=
                producedA;

            submittedB +=
                producedB;

            playedA +=
                blockFrames *
                1.00025;

            playedB +=
                blockFrames *
                0.99980;

            sourcePosition +=
                blockFrames;
        }

        Assert.True(
            decisionA.ResampleRatio >
            1.0);

        Assert.True(
            decisionB.ResampleRatio <
            1.0);

        Assert.NotEqual(
            decisionA.ResampleRatio,
            decisionB.ResampleRatio);

        Assert.InRange(
            Math.Abs(
                decisionA.TimingErrorFrames),
            0.0,
            10.0);

        Assert.InRange(
            Math.Abs(
                decisionB.TimingErrorFrames),
            0.0,
            10.0);
    }

    [Fact]
    public void An_unrepresentative_startup_snapshot_does_not_stay_wrong_forever()
    {
        // The very first Update() call fires before the route has written a
        // single frame, so a real device's buffer has not yet reached
        // whatever depth it naturally settles at once actually playing.
        // Modelled here as: 100 frames queued at that first call, but the
        // device actually runs at a healthy, stable 400 frames queued once
        // real playback gets going -- a state that never changes again, so
        // there is no real drift to correct, only a bad initial reference.
        var synchronizer =
            new RouteSynchronizer(
                sampleRate: 1_000,
                estimationWindowFrames: 100);

        long sourcePosition =
            0;

        RouteSyncDecision decision =
            synchronizer.Update(
                sourcePosition,
                new AudioOutputClockSnapshot(
                    playedFramePosition: 0.0,
                    submittedFramePosition: 100));

        // ~40 simulated seconds (400 estimation windows at 100ms each) --
        // long enough for the slow target tracking to fully absorb a bad
        // 300-frame startup gap, comfortably short of a real listening
        // session.
        for (int i = 0;
             i < 4_000;
             i++)
        {
            sourcePosition +=
                10;

            decision =
                synchronizer.Update(
                    sourcePosition,
                    new AudioOutputClockSnapshot(
                        playedFramePosition:
                            sourcePosition,
                        submittedFramePosition:
                            sourcePosition +
                            400));
        }

        // A steady, unchanging 400-frame queue is not a timing problem --
        // it is what this device always does. The route must have stopped
        // treating the gap from the unrepresentative first sample as a
        // permanent error to correct.
        Assert.False(
            decision.IsResynchronizing);

        // Converging asymptotically, not landing on exactly zero: well under
        // 1% of the -20,000ppm ceiling it was pinned to before the fix is
        // enough to show it actually resolved rather than settling
        // somewhere still audible.
        Assert.InRange(
            Math.Abs(
                decision.CorrectionPpm),
            0.0,
            50.0);

        Assert.InRange(
            decision.ResampleRatio,
            0.9999,
            1.0001);
    }

    [Fact]
    public void Long_playback_does_not_accumulate_unbounded_error()
    {
        var synchronizer =
            new RouteSynchronizer(
                sampleRate: 1_000,
                estimationWindowFrames: 100);

        long sourcePosition =
            0;

        double playedPosition =
            0.0;

        long submittedPosition =
            100;

        double producedRemainder =
            0.0;

        RouteSyncDecision decision =
            default;

        const int blockFrames =
            10;

        const double physicalClockRatio =
            1.00030;

        for (int i = 0;
             i < 20_000;
             i++)
        {
            decision =
                synchronizer.Update(
                    sourcePosition,
                    new AudioOutputClockSnapshot(
                        playedPosition,
                        submittedPosition));

            producedRemainder +=
                blockFrames *
                decision.ResampleRatio;

            int producedFrames =
                (int)Math.Floor(
                    producedRemainder);

            producedRemainder -=
                producedFrames;

            submittedPosition +=
                producedFrames;

            playedPosition +=
                blockFrames *
                physicalClockRatio;

            sourcePosition +=
                blockFrames;
        }

        Assert.InRange(
            Math.Abs(
                decision.TimingErrorFrames),
            0.0,
            10.0);

        Assert.InRange(
            decision.ResampleRatio,
            1.0000,
            1.0006);
    }
}
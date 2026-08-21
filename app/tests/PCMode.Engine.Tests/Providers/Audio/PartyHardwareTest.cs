using System.Globalization;

namespace PCMode.Engine.Tests;

/// <summary>
/// A test that is skipped unless the machine was explicitly volunteered.
/// </summary>
/// <remarks>
/// Decided when tests are discovered, before anything in the class is
/// constructed, so an ordinary run never enters it. A guard inside the test body
/// would be weaker in both directions: it would report as a passing test that
/// had quietly done nothing, and a mistake in it would play sound out of
/// somebody's speakers in the middle of a build.
/// </remarks>
public sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute()
    {
        if (!PartyHardwareTest.Requested)
        {
            Skip =
                "Hardware check. Set PCMODE_PARTY_HARDWARE_TEST=1 to run it: it opens the real Party " +
                "endpoints, changes the Windows default playback device and plays a tone out loud.";
        }
    }
}

/// <summary>
/// The one test that uses the real speakers and the real soundbar.
/// </summary>
/// <remarks>
/// NOT PART OF THE TEST SUITE, AND CANNOT BECOME PART OF IT BY ACCIDENT
///
/// It skips itself unless PCMODE_PARTY_HARDWARE_TEST is exactly "1". Every
/// other test in this project fakes the endpoints; this one opens them, changes
/// the Windows default playback device, moves endpoint volumes and plays a tone
/// out loud, none of which may happen because somebody ran "dotnet test".
///
/// Run it with:
///
///   $env:PCMODE_PARTY_HARDWARE_TEST="1"; dotnet test app/tests/PCMode.Engine.Tests --filter "FullyQualifiedName~PartyHardwareTest" --logger "console;verbosity=detailed"
///
/// It reads the real configuration -- the Party group in
/// %LOCALAPPDATA%\PCMode\Native\config.json -- so it exercises the same two
/// endpoints the product will. See docs/party-mode.md for what to listen for.
///
/// WHAT IT PUTS BACK
///
/// The Windows default playback device and both endpoints' volumes are recorded
/// before anything is changed and restored afterwards, including when the test
/// fails. A hardware check that left the machine playing through the soundbar at
/// whatever level the configuration wanted would be a worse experience than the
/// bug it was looking for.
/// </remarks>
public sealed class PartyHardwareTest(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Switch = "PCMODE_PARTY_HARDWARE_TEST";
    private const string RootVariable = "PCMODE_PARTY_HARDWARE_ROOT";
    private const string SecondsVariable = "PCMODE_PARTY_HARDWARE_SECONDS";

    private readonly Xunit.Abstractions.ITestOutputHelper _output = output;

    /// <summary>
    /// Whether the one environment variable that lets this run is set.
    /// </summary>
    /// <remarks>
    /// Checked by xunit before the test is constructed, so an ordinary run does
    /// not enter this class at all. A guard inside the test body would be weaker:
    /// it would report as a passing test that had quietly done nothing, and a
    /// mistake in the guard would be a mistake that plays sound out of somebody's
    /// speakers in the middle of a build.
    /// </remarks>
    public static bool Requested =>
        string.Equals(Environment.GetEnvironmentVariable(Switch), "1", StringComparison.Ordinal);

    [HardwareFact]
    public void Plays_a_test_signal_through_both_configured_party_outputs()
    {
        string root = Environment.GetEnvironmentVariable(RootVariable) ?? PCModePaths.DefaultRoot;

        int seconds = int.TryParse(
            Environment.GetEnvironmentVariable(SecondsVariable),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int given) && given > 0
            ? given
            : 30;

        var host = new PartyAudioHost();
        var endpoints = new CoreAudioEndpoints();

        // Built and validated exactly as the shell builds it, so this is the
        // configuration the product would run rather than a second reading of it.
        (string json, string path) = ModeConfiguration.ReadOrCreate(root);
        IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(root, json, host);
        _ = ModeConfiguration.Parse(json, providers, path);

        AudioProvider provider = providers.OfType<AudioProvider>().Single();

        PartySettings party = provider.Party
            ?? throw new InvalidOperationException(
                $"No Party group is configured in {path}. Add a '{PartySettings.SettingsPath}' section " +
                "naming a reference output and a mirror output.");

        Say($"Configuration   {path}");
        Say($"Internal reference (Windows default, captured):  '{party.Reference}'");
        Say($"Delayed output     (mirrored copy, adjustable):  '{party.Mirror}'");
        Say($"Configured      {party.DelayMs} ms delay, gain {party.Gain.ToString("0.00", CultureInfo.InvariantCulture)}");

        AudioEndpoint? wasDefault = endpoints.DefaultPlayback();
        string referenceId = Locate(endpoints, provider, party.Reference);
        string mirrorId = Locate(endpoints, provider, party.Mirror);

        double? referenceVolume = endpoints.ReadVolume(referenceId);
        double? mirrorVolume = endpoints.ReadVolume(mirrorId);

        Say($"Before          default '{wasDefault?.Name ?? "none"}', " +
            $"volumes {Describe(referenceVolume)} / {Describe(mirrorVolume)}");

        WasapiTestSignal? signal = null;

        try
        {
            provider.Apply(party.Name, CancellationToken.None);

            AudioPartyStatus started = provider.PartyStatus()!;
            Assert.Equal(PartyState.Running, started.State);
            Say("Party stream opened successfully.");

            AudioMirrorHealth startHealth = started.Health!;

            Say(string.Empty);
            Say($"Reference buffer   requested {startHealth.ReferenceBuffer.RequestedMs:0.00} ms, " +
                $"actual {startHealth.ReferenceBuffer.ActualMs:0.00} ms, " +
                $"device period {startHealth.ReferenceBuffer.DevicePeriodMs:0.00} ms, " +
                $"{(startHealth.ReferenceBuffer.LowLatencyEngine ? "IAudioClient3" : "IAudioClient")}");
            Say($"Mirror buffer      requested {startHealth.MirrorBuffer.RequestedMs:0.00} ms, " +
                $"actual {startHealth.MirrorBuffer.ActualMs:0.00} ms, " +
                $"device period {startHealth.MirrorBuffer.DevicePeriodMs:0.00} ms, " +
                $"{(startHealth.MirrorBuffer.LowLatencyEngine ? "IAudioClient3" : "IAudioClient")}");
            Say($"Cushion            {startHealth.CushionMs:0.00} ms (added automatically, not part of the configured delay)");
            Say($"Baseline latency   {startHealth.BaselineLatencyMs:0.00} ms -- unavoidable, even at 0 ms configured delay");
            Say($"Effective latency  {startHealth.EffectiveLatencyMs:0.00} ms -- baseline plus the {party.DelayMs} ms configured");

            signal = WasapiTestSignal.Start(referenceId, party.Reference);

            Say(string.Empty);
            Say($"Playing for {seconds}s. Clicks, then two tones, repeating every two seconds.");
            Say("Both outputs should carry the same pattern; a doubled click means they are not aligned.");
            Say(string.Empty);

            Watch(provider, party, seconds);

            AudioPartyStatus final = provider.PartyStatus()!;

            Say(string.Empty);
            Say($"Final           {Describe(final)}");

            Assert.Equal(PartyState.Running, final.State);
            Assert.Null(final.Health!.LastFailure);
            Assert.NotEqual(0, final.Health.FramesMirrored);
        }
        finally
        {
            signal?.Dispose();
            provider.StopParty();
            host.Dispose();

            Restore(endpoints, referenceId, referenceVolume);
            Restore(endpoints, mirrorId, mirrorVolume);

            if (wasDefault is not null)
            {
                endpoints.MakeDefault(wasDefault.Id);
            }

            Say($"Restored        default '{wasDefault?.Name ?? "none"}', " +
                $"volumes {Describe(referenceVolume)} / {Describe(mirrorVolume)}");
        }
    }

    /// <summary>
    /// Reports every second, and moves the delay and gain part way through.
    /// </summary>
    /// <remarks>
    /// The adjustment is the half of the feature a listener has to judge: whether
    /// a change lands immediately, whether it lands without a click, and whether
    /// the mirrored output really is the one that moved.
    /// </remarks>
    private void Watch(AudioProvider provider, PartySettings party, int seconds)
    {
        int shiftAt = Math.Max(1, seconds / 3);
        int restoreAt = Math.Max(shiftAt + 1, seconds * 2 / 3);

        TimeSpan shifted = TimeSpan.FromMilliseconds(Math.Min(PartySettings.MaxDelayMs, party.DelayMs + 150));

        for (int second = 1; second <= seconds; second++)
        {
            Thread.Sleep(1000);

            if (second == shiftAt)
            {
                Assert.True(provider.AdjustParty(shifted, 0.35));
                Say($">>> delay {shifted.TotalMilliseconds:0} ms, gain 0.35 -- the mirror should move back and drop");
            }
            else if (second == restoreAt)
            {
                Assert.True(provider.AdjustParty(party.Delay, party.Gain));
                Say($">>> delay {party.DelayMs} ms, gain {party.Gain.ToString("0.00", CultureInfo.InvariantCulture)} -- back to the configured alignment");
            }

            if (second % 2 == 0 || second == seconds)
            {
                Say($"{second,4}s  {Describe(provider.PartyStatus()!)}");
            }
        }
    }

    private static string Describe(AudioPartyStatus status)
    {
        if (status.Health is not AudioMirrorHealth health)
        {
            return $"{status.State}. {status.Problem}";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{status.State,-7} capture {(health.Capturing ? "on " : "OFF")} [{health.ReferenceFormat}] " +
            $"render {(health.Rendering ? "on " : "OFF")} [{health.MirrorFormat}] " +
            $"{(health.EventDriven ? "event" : "polled")} " +
            $"delay {health.DelayMs,4} ms  gain {health.Gain:0.00}  effective {health.EffectiveLatencyMs,6:0.0} ms  " +
            $"offset {health.ClockOffsetMs,7:0.00} ms  drift {health.DriftCorrections,6}  " +
            $"resync {health.Resyncs,3}  underrun {health.Underruns,4}  frames {health.FramesMirrored}" +
            $"{(health.LastFailure is null ? string.Empty : "  FAILED: " + health.LastFailure)}");
    }

    private static string Describe(double? volume) =>
        volume is double level ? (level * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "none";

    /// <summary>The endpoint ID behind a configured name, or a refusal naming what is there.</summary>
    private static string Locate(IAudioEndpoints endpoints, AudioProvider provider, string logical)
    {
        string? wanted = provider.EndpointNameFor(logical);

        List<AudioEndpoint> matches =
        [
            .. endpoints.ActivePlayback().Where(endpoint =>
                string.Equals(endpoint.Name, wanted, StringComparison.OrdinalIgnoreCase)),
        ];

        return matches.Count == 1
            ? matches[0].Id
            : throw new InvalidOperationException(
                $"'{logical}' ({wanted}) matches {matches.Count} active playback devices. " +
                $"Active now: {string.Join(", ", endpoints.ActivePlayback().Select(endpoint => $"'{endpoint.Name}'"))}.");
    }

    private static void Restore(IAudioEndpoints endpoints, string endpointId, double? volume)
    {
        if (volume is double level)
        {
            endpoints.SetVolume(endpointId, level);
        }
    }

    private void Say(string line)
    {
        _output.WriteLine(line);
        Console.WriteLine(line);
    }
}

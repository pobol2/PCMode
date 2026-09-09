using PCMode.Audio.Client;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Protocol.Messages;

namespace PCMode.Engine.Tests;

/// <summary>
/// A client that fails the test if anything on it is ever called.
/// </summary>
/// <remarks>
/// The whole point of the "not installed" short-circuit in
/// <see cref="AudioHostLink"/>: a machine with no Native Audio Host must never
/// reach for the pipe at all, not even to be told nothing answers. A fake that
/// throws on every member is what proves that, rather than a fake that would
/// merely answer quickly.
/// </remarks>
internal sealed class UnreachableAudioHostClient : IAudioHostClient
{
    private static InvalidOperationException NotExpected(
        [System.Runtime.CompilerServices.CallerMemberName] string member = ""
    ) => new($"{member} was called, but the audio Host is not installed and must never be contacted.");

    public Task ApplyTargetAsync(AudioTarget target, CancellationToken cancellationToken = default) =>
        throw NotExpected();

    public Task RecalibrateRouteAsync(
        AudioEndpointId endpointId,
        float volume,
        bool muted,
        TimeSpan delay,
        AudioProcessingProfile processing,
        CancellationToken cancellationToken = default
    ) => throw NotExpected();

    public Task StopAsync(CancellationToken cancellationToken = default) => throw NotExpected();

    public Task<AudioHostStatus> GetStatusAsync(CancellationToken cancellationToken = default) => throw NotExpected();

    public Task<AudioEngineHealth> GetHealthAsync(CancellationToken cancellationToken = default) => throw NotExpected();

    public Task<IReadOnlyList<AudioRouteWaveform>> GetWaveformAsync(CancellationToken cancellationToken = default) =>
        throw NotExpected();

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => throw NotExpected();
}

/// <summary>
/// What <see cref="AudioHostLink"/> does when Native Audio is not installed.
/// </summary>
public sealed class AudioHostLinkTests
{
    private static AudioHostLink NotInstalled(bool canLaunch = false) =>
        new(
            new UnreachableAudioHostClient(),
            canLaunch ? () => throw new InvalidOperationException("Should not launch in this test.") : null,
            installed: () => false
        );

    [Fact]
    public void Reports_unreachable_without_touching_the_client()
    {
        using AudioHostLink link = NotInstalled();

        AudioRuntimeStatus status = link.Refresh();

        Assert.False(status.Reachable);
        Assert.Contains("not installed", status.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stop_is_a_silent_no_op_without_touching_the_client()
    {
        using AudioHostLink link = NotInstalled();

        // Would throw from UnreachableAudioHostClient if Stop reached the pipe.
        link.Stop();

        Assert.False(link.Status.Reachable);
    }

    [Fact]
    public void Status_reads_the_last_answer_without_touching_the_client()
    {
        using AudioHostLink link = NotInstalled();

        Assert.False(link.Status.Reachable);
    }

    [Fact]
    public void Apply_explains_that_the_subsystem_is_not_installed_rather_than_timing_out()
    {
        using AudioHostLink link = NotInstalled();

        var target = new AudioTarget("target", "Target", [new AudioRoute(new AudioEndpointId("endpoint"))]);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            link.Apply(target, CancellationToken.None)
        );

        Assert.Contains("not installed", failure.Message, StringComparison.OrdinalIgnoreCase);
    }
}

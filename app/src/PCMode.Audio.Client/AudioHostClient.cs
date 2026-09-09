using PCMode.Audio.Core.Models;
using PCMode.Audio.Protocol;
using PCMode.Audio.Protocol.Messages;

namespace PCMode.Audio.Client;

public sealed class AudioHostClient : IAudioHostClient
{
    private readonly string _pipeName;

    private readonly int _protocolVersion;

    private readonly int _connectTimeoutMilliseconds;

    public AudioHostClient(
        string pipeName = HostProtocolVersion.DefaultPipeName,
        int protocolVersion = HostProtocolVersion.Current,
        int connectTimeoutMilliseconds = 2000
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(connectTimeoutMilliseconds);

        _pipeName = pipeName;

        _protocolVersion = protocolVersion;

        _connectTimeoutMilliseconds = connectTimeoutMilliseconds;
    }

    public async Task ApplyTargetAsync(AudioTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        AudioHostResponse response = await ExecuteAsync(
            AudioHostCommandKind.ApplyTarget,
            ApplyAudioTargetRequest.FromModel(target),
            cancellationToken
        );

        EnsureSuccess(response);
    }

    public async Task RecalibrateRouteAsync(
        AudioEndpointId endpointId,
        float volume,
        bool muted,
        TimeSpan delay,
        AudioProcessingProfile processing,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(processing);

        AudioHostResponse response = await ExecuteAsync(
            AudioHostCommandKind.RecalibrateRoute,
            applyTarget: null,
            cancellationToken,
            recalibrate: RecalibrateRouteRequest.FromModel(endpointId, volume, muted, delay, processing)
        );

        EnsureSuccess(response);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        AudioHostResponse response = await ExecuteAsync(AudioHostCommandKind.Stop, null, cancellationToken);

        EnsureSuccess(response);
    }

    public async Task<AudioHostStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        AudioHostResponse response = await ExecuteAsync(AudioHostCommandKind.GetStatus, null, cancellationToken);

        EnsureSuccess(response);

        return response.Status ?? throw new InvalidDataException("Audio Host returned no status.");
    }

    public async Task<AudioEngineHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        AudioHostResponse response = await ExecuteAsync(AudioHostCommandKind.GetHealth, null, cancellationToken);

        EnsureSuccess(response);

        return response.Health?.ToModel() ?? throw new InvalidDataException("Audio Host returned no Health.");
    }

    public async Task<IReadOnlyList<AudioRouteWaveform>> GetWaveformAsync(CancellationToken cancellationToken = default)
    {
        AudioHostResponse response = await ExecuteAsync(AudioHostCommandKind.GetWaveform, null, cancellationToken);

        EnsureSuccess(response);

        return response.Waveform?.Select(waveform => waveform.ToModel()).ToArray()
            ?? throw new InvalidDataException("Audio Host returned no Waveform.");
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        AudioHostResponse response = await ExecuteAsync(AudioHostCommandKind.Shutdown, null, cancellationToken);

        EnsureSuccess(response);
    }

    private async Task<AudioHostResponse> ExecuteAsync(
        AudioHostCommandKind kind,
        ApplyAudioTargetRequest? applyTarget,
        CancellationToken cancellationToken,
        RecalibrateRouteRequest? recalibrate = null
    )
    {
        await using AudioHostConnection connection = await AudioHostConnection.ConnectAsync(
            _pipeName,
            _protocolVersion,
            _connectTimeoutMilliseconds,
            cancellationToken
        );

        AudioHostCommand command = AudioHostCommand.Create(kind, applyTarget, _protocolVersion, recalibrate);

        return await connection.SendAsync(command, cancellationToken);
    }

    private static void EnsureSuccess(AudioHostResponse response)
    {
        if (!response.Success)
        {
            throw AudioHostConnection.CreateException(response);
        }
    }
}

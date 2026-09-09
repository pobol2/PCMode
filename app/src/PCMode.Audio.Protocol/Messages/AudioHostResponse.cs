using PCMode.Audio.Core.Buffers;
using PCMode.Audio.Core.Models;

namespace PCMode.Audio.Protocol.Messages;

public sealed record AudioHostResponse(
    int ProtocolVersion,
    Guid RequestId,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    AudioHostStatus? Status,
    AudioEngineHealthDto? Health,
    AudioRouteWaveformDto[]? Waveform = null
)
{
    public static AudioHostResponse Ok(
        Guid requestId,
        AudioHostStatus? status = null,
        AudioEngineHealthDto? health = null,
        AudioRouteWaveformDto[]? waveform = null
    )
    {
        return new AudioHostResponse(
            HostProtocolVersion.Current,
            requestId,
            true,
            null,
            null,
            status,
            health,
            waveform
        );
    }

    public static AudioHostResponse Error(Guid requestId, string errorCode, string errorMessage)
    {
        return new AudioHostResponse(
            HostProtocolVersion.Current,
            requestId,
            false,
            errorCode,
            errorMessage,
            null,
            null,
            null
        );
    }
}

/// <summary>
/// One route's waveform envelope over the wire: two parallel arrays rather
/// than an array of pairs, which is what a redraw loop calling this many
/// times a second should cost in JSON.
/// </summary>
public sealed record AudioRouteWaveformDto(string EndpointId, bool Available, float[] Min, float[] Max)
{
    public static AudioRouteWaveformDto FromModel(AudioRouteWaveform model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!model.Available)
        {
            return new AudioRouteWaveformDto(model.EndpointId.Value, false, [], []);
        }

        var min = new float[model.Buckets.Count];
        var max = new float[model.Buckets.Count];

        for (int i = 0; i < model.Buckets.Count; i++)
        {
            min[i] = model.Buckets[i].Min;
            max[i] = model.Buckets[i].Max;
        }

        return new AudioRouteWaveformDto(model.EndpointId.Value, true, min, max);
    }

    public AudioRouteWaveform ToModel()
    {
        if (!Available || Min.Length == 0)
        {
            return new AudioRouteWaveform(new AudioEndpointId(EndpointId), false, []);
        }

        var buckets = new WaveformEnvelopeSample[Min.Length];

        for (int i = 0; i < buckets.Length; i++)
        {
            buckets[i] = new WaveformEnvelopeSample(Min[i], Max[i]);
        }

        return new AudioRouteWaveform(new AudioEndpointId(EndpointId), true, buckets);
    }
}

public sealed record AudioEngineHealthDto(
    AudioEngineRuntimeState EngineState,
    string? ActiveTargetId,
    string? ActiveTargetName,
    AudioRouteHealthDto[] Routes,
    AudioFailureDto? LastFailure,
    AudioInputHealthDto? Input = null
)
{
    public static AudioEngineHealthDto FromModel(AudioEngineHealth health)
    {
        return new AudioEngineHealthDto(
            health.EngineState,
            health.ActiveTargetId,
            health.ActiveTargetName,
            [.. health.Routes.Select(AudioRouteHealthDto.FromModel)],
            health.LastFailure is null ? null : AudioFailureDto.FromModel(health.LastFailure),
            health.Input is null ? null : AudioInputHealthDto.FromModel(health.Input)
        );
    }

    public AudioEngineHealth ToModel()
    {
        return new AudioEngineHealth(
            EngineState,
            ActiveTargetId,
            ActiveTargetName,
            Routes.Select(route => route.ToModel()),
            LastFailure?.ToModel(),
            Input?.ToModel()
        );
    }
}

public sealed record AudioInputHealthDto(
    AudioInputKind Kind,
    AudioInputHealthState State,
    AudioFormatDto? Format,
    AudioFailureDto? LastFailure
)
{
    public static AudioInputHealthDto FromModel(AudioInputHealth health)
    {
        return new AudioInputHealthDto(
            health.Kind,
            health.State,
            health.Format is null ? null : AudioFormatDto.FromModel(health.Format.Value),
            health.LastFailure is null ? null : AudioFailureDto.FromModel(health.LastFailure)
        );
    }

    public AudioInputHealth ToModel()
    {
        return new AudioInputHealth(Kind, State, Format?.ToModel(), LastFailure?.ToModel());
    }
}

public sealed record AudioRouteHealthDto(
    string EndpointId,
    AudioRouteRequirement Requirement,
    AudioRouteHealthState State,
    AudioEndpointState EndpointState,
    AudioFormatDto? Format,
    double ConfiguredDelayMilliseconds,
    double ClockOffsetFrames,
    double DriftCorrectionPpm,
    double ResampleRatio,
    bool IsResynchronizing,
    long UnderrunCount,
    long ResyncCount,
    long StallRecoveryCount,
    double LongestProcessGapMilliseconds,
    long OutputRecoveryFadeCount,
    long OutputEndpointDryCount,
    double LongestOutputSubmitGapMilliseconds,
    double LongestOutputWriteMilliseconds,
    double LongestOutputBufferWaitMilliseconds,
    AudioProcessingProfileDto Processing,
    AudioFailureDto? LastFailure
)
{
    public static AudioRouteHealthDto FromModel(AudioRouteHealth health)
    {
        return new AudioRouteHealthDto(
            health.EndpointId.Value,
            health.Requirement,
            health.State,
            health.EndpointState,
            health.Format is null ? null : AudioFormatDto.FromModel(health.Format.Value),
            health.ConfiguredDelay.TotalMilliseconds,
            health.ClockOffsetFrames,
            health.DriftCorrectionPpm,
            health.ResampleRatio,
            health.IsResynchronizing,
            health.UnderrunCount,
            health.ResyncCount,
            health.StallRecoveryCount,
            health.LongestProcessGapMilliseconds,
            health.OutputRecoveryFadeCount,
            health.OutputEndpointDryCount,
            health.LongestOutputSubmitGapMilliseconds,
            health.LongestOutputWriteMilliseconds,
            health.LongestOutputBufferWaitMilliseconds,
            AudioProcessingProfileDto.FromModel(health.Processing),
            health.LastFailure is null ? null : AudioFailureDto.FromModel(health.LastFailure)
        );
    }

    public AudioRouteHealth ToModel()
    {
        return new AudioRouteHealth(
            new AudioEndpointId(EndpointId),
            Requirement,
            State,
            EndpointState,
            Format?.ToModel(),
            TimeSpan.FromMilliseconds(ConfiguredDelayMilliseconds),
            ClockOffsetFrames,
            DriftCorrectionPpm,
            ResampleRatio,
            IsResynchronizing,
            UnderrunCount,
            ResyncCount,
            StallRecoveryCount,
            LongestProcessGapMilliseconds,
            OutputRecoveryFadeCount,
            OutputEndpointDryCount,
            LongestOutputSubmitGapMilliseconds,
            LongestOutputWriteMilliseconds,
            LongestOutputBufferWaitMilliseconds,
            Processing.ToModel(),
            LastFailure?.ToModel()
        );
    }
}

public sealed record AudioFormatDto(int SampleRate, int ChannelCount)
{
    public static AudioFormatDto FromModel(AudioFormat format)
    {
        return new AudioFormatDto(format.SampleRate, format.ChannelCount);
    }

    public AudioFormat ToModel()
    {
        return new AudioFormat(SampleRate, ChannelCount);
    }
}

public sealed record AudioFailureDto(string Type, string Message, int HResult, DateTimeOffset TimestampUtc)
{
    public static AudioFailureDto FromModel(AudioFailure failure)
    {
        return new AudioFailureDto(failure.Type, failure.Message, failure.HResult, failure.TimestampUtc);
    }

    public AudioFailure ToModel()
    {
        return new AudioFailure(Type, Message, HResult, TimestampUtc);
    }
}

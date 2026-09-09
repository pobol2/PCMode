using PCMode.Audio.Core.Models;

namespace PCMode.Audio.Protocol.Messages;

public sealed record ApplyAudioTargetRequest(AudioTargetDto Target)
{
    public static ApplyAudioTargetRequest FromModel(AudioTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return new ApplyAudioTargetRequest(AudioTargetDto.FromModel(target));
    }
}

public sealed record AudioTargetDto(string Id, string Name, AudioRouteDto[] Routes)
{
    public static AudioTargetDto FromModel(AudioTarget target)
    {
        return new AudioTargetDto(target.Id, target.Name, [.. target.Routes.Select(AudioRouteDto.FromModel)]);
    }

    public AudioTarget ToModel()
    {
        return new AudioTarget(Id, Name, [.. Routes.Select(route => route.ToModel())]);
    }
}

public sealed record AudioRouteDto(
    string EndpointId,
    AudioRouteRequirement Requirement,
    float Volume,
    bool Muted,
    double DelayMilliseconds,
    AudioProcessingProfileDto Processing
)
{
    public static AudioRouteDto FromModel(AudioRoute route)
    {
        return new AudioRouteDto(
            route.EndpointId.Value,
            route.Requirement,
            route.Volume,
            route.Muted,
            route.Delay.TotalMilliseconds,
            AudioProcessingProfileDto.FromModel(route.Processing)
        );
    }

    public AudioRoute ToModel()
    {
        return new AudioRoute(
            new AudioEndpointId(EndpointId),
            Requirement,
            Volume,
            Muted,
            TimeSpan.FromMilliseconds(DelayMilliseconds),
            Processing.ToModel()
        );
    }
}

public sealed record AudioProcessingProfileDto(float GainDb, ParametricEqProfileDto? ParametricEq)
{
    public static AudioProcessingProfileDto FromModel(AudioProcessingProfile profile)
    {
        return new AudioProcessingProfileDto(
            profile.GainDb,
            profile.ParametricEq is null ? null : ParametricEqProfileDto.FromModel(profile.ParametricEq)
        );
    }

    public AudioProcessingProfile ToModel()
    {
        return new AudioProcessingProfile(GainDb, ParametricEq?.ToModel());
    }
}

public sealed record ParametricEqProfileDto(float PreampGainDb, EqBandDto[] Bands)
{
    public static ParametricEqProfileDto FromModel(ParametricEqProfile profile)
    {
        return new ParametricEqProfileDto(profile.PreampGainDb, [.. profile.Bands.Select(EqBandDto.FromModel)]);
    }

    public ParametricEqProfile ToModel()
    {
        return new ParametricEqProfile(PreampGainDb, [.. Bands.Select(band => band.ToModel())]);
    }
}

/// <summary>
/// Recalibrates one running route's gain, mute, endpoint volume, parametric
/// EQ and delay, without touching the rest of the target.
/// </summary>
public sealed record RecalibrateRouteRequest(
    string EndpointId,
    float Volume,
    bool Muted,
    double DelayMilliseconds,
    AudioProcessingProfileDto Processing
)
{
    public static RecalibrateRouteRequest FromModel(
        AudioEndpointId endpointId,
        float volume,
        bool muted,
        TimeSpan delay,
        AudioProcessingProfile processing
    )
    {
        ArgumentNullException.ThrowIfNull(processing);

        return new RecalibrateRouteRequest(
            endpointId.Value,
            volume,
            muted,
            delay.TotalMilliseconds,
            AudioProcessingProfileDto.FromModel(processing)
        );
    }
}

public sealed record EqBandDto(EqFilterType Type, float FrequencyHz, float GainDb, float Q)
{
    public static EqBandDto FromModel(EqBand band)
    {
        return new EqBandDto(band.Type, band.FrequencyHz, band.GainDb, band.Q);
    }

    public EqBand ToModel()
    {
        return new EqBand(Type, FrequencyHz, GainDb, Q);
    }
}

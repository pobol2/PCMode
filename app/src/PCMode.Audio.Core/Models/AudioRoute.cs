namespace PCMode.Audio.Core.Models;

public enum AudioRouteRequirement
{
    Required,
    Optional,
}

public sealed class AudioRoute
{
    public const int MaximumDelayMilliseconds = 1000;

    public AudioEndpointId EndpointId { get; }

    public AudioRouteRequirement Requirement { get; }

    public float Volume { get; }

    public bool Muted { get; }

    public TimeSpan Delay { get; }

    public AudioProcessingProfile Processing { get; }

    public AudioRoute(
        AudioEndpointId endpointId,
        AudioRouteRequirement requirement = AudioRouteRequirement.Required,
        float volume = 1.0f,
        bool muted = false,
        TimeSpan delay = default,
        AudioProcessingProfile? processing = null
    )
    {
        if (endpointId == default)
        {
            throw new ArgumentException("Endpoint ID must be specified.", nameof(endpointId));
        }

        if (!Enum.IsDefined(typeof(AudioRouteRequirement), requirement))
        {
            throw new ArgumentOutOfRangeException(nameof(requirement));
        }

        if (!float.IsFinite(volume) || volume < 0.0f || volume > 1.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(volume),
                volume,
                "Volume must be a finite value between 0.0 and 1.0."
            );
        }

        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay cannot be negative.");
        }

        if (delay > TimeSpan.FromMilliseconds(MaximumDelayMilliseconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(delay),
                delay,
                $"Delay cannot exceed {MaximumDelayMilliseconds} ms."
            );
        }

        EndpointId = endpointId;

        Requirement = requirement;

        Volume = volume;

        Muted = muted;

        Delay = delay;

        Processing = processing ?? AudioProcessingProfile.Flat;
    }
}

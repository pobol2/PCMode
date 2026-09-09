namespace PCMode.Audio.Core.Models;

public sealed record AudioProcessingProfile
{
    public static AudioProcessingProfile Flat { get; } = new(0.0f);

    public float GainDb { get; }

    public ParametricEqProfile? ParametricEq { get; }

    public AudioProcessingProfile(float gainDb, ParametricEqProfile? parametricEq = null)
    {
        if (!float.IsFinite(gainDb))
        {
            throw new ArgumentOutOfRangeException(nameof(gainDb), gainDb, "Gain must be finite.");
        }

        GainDb = gainDb;
        ParametricEq = parametricEq;
    }
}

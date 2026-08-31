namespace PCMode.Audio.Core.Models;

public readonly record struct EqBand
{
    public const float MinGainDb = -24.0f;
    public const float MaxGainDb = 24.0f;

    public const float MinQ = 0.1f;
    public const float MaxQ = 24.0f;

    public EqFilterType Type { get; }

    public float FrequencyHz { get; }

    public float GainDb { get; }

    public float Q { get; }

    public EqBand(
        EqFilterType type,
        float frequencyHz,
        float gainDb = 0.0f,
        float q = 0.70710677f)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(
                nameof(type));
        }

        if (!float.IsFinite(frequencyHz) ||
            frequencyHz <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frequencyHz),
                frequencyHz,
                "Frequency must be finite and greater than zero.");
        }

        if (!float.IsFinite(gainDb) ||
            gainDb < MinGainDb ||
            gainDb > MaxGainDb)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gainDb),
                gainDb,
                $"Gain must be finite and between {MinGainDb} dB and {MaxGainDb} dB.");
        }

        if (!float.IsFinite(q) ||
            q < MinQ ||
            q > MaxQ)
        {
            throw new ArgumentOutOfRangeException(
                nameof(q),
                q,
                $"Q must be finite and between {MinQ} and {MaxQ}.");
        }

        if ((type == EqFilterType.LowPass ||
             type == EqFilterType.HighPass) &&
            gainDb != 0.0f)
        {
            throw new ArgumentException(
                "Low-pass and high-pass filters do not use gain; specify 0 dB.",
                nameof(gainDb));
        }

        Type = type;
        FrequencyHz = frequencyHz;
        GainDb = gainDb;
        Q = q;
    }
}
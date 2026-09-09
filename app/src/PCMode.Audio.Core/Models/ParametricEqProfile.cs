namespace PCMode.Audio.Core.Models;

public sealed class ParametricEqProfile : IEquatable<ParametricEqProfile>
{
    public const float MinPreampGainDb = -24.0f;
    public const float MaxPreampGainDb = 24.0f;

    private readonly EqBand[] _bands;
    private readonly IReadOnlyList<EqBand> _readOnlyBands;

    public static ParametricEqProfile Flat { get; } = new(0.0f);

    public float PreampGainDb { get; }

    public IReadOnlyList<EqBand> Bands => _readOnlyBands;

    public ParametricEqProfile(float preampGainDb, params EqBand[] bands)
    {
        if (!float.IsFinite(preampGainDb) || preampGainDb < MinPreampGainDb || preampGainDb > MaxPreampGainDb)
        {
            throw new ArgumentOutOfRangeException(
                nameof(preampGainDb),
                preampGainDb,
                $"Preamp gain must be finite and between {MinPreampGainDb} dB and {MaxPreampGainDb} dB."
            );
        }

        ArgumentNullException.ThrowIfNull(bands);

        PreampGainDb = preampGainDb;

        _bands = [.. bands];
        _readOnlyBands = Array.AsReadOnly(_bands);
    }

    public bool Equals(ParametricEqProfile? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || PreampGainDb != other.PreampGainDb || _bands.Length != other._bands.Length)
        {
            return false;
        }

        for (int i = 0; i < _bands.Length; i++)
        {
            if (_bands[i] != other._bands[i])
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj)
    {
        return obj is ParametricEqProfile other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();

        hash.Add(PreampGainDb);

        for (int i = 0; i < _bands.Length; i++)
        {
            hash.Add(_bands[i]);
        }

        return hash.ToHashCode();
    }
}

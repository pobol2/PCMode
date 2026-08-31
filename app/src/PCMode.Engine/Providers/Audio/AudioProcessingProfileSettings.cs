using System.Globalization;
using System.Text.Json;
using PCMode.Audio.Core.Models;

namespace PCMode.Engine;

/// <summary>
/// One named PCMode audio processing profile: a preamp trim and a bank of
/// parametric EQ bands, reusing exactly the native processing already built
/// for AudioLab -- see <see cref="ParametricEqProcessor"/> in
/// PCMode.Audio.Engine. A route names one of these, or none for a flat,
/// bypassed route.
/// </summary>
/// <remarks>
/// NAMED HERE, ANONYMOUS BELOW
///
/// <see cref="ParametricEqProfile"/> itself carries no name -- it is the
/// engine's own value type, applied to whichever route asked for it. The name
/// exists only at this configuration layer, the same division
/// <see cref="AudioTargetSettings"/> already keeps between a target's name and
/// the routes it is built from.
/// </remarks>
public sealed record AudioProcessingProfileSettings(string Name, float PreampGainDb, IReadOnlyList<EqBand> Bands)
{
    /// <summary>Where these live in the document.</summary>
    public const string SettingsPath = "providers.audio.profiles";

    /// <summary>The engine-facing profile this settles into.</summary>
    public ParametricEqProfile ToProfile() => new(PreampGainDb, [.. Bands]);

    /// <summary>
    /// Reads the "profiles" object, or says what is wrong with it.
    /// </summary>
    public static bool TryParse(
        JsonElement written,
        out IReadOnlyDictionary<string, AudioProcessingProfileSettings>? profiles,
        out string? problem)
    {
        profiles = null;

        if (written.ValueKind != JsonValueKind.Object)
        {
            problem = $"'{SettingsPath}' must be an object mapping a profile name you choose to its preamp and bands.";
            return false;
        }

        var parsed = new Dictionary<string, AudioProcessingProfileSettings>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty entry in written.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                problem = $"A profile in '{SettingsPath}' has no name.";
                return false;
            }

            string name = entry.Name.Trim();

            if (!TryReadProfile(name, entry.Value, out AudioProcessingProfileSettings? profile, out problem))
            {
                return false;
            }

            if (!parsed.TryAdd(name, profile!))
            {
                problem = $"'{SettingsPath}' defines '{name}' more than once.";
                return false;
            }
        }

        profiles = parsed;
        problem = null;
        return true;
    }

    private static bool TryReadProfile(
        string name,
        JsonElement written,
        out AudioProcessingProfileSettings? profile,
        out string? problem)
    {
        profile = null;
        string where = $"'{SettingsPath}.{name}'";

        if (written.ValueKind != JsonValueKind.Object)
        {
            problem = $"{where} must be an object with an optional 'preampGainDb' and a 'bands' list.";
            return false;
        }

        double preampGainDb = 0;
        JsonElement? bandsElement = null;

        foreach (JsonProperty field in written.EnumerateObject())
        {
            if (field.NameEquals("preampGainDb"))
            {
                if (!TryReadNumber(
                        field.Value,
                        $"{where}.preampGainDb",
                        ParametricEqProfile.MinPreampGainDb,
                        ParametricEqProfile.MaxPreampGainDb,
                        out preampGainDb,
                        out problem))
                {
                    return false;
                }
            }
            else if (field.NameEquals("bands"))
            {
                bandsElement = field.Value;
            }
            else
            {
                problem = $"{where} does not understand '{field.Name}'. It takes 'preampGainDb' and 'bands'.";
                return false;
            }
        }

        var bands = new List<EqBand>();

        if (bandsElement is JsonElement written2)
        {
            if (written2.ValueKind != JsonValueKind.Array)
            {
                problem = $"{where}.bands must be a list of bands.";
                return false;
            }

            int index = 0;

            foreach (JsonElement bandElement in written2.EnumerateArray())
            {
                if (!TryReadBand($"{where}.bands[{index}]", bandElement, out EqBand band, out problem))
                {
                    return false;
                }

                bands.Add(band);
                index++;
            }
        }

        try
        {
            profile = new AudioProcessingProfileSettings(name, (float)preampGainDb, bands);
        }
        catch (ArgumentException exception)
        {
            problem = $"{where}: {exception.Message}";
            return false;
        }

        problem = null;
        return true;
    }

    private static bool TryReadBand(string where, JsonElement written, out EqBand band, out string? problem)
    {
        band = default;

        if (written.ValueKind != JsonValueKind.Object)
        {
            problem = $"{where} must be an object with 'type', 'frequencyHz', 'gainDb' and 'q'.";
            return false;
        }

        EqFilterType? type = null;
        double frequencyHz = 0;
        double gainDb = 0;
        double q = 0.70710677;

        foreach (JsonProperty field in written.EnumerateObject())
        {
            if (field.NameEquals("type"))
            {
                string? text = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : null;

                if (text is null || !Enum.TryParse(text, ignoreCase: true, out EqFilterType parsed) || !Enum.IsDefined(parsed))
                {
                    problem = $"{where}.type must be one of: {string.Join(", ", Enum.GetNames<EqFilterType>())}.";
                    return false;
                }

                type = parsed;
            }
            else if (field.NameEquals("frequencyHz"))
            {
                if (!TryReadNumber(field.Value, $"{where}.frequencyHz", 1, 96_000, out frequencyHz, out problem))
                {
                    return false;
                }
            }
            else if (field.NameEquals("gainDb"))
            {
                if (!TryReadNumber(field.Value, $"{where}.gainDb", EqBand.MinGainDb, EqBand.MaxGainDb, out gainDb, out problem))
                {
                    return false;
                }
            }
            else if (field.NameEquals("q"))
            {
                if (!TryReadNumber(field.Value, $"{where}.q", EqBand.MinQ, EqBand.MaxQ, out q, out problem))
                {
                    return false;
                }
            }
            else
            {
                problem = $"{where} does not understand '{field.Name}'. It takes 'type', 'frequencyHz', 'gainDb' and 'q'.";
                return false;
            }
        }

        if (type is not EqFilterType filterType)
        {
            problem = $"{where} needs a 'type'.";
            return false;
        }

        try
        {
            band = new EqBand(filterType, (float)frequencyHz, (float)gainDb, (float)q);
        }
        catch (ArgumentException exception)
        {
            problem = $"{where}: {exception.Message}";
            return false;
        }

        problem = null;
        return true;
    }

    private static bool TryReadNumber(
        JsonElement written, string where, double lowest, double highest, out double value, out string? problem)
    {
        value = 0;

        if (written.ValueKind != JsonValueKind.Number || !written.TryGetDouble(out value) ||
            double.IsNaN(value) || double.IsInfinity(value))
        {
            problem = $"'{where}' must be a number.";
            return false;
        }

        if (value < lowest || value > highest)
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"'{where}' must be between {lowest} and {highest}.");
            return false;
        }

        problem = null;
        return true;
    }
}

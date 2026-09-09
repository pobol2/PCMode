namespace PCMode.Engine;

/// <summary>
/// Builds the four engine resource keys owned by one television.
/// </summary>
/// <remarks>
/// A television is addressed by name -- <c>tv.Bedroom.power</c> -- except for the
/// one the configuration makes the default, which keeps the unprefixed keys every
/// document written before televisions had names uses. See
/// <see cref="LgTvCatalog"/> for why that is the only thing "default" does.
///
/// A null name here means those unprefixed keys. It is the RESOURCE name and not
/// the television's own -- the default television still has a name, and still
/// says it in <see cref="DisplayName"/>, so a failed step names the device
/// somebody has to go and look at rather than "the television".
/// </remarks>
public static class LgTvResourceKeys
{
    private const string Prefix = "tv.";

    public static string Power(string? resourceName) => Build(resourceName, "power", TvPowerProvider.ResourceKey);

    public static string Input(string? resourceName) => Build(resourceName, "input", TvInputProvider.ResourceKey);

    public static string Signal(string? resourceName) => Build(resourceName, "signal", TvSignalProvider.ResourceKey);

    public static string Profile(string? resourceName) => Build(resourceName, "profile", TvProfileProvider.ResourceKey);

    public static string DisplayName(string? televisionName, string capability) =>
        string.IsNullOrWhiteSpace(televisionName)
            ? $"Television {capability}"
            : $"{televisionName.Trim()} television {capability}";

    /// <summary>
    /// Whether a resource key belongs to a television, whichever way it is
    /// spelled.
    /// </summary>
    /// <remarks>
    /// For a settings screen that groups the television's resources together. It
    /// answers from the key alone rather than from the providers, because it is
    /// also asked about a key left in a document by a television that has since
    /// been deleted -- which is exactly the case where grouping it correctly is
    /// worth something.
    /// </remarks>
    public static bool IsTelevisionKey(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && (
            key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, TvPowerProvider.ResourceKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, TvInputProvider.ResourceKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, TvSignalProvider.ResourceKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, TvProfileProvider.ResourceKey, StringComparison.OrdinalIgnoreCase)
        );

    private static string Build(string? resourceName, string capability, string defaultKey) =>
        string.IsNullOrWhiteSpace(resourceName) ? defaultKey : $"{Prefix}{resourceName.Trim()}.{capability}";
}

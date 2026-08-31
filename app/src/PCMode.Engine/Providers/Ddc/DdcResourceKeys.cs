namespace PCMode.Engine;

/// <summary>Builds the resource keys owned by one configured DDC/CI monitor.</summary>
/// <remarks>
/// Every DDC/CI monitor is named -- there is no unprefixed, single-monitor
/// shape the way <see cref="LgTvResourceKeys"/> keeps for backward
/// compatibility with documents written before televisions had names. DDC/CI
/// support did not exist before this, so there is nothing to be compatible
/// with, and a machine can have more than one monitor from the start.
/// </remarks>
public static class DdcResourceKeys
{
    private const string Prefix = "ddc.";

    public static string Brightness(string alias) => Build(alias, "brightness");

    public static string Input(string alias) => Build(alias, "input");

    public static string DisplayName(string alias, string capability) => $"{alias.Trim()} monitor {capability}";

    private static string Build(string alias, string capability) => $"{Prefix}{alias.Trim()}.{capability}";
}

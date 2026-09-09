using System.Globalization;
using System.Text.Json;

namespace PCMode.Engine;

/// <summary>Switches a configured monitor's DDC/CI input source (VCP 0x60).</summary>
/// <remarks>
/// A value is either one of the common MCCS 2.2 input names in
/// <see cref="KnownInputs"/>, or a raw VCP value written as decimal or as
/// <c>0x</c>-prefixed hex, for a vendor-specific or uncommon input this build
/// does not name. <see cref="IsValidValue"/> only checks that shape; whether a
/// specific monitor actually offers that particular value is a fact about the
/// room, checked -- honestly, and only when it can be -- in
/// <see cref="Apply"/>. See <see cref="DisplayProvider"/>'s remarks for why
/// that split exists.
/// </remarks>
public sealed class DdcInputProvider
    : IConfigurableProvider,
        IAvailableProvider,
        IConvergentProvider,
        IHealthReportingProvider
{
    /// <summary>
    /// The MCCS 2.2 standard input-select values common enough to name.
    /// </summary>
    /// <remarks>
    /// Not exhaustive, and deliberately not: an input outside this table is
    /// still reachable by writing its VCP value directly, and whether that
    /// value is real is decided by the monitor's own reported capabilities,
    /// not by this table's size.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, uint> KnownInputs = new Dictionary<string, uint>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["VGA1"] = 0x01,
        ["VGA2"] = 0x02,
        ["DVI1"] = 0x03,
        ["DVI2"] = 0x04,
        ["DisplayPort1"] = 0x0F,
        ["DisplayPort2"] = 0x10,
        ["HDMI1"] = 0x11,
        ["HDMI2"] = 0x12,
    };

    private readonly DdcMonitorConnection _connection;

    public DdcInputProvider(DdcMonitorConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    public string Key => DdcResourceKeys.Input(_connection.Alias);

    public string DisplayName => DdcResourceKeys.DisplayName(_connection.Alias, "input");

    /// <summary>Which configured monitor this is, for a screen that lists them.</summary>
    public string Alias => _connection.Alias;

    /// <summary>Also exposed publicly for a settings screen's input picker.</summary>
    public static IReadOnlyDictionary<string, uint> KnownInputNames => KnownInputs;

    /// <summary>Shared with the brightness resource. One monitor, one section.</summary>
    public string SettingsKey => DdcMonitorCatalog.SectionKey;

    public bool TryConfigure(JsonElement? settings, out string? problem) =>
        _connection.TryConfigure(settings, out problem);

    /// <summary>Whether this monitor is configured and reports DDC/CI input-select support.</summary>
    public ResourceAvailability AvailabilityOf(string target)
    {
        if (!_connection.IsConfigured)
        {
            return _connection.Availability;
        }

        if (!_connection.TryGetCapabilities(out DdcCapabilities capabilities, out string? problem))
        {
            return ResourceAvailability.Unavailable(problem!);
        }

        return capabilities.Supports(DdcVcpCode.InputSource)
            ? ResourceAvailability.Available
            : ResourceAvailability.Unavailable(
                $"monitor '{_connection.Alias}' does not report DDC/CI input-select (VCP 0x60) support."
            );
    }

    /// <summary>
    /// Verified, with a little more patience than brightness: switching input
    /// can make a panel briefly drop and renegotiate its signal.
    /// </summary>
    public ConvergencePolicy PolicyFor(string target) =>
        new()
        {
            Verify = true,
            MaxAttempts = 3,
            RetryInterval = TimeSpan.FromSeconds(3),
            PollInterval = TimeSpan.FromMilliseconds(300),
            Settle = TimeSpan.FromMilliseconds(500),
        };

    public bool IsValidValue(string value, out string? problem)
    {
        if (!TryResolveInput(value, out _))
        {
            problem =
                $"'{value}' is not a DDC/CI input. Give a known name ({string.Join(", ", KnownInputs.Keys)}), "
                + "a decimal VCP value, or a hex value such as \"0x11\".";
            return false;
        }

        problem = null;
        return true;
    }

    public string? Read()
    {
        uint? current = _connection.ReadVcp(DdcVcpCode.InputSource);

        if (current is not uint code)
        {
            return null;
        }

        foreach ((string name, uint value) in KnownInputs)
        {
            if (value == code)
            {
                return name;
            }
        }

        return "0x" + code.ToString("X2", CultureInfo.InvariantCulture);
    }

    public void Apply(string value, CancellationToken cancellationToken)
    {
        if (!TryResolveInput(value, out uint code))
        {
            throw new ArgumentException(
                $"The input cannot be set to '{value}': it is not a DDC/CI input.",
                nameof(value)
            );
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Checked against the monitor's own reported list when it gave one.
        // See the class remarks: a monitor that names no explicit values for
        // VCP 0x60 has not said this value is wrong, only that it did not say
        // which values are right, so the write proceeds and the monitor gets
        // the last word.
        if (
            _connection.TryGetCapabilities(out DdcCapabilities capabilities, out _)
            && capabilities.Features.TryGetValue(DdcVcpCode.InputSource, out DdcVcpCapability? supported)
            && supported.SupportedValues.Count > 0
            && !supported.SupportedValues.Contains(code)
        )
        {
            throw new InvalidOperationException(
                $"Monitor '{_connection.Alias}' does not list '{value.Trim()}' (0x{code:X2}) among its supported inputs."
            );
        }

        _connection.WriteVcp(DdcVcpCode.InputSource, code);
    }

    public string? HealthDetail(string target) =>
        _connection.TryGetCapabilities(out DdcCapabilities capabilities, out string? problem)
            ? $"VCP 0x60 supported: {capabilities.Supports(DdcVcpCode.InputSource)}."
            : problem;

    private static bool TryResolveInput(string value, out uint code)
    {
        string trimmed = (value ?? string.Empty).Trim();

        if (KnownInputs.TryGetValue(trimmed, out code))
        {
            return true;
        }

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)
                && code is > 0 and <= 0xFF;
        }

        return uint.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out code)
            && code is > 0 and <= 0xFF;
    }
}

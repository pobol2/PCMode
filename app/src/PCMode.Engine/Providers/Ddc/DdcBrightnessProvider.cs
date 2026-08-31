using System.Globalization;
using System.Text.Json;

namespace PCMode.Engine;

/// <summary>Sets a configured monitor's DDC/CI brightness (VCP 0x10).</summary>
/// <remarks>
/// The value is the monitor's own luminance scale, written as a whole number
/// from 0 to 100 -- what the DDC/CI specification's brightness feature already
/// is on the monitors this targets, so nothing here invents a second unit to
/// convert to and from.
/// </remarks>
public sealed class DdcBrightnessProvider : IConfigurableProvider, IAvailableProvider, IConvergentProvider, IHealthReportingProvider
{
    private readonly DdcMonitorConnection _connection;

    public DdcBrightnessProvider(DdcMonitorConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    public string Key => DdcResourceKeys.Brightness(_connection.Alias);

    public string DisplayName => DdcResourceKeys.DisplayName(_connection.Alias, "brightness");

    /// <summary>Which configured monitor this is, for a screen that lists them.</summary>
    public string Alias => _connection.Alias;

    /// <summary>Shared with the input resource. One monitor, one section.</summary>
    public string SettingsKey => DdcMonitorCatalog.SectionKey;

    public bool TryConfigure(JsonElement? settings, out string? problem) =>
        _connection.TryConfigure(settings, out problem);

    /// <summary>
    /// Whether this monitor is configured and reports DDC/CI brightness support.
    /// </summary>
    /// <remarks>
    /// A capability probe, not merely a configuration check -- see
    /// <see cref="DdcMonitorConnection.TryGetCapabilities"/>. It is what turns
    /// "hide unsupported controls" into something true rather than aspirational:
    /// a monitor that does not list VCP 0x10 is left out of a transition the
    /// same way a missing one is, with an honest reason either way.
    /// </remarks>
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

        return capabilities.Supports(DdcVcpCode.Brightness)
            ? ResourceAvailability.Available
            : ResourceAvailability.Unavailable(
                $"monitor '{_connection.Alias}' does not report DDC/CI brightness (VCP 0x10) support.");
    }

    /// <summary>
    /// Verified, briefly -- a DDC/CI write is local and fast, unlike a
    /// networked television.
    /// </summary>
    public ConvergencePolicy PolicyFor(string target) => new()
    {
        Verify = true,
        MaxAttempts = 2,
        RetryInterval = TimeSpan.FromSeconds(2),
        PollInterval = TimeSpan.FromMilliseconds(300),
        Settle = TimeSpan.FromMilliseconds(300),
    };

    public bool IsValidValue(string value, out string? problem)
    {
        if (!byte.TryParse((value ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out byte level) ||
            level > 100)
        {
            problem = $"'{value}' is not a DDC/CI brightness. Give a whole number from 0 to 100.";
            return false;
        }

        problem = null;
        return true;
    }

    public string? Read()
    {
        uint? current = _connection.ReadVcp(DdcVcpCode.Brightness);
        return current?.ToString(CultureInfo.InvariantCulture);
    }

    public void Apply(string value, CancellationToken cancellationToken)
    {
        if (!IsValidValue(value, out string? invalid))
        {
            throw new ArgumentException($"The brightness cannot be set to '{value}': {invalid}", nameof(value));
        }

        cancellationToken.ThrowIfCancellationRequested();

        byte level = byte.Parse(value.Trim(), CultureInfo.InvariantCulture);
        _connection.WriteVcp(DdcVcpCode.Brightness, level);
    }

    public string? HealthDetail(string target) =>
        _connection.TryGetCapabilities(out DdcCapabilities capabilities, out string? problem)
            ? $"VCP 0x10 supported: {capabilities.Supports(DdcVcpCode.Brightness)}."
            : problem;
}

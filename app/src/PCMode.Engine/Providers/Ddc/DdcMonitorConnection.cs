using System.Text.Json;

namespace PCMode.Engine;

/// <summary>
/// The one DDC/CI conversation with one configured monitor, shared by its
/// resources.
/// </summary>
/// <remarks>
/// Brightness and input are two resources but one physical monitor and one
/// I2C bus, so both share this rather than each resolving the monitor and
/// probing capabilities on their own -- the same division
/// <see cref="LgTvConnection"/> keeps for one television's four resources.
///
/// EVERY OPERATION RESOLVES FRESH
///
/// No physical monitor handle survives a call: see <see cref="IDdcMonitors"/>.
/// This class holds only the alias and the device path it was configured
/// with, never a handle.
///
/// SERIALIZED, NOT CONCURRENT
///
/// Most panels do not answer two DDC/CI requests on their I2C bus at once. A
/// lock keeps this connection's own operations one at a time; it says nothing
/// about another connection talking to a different monitor, which proceeds
/// independently.
/// </remarks>
public sealed class DdcMonitorConnection
{
    /// <summary>How long a read or a capability probe may take.</summary>
    /// <remarks>
    /// Short, because the answer that matters most is "it did not answer", and
    /// that is what an asleep or disconnected monitor looks like. Reads also
    /// happen on every poll of every convergence loop and every dry run, so
    /// bounding them generously would make an unlucky one cost every caller
    /// that follows.
    /// </remarks>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long a command that changes something may take.</summary>
    /// <remarks>
    /// Longer than a read, because it is measuring the monitor actually doing
    /// as it was told rather than merely answering -- some panels visibly
    /// step a brightness change rather than jumping to it.
    /// </remarks>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(4);

    private readonly IDdcMonitors _monitors;
    private readonly string _alias;
    private readonly Lock _gate = new();

    private string? _devicePath;

    public DdcMonitorConnection(IDdcMonitors monitors, string alias)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        _monitors = monitors;
        _alias = alias.Trim();
    }

    /// <summary>The name this monitor was configured under.</summary>
    public string Alias => _alias;

    /// <summary>Whether this alias resolved to a device path when last configured.</summary>
    public bool IsConfigured => _devicePath is not null;

    /// <summary>
    /// Takes this monitor's device path out of the shared section.
    /// </summary>
    /// <remarks>
    /// Called once per provider that reads the section, which is twice per
    /// monitor -- see <see cref="ApplicationCatalog"/>'s remarks for why that
    /// is harmless and deliberate.
    /// </remarks>
    public bool TryConfigure(JsonElement? settings, out string? problem)
    {
        if (!DdcMonitorCatalog.TryRead(settings, out IReadOnlyDictionary<string, string> monitors, out problem))
        {
            return false;
        }

        if (!monitors.TryGetValue(_alias, out string? devicePath))
        {
            problem = $"'{DdcMonitorCatalog.SettingsPath}' does not define monitor '{_alias}'.";
            return false;
        }

        _devicePath = devicePath;
        problem = null;
        return true;
    }

    /// <summary>
    /// Whether there is a monitor here at all, as far as configuration alone can
    /// say.
    /// </summary>
    /// <remarks>
    /// Two answers, like <see cref="LgTvConnection.Availability"/>: an alias
    /// nobody configured is <see cref="AvailabilityState.Disabled"/>, settled
    /// and safe to leave a mode built around. A configured alias is
    /// <see cref="AvailabilityState.Unknown"/> until it has actually been
    /// probed -- see <see cref="TryGetCapabilities"/>, which is what a
    /// provider's own <c>AvailabilityOf</c> does next.
    /// </remarks>
    public ResourceAvailability Availability => IsConfigured
        ? ResourceAvailability.Unknown($"monitor '{_alias}' has not been probed yet.")
        : ResourceAvailability.Disabled(
            $"no DDC/CI monitor '{_alias}' is configured under '{DdcMonitorCatalog.SettingsPath}'.");

    /// <summary>
    /// This monitor's capabilities, bounded, or the reason they could not be
    /// read.
    /// </summary>
    public bool TryGetCapabilities(out DdcCapabilities capabilities, out string? problem)
    {
        string devicePath = Require();

        lock (_gate)
        {
            DdcCapabilities? result = _monitors.ReadCapabilities(devicePath, ProbeTimeout);

            if (result is null)
            {
                capabilities = DdcCapabilities.None;
                problem =
                    $"monitor '{_alias}' did not answer a DDC/CI capability request within " +
                    $"{ProbeTimeout.TotalSeconds:0.#}s. It may be asleep, disconnected, or not present.";
                return false;
            }

            capabilities = result;
            problem = null;
            return true;
        }
    }

    /// <summary>One VCP feature's current value, or null when it could not be read.</summary>
    public uint? ReadVcp(DdcVcpCode code)
    {
        if (_devicePath is not { } devicePath)
        {
            return null;
        }

        lock (_gate)
        {
            return _monitors.GetVcpFeature(devicePath, code, ProbeTimeout)?.Current;
        }
    }

    /// <summary>Writes one VCP feature. Throws on failure -- see <see cref="IDdcMonitors.SetVcpFeature"/>.</summary>
    public void WriteVcp(DdcVcpCode code, uint value)
    {
        string devicePath = Require();

        lock (_gate)
        {
            _monitors.SetVcpFeature(devicePath, code, value, CommandTimeout);
        }
    }

    private string Require() => _devicePath
        ?? throw new InvalidOperationException(
            $"No DDC/CI monitor is configured for '{_alias}'. Add one under '{DdcMonitorCatalog.SettingsPath}'.");
}

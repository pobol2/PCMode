namespace PCMode.Engine.Tests;

/// <summary>A DDC/CI monitor bus that exists only inside the test.</summary>
/// <remarks>
/// Every case worth having about DDC/CI is a case that cannot be arranged on
/// the machine running the suite: a monitor that never answers, one that
/// reports no capabilities, one that refuses a value it did not enumerate.
/// This answers the way the real Windows API does -- present monitors, a
/// capability string per monitor, current values per VCP code -- without ever
/// touching an actual screen.
/// </remarks>
internal sealed class FakeDdcMonitors : IDdcMonitors
{
    private readonly HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DdcCapabilities> _capabilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<DdcVcpCode, uint>> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every value actually written, in the order it was written.</summary>
    public List<(string DevicePath, DdcVcpCode Code, uint Value)> Writes { get; } = [];

    /// <summary>Every timeout a caller asked for, whichever member it belonged to.</summary>
    public List<TimeSpan> RequestedTimeouts { get; } = [];

    /// <summary>Whether a capability probe should behave as a monitor that never answers.</summary>
    public bool TimeOutCapabilities { get; set; }

    /// <summary>Whether a VCP read should behave as a monitor that never answers.</summary>
    public bool TimeOutReads { get; set; }

    /// <summary>Whether a VCP write should behave as a monitor that never answers.</summary>
    public bool TimeOutWrites { get; set; }

    /// <summary>
    /// Per (device, code), what the monitor's own current value becomes after an
    /// otherwise-accepted write, when that differs from what was written.
    /// </summary>
    /// <remarks>
    /// A monitor that clamps or rounds a value rather than rejecting it: the
    /// command does not throw, but the readback disagrees with what was sent.
    /// Without this, <see cref="SetVcpFeature"/> can only ever agree with its own
    /// write, which cannot exercise a caller's honesty about what it reads back
    /// afterward.
    /// </remarks>
    public Dictionary<(string DevicePath, DdcVcpCode Code), uint> ClampsWritesTo { get; } = [];

    public void AddMonitor(string devicePath, DdcCapabilities capabilities)
    {
        _present.Add(devicePath);
        _capabilities[devicePath] = capabilities;
    }

    public void SetCurrentValue(string devicePath, DdcVcpCode code, uint value)
    {
        if (!_values.TryGetValue(devicePath, out Dictionary<DdcVcpCode, uint>? values))
        {
            values = [];
            _values[devicePath] = values;
        }

        values[code] = value;
    }

    public IReadOnlyList<string> KnownDevicePaths(TimeSpan within)
    {
        RequestedTimeouts.Add(within);
        return [.. _present];
    }

    public DdcCapabilities? ReadCapabilities(string devicePath, TimeSpan within)
    {
        RequestedTimeouts.Add(within);

        if (TimeOutCapabilities || !_present.Contains(devicePath))
        {
            return null;
        }

        return _capabilities.TryGetValue(devicePath, out DdcCapabilities? capabilities)
            ? capabilities
            : DdcCapabilities.None;
    }

    public DdcVcpValue? GetVcpFeature(string devicePath, DdcVcpCode code, TimeSpan within)
    {
        RequestedTimeouts.Add(within);

        if (TimeOutReads || !_present.Contains(devicePath))
        {
            return null;
        }

        return
            _values.TryGetValue(devicePath, out Dictionary<DdcVcpCode, uint>? values)
            && values.TryGetValue(code, out uint value)
            ? new DdcVcpValue(value, 100)
            : null;
    }

    public void SetVcpFeature(string devicePath, DdcVcpCode code, uint value, TimeSpan within)
    {
        RequestedTimeouts.Add(within);

        if (TimeOutWrites)
        {
            throw new InvalidOperationException(
                $"The monitor did not respond to VCP 0x{(byte)code:X2} within {within.TotalSeconds:0.#}s. "
                    + "It may be asleep, disconnected, or not DDC/CI compliant."
            );
        }

        if (!_present.Contains(devicePath))
        {
            throw new InvalidOperationException($"No DDC/CI monitor is currently present at '{devicePath}'.");
        }

        Writes.Add((devicePath, code, value));

        uint stored = ClampsWritesTo.TryGetValue((devicePath, code), out uint clamped) ? clamped : value;
        SetCurrentValue(devicePath, code, stored);
    }
}

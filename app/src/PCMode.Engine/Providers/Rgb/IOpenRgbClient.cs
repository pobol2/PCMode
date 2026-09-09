namespace PCMode.Engine;

/// <summary>One OpenRGB LED exposed by a controller.</summary>
public sealed record OpenRgbLedInfo(int Index, string Name);

/// <summary>One OpenRGB controller discovered through the external SDK server.</summary>
public sealed record OpenRgbControllerInfo(
    int Index,
    string Identity,
    string Name,
    string? Vendor,
    string Serial,
    string Location,
    IReadOnlyList<string> Modes
)
{
    /// <summary>LEDs currently exposed by the controller, in SDK order.</summary>
    public IReadOnlyList<OpenRgbLedInfo> Leds { get; init; } = [];

    /// <summary>Modes that expose individual LED colours through the SDK.</summary>
    public IReadOnlySet<string> PerLedModes { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One reliably readable OpenRGB controller state.</summary>
public sealed record OpenRgbDeviceState(int Index, string Mode, string Color)
{
    /// <summary>
    /// Exact LED colours when the active mode exposes reliable per-LED state.
    /// Null means the controller state is represented only by <see cref="Color"/>.
    /// </summary>
    public IReadOnlyList<string>? LedColors { get; init; }
}

/// <summary>One requested OpenRGB controller state.</summary>
public sealed record OpenRgbDeviceTarget(int Index, string Mode, string Color)
{
    /// <summary>Optional exact per-LED colours, in the controller's SDK LED order.</summary>
    public IReadOnlyList<string>? LedColors { get; init; }
}

/// <summary>The OpenRGB SDK operations case lighting needs.</summary>
public interface IOpenRgbClient
{
    /// <summary>Checks that the external OpenRGB SDK server can be reached.</summary>
    void Probe(TimeSpan within);

    /// <summary>Returns the controllers currently exposed by the external OpenRGB SDK server.</summary>
    IReadOnlyList<OpenRgbControllerInfo> DiscoverControllers(TimeSpan within)
    {
        Probe(within);
        return [];
    }

    /// <summary>
    /// Reads the current mode and colour state for each requested controller.
    /// Returns null when any requested controller cannot be read reliably.
    /// </summary>
    IReadOnlyList<OpenRgbDeviceState>? ReadStates(IReadOnlyList<int> deviceIndices, TimeSpan within) => null;

    /// <summary>
    /// Reads one common colour from resolved OpenRGB device/mode pairs.
    /// Returns null when the current state cannot be read reliably or the devices disagree.
    /// </summary>
    string? ReadColor(IReadOnlyList<KeyValuePair<int, string>> devices, TimeSpan within) => null;

    /// <summary>Sets one colour on resolved OpenRGB device/mode pairs.</summary>
    void SetColor(
        IReadOnlyList<KeyValuePair<int, string>> devices,
        string rgb,
        TimeSpan within,
        CancellationToken cancellationToken
    );

    /// <summary>Sets a mode and colour state independently for each resolved controller.</summary>
    void SetStates(IReadOnlyList<OpenRgbDeviceTarget> devices, TimeSpan within, CancellationToken cancellationToken)
    {
        foreach (OpenRgbDeviceTarget device in devices)
        {
            SetColor(
                [new KeyValuePair<int, string>(device.Index, device.Mode)],
                device.Color,
                within,
                cancellationToken
            );
        }
    }
}

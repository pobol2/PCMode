using System.Globalization;
using SdkClient = OpenRGB.NET.OpenRgbClient;
using SdkColor = OpenRGB.NET.Color;
using SdkColorMode = OpenRGB.NET.ColorMode;
using SdkDevice = OpenRGB.NET.Device;
using SdkMode = OpenRGB.NET.Mode;

namespace PCMode.Engine;

/// <summary>Connects PCMode to the external OpenRGB SDK server.</summary>
public sealed class OpenRgbSdkClient : IOpenRgbClient
{
    private const string Host = "127.0.0.1";
    private const int Port = 6742;
    private const string ClientName = "PCMode";

    public void Probe(TimeSpan within)
    {
        using SdkClient client = Connect(within, CancellationToken.None);
        _ = client.GetControllerCount();
    }

    public IReadOnlyList<OpenRgbControllerInfo> DiscoverControllers(TimeSpan within)
    {
        using SdkClient client = Connect(within, CancellationToken.None);
        SdkDevice[] devices = client.GetAllControllerData();

        return
        [
            .. devices.Select(device => new OpenRgbControllerInfo(
                device.Index,
                StableIdentity(device),
                device.Name,
                device.Vendor,
                device.Serial,
                device.Location,
                [.. device.Modes.Select(mode => mode.Name)]
            )
            {
                Leds =
                [
                    .. device.Leds.Select(led => new OpenRgbLedInfo(
                        led.Index,
                        string.IsNullOrWhiteSpace(led.Name) ? $"LED {led.Index + 1}" : led.Name
                    )),
                ],
                PerLedModes = new HashSet<string>(
                    device.Modes.Where(mode => mode.ColorMode == SdkColorMode.PerLed).Select(mode => mode.Name),
                    StringComparer.OrdinalIgnoreCase
                ),
            }),
        ];
    }

    public IReadOnlyList<OpenRgbDeviceState>? ReadStates(IReadOnlyList<int> deviceIndices, TimeSpan within)
    {
        ArgumentNullException.ThrowIfNull(deviceIndices);

        using SdkClient client = Connect(within, CancellationToken.None);
        SdkDevice[] controllers = client.GetAllControllerData();
        var states = new List<OpenRgbDeviceState>(deviceIndices.Count);

        foreach (int deviceIndex in deviceIndices)
        {
            SdkDevice? device = controllers.FirstOrDefault(controller => controller.Index == deviceIndex);

            if (device is null || device.ActiveModeIndex < 0 || device.ActiveModeIndex >= device.Modes.Length)
            {
                return null;
            }

            switch (device.ActiveMode.ColorMode)
            {
                case SdkColorMode.ModeSpecific:
                {
                    string? colour = ReadUniformColor(device.ActiveMode.Colors);

                    if (colour is null)
                    {
                        return null;
                    }

                    states.Add(new OpenRgbDeviceState(deviceIndex, device.ActiveMode.Name, colour));
                    break;
                }

                case SdkColorMode.PerLed:
                {
                    if (device.Leds.Length == 0 || device.Colors.Length != device.Leds.Length)
                    {
                        return null;
                    }

                    string[] ledColors = [.. device.Colors.Select(ToRgb)];
                    string uniform = ReadUniformColor(device.Colors) ?? string.Empty;

                    states.Add(
                        new OpenRgbDeviceState(deviceIndex, device.ActiveMode.Name, uniform) { LedColors = ledColors }
                    );
                    break;
                }

                default:
                    return null;
            }
        }

        return states;
    }

    public string? ReadColor(IReadOnlyList<KeyValuePair<int, string>> devices, TimeSpan within)
    {
        ArgumentNullException.ThrowIfNull(devices);

        if (devices.Count == 0)
        {
            return null;
        }

        IReadOnlyList<OpenRgbDeviceState>? states = ReadStates([.. devices.Select(device => device.Key)], within);

        if (states is null || states.Count != devices.Count)
        {
            return null;
        }

        string? agreed = null;

        for (int i = 0; i < devices.Count; i++)
        {
            KeyValuePair<int, string> expected = devices[i];
            OpenRgbDeviceState current = states[i];

            if (
                current.Index != expected.Key
                || !string.Equals(current.Mode, expected.Value, StringComparison.OrdinalIgnoreCase)
                || !IsRgb(current.Color)
            )
            {
                return null;
            }

            if (agreed is null)
            {
                agreed = current.Color;
            }
            else if (!string.Equals(agreed, current.Color, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return agreed;
    }

    public void SetColor(
        IReadOnlyList<KeyValuePair<int, string>> devices,
        string rgb,
        TimeSpan within,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentException.ThrowIfNullOrWhiteSpace(rgb);

        SetStatesCore(
            [.. devices.Select(device => new OpenRgbDeviceTarget(device.Key, device.Value, rgb))],
            within,
            cancellationToken
        );
    }

    public void SetStates(
        IReadOnlyList<OpenRgbDeviceTarget> devices,
        TimeSpan within,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(devices);
        SetStatesCore(devices, within, cancellationToken);
    }

    private static void SetStatesCore(
        IReadOnlyList<OpenRgbDeviceTarget> devices,
        TimeSpan within,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SdkClient client = Connect(within, cancellationToken);
        int controllerCount = client.GetControllerCount();

        foreach (OpenRgbDeviceTarget requested in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (requested.Index < 0 || requested.Index >= controllerCount)
            {
                throw new InvalidOperationException(
                    $"Configured OpenRGB device {requested.Index} does not exist. "
                        + $"The SDK server currently reports {controllerCount} device(s)."
                );
            }

            SdkDevice device = client.GetControllerData(requested.Index);
            int modeIndex = Array.FindIndex(
                device.Modes,
                mode => string.Equals(mode.Name, requested.Mode, StringComparison.OrdinalIgnoreCase)
            );

            if (modeIndex < 0)
            {
                throw new InvalidOperationException(
                    $"OpenRGB device {requested.Index} ('{device.Name}') does not have mode '{requested.Mode}'."
                );
            }

            ApplyModeAndColor(
                client,
                requested.Index,
                device,
                modeIndex,
                ParseColor(requested.Color),
                requested.LedColors
            );
        }
    }

    private static string StableIdentity(SdkDevice device)
    {
        string vendor = NormalizeIdentityPart(device.Vendor);
        string name = NormalizeIdentityPart(device.Name);

        if (!string.IsNullOrWhiteSpace(device.Serial))
        {
            return $"serial:{vendor}/{name}/{NormalizeIdentityPart(device.Serial)}";
        }

        if (!string.IsNullOrWhiteSpace(device.Location))
        {
            return $"location:{vendor}/{name}/{NormalizeIdentityPart(device.Location)}";
        }

        return $"metadata:{NormalizeIdentityPart(device.Type.ToString())}/"
            + $"{vendor}/{name}/{NormalizeIdentityPart(device.Description)}/"
            + $"{NormalizeIdentityPart(device.Version)}";
    }

    private static string NormalizeIdentityPart(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return Uri.EscapeDataString(normalized);
    }

    private static string? ReadUniformColor(IReadOnlyList<SdkColor> colors)
    {
        if (colors.Count == 0)
        {
            return null;
        }

        SdkColor first = colors[0];

        if (colors.Any(color => color.R != first.R || color.G != first.G || color.B != first.B))
        {
            return null;
        }

        return ToRgb(first);
    }

    private static string ToRgb(SdkColor color) => $"{color.R:X2}{color.G:X2}{color.B:X2}";

    private static void ApplyModeAndColor(
        SdkClient client,
        int deviceIndex,
        SdkDevice device,
        int modeIndex,
        SdkColor color,
        IReadOnlyList<string>? requestedLedColors
    )
    {
        SdkMode mode = device.Modes[modeIndex];

        switch (mode.ColorMode)
        {
            case SdkColorMode.ModeSpecific:
                if (requestedLedColors is { Count: > 0 })
                {
                    throw new InvalidOperationException(
                        $"OpenRGB device {deviceIndex} ('{device.Name}') mode '{mode.Name}' does not support per-LED colours."
                    );
                }

                if (mode.Colors.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"OpenRGB device {deviceIndex} ('{device.Name}') mode '{mode.Name}' exposes no effect colours."
                    );
                }

                SdkColor[] modeColors = Enumerable.Repeat(color, mode.Colors.Length).ToArray();
                client.UpdateMode(deviceIndex, modeIndex, colors: modeColors);
                return;

            case SdkColorMode.PerLed:
                if (device.Leds.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"OpenRGB device {deviceIndex} ('{device.Name}') mode '{mode.Name}' exposes no LEDs."
                    );
                }

                SdkColor[] ledColors;

                if (requestedLedColors is { Count: > 0 })
                {
                    if (requestedLedColors.Count != device.Leds.Length)
                    {
                        throw new InvalidOperationException(
                            $"OpenRGB device {deviceIndex} ('{device.Name}') exposes {device.Leds.Length} LED(s), "
                                + $"but the scene contains {requestedLedColors.Count} LED colour(s)."
                        );
                    }

                    ledColors = [.. requestedLedColors.Select(ParseColor)];
                }
                else
                {
                    ledColors = Enumerable.Repeat(color, device.Leds.Length).ToArray();
                }

                client.UpdateMode(deviceIndex, modeIndex);
                client.UpdateLeds(deviceIndex, ledColors);
                return;

            case SdkColorMode.None:
            case SdkColorMode.Random:
                if (requestedLedColors is { Count: > 0 })
                {
                    throw new InvalidOperationException(
                        $"OpenRGB device {deviceIndex} ('{device.Name}') mode '{mode.Name}' does not support per-LED colours."
                    );
                }

                client.UpdateMode(deviceIndex, modeIndex);
                return;

            default:
                throw new InvalidOperationException(
                    $"OpenRGB device {deviceIndex} ('{device.Name}') mode '{mode.Name}' has an unsupported colour mode."
                );
        }
    }

    private static SdkClient Connect(TimeSpan within, CancellationToken cancellationToken)
    {
        int timeoutMs = checked((int)within.TotalMilliseconds);
        cancellationToken.ThrowIfCancellationRequested();

        var client = new SdkClient(ip: Host, port: Port, name: ClientName, autoConnect: false, timeoutMs: timeoutMs);

        try
        {
            client.Connect();
            cancellationToken.ThrowIfCancellationRequested();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static SdkColor ParseColor(string rgb)
    {
        string value = rgb.Trim();

        if (!IsRgb(value))
        {
            throw new ArgumentException("OpenRGB colours must be six hexadecimal digits (RRGGBB).", nameof(rgb));
        }

        byte red = byte.Parse(value.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte green = byte.Parse(value.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte blue = byte.Parse(value.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new SdkColor(red, green, blue);
    }

    private static bool IsRgb(string value) => value.Length == 6 && value.All(char.IsAsciiHexDigit);
}

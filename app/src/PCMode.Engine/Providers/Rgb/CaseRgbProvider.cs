using System.Text.Json;

namespace PCMode.Engine;

public enum CaseRgbDeviceState
{
    Ready,
    Missing,
    Ambiguous,
}

public sealed record CaseRgbConfiguredDevice(int Index, string Mode, string? Identity);

public sealed record CaseRgbSceneDevice(int Index, string Mode, string Color)
{
    /// <summary>Optional exact per-LED colours in the controller's SDK LED order.</summary>
    public IReadOnlyList<string>? LedColors { get; init; }
}

public sealed record CaseRgbScene(string Name, IReadOnlyList<CaseRgbSceneDevice> Devices);

public sealed record CaseRgbDeviceResolution(
    CaseRgbConfiguredDevice Configured,
    CaseRgbDeviceState State,
    OpenRgbControllerInfo? Controller,
    string Detail
);

/// <summary>Sets case lighting to either one RGB colour or one configured named scene.</summary>
public sealed class CaseRgbProvider : IConfigurableProvider, IConvergentProvider, IAvailableProvider
{
    public const string ResourceKey = "caseRgb";
    public const string SettingsPath = "providers.caseRgb";

    private static readonly TimeSpan DefaultWithin = TimeSpan.FromSeconds(4);

    private readonly IOpenRgbClient _client;
    private bool _enabled;
    private string? _executable;
    private IReadOnlyList<CaseRgbConfiguredDevice> _configuredDevices = [];
    private IReadOnlyList<KeyValuePair<int, string>> _devices = [];
    private IReadOnlyDictionary<string, CaseRgbScene> _scenes = new Dictionary<string, CaseRgbScene>(
        StringComparer.OrdinalIgnoreCase
    );
    private TimeSpan _within = DefaultWithin;

    public CaseRgbProvider(IOpenRgbClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public string Key => ResourceKey;
    public string DisplayName => "Case lighting";
    public bool Enabled => _enabled;

    /// <summary>Legacy OpenRGB.exe path retained for configuration compatibility; the SDK transport ignores it.</summary>
    public string? Executable => _executable;

    /// <summary>Legacy controller-index/mode view retained for existing callers.</summary>
    public IReadOnlyList<KeyValuePair<int, string>> Devices => _devices;

    public IReadOnlyList<CaseRgbConfiguredDevice> ConfiguredDevices => _configuredDevices;
    public IReadOnlyDictionary<string, CaseRgbScene> Scenes => _scenes;
    public int WithinMs => (int)_within.TotalMilliseconds;

    public bool TryConfigure(JsonElement? settings, out string? problem)
    {
        _enabled = false;
        _executable = null;
        _configuredDevices = [];
        _devices = [];
        _scenes = new Dictionary<string, CaseRgbScene>(StringComparer.OrdinalIgnoreCase);
        _within = DefaultWithin;

        if (settings is not JsonElement element || element.ValueKind == JsonValueKind.Null)
        {
            problem = null;
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = $"'{SettingsPath}' must be an object.";
            return false;
        }

        bool enabled = false;
        string? executable = null;
        List<CaseRgbConfiguredDevice>? devices = null;
        Dictionary<string, CaseRgbScene>? scenes = null;
        TimeSpan within = DefaultWithin;

        foreach (JsonProperty field in element.EnumerateObject())
        {
            if (field.NameEquals("enabled"))
            {
                if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    problem = $"'{SettingsPath}.enabled' must be true or false.";
                    return false;
                }

                enabled = field.Value.ValueKind == JsonValueKind.True;
            }
            else if (field.NameEquals("executable"))
            {
                if (field.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.Value.GetString()))
                {
                    problem = $"'{SettingsPath}.executable' must be a non-empty string when it is present.";
                    return false;
                }

                executable = Environment.ExpandEnvironmentVariables(field.Value.GetString()!.Trim());
            }
            else if (field.NameEquals("devices"))
            {
                if (!TryReadDevices(field.Value, out devices, out problem))
                {
                    return false;
                }
            }
            else if (field.NameEquals("scenes"))
            {
                if (!TryReadScenes(field.Value, out scenes, out problem))
                {
                    return false;
                }
            }
            else if (field.NameEquals("within"))
            {
                if (
                    field.Value.ValueKind != JsonValueKind.Number
                    || !field.Value.TryGetInt32(out int milliseconds)
                    || milliseconds < 500
                    || milliseconds > 60_000
                )
                {
                    problem = $"'{SettingsPath}.within' must be a whole number of milliseconds between 500 and 60000.";
                    return false;
                }

                within = TimeSpan.FromMilliseconds(milliseconds);
            }
            else
            {
                problem =
                    $"'{SettingsPath}' does not understand '{field.Name}'. "
                    + "It takes 'enabled', 'executable', 'devices', 'scenes' and 'within'.";
                return false;
            }
        }

        if (enabled && (devices is null || devices.Count == 0))
        {
            problem = $"'{SettingsPath}' is switched on but lists no devices, so there would be nothing to colour.";
            return false;
        }

        devices ??= [];
        scenes ??= new Dictionary<string, CaseRgbScene>(StringComparer.OrdinalIgnoreCase);

        if (!ValidateScenes(devices, scenes, out problem))
        {
            return false;
        }

        _enabled = enabled;
        _executable = executable;
        _configuredDevices = devices;
        _devices = [.. devices.Select(device => new KeyValuePair<int, string>(device.Index, device.Mode))];
        _scenes = scenes;
        _within = within;
        problem = null;
        return true;
    }

    public IReadOnlyList<OpenRgbControllerInfo> DiscoverControllers() => _client.DiscoverControllers(_within);

    public IReadOnlyList<CaseRgbDeviceResolution> ResolveConfiguredDevices()
    {
        IReadOnlyList<OpenRgbControllerInfo> controllers = _client.DiscoverControllers(_within);
        return ResolveConfiguredDevices(_configuredDevices, controllers);
    }

    public ResourceAvailability AvailabilityOf(string target)
    {
        if (!_enabled)
        {
            return ResourceAvailability.Disabled(
                $"case lighting is switched off. Set '{SettingsPath}.enabled' to true to use it."
            );
        }

        if (!TryGetTargetDevices(target, out IReadOnlyList<CaseRgbConfiguredDevice>? configured, out string? invalid))
        {
            return ResourceAvailability.Unavailable(invalid!);
        }

        try
        {
            // Legacy index-only configuration is trusted the same way Apply()
            // trusts it -- directly, without requiring the SDK's controller
            // enumeration to name-match it -- so a target is never reported
            // unavailable here when it would in fact be applied. The server
            // still has to answer, so reachability is checked either way.
            IReadOnlyList<CaseRgbConfiguredDevice> devices = configured!;
            IReadOnlyList<CaseRgbDeviceResolution> resolutions;

            if (devices.Any(device => device.Identity is not null))
            {
                IReadOnlyList<OpenRgbControllerInfo> controllers = _client.DiscoverControllers(_within);
                resolutions = ResolveConfiguredDevices(devices, controllers);
            }
            else
            {
                _client.Probe(_within);
                resolutions =
                [
                    .. devices.Select(device => new CaseRgbDeviceResolution(
                        device,
                        CaseRgbDeviceState.Ready,
                        null,
                        $"legacy OpenRGB controller index {device.Index} will be used directly."
                    )),
                ];
            }

            CaseRgbDeviceResolution[] problems =
            [
                .. resolutions.Where(resolution => resolution.State != CaseRgbDeviceState.Ready),
            ];

            if (problems.Length > 0)
            {
                return ResourceAvailability.Unavailable(
                    string.Join(" ", problems.Select(problem => $"{problem.State}: {problem.Detail}"))
                );
            }

            if (
                _scenes.TryGetValue(target.Trim(), out CaseRgbScene? scene)
                && !TryValidateResolvedScene(scene, resolutions, out string? sceneProblem)
            )
            {
                return ResourceAvailability.Unavailable(sceneProblem!);
            }

            return new ResourceAvailability(
                AvailabilityState.Available,
                $"Ready: {resolutions.Count} configured OpenRGB controller(s) resolved."
            );
        }
        catch (Exception exception)
        {
            return ResourceAvailability.Unavailable(
                $"the external OpenRGB SDK server is not reachable on localhost:6742. {exception.Message}"
            );
        }
    }

    public ConvergencePolicy PolicyFor(string target) =>
        new()
        {
            Verify = true,
            MaxAttempts = 1,
            RetryInterval = TimeSpan.FromSeconds(1),
            PollInterval = TimeSpan.FromMilliseconds(100),
        };

    public bool IsValidValue(string value, out string? problem)
    {
        string target = (value ?? string.Empty).Trim();

        if (target.Length == 0)
        {
            problem =
                "the case lighting target has no colour. Give six hex digits such as 0020FF, or a configured scene name.";
            return false;
        }

        if (target.StartsWith('#'))
        {
            problem = $"The case lighting target does not take a leading '#'. Write {target.TrimStart('#')} instead.";
            return false;
        }

        if (IsRgb(target) || _scenes.ContainsKey(target))
        {
            problem = null;
            return true;
        }

        problem = $"'{target}' is neither a six-digit hex colour (RRGGBB) nor a configured RGB scene.";
        return false;
    }

    /// <summary>
    /// Returns a scene name when one complete scene matches reliably; otherwise
    /// preserves direct RRGGBB readback for the base configured modes.
    /// </summary>
    public string? Read()
    {
        if (!_enabled || _configuredDevices.Count == 0)
        {
            return null;
        }

        try
        {
            if (_scenes.Count == 0)
            {
                IReadOnlyList<KeyValuePair<int, string>> resolved = ResolveDirectForApply();
                return _client.ReadColor(resolved, _within);
            }

            IReadOnlyList<OpenRgbControllerInfo> controllers = _client.DiscoverControllers(_within);
            IReadOnlyList<CaseRgbDeviceResolution> baseResolutions = ResolveConfiguredDevices(
                _configuredDevices,
                controllers
            );

            if (
                baseResolutions.Count != _configuredDevices.Count
                || baseResolutions.Any(resolution => resolution.State != CaseRgbDeviceState.Ready)
            )
            {
                return null;
            }

            Dictionary<int, int> runtimeIndices = baseResolutions.ToDictionary(
                resolution => resolution.Configured.Index,
                resolution => resolution.Controller!.Index
            );

            IReadOnlyList<int> indices = [.. _configuredDevices.Select(device => runtimeIndices[device.Index])];

            IReadOnlyList<OpenRgbDeviceState>? current = _client.ReadStates(indices, _within);

            if (current is null || current.Count != indices.Count)
            {
                return null;
            }

            Dictionary<int, OpenRgbDeviceState> byIndex = current.ToDictionary(state => state.Index);

            string[] matchingScenes =
            [
                .. _scenes
                    .Values.Where(scene => SceneMatches(scene, runtimeIndices, byIndex))
                    .Select(scene => scene.Name),
            ];

            if (matchingScenes.Length == 1)
            {
                return matchingScenes[0];
            }

            if (matchingScenes.Length > 1)
            {
                return null;
            }

            string? colour = null;

            foreach (CaseRgbDeviceResolution resolution in baseResolutions)
            {
                OpenRgbDeviceState state = byIndex[resolution.Controller!.Index];

                if (!string.Equals(state.Mode, resolution.Configured.Mode, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (!IsRgb(state.Color))
                {
                    return null;
                }

                if (colour is null)
                {
                    colour = state.Color;
                }
                else if (!string.Equals(colour, state.Color, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return colour;
        }
        catch
        {
            return null;
        }
    }

    public void Apply(string value, CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            throw new InvalidOperationException(
                $"Case lighting is switched off on this machine. Set '{SettingsPath}.enabled' to true first."
            );
        }

        if (!IsValidValue(value, out string? invalid))
        {
            throw new ArgumentException($"The case lighting cannot be set to '{value}': {invalid}", nameof(value));
        }

        string target = value.Trim();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (IsRgb(target))
            {
                _client.SetColor(ResolveDirectForApply(), target, _within, cancellationToken);
                return;
            }

            CaseRgbScene scene = _scenes[target];
            IReadOnlyList<CaseRgbConfiguredDevice> configured = SceneConfiguredDevices(scene);
            IReadOnlyList<CaseRgbDeviceResolution> resolutions = ResolveForApply(configured);

            Dictionary<int, CaseRgbDeviceResolution> byConfiguredIndex = resolutions.ToDictionary(resolution =>
                resolution.Configured.Index
            );

            _client.SetStates(
                [
                    .. scene.Devices.Select(device => new OpenRgbDeviceTarget(
                        byConfiguredIndex[device.Index].Controller?.Index ?? device.Index,
                        device.Mode,
                        device.Color
                    )
                    {
                        LedColors = device.LedColors,
                    }),
                ],
                _within,
                cancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"OpenRGB could not set the case lighting to {target}. {exception.Message}",
                exception
            );
        }
    }

    private IReadOnlyList<KeyValuePair<int, string>> ResolveDirectForApply()
    {
        IReadOnlyList<CaseRgbDeviceResolution> resolutions = ResolveForApply(_configuredDevices);
        return
        [
            .. resolutions.Select(resolution => new KeyValuePair<int, string>(
                resolution.Controller?.Index ?? resolution.Configured.Index,
                resolution.Configured.Mode
            )),
        ];
    }

    private IReadOnlyList<CaseRgbDeviceResolution> ResolveForApply(IReadOnlyList<CaseRgbConfiguredDevice> configured)
    {
        if (!configured.Any(device => device.Identity is not null))
        {
            return
            [
                .. configured.Select(device => new CaseRgbDeviceResolution(
                    device,
                    CaseRgbDeviceState.Ready,
                    null,
                    $"legacy OpenRGB controller index {device.Index} will be used directly."
                )),
            ];
        }

        IReadOnlyList<CaseRgbDeviceResolution> resolutions = ResolveConfiguredDevices(
            configured,
            _client.DiscoverControllers(_within)
        );

        CaseRgbDeviceResolution? unresolved = resolutions.FirstOrDefault(resolution =>
            resolution.State != CaseRgbDeviceState.Ready
        );

        if (unresolved is not null)
        {
            throw new InvalidOperationException($"{unresolved.State}: {unresolved.Detail}");
        }

        return resolutions;
    }

    private IReadOnlyList<CaseRgbConfiguredDevice> SceneConfiguredDevices(CaseRgbScene scene)
    {
        Dictionary<int, CaseRgbConfiguredDevice> baseByIndex = _configuredDevices.ToDictionary(device => device.Index);

        return
        [
            .. scene.Devices.Select(device => new CaseRgbConfiguredDevice(
                device.Index,
                device.Mode,
                baseByIndex[device.Index].Identity
            )),
        ];
    }

    private bool TryGetTargetDevices(
        string target,
        out IReadOnlyList<CaseRgbConfiguredDevice>? configured,
        out string? problem
    )
    {
        string normalized = (target ?? string.Empty).Trim();

        if (IsRgb(normalized))
        {
            configured = _configuredDevices;
            problem = null;
            return true;
        }

        if (_scenes.TryGetValue(normalized, out CaseRgbScene? scene))
        {
            configured = SceneConfiguredDevices(scene);
            problem = null;
            return true;
        }

        configured = null;
        problem = $"'{normalized}' is not a configured RGB colour or scene.";
        return false;
    }

    private static IReadOnlyList<CaseRgbDeviceResolution> ResolveConfiguredDevices(
        IReadOnlyList<CaseRgbConfiguredDevice> configuredDevices,
        IReadOnlyList<OpenRgbControllerInfo> controllers
    )
    {
        var resolved = new List<CaseRgbDeviceResolution>(configuredDevices.Count);

        foreach (CaseRgbConfiguredDevice configured in configuredDevices)
        {
            if (configured.Identity is null)
            {
                OpenRgbControllerInfo? controller = controllers.FirstOrDefault(candidate =>
                    candidate.Index == configured.Index
                );

                if (controller is null)
                {
                    resolved.Add(
                        new CaseRgbDeviceResolution(
                            configured,
                            CaseRgbDeviceState.Missing,
                            null,
                            $"legacy OpenRGB controller index {configured.Index} is not currently present."
                        )
                    );
                    continue;
                }

                if (!HasMode(controller, configured.Mode))
                {
                    resolved.Add(
                        new CaseRgbDeviceResolution(
                            configured,
                            CaseRgbDeviceState.Missing,
                            controller,
                            $"controller {configured.Index} ('{controller.Name}') is present but mode '{configured.Mode}' is missing."
                        )
                    );
                    continue;
                }

                resolved.Add(
                    new CaseRgbDeviceResolution(
                        configured,
                        CaseRgbDeviceState.Ready,
                        controller,
                        $"legacy index {configured.Index} resolves to '{controller.Name}'."
                    )
                );
                continue;
            }

            OpenRgbControllerInfo[] matches =
            [
                .. controllers.Where(controller =>
                    string.Equals(controller.Identity, configured.Identity, StringComparison.OrdinalIgnoreCase)
                ),
            ];

            if (matches.Length == 0)
            {
                resolved.Add(
                    new CaseRgbDeviceResolution(
                        configured,
                        CaseRgbDeviceState.Missing,
                        null,
                        $"configured OpenRGB identity '{configured.Identity}' is not currently present."
                    )
                );
                continue;
            }

            if (matches.Length > 1)
            {
                resolved.Add(
                    new CaseRgbDeviceResolution(
                        configured,
                        CaseRgbDeviceState.Ambiguous,
                        null,
                        $"configured OpenRGB identity '{configured.Identity}' matches {matches.Length} controllers."
                    )
                );
                continue;
            }

            OpenRgbControllerInfo match = matches[0];

            if (!HasMode(match, configured.Mode))
            {
                resolved.Add(
                    new CaseRgbDeviceResolution(
                        configured,
                        CaseRgbDeviceState.Missing,
                        match,
                        $"controller '{match.Name}' resolved by identity but mode '{configured.Mode}' is missing."
                    )
                );
                continue;
            }

            resolved.Add(
                new CaseRgbDeviceResolution(
                    configured,
                    CaseRgbDeviceState.Ready,
                    match,
                    $"'{match.Name}' resolved to current SDK controller index {match.Index}."
                )
            );
        }

        return resolved;
    }

    private static bool SceneMatches(
        CaseRgbScene scene,
        IReadOnlyDictionary<int, int> runtimeIndices,
        IReadOnlyDictionary<int, OpenRgbDeviceState> current
    )
    {
        foreach (CaseRgbSceneDevice expected in scene.Devices)
        {
            int runtimeIndex = runtimeIndices[expected.Index];

            if (
                !current.TryGetValue(runtimeIndex, out OpenRgbDeviceState? actual)
                || !string.Equals(actual.Mode, expected.Mode, StringComparison.OrdinalIgnoreCase)
            )
            {
                return false;
            }

            if (expected.LedColors is { Count: > 0 } expectedLeds)
            {
                if (actual.LedColors is not { } actualLeds || actualLeds.Count != expectedLeds.Count)
                {
                    return false;
                }

                for (int i = 0; i < expectedLeds.Count; i++)
                {
                    if (!string.Equals(expectedLeds[i], actualLeds[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }
            else if (!string.Equals(actual.Color, expected.Color, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateResolvedScene(
        CaseRgbScene scene,
        IReadOnlyList<CaseRgbDeviceResolution> resolutions,
        out string? problem
    )
    {
        Dictionary<int, OpenRgbControllerInfo> controllers = resolutions
            .Where(resolution => resolution.Controller is not null)
            .ToDictionary(resolution => resolution.Configured.Index, resolution => resolution.Controller!);

        foreach (CaseRgbSceneDevice device in scene.Devices)
        {
            if (device.LedColors is not { Count: > 0 } ledColors)
            {
                continue;
            }

            if (!controllers.TryGetValue(device.Index, out OpenRgbControllerInfo? controller))
            {
                problem = $"OpenRGB controller {device.Index} could not be resolved for per-LED control.";
                return false;
            }

            if (!controller.PerLedModes.Contains(device.Mode))
            {
                problem =
                    $"OpenRGB controller '{controller.Name}' mode '{device.Mode}' does not expose per-LED colour control.";
                return false;
            }

            if (controller.Leds.Count != ledColors.Count)
            {
                problem =
                    $"OpenRGB controller '{controller.Name}' currently exposes {controller.Leds.Count} LED(s), "
                    + $"but scene '{scene.Name}' contains {ledColors.Count} LED colour(s).";
                return false;
            }
        }

        problem = null;
        return true;
    }

    private static bool HasMode(OpenRgbControllerInfo controller, string mode) =>
        controller.Modes.Any(candidate => string.Equals(candidate, mode, StringComparison.OrdinalIgnoreCase));

    private static bool ValidateScenes(
        IReadOnlyList<CaseRgbConfiguredDevice> configuredDevices,
        IReadOnlyDictionary<string, CaseRgbScene> scenes,
        out string? problem
    )
    {
        HashSet<int> configuredIndices = [.. configuredDevices.Select(device => device.Index)];
        var signatures = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (CaseRgbScene scene in scenes.Values)
        {
            HashSet<int> sceneIndices = [.. scene.Devices.Select(device => device.Index)];

            if (!sceneIndices.SetEquals(configuredIndices))
            {
                int[] missing = [.. configuredIndices.Except(sceneIndices).Order()];
                int[] unknown = [.. sceneIndices.Except(configuredIndices).Order()];

                string detail = string.Join(
                        " ",
                        missing.Length > 0 ? $"Missing device(s): {string.Join(", ", missing)}." : string.Empty,
                        unknown.Length > 0 ? $"Unknown device(s): {string.Join(", ", unknown)}." : string.Empty
                    )
                    .Trim();

                problem = $"RGB scene '{scene.Name}' must define exactly the configured RGB controllers. {detail}";
                return false;
            }

            foreach (CaseRgbSceneDevice device in scene.Devices)
            {
                if (!IsRgb(device.Color))
                {
                    problem = $"RGB scene '{scene.Name}' needs a six-digit RGB colour for controller {device.Index}.";
                    return false;
                }

                if (device.LedColors is { } ledColors)
                {
                    if (ledColors.Count == 0)
                    {
                        problem =
                            $"RGB scene '{scene.Name}' has an empty LED colour list for controller {device.Index}.";
                        return false;
                    }

                    if (ledColors.Any(color => !IsRgb(color)))
                    {
                        problem =
                            $"RGB scene '{scene.Name}' has an invalid per-LED colour for controller {device.Index}.";
                        return false;
                    }
                }
            }

            string signature = SceneSignature(scene);

            if (signatures.TryGetValue(signature, out string? duplicate))
            {
                problem =
                    $"RGB scenes '{duplicate}' and '{scene.Name}' describe the same controller state, "
                    + "so readback could not tell them apart.";
                return false;
            }

            signatures.Add(signature, scene.Name);
        }

        problem = null;
        return true;
    }

    private static string SceneSignature(CaseRgbScene scene) =>
        string.Join(
            "|",
            scene
                .Devices.OrderBy(device => device.Index)
                .Select(device =>
                    $"{device.Index}:{device.Mode.ToUpperInvariant()}:{device.Color.ToUpperInvariant()}:"
                    + (
                        device.LedColors is { Count: > 0 } ledColors
                            ? string.Join(",", ledColors.Select(color => color.ToUpperInvariant()))
                            : "-"
                    )
                )
        );

    private static bool TryReadDevices(
        JsonElement written,
        out List<CaseRgbConfiguredDevice>? devices,
        out string? problem
    )
    {
        devices = null;

        if (written.ValueKind != JsonValueKind.Object)
        {
            problem =
                $"'{SettingsPath}.devices' must map each device number to either a mode string or an object with 'mode' and optional 'identity'.";
            return false;
        }

        var parsed = new SortedDictionary<int, CaseRgbConfiguredDevice>();

        foreach (JsonProperty device in written.EnumerateObject())
        {
            if (!TryReadIndex(device.Name, out int index))
            {
                problem =
                    $"'{SettingsPath}.devices' has a key '{device.Name}' that is not a device number. "
                    + "OpenRGB numbers its SDK controllers from 0.";
                return false;
            }

            string? mode;
            string? identity = null;

            if (device.Value.ValueKind == JsonValueKind.String)
            {
                mode = device.Value.GetString();
            }
            else if (device.Value.ValueKind == JsonValueKind.Object)
            {
                mode = null;

                foreach (JsonProperty field in device.Value.EnumerateObject())
                {
                    if (field.NameEquals("mode"))
                    {
                        if (field.Value.ValueKind != JsonValueKind.String)
                        {
                            problem = $"'{SettingsPath}.devices.{index}.mode' must be a string.";
                            return false;
                        }

                        mode = field.Value.GetString();
                    }
                    else if (field.NameEquals("identity"))
                    {
                        if (
                            field.Value.ValueKind != JsonValueKind.String
                            || string.IsNullOrWhiteSpace(field.Value.GetString())
                        )
                        {
                            problem = $"'{SettingsPath}.devices.{index}.identity' must be a non-empty string.";
                            return false;
                        }

                        identity = field.Value.GetString()!.Trim();
                    }
                    else
                    {
                        problem =
                            $"'{SettingsPath}.devices.{index}' does not understand '{field.Name}'. "
                            + "It takes 'mode' and optional 'identity'.";
                        return false;
                    }
                }
            }
            else
            {
                problem =
                    $"'{SettingsPath}.devices.{index}' must be a mode string or an object with 'mode' and optional 'identity'.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(mode))
            {
                problem =
                    $"'{SettingsPath}.devices.{index}' has no lighting mode. "
                    + "It is commonly 'Direct' or 'Static', and depends on the device.";
                return false;
            }

            if (!parsed.TryAdd(index, new CaseRgbConfiguredDevice(index, mode.Trim(), identity)))
            {
                problem = $"'{SettingsPath}.devices' gives device {index} more than one configuration.";
                return false;
            }
        }

        devices = [.. parsed.Values];
        problem = null;
        return true;
    }

    private static bool TryReadScenes(
        JsonElement written,
        out Dictionary<string, CaseRgbScene>? scenes,
        out string? problem
    )
    {
        scenes = null;

        if (written.ValueKind != JsonValueKind.Object)
        {
            problem = $"'{SettingsPath}.scenes' must be an object keyed by scene name.";
            return false;
        }

        var parsed = new Dictionary<string, CaseRgbScene>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty sceneProperty in written.EnumerateObject())
        {
            string name = sceneProperty.Name.Trim();

            if (name.Length == 0)
            {
                problem = $"'{SettingsPath}.scenes' contains an empty scene name.";
                return false;
            }

            if (IsRgb(name))
            {
                problem =
                    $"RGB scene name '{name}' is ambiguous with a direct RRGGBB target. Choose a non-hex scene name.";
                return false;
            }

            if (sceneProperty.Value.ValueKind != JsonValueKind.Object)
            {
                problem =
                    $"'{SettingsPath}.scenes.{name}' must map device numbers to mode/color objects with optional per-LED colours.";
                return false;
            }

            var devices = new SortedDictionary<int, CaseRgbSceneDevice>();

            foreach (JsonProperty deviceProperty in sceneProperty.Value.EnumerateObject())
            {
                if (!TryReadIndex(deviceProperty.Name, out int index))
                {
                    problem =
                        $"'{SettingsPath}.scenes.{name}' has a key '{deviceProperty.Name}' that is not a device number.";
                    return false;
                }

                if (deviceProperty.Value.ValueKind != JsonValueKind.Object)
                {
                    problem =
                        $"'{SettingsPath}.scenes.{name}.{index}' must be an object with 'mode', 'color' and optional 'leds'.";
                    return false;
                }

                string? mode = null;
                string? color = null;
                List<string>? ledColors = null;

                foreach (JsonProperty field in deviceProperty.Value.EnumerateObject())
                {
                    if (field.NameEquals("mode"))
                    {
                        if (field.Value.ValueKind != JsonValueKind.String)
                        {
                            problem = $"'{SettingsPath}.scenes.{name}.{index}.mode' must be a string.";
                            return false;
                        }

                        mode = field.Value.GetString()?.Trim();
                    }
                    else if (field.NameEquals("color"))
                    {
                        if (field.Value.ValueKind != JsonValueKind.String)
                        {
                            problem = $"'{SettingsPath}.scenes.{name}.{index}.color' must be a six-digit RGB string.";
                            return false;
                        }

                        color = field.Value.GetString()?.Trim().ToUpperInvariant();
                    }
                    else if (field.NameEquals("leds"))
                    {
                        if (field.Value.ValueKind != JsonValueKind.Array)
                        {
                            problem =
                                $"'{SettingsPath}.scenes.{name}.{index}.leds' must be an array of six-digit RGB strings.";
                            return false;
                        }

                        ledColors = [];

                        foreach (JsonElement led in field.Value.EnumerateArray())
                        {
                            if (
                                led.ValueKind != JsonValueKind.String
                                || led.GetString() is not string ledColor
                                || !IsRgb(ledColor.Trim())
                            )
                            {
                                problem =
                                    $"'{SettingsPath}.scenes.{name}.{index}.leds' must contain only six-digit RGB strings.";
                                return false;
                            }

                            ledColors.Add(ledColor.Trim().ToUpperInvariant());
                        }

                        if (ledColors.Count == 0)
                        {
                            problem = $"'{SettingsPath}.scenes.{name}.{index}.leds' cannot be empty.";
                            return false;
                        }
                    }
                    else
                    {
                        problem =
                            $"'{SettingsPath}.scenes.{name}.{index}' does not understand '{field.Name}'. "
                            + "It takes 'mode', 'color' and optional 'leds'.";
                        return false;
                    }
                }

                if (string.IsNullOrWhiteSpace(mode))
                {
                    problem = $"'{SettingsPath}.scenes.{name}.{index}' has no lighting mode.";
                    return false;
                }

                if (color is null || !IsRgb(color))
                {
                    problem = $"'{SettingsPath}.scenes.{name}.{index}.color' must be six hexadecimal digits (RRGGBB).";
                    return false;
                }

                var sceneDevice = new CaseRgbSceneDevice(index, mode, color) { LedColors = ledColors };

                if (!devices.TryAdd(index, sceneDevice))
                {
                    problem = $"RGB scene '{name}' gives device {index} more than one state.";
                    return false;
                }
            }

            if (devices.Count == 0)
            {
                problem = $"RGB scene '{name}' has no devices.";
                return false;
            }

            if (!parsed.TryAdd(name, new CaseRgbScene(name, [.. devices.Values])))
            {
                problem = $"'{SettingsPath}.scenes' gives scene '{name}' more than once.";
                return false;
            }
        }

        scenes = parsed;
        problem = null;
        return true;
    }

    private static bool TryReadIndex(string text, out int index)
    {
        if (
            !int.TryParse(
                text,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out index
            )
            || index < 0
        )
        {
            index = -1;
            return false;
        }

        return true;
    }

    private static bool IsRgb(string value) => value.Length == 6 && value.All(char.IsAsciiHexDigit);
}

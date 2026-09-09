namespace PCMode.Engine;

/// <summary>
/// Finding the PCMode virtual render endpoint among the machine's own devices.
/// </summary>
/// <remarks>
/// WHY IT HAS TO BE FOUND AT ALL
///
/// An Audio Target plays through the audio engine, and the engine's source is
/// the PCMode virtual endpoint: applications render into it, the driver hands
/// the PCM to the Host, and the Host fans it out to the target's routes. So
/// starting a target means making that endpoint the machine's playback device,
/// and that needs its Core Audio endpoint ID.
///
/// Windows composes the friendly name as "&lt;something&gt; (&lt;device
/// description&gt;)" -- observed as "Speakers (PCMode Virtual Audio Endpoint)"
/// on real hardware, where the leading part is whatever jack/port association
/// Windows assigned the render pin and is not something the driver controls.
/// The parenthetical is the INF's device description, and it is the one part
/// that is actually stable. Matching the whole string exactly would break the
/// moment Windows chose a different leading word; matching loosely on
/// unrelated text would be exactly the endpoint substitution this provider
/// refuses everywhere else.
///
/// So: the marker below has to appear in the name, and the match has to be
/// unique. One match is the endpoint. None is "it is not installed" and says so.
/// More than one is refused rather than guessed, with the way to settle it --
/// name the exact device in the configuration.
/// </remarks>
public static class AudioVirtualEndpoint
{
    /// <summary>Where an exact name can be written when the marker is not enough.</summary>
    public const string SettingsPath = "providers.audio.virtualEndpoint";

    /// <summary>
    /// What the PCMode virtual endpoint's Windows name always contains.
    /// </summary>
    /// <remarks>
    /// The driver's device description, from the INF's <c>PCModeAudio.DeviceDesc</c>
    /// string -- the parenthetical half of the composed friendly name, not the
    /// leading word, which is a jack/port label Windows assigns on its own and
    /// is not under the driver's control. Observed as "Speakers (PCMode Virtual
    /// Audio Endpoint)" on real hardware.
    /// </remarks>
    public const string NameMarker = "PCMode Virtual Audio Endpoint";

    /// <summary>
    /// The endpoint ID of the PCMode virtual endpoint, or an explanation of why
    /// there is not exactly one.
    /// </summary>
    /// <param name="active">Every playback endpoint Windows currently has active.</param>
    /// <param name="configuredName">
    /// The exact Windows name to use instead of the marker, when the document
    /// names one.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// There is no PCMode virtual endpoint, or there is more than one thing that
    /// could be it.
    /// </exception>
    public static string Resolve(IReadOnlyList<AudioEndpoint> active, string? configuredName = null)
    {
        ArgumentNullException.ThrowIfNull(active);

        if (!string.IsNullOrWhiteSpace(configuredName))
        {
            string wanted = configuredName.Trim();

            List<AudioEndpoint> named =
            [
                .. active.Where(endpoint => string.Equals(endpoint.Name, wanted, StringComparison.OrdinalIgnoreCase)),
            ];

            return named.Count switch
            {
                1 => named[0].Id,
                0 => throw new InvalidOperationException(
                    $"No active playback device is named '{wanted}', which '{SettingsPath}' says is the "
                        + "PCMode virtual audio endpoint. "
                        + Present(active)
                ),
                _ => throw new InvalidOperationException(
                    $"{named.Count} active playback devices are named '{wanted}', so there is no way to "
                        + "tell which one is the PCMode virtual audio endpoint."
                ),
            };
        }

        List<AudioEndpoint> matches =
        [
            .. active.Where(endpoint =>
                endpoint.Name is not null && endpoint.Name.Contains(NameMarker, StringComparison.OrdinalIgnoreCase)
            ),
        ];

        return matches.Count switch
        {
            1 => matches[0].Id,
            0 => throw new InvalidOperationException(
                "The PCMode virtual audio endpoint is not present, so audio targets cannot play. "
                    + "Install the PCMode audio subsystem, or use a single output instead. "
                    + Present(active)
            ),
            _ => throw new InvalidOperationException(
                $"{matches.Count} active playback devices could be the PCMode virtual audio endpoint "
                    + $"({string.Join(", ", matches.Select(endpoint => $"'{endpoint.Name}'"))}). "
                    + $"Write the exact one under '{SettingsPath}'."
            ),
        };
    }

    /// <summary>
    /// Whether that endpoint name is the PCMode virtual endpoint's.
    /// </summary>
    /// <remarks>
    /// For presentation only -- a settings list saying which device is PCMode's
    /// own. Nothing decides where sound goes on this answer; that is
    /// <see cref="Resolve"/>, which refuses to guess.
    /// </remarks>
    public static bool IsVirtualEndpointName(string? name, string? configuredName = null) =>
        !string.IsNullOrWhiteSpace(name)
        && (
            string.IsNullOrWhiteSpace(configuredName)
                ? name.Contains(NameMarker, StringComparison.OrdinalIgnoreCase)
                : string.Equals(name.Trim(), configuredName.Trim(), StringComparison.OrdinalIgnoreCase)
        );

    private static string Present(IReadOnlyList<AudioEndpoint> active) =>
        active.Count == 0
            ? "Windows reports no active playback devices at all."
            : $"Active right now: {string.Join(", ", active.Select(endpoint => $"'{endpoint.Name}'"))}.";
}

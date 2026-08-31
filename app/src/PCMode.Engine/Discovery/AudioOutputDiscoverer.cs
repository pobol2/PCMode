namespace PCMode.Engine;

/// <summary>
/// Every playback endpoint Windows currently has active, in terms someone
/// inspecting their setup would read.
/// </summary>
/// <remarks>
/// Reuses <see cref="AudioProvider.Discover"/> rather than <see cref="IAudioEndpoints"/>
/// directly, for the same reason <see cref="DisplayDiscoverer"/> reuses
/// <see cref="DisplayProvider"/>: which configured alias, if any, an endpoint
/// resolves to is already computed there, including the rule that two active
/// endpoints sharing a name make that alias unresolvable rather than a guess.
///
/// Only active endpoints exist to enumerate at all: unlike a display target, a
/// disabled or unplugged output leaves no trace to report as "not detected". A
/// machine with nothing plugged in simply has an empty list, which is not a
/// fault either.
/// </remarks>
public sealed class AudioOutputDiscoverer(AudioProvider provider) : IHardwareDiscoverer
{
    /// <summary>The category name, shared with the alias-writing side.</summary>
    public const string CategoryName = "Audio outputs";

    private readonly AudioProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public string Category => CategoryName;

    public IReadOnlyList<HardwareItem> Discover()
    {
        AudioSnapshot snapshot = _provider.Discover();

        if (!snapshot.Available)
        {
            throw new InvalidOperationException(snapshot.Problem ?? "The audio outputs could not be read.");
        }

        return DescribeOutputs(snapshot);
    }

    /// <summary>
    /// Projects an already-read provider snapshot into endpoint discovery rows, allowing a feature
    /// page that also needs mapping or target runtime state to enumerate Core Audio only once.
    /// </summary>
    public static IReadOnlyList<HardwareItem> DescribeOutputs(AudioSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Only a mapping that unambiguously resolves counts as this endpoint's
        // alias -- the same rule AudioProvider.Read applies before it will call
        // a device "selected". Two endpoints sharing a name leave the alias
        // unresolved for both rather than guessing which one was meant.
        var aliasByEndpoint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (AudioDeviceMapping mapping in snapshot.Devices)
        {
            if (mapping.State == AudioMappingState.Ready)
            {
                aliasByEndpoint[mapping.EndpointName] = mapping.LogicalName;
            }
        }

        // How many active endpoints currently share each name. AudioProvider
        // matches by name alone, so a name two endpoints share right now cannot
        // be aliased safely: the entry would be written, and would immediately
        // read back as ambiguous, matching either one.
        Dictionary<string, int> nameCounts = snapshot.Endpoints
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.Name))
            .GroupBy(endpoint => endpoint.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var items = new List<HardwareItem>(snapshot.Endpoints.Count);

        foreach (AudioEndpointInfo endpoint in snapshot.Endpoints)
        {
            string name = string.IsNullOrWhiteSpace(endpoint.Name) ? "Unnamed audio device" : endpoint.Name;

            // An endpoint with no name, or one sharing its name with another
            // active endpoint right now, cannot be aliased.
            string? identifier = string.IsNullOrWhiteSpace(endpoint.Name) || nameCounts.GetValueOrDefault(endpoint.Name) > 1
                ? null
                : endpoint.Name;

            string? alias = identifier is not null && aliasByEndpoint.TryGetValue(identifier, out string? found)
                ? found
                : null;

            List<HardwareDetail> details = [new("Default", endpoint.IsDefault ? "Yes" : "No")];

            if (endpoint.IsVirtualEndpoint)
            {
                details.Add(new("PCMode virtual endpoint", "Yes"));
            }

            items.Add(new HardwareItem(
                name,
                HardwareItemStatus.Ok,
                endpoint.IsVirtualEndpoint
                    ? "PCMode's own audio endpoint. Audio targets play into this one."
                    : endpoint.IsDefault ? "Default Windows output." : "Available.",
                details,

                // The virtual endpoint is offered no identifier, so nothing
                // proposes aliasing it. It is not a device a mode should be able
                // to select: it is where audio targets send sound in, and a mode
                // naming it directly would put Windows in front of an engine
                // holding no target at all.
                endpoint.IsVirtualEndpoint ? null : identifier,
                endpoint.IsVirtualEndpoint ? null : alias,
                CategoryName));
        }

        return HardwareNaming.Disambiguate(items);
    }
}

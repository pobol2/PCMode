using System.Globalization;

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

            items.Add(new HardwareItem(
                name,
                HardwareItemStatus.Ok,
                endpoint.IsDefault ? "Default playback device." : "Available.",
                [new("Default", endpoint.IsDefault ? "Yes" : "No")],
                identifier,
                alias,
                CategoryName));
        }

        List<HardwareItem> reported = [.. HardwareNaming.Disambiguate(items)];

        if (Describe(snapshot.Party) is HardwareItem party)
        {
            reported.Add(party);
        }

        return reported;
    }

    /// <summary>
    /// The Party group as a line in the hardware report.
    /// </summary>
    /// <remarks>
    /// Reported beside the endpoints rather than somewhere of its own, because
    /// what a person wants to know here is one thing: what is this machine
    /// playing through. A group that has failed is the answer to that question
    /// and the endpoint list on its own cannot give it -- both members are still
    /// present and active, and the mirror between them stopped an hour ago.
    ///
    /// No identifier, so nothing offers to alias it. It is not a device: it is
    /// two of them and a configuration.
    /// </remarks>
    private static HardwareItem? Describe(AudioPartyStatus? party)
    {
        if (party is null)
        {
            return null;
        }

        List<HardwareDetail> details =
        [
            new("Members", string.Join(" -> ", party.Members)),
            new("Delay", $"{party.DelayMs} ms"),
            new("Gain", party.Gain.ToString("0.00", CultureInfo.InvariantCulture)),
        ];

        if (party.Health is AudioMirrorHealth health)
        {
            details.Add(new("Reference format", health.ReferenceFormat));
            details.Add(new("Mirrored format", health.MirrorFormat));
            details.Add(new("Capture", health.EventDriven ? "Event-driven" : "Polled"));
            details.Add(new("Clock offset", string.Create(
                CultureInfo.InvariantCulture, $"{health.ClockOffsetMs:0.00} ms")));
            details.Add(new("Drift corrections", health.DriftCorrections.ToString(CultureInfo.InvariantCulture)));
            details.Add(new("Re-alignments", health.Resyncs.ToString(CultureInfo.InvariantCulture)));
            details.Add(new("Buffer underruns", health.Underruns.ToString(CultureInfo.InvariantCulture)));
        }

        if (party.Problem is not null)
        {
            details.Add(new("Last failure", party.Problem));
        }

        return new HardwareItem(
            $"{party.Name} (two-output group)",
            party.State switch
            {
                PartyState.Failed => HardwareItemStatus.Error,
                PartyState.Running => HardwareItemStatus.Ok,
                _ => HardwareItemStatus.NotDetected,
            },
            party.State switch
            {
                PartyState.Running => $"Mirroring '{party.Members[0]}' to '{party.Members[1]}'.",
                PartyState.Failed => party.Problem ?? "The mirror stopped on its own.",
                _ => "Configured, and not running.",
            },
            details,
            Category: CategoryName);
    }
}

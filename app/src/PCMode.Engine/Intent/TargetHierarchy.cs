namespace PCMode.Engine;

/// <summary>What a Source is, when it is not a program on this PC.</summary>
/// <remarks>
/// A kind, and nothing more. It says what somebody is looking at when they pick
/// this Source, so the rest of the application can describe it honestly without
/// inventing a Windows process for a games console. What a Source actually
/// changes is whatever resources it sets, exactly like every other level of the
/// hierarchy: a console is an HDMI input and a picture profile, not a fake
/// application definition with a made-up executable.
///
/// Handing over to an external device -- waking it, telling it to switch on,
/// giving it the room -- is deliberately not here. That is the later external
/// Sources step; this is only the vocabulary needed to say that such a Source
/// exists.
/// </remarks>
public enum SourceKind
{
    /// <summary>A program on this PC, usually a configured application resource.</summary>
    PcApplication,

    /// <summary>Something plugged into the television, such as a console.</summary>
    ExternalDevice,

    /// <summary>An application that runs on the television itself.</summary>
    TvApplication,
}

/// <summary>
/// One resource, as one level of the hierarchy declares it.
/// </summary>
/// <param name="Value">
/// What this level wants the resource to be, or null to clear it: an explicit
/// statement that this level does not want whatever it would otherwise have
/// inherited. Null is not the same as saying nothing -- saying nothing inherits.
/// </param>
/// <param name="Optional">
/// Whether the target still means something without this. Carried down to the
/// composed resource unchanged; see <see cref="ConfiguredResource"/>.
/// </param>
public sealed record ResourceAssignment(string Key, string? Value, bool Optional = false)
{
    /// <summary>Whether this removes an inherited resource rather than setting one.</summary>
    public bool Clears => Value is null;
}

/// <summary>
/// One Source: what provides the experience inside an Activity.
/// </summary>
public sealed record ConfiguredSource(
    string Id,
    string Name,
    SourceKind Kind,
    IReadOnlyList<ResourceAssignment> Resources)
{
    /// <summary>Whether this source target may be switched to.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// One Activity: what the user is doing inside a Space.
/// </summary>
/// <remarks>
/// An Activity is activatable in its own right, whether or not it has Sources.
/// "Work" with nothing plugged into it is a complete answer, and an Activity
/// that later gains Sources keeps working exactly as it did -- the Sources are
/// additional targets beside it, never a replacement for it. Any other rule
/// would mean adding a Source silently invalidated every reference to the
/// Activity.
/// </remarks>
public sealed record ConfiguredActivity(
    string Id,
    string Name,
    IReadOnlyList<ResourceAssignment> Resources,
    IReadOnlyList<ConfiguredSource> Sources)
{
    /// <summary>Whether this activity target may be switched to.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>One Space: where the setup is being used.</summary>
public sealed record ConfiguredSpace(
    string Id,
    string Name,
    IReadOnlyList<ResourceAssignment> Resources,
    IReadOnlyList<ConfiguredActivity> Activities);

/// <summary>
/// Which Space, Activity and Source a target is, by their stable logical IDs.
/// </summary>
/// <remarks>
/// IDs rather than names, because names are the user's to change and a
/// reference that broke when somebody corrected a spelling would be a
/// configuration that quietly stopped working. Everything that points at a
/// target -- a picker favorite, the startup target, the recorded current target
/// -- points at <see cref="Key"/>.
/// </remarks>
public sealed record TargetIdentity(string SpaceId, string ActivityId, string? SourceId)
{
    /// <summary>The separator between the levels of a key. Never allowed inside an ID.</summary>
    public const char Separator = '/';

    /// <summary>The stable reference for this target.</summary>
    public string Key => SourceId is null
        ? $"{SpaceId}{Separator}{ActivityId}"
        : $"{SpaceId}{Separator}{ActivityId}{Separator}{SourceId}";

    public override string ToString() => Key;
}

/// <summary>
/// One thing a user can activate, in the terms they chose it by.
/// </summary>
/// <remarks>
/// The one description everything outside the engine uses to name a target:
/// <see cref="Key"/> to ask for it, <see cref="DisplayPath"/> to show which
/// Space, Activity and Source it is, and <see cref="DisplayName"/> when there is
/// only room for the part the user actually picked.
/// </remarks>
public sealed record ConfiguredTarget
{
    /// <summary>What this target is asked for by.</summary>
    public required string Key { get; init; }

    /// <summary>The Source's name, or the Activity's when there is no Source.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Space, Activity and Source, in that order.</summary>
    public required string DisplayPath { get; init; }

    /// <summary>Which Space, Activity and Source this is, by their stable logical IDs.</summary>
    public required TargetIdentity Identity { get; init; }

    public string? SpaceName { get; init; }

    public string? ActivityName { get; init; }

    public string? SourceName { get; init; }

    public SourceKind? SourceKind { get; init; }

    /// <summary>
    /// Whether PCMode may switch to this target. Disabled targets stay in the
    /// catalog so their configuration and stable references are preserved.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>A target for one Activity, and optionally one of its Sources.</summary>
    public static ConfiguredTarget For(
        ConfiguredSpace space,
        ConfiguredActivity activity,
        ConfiguredSource? source)
    {
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(activity);

        return new ConfiguredTarget
        {
            Key = new TargetIdentity(space.Id, activity.Id, source?.Id).Key,
            DisplayName = source?.Name ?? activity.Name,
            DisplayPath = source is null
                ? $"{space.Name} — {activity.Name}"
                : $"{space.Name} — {activity.Name} — {source.Name}",
            Identity = new TargetIdentity(space.Id, activity.Id, source?.Id),
            SpaceName = space.Name,
            ActivityName = activity.Name,
            SourceName = source?.Name,
            SourceKind = source?.Kind,
            Enabled = source?.Enabled ?? activity.Enabled,
        };
    }

    public override string ToString() => DisplayPath;
}

/// <summary>
/// Composes the levels of the hierarchy into the resources one transition
/// applies.
/// </summary>
/// <remarks>
/// THE WHOLE OF WHAT INHERITANCE MEANS, IN ONE PLACE
///
/// Space first, then Activity, then Source, each seeing everything the levels
/// above it decided:
///
/// - a level that says nothing about a resource inherits it;
/// - a level that gives a value replaces the inherited one;
/// - a level that clears it removes it, so the transition does not set it at
///   all -- which is a different statement from setting it to Off, and both are
///   available because both are things people mean.
///
/// "Off" needs no special handling and deliberately gets none: it is an ordinary
/// value that happens to mean off to whichever provider owns it, and it
/// overrides an inherited value exactly like any other.
///
/// ORDER IS DETERMINISTIC
///
/// A resource keeps the position it was first declared at, however many levels
/// later override it, and a resource that a lower level introduces is appended.
/// So the composed list is a function of the document alone -- the same
/// hierarchy composes identically every time. It is only a tie-breaker either
/// way: <see cref="TransitionPlan"/> still decides the real order from
/// dependencies.
/// </remarks>
public static class TargetComposition
{
    /// <summary>
    /// The resources of a target, from its Space, its Activity and -- when it
    /// has one -- its Source.
    /// </summary>
    public static IReadOnlyList<ConfiguredResource> Compose(
        ConfiguredSpace space,
        ConfiguredActivity activity,
        ConfiguredSource? source)
    {
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(activity);

        return source is null
            ? Compose(space.Resources, activity.Resources)
            : Compose(space.Resources, activity.Resources, source.Resources);
    }

    /// <summary>Layers assignments, outermost level first.</summary>
    public static IReadOnlyList<ConfiguredResource> Compose(params IReadOnlyList<ResourceAssignment>[] levels)
    {
        ArgumentNullException.ThrowIfNull(levels);

        var order = new List<string>();
        var resolved = new Dictionary<string, ConfiguredResource>(StringComparer.OrdinalIgnoreCase);

        foreach (IReadOnlyList<ResourceAssignment> level in levels)
        {
            foreach (ResourceAssignment assignment in level)
            {
                if (assignment.Clears)
                {
                    if (resolved.Remove(assignment.Key))
                    {
                        order.RemoveAll(key => string.Equals(key, assignment.Key, StringComparison.OrdinalIgnoreCase));
                    }

                    continue;
                }

                if (!resolved.ContainsKey(assignment.Key))
                {
                    order.Add(assignment.Key);
                }

                resolved[assignment.Key] = new ConfiguredResource(
                    assignment.Key, assignment.Value!, assignment.Optional);
            }
        }

        return [.. order.Select(key => resolved[key])];
    }
}

/// <summary>
/// Every target a configuration offers, and the one way a reference to one is
/// resolved.
/// </summary>
/// <remarks>
/// ONE RESOLUTION RULE, SHARED
///
/// A picker favorite, the startup target, the recorded current target, a tray
/// click and a command line all name a target as a string, and all of them are
/// resolved here. Resolution identifies configured targets, including disabled
/// ones whose references must remain intact; the engine separately enforces
/// whether a resolved target may be switched to.
///
/// A key is the canonical form and is tried first. A full display path and an
/// unambiguous leaf name are also accepted, which is what lets a hand-written
/// reference say "Gaming" rather than "desk/gaming" -- and why an ambiguous one
/// is refused by name rather than resolved to whichever happened to be first.
/// </remarks>
public sealed class TargetCatalog
{
    private readonly Dictionary<string, ConfiguredTarget> _byKey;

    public TargetCatalog(IReadOnlyList<ConfiguredTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        Targets = targets;
        _byKey = new Dictionary<string, ConfiguredTarget>(StringComparer.OrdinalIgnoreCase);

        foreach (ConfiguredTarget target in targets)
        {
            // Duplicates cannot reach here: identities are checked as the
            // document is read. Last-one-wins is simply not a policy this needs
            // to have.
            _byKey[target.Key] = target;
        }
    }

    /// <summary>In configuration order: Spaces, then Activities, then Sources.</summary>
    public IReadOnlyList<ConfiguredTarget> Targets { get; }

    /// <summary>Every target's key, in configuration order.</summary>
    public IReadOnlyList<string> Keys => [.. Targets.Select(target => target.Key)];

    /// <summary>The target a reference names, or null when it names none or more than one.</summary>
    public ConfiguredTarget? Resolve(string? reference) =>
        TryResolve(reference, out ConfiguredTarget? target, out _) ? target : null;

    /// <summary>
    /// The target a reference names, or why it does not name exactly one.
    /// </summary>
    public bool TryResolve(string? reference, out ConfiguredTarget? target, out string? problem)
    {
        target = null;
        problem = null;

        if (string.IsNullOrWhiteSpace(reference))
        {
            problem = "No target was named.";
            return false;
        }

        string trimmed = reference.Trim();

        if (_byKey.TryGetValue(trimmed, out ConfiguredTarget? byKey))
        {
            target = byKey;
            return true;
        }

        ConfiguredTarget? byPath = Targets.FirstOrDefault(candidate =>
            string.Equals(candidate.DisplayPath, trimmed, StringComparison.OrdinalIgnoreCase));

        if (byPath is not null)
        {
            target = byPath;
            return true;
        }

        ConfiguredTarget[] byName =
        [
            .. Targets.Where(candidate =>
                string.Equals(candidate.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase))
        ];

        if (byName.Length == 1)
        {
            target = byName[0];
            return true;
        }

        problem = byName.Length > 1
            ? $"'{trimmed}' names {byName.Length} targets ({string.Join(", ", byName.Select(match => match.Key))}). " +
              "Use the full one."
            : $"'{trimmed}' is not a configured target. Known: {string.Join(", ", Keys)}.";

        return false;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCMode.Engine;

/// <summary>Whether a change was saved, and why not when it was refused.</summary>
public sealed record ModeSaveOutcome(bool Success, string? Problem)
{
    public static readonly ModeSaveOutcome Ok = new(true, null);

    public static ModeSaveOutcome Failed(string problem) => new(false, problem);
}

/// <summary>Which level of the hierarchy something is.</summary>
public enum NodeLevel
{
    Space,
    Activity,
    Source,
}

/// <summary>
/// Where in the hierarchy an edit applies.
/// </summary>
/// <remarks>
/// By id at every level, for the same reason a target is referenced by key:
/// the names are the user's and change, and an edit addressed by name would
/// apply to the wrong thing the moment two levels agreed about a word.
/// </remarks>
public sealed record NodeAddress(string SpaceId, string? ActivityId = null, string? SourceId = null)
{
    public NodeLevel Level =>
        SourceId is not null ? NodeLevel.Source
        : ActivityId is not null ? NodeLevel.Activity
        : NodeLevel.Space;

    /// <summary>The target key this addresses, or null for a Space.</summary>
    public string? TargetKey => ActivityId is null ? null : new TargetIdentity(SpaceId, ActivityId, SourceId).Key;
}

/// <summary>What one level says about one resource.</summary>
public enum ResourceIntent
{
    /// <summary>Says nothing, so whatever is above it applies.</summary>
    Inherit,

    /// <summary>Sets a value of its own.</summary>
    Set,

    /// <summary>
    /// Explicitly does not want the inherited value, so the target does not set
    /// the resource at all. Different from setting it to Off, which sets it.
    /// </summary>
    Clear,
}

/// <summary>One resource, as an edit to one level.</summary>
public sealed record ResourceEdit(ResourceIntent Intent, string? Value = null, bool Optional = false)
{
    public static readonly ResourceEdit Inherited = new(ResourceIntent.Inherit);

    public static readonly ResourceEdit Cleared = new(ResourceIntent.Clear);

    public static ResourceEdit Of(string value, bool optional = false) => new(ResourceIntent.Set, value, optional);
}

/// <summary>
/// Creates, edits, orders and deletes Spaces, Activities and Sources in the same
/// configuration file <see cref="ModeConfiguration"/> reads and every other
/// store writes to.
/// </summary>
/// <remarks>
/// NO SECOND SOURCE OF TRUTH
///
/// Every operation reads the real file, builds a complete candidate document in
/// memory, validates it exactly the way a real startup would -- through
/// <see cref="EngineBootstrap.ProductionProviders"/> and
/// <see cref="ModeConfiguration.Parse"/> -- and only then writes it atomically.
/// A candidate that fails validation leaves the file untouched. This is the one
/// place the hierarchy is edited: identities, relationships and composition
/// conflicts are therefore rejected before anything can be activated, because
/// they are rejected before anything is saved.
///
/// APPLICATION REFERENCES ARE PART OF THE SAME DOCUMENT
///
/// Picker favorites, direct shortcuts and startup settings name targets, even
/// though this class does not know their GUI behavior (that belongs to the
/// shell). A delete that left one of those pointing at a target that no longer exists would be a
/// document nothing downstream could apply, so every candidate here is also
/// checked for that -- the one piece of validation
/// <see cref="ModeConfiguration.Parse"/> itself does not do, because it does not
/// read the 'application' section.
///
/// A RENAME IS NOT A REFERENCE CHANGE
///
/// Renaming anything changes a name and nothing else. That is the whole return
/// on identities having ids: what favorites, startup and the recorded current
/// target point at is unaffected, so there is nothing to keep in step and
/// nothing to get wrong.
/// </remarks>
public static class HierarchyStore
{
    /// <summary>Adds a Space with no activities yet.</summary>
    public static ModeSaveOutcome CreateSpace(string? root, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string trimmed = name.Trim();

        return Edit(
            root,
            (_, spaces) =>
            {
                if (FindByName(spaces, trimmed) is not null)
                {
                    return ModeSaveOutcome.Failed($"'{trimmed}' is already a space.");
                }

                spaces.Add(
                    new JsonObject
                    {
                        ["id"] = LogicalId.From(trimmed, IdsOf(spaces)),
                        ["name"] = trimmed,
                        ["resources"] = new JsonObject(),
                        ["activities"] = new JsonArray(),
                    }
                );

                return null;
            }
        );
    }

    /// <summary>Adds an Activity to a Space, optionally copying another one whole.</summary>
    public static ModeSaveOutcome CreateActivity(string? root, string spaceId, string name, string? duplicateOf = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string trimmed = name.Trim();

        return Edit(
            root,
            (_, spaces) =>
            {
                if (FindById(spaces, spaceId) is not JsonObject space)
                {
                    return ModeSaveOutcome.Failed($"'{spaceId}' is not a configured space.");
                }

                JsonArray activities = ArrayIn(space, "activities");

                if (FindByName(activities, trimmed) is not null)
                {
                    return ModeSaveOutcome.Failed($"'{trimmed}' is already an activity of this space.");
                }

                if (duplicateOf is not null)
                {
                    if (FindById(activities, duplicateOf) is not JsonObject original)
                    {
                        return ModeSaveOutcome.Failed($"'{duplicateOf}' is not an activity of this space.");
                    }

                    AddDuplicate(activities, original, trimmed, NodeLevel.Activity);
                    return null;
                }

                activities.Add(
                    new JsonObject
                    {
                        ["id"] = LogicalId.From(trimmed, IdsOf(activities)),
                        ["name"] = trimmed,
                        ["resources"] = new JsonObject(),
                        ["sources"] = new JsonArray(),
                    }
                );

                return null;
            }
        );
    }

    /// <summary>Adds a Source to an Activity.</summary>
    public static ModeSaveOutcome CreateSource(
        string? root,
        string spaceId,
        string activityId,
        string name,
        SourceKind kind
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string trimmed = name.Trim();

        return Edit(
            root,
            (_, spaces) =>
            {
                if (
                    Locate(spaces, new NodeAddress(spaceId, activityId), out JsonObject? activity, out string? missing)
                    is false
                )
                {
                    return ModeSaveOutcome.Failed(missing!);
                }

                JsonArray sources = ArrayIn(activity!, "sources");

                if (FindByName(sources, trimmed) is not null)
                {
                    return ModeSaveOutcome.Failed($"'{trimmed}' is already a source of this activity.");
                }

                sources.Add(
                    new JsonObject
                    {
                        ["id"] = LogicalId.From(trimmed, IdsOf(sources)),
                        ["name"] = trimmed,
                        ["kind"] = Write(kind),
                        ["resources"] = new JsonObject(),
                    }
                );

                return null;
            }
        );
    }

    /// <summary>
    /// Copies one Space, Activity or Source beside the original, including its
    /// own resources and everything below it, under identities of its own.
    /// References to the original are deliberately left alone.
    /// </summary>
    public static ModeSaveOutcome Duplicate(string? root, NodeAddress address, string name)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string trimmed = name.Trim();

        return Edit(
            root,
            (_, spaces) =>
            {
                if (Locate(spaces, address, out JsonObject? original, out string? missing) is false)
                {
                    return ModeSaveOutcome.Failed(missing!);
                }

                JsonArray siblings = SiblingsOf(spaces, address)!;

                if (FindByName(siblings, trimmed) is not null)
                {
                    return ModeSaveOutcome.Failed(
                        $"'{trimmed}' is already the name of another {Describe(address.Level)}."
                    );
                }

                AddDuplicate(siblings, original!, trimmed, address.Level);
                return null;
            }
        );
    }

    /// <summary>Renames one node. References are unaffected: they point at ids.</summary>
    public static ModeSaveOutcome Rename(string? root, NodeAddress address, string newName)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        string trimmed = newName.Trim();

        return Edit(
            root,
            (_, spaces) =>
            {
                if (Locate(spaces, address, out JsonObject? node, out string? missing) is false)
                {
                    return ModeSaveOutcome.Failed(missing!);
                }

                JsonArray siblings = SiblingsOf(spaces, address)!;

                if (FindByName(siblings, trimmed) is JsonObject other && !ReferenceEquals(other, node))
                {
                    return ModeSaveOutcome.Failed(
                        $"'{trimmed}' is already the name of another {Describe(address.Level)}."
                    );
                }

                node!["name"] = trimmed;
                return null;
            }
        );
    }

    /// <summary>Changes what kind of thing a Source is.</summary>
    public static ModeSaveOutcome SetSourceKind(string? root, NodeAddress address, SourceKind kind)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.Level != NodeLevel.Source)
        {
            return ModeSaveOutcome.Failed("Only a source has a kind.");
        }

        return Edit(
            root,
            (_, spaces) =>
            {
                if (Locate(spaces, address, out JsonObject? node, out string? missing) is false)
                {
                    return ModeSaveOutcome.Failed(missing!);
                }

                node!["kind"] = Write(kind);
                return null;
            }
        );
    }

    /// <summary>
    /// Enables or disables one activatable Activity or Source without removing
    /// it, its resources, or anything that refers to its stable key.
    /// </summary>
    public static ModeSaveOutcome SetEnabled(string? root, NodeAddress address, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.Level == NodeLevel.Space)
        {
            return ModeSaveOutcome.Failed("A space is not a target and cannot be enabled or disabled.");
        }

        return Edit(
            root,
            (_, spaces) =>
            {
                if (Locate(spaces, address, out JsonObject? node, out string? missing) is false)
                {
                    return ModeSaveOutcome.Failed(missing!);
                }

                HardwareConfigurationStore.RemoveCaseInsensitive(node!, "enabled");

                // True is the document default, so re-enabling restores the compact
                // shape existing configurations already use.
                if (!enabled)
                {
                    node!["enabled"] = false;
                }

                return null;
            }
        );
    }

    /// <summary>
    /// Removes a node and everything under it.
    /// <paramref name="favoriteReplacements"/> supplies the target to put in
    /// place of every picker direction that currently favors something being
    /// removed, keyed by direction name ("Up", "Right", "Down", "Left"); a
    /// direction with no entry fails the whole operation rather than guessing.
    /// <paramref name="startupReplacement"/> is the target to switch startup to
    /// if it named one being removed; null turns startup behaviour off instead
    /// of guessing.
    /// </summary>
    public static ModeSaveOutcome Delete(
        string? root,
        NodeAddress address,
        IReadOnlyDictionary<string, string>? favoriteReplacements = null,
        string? startupReplacement = null
    )
    {
        ArgumentNullException.ThrowIfNull(address);

        IReadOnlyList<string> removedKeys = [];

        ModeSaveOutcome outcome = Edit(
            root,
            (document, spaces) =>
            {
                if (Locate(spaces, address, out JsonObject? node, out string? missing) is false)
                {
                    return ModeSaveOutcome.Failed(missing!);
                }

                removedKeys = KeysUnder(spaces, address);

                string? replacementProblem = ReplaceApplicationReferences(
                    document,
                    removedKeys,
                    favoriteReplacements,
                    startupReplacement
                );

                if (replacementProblem is not null)
                {
                    return ModeSaveOutcome.Failed(replacementProblem);
                }

                SiblingsOf(spaces, address)!.Remove(node);
                return null;
            }
        );

        if (outcome.Success)
        {
            ClearRecordedTargetIfRemoved(root, removedKeys);
        }

        return outcome;
    }

    /// <summary>
    /// Moves a node one place earlier or later among its siblings. Order here is
    /// what every list of targets shows; picker directions are unaffected, since
    /// they are references rather than positions.
    /// </summary>
    public static ModeSaveOutcome Move(string? root, NodeAddress address, bool up)
    {
        ArgumentNullException.ThrowIfNull(address);

        return Edit(
            root,
            (_, spaces) =>
            {
                if (Locate(spaces, address, out JsonObject? node, out string? missing) is false)
                {
                    return ModeSaveOutcome.Failed(missing!);
                }

                JsonArray siblings = SiblingsOf(spaces, address)!;
                int index = siblings.IndexOf(node);
                int destination = up ? index - 1 : index + 1;

                if (destination < 0 || destination >= siblings.Count)
                {
                    return ModeSaveOutcome.Failed(up ? "That is already first." : "That is already last.");
                }

                siblings.RemoveAt(index);
                siblings.Insert(destination, node);
                return null;
            }
        );
    }

    /// <summary>
    /// Sets, clears or removes resources on one level, leaving every resource
    /// not named here exactly as it was.
    /// </summary>
    /// <remarks>
    /// The three intents are the three things a level can say, and each is
    /// written as itself: a value, a null, or nothing at all. Nothing infers one
    /// from another -- a level that inherits and a level that clears look the
    /// same on screen only if the screen chooses to draw them that way, and they
    /// mean opposite things.
    /// </remarks>
    public static ModeSaveOutcome SaveResources(
        string? root,
        NodeAddress address,
        IReadOnlyDictionary<string, ResourceEdit> resources
    )
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(resources);

        return Edit(
            root,
            (_, spaces) =>
            {
                if (Locate(spaces, address, out JsonObject? node, out string? missing) is false)
                {
                    return ModeSaveOutcome.Failed(missing!);
                }

                JsonObject holder = HardwareConfigurationStore.AsObject(node!, "resources");

                foreach ((string key, ResourceEdit edit) in resources)
                {
                    HardwareConfigurationStore.RemoveCaseInsensitive(holder, key);

                    switch (edit.Intent)
                    {
                        case ResourceIntent.Set:
                            holder.Add(
                                key,
                                edit.Optional
                                    ? new JsonObject { ["value"] = edit.Value, ["optional"] = true }
                                    : JsonValue.Create(edit.Value)!
                            );
                            break;

                        case ResourceIntent.Clear:
                            holder.Add(key, null);
                            break;

                        default:
                            // Inherit: the line is gone, which is how a document
                            // says nothing about a resource.
                            break;
                    }
                }

                return null;
            }
        );
    }

    /// <summary>
    /// Reads the document, lets <paramref name="mutate"/> build the candidate in
    /// memory against 'spaces', then validates and writes exactly like every
    /// other store that edits this file.
    /// </summary>
    private static ModeSaveOutcome Edit(string? root, Func<JsonObject, JsonArray, ModeSaveOutcome?> mutate)
    {
        string resolvedRoot = string.IsNullOrWhiteSpace(root) ? PCModePaths.DefaultRoot : root;
        string path = PCModePaths.ConfigurationFile(resolvedRoot);
        string original;

        try
        {
            original = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ModeSaveOutcome.Failed($"Could not read {path}. {exception.Message}");
        }

        JsonNode? parsed;

        try
        {
            parsed = JsonNode.Parse(original, documentOptions: HardwareConfigurationStore.ReadOptions);
        }
        catch (JsonException exception)
        {
            return ModeSaveOutcome.Failed($"The configuration is not valid JSON. {exception.Message}");
        }

        if (parsed is not JsonObject document)
        {
            return ModeSaveOutcome.Failed("The configuration is not a JSON object.");
        }

        if (HardwareConfigurationStore.FindProperty(document, "spaces") is not JsonArray spaces)
        {
            return ModeSaveOutcome.Failed("The configuration does not describe spaces.");
        }

        ModeSaveOutcome? refused = mutate(document, spaces);

        if (refused is not null)
        {
            return refused;
        }

        string candidate = document.ToJsonString(HardwareConfigurationStore.WriteOptions);
        ModeConfiguration configuration;

        try
        {
            // The same validation a real startup runs, against the same
            // production provider list -- not a second, hand-rolled check that
            // could disagree with it.
            IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(
                resolvedRoot,
                candidate
            );

            configuration = ModeConfiguration.Parse(candidate, providers, path);
        }
        catch (ModeConfigurationException exception)
        {
            return ModeSaveOutcome.Failed($"That could not be saved: {exception.Message}");
        }

        string? applicationProblem = ValidateApplicationReferences(document, configuration.Targets);

        if (applicationProblem is not null)
        {
            return ModeSaveOutcome.Failed(applicationProblem);
        }

        try
        {
            // Atomic, and backed up: see ConfigurationWriter.
            ConfigurationWriter.WriteAtomically(path, candidate);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ModeSaveOutcome.Failed($"Could not write {path}. {exception.Message}");
        }

        return ModeSaveOutcome.Ok;
    }

    /// <summary>The node an address names, or why it names none.</summary>
    private static bool Locate(JsonArray spaces, NodeAddress address, out JsonObject? node, out string? problem)
    {
        node = null;
        problem = null;

        if (FindById(spaces, address.SpaceId) is not JsonObject space)
        {
            problem = $"'{address.SpaceId}' is not a configured space.";
            return false;
        }

        if (address.ActivityId is null)
        {
            node = space;
            return true;
        }

        if (FindById(ArrayIn(space, "activities"), address.ActivityId) is not JsonObject activity)
        {
            problem = $"'{address.ActivityId}' is not an activity of '{address.SpaceId}'.";
            return false;
        }

        if (address.SourceId is null)
        {
            node = activity;
            return true;
        }

        if (FindById(ArrayIn(activity, "sources"), address.SourceId) is not JsonObject source)
        {
            problem = $"'{address.SourceId}' is not a source of '{address.ActivityId}'.";
            return false;
        }

        node = source;
        return true;
    }

    /// <summary>The array a node lives in, or null when the address names nothing.</summary>
    private static JsonArray? SiblingsOf(JsonArray spaces, NodeAddress address)
    {
        if (address.ActivityId is null)
        {
            return spaces;
        }

        if (FindById(spaces, address.SpaceId) is not JsonObject space)
        {
            return null;
        }

        JsonArray activities = ArrayIn(space, "activities");

        if (address.SourceId is null)
        {
            return activities;
        }

        return FindById(activities, address.ActivityId) is JsonObject activity ? ArrayIn(activity, "sources") : null;
    }

    /// <summary>Every target key a delete at this address would take with it.</summary>
    private static IReadOnlyList<string> KeysUnder(JsonArray spaces, NodeAddress address)
    {
        if (address.ActivityId is not null)
        {
            var keys = new List<string>();

            if (address.SourceId is not null)
            {
                return [address.TargetKey!];
            }

            keys.Add(address.TargetKey!);

            if (
                FindById(spaces, address.SpaceId) is JsonObject owner
                && FindById(ArrayIn(owner, "activities"), address.ActivityId) is JsonObject activity
            )
            {
                keys.AddRange(SourceKeys(address.SpaceId, address.ActivityId, activity));
            }

            return keys;
        }

        var all = new List<string>();

        if (FindById(spaces, address.SpaceId) is not JsonObject space)
        {
            return all;
        }

        foreach (JsonNode? node in ArrayIn(space, "activities"))
        {
            if (node is not JsonObject activity || IdOf(activity) is not string activityId)
            {
                continue;
            }

            all.Add(new TargetIdentity(address.SpaceId, activityId, null).Key);
            all.AddRange(SourceKeys(address.SpaceId, activityId, activity));
        }

        return all;
    }

    private static IEnumerable<string> SourceKeys(string spaceId, string activityId, JsonObject activity)
    {
        foreach (JsonNode? node in ArrayIn(activity, "sources"))
        {
            if (node is JsonObject source && IdOf(source) is string sourceId)
            {
                yield return new TargetIdentity(spaceId, activityId, sourceId).Key;
            }
        }
    }

    /// <summary>
    /// Applies the caller's chosen replacements for targets about to be removed,
    /// or explains which one was required and missing. Nothing is guessed.
    /// </summary>
    private static string? ReplaceApplicationReferences(
        JsonObject document,
        IReadOnlyList<string> removedKeys,
        IReadOnlyDictionary<string, string>? favoriteReplacements,
        string? startupReplacement
    )
    {
        if (HardwareConfigurationStore.FindProperty(document, "application") is not JsonObject application)
        {
            return null;
        }

        bool Removed(string? reference) =>
            reference is not null && removedKeys.Contains(reference.Trim(), StringComparer.OrdinalIgnoreCase);

        if (
            HardwareConfigurationStore.FindProperty(application, "picker") is JsonObject picker
            && HardwareConfigurationStore.FindProperty(picker, "favorites") is JsonArray favorites
        )
        {
            foreach (JsonNode? node in favorites)
            {
                if (node is not JsonObject favorite || !Removed(TargetOf(favorite)))
                {
                    continue;
                }

                string direction =
                    HardwareConfigurationStore.FindProperty(favorite, "direction") is JsonValue directionValue
                    && directionValue.TryGetValue(out string? directionText)
                        ? directionText
                        : "?";

                if (
                    favoriteReplacements is null
                    || !favoriteReplacements.TryGetValue(direction, out string? replacement)
                    || string.IsNullOrWhiteSpace(replacement)
                )
                {
                    return $"The {direction} picker favorite names it. Choose a replacement for it first.";
                }

                SetTarget(favorite, replacement.Trim());
            }
        }

        // Direct shortcuts are application conveniences rather than picker
        // positions. They do not need a replacement when their target is
        // deleted; remove only the bindings that can no longer be applied.
        if (HardwareConfigurationStore.FindProperty(application, "shortcuts") is JsonArray shortcuts)
        {
            for (int index = shortcuts.Count - 1; index >= 0; index--)
            {
                if (shortcuts[index] is JsonObject shortcut && Removed(TargetOf(shortcut)))
                {
                    shortcuts.RemoveAt(index);
                }
            }
        }

        if (
            HardwareConfigurationStore.FindProperty(application, "startup") is JsonObject startup
            && Removed(TargetOf(startup))
        )
        {
            if (startupReplacement is null)
            {
                startup["behavior"] = "doNothing";
                SetTarget(startup, null);
            }
            else
            {
                SetTarget(startup, startupReplacement.Trim());
            }
        }

        return null;
    }

    /// <summary>
    /// Checks that nothing in 'application' points at a target that does not
    /// exist, against the very targets <see cref="ModeConfiguration.Parse"/>
    /// just accepted -- and resolved the same way a real activation resolves
    /// them, so a reference that still uses a name rather than a key is judged
    /// by whether it would work rather than by how it is spelled.
    /// </summary>
    private static string? ValidateApplicationReferences(JsonObject document, TargetCatalog targets)
    {
        if (HardwareConfigurationStore.FindProperty(document, "application") is not JsonObject application)
        {
            return null;
        }

        if (
            HardwareConfigurationStore.FindProperty(application, "picker") is JsonObject picker
            && HardwareConfigurationStore.FindProperty(picker, "favorites") is JsonArray favorites
        )
        {
            foreach (JsonNode? node in favorites)
            {
                if (node is not JsonObject favorite || TargetOf(favorite) is not string reference)
                {
                    continue;
                }

                if (!targets.TryResolve(reference, out _, out string? unresolved))
                {
                    string direction =
                        HardwareConfigurationStore.FindProperty(favorite, "direction") is JsonValue directionValue
                        && directionValue.TryGetValue(out string? directionText)
                            ? directionText
                            : "?";

                    return $"The {direction} picker favorite would name '{reference}'. {unresolved}";
                }
            }
        }

        if (HardwareConfigurationStore.FindProperty(application, "startup") is JsonObject startup)
        {
            string? behavior =
                HardwareConfigurationStore.FindProperty(startup, "behavior") is JsonValue behaviorValue
                && behaviorValue.TryGetValue(out string? behaviorText)
                    ? behaviorText
                    : null;

            if (
                string.Equals(behavior, "switchToMode", StringComparison.OrdinalIgnoreCase)
                && !targets.TryResolve(TargetOf(startup), out _, out string? unresolved)
            )
            {
                return $"The startup target would be '{TargetOf(startup)}'. {unresolved}";
            }
        }

        if (HardwareConfigurationStore.FindProperty(application, "shortcuts") is JsonArray shortcuts)
        {
            foreach (JsonNode? node in shortcuts)
            {
                if (node is not JsonObject shortcut || TargetOf(shortcut) is not string reference)
                {
                    continue;
                }

                if (!targets.TryResolve(reference, out _, out string? unresolved))
                {
                    return $"A direct shortcut would name '{reference}'. {unresolved}";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Forgets the recorded current target when it is one of the targets that
    /// has just stopped existing.
    /// </summary>
    /// <remarks>
    /// Best effort, like every other write to state.json: the configuration
    /// change already succeeded, so a problem here is not allowed to turn a real
    /// save into a reported failure.
    /// </remarks>
    private static void ClearRecordedTargetIfRemoved(string? root, IReadOnlyList<string> removedKeys)
    {
        string resolvedRoot = string.IsNullOrWhiteSpace(root) ? PCModePaths.DefaultRoot : root;
        var store = new ModeStateStore(PCModePaths.StateFile(resolvedRoot));
        RecordedMode? recorded = store.Read(out _);

        if (recorded is not null && removedKeys.Contains(recorded.Mode, StringComparer.OrdinalIgnoreCase))
        {
            store.TryClear(out _);
        }
    }

    /// <summary>
    /// What a favorite or startup points at, reading the name it was written
    /// under before the product model had targets as well as the current one.
    /// </summary>
    private static string? TargetOf(JsonObject holder)
    {
        foreach (string name in (string[])["target", "mode"])
        {
            if (
                HardwareConfigurationStore.FindProperty(holder, name) is JsonValue value
                && value.TryGetValue(out string? reference)
                && !string.IsNullOrWhiteSpace(reference)
            )
            {
                return reference;
            }
        }

        return null;
    }

    private static void SetTarget(JsonObject holder, string? reference)
    {
        HardwareConfigurationStore.RemoveCaseInsensitive(holder, "mode");
        HardwareConfigurationStore.RemoveCaseInsensitive(holder, "target");
        holder["target"] = reference;
    }

    private static string Write(SourceKind kind) =>
        kind switch
        {
            SourceKind.ExternalDevice => "externalDevice",
            SourceKind.TvApplication => "tvApplication",
            _ => "pcApplication",
        };

    private static string Describe(NodeLevel level) =>
        level switch
        {
            NodeLevel.Space => "space",
            NodeLevel.Activity => "activity",
            _ => "source",
        };

    private static JsonArray ArrayIn(JsonObject parent, string name)
    {
        if (HardwareConfigurationStore.FindProperty(parent, name) is JsonArray existing)
        {
            return existing;
        }

        var created = new JsonArray();
        HardwareConfigurationStore.RemoveCaseInsensitive(parent, name);
        parent[name] = created;
        return created;
    }

    /// <summary>
    /// Adds a deep copy beside the original and gives every copied node an id
    /// that belongs in its new sibling set.
    /// </summary>
    /// <remarks>
    /// Descendant ids are regenerated too. Parent ids already make their target
    /// keys distinct, but deriving every copied id from its current name keeps a
    /// copied subtree internally consistent even when the original had since
    /// been renamed.
    /// </remarks>
    private static void AddDuplicate(JsonArray siblings, JsonObject original, string name, NodeLevel level)
    {
        var copy = (JsonObject)original.DeepClone();
        copy["id"] = LogicalId.From(name, IdsOf(siblings));
        copy["name"] = name;

        if (level == NodeLevel.Space)
        {
            ReidentifyChildren(copy, "activities", NodeLevel.Activity);
        }
        else if (level == NodeLevel.Activity)
        {
            ReidentifyChildren(copy, "sources", NodeLevel.Source);
        }

        siblings.Add(copy);
    }

    private static void ReidentifyChildren(JsonObject parent, string property, NodeLevel childLevel)
    {
        if (HardwareConfigurationStore.FindProperty(parent, property) is not JsonArray children)
        {
            return;
        }

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonObject child in children.OfType<JsonObject>())
        {
            string name = NameOf(child) ?? Describe(childLevel);
            child["id"] = LogicalId.From(name, taken);

            if (childLevel == NodeLevel.Activity)
            {
                ReidentifyChildren(child, "sources", NodeLevel.Source);
            }
        }
    }

    private static ISet<string> IdsOf(JsonArray nodes) =>
        new HashSet<string>(nodes.OfType<JsonObject>().Select(IdOf).OfType<string>(), StringComparer.OrdinalIgnoreCase);

    private static JsonObject? FindById(JsonArray nodes, string id) =>
        nodes
            .OfType<JsonObject>()
            .FirstOrDefault(node => string.Equals(IdOf(node), id.Trim(), StringComparison.OrdinalIgnoreCase));

    private static JsonObject? FindByName(JsonArray nodes, string name) =>
        nodes
            .OfType<JsonObject>()
            .FirstOrDefault(node => string.Equals(NameOf(node), name, StringComparison.OrdinalIgnoreCase));

    private static string? IdOf(JsonObject node) => Text(node, "id");

    private static string? NameOf(JsonObject node) => Text(node, "name");

    private static string? Text(JsonObject node, string property) =>
        HardwareConfigurationStore.FindProperty(node, property) is JsonValue value
        && value.TryGetValue(out string? text)
        && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}

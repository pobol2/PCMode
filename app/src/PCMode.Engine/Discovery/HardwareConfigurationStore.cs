using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCMode.Engine;

/// <summary>Whether an alias was saved, and why not when it was refused.</summary>
public sealed record HardwareAliasOutcome(bool Success, string? Problem)
{
    public static readonly HardwareAliasOutcome Ok = new(true, null);

    public static HardwareAliasOutcome Failed(string problem) => new(false, problem);
}

/// <summary>
/// Adds and renames PCMode aliases for discovered hardware, by editing the same
/// configuration file <see cref="ModeConfiguration"/> reads.
/// </summary>
/// <remarks>
/// NO SECOND SOURCE OF TRUTH
///
/// An alias is nothing but an entry under 'providers.display.monitors' or
/// 'providers.audio.devices' -- the sections <see cref="DisplayProvider"/> and
/// <see cref="AudioProvider"/> already read to apply modes. There is no separate
/// device database: this writes exactly the document those providers parse, and
/// validates the result the same way <see cref="EngineBootstrap.Create"/> does,
/// through <see cref="EngineBootstrap.ProductionProviders"/> and
/// <see cref="ModeConfiguration.Parse"/>, before a single byte reaches disk.
///
/// ONLY EVER ONE ALIAS PER DEVICE
///
/// Setting an alias for a device that already has a different one moves it
/// rather than adding a second entry: the same call that adds a never-before-seen
/// device to PCMode is the one that renames an existing alias, and neither path
/// can leave two names pointing at one piece of hardware.
///
/// NOTHING HERE TOUCHES A MODE, AND NOTHING HERE TOUCHES HARDWARE
///
/// No mode's resources are read or written, and no display, audio, controller or
/// application state is asked to change -- this edits JSON on disk and nothing
/// else. Whether a newly aliased device is ever used by a mode is left entirely
/// to whoever edits the modes.
/// </remarks>
public static class HardwareConfigurationStore
{
    /// <summary>
    /// Adds or renames a monitor's alias under 'providers.display.monitors'.
    /// </summary>
    /// <param name="devicePath">
    /// The stable identifier <see cref="DisplayDiscoverer"/> reports as a
    /// monitor's <c>Identifier</c> -- never a friendly name, which is not unique.
    /// </param>
    public static HardwareAliasOutcome SetDisplayAlias(string? root, string devicePath, string alias) =>
        SetAlias(root, "display", "monitors", devicePath, alias, "monitor");

    /// <summary>
    /// Adds or renames an audio output's alias under 'providers.audio.devices'.
    /// </summary>
    /// <param name="endpointName">
    /// The exact name Windows shows in Sound settings -- what
    /// <see cref="AudioProvider"/> itself matches by, and what
    /// <see cref="AudioOutputDiscoverer"/> reports as an endpoint's
    /// <c>Identifier</c> only when no other active endpoint shares it.
    /// </param>
    public static HardwareAliasOutcome SetAudioAlias(string? root, string endpointName, string alias) =>
        SetAlias(root, "audio", "devices", endpointName, alias, "audio device");

    private static HardwareAliasOutcome SetAlias(
        string? root, string providerKey, string mapKey, string identifier, string alias, string noun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        string resolvedRoot = string.IsNullOrWhiteSpace(root) ? PCModePaths.DefaultRoot : root;
        alias = alias?.Trim() ?? string.Empty;

        if (alias.Length == 0)
        {
            return HardwareAliasOutcome.Failed($"Give the {noun} a name.");
        }

        string path = PCModePaths.ConfigurationFile(resolvedRoot);
        string original;

        try
        {
            original = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return HardwareAliasOutcome.Failed($"Could not read {path}. {exception.Message}");
        }

        JsonNode? parsed;

        try
        {
            parsed = JsonNode.Parse(original, documentOptions: ReadOptions);
        }
        catch (JsonException exception)
        {
            return HardwareAliasOutcome.Failed($"The configuration is not valid JSON. {exception.Message}");
        }

        if (parsed is not JsonObject document)
        {
            return HardwareAliasOutcome.Failed("The configuration is not a JSON object.");
        }

        try
        {
            JsonObject providers = AsObject(document, "providers");
            JsonObject section = AsObject(providers, providerKey);
            JsonObject map = AsObject(section, mapKey);

            JsonNode? existingForAlias = FindProperty(map, alias);

            if (existingForAlias is not null &&
                !string.Equals(IdentifierOf(existingForAlias), identifier, StringComparison.OrdinalIgnoreCase))
            {
                return HardwareAliasOutcome.Failed(
                    $"'{alias}' already names a different {noun}. Choose another name, or rename that one first.");
            }

            // Moves rather than duplicates: a device that already has a
            // different alias loses it here, so there is never more than one
            // name for one piece of hardware.
            string? previousKey = FindKeyFor(map, identifier);
            bool renamed = previousKey is not null && !string.Equals(previousKey, alias, StringComparison.OrdinalIgnoreCase);

            if (renamed)
            {
                map.Remove(previousKey!);
            }

            RemoveCaseInsensitive(map, alias);
            map.Add(alias, JsonValue.Create(identifier));

            // A rename is not just this entry: a display layout may name this
            // monitor in its 'monitors' list, 'primary', 'placement' or
            // 'refresh' strings, and a mode may name an audio device directly
            // as its 'audio' resource. Those are references to the alias, not
            // copies of it -- leaving them pointing at a name that no longer
            // exists would fail the very validation below, and silently orphan
            // whatever used the old name if it did not.
            if (renamed)
            {
                if (providerKey == "display")
                {
                    RenameDisplayLayoutReferences(section, previousKey!, alias);
                }
                else if (providerKey == "audio")
                {
                    RenameAudioModeReferences(document, previousKey!, alias);
                }
            }
        }
        catch (InvalidOperationException exception)
        {
            return HardwareAliasOutcome.Failed(exception.Message);
        }

        string candidate = document.ToJsonString(WriteOptions);

        try
        {
            // The same validation a real startup runs, against the same
            // production provider list -- not a second, hand-rolled check that
            // could disagree with it.
            IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(resolvedRoot, candidate);
            ModeConfiguration.Parse(candidate, providers, path);
        }
        catch (ModeConfigurationException exception)
        {
            return HardwareAliasOutcome.Failed($"That name could not be saved: {exception.Message}");
        }

        try
        {
            // Atomic, and backed up: see ConfigurationWriter.
            ConfigurationWriter.WriteAtomically(path, candidate);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return HardwareAliasOutcome.Failed($"Could not write {path}. {exception.Message}");
        }

        return HardwareAliasOutcome.Ok;
    }

    /// <summary>
    /// Comments and trailing commas are tolerated on read, like everywhere else
    /// this file is parsed -- but are not preserved: rewriting a document a
    /// person may have hand-edited is what an alias-writing action is, and a
    /// second, comment-preserving JSON writer is not worth carrying for it.
    /// </summary>
    private static readonly string[] LayoutDirections = ["LeftOf", "RightOf", "Above", "Below"];

    /// <summary>
    /// Renames every reference to a monitor alias inside 'providers.display.layouts':
    /// its entry in 'monitors', 'primary' if it is the primary, and any
    /// 'placement' or 'refresh' string that names it.
    /// </summary>
    private static void RenameDisplayLayoutReferences(JsonObject displaySection, string oldName, string newName)
    {
        if (FindProperty(displaySection, "layouts") is not JsonObject layouts)
        {
            return;
        }

        foreach ((string _, JsonNode? layoutNode) in layouts)
        {
            if (layoutNode is not JsonObject layout)
            {
                continue;
            }

            if (FindProperty(layout, "monitors") is JsonArray monitors)
            {
                for (int index = 0; index < monitors.Count; index++)
                {
                    if (monitors[index] is JsonValue value && value.TryGetValue(out string? name) &&
                        string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase))
                    {
                        monitors[index] = JsonValue.Create(newName);
                    }
                }
            }

            if (FindProperty(layout, "primary") is JsonValue primary && primary.TryGetValue(out string? primaryName) &&
                string.Equals(primaryName, oldName, StringComparison.OrdinalIgnoreCase))
            {
                layout["primary"] = JsonValue.Create(newName);
            }

            RenamePlacementEntries(layout, oldName, newName);
            RenameRefreshEntries(layout, oldName, newName);
        }
    }

    /// <summary>Rewrites "ThisMonitor RightOf ThatMonitor" entries naming <paramref name="oldName"/>.</summary>
    private static void RenamePlacementEntries(JsonObject layout, string oldName, string newName)
    {
        if (FindProperty(layout, "placement") is not JsonArray placement)
        {
            return;
        }

        for (int index = 0; index < placement.Count; index++)
        {
            if (placement[index] is not JsonValue value || !value.TryGetValue(out string? entry))
            {
                continue;
            }

            string[] tokens = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            int directionIndex = Array.FindIndex(
                tokens, token => LayoutDirections.Any(direction => string.Equals(direction, token, StringComparison.OrdinalIgnoreCase)));

            if (directionIndex <= 0 || directionIndex >= tokens.Length - 1)
            {
                continue;
            }

            string monitor = string.Join(' ', tokens[..directionIndex]);
            string direction = tokens[directionIndex];
            string anchor = string.Join(' ', tokens[(directionIndex + 1)..]);
            bool changed = false;

            if (string.Equals(monitor, oldName, StringComparison.OrdinalIgnoreCase))
            {
                monitor = newName;
                changed = true;
            }

            if (string.Equals(anchor, oldName, StringComparison.OrdinalIgnoreCase))
            {
                anchor = newName;
                changed = true;
            }

            if (changed)
            {
                placement[index] = JsonValue.Create($"{monitor} {direction} {anchor}");
            }
        }
    }

    /// <summary>Rewrites "ThisMonitor 144" entries naming <paramref name="oldName"/>.</summary>
    private static void RenameRefreshEntries(JsonObject layout, string oldName, string newName)
    {
        if (FindProperty(layout, "refresh") is not JsonArray refresh)
        {
            return;
        }

        for (int index = 0; index < refresh.Count; index++)
        {
            if (refresh[index] is not JsonValue value || !value.TryGetValue(out string? entry))
            {
                continue;
            }

            string[] tokens = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length < 2)
            {
                continue;
            }

            string monitor = string.Join(' ', tokens[..^1]);

            if (string.Equals(monitor, oldName, StringComparison.OrdinalIgnoreCase))
            {
                refresh[index] = JsonValue.Create($"{newName} {tokens[^1]}");
            }
        }
    }

    /// <summary>
    /// Renames every 'audio' resource that names this device alias, at every
    /// level of the hierarchy, since that resource is the alias itself rather
    /// than a copy of it.
    /// </summary>
    private static void RenameAudioModeReferences(JsonObject document, string oldName, string newName) =>
        LayoutConfigurationStore.RenameModeReferences(document, "audio", oldName, newName);

    /// <summary>
    /// Shared with every other store that edits the same document, for the same
    /// reason: reading and writing a hand-editable JSON file the same way
    /// everywhere is what keeps two editors from disagreeing about what it
    /// means.
    /// </summary>
    internal static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    internal static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    internal static JsonObject AsObject(JsonObject parent, string name)
    {
        JsonNode? existing = FindProperty(parent, name);

        if (existing is JsonObject already)
        {
            return already;
        }

        if (existing is not null)
        {
            throw new InvalidOperationException($"'{name}' is not an object in the configuration.");
        }

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    internal static JsonNode? FindProperty(JsonObject obj, string name)
    {
        foreach ((string key, JsonNode? value) in obj)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    internal static void RemoveCaseInsensitive(JsonObject obj, string name)
    {
        string? exact = obj.Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            obj.Remove(exact);
        }
    }

    /// <summary>The key already pointing at this identifier, or null.</summary>
    private static string? FindKeyFor(JsonObject map, string identifier)
    {
        foreach ((string key, JsonNode? value) in map)
        {
            if (string.Equals(IdentifierOf(value), identifier, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// The identifier a mapping entry names, in either the plain string form or
    /// the longer <c>{ "device": "...", "requires": ... }</c> form audio devices
    /// may also be written in.
    /// </summary>
    private static string IdentifierOf(JsonNode? value) => value switch
    {
        JsonValue scalar when scalar.TryGetValue(out string? text) => text ?? string.Empty,
        JsonObject detailed when detailed["device"] is JsonValue deviceValue &&
            deviceValue.TryGetValue(out string? device) => device ?? string.Empty,
        _ => string.Empty,
    };
}

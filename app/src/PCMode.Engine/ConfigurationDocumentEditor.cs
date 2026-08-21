using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCMode.Engine;

/// <summary>
/// Reads the configuration document, lets a caller change it, and puts it back
/// -- validated the same way a real startup validates it, and never half-written.
/// </summary>
/// <remarks>
/// ONE EDIT PATH, NOT ONE PER SECTION
///
/// Every graphical editor in PCMode changes the same file: display layouts,
/// cursor positions, audio outputs and the Party group are all sections of the
/// one document <see cref="ModeConfiguration"/> parses. What they have in common
/// -- read it, refuse a file that is not valid JSON, hand the object graph to a
/// mutation, validate the candidate through
/// <see cref="EngineBootstrap.ProductionProviders"/> and
/// <see cref="ModeConfiguration.Parse"/>, and only then write it atomically --
/// is this, and it lives here so that a new section cannot accidentally get a
/// weaker version of it.
///
/// NOTHING REACHES DISK THAT A STARTUP WOULD REFUSE
///
/// The candidate is validated in memory against the same production provider
/// list a real startup builds. A mutation that produces a document the engine
/// could not load leaves the file exactly as it was, and the caller is told why
/// in the same words the engine would have used.
///
/// <see cref="HierarchyStore"/> keeps its own variant deliberately: it also
/// has to reach the 'spaces' array before mutating and re-check 'application'
/// references afterwards, neither of which any section editor needs.
/// </remarks>
internal static class ConfigurationDocumentEditor
{
    /// <summary>
    /// Applies <paramref name="mutate"/> to the configuration document.
    /// </summary>
    /// <param name="mutate">
    /// Changes the document in place. Returns null to accept the change, or a
    /// refusal -- which leaves the file completely untouched.
    /// </param>
    internal static ModeSaveOutcome Edit(string? root, Func<JsonObject, ModeSaveOutcome?> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

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

        ModeSaveOutcome? refused = mutate(document);

        if (refused is not null)
        {
            return refused;
        }

        string candidate = document.ToJsonString(HardwareConfigurationStore.WriteOptions);

        try
        {
            // The same validation a real startup runs, against the same
            // production provider list -- not a second, hand-rolled check
            // that could disagree with it.
            IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.ProductionProviders(resolvedRoot, candidate);
            ModeConfiguration.Parse(candidate, providers, path);
        }
        catch (ModeConfigurationException exception)
        {
            return ModeSaveOutcome.Failed($"That could not be saved: {exception.Message}");
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
}

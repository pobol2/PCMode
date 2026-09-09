using System.Text.Json.Nodes;

namespace PCMode.Engine;

/// <summary>What a start or stop command edit takes: a path, or a URI, never both.</summary>
/// <param name="Executable">A file this machine has. Null when this is a URI.</param>
/// <param name="Arguments">
/// One entry per argument. Ignored when <paramref name="Uri"/> is given -- a URI
/// has nowhere for an argument to go.
/// </param>
/// <param name="Uri">What to hand to the shell, such as "steam://open/bigpicture". Null for a program.</param>
public sealed record LaunchCommandEdit(string? Executable, IReadOnlyList<string> Arguments, string? Uri);

/// <summary>One entry of a "requires" block: a resource, and the value it must hold.</summary>
public sealed record ApplicationRequirementEdit(string ResourceKey, string Value);

/// <summary>What to do once the application has a window.</summary>
public sealed record ReadinessEdit(bool Activate, string? Keys, int WithinMs);

/// <summary>
/// One application as a settings screen has it: unchecked, and not yet the
/// <see cref="ApplicationDefinition"/> that comes out of a document that has
/// already been validated.
/// </summary>
public sealed record ApplicationEdit(
    string Name,
    ApplicationObservationKind ObservationKind,
    string ProcessName,
    LaunchCommandEdit Start,
    LaunchCommandEdit? Stop,
    bool Reactivate,
    IReadOnlyList<ApplicationRequirementEdit> Requires,
    IReadOnlyList<string> RestartWhen,
    ReadinessEdit? Ready,
    int StartWithinMs,
    int StopWithinMs
);

/// <summary>
/// Creates, edits and removes applications in the same configuration file
/// <see cref="ApplicationProvider"/> reads, by editing 'providers.applications'.
/// </summary>
/// <remarks>
/// NO SECOND SOURCE OF TRUTH
///
/// This writes exactly the document <see cref="ApplicationCatalog"/> parses, and
/// validates the result through the same production pipeline a real startup
/// uses -- see <see cref="ConfigurationDocumentEditor"/> -- before a single byte
/// reaches disk. Most of what makes an entry malformed -- a scheme-less URI, a
/// wait outside the allowed range, arguments given alongside a URI -- is refused
/// there, by <see cref="ApplicationCatalog.TryRead"/> itself; this only checks
/// the handful of things a person can act on before that pass even runs.
///
/// AN APPLICATION'S NAME IS ITS RESOURCE KEY, NOT A VALUE
///
/// Every other alias this product has -- a display layout, an audio device, a
/// cursor position -- is a value some fixed resource key can be set to. An
/// application IS the resource key: a mode holds 'SteamBigPicture', not an
/// 'application' resource set to it. Renaming one therefore does not rewrite a
/// value the way <see cref="LayoutConfigurationStore.RenameModeReferences"/>
/// does; it moves the key itself, in every mode that has an opinion about it --
/// see <see cref="LayoutConfigurationStore.RenameResourceKey"/>. Removing one is
/// refused the same way, by whether any mode defines the key at all, regardless
/// of whether it asked for Running or Stopped.
/// </remarks>
public static class ApplicationConfigurationStore
{
    /// <summary>
    /// Creates a new application, or replaces the one named
    /// <paramref name="previousName"/> when this is an edit.
    /// </summary>
    /// <param name="previousName">
    /// Null for a new application. The application's current name for an edit,
    /// even when <paramref name="edit"/> does not rename it -- passing it always
    /// is what lets a rename and an ordinary save share one code path.
    /// </param>
    public static ModeSaveOutcome SaveApplication(string? root, string? previousName, ApplicationEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        string name = edit.Name?.Trim() ?? string.Empty;
        string process = edit.ProcessName?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            return ModeSaveOutcome.Failed("Give the application a name. That is what a mode asks for.");
        }

        if (edit.ObservationKind == ApplicationObservationKind.Process && process.Length == 0)
        {
            return ModeSaveOutcome.Failed(
                "Give the process name Windows reports for it, without '.exe', or choose Launch only."
            );
        }

        if (edit.ObservationKind is not (ApplicationObservationKind.Process or ApplicationObservationKind.None))
        {
            return ModeSaveOutcome.Failed("Choose how PCMode should detect this application at runtime.");
        }

        if (CommandProblem(edit.Start, "start") is string startProblem)
        {
            return ModeSaveOutcome.Failed(startProblem);
        }

        if (edit.Stop is LaunchCommandEdit stop && CommandProblem(stop, "stop") is string stopProblem)
        {
            return ModeSaveOutcome.Failed(stopProblem);
        }

        return ConfigurationDocumentEditor.Edit(
            root,
            document =>
            {
                JsonObject providers = HardwareConfigurationStore.AsObject(document, "providers");
                JsonObject applications = HardwareConfigurationStore.AsObject(providers, ApplicationCatalog.SectionKey);

                bool renaming =
                    previousName is not null && !string.Equals(previousName, name, StringComparison.OrdinalIgnoreCase);

                if (previousName is null && HardwareConfigurationStore.FindProperty(applications, name) is not null)
                {
                    return ModeSaveOutcome.Failed($"'{name}' is already a configured application.");
                }

                if (renaming)
                {
                    HardwareConfigurationStore.RemoveCaseInsensitive(applications, previousName!);

                    // What a mode holds is this key itself, not a value naming it --
                    // see the class remarks. A value rename would leave every mode
                    // that used to ask for this application asking for nothing.
                    LayoutConfigurationStore.RenameResourceKey(document, previousName!, name);
                }

                HardwareConfigurationStore.RemoveCaseInsensitive(applications, name);
                applications.Add(name, BuildBody(edit));
                return null;
            }
        );
    }

    /// <summary>
    /// Removes an application, refused while any mode still has an opinion about
    /// it -- Running or Stopped both count.
    /// </summary>
    public static ModeSaveOutcome RemoveApplication(string? root, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string trimmed = name.Trim();

        return ConfigurationDocumentEditor.Edit(
            root,
            document =>
            {
                if (
                    FindApplicationsSection(document) is not JsonObject applications
                    || HardwareConfigurationStore.FindProperty(applications, trimmed) is null
                )
                {
                    return ModeSaveOutcome.Failed($"'{trimmed}' is not a configured application.");
                }

                if (LayoutConfigurationStore.ModesReferencingKey(document, trimmed) is { Count: > 0 } referencing)
                {
                    return ModeSaveOutcome.Failed(
                        $"'{trimmed}' is used by {string.Join(", ", referencing)}. Change those first."
                    );
                }

                HardwareConfigurationStore.RemoveCaseInsensitive(applications, trimmed);
                return null;
            }
        );
    }

    /// <summary>
    /// What is obviously wrong with a start or stop command, before the shared
    /// parser gets to the rest of it.
    /// </summary>
    private static string? CommandProblem(LaunchCommandEdit command, string field)
    {
        bool hasExecutable = !string.IsNullOrWhiteSpace(command.Executable);
        bool hasUri = !string.IsNullOrWhiteSpace(command.Uri);

        if (hasExecutable == hasUri)
        {
            return hasExecutable
                ? $"Give the '{field}' command either a program to run or a web address, not both."
                : $"Give the '{field}' command either a program to run or a web address to open.";
        }

        if (hasUri && command.Arguments.Count > 0)
        {
            return $"A web address cannot take arguments. Put them in the address itself, or switch '{field}' to a program.";
        }

        return null;
    }

    private static JsonObject BuildBody(ApplicationEdit edit)
    {
        var body = new JsonObject { ["start"] = BuildCommand(edit.Start) };

        if (edit.ObservationKind == ApplicationObservationKind.Process)
        {
            body["process"] = edit.ProcessName.Trim();
        }

        if (edit.ObservationKind == ApplicationObservationKind.Process && edit.Stop is LaunchCommandEdit stop)
        {
            body["stop"] = BuildCommand(stop);
        }

        if (edit.ObservationKind == ApplicationObservationKind.Process && edit.Reactivate)
        {
            body["reactivate"] = true;
        }

        if (edit.Requires.Count > 0)
        {
            var requires = new JsonObject();

            foreach (ApplicationRequirementEdit requirement in edit.Requires)
            {
                requires[requirement.ResourceKey.Trim()] = requirement.Value.Trim();
            }

            body["requires"] = requires;
        }

        if (edit.ObservationKind == ApplicationObservationKind.Process && edit.RestartWhen.Count > 0)
        {
            body["restartWhen"] = new JsonArray([
                .. edit.RestartWhen.Select(key => (JsonNode)JsonValue.Create(key.Trim())!),
            ]);
        }

        if (edit.ObservationKind == ApplicationObservationKind.Process && edit.Ready is ReadinessEdit ready)
        {
            var readyBody = new JsonObject();

            if (ready.Activate)
            {
                readyBody["activate"] = true;
            }

            if (!string.IsNullOrEmpty(ready.Keys))
            {
                readyBody["keys"] = ready.Keys;
            }

            readyBody["within"] = ready.WithinMs;
            body["ready"] = readyBody;
        }

        body["startWithin"] = edit.StartWithinMs;

        if (edit.ObservationKind == ApplicationObservationKind.Process)
        {
            body["stopWithin"] = edit.StopWithinMs;
        }

        return body;
    }

    /// <summary>
    /// Writes the plain string form for a bare path or URI, and the longer
    /// object form only when arguments make it necessary -- the same choice a
    /// person hand-writing the file would make, and what keeps a document that
    /// never used arguments looking the way it always did.
    /// </summary>
    private static JsonNode BuildCommand(LaunchCommandEdit command)
    {
        if (!string.IsNullOrWhiteSpace(command.Uri))
        {
            return JsonValue.Create(command.Uri.Trim())!;
        }

        string executable = command.Executable!.Trim();

        if (command.Arguments.Count == 0)
        {
            return JsonValue.Create(executable)!;
        }

        return new JsonObject
        {
            ["executable"] = executable,
            ["arguments"] = new JsonArray([
                .. command.Arguments.Select(argument => (JsonNode)JsonValue.Create(argument)!),
            ]),
        };
    }

    private static JsonObject? FindApplicationsSection(JsonObject document) =>
        HardwareConfigurationStore.FindProperty(document, "providers") is JsonObject providers
            ? HardwareConfigurationStore.FindProperty(providers, ApplicationCatalog.SectionKey) as JsonObject
            : null;
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCMode.Engine;

/// <summary>What is wrong with a configuration document.</summary>
public sealed class ModeConfigurationException(string message) : Exception(message);

/// <summary>One target, and what it sets, once the hierarchy has been composed.</summary>
/// <remarks>
/// The one shape the engine applies. Resources are keyed by provider key
/// ("theme"), and the order they were composed in is a tie-breaker only:
/// dependency planning belongs to <see cref="TransitionPlan"/>.
/// </remarks>
/// <param name="Name">
/// The target's key -- what a favorite, the startup target and the recorded
/// current target name it by. Never shown to a user on its own; see
/// <see cref="DisplayName"/>.
/// </param>
/// <param name="Target">Which Space, Activity and Source this is.</param>
public sealed record ConfiguredMode(
    string Name,
    IReadOnlyList<ConfiguredResource> Resources,
    ConfiguredTarget Target)
{
    /// <summary>What to call this target in something a person reads.</summary>
    public string DisplayName => Target.DisplayPath;
}

/// <summary>One resource of one target.</summary>
/// <param name="Optional">
/// Whether the target still means something without this. An optional resource
/// that is definitely unavailable is left out and reported; a required one
/// refuses the whole transition. It changes nothing else: an optional resource
/// that is present is applied, verified and allowed to fail exactly like any
/// other.
/// </param>
public sealed record ConfiguredResource(string Key, string Value, bool Optional = false);

/// <summary>
/// PCMode's configured intent: Space, Activity and Source.
/// </summary>
/// <remarks>
/// VERSIONED, AND CHECKED
///
/// schemaVersion is required and must be one this build understands. A document
/// from a newer version is refused by name rather than read leniently: a file
/// whose unknown fields were silently dropped is a configuration the user
/// believes they have and does not.
///
/// VALIDATED BEFORE ANYTHING IS APPLIED
///
/// Every resource of every level is checked against a real provider at load
/// time -- unknown key, unusable value, duplicate or malformed identity, a name
/// that would make two targets indistinguishable. A document that fails is
/// refused whole. Partially loading it would leave a target list where some
/// entries work and some do nothing, which is a worse state than not starting.
/// </remarks>
public sealed class ModeConfiguration
{
    /// <summary>The document version this build reads and writes.</summary>
    public const int SchemaVersion = 1;

    private readonly Dictionary<string, ConfiguredMode> _byKey;

    private ModeConfiguration(IReadOnlyList<ConfiguredMode> modes, IReadOnlyList<ConfiguredSpace> spaces, string? path)
    {
        Modes = modes;
        Spaces = spaces;
        Path = path;
        Targets = new TargetCatalog([.. modes.Select(mode => mode.Target)]);
        _byKey = modes.ToDictionary(mode => mode.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every target, composed, in configuration order.</summary>
    public IReadOnlyList<ConfiguredMode> Modes { get; }

    /// <summary>The hierarchy as it is written.</summary>
    public IReadOnlyList<ConfiguredSpace> Spaces { get; }

    /// <summary>Every target and the one way a reference to one is resolved.</summary>
    public TargetCatalog Targets { get; }

    /// <summary>Where this came from, when it came from a file.</summary>
    public string? Path { get; }

    /// <summary>
    /// The target a reference names, or null when it names none or more than
    /// one. Keys, full display paths and unambiguous names are all accepted;
    /// see <see cref="TargetCatalog"/>.
    /// </summary>
    public ConfiguredMode? Find(string name) =>
        Targets.Resolve(name) is ConfiguredTarget target && _byKey.TryGetValue(target.Key, out ConfiguredMode? mode)
            ? mode
            : null;

    /// <summary>
    /// Reads the configuration under <paramref name="root"/>, writing the
    /// defaults first if there is nothing there yet.
    /// </summary>
    /// <remarks>
    /// First run is the ordinary case, not an error: a fresh machine gets a
    /// working Space with four activities rather than an empty list and an
    /// explanation. The file is then the user's, and is never rewritten except
    /// by something they asked for.
    /// </remarks>
    public static ModeConfiguration LoadOrCreate(string root, IReadOnlyList<IModeResourceProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        (string json, string path) = ReadOrCreate(root);

        return Parse(json, providers, path);
    }

    /// <summary>
    /// The document under <paramref name="root"/>, and where it came from,
    /// writing the defaults first if there is nothing there yet.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="LoadOrCreate"/> because of a chicken and egg.
    /// Some providers do not exist until the document says so -- there is one
    /// application resource per application somebody configured -- so the text has
    /// to be readable before the provider list can be built, and the provider list
    /// has to exist before the text can be validated. This is the first half; see
    /// <see cref="EngineBootstrap.Create"/> for both.
    ///
    /// Nothing here interprets anything. Getting the file and understanding it
    /// stay separate, so there is still exactly one place that decides what a
    /// document means.
    /// </remarks>
    public static (string Json, string Path) ReadOrCreate(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        string path = PCModePaths.ConfigurationFile(root);

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, DefaultDocument());
        }

        try
        {
            return (File.ReadAllText(path), path);
        }
        catch (IOException exception)
        {
            throw new ModeConfigurationException($"Could not read {path}. {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ModeConfigurationException($"Could not read {path}. {exception.Message}");
        }
    }

    /// <summary>Reads a document, refusing anything it cannot apply.</summary>
    public static ModeConfiguration Parse(
        string json,
        IReadOnlyList<IModeResourceProvider> providers,
        string? path = null)
    {
        ArgumentNullException.ThrowIfNull(providers);

        ConfigurationDocument? document;

        try
        {
            document = JsonSerializer.Deserialize<ConfigurationDocument>(json, ReadOptions);
        }
        catch (JsonException exception)
        {
            throw new ModeConfigurationException($"The configuration is not valid JSON. {exception.Message}");
        }

        if (document is null)
        {
            throw new ModeConfigurationException("The configuration is empty.");
        }

        if (document.SchemaVersion != SchemaVersion)
        {
            throw new ModeConfigurationException(
                $"The configuration is schema version {document.SchemaVersion}, and this build reads " +
                $"version {SchemaVersion}.");
        }

        Dictionary<string, IModeResourceProvider> known =
            providers.ToDictionary(provider => provider.Key, StringComparer.OrdinalIgnoreCase);

        Configure(document.Providers, known, providers);

        bool declaresSpaces = document.Spaces is JsonElement spaces && spaces.ValueKind != JsonValueKind.Null;
        bool declaresModes = document.Modes is JsonElement modes && modes.ValueKind != JsonValueKind.Null;

        if (declaresModes)
        {
            // The flat mode list this build no longer reads. Refused by name
            // rather than ignored, so a document that still has one is not
            // silently treated as having no targets at all.
            throw new ModeConfigurationException(
                "The configuration defines 'modes', the flat list from before Space -> Activity -> Source. " +
                "This build no longer converts it; open the configuration with a version of PCMode that " +
                "still supports the conversion first.");
        }

        if (!declaresSpaces)
        {
            throw new ModeConfigurationException(
                $"The configuration is schema version {SchemaVersion} and defines no spaces.");
        }

        IReadOnlyList<ConfiguredSpace> read = TargetHierarchyReader.Read(document.Spaces!.Value, known);

        return new ModeConfiguration(Compose(read), read, path);
    }

    /// <summary>
    /// Every target the hierarchy offers, composed into what a transition
    /// applies.
    /// </summary>
    /// <remarks>
    /// An Activity is a target, and each of its Sources is another target beside
    /// it. Deliberately not "an Activity with Sources can only be reached
    /// through one of them": adding a Source would then silently invalidate
    /// every reference to the Activity, and "Gaming, without picking anything
    /// yet" is a thing people mean.
    /// </remarks>
    private static IReadOnlyList<ConfiguredMode> Compose(IReadOnlyList<ConfiguredSpace> spaces)
    {
        var modes = new List<ConfiguredMode>();

        foreach (ConfiguredSpace space in spaces)
        {
            foreach (ConfiguredActivity activity in space.Activities)
            {
                modes.Add(new ConfiguredMode(
                    ConfiguredTarget.For(space, activity, source: null).Key,
                    TargetComposition.Compose(space, activity, source: null),
                    ConfiguredTarget.For(space, activity, source: null)));

                foreach (ConfiguredSource source in activity.Sources)
                {
                    modes.Add(new ConfiguredMode(
                        ConfiguredTarget.For(space, activity, source).Key,
                        TargetComposition.Compose(space, activity, source),
                        ConfiguredTarget.For(space, activity, source)));
                }
            }
        }

        return modes;
    }

    /// <summary>
    /// Hands each provider its own settings, before anything is checked.
    /// </summary>
    /// <remarks>
    /// The order matters. Whether a value is usable can depend entirely on the
    /// settings -- "Corner" is a cursor position only because the document said
    /// so -- and checking the hierarchy first would refuse every part of it for
    /// a reason that is about to stop being true.
    ///
    /// A settings key naming nothing is refused, like an unknown resource key
    /// and for the same reason: a mistyped section is not a section anybody
    /// notices is missing. It is simply ignored, and the positions the user
    /// carefully wrote never exist.
    /// </remarks>
    private static void Configure(
        Dictionary<string, JsonElement>? declared,
        Dictionary<string, IModeResourceProvider> known,
        IReadOnlyList<IModeResourceProvider> providers)
    {
        // Which section each provider reads. Usually its own key, and deliberately
        // allowed to be shared: one television is two resources, and its address
        // belongs in one place. See IConfigurableProvider.SettingsKey.
        var readers = new Dictionary<string, List<IConfigurableProvider>>(StringComparer.OrdinalIgnoreCase);

        foreach (IModeResourceProvider provider in providers)
        {
            if (provider is IConfigurableProvider configurable)
            {
                if (!readers.TryGetValue(configurable.SettingsKey, out List<IConfigurableProvider>? sharing))
                {
                    readers[configurable.SettingsKey] = sharing = [];
                }

                sharing.Add(configurable);
            }
        }

        var settings = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, JsonElement value) in declared ?? [])
        {
            if (!readers.ContainsKey(key))
            {
                // Two different mistakes, told apart because they call for
                // different corrections: a section for a provider that takes none,
                // and a section nothing reads at all.
                throw new ModeConfigurationException(
                    known.TryGetValue(key, out IModeResourceProvider? owner)
                        ? $"'providers.{key}' has settings, but {owner.DisplayName} takes none."
                        : $"'providers' has settings for '{key}', which nothing here reads. " +
                          $"Known: {string.Join(", ", readers.Keys.Order(StringComparer.OrdinalIgnoreCase))}.");
            }

            if (!settings.TryAdd(key, value))
            {
                throw new ModeConfigurationException($"'providers' defines '{key}' more than once.");
            }
        }

        foreach (IModeResourceProvider provider in providers)
        {
            if (provider is not IConfigurableProvider configurable)
            {
                continue;
            }

            // Every reader of a shared section is handed it, and each reads it for
            // itself. Configuring one and letting it inform the others would make
            // the result depend on the order this list happens to be in.
            JsonElement? own = settings.TryGetValue(configurable.SettingsKey, out JsonElement value) ? value : null;

            if (!configurable.TryConfigure(own, out string? problem))
            {
                throw new ModeConfigurationException(problem!);
            }
        }
    }

    /// <summary>
    /// What a machine that has never run this gets.
    /// </summary>
    /// <remarks>
    /// One Space, because a fresh machine is one desk until its owner says
    /// otherwise, and the four activities the picker's compass points at -- so
    /// the wheel works out of the box rather than pointing at targets that do
    /// not exist. Desktop is light and the three leisure activities are dark,
    /// which is the distinction the theme provider is actually good for.
    ///
    /// The cursor position is set once, on the Space, and overridden by the one
    /// activity that wants the pointer where it can be seen. That is the
    /// inheritance the hierarchy is for, expressed in the smallest configuration
    /// anybody will ever have: on a television a pointer left in the middle of
    /// the picture sits there for the whole film.
    /// </remarks>
    public static string DefaultDocument() =>
        """
        {
          "schemaVersion": 1,
          "application": {
            "picker": {
              "monitor": "primary",
              "favorites": [
                { "direction": "Up",    "target": "desk/desktop" },
                { "direction": "Right", "target": "desk/youtube" },
                { "direction": "Down",  "target": "desk/gaming" },
                { "direction": "Left",  "target": "desk/movie" }
              ]
            },
            "shortcuts": [
              { "target": "desk/desktop", "key": "D" },
              { "target": "desk/youtube", "key": "Y" },
              { "target": "desk/gaming", "key": "G" },
              { "target": "desk/movie", "key": "M" }
            ],
            "controller": { "enabled": true },
            "startup": {
              "startWithWindows": false,
              "behavior": "doNothing",
              "target": null
            }
          },
          "providers": {
            "cursor": {
              "positions": {
                "Center": { "xPercent": 50, "yPercent": 50 },
                "Corner": { "xFromRight": 2, "yFromBottom": 200 }
              }
            }
          },
          "spaces": [
            {
              "id": "desk",
              "name": "Desk",
              "resources": { "cursor": "Corner" },
              "activities": [
                { "id": "desktop", "name": "Desktop", "resources": { "theme": "Light", "cursor": "Center" } },
                { "id": "youtube", "name": "YouTube", "resources": { "theme": "Dark" } },
                { "id": "gaming",  "name": "Gaming",  "resources": { "theme": "Dark" } },
                { "id": "movie",   "name": "Movie",   "resources": { "theme": "Dark" } }
              ]
            }
          ]
        }

        """;

    /// <summary>
    /// Tolerant about shape, strict about meaning.
    /// </summary>
    /// <remarks>
    /// Comments are skipped and a trailing comma is allowed because this is a
    /// file a person edits by hand, and refusing it over a comma teaches
    /// nothing. What is NOT relaxed is any rule about content: an unknown
    /// resource key or an unusable value is still refused by name.
    /// </remarks>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class ConfigurationDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        /// <summary>
        /// Per-provider settings, keyed by the same provider key the resources use.
        /// </summary>
        /// <remarks>
        /// Left as raw JSON on purpose. The engine has no business knowing what
        /// a cursor position looks like, and a typed shape here would put every
        /// future provider's schema in one class that all of them share.
        /// </remarks>
        [JsonPropertyName("providers")]
        public Dictionary<string, JsonElement>? Providers { get; set; }

        /// <summary>
        /// The hierarchy, left as raw JSON for the same reason a mode's
        /// resources are: it is read by rules rather than by shape.
        /// </summary>
        [JsonPropertyName("spaces")]
        public JsonElement? Spaces { get; set; }

        /// <summary>
        /// The flat mode list from before Space -> Activity -> Source, kept only
        /// so its presence can be detected and refused by name -- see
        /// <see cref="Parse"/>.
        /// </summary>
        [JsonPropertyName("modes")]
        public JsonElement? Modes { get; set; }
    }
}

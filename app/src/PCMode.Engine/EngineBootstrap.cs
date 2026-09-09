namespace PCMode.Engine;

/// <summary>
/// Builds the engine a real machine runs, from a real profile folder.
/// </summary>
/// <remarks>
/// One place where the production provider list is written down. Tests build a
/// <see cref="ModeEngine"/> directly with their own providers; nothing outside
/// this file may substitute one into the shell, which is what keeps "the
/// application actually changes the machine" a property of the product rather
/// than of how it happened to be constructed.
/// </remarks>
public static class EngineBootstrap
{
    /// <summary>
    /// The providers the shipped application has.
    /// </summary>
    /// <remarks>
    /// A fresh list every call. These carry configuration, so handing the same
    /// instances to two engines built from two documents would leave whichever
    /// was loaded last speaking for both.
    ///
    /// The Do Not Disturb and case lighting providers are always in the list,
    /// whether or not this machine has either.
    ///
    /// TELEVISIONS AND APPLICATIONS COME FROM THE DOCUMENT
    ///
    /// Each configured television gets its own connection and its own four
    /// providers; applications likewise get one provider per configured
    /// application. Both sets have to be discovered from the document before that
    /// same document can be fully validated. Televisions are added first, so an
    /// application may not take a name one of them has already claimed.
    /// </remarks>
    /// <param name="root">
    /// Where this machine's data lives. The television's pairing key is kept
    /// there, so a caller pointed at a temporary folder gets a temporary
    /// credential store rather than the real profile's.
    /// </param>
    /// <param name="document">
    /// The configuration text, when there is one, so that the applications it
    /// defines each get a resource. Without it there are simply no application
    /// resources, which is the right answer for a caller that only wants to know
    /// what this build can do.
    /// </param>
    /// <param name="audio">
    /// Who owns the connection to the audio Host, when the caller has one to
    /// share. The providers built here are rebuilt whenever the configuration is
    /// re-read, and a Host launched by a discarded provider would be a process
    /// nothing could find in order to steer it -- see <see cref="AudioHostLink"/>.
    /// Callers that only want to validate a document pass nothing and get a link
    /// that never starts one.
    /// </param>
    public static IReadOnlyList<IModeResourceProvider> ProductionProviders(
        string? root = null,
        string? document = null,
        IAudioRuntime? audio = null
    )
    {
        string profileRoot = root ?? PCModePaths.DefaultRoot;

        List<IModeResourceProvider> providers =
        [
            new WindowsThemeProvider(),
            new DisplayProvider(),
            new CursorProvider(),
            new AudioProvider(new CoreAudioEndpoints(), audio),
            new DoNotDisturbProvider(new WindowsNotificationSetting()),
            new CaseRgbProvider(new OpenRgbSdkClient()),
        ];

        providers.AddRange(TelevisionProviders(document ?? string.Empty, profileRoot));

        if (document is not null)
        {
            providers.AddRange(DdcMonitorProviders(document, new WindowsDdcMonitors()));
            providers.AddRange(ApplicationProviders(document, new WindowsProcessHost(), providers));
        }

        return providers;
    }

    /// <summary>
    /// Two providers per configured DDC/CI monitor: brightness and input.
    /// </summary>
    /// <remarks>
    /// Unlike the television, a machine that configures none gets no DDC/CI
    /// resources at all. There is no historical document shape to stay
    /// compatible with -- see <see cref="DdcResourceKeys"/> -- so there is no
    /// reason to carry four keys nobody can use onto every machine that has
    /// never named a monitor.
    /// </remarks>
    public static IReadOnlyList<IModeResourceProvider> DdcMonitorProviders(string document, IDdcMonitors monitors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(monitors);

        IReadOnlyList<string> aliases = DdcMonitorCatalog.NamesIn(document);

        if (aliases.Count == 0)
        {
            return [];
        }

        var providers = new List<IModeResourceProvider>(aliases.Count * 2);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string alias in aliases)
        {
            if (!seen.Add(alias))
            {
                // The full parser gives the detailed configuration error. This
                // guard merely prevents constructing two connections for the
                // same alias before validation gets that chance.
                continue;
            }

            var connection = new DdcMonitorConnection(monitors, alias);
            providers.Add(new DdcBrightnessProvider(connection));
            providers.Add(new DdcInputProvider(connection));
        }

        return providers;
    }

    /// <summary>
    /// Four providers per television the document defines, and four for a machine
    /// that defines none.
    /// </summary>
    /// <remarks>
    /// A provider-construction pre-pass, not configuration validation.
    /// <see cref="LgTvCatalog.DeclarationsIn"/> extracts only names so the
    /// provider set exists before <see cref="ModeConfiguration.Parse"/> validates
    /// the real television section through each connection.
    ///
    /// ONE CONNECTION PER TELEVISION, AND NOT ONE MORE
    ///
    /// Every television gets a fresh network client, credential store wrapper and
    /// connection, and its four resources share it. That is what keeps one
    /// television's webOS operations serialized -- see <see cref="LgTvConnection"/>
    /// -- without one television's pairing prompt holding up another's. A second
    /// connection to the same address would defeat exactly that: two sockets, two
    /// registrations, and two callers able to talk over each other on a protocol
    /// that does not expect it.
    ///
    /// A MACHINE WITH NO TELEVISION STILL HAS THE RESOURCES
    ///
    /// Most machines have none, and their documents may still name tvPower --
    /// every document this build has ever written does. So a document declaring no
    /// television gets one set anyway, over a connection with nothing configured:
    /// the four keys exist, validate, report themselves
    /// <see cref="AvailabilityState.Disabled"/> and are left out of transitions
    /// that can manage without them. Nothing is ever sent, because a connection
    /// whose settings are not enabled does not open a socket at all.
    /// </remarks>
    public static IReadOnlyList<IModeResourceProvider> TelevisionProviders(string document, string root)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        LgTvDeclarations declared = document.Length == 0 ? LgTvDeclarations.None : LgTvCatalog.DeclarationsIn(document);

        if (declared.Names.Count == 0)
        {
            return [.. Set(Connect(root, televisionName: null, isDefault: true))];
        }

        var providers = new List<IModeResourceProvider>(declared.Names.Count * 4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in declared.Names)
        {
            if (!seen.Add(name))
            {
                // The full parser gives the detailed configuration error. This
                // guard merely prevents constructing two sets with the same keys
                // before validation gets that chance.
                continue;
            }

            providers.AddRange(Set(Connect(root, name, declared.IsDefault(name))));
        }

        return providers;

        static LgTvConnection Connect(string root, string? televisionName, bool isDefault) =>
            new(
                new WebSocketLgTvNetwork(),
                new ProtectedLgTvCredentials(root),
                televisionName: televisionName,
                isDefault: isDefault
            );

        static IEnumerable<IModeResourceProvider> Set(LgTvConnection television) =>
            [
                new TvPowerProvider(television),
                new TvInputProvider(television),
                new TvSignalProvider(television),
                new TvProfileProvider(television),
            ];
    }

    /// <summary>
    /// One provider per application the document defines.
    /// </summary>
    /// <remarks>
    /// Deliberately not private: a test that wants an engine with applications in
    /// it needs the same list the shell gets, built by the same rules, from its
    /// own document and its own process host. Building it a second way in a test
    /// would mean testing something the application does not do.
    ///
    /// Only the names are read here. Everything about an application is checked
    /// when the document is validated a moment later, by the provider itself, so
    /// there is still one set of rules about what a malformed entry is.
    /// </remarks>
    /// <param name="taken">
    /// The providers that already exist. An application may not be called what one
    /// of them is called: a resource key is how a mode names the thing that
    /// applies it, and two claimants would mean a mode that sets the display
    /// silently starting a program instead.
    /// </param>
    public static IReadOnlyList<IModeResourceProvider> ApplicationProviders(
        string document,
        IProcessHost host,
        IReadOnlyList<IModeResourceProvider> taken
    )
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(taken);

        IReadOnlyList<string> names = ApplicationCatalog.NamesIn(document);

        if (names.Count == 0)
        {
            return [];
        }

        var reserved = new Dictionary<string, IModeResourceProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (IModeResourceProvider provider in taken)
        {
            reserved[provider.Key] = provider;
        }

        var applications = new List<IModeResourceProvider>(names.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in names)
        {
            if (reserved.TryGetValue(name, out IModeResourceProvider? owner))
            {
                throw new ModeConfigurationException(
                    $"'{ApplicationCatalog.SettingsPath}' calls an application '{name}', which is already "
                        + $"{owner.DisplayName}. Give it another name: a mode setting '{name}' could otherwise "
                        + "mean either of them."
                );
            }

            if (!seen.Add(name))
            {
                // Refused here as well as when the entries are read, because this
                // pass is what decides how many resources there are. Two providers
                // with one key would leave the engine's own lookup keeping
                // whichever it saw last.
                throw new ModeConfigurationException(
                    $"'{ApplicationCatalog.SettingsPath}' defines '{name}' more than once."
                );
            }

            // A provisional definition, replaced by the checked one the moment
            // the document is validated. Nothing reads it before then.
            applications.Add(
                new ApplicationProvider(
                    new ApplicationDefinition
                    {
                        Name = name,
                        Observation = ApplicationObservation.ForProcess(name),
                        Start = new LaunchCommand { Executable = name },
                    },
                    host
                )
            );
        }

        return applications;
    }

    /// <summary>
    /// Loads the configuration under <paramref name="root"/>, seeding the
    /// defaults on first run, and returns an engine ready to use.
    /// </summary>
    /// <remarks>
    /// Read, then built, then validated. The middle step is there because some
    /// providers do not exist until the document says so -- four per configured
    /// television and one per configured application -- and the document cannot be
    /// checked until they do. It is
    /// still one document and one validation: the pre-pass learns names and
    /// refuses nothing.
    /// </remarks>
    /// <exception cref="ModeConfigurationException">
    /// The configuration could not be read or is not one this build can apply.
    /// The caller shows the message: a shell that started with a broken
    /// configuration would list no modes and explain nothing.
    /// </exception>
    public static ModeEngine Create(string? root = null, IAudioRuntime? audio = null)
    {
        string folder = root ?? PCModePaths.DefaultRoot;

        (string json, string path) = ModeConfiguration.ReadOrCreate(folder);

        json = ConvertAudioTargets(folder, path, json);

        IReadOnlyList<IModeResourceProvider> providers = ProductionProviders(folder, json, audio);

        ModeConfiguration configuration = ModeConfiguration.Parse(json, providers, path);
        var state = new ModeStateStore(PCModePaths.StateFile(folder));

        return new ModeEngine(configuration, state, providers);
    }

    /// <summary>
    /// Converts a document that still has a Party group, once, before anything
    /// tries to read it.
    /// </summary>
    /// <remarks>
    /// BACKED UP, VALIDATED, AND ONLY THEN WRITTEN
    ///
    /// The converted text is built into providers and parsed exactly as a real
    /// startup would -- see <see cref="AudioMigration"/> for what the conversion
    /// preserves -- before a byte of it reaches disk. A conversion that does not
    /// produce a document this build can load is abandoned, and the original is
    /// returned untouched: the loader then refuses it by name, which is a far
    /// better outcome than a machine left holding a file it wrote itself and
    /// cannot read.
    ///
    /// The original is copied beside it first, so a conversion that turns out to
    /// be wrong in some way nothing checked is still recoverable by hand.
    ///
    /// A DOCUMENT THAT NEEDS NOTHING COSTS NOTHING
    ///
    /// Almost every start is that one. The conversion is a pure function of the
    /// text and reports that it changed nothing, and this returns immediately
    /// without touching the file.
    ///
    /// INTERNAL RATHER THAN PRIVATE, FOR ONE CALLER
    ///
    /// <c>AudioTargetHardwareTest</c> reads a real machine's configuration but
    /// deliberately builds its own <see cref="ModeEngine"/> over a temporary
    /// state file rather than calling <see cref="Create"/> -- see that test for
    /// why. It still has to see the same migrated document a real start would,
    /// so it calls this directly instead of re-implementing conversion, or
    /// skipping it and failing on a document <see cref="Create"/> would have
    /// handled.
    /// </remarks>
    internal static string ConvertAudioTargets(string root, string path, string json)
    {
        if (!AudioMigration.TryConvert(json, out string migrated, out _))
        {
            return json;
        }

        try
        {
            _ = ModeConfiguration.Parse(migrated, ProductionProviders(root, migrated), path);
        }
        catch (ModeConfigurationException)
        {
            return json;
        }

        try
        {
            File.WriteAllText(Path.Combine(root, AudioMigration.BackupFileName), json);
            File.WriteAllText(path, migrated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The conversion is a pure function of the text, so a start that
            // could not write it simply runs on the converted document in memory
            // and tries again next time.
        }

        return migrated;
    }
}

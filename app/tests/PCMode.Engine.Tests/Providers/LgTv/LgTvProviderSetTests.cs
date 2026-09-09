using System.Text.Json.Nodes;

namespace PCMode.Engine.Tests;

/// <summary>
/// Which television resources a document produces, and which television each of
/// them commands.
/// </summary>
/// <remarks>
/// The rules here are the ones that cannot be checked by looking at a single
/// class. A television is a connection plus four resources, and the two ways of
/// getting that wrong are invisible from inside any of them: building two sets
/// for one television -- two sockets to one address, neither serializing against
/// the other -- and building one set whose four resources do not all mean the
/// same device.
/// </remarks>
public sealed class LgTvProviderSetTests
{
    // ---- how many, and under what names -------------------------------------

    [Fact]
    public void One_television_is_one_connection_and_the_four_historical_keys()
    {
        // The ordinary machine, and every document this build has ever written.
        // A second set here would be a second socket to the same television.
        IReadOnlyList<IModeResourceProvider> providers = Providers(Section(("Television", "192.0.2.31")));

        Assert.Equal(["tvPower", "tvInput", "tvSignal", "tvProfile"], providers.Select(provider => provider.Key));

        Assert.Single(providers.OfType<TvPowerProvider>());
        Assert.Single(Connections(providers));
    }

    [Fact]
    public void The_single_television_body_written_before_names_existed_still_reads()
    {
        var body = new JsonObject
        {
            ["host"] = "192.0.2.31",
            ["macAddress"] = "00:00:5E:00:53:31",
            ["inputs"] = new JsonObject { ["PC"] = "HDMI_4" },
        };

        IReadOnlyList<IModeResourceProvider> providers = Providers(body);

        TvPowerProvider power = Assert.Single(providers.OfType<TvPowerProvider>());

        Assert.Equal("tvPower", power.Key);
        Assert.Equal("Television", power.TelevisionName);
        Assert.Equal("192.0.2.31", power.Settings.Host);
        Assert.Equal("HDMI_4", power.Settings.InputIdFor("PC"));
    }

    [Fact]
    public void Two_televisions_are_two_connections_and_only_the_default_keeps_the_historical_keys()
    {
        IReadOnlyList<IModeResourceProvider> providers = Providers(
            Section(("Living Room", "192.0.2.31"), ("Bedroom", "192.0.2.32"), defaultName: "Living Room")
        );

        Assert.Equal(
            [
                "tv.Bedroom.input",
                "tv.Bedroom.power",
                "tv.Bedroom.profile",
                "tv.Bedroom.signal",
                "tvInput",
                "tvPower",
                "tvProfile",
                "tvSignal",
            ],
            providers.Select(provider => provider.Key).Order(StringComparer.OrdinalIgnoreCase)
        );

        Assert.Equal(2, Connections(providers).Count);
    }

    [Fact]
    public void The_default_is_the_only_thing_that_decides_which_television_the_historical_keys_mean()
    {
        IReadOnlyList<IModeResourceProvider> providers = Providers(
            Section(("Living Room", "192.0.2.31"), ("Bedroom", "192.0.2.32"), defaultName: "Bedroom")
        );

        TvPowerProvider historical = Historical(providers);

        Assert.Equal("Bedroom", historical.TelevisionName);
        Assert.Equal("192.0.2.32", historical.Settings.Host);

        // And the other one is still perfectly usable, by its own name.
        TvPowerProvider named = Named(providers, "tv.Living Room.power");

        Assert.Equal("Living Room", named.TelevisionName);
        Assert.Equal("192.0.2.31", named.Settings.Host);
    }

    [Fact]
    public void With_no_default_named_it_is_the_first_in_name_order_rather_than_in_file_order()
    {
        // So that adding a television never silently re-points existing modes at
        // a different device just because of where it landed in the file.
        IReadOnlyList<IModeResourceProvider> providers = Providers(
            Section(("Living Room", "192.0.2.31"), ("Bedroom", "192.0.2.32"))
        );

        Assert.Equal("Bedroom", Historical(providers).TelevisionName);
    }

    // ---- the machine that has no television ---------------------------------

    [Fact]
    public void A_machine_with_no_television_still_has_the_four_resources_and_reports_them_disabled()
    {
        // Absent is not broken. A document that names tvPower on a machine with
        // no television must still load, and the resource must be settled enough
        // to be left out rather than refused.
        IReadOnlyList<IModeResourceProvider> providers = Providers(section: null);

        TvPowerProvider power = Assert.Single(providers.OfType<TvPowerProvider>());

        Assert.Equal("tvPower", power.Key);
        Assert.False(power.Settings.Enabled);

        ResourceAvailability availability = power.AvailabilityOf(TvPowerProvider.Off);

        Assert.Equal(AvailabilityState.Disabled, availability.State);
        Assert.Contains("no television is configured", availability.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_named_television_that_is_switched_off_says_which_one_it_is()
    {
        // The message a single-television build could get away with -- "no
        // television is configured" -- is wrong the moment there are two.
        var bedroom = new JsonObject
        {
            ["enabled"] = false,
            ["host"] = "192.0.2.32",
            ["macAddress"] = "00:00:5E:00:53:32",
        };

        var section = new JsonObject { ["televisions"] = new JsonObject { ["Bedroom"] = bedroom } };

        IReadOnlyList<IModeResourceProvider> providers = Providers(section);

        ResourceAvailability availability = Historical(providers).AvailabilityOf(TvPowerProvider.Off);

        Assert.Equal(AvailabilityState.Disabled, availability.State);
        Assert.Contains("Bedroom", availability.Explain(), StringComparison.Ordinal);
    }

    // ---- what one television's resources depend on ---------------------------

    [Fact]
    public void Every_dependency_stays_inside_the_television_it_belongs_to()
    {
        // The failure this exists to catch: a mode switching the bedroom's input
        // being satisfied by the living room being on.
        JsonObject section = Section(
            ("Living Room", "192.0.2.31"),
            ("Bedroom", "192.0.2.32"),
            defaultName: "Living Room"
        );
        AddInputsAndProfile(section, "Bedroom");

        IReadOnlyList<IModeResourceProvider> providers = Providers(section);

        var input = (IDependentProvider)Named(providers, "tv.Bedroom.input", typeof(TvInputProvider));
        var signal = (IDependentProvider)Named(providers, "tv.Bedroom.signal", typeof(TvSignalProvider));
        var picture = (IDependentProvider)Named(providers, "tv.Bedroom.profile", typeof(TvProfileProvider));

        Assert.Equal("tv.Bedroom.power", Assert.Single(input.RequirementsFor("PC")).Key);

        Assert.Equal(
            ["tv.Bedroom.power", "tv.Bedroom.input"],
            signal.RequirementsFor("Movie").Select(requirement => requirement.Key)
        );

        Assert.Equal("tv.Bedroom.signal", Assert.Single(picture.RequirementsFor("Movie")).Key);
    }

    [Fact]
    public void The_default_televisions_dependencies_use_the_historical_keys_throughout()
    {
        // Mixing the two spellings would put one television in a plan twice.
        JsonObject section = Section(
            ("Living Room", "192.0.2.31"),
            ("Bedroom", "192.0.2.32"),
            defaultName: "Living Room"
        );
        AddInputsAndProfile(section, "Living Room");

        IReadOnlyList<IModeResourceProvider> providers = Providers(section);

        var input = (IDependentProvider)Historical(providers, typeof(TvInputProvider));

        Assert.Equal("tvPower", Assert.Single(input.RequirementsFor("PC")).Key);
    }

    // ---- what a person is shown ----------------------------------------------

    [Fact]
    public void A_step_names_the_television_even_when_its_key_is_the_historical_one()
    {
        // Retry, repair and health all read the display name. "Television power"
        // is not an answer to which device somebody has to go and look at.
        IReadOnlyList<IModeResourceProvider> providers = Providers(
            Section(("Living Room", "192.0.2.31"), defaultName: "Living Room")
        );

        Assert.Equal("Living Room television power", Historical(providers).DisplayName);
    }

    [Theory]
    [InlineData("tvPower", true)]
    [InlineData("tvProfile", true)]
    [InlineData("tv.Bedroom.input", true)]
    [InlineData("display", false)]
    [InlineData("Steam", false)]
    public void A_television_resource_is_recognised_however_it_is_spelled(string key, bool television) =>
        Assert.Equal(television, LgTvResourceKeys.IsTelevisionKey(key));

    // ---- the harness -----------------------------------------------------------

    private static TvPowerProvider Historical(IReadOnlyList<IModeResourceProvider> providers) =>
        (TvPowerProvider)Historical(providers, typeof(TvPowerProvider));

    private static IModeResourceProvider Historical(IReadOnlyList<IModeResourceProvider> providers, Type kind) =>
        providers.Single(provider =>
            provider.GetType() == kind && !provider.Key.StartsWith("tv.", StringComparison.OrdinalIgnoreCase)
        );

    private static TvPowerProvider Named(IReadOnlyList<IModeResourceProvider> providers, string key) =>
        (TvPowerProvider)Named(providers, key, typeof(TvPowerProvider));

    private static IModeResourceProvider Named(IReadOnlyList<IModeResourceProvider> providers, string key, Type kind)
    {
        IModeResourceProvider found = providers.Single(provider =>
            string.Equals(provider.Key, key, StringComparison.OrdinalIgnoreCase)
        );

        Assert.IsType(kind, found);
        return found;
    }

    /// <summary>
    /// The distinct connections behind a provider list.
    /// </summary>
    /// <remarks>
    /// By reference, because that is the actual claim: one television is one
    /// object, and it is the object that holds the gate everything about that
    /// television is serialized through.
    /// </remarks>
    private static IReadOnlyList<LgTvConnection> Connections(IReadOnlyList<IModeResourceProvider> providers) =>
        [
            .. providers
                .OfType<TvPowerProvider>()
                .Select(power => power.Connection)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Cast<LgTvConnection>(),
        ];

    /// <summary>Builds a <c>providers.lgTv</c> body naming one or more televisions.</summary>
    private static JsonObject Section(params (string Name, string Host)[] televisions)
    {
        var map = new JsonObject();

        foreach ((string name, string host) in televisions)
        {
            map[name] = new JsonObject { ["host"] = host, ["macAddress"] = "00:00:5E:00:53:31" };
        }

        return new JsonObject { ["televisions"] = map };
    }

    private static JsonObject Section((string Name, string Host)[] televisions, string? defaultName)
    {
        JsonObject section = Section(televisions);

        if (defaultName is not null)
        {
            section["default"] = defaultName;
        }

        return section;
    }

    private static JsonObject Section((string Name, string Host) television, string? defaultName = null) =>
        Section([television], defaultName);

    private static JsonObject Section(
        (string Name, string Host) first,
        (string Name, string Host) second,
        string? defaultName = null
    ) => Section([first, second], defaultName);

    private static void AddInputsAndProfile(JsonObject section, string televisionName)
    {
        var televisions = (JsonObject)section["televisions"]!;
        var television = (JsonObject)televisions[televisionName]!;

        television["inputs"] = new JsonObject { ["PC"] = "HDMI_4" };

        television["profiles"] = new JsonObject
        {
            ["Movie"] = new JsonObject
            {
                ["pictureMode"] = "filmMaker",
                ["signal"] = new JsonObject { ["gamemode_hdmi{n}"] = "off" },
            },
        };
    }

    /// <summary>
    /// The providers the shipped application builds, configured by the pipeline
    /// it configures them with.
    /// </summary>
    /// <remarks>
    /// Deliberately the real <see cref="EngineBootstrap.TelevisionProviders"/> and
    /// the real <see cref="ModeConfiguration.Parse"/>. What is being checked is
    /// how many resources a document produces and what each one is wired to, and
    /// building that a second way in a test would be checking something the
    /// application does not do.
    /// </remarks>
    private static IReadOnlyList<IModeResourceProvider> Providers(JsonObject? section)
    {
        var document = new JsonObject { ["schemaVersion"] = 1 };

        if (section is not null)
        {
            document["providers"] = new JsonObject { ["lgTv"] = section };
        }

        document["spaces"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "space",
                ["name"] = "Space",
                ["activities"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "desktop",
                        ["name"] = "Desktop",
                        ["resources"] = new JsonObject(),
                    },
                },
            },
        };

        string json = document.ToJsonString();

        using var root = new TemporaryRoot(json);

        IReadOnlyList<IModeResourceProvider> providers = EngineBootstrap.TelevisionProviders(json, root.Path);

        ModeConfiguration.Parse(json, providers);

        return providers;
    }
}

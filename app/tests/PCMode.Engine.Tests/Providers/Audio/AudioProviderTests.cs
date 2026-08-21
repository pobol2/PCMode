using System.Text.Json;

namespace PCMode.Engine.Tests;

/// <summary>
/// What the audio provider will and will not do, with a made-up sound card.
/// </summary>
public sealed class AudioProviderTests
{
    private const string Speakers = "Desk speakers";
    private const string Television = "LG TV (NVIDIA High Definition Audio)";

    // ---- configuration ------------------------------------------------------

    [Fact]
    public void Stays_out_of_the_way_when_nothing_asks_for_audio()
    {
        // A machine with one pair of speakers has no reason to configure this,
        // and a provider that made itself mandatory would stop such a machine
        // from starting at all.
        ModeConfiguration configuration = Parse(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
              {"id":"movie","name":"Movie","resources":{"theme":"Dark"}}]}]}
            """,
            new AudioProvider(new FakeAudioEndpoints()));

        Assert.NotNull(configuration.Find("Movie"));
    }

    [Fact]
    public void Refuses_a_mode_that_asks_for_audio_when_none_is_configured()
    {
        // The other half of the rule above. Unconfigured is fine until a mode
        // depends on it, and then it is refused by name rather than at the
        // moment the sound was supposed to move.
        ModeConfigurationException failure = Assert.Throws<ModeConfigurationException>(() => Parse(
            """
            {"schemaVersion":1,"spaces":[{"id":"space","name":"Space","activities":[
              {"id":"movie","name":"Movie","resources":{"audio":"TV"}}]}]}
            """,
            new AudioProvider(new FakeAudioEndpoints())));

        Assert.Contains(AudioProvider.SettingsPath, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"devices":{"TV":""}}""")]
    [InlineData("""{"devices":{"TV":"   "}}""")]
    [InlineData("""{"devices":{"TV":42}}""")]
    [InlineData("""{"devices":{"TV":{"name":"LG"}}}""")]
    public void Refuses_a_device_that_maps_to_nothing_usable(string settings)
    {
        // An entry that exists but says nothing is worse than a missing one:
        // every mode using the name still validates, and the failure waits until
        // a transition is already under way.
        string? problem = Configure(new AudioProvider(new FakeAudioEndpoints()), settings);

        Assert.NotNull(problem);
        Assert.Contains("TV", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"speakers\"")]
    [InlineData("""{"device":{"TV":"LG"}}""")]
    [InlineData("""{"devices":[]}""")]
    public void Refuses_settings_it_does_not_understand(string settings) =>
        Assert.NotNull(Configure(new AudioProvider(new FakeAudioEndpoints()), settings));

    [Fact]
    public void Refuses_two_names_for_the_same_device()
    {
        // Readback would have to pick one, and whichever it picked would make
        // the other name report the wrong current device for good.
        string? problem = Configure(
            new AudioProvider(new FakeAudioEndpoints()),
            $$$"""{"devices":{"TV":"{{{Television}}}","Cinema":"{{{Television}}}"}}""");

        Assert.NotNull(problem);
        Assert.Contains("TV", problem, StringComparison.Ordinal);
        Assert.Contains("Cinema", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_two_devices_that_differ_only_in_case()
    {
        // A mode looks one up case-insensitively, so one of these could never be
        // selected and nothing would say which.
        string? problem = Configure(
            new AudioProvider(new FakeAudioEndpoints()),
            $$$"""{"devices":{"TV":"{{{Television}}}","tv":"{{{Speakers}}}"}}""");

        Assert.NotNull(problem);
        Assert.Contains("more than once", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_the_devices_it_has_when_a_mode_asks_for_another()
    {
        AudioProvider provider = Provider(new FakeAudioEndpoints());

        Assert.False(provider.IsValidValue("Kitchen", out string? problem));

        Assert.NotNull(problem);
        Assert.Contains("Headphones", problem, StringComparison.Ordinal);
        Assert.Contains("TV", problem, StringComparison.Ordinal);
        Assert.Equal(["Headphones", "TV"], provider.DeviceNames);
    }

    [Fact]
    public void Matches_the_device_name_exactly_apart_from_what_a_copy_picks_up()
    {
        // Surrounding whitespace is removed because that is what pasting a name
        // out of Sound settings collects. Nothing else about it is interpreted:
        // it is matched against Windows' own string.
        var endpoints = new FakeAudioEndpoints(Speakers);
        var provider = new AudioProvider(endpoints);

        Assert.Null(Configure(provider, $$$"""{"devices":{"Desk":"  {{{Speakers}}}  "}}"""));

        provider.Apply("Desk", CancellationToken.None);

        Assert.Equal([endpoints.IdOf(0)], endpoints.MadeDefault);
    }

    // ---- discovery ----------------------------------------------------------

    [Fact]
    public void Lists_the_live_devices_and_marks_the_current_one()
    {
        var endpoints = new FakeAudioEndpoints(Speakers, Television);
        endpoints.Default = endpoints.IdOf(1);

        AudioSnapshot snapshot = Provider(endpoints).Discover();

        Assert.True(snapshot.Available);
        Assert.Null(snapshot.Problem);
        Assert.Equal([Speakers, Television], snapshot.Endpoints.Select(endpoint => endpoint.Name));
        Assert.Equal(Television, Assert.Single(snapshot.Endpoints, endpoint => endpoint.IsDefault).Name);
    }

    [Fact]
    public void Says_which_configured_devices_can_actually_be_selected()
    {
        // The three answers a settings screen needs, and the reason the provider
        // gives them rather than a UI working them out: these are the same rules
        // Apply refuses by, so they cannot drift apart.
        var endpoints = new FakeAudioEndpoints(Speakers, Speakers);

        AudioProvider provider = Provider(endpoints);

        AudioSnapshot snapshot = provider.Discover();

        Assert.True(snapshot.Configured);

        AudioDeviceMapping headphones = Mapping(snapshot, "Headphones");
        AudioDeviceMapping television = Mapping(snapshot, "TV");

        // Two active devices share the name, so nothing can tell them apart.
        Assert.Equal(AudioMappingState.Ambiguous, headphones.State);
        Assert.Equal(2, headphones.Matches);

        // The television is off, which is the ordinary state of an HDMI
        // endpoint rather than a fault in the configuration.
        Assert.Equal(AudioMappingState.Missing, television.State);
        Assert.Equal(0, television.Matches);

        // And with the set awake, the same mapping is simply ready.
        endpoints.Add(Television);

        Assert.Equal(AudioMappingState.Ready, Mapping(provider.Discover(), "TV").State);
    }

    [Fact]
    public void Reports_audio_as_unavailable_rather_than_as_devices_that_are_missing()
    {
        // "Core Audio did not answer" and "that device is not plugged in" call
        // for different things from a user, and reporting the first as the
        // second would be a claim about the machine that nothing checked.
        var endpoints = new FakeAudioEndpoints(Speakers)
        {
            Fails = new InvalidOperationException("Windows Core Audio is not available on this machine."),
        };

        AudioSnapshot snapshot = Provider(endpoints).Discover();

        Assert.False(snapshot.Available);
        Assert.Contains("not available", snapshot.Problem!, StringComparison.Ordinal);
        Assert.Empty(snapshot.Endpoints);
        Assert.All(snapshot.Devices, device => Assert.Equal(AudioMappingState.Unknown, device.State));
    }

    // ---- readback -----------------------------------------------------------

    [Fact]
    public void Reads_the_logical_name_of_the_current_device()
    {
        var endpoints = new FakeAudioEndpoints(Speakers, Television);
        endpoints.Default = endpoints.IdOf(1);

        Assert.Equal("TV", Provider(endpoints).Read());
    }

    [Fact]
    public void Reads_nothing_when_it_cannot_name_the_current_device()
    {
        // Three different situations, folded into one answer on purpose: to the
        // engine null means "apply it anyway", which is correct for all of them.
        var nothingDefault = new FakeAudioEndpoints(Speakers, Television);
        Assert.Null(Provider(nothingDefault).Read());

        // A device the configuration has no name for.
        var unnamed = new FakeAudioEndpoints(Speakers, Television, "Webcam microphone monitor");
        unnamed.Default = unnamed.IdOf(2);
        Assert.Null(Provider(unnamed).Read());

        // And Core Audio not answering at all, which must not throw out of a
        // read: the transition can still go ahead.
        var broken = new FakeAudioEndpoints(Speakers) { Fails = new InvalidOperationException("no") };
        Assert.Null(Provider(broken).Read());
    }

    // ---- switching ----------------------------------------------------------

    [Fact]
    public async Task Points_the_default_at_the_configured_device()
    {
        using var root = new TemporaryRoot();

        var endpoints = new FakeAudioEndpoints(Speakers, Television);
        endpoints.Default = endpoints.IdOf(0);

        TransitionOutcome outcome = await Engine(root, endpoints).ApplyAsync("Movie");

        Assert.True(outcome.Succeeded);
        Assert.Equal([endpoints.IdOf(1)], endpoints.MadeDefault);

        TransitionStep step = Assert.Single(
            outcome.Steps,
            reported => reported.Resource == AudioProvider.ResourceKey);

        Assert.Equal(TransitionStepStatus.Applied, step.Status);
        Assert.Equal("TV", step.Target);
        Assert.Equal("Headphones", step.Current);
        Assert.Equal("Audio output", step.ResourceName);

        // And only then is the mode recorded.
        Assert.True(outcome.StateRecorded);
        Assert.True(File.Exists(root.StateFile));
    }

    [Fact]
    public async Task Leaves_the_default_alone_when_it_is_already_right()
    {
        // Unlike the pointer, this one has a current value worth asking about,
        // so re-applying a mode does not command the sound card again.
        using var root = new TemporaryRoot();

        var endpoints = new FakeAudioEndpoints(Speakers, Television);
        endpoints.Default = endpoints.IdOf(1);

        TransitionOutcome outcome = await Engine(root, endpoints).ApplyAsync("Movie");

        Assert.True(outcome.Succeeded);
        Assert.Empty(endpoints.MadeDefault);

        Assert.Equal(
            TransitionStepStatus.AlreadyCorrect,
            Assert.Single(outcome.Steps, reported => reported.Resource == AudioProvider.ResourceKey).Status);
    }

    [Fact]
    public async Task Fails_the_transition_when_the_device_is_not_there()
    {
        // The television is off, so its HDMI endpoint does not exist. Validation
        // could not have caught this: it is a fact about the room, not the file.
        using var root = new TemporaryRoot();

        var endpoints = new FakeAudioEndpoints(Speakers);

        TransitionOutcome outcome = await Engine(root, endpoints).ApplyAsync("Movie");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Contains(Television, outcome.Message, StringComparison.Ordinal);

        // The names of what IS there, so a typo and a switched-off set are told
        // apart without running anything else.
        Assert.Contains(Speakers, outcome.Message, StringComparison.Ordinal);

        // Nothing was commanded, and nothing was recorded.
        Assert.Empty(endpoints.MadeDefault);
        Assert.False(outcome.StateRecorded);
        Assert.False(File.Exists(root.StateFile));
    }

    [Fact]
    public async Task Refuses_to_guess_when_two_devices_share_a_name()
    {
        // Windows does not enforce unique friendly names. Picking one would send
        // sound wherever enumeration happened to land, which looks like a
        // working feature until the day it lands the other way.
        using var root = new TemporaryRoot();

        var endpoints = new FakeAudioEndpoints(Speakers, Television).Add(Television);

        TransitionOutcome outcome = await Engine(root, endpoints).ApplyAsync("Movie");

        Assert.Equal(TransitionStatus.Failed, outcome.Status);
        Assert.Contains("no way to tell which one was meant", outcome.Message, StringComparison.Ordinal);

        Assert.Empty(endpoints.MadeDefault);
        Assert.False(File.Exists(root.StateFile));
    }

    // ---- helpers ------------------------------------------------------------

    private static readonly string Settings =
        $$"""
        { "devices": { "Headphones": "{{Speakers}}", "TV": "{{Television}}" } }
        """;

    private static readonly string Document =
        $$"""
        {
          "schemaVersion": 1,
          "providers": {
            "audio": { "devices": { "Headphones": "{{Speakers}}", "TV": "{{Television}}" } }
          },
          "spaces": [
            { "id": "space", "name": "Space", "activities": [
              { "id": "movie", "name": "Movie", "resources": { "theme": "Dark", "audio": "TV" } }
            ] }
          ]
        }
        """;

    private static AudioDeviceMapping Mapping(AudioSnapshot snapshot, string logical) =>
        Assert.Single(snapshot.Devices, device => device.LogicalName == logical);

    private static ModeEngine Engine(TemporaryRoot root, FakeAudioEndpoints endpoints)
    {
        root.Write(Document);

        IReadOnlyList<IModeResourceProvider> providers =
            [new FakeProvider("theme", "Dark"), new AudioProvider(endpoints)];

        // A fake clock, because this provider retries six times over twelve
        // seconds when a device is missing, and these tests are about what it
        // concludes rather than how patiently it gets there.
        return new ModeEngine(
            ModeConfiguration.LoadOrCreate(root.Path, providers),
            new ModeStateStore(root.StateFile),
            providers,
            new FakeTime());
    }

    private static ModeConfiguration Parse(string json, AudioProvider audio) =>
        ModeConfiguration.Parse(json, [new FakeProvider("theme") { Allowed = ["Light", "Dark"] }, audio]);

    // ---- a device that does not exist until something else has happened -----

    [Fact]
    public void Takes_a_device_that_says_what_it_needs()
    {
        // The long form, beside the short one it has to keep working with. An
        // endpoint carried over a display cable does not exist until that display
        // does, and which layout that is depends on somebody's cabling.
        var provider = new AudioProvider(new FakeAudioEndpoints());

        Assert.Null(Configure(
            provider,
            """
            {"devices":{
              "Desk":"Speakers (Realtek)",
              "TV":{"device":"Digital Audio (HDMI)","requires":{"display":"TVOnly"}}}}
            """));

        Assert.Equal(["Desk", "TV"], provider.DeviceNames);

        // Only the device that asked for it, and nothing for the one that did not.
        ResourceRequirement needed = Assert.Single(provider.RequirementsFor("TV"));

        Assert.Equal("display", needed.Key);
        Assert.Equal("TVOnly", needed.Target);
        Assert.Empty(provider.RequirementsFor("Desk"));
    }

    [Theory]
    [InlineData("""{"devices":{"TV":{"requires":{"display":"TVOnly"}}}}""", "what it is")]
    [InlineData("""{"devices":{"TV":{"device":"HDMI","colour":"red"}}}""", "colour")]
    [InlineData("""{"devices":{"TV":{"device":"HDMI","requires":"display"}}}""", "requires")]
    [InlineData("""{"devices":{"TV":{"device":"HDMI","requires":{"display":""}}}}""", "display")]
    [InlineData("""{"devices":{"TV":{"device":"HDMI","requires":{"audio":"Desk"}}}}""", "what it sets")]
    public void Refuses_a_device_whose_requirements_do_not_say_anything(string settings, string mentions)
    {
        // Strict for the same reason the rest of the document is: a requirement
        // that was silently dropped is one somebody believes they have written,
        // and they find out when the sound comes out of the wrong thing.
        Assert.Contains(
            mentions,
            Configure(new AudioProvider(new FakeAudioEndpoints()), settings)!,
            StringComparison.Ordinal);
    }

    private static AudioProvider Provider(FakeAudioEndpoints endpoints)
    {
        var provider = new AudioProvider(endpoints);

        Assert.Null(Configure(provider, Settings));

        return provider;
    }

    /// <summary>The refusal, or null when the settings were accepted.</summary>
    private static string? Configure(AudioProvider provider, string settings)
    {
        using JsonDocument document = JsonDocument.Parse(settings);

        bool accepted = provider.TryConfigure(document.RootElement, out string? problem);

        Assert.Equal(accepted, problem is null);

        return problem;
    }
}

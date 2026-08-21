using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCMode.Engine;

/// <summary>
/// What became of an attempt to talk to the television.
/// </summary>
/// <remarks>
/// Three outcomes, not two, because "it said no" and "it said nothing" and "it was
/// never asked" are three different facts and the callers want different things
/// from each. Collapsing the last two into a single null is what made a television
/// nobody has ever paired with indistinguishable from one that is switched off --
/// and only one of those is something a mode can act on.
/// </remarks>
internal enum LgTvReach
{
    /// <summary>Nothing was attempted: none is configured, or none may be woken.</summary>
    NotAttempted,

    /// <summary>It was reached for, and did not answer at all.</summary>
    Unreachable,

    /// <summary>It answered.</summary>
    Answered,
}

/// <summary>An answer from the television, and whether there was one.</summary>
/// <remarks>
/// <see cref="Value"/> is meaningful only when <see cref="Reach"/> is
/// <see cref="LgTvReach.Answered"/>, and is the type's default otherwise. Even
/// then it may be null: a television can answer a question with a value that is
/// none of the ones a mode can name.
/// </remarks>
internal readonly record struct Reached<T>(LgTvReach Reach, T? Value);

/// <summary>
/// Where the television's power is, as far as anything outside it can tell.
/// </summary>
/// <remarks>
/// WHY UNREACHABLE IS NOT SPELLED "OFF" HERE
///
/// It is very nearly the same thing -- the socket is served by webOS, which runs
/// only while the television is on, so a television that does not answer is almost
/// always one that is off. But "almost always" is a judgement, and a judgement
/// belongs where the target is known. A misplaced network cable and a television
/// switched off at the wall look identical from here, and a layer whose job is to
/// carry messages should say which of those it observed rather than which of them
/// it thinks it means.
///
/// <see cref="TvPowerProvider"/> is where the judgement is made, and where it is
/// written down.
/// </remarks>
internal enum LgTvPower
{
    /// <summary>
    /// Nobody can say. Never paired with, or part way through changing state.
    /// </summary>
    Unknown,

    /// <summary>It answered, and said it is on.</summary>
    On,

    /// <summary>It answered, and said it is not.</summary>
    Off,

    /// <summary>It was reached for and did not answer at all.</summary>
    Unreachable,
}

/// <summary>
/// The one conversation with the television, shared by everything that needs it.
/// </summary>
/// <remarks>
/// WHY THE PROVIDERS SHARE THIS
///
/// Switching a television on and choosing its input are two resources, but there
/// is one television, one address, one pairing key and one socket at a time. Two
/// providers each holding their own would pair twice, store two keys, and be able
/// to talk over each other. They hold this instead, and everything about reaching
/// the television is here.
///
/// WHAT MAY HAPPEN WHEN, AND WHY IT IS A PARAMETER
///
/// Three things must never happen except during a transition somebody actually
/// asked for: waking the television, opening a pairing prompt on its screen, and
/// writing a credential. Reading and dry runs happen constantly -- every poll of
/// every convergence loop reads -- and a dry run's entire promise is that it
/// changes nothing.
///
/// So permission is passed in rather than inferred. <see cref="ReadPowerAsync"/>
/// and <see cref="ReadInputAsync"/> never pass it; the apply paths do. The strong
/// rule falls out of it: with no stored credential and no permission to pair, this
/// does not open a socket at all, because sending a registration without a key IS
/// the pairing prompt. There is no way to "just look" that could surprise somebody
/// with a dialog on their television.
///
/// ONE AT A TIME, AND BOUNDED
///
/// Every operation takes the gate, gets its own deadline, and connects fresh. A
/// held-open socket would be faster and would need reconnection, liveness and
/// staleness handling to be correct across a television that is deliberately
/// switched off part of the time -- which is the normal case here, not the edge.
/// The cost is a handshake per operation against a device on the local network;
/// the saving is a category of bug that only appears after the television has been
/// off for an hour.
///
/// NOTHING HERE IS EVER PUT IN A MESSAGE
///
/// The client key does not appear in an exception, a status line, a transition
/// step or a report. What is quoted back to a user is the television's own error
/// text and nothing this class holds.
/// </remarks>
public sealed class LgTvConnection
{
    /// <summary>How long opening a socket may take.</summary>
    /// <remarks>
    /// Short, because the answer that matters most is "it is not answering", and
    /// that is what a television which is switched off says. Waiting longer would
    /// make every poll of a power-on slower without making it more informative.
    ///
    /// Sized for the network this actually runs on. The television is on the same
    /// subnet as the machine talking to it, where a TLS handshake completes in tens
    /// of milliseconds; two seconds is two orders of magnitude of headroom, and
    /// every second beyond that is a second added to every mode that has to
    /// discover the television is off.
    /// </remarks>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long a request that changes something may take.</summary>
    /// <remarks>
    /// Generous, because it is measuring the television doing as it was told --
    /// loading a picture mode, renegotiating an HDMI link -- and not merely
    /// answering.
    /// </remarks>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a request that only asks for a value may take.</summary>
    /// <remarks>
    /// Separate from <see cref="RequestTimeout"/>, and much shorter, because it is
    /// a different question. A read asks the television for something it already
    /// knows -- which input is in front, which picture mode is selected -- and a
    /// television that has registered answers that at once or is not going to.
    ///
    /// Reads are also the calls that happen most: every poll of every convergence
    /// loop and every line of every dry run is one. Bounding them by what a
    /// settings write is worth waiting for is what made an unlucky poll cost
    /// twenty seconds.
    /// </remarks>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long somebody has to accept a prompt on the television.</summary>
    /// <remarks>
    /// Generous, because it is measuring a person finding the remote control. It
    /// happens once per television, and every later connection uses the stored key
    /// and is bounded by <see cref="RequestTimeout"/> instead.
    /// </remarks>
    private static readonly TimeSpan PairingTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long a television with a stored key has to accept it.</summary>
    /// <remarks>
    /// Short, because a registration that presents a key it already knows is
    /// answered immediately or not at all. What makes this worth having its own
    /// bound is standby: a television that is off keeps its network interface
    /// awake to be woken through, so it accepts the socket and then never gets as
    /// far as answering, and that is the commonest state it is in.
    ///
    /// So this number is, in practice, what "the television is off" costs to find
    /// out -- paid by every read of every television resource. Three seconds is
    /// still an age for a handshake on a local network that normally answers in
    /// under a hundred milliseconds.
    /// </remarks>
    private static readonly TimeSpan RegisterTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long to leave between one settings write and the next on one socket.
    /// </summary>
    /// <remarks>
    /// webOS drops settings that arrive while it is still applying the last ones.
    /// Only needed between separate writes: a single write carries as many
    /// settings as it likes in one object, and the television applies them
    /// together.
    /// </remarks>
    private static readonly TimeSpan SettingInterval = TimeSpan.FromMilliseconds(250);

    private const string RegisterId = "register_0";

    /// <summary>The television's own settings service, reached indirectly.</summary>
    /// <remarks>See <see cref="Session.WriteSettingsAsync"/> for why "indirectly".</remarks>
    private const string SettingsService = "luna://com.webos.settingsservice/setSystemSettings";

    private readonly ILgTvNetwork _network;
    private readonly ILgTvCredentials _credentials;
    private readonly TimeProvider _time;
    private readonly string? _televisionName;
    private readonly bool _isDefault;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LgTvSettings _settings = LgTvSettings.Disabled;

    /// <param name="time">
    /// The clock every deadline here is measured against. Injected for the same
    /// reason the engine's is: a test that a television which never answers is
    /// eventually given up on should not take as long as giving up on one.
    /// </param>
    /// <param name="televisionName">
    /// Which of the configured televisions this one is, or null for a machine
    /// that has none -- see <see cref="EngineBootstrap.TelevisionProviders"/>.
    /// </param>
    /// <param name="isDefault">
    /// Whether this television answers to the unprefixed resource keys. It
    /// affects the four <see cref="LgTvResourceKeys"/> and nothing else: not what
    /// is reached, not what is configured, not what is paired with.
    /// </param>
    public LgTvConnection(
        ILgTvNetwork network,
        ILgTvCredentials credentials,
        TimeProvider? time = null,
        string? televisionName = null,
        bool isDefault = true)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(credentials);

        if (televisionName is not null && string.IsNullOrWhiteSpace(televisionName))
        {
            throw new ArgumentException("A television name cannot be empty.", nameof(televisionName));
        }

        _network = network;
        _credentials = credentials;
        _time = time ?? TimeProvider.System;
        _televisionName = televisionName?.Trim();
        _isDefault = isDefault;
    }

    /// <summary>
    /// The configured television this connection belongs to, or null on a machine
    /// with none configured.
    /// </summary>
    /// <remarks>
    /// What a person is shown and what selects this television's own body out of
    /// <c>providers.lgTv</c>. Never the resource key -- see
    /// <see cref="ResourceName"/>.
    /// </remarks>
    public string? TelevisionName => _televisionName;

    /// <summary>
    /// The name this television's four resource keys are built from, or null when
    /// they are the unprefixed historical ones.
    /// </summary>
    public string? ResourceName => _isDefault ? null : _televisionName;

    /// <summary>The television as configured, or a disabled one.</summary>
    public LgTvSettings Settings => _settings;

    /// <summary>
    /// Whether there is a television here to do anything with, shared by all four
    /// of its resources.
    /// </summary>
    /// <remarks>
    /// Answered from the configuration and nothing else. No packet is sent and no
    /// socket is opened: this is asked while a transition is still being decided,
    /// possibly one that is about to be refused, and waking somebody's television
    /// in order to find out whether a mode is viable would be doing the thing the
    /// decision has not been made about yet.
    ///
    /// So there are exactly two answers. No television, or one switched off in the
    /// configuration, is <see cref="AvailabilityState.Disabled"/> -- settled, and
    /// the one case where a mode may leave the television out. A configured
    /// television is <see cref="AvailabilityState.Unknown"/>, because whether it
    /// is reachable is a question about the room that only the transition itself
    /// can answer. It is never <see cref="AvailabilityState.Unavailable"/>: a
    /// television that is asleep, unplugged for the evening or slow to answer is
    /// exactly what resource convergence exists for, and reporting it as absent
    /// would drop it at the moment it was about to work.
    ///
    /// One implementation, so that four resources of one device can never
    /// disagree about whether that device is there -- a mode in which the
    /// television is available for its power and not for its input would be a mode
    /// that half happens.
    /// </remarks>
    internal ResourceAvailability Availability => _settings.Enabled
        ? ResourceAvailability.Unknown(
            _televisionName is null
                ? "the television has not been contacted yet."
                : $"'{_televisionName}' has not been contacted yet.")
        : ResourceAvailability.Disabled(
            _televisionName is null
                ? $"no television is configured under '{LgTvSettings.SettingsPath}'."
                : $"the television '{_televisionName}' is switched off in '{LgTvSettings.SettingsPath}'.");

    /// <summary>
    /// Takes this television's own settings out of the shared section.
    /// </summary>
    /// <remarks>
    /// Called once per provider that reads the section, which is four times per
    /// television. That is harmless and deliberate: parsing is a pure function of
    /// the document, so the second call produces exactly what the first did. The
    /// alternative -- one provider configuring and the others depending on it --
    /// would make the result depend on the order the provider list happens to be
    /// in.
    ///
    /// A connection with no television name is the one a machine with no
    /// television has. It takes whatever the catalog says the default is, which
    /// on such a machine is <see cref="LgTvSettings.Disabled"/>.
    /// </remarks>
    public bool TryConfigure(JsonElement? settings, out string? problem)
    {
        if (!LgTvCatalog.TryParse(settings, out LgTvCatalog? catalog, out problem))
        {
            return false;
        }

        if (_televisionName is null)
        {
            _settings = catalog!.DefaultSettings;
            return true;
        }

        LgTvSettings? configured = catalog!.SettingsFor(_televisionName);

        if (configured is null)
        {
            problem =
                $"'{LgTvSettings.SettingsPath}' does not define television '{_televisionName}'.";
            return false;
        }

        _settings = configured;
        problem = null;
        return true;
    }

    /// <summary>
    /// Where the television's power is, reported as observed.
    /// </summary>
    /// <remarks>
    /// <see cref="LgTvPower.Unknown"/> covers the two cases where the question was
    /// genuinely not answered: a television nobody has paired with, which is never
    /// reached for at all because the only way to reach one is to put a prompt on
    /// its screen, and one that reports itself as still changing state.
    ///
    /// Silence is reported as silence and left for <see cref="TvPowerProvider"/> to
    /// interpret. See <see cref="LgTvPower"/>.
    /// </remarks>
    internal async Task<LgTvPower> ReadPowerAsync(CancellationToken cancellationToken)
    {
        Reached<LgTvPower> reached = await RunAsync(
            allowPairing: false,
            unreachableIsFine: true,
            roundTrips: 1,
            requestTimeout: ReadTimeout,
            async (session, token) =>
            {
                JsonElement payload = await session
                    .RequestAsync("ssap://com.webos.service.tvpower/power/getPowerState", null, token)
                    .ConfigureAwait(false);

                string? state = Text(payload, "state");

                if (string.IsNullOrWhiteSpace(state))
                {
                    return LgTvPower.Unknown;
                }

                // A television part way through switching on reports where it is
                // going as well as where it is. Answering with the destination
                // would be reporting the transition finished at the moment there
                // is most reason to think it is not.
                if (!string.IsNullOrWhiteSpace(Text(payload, "processing")))
                {
                    return LgTvPower.Unknown;
                }

                return string.Equals(state, "Active", StringComparison.OrdinalIgnoreCase)
                    ? LgTvPower.On
                    : LgTvPower.Off;
            },
            cancellationToken).ConfigureAwait(false);

        return reached.Reach switch
        {
            LgTvReach.Answered => reached.Value,
            LgTvReach.Unreachable => LgTvPower.Unreachable,
            _ => LgTvPower.Unknown,
        };
    }

    /// <summary>
    /// Which configured input the television is showing, or null when nobody can
    /// say or it is showing something that is not an input at all.
    /// </summary>
    internal async Task<string?> ReadInputAsync(CancellationToken cancellationToken)
    {
        Reached<string> reached = await RunAsync<string>(
            allowPairing: false,
            unreachableIsFine: true,
            roundTrips: 1,
            requestTimeout: ReadTimeout,
            async (session, token) =>
            {
                JsonElement payload = await session
                    .RequestAsync("ssap://com.webos.applicationManager/getForegroundAppInfo", null, token)
                    .ConfigureAwait(false);

                string? application = Text(payload, "appId");

                if (string.IsNullOrWhiteSpace(application))
                {
                    return null;
                }

                // The television names its inputs as applications:
                // "com.webos.app.hdmi4". A television showing its own menu, or
                // anything streaming, is showing no input at all -- which is not
                // an error and not a failure, simply not one of the answers a
                // mode can ask for.
                Match hdmi = Regex.Match(
                    application,
                    @"hdmi[^1-9]*([1-9])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1));

                return hdmi.Success ? _settings.LogicalFor($"HDMI_{hdmi.Groups[1].Value}") : null;
            },
            cancellationToken).ConfigureAwait(false);

        // Null for anything that is not an answer, silence included. A television
        // that is off is not showing an input, and saying which one it would be
        // showing if it were on would be inventing a reading.
        return reached.Value;
    }

    /// <summary>
    /// Asks the television to switch on, by the only means there is.
    /// </summary>
    /// <remarks>
    /// A magic packet, and nothing else. Nothing acknowledges one, so this
    /// returning means it was sent and not that anything received it -- whether it
    /// worked is answered by <see cref="ReadPowerAsync"/> afterwards, which is
    /// exactly what the convergence loop is for.
    /// </remarks>
    internal async Task WakeAsync(CancellationToken cancellationToken)
    {
        LgTvSettings settings = Require();

        using var deadline = new CancellationTokenSource(RequestTimeout, _time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        await _network.WakeAsync(settings.Host, settings.MacAddress, bounded.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells the television to switch off.
    /// </summary>
    /// <remarks>
    /// A television that cannot be reached is not treated as a failure, and the
    /// distinction matters. The command means "this television must not be on",
    /// and one that is not answering its socket is the ordinary way a television
    /// that is already off presents itself -- so failing here would mean every
    /// desktop mode failing whenever the television happened to be off already,
    /// which is most of the time.
    ///
    /// What is NOT claimed is that this verified anything. The power-off policy
    /// does not read back at all, so the step reports a command that was issued
    /// and no observed value. Silence is treated as "nothing to do", never as
    /// evidence that something was done.
    /// </remarks>
    internal async Task TurnOffAsync(CancellationToken cancellationToken)
    {
        _ = Require();

        _ = await RunAsync(
            allowPairing: true,
            unreachableIsFine: true,
            roundTrips: 1,
            requestTimeout: RequestTimeout,
            async (session, token) =>
            {
                await session.SendRequestAsync("ssap://system/turnOff", null, token).ConfigureAwait(false);
                return (object?)null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Switches the television to one of its inputs.</summary>
    internal async Task SwitchInputAsync(string inputId, CancellationToken cancellationToken)
    {
        LgTvSettings settings = Require();

        Reached<bool> switched = await RunAsync(
            allowPairing: true,
            unreachableIsFine: false,
            roundTrips: 1,
            requestTimeout: RequestTimeout,
            async (session, token) =>
            {
                JsonElement payload = await session
                    .RequestAsync("ssap://tv/switchInput", new JsonObject { ["inputId"] = inputId }, token)
                    .ConfigureAwait(false);

                return !payload.TryGetProperty("returnValue", out JsonElement returned) ||
                       returned.ValueKind != JsonValueKind.False;
            },
            cancellationToken).ConfigureAwait(false);

        // Only an answer of "no" is a refusal. Not having been asked at all cannot
        // happen here -- this path may pair, so the one case that skips the
        // television is a television that is not configured, and Require() has
        // already ruled that out.
        if (switched.Reach is LgTvReach.Answered && !switched.Value)
        {
            throw new InvalidOperationException(
                $"The television at {settings.Host} refused to switch to {inputId}.");
        }
    }

    /// <summary>
    /// Which profile the television's current picture mode belongs to, or null
    /// when nobody can say.
    /// </summary>
    /// <remarks>
    /// The picture mode is the only part of a profile webOS will report, so this
    /// is a partial answer by construction and is treated as one -- see
    /// <see cref="TvProfileProvider"/>, which dispatches whether or not this
    /// agrees.
    /// </remarks>
    internal async Task<string?> ReadProfileAsync(CancellationToken cancellationToken)
    {
        Reached<string> reached = await RunAsync<string>(
            allowPairing: false,
            unreachableIsFine: true,
            roundTrips: 1,
            requestTimeout: ReadTimeout,
            async (session, token) =>
            {
                JsonElement settings = await session
                    .GetSettingsAsync(LgTvProfile.PictureCategory, [LgTvProfile.PictureModeKey], token)
                    .ConfigureAwait(false);

                return _settings.ProfileForPictureMode(Text(settings, LgTvProfile.PictureModeKey) ?? string.Empty);
            },
            cancellationToken).ConfigureAwait(false);

        // Null for anything that is not an answer. A television that is off is in
        // no picture mode at all.
        return reached.Value;
    }

    /// <summary>
    /// Writes the half of a profile that changes how the HDMI link negotiates.
    /// </summary>
    /// <remarks>
    /// Silence is a failure here, unlike switching off. This has work to do that
    /// cannot be done to a television which is not there, and reporting success
    /// because nothing answered would mean a mode that quietly did not configure
    /// the picture and said it had.
    /// </remarks>
    internal async Task ApplySignalAsync(string profileName, CancellationToken cancellationToken)
    {
        LgTvProfile profile = RequireProfile(profileName);

        if (!profile.HasSignal)
        {
            return;
        }

        _ = await RunAsync<object>(
            allowPairing: true,
            unreachableIsFine: false,
            roundTrips: 2,
            requestTimeout: RequestTimeout,
            async (session, token) =>
            {
                await session
                    .WriteSettingsAsync(LgTvProfile.SignalCategory, profile.Signal, token)
                    .ConfigureAwait(false);

                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the picture mode, and anything written alongside it.</summary>
    internal async Task ApplyProfileAsync(string profileName, CancellationToken cancellationToken)
    {
        LgTvProfile profile = RequireProfile(profileName);

        _ = await RunAsync<object>(
            allowPairing: true,
            unreachableIsFine: false,
            roundTrips: 4,
            requestTimeout: RequestTimeout,
            async (session, token) =>
            {
                // The picture mode goes first and alone. Selecting one loads that
                // mode's own stored values, so anything written before it in the
                // same breath is overwritten by the act of selecting it.
                await session.WriteSettingsAsync(
                    LgTvProfile.PictureCategory,
                    [new LgTvSetting(LgTvProfile.PictureModeKey, profile.PictureMode)],
                    token).ConfigureAwait(false);

                if (profile.Settings.Count > 0)
                {
                    // Paced, because webOS drops settings that arrive while it is
                    // still loading the mode that was just selected.
                    await Task.Delay(SettingInterval, _time, token).ConfigureAwait(false);

                    await session
                        .WriteSettingsAsync(LgTvProfile.PictureCategory, profile.Settings, token)
                        .ConfigureAwait(false);
                }

                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a socket and registers, and nothing else.
    /// </summary>
    /// <remarks>
    /// For a settings screen's "Pair" action: it puts the prompt on the
    /// television's own screen, waits for somebody to accept it, and lets
    /// <see cref="RunAsync{T}"/> write the credential it is issued -- the same
    /// path every other command already takes on its way to doing something,
    /// with nothing asked of the television once registered.
    /// </remarks>
    internal Task PairAsync(CancellationToken cancellationToken)
    {
        _ = Require();

        return RunAsync<object?>(
            allowPairing: true,
            unreachableIsFine: false,
            roundTrips: 0,
            requestTimeout: RequestTimeout,
            (_, _) => Task.FromResult<object?>(null),
            cancellationToken);
    }

    /// <summary>
    /// Pairs with the television, synchronously, for a settings screen.
    /// </summary>
    /// <remarks>
    /// The one place outside the engine's own transitions that opens a socket to
    /// the television at all -- see <see cref="LgTvBridge"/> for why the
    /// asynchronous/synchronous boundary is drawn here rather than in the
    /// interface every provider implements. Failure is reported rather than
    /// thrown, in the caller's own words, because a settings screen wants a
    /// message to show and not an exception to catch.
    /// </remarks>
    public bool TryPair(out string? problem)
    {
        try
        {
            LgTvBridge.Wait(PairAsync, CancellationToken.None);
            problem = null;
            return true;
        }
        catch (Exception exception)
        {
            problem = exception.Message;
            return false;
        }
    }

    private LgTvProfile RequireProfile(string name)
    {
        _ = Require();

        return _settings.ProfileFor(name)
            ?? throw new ArgumentException($"'{name}' is not a television profile.", nameof(name));
    }

    private LgTvSettings Require() => _settings.Enabled
        ? _settings
        : throw new InvalidOperationException(
            $"No television is configured. Add one under '{LgTvSettings.SettingsPath}'.");

    /// <summary>
    /// Opens a socket, registers on it, does one thing, and closes it.
    /// </summary>
    /// <remarks>
    /// Reports which of the three things happened -- see <see cref="LgTvReach"/> --
    /// rather than folding "did not answer" and "was not asked" into one absent
    /// value. Callers that command have already established that a television is
    /// configured, and a failure to reach one on a command path surfaces as the
    /// exception the network threw.
    /// </remarks>
    /// <param name="unreachableIsFine">
    /// Whether a television that does not answer at all is an acceptable outcome
    /// rather than a failure. True for reading, where not answering is how a
    /// television that is off looks, and true for switching off, where it is how
    /// one that is ALREADY off looks. False for anything that has actual work to
    /// do, which cannot be done to a television that is not there.
    ///
    /// It covers silence wherever it happens, not only at the connection. A
    /// television in network standby is the case that decides this: it accepts the
    /// socket, because its network interface is awake to be woken by, and then
    /// never finishes the registration, because webOS is not running. Treating
    /// that as a failure made every return to the desktop fail on the ordinary day
    /// when the television was already off.
    ///
    /// What it does not cover is a television that answers and says no. That is a
    /// device refusing something, not a device that is not there, and it fails.
    /// </param>
    /// <param name="roundTrips">
    /// How many requests this operation makes once it is registered, so that its
    /// deadline is about the work rather than a single number that is either too
    /// short for a profile or too long for a poll.
    /// </param>
    /// <param name="requestTimeout">
    /// How long each of those requests may take. <see cref="ReadTimeout"/> for
    /// asking the television something it already knows, <see cref="RequestTimeout"/>
    /// for telling it to do something.
    /// </param>
    private async Task<Reached<T>> RunAsync<T>(
        bool allowPairing,
        bool unreachableIsFine,
        int roundTrips,
        TimeSpan requestTimeout,
        Func<Session, CancellationToken, Task<T?>> work,
        CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            return new Reached<T>(LgTvReach.NotAttempted, default);
        }

        LgTvCredential? credential = _credentials.Read(_settings.Host);

        if (credential is null && !allowPairing)
        {
            // The line that keeps a dry run silent. A registration without a key
            // IS the pairing prompt, so there is no way to look at an unpaired
            // television without asking its owner to accept something. Nothing is
            // opened, and the answer is that nothing was asked -- which is not the
            // same answer as a television that was asked and stayed quiet.
            return new Reached<T>(LgTvReach.NotAttempted, default);
        }

        // The gate itself is bounded, and bounded by what the CALLER is worth
        // waiting for rather than by what it might be waiting behind. A command
        // may queue behind a pairing prompt, because a transition somebody asked
        // for should still happen once they have accepted it. A read may not:
        // reads are what every convergence poll and every status refresh does, and
        // one that sat for a minute waiting for somebody to find the remote
        // control would report a television as unreadable long after it had
        // answered. It gives up in its own time and says it does not know.
        //
        // Measured against the injected clock, like every other deadline here --
        // the ordinary TimeSpan overload is not, and a gate wait that ran on real
        // wall time while everything around it ran on a fake one would make this
        // untestable without actually waiting.
        TimeSpan gateTimeout = allowPairing
            ? PairingTimeout
            : ConnectTimeout + RegisterTimeout + (requestTimeout * roundTrips);

        using var gateDeadline = new CancellationTokenSource(gateTimeout, _time);
        using var gateBound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, gateDeadline.Token);

        try
        {
            await _gate.WaitAsync(gateBound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            gateDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "The television is already busy with another request.");
        }

        try
        {
            return await ConnectAndRunAsync(
                    unreachableIsFine, roundTrips, requestTimeout, credential, work, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private async Task<Reached<T>> ConnectAndRunAsync<T>(
        bool unreachableIsFine,
        int roundTrips,
        TimeSpan requestTimeout,
        LgTvCredential? credential,
        Func<Session, CancellationToken, Task<T?>> work,
        CancellationToken cancellationToken)
    {
        // A connection that has to be approved on the television's screen is
        // measuring a person; one that has a key is measuring a local network --
        // the handshake, the registration, and this operation's own requests.
        //
        // Each of those three is bounded on its own and the total is their sum,
        // rather than the registration being charged at the price of a request.
        // What that arithmetic used to produce was a read of a single value
        // carrying the same twenty-five second deadline as a nine-setting profile
        // write, which is how one unlucky poll could outlast the thing it was
        // polling for.
        using var deadline = new CancellationTokenSource(
            credential is null
                ? PairingTimeout
                : ConnectTimeout + RegisterTimeout + (requestTimeout * roundTrips),
            _time);

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        ILgTvSocket socket;

        try
        {
            socket = await _network
                .ConnectAsync(
                    _settings.Host,
                    LgTvSettings.SecurePort,
                    credential?.CertificateFingerprint,
                    bounded.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Gave up waiting for it to answer. Not a cancellation as far as the
            // engine is concerned -- nobody called anything off -- so it must not
            // reach convergence looking like one.
            if (unreachableIsFine)
            {
                return Unreachable<T>();
            }

            throw new InvalidOperationException($"The television at {_settings.Host} did not answer.");
        }
        catch (Exception) when (unreachableIsFine && !cancellationToken.IsCancellationRequested)
        {
            // A television that is off does not answer, and that is not news. On a
            // read this is the expected outcome half the time, so it is an absent
            // answer rather than a fault.
            return Unreachable<T>();
        }

        // A registration with a key in hand is answered at once or not at all: the
        // television either recognises it or is not really there. Bounding it
        // separately is what keeps a return to the desktop quick on the ordinary
        // evening when the television is in standby -- it accepts the socket and
        // then never registers, and waiting the whole operation's deadline for
        // that would put half a minute into every desktop mode. A registration
        // that is waiting for somebody to find the remote control is bounded by
        // the operation instead, which is already the pairing timeout.
        using CancellationTokenSource? registering = credential is null
            ? null
            : new CancellationTokenSource(RegisterTimeout, _time);

        using CancellationTokenSource registerBound = registering is null
            ? CancellationTokenSource.CreateLinkedTokenSource(bounded.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(bounded.Token, registering.Token);

        await using (socket.ConfigureAwait(false))
        {
            try
            {
                var session = new Session(socket);

                string issued = await session
                    .RegisterAsync(credential?.ClientKey, registerBound.Token)
                    .ConfigureAwait(false);

                // Written only when the television actually issued something new,
                // which only happens when somebody accepted a prompt on its screen.
                // A connection that reused a stored key writes nothing.
                if (!string.Equals(issued, credential?.ClientKey, StringComparison.Ordinal))
                {
                    _ = _credentials.TryWrite(
                        _settings.Host,
                        new LgTvCredential(issued, socket.CertificateFingerprint),
                        out _);
                }

                return new Reached<T>(
                    LgTvReach.Answered, await work(session, bounded.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (
                (deadline.IsCancellationRequested || registering?.IsCancellationRequested == true) &&
                !cancellationToken.IsCancellationRequested)
            {
                // The television stopped answering part way through, and one of
                // this operation's own deadlines is what ended it. Letting the
                // cancellation escape as one would tell the engine the USER
                // cancelled -- which stops the transition and reports it as
                // uncertain, when what actually happened is a device that went
                // quiet and a step that should fail and be retried.
                if (unreachableIsFine)
                {
                    return Unreachable<T>();
                }

                throw new InvalidOperationException(
                    $"The television at {_settings.Host} stopped answering.");
            }
            catch (Exception exception) when (
                unreachableIsFine && IsSilence(exception) && !cancellationToken.IsCancellationRequested)
            {
                // The socket dropped. For an operation that is content with
                // silence this is the same answer as never having connected at
                // all, and it is what a television that has just done what it was
                // told looks like from here.
                //
                // Narrow on purpose: only a socket going away. A television that
                // answers and says no still fails, because that is a device
                // refusing something rather than a device that is not there.
                return Unreachable<T>();
            }
        }
    }

    /// <summary>Reached for, and nothing came back.</summary>
    private static Reached<T> Unreachable<T>() => new(LgTvReach.Unreachable, default);

    /// <summary>
    /// Whether an exception is the television not being there, as opposed to the
    /// television saying something.
    /// </summary>
    /// <remarks>
    /// Everything here is a socket that stopped: the WebSocket closing or
    /// faulting, the stream underneath it ending, the connection being reset.
    /// <see cref="InvalidOperationException"/> is deliberately absent -- that is
    /// what this file throws when the television answered and the answer was an
    /// error, which is a fact worth reporting rather than silence to be absorbed.
    /// </remarks>
    private static bool IsSilence(Exception exception) =>
        exception is WebSocketException or IOException or SocketException;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>
    /// One open socket, and the SSAP conversation carried on it.
    /// </summary>
    /// <remarks>
    /// webOS multiplexes: replies carry the id of the request they answer, and
    /// unsolicited messages arrive in between. So everything waits for its own id
    /// and discards what is not addressed to it, rather than assuming the next
    /// message is the answer.
    /// </remarks>
    private sealed class Session(ILgTvSocket socket)
    {
        private int _sent;

        /// <summary>
        /// Introduces this application, and gets back the key that lets it back in.
        /// </summary>
        /// <remarks>
        /// With a key, the television accepts silently. Without one it puts a
        /// prompt on the screen and answers when somebody accepts it -- which is
        /// why this is only ever reached on a path that was allowed to pair.
        /// </remarks>
        public async Task<string> RegisterAsync(string? clientKey, CancellationToken cancellationToken)
        {
            var payload = new JsonObject
            {
                ["forcePairing"] = false,
                ["pairingType"] = "PROMPT",
                ["manifest"] = JsonNode.Parse(LgTvManifest.Json),
            };

            if (!string.IsNullOrWhiteSpace(clientKey))
            {
                payload["client-key"] = clientKey;
            }

            await SendAsync(
                new JsonObject { ["type"] = "register", ["id"] = RegisterId, ["payload"] = payload },
                cancellationToken).ConfigureAwait(false);

            while (true)
            {
                JsonElement message = await ReceiveAsync(cancellationToken).ConfigureAwait(false);

                if (!string.Equals(Text(message, "id"), RegisterId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(Text(message, "type"), "error", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The television refused the connection: {Text(message, "error") ?? "no reason given"}.");
                }

                // The television answers a registration twice: once to say it is
                // showing the prompt, and again once it has been accepted. Only
                // the second one is an answer.
                if (!string.Equals(Text(message, "type"), "registered", StringComparison.Ordinal))
                {
                    continue;
                }

                if (message.TryGetProperty("payload", out JsonElement payloadElement) &&
                    Text(payloadElement, "client-key") is string issued &&
                    !string.IsNullOrWhiteSpace(issued))
                {
                    return issued;
                }

                if (!string.IsNullOrWhiteSpace(clientKey))
                {
                    // Accepted the key it was given without reissuing it.
                    return clientKey!;
                }

                throw new InvalidOperationException(
                    "The television accepted the connection but issued no pairing key.");
            }
        }

        /// <summary>Sends a request and waits for its reply.</summary>
        public async Task<JsonElement> RequestAsync(
            string uri,
            JsonObject? payload,
            CancellationToken cancellationToken)
        {
            string id = await SendRequestAsync(uri, payload, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                JsonElement message = await ReceiveAsync(cancellationToken).ConfigureAwait(false);

                if (!string.Equals(Text(message, "id"), id, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(Text(message, "type"), "error", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The television refused {uri}: {Text(message, "error") ?? "no reason given"}.");
                }

                if (!message.TryGetProperty("payload", out JsonElement answer))
                {
                    throw new InvalidOperationException($"The television's answer to {uri} had nothing in it.");
                }

                return answer.Clone();
            }
        }

        /// <summary>
        /// Sends a request without waiting for a reply, and returns its id.
        /// </summary>
        /// <remarks>
        /// For the one command whose reply cannot be waited for: a television
        /// asked to switch off stops answering because it did what it was told.
        /// </remarks>
        public async Task<string> SendRequestAsync(
            string uri,
            JsonObject? payload,
            CancellationToken cancellationToken)
        {
            string id = $"pcmode_{++_sent}";

            await SendAsync(
                new JsonObject
                {
                    ["type"] = "request",
                    ["id"] = id,
                    ["uri"] = uri,
                    ["payload"] = payload ?? [],
                },
                cancellationToken).ConfigureAwait(false);

            return id;
        }

        /// <summary>Reads named settings out of one of the television's categories.</summary>
        public async Task<JsonElement> GetSettingsAsync(
            string category,
            IReadOnlyList<string> keys,
            CancellationToken cancellationToken)
        {
            var payload = new JsonObject
            {
                ["category"] = category,
                ["keys"] = new JsonArray([.. keys.Select(key => (JsonNode)JsonValue.Create(key))]),
            };

            JsonElement answer = await RequestAsync("ssap://settings/getSystemSettings", payload, cancellationToken)
                .ConfigureAwait(false);

            if (!answer.TryGetProperty("settings", out JsonElement settings) ||
                settings.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"The television reported no '{category}' settings.");
            }

            return settings.Clone();
        }

        /// <summary>
        /// Writes settings the television will not let an application write
        /// directly.
        /// </summary>
        /// <remarks>
        /// WHY THIS IS NOT ONE REQUEST
        ///
        /// webOS does not expose setting the picture or the HDMI ports over SSAP
        /// at all. What it does expose is creating an on-screen alert whose
        /// buttons can invoke a luna:// service -- and the settings service is one
        /// of those, running with the television's own authority rather than the
        /// connection's. So an alert is created carrying the settings, and then
        /// immediately closed; closing it fires the 'onclose' action, and the
        /// settings are written.
        ///
        /// The alert exists for a fraction of a second and carries a single space
        /// as its message, so nothing legible appears on screen. This is what
        /// every tool that configures these settings does, because it is the only
        /// route the television offers. It is written out here rather than hidden
        /// behind a helper called "setSetting", because somebody reading this
        /// later is owed the explanation of why a picture setting is sent as a
        /// notification.
        ///
        /// Both halves are checked. The creation says whether the television took
        /// the alert; the close says whether the action attached to it ran. A
        /// write that only checked the first would report success for settings
        /// that were never applied.
        /// </remarks>
        public async Task WriteSettingsAsync(
            string category,
            IReadOnlyList<LgTvSetting> settings,
            CancellationToken cancellationToken)
        {
            if (settings.Count == 0)
            {
                return;
            }

            var values = new JsonObject();

            foreach (LgTvSetting setting in settings)
            {
                values[setting.Key] = setting.Value;
            }

            // Cloned per use: a node belongs to one parent, and the same
            // parameters are carried in three places in the one message.
            JsonObject Parameters() => new()
            {
                ["category"] = category,
                ["settings"] = values.DeepClone(),
            };

            JsonObject Write() => new()
            {
                ["uri"] = SettingsService,
                ["params"] = Parameters(),
            };

            var alert = new JsonObject
            {
                ["message"] = " ",
                ["buttons"] = new JsonArray(new JsonObject
                {
                    ["label"] = string.Empty,
                    ["onClick"] = SettingsService,
                    ["params"] = Parameters(),
                }),
                ["onclose"] = Write(),
                ["onfail"] = Write(),
            };

            JsonElement created = await RequestAsync(
                "ssap://system.notifications/createAlert", alert, cancellationToken).ConfigureAwait(false);

            if (created.TryGetProperty("returnValue", out JsonElement accepted) &&
                accepted.ValueKind == JsonValueKind.False)
            {
                throw new InvalidOperationException($"The television refused to change {Describe(settings)}.");
            }

            if (Text(created, "alertId") is not string alertId || string.IsNullOrWhiteSpace(alertId))
            {
                throw new InvalidOperationException(
                    $"The television did not say how to apply {Describe(settings)}.");
            }

            JsonElement closed = await RequestAsync(
                "ssap://system.notifications/closeAlert",
                new JsonObject { ["alertId"] = alertId },
                cancellationToken).ConfigureAwait(false);

            if (closed.TryGetProperty("returnValue", out JsonElement applied) &&
                applied.ValueKind == JsonValueKind.False)
            {
                throw new InvalidOperationException($"The television did not apply {Describe(settings)}.");
            }
        }

        /// <summary>Names settings for a message, without their values.</summary>
        private static string Describe(IReadOnlyList<LgTvSetting> settings) =>
            string.Join(", ", settings.Select(setting => setting.Key));

        private Task SendAsync(JsonObject message, CancellationToken cancellationToken) =>
            socket.SendAsync(message.ToJsonString(), cancellationToken);

        private async Task<JsonElement> ReceiveAsync(CancellationToken cancellationToken)
        {
            string message = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return JsonDocument.Parse(message).RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    $"The television sent something that is not JSON. {exception.Message}");
            }
        }
    }
}

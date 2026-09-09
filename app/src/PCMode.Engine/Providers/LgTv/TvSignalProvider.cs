using System.Text.Json;

namespace PCMode.Engine;

/// <summary>
/// Sets up how the television's HDMI port negotiates, before Windows drives it.
/// </summary>
/// <remarks>
/// WHY THIS IS NOT PART OF THE PICTURE PROFILE
///
/// Because the two halves have opposite ordering requirements, and no single
/// resource can be on both sides of the display switch. Writing any of these
/// settings makes the television re-handshake the HDMI link, which drops the sink
/// off the bus. Doing that once the television is the active Windows display --
/// and in a television mode it is the only one -- leaves Windows with no display,
/// and Windows recovers by reactivating the desk monitors on its own, silently
/// undoing the layout that was just applied. So these go in first, while the desk
/// monitors are still drawing the picture. See <see cref="TvProfileProvider"/>
/// for the other half.
///
/// NOTHING IS READ BACK, AND IT WAITS INSTEAD
///
/// The settings can be read, but reading them back proves the television accepted
/// the values, not that the link has finished renegotiating -- and it is the
/// renegotiation, not the values, that the display is waiting for. There is
/// nothing to watch that answers the question being asked, so this is the one
/// resource that waits on a clock, and says so:
/// <see cref="ConvergencePolicy.PostDispatchDelay"/> is an admission rather than
/// a measurement.
/// </remarks>
public sealed class TvSignalProvider
    : IConfigurableProvider,
        IDependentProvider,
        IConvergentProvider,
        IAvailableProvider
{
    /// <summary>The configuration key.</summary>
    public const string ResourceKey = "tvSignal";

    private readonly LgTvConnection _connection;

    public TvSignalProvider(LgTvConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    public string Key => LgTvResourceKeys.Signal(_connection.ResourceName);

    public string DisplayName => LgTvResourceKeys.DisplayName(_connection.TelevisionName, "signal");

    /// <summary>Which configured television this is, for a screen that lists them.</summary>
    public string? TelevisionName => _connection.TelevisionName;

    /// <summary>The television's settings, for a screen that offers its profile names.</summary>
    public LgTvSettings Settings => _connection.Settings;

    /// <summary>Shared with the rest of the television. One device, one section.</summary>
    public string SettingsKey => LgTvSettings.SectionKey;

    public bool TryConfigure(JsonElement? settings, out string? problem) =>
        _connection.TryConfigure(settings, out problem);

    /// <summary>
    /// The television awake, and showing the socket these settings are about.
    /// </summary>
    /// <remarks>
    /// The input is a requirement rather than an ordering preference because most
    /// of these settings name one HDMI socket and mean nothing without it. It also
    /// means a mode that says only which picture it wants gets the television on,
    /// on the right input, with the link configured -- which is the only reading
    /// of that request that is not a failure.
    ///
    /// A configuration whose profiles set nothing per-socket needs no input, and
    /// asks for none: requiring one there would drag an input switch into a mode
    /// that had no use for it.
    /// </remarks>
    public IReadOnlyList<ResourceRequirement> RequirementsFor(string target)
    {
        string powerKey = LgTvResourceKeys.Power(_connection.ResourceName);
        string inputKey = LgTvResourceKeys.Input(_connection.ResourceName);
        LgTvProfile? profile = _connection.Settings.ProfileFor(target);

        return profile is { HasSignal: true, InputName: string input }
            ? [new ResourceRequirement(powerKey, TvPowerProvider.On), new ResourceRequirement(inputKey, input)]
            : [new ResourceRequirement(powerKey, TvPowerProvider.On)];
    }

    /// <summary>
    /// Written once, then left alone long enough for the link to come back.
    /// </summary>
    /// <remarks>
    /// One attempt, because the write is complete and idempotent and there is
    /// nothing to read that would say it had not worked. Repeating it would
    /// renegotiate the link a second time, which is the very disturbance the
    /// ordering exists to keep away from the display switch.
    ///
    /// Four seconds afterwards, and the number is empirical rather than principled:
    /// it is how long a television takes to drop and re-establish the link. The
    /// display step is what that time is being bought for.
    /// </remarks>
    public ConvergencePolicy PolicyFor(string target) =>
        new()
        {
            Verify = false,
            MaxAttempts = 1,
            PostDispatchDelay = TimeSpan.FromSeconds(4),
        };

    /// <summary>
    /// Whether there is a television to configure. See
    /// <see cref="LgTvConnection.Availability"/>.
    /// </summary>
    public ResourceAvailability AvailabilityOf(string target) => _connection.Availability;

    public bool IsValidValue(string value, out string? problem) =>
        ValidateProfile(_connection.Settings, value, out problem);

    /// <summary>
    /// Nothing. There is no answer to the question this resource asks.
    /// </summary>
    /// <remarks>
    /// Deliberate, and the reason the policy does not verify. Null means "apply
    /// it", which is right: these are written once per transition into a
    /// television that may have been changed with the remote control in between,
    /// and they are idempotent.
    /// </remarks>
    public string? Read() => null;

    public void Apply(string value, CancellationToken cancellationToken) =>
        LgTvBridge.Wait(token => _connection.ApplySignalAsync(value, token), cancellationToken);

    /// <summary>
    /// Whether a profile name is one this machine has, shared by both halves.
    /// </summary>
    /// <remarks>
    /// One implementation so that a profile can never be accepted by the signal
    /// and refused by the picture, and so that both give the same message for the
    /// same mistake.
    /// </remarks>
    internal static bool ValidateProfile(LgTvSettings settings, string value, out string? problem)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problem = "the television profile has no name.";
            return false;
        }

        if (settings.Profiles.Count == 0)
        {
            problem =
                $"'{value.Trim()}' cannot be applied because no television profiles are defined. "
                + $"Add one under '{LgTvSettings.SettingsPath}.profiles'.";

            return false;
        }

        if (settings.ProfileFor(value) is null)
        {
            problem =
                $"'{value.Trim()}' is not a television profile. "
                + $"'{LgTvSettings.SettingsPath}.profiles' defines: {string.Join(", ", settings.ProfileNames)}.";

            return false;
        }

        problem = null;
        return true;
    }
}

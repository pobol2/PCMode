using System.Text.Json;

namespace PCMode.Engine;

/// <summary>
/// Selects the television's picture mode, once there is a picture to select it
/// against.
/// </summary>
/// <remarks>
/// WHY THIS COMES AFTER THE DISPLAY
///
/// webOS exposes 'filmMaker' and 'hdrFilmMaker' -- and 'game' and 'hdrGame' -- as
/// separate picture modes, and refuses the HDR one until the incoming signal
/// really is HDR. Until Windows has switched the desktop onto the television
/// there is no incoming signal at all, so a profile naming an HDR mode would be
/// accepted, ignored, and leave the television where it was. See
/// <see cref="TvSignalProvider"/> for the half that must happen before the
/// display instead.
///
/// A PARTIAL READBACK, TREATED AS ONE
///
/// The picture mode is the only part of a profile the television will report. So
/// a profile that reads back as matching has confirmed the picture mode and
/// nothing else -- and a transition interrupted part way through leaves exactly
/// that: the picture mode already correct and an unknown number of the settings
/// beside it never sent. Verification alone can never notice.
///
/// <see cref="ConvergencePolicy.DispatchAtLeastOnce"/> is the answer, and it is
/// what that flag exists for: the profile is written once per transition whether
/// or not the readback already agrees, and then verified normally. Once, not
/// repeatedly -- a resource that was genuinely already correct confirms on the
/// first read rather than being commanded again for the same reason.
/// </remarks>
public sealed class TvProfileProvider
    : IConfigurableProvider,
        IDependentProvider,
        IConvergentProvider,
        IAvailableProvider
{
    /// <summary>The configuration key.</summary>
    public const string ResourceKey = "tvProfile";

    private readonly LgTvConnection _connection;

    public TvProfileProvider(LgTvConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    public string Key => LgTvResourceKeys.Profile(_connection.ResourceName);

    public string DisplayName => LgTvResourceKeys.DisplayName(_connection.TelevisionName, "picture");

    /// <summary>Which configured television this is, for a screen that lists them.</summary>
    public string? TelevisionName => _connection.TelevisionName;

    /// <summary>The television's settings, for a screen that offers its profile names.</summary>
    public LgTvSettings Settings => _connection.Settings;

    /// <summary>Shared with the rest of the television. One device, one section.</summary>
    public string SettingsKey => LgTvSettings.SectionKey;

    public bool TryConfigure(JsonElement? settings, out string? problem) =>
        _connection.TryConfigure(settings, out problem);

    /// <summary>
    /// The same profile's signal half, which brings the rest of the chain with it.
    /// </summary>
    /// <remarks>
    /// Only the signal is named. It requires the input, which requires the power,
    /// so naming any of those again here would be the same fact written in two
    /// places for the two to fall out of step. A mode that asks only for a picture
    /// gets the whole chain.
    /// </remarks>
    public IReadOnlyList<ResourceRequirement> RequirementsFor(string target) =>
        [new ResourceRequirement(LgTvResourceKeys.Signal(_connection.ResourceName), target)];

    /// <summary>
    /// After the display, when there is one.
    /// </summary>
    /// <remarks>
    /// An ordering preference rather than a requirement, so that a mode which
    /// changes only the television's picture -- and leaves the monitors exactly as
    /// they are -- is applied as written instead of having a display layout
    /// invented for it.
    /// </remarks>
    public IReadOnlyList<string> RunsAfter => [DisplayProvider.ResourceKey];

    /// <summary>
    /// Written once, and then watched.
    /// </summary>
    /// <remarks>
    /// One attempt, because a readback that does not come back does not mean the
    /// write failed: much the commonest reason a television goes quiet here is
    /// that it is busy applying what was just sent it. Re-sending the profile on
    /// that evidence disturbs a television that is already doing the right thing.
    ///
    /// The readback still runs, and still decides whether this step succeeded --
    /// it is the difference between "the picture mode is what the mode asked for"
    /// and "something was sent". An HDR mode selected against a signal that never
    /// became HDR fails here, which is exactly the mistake worth catching.
    /// </remarks>
    public ConvergencePolicy PolicyFor(string target) =>
        new()
        {
            Verify = true,
            MaxAttempts = 1,
            RetryInterval = TimeSpan.FromSeconds(8),
            PollInterval = TimeSpan.FromSeconds(1),
            DispatchAtLeastOnce = true,
        };

    /// <summary>
    /// Whether there is a television whose picture can be set. See
    /// <see cref="LgTvConnection.Availability"/>.
    /// </summary>
    public ResourceAvailability AvailabilityOf(string target) => _connection.Availability;

    public bool IsValidValue(string value, out string? problem) =>
        TvSignalProvider.ValidateProfile(_connection.Settings, value, out problem);

    public string? Read() => LgTvBridge.Wait(_connection.ReadProfileAsync);

    public void Apply(string value, CancellationToken cancellationToken) =>
        LgTvBridge.Wait(token => _connection.ApplyProfileAsync(value, token), cancellationToken);
}

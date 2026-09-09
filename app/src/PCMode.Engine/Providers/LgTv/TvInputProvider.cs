using System.Text.Json;

namespace PCMode.Engine;

/// <summary>
/// Chooses which of the television's inputs it is showing.
/// </summary>
/// <remarks>
/// LOGICAL NAMES, NOT SOCKET NUMBERS
///
/// A mode says "PC". Which HDMI socket that is depends on which one somebody
/// plugged the machine into, so it lives in
/// <see cref="LgTvSettings.Inputs"/> -- the same division the display, cursor and
/// audio providers already keep. Moving the cable is then one edit rather than an
/// edit in every mode.
///
/// IT NEEDS THE TELEVISION ON, AND SAYS SO
///
/// Asking a television that is asleep to change input is asking nothing of
/// anybody. That is a requirement rather than an ordering preference, which means
/// a mode that names only the input gets the television switched on as part of
/// getting what it asked for -- the only reading of the request that is not a
/// failure.
/// </remarks>
public sealed class TvInputProvider : IConfigurableProvider, IDependentProvider, IConvergentProvider, IAvailableProvider
{
    /// <summary>The configuration key.</summary>
    public const string ResourceKey = "tvInput";

    private readonly LgTvConnection _connection;

    public TvInputProvider(LgTvConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    public string Key => LgTvResourceKeys.Input(_connection.ResourceName);

    public string DisplayName => LgTvResourceKeys.DisplayName(_connection.TelevisionName, "input");

    /// <summary>Which configured television this is, for a screen that lists them.</summary>
    public string? TelevisionName => _connection.TelevisionName;

    /// <summary>The television's settings, for a screen that offers its input names.</summary>
    public LgTvSettings Settings => _connection.Settings;

    /// <summary>Shared with the power resource. One television, one section.</summary>
    public string SettingsKey => LgTvSettings.SectionKey;

    public bool TryConfigure(JsonElement? settings, out string? problem) =>
        _connection.TryConfigure(settings, out problem);

    /// <summary>This television, awake, whatever input is being asked for.</summary>
    /// <remarks>
    /// Its OWN power, named through the same connection, so a mode that switches
    /// one television's input can never be satisfied by another television being
    /// on.
    /// </remarks>
    public IReadOnlyList<ResourceRequirement> RequirementsFor(string target) =>
        [new ResourceRequirement(LgTvResourceKeys.Power(_connection.ResourceName), TvPowerProvider.On)];

    /// <summary>
    /// Quick, but not instant.
    /// </summary>
    /// <remarks>
    /// Switching input is a command the television carries out in about a second,
    /// and it reports which application is in front, so it can be watched properly.
    /// It settles briefly because the television reports the new input while the
    /// picture is still arriving, and anything ordered after this -- the display
    /// layout that is about to be sent down that cable -- wants the input actually
    /// there rather than merely selected.
    /// </remarks>
    public ConvergencePolicy PolicyFor(string target) =>
        new()
        {
            Verify = true,
            MaxAttempts = 3,
            RetryInterval = TimeSpan.FromSeconds(5),
            PollInterval = TimeSpan.FromMilliseconds(500),
            Settle = TimeSpan.FromSeconds(1),
        };

    /// <summary>
    /// Whether there is a television to switch inputs on. See
    /// <see cref="LgTvConnection.Availability"/>.
    /// </summary>
    public ResourceAvailability AvailabilityOf(string target) => _connection.Availability;

    public bool IsValidValue(string value, out string? problem)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problem = "the television input has no name.";
            return false;
        }

        LgTvSettings settings = _connection.Settings;

        if (settings.Inputs.Count == 0)
        {
            problem =
                $"'{value.Trim()}' cannot be applied because no television inputs are defined. "
                + $"Add one under '{LgTvSettings.SettingsPath}.inputs'.";

            return false;
        }

        if (settings.InputIdFor(value) is null)
        {
            problem =
                $"'{value.Trim()}' is not a television input. "
                + $"'{LgTvSettings.SettingsPath}.inputs' defines: {string.Join(", ", settings.InputNames)}.";

            return false;
        }

        problem = null;
        return true;
    }

    public string? Read() => LgTvBridge.Wait(_connection.ReadInputAsync);

    public void Apply(string value, CancellationToken cancellationToken)
    {
        string inputId =
            _connection.Settings.InputIdFor(value)
            ?? throw new ArgumentException($"'{value}' is not a television input.", nameof(value));

        LgTvBridge.Wait(token => _connection.SwitchInputAsync(inputId, token), cancellationToken);
    }
}

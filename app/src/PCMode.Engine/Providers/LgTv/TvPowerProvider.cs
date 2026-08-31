using System.Text.Json;

namespace PCMode.Engine;

/// <summary>
/// Switches the television on and off.
/// </summary>
/// <remarks>
/// TWO TARGETS, TWO DIFFERENT PROBLEMS
///
/// They are not opposites. Switching on can be watched: the television starts
/// answering, and says so, so it is verified by reading it back until it does.
/// Switching off cannot -- a television that has done what it was told stops
/// answering, and so does one that was never reached, and nothing can tell those
/// apart from outside. So off is dispatched and not verified, and the step it
/// produces reports no observed value. Treating the silence afterwards as proof
/// would be inventing evidence out of the absence of it.
///
/// GOING OFF NEEDS SOMEWHERE TO PUT THE DESKTOP
///
/// Switching off a television that is currently showing the desktop leaves
/// Windows with nowhere to draw and whoever did it with nothing to look at. Where
/// a safe display layout is configured, this requires it -- which both brings it
/// into the transition and orders it first, because a requirement is also an
/// ordering. See <see cref="LgTvSettings.SafeDisplayTarget"/>.
/// </remarks>
public sealed class TvPowerProvider : IConfigurableProvider, IDependentProvider, IConvergentProvider, IAvailableProvider
{
    /// <summary>The configuration key.</summary>
    public const string ResourceKey = "tvPower";

    public const string On = "On";
    public const string Off = "Off";

    private readonly LgTvConnection _connection;

    public TvPowerProvider(LgTvConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    public string Key => LgTvResourceKeys.Power(_connection.ResourceName);

    public string DisplayName => LgTvResourceKeys.DisplayName(_connection.TelevisionName, "power");

    /// <summary>Which configured television this is, for a screen that lists them.</summary>
    public string? TelevisionName => _connection.TelevisionName;

    /// <summary>The television's settings, for a screen that shows or edits them.</summary>
    public LgTvSettings Settings => _connection.Settings;

    /// <summary>
    /// The shared connection, for a settings screen's "Pair" action. See
    /// <see cref="LgTvConnection.TryPair"/>.
    /// </summary>
    public LgTvConnection Connection => _connection;

    /// <summary>The latest observations made through this television's shared connection.</summary>
    public LgTvObservedState ObservedState => _connection.ObservedState;

    /// <summary>Shared with the input resource. One television, one section.</summary>
    public string SettingsKey => LgTvSettings.SectionKey;

    public bool TryConfigure(JsonElement? settings, out string? problem) =>
        _connection.TryConfigure(settings, out problem);

    /// <summary>
    /// What switching off cannot do without.
    /// </summary>
    /// <remarks>
    /// Only for off, and only when a safe layout is configured. Switching ON must
    /// require nothing: it is the thing everything else about the television
    /// depends on, and giving it a requirement of its own is how a graph acquires
    /// a loop.
    /// </remarks>
    public IReadOnlyList<ResourceRequirement> RequirementsFor(string target) =>
        IsOff(target) && !string.IsNullOrWhiteSpace(_connection.Settings.SafeDisplayTarget)
            ? [new ResourceRequirement(DisplayProvider.ResourceKey, _connection.Settings.SafeDisplayTarget!)]
            : [];

    /// <summary>
    /// Patient going on, and honest going off.
    /// </summary>
    /// <remarks>
    /// A television takes its time waking: the magic packet is sent, and then
    /// nothing happens for several seconds while it starts up and joins the
    /// network. So an attempt is given a long window, the readback is taken
    /// steadily rather than constantly, and it settles briefly because a
    /// television reports itself active a moment before it stops reporting that it
    /// is still changing state. Three attempts, because a magic packet is cheap
    /// and one of them genuinely does get lost.
    ///
    /// Off verifies nothing, for the reason in the class remarks, and instead
    /// waits a few seconds before anything ordered after it may start -- long
    /// enough for the television to have actually dropped the HDMI link rather
    /// than merely having been told to.
    /// </remarks>
    public ConvergencePolicy PolicyFor(string target) => IsOff(target)
        ? new ConvergencePolicy
        {
            Verify = false,
            MaxAttempts = 1,
            PostDispatchDelay = TimeSpan.FromSeconds(3),
        }
        : new ConvergencePolicy
        {
            Verify = true,
            MaxAttempts = 3,
            RetryInterval = TimeSpan.FromSeconds(12),
            PollInterval = TimeSpan.FromSeconds(2),
            Settle = TimeSpan.FromSeconds(1),
        };

    /// <summary>
    /// Whether there is a television to switch at all. See
    /// <see cref="LgTvConnection.Availability"/>.
    /// </summary>
    public ResourceAvailability AvailabilityOf(string target) => _connection.Availability;

    public bool IsValidValue(string value, out string? problem)
    {
        if (IsOn(value) || IsOff(value))
        {
            problem = null;
            return true;
        }

        problem = $"'{value}' is not a television power state. Use {On} or {Off}.";
        return false;
    }

    /// <summary>
    /// Where the television is, in the words a mode can use.
    /// </summary>
    /// <remarks>
    /// SILENCE IS NOT AN OBSERVED POWER STATE
    ///
    /// This is the judgement, and this is the right place for it: the connection
    /// reports what it observed -- see <see cref="LgTvPower"/> -- and the resource
    /// that owns the meaning of "on" and "off" decides what that observation is
    /// worth. Only an answer from the television becomes On or Off. Unpaired,
    /// unreachable, changing, and failed reads remain unknown to convergence and
    /// retain their distinct detail in <see cref="ObservedState"/>.
    /// </remarks>
    public string? Read() =>
        LgTvBridge.Wait(_connection.ReadPowerAsync, LgTvPower.Unknown) switch
        {
            LgTvPower.On => On,
            LgTvPower.Off => Off,
            _ => null,
        };

    public void Apply(string value, CancellationToken cancellationToken)
    {
        if (IsOff(value))
        {
            LgTvBridge.Wait(_connection.TurnOffAsync, cancellationToken);
            return;
        }

        if (!IsOn(value))
        {
            // Unreachable through the engine, which validates first. Kept because
            // a provider that trusted its caller would send a magic packet on the
            // strength of a word nobody recognised.
            throw new ArgumentException($"'{value}' is not a television power state.", nameof(value));
        }

        LgTvBridge.Wait(_connection.WakeAsync, cancellationToken);
    }

    private static bool IsOn(string value) =>
        !string.IsNullOrWhiteSpace(value) && string.Equals(value.Trim(), On, StringComparison.OrdinalIgnoreCase);

    private static bool IsOff(string value) =>
        !string.IsNullOrWhiteSpace(value) && string.Equals(value.Trim(), Off, StringComparison.OrdinalIgnoreCase);
}

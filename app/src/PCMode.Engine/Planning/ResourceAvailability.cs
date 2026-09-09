namespace PCMode.Engine;

/// <summary>Whether a resource can be sent somewhere at all, right now.</summary>
/// <remarks>
/// The distinction that matters is not "yes or no", it is "settled or not". Only
/// a definite no may remove a resource from a transition or refuse one, because
/// only a definite no is still true in a second's time.
/// </remarks>
public enum AvailabilityState
{
    /// <summary>It can be done. The answer a provider gets for saying nothing.</summary>
    Available,

    /// <summary>
    /// Nobody can currently say. A query that failed, a device that has not
    /// finished arriving, a television that has not answered yet.
    /// </summary>
    /// <remarks>
    /// Planned and attempted like anything else. Convergence is precisely the
    /// mechanism for a resource that may be about to work, and treating "I do
    /// not know" as "no" would quietly drop the resources most worth waiting
    /// for -- the HDMI endpoint that appears seconds after the television wakes
    /// is unknown for exactly as long as it takes to become available.
    /// </remarks>
    Unknown,

    /// <summary>
    /// Definitely not there. Hardware that is not installed, a name that resolves
    /// to nothing on this machine.
    /// </summary>
    Unavailable,

    /// <summary>
    /// Definitely there and deliberately off. Switched off by the user or by
    /// policy, rather than absent.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Unavailable"/> because they call for different
    /// things from whoever reads the report: one is a machine that cannot do
    /// this, the other is a machine that has been told not to.
    /// </remarks>
    Disabled,
}

/// <summary>An availability answer, and why.</summary>
/// <param name="Detail">
/// What the provider would want a person to read. Required for a definite no --
/// a resource dropped from a transition with no explanation is indistinguishable
/// from one the planner forgot.
/// </param>
public sealed record ResourceAvailability(AvailabilityState State, string? Detail)
{
    /// <summary>The answer for a provider that has nothing to declare.</summary>
    public static readonly ResourceAvailability Available = new(AvailabilityState.Available, null);

    public static ResourceAvailability Unknown(string? detail) => new(AvailabilityState.Unknown, detail);

    public static ResourceAvailability Unavailable(string detail) => new(AvailabilityState.Unavailable, detail);

    public static ResourceAvailability Disabled(string detail) => new(AvailabilityState.Disabled, detail);

    /// <summary>
    /// Whether this is settled enough to drop a resource or refuse a transition
    /// over.
    /// </summary>
    public bool IsDefinitelyOut => State is AvailabilityState.Unavailable or AvailabilityState.Disabled;

    /// <summary>The reason, in a form that can be put in a sentence.</summary>
    public string Explain() =>
        Detail
        ?? State switch
        {
            AvailabilityState.Unavailable => "it is not available.",
            AvailabilityState.Disabled => "it is switched off.",
            AvailabilityState.Unknown => "it could not be checked.",
            _ => "it is available.",
        };
}

/// <summary>
/// A provider that can say whether a target is possible on this machine at all.
/// </summary>
/// <remarks>
/// DEFAULTED, LIKE THE OTHERS
///
/// A provider implements this only when it can genuinely tell the difference
/// between "not there" and "not there yet". Saying nothing means available, which
/// is what every provider shipped today says: a missing HDMI endpoint, a sleeping
/// monitor and a discovery query that failed are all states this machine passes
/// through on its way to working, and a provider that reported any of them as
/// unavailable would be dropping resources for being slow.
///
/// ASKED EVERY TIME A TRANSITION IS PLANNED
///
/// Never once at load. Whether a television is plugged in is a fact about the
/// room, and the room changes between transitions; an answer cached from startup
/// would be a claim about a machine that has since been rearranged.
///
/// READ-ONLY
///
/// This is asked while deciding what a transition consists of, before the rule
/// that a mode with one unusable resource changes nothing has been satisfied. A
/// provider that changed something here would change it during a transition that
/// may be about to be refused.
/// </remarks>
public interface IAvailableProvider : IModeResourceProvider
{
    /// <summary>Whether <paramref name="target"/> is possible right now.</summary>
    ResourceAvailability AvailabilityOf(string target) => ResourceAvailability.Available;
}

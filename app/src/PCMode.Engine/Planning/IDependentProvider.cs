namespace PCMode.Engine;

/// <summary>Another resource, at a particular target, that one target needs.</summary>
/// <remarks>
/// A requirement is about a value, not about a provider: switching the sound to
/// a television needs that television showing a picture, and switching it to the
/// desk speakers needs nothing at all. A provider that declared its needs once
/// for every target would drag the whole set into every mode.
/// </remarks>
public sealed record ResourceRequirement(string Key, string Target);

/// <summary>
/// A provider whose place in a transition depends on the others.
/// </summary>
/// <remarks>
/// WHY TWO KINDS OF RELATIONSHIP
///
/// They answer different questions, and collapsing them would make one of the
/// answers wrong.
///
/// <see cref="RequirementsFor"/> is "this cannot work without that". It is
/// strong enough to add a resource the mode never mentioned, because a mode that
/// asks for sound over a television it did not switch on is asking for something
/// that cannot happen. Adding it is the only reading of the request that is not
/// a failure.
///
/// <see cref="RunsAfter"/> is "if that is happening too, it happens first". It
/// never adds anything. The cursor wants to be placed after the desktop has
/// finished changing shape, because a corner is a different pixel once a monitor
/// has come or gone -- but a mode that only moves the pointer must not start
/// rearranging monitors to satisfy that preference. The whole difference between
/// the two is what happens when the other resource is absent: a requirement
/// brings it in, an ordering preference lets it go.
///
/// BOTH ARE DEFAULTED
///
/// A provider implements the one it has something to say about. The theme has
/// neither and does not name this interface at all.
/// </remarks>
public interface IDependentProvider : IModeResourceProvider
{
    /// <summary>
    /// What must also be true for <paramref name="target"/> to mean anything.
    /// </summary>
    /// <remarks>
    /// Resolved before the transition dispatches anything. A requirement the
    /// mode omits is added to the plan; one the mode contradicts refuses the
    /// whole transition rather than picking a winner.
    /// </remarks>
    IReadOnlyList<ResourceRequirement> RequirementsFor(string target) => [];

    /// <summary>
    /// Resource keys this must run after, when they are in the same transition.
    /// </summary>
    /// <remarks>
    /// A key naming a resource the mode does not set is ignored, which is the
    /// point: this expresses an order, never a need.
    /// </remarks>
    IReadOnlyList<string> RunsAfter => [];
}

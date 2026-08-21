namespace PCMode.Engine;

/// <summary>
/// A provider that can declare whether a particular target has a trustworthy
/// runtime signal PCMode can read back.
/// </summary>
/// <remarks>
/// This is an intentional capability boundary, not the result of one failed
/// read. Returning false means the resource is open-loop for that target: a
/// successful command may be reported as applied, but never as verified, and
/// recovery must not invent state by repeatedly dispatching it.
///
/// Providers that do not implement this interface keep the engine's existing
/// behavior. That lets resources which use dispatch-only convergence for other
/// reasons remain unchanged while applications can explicitly opt into
/// launch-only semantics.
/// </remarks>
public interface IResourceObservabilityProvider : IModeResourceProvider
{
    /// <summary>Whether PCMode can reliably observe <paramref name="target"/> after applying it.</summary>
    bool CanObserve(string target);
}

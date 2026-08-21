namespace PCMode.Engine;

/// <summary>
/// A provider whose work depends on what the rest of the transition has already
/// done to the machine.
/// </summary>
/// <remarks>
/// WHY BEING TOLD IS DIFFERENT FROM READING
///
/// Every other question a provider is asked is about the machine as it stands:
/// what is the theme now, is that endpoint there, is the television answering.
/// This one is about the transition, and no amount of looking at the machine can
/// answer it. An application whose window opened on a monitor that has since been
/// switched off is running perfectly, on a desktop that no longer exists -- and
/// the only difference between that and the same application started a moment
/// later is which of them happened first.
///
/// So the fact is carried rather than deduced. The engine knows which resources
/// it actually changed, because it changed them; a provider that tried to work it
/// out would be re-reading resources that belong to other providers, which is the
/// coupling the whole boundary exists to prevent.
///
/// TOLD BEFORE EVERY STEP, INCLUDING THE FIRST
///
/// So a provider never has to reset anything, and never carries an answer from
/// one transition into the next. A dry run is told the truth as well, which is
/// that nothing has changed: it dispatches nothing, so nothing has.
///
/// ONLY WHAT WAS CHANGED
///
/// A resource that was found already correct is not in the set. It did not move,
/// so nothing that depended on where it was has been disturbed -- and counting it
/// would make every re-assert of a mode restart every application in it.
/// </remarks>
public interface ITransitionAwareProvider : IModeResourceProvider
{
    /// <summary>
    /// The resources this transition has already changed, told immediately before
    /// this one is read and dispatched.
    /// </summary>
    /// <param name="changed">
    /// Resource keys, compared case-insensitively. Empty at the first step of
    /// every transition.
    /// </param>
    void Preceded(IReadOnlySet<string> changed);
}

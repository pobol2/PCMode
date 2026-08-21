namespace PCMode.Engine;

/// <summary>
/// How hard the engine works to get one resource to one target, and how it
/// decides that it got there.
/// </summary>
/// <remarks>
/// WHY A SUCCESSFUL CALL IS NOT AN ANSWER
///
/// Every provider here commands something that is not finished when the command
/// returns. Windows accepts a display topology and then spends seconds bringing
/// monitors up; Core Audio accepts a default endpoint that exists only while a
/// television is awake; the theme is written and then broadcast to every window
/// on the desktop. A transition that treated the return of <c>Apply</c> as proof
/// would report success at the moment it had the least evidence for it.
///
/// So a verifiable resource is believed only when it can be read back, and a
/// resource with nothing worth reading back says so rather than being retried at
/// random in the hope that something changes.
///
/// THE DEFAULT IS TODAY'S BEHAVIOUR
///
/// <see cref="DispatchOnce"/> -- one attempt, no readback, no waiting -- is what
/// a provider gets for saying nothing, and it is exactly what the engine did
/// before convergence existed. Nothing already written has to opt out of
/// anything.
///
/// RETRYINTERVAL DOES TWO JOBS, AND THEY ARE THE SAME JOB
///
/// It is how long one attempt is given before another is made. When a dispatch
/// throws, that is a pause before trying again. When a dispatch succeeds and the
/// resource is verifiable, that is how long the readback is given to arrive.
/// Both are the same question -- how long is this worth waiting for? -- and
/// splitting them into two numbers would mean two ways to say a resource is
/// slow, which have to be kept in agreement by hand.
///
/// SETTLING AND PROPAGATING ARE NOT THE SAME THING
///
/// <see cref="Settle"/> is evidence: a readback that agreed without interruption
/// for that long. <see cref="PostDispatchDelay"/> is not evidence at all -- it is
/// an admission that a command takes a moment to spread and that nothing here can
/// watch it spreading. They are separate knobs because they are separate claims,
/// and a single one would let a blind pause be reported as though something had
/// confirmed it.
/// </remarks>
public sealed record ConvergencePolicy
{
    /// <summary>
    /// One attempt, believed on the strength of the call returning.
    /// </summary>
    /// <remarks>
    /// For a resource with no meaningful readback. Not a weaker version of
    /// verification: repeating a command because nothing can confirm it would
    /// mean repeating it forever, since nothing will ever confirm it.
    /// </remarks>
    public static readonly ConvergencePolicy DispatchOnce = new();

    /// <summary>Whether the readback has to agree before this counts as done.</summary>
    public bool Verify { get; init; }

    /// <summary>How many times the command may be issued. At least one.</summary>
    public int MaxAttempts { get; init; } = 1;

    /// <summary>How long one attempt is given before another is made.</summary>
    public TimeSpan RetryInterval { get; init; }

    /// <summary>How often the readback is taken while waiting for it.</summary>
    public TimeSpan PollInterval { get; init; }

    /// <summary>
    /// How long the readback must agree, without interruption, before the
    /// resource is finished and whatever follows it may start.
    /// </summary>
    /// <remarks>
    /// A display that reports the wanted layout, then reports something else a
    /// moment later as the monitors finish negotiating, never reached the
    /// layout: it passed through it. Anything ordered after such a resource --
    /// the pointer, the sound -- would be acting on a desktop that is still
    /// moving, which is the whole reason it was ordered afterwards.
    /// </remarks>
    public TimeSpan Settle { get; init; }

    /// <summary>
    /// How long to leave a resource with no readback alone after commanding it,
    /// before anything ordered after it may start.
    /// </summary>
    /// <remarks>
    /// For a command that is known to take a moment to spread and that nothing
    /// can watch spreading. It proves nothing, and is deliberately awkward to
    /// reach for: only a resource that does not verify may have one, because on
    /// a resource that does verify it would be a fixed wait pretending to be a
    /// measurement the readback is already making properly.
    ///
    /// Zero for everything shipped today. The pointer is moved and is then the
    /// user's again, and a delay after moving it would be the transition sitting
    /// still for no reason anybody could name.
    /// </remarks>
    public TimeSpan PostDispatchDelay { get; init; }

    /// <summary>
    /// Whether the command is issued even when the readback already reports the
    /// target.
    /// </summary>
    /// <remarks>
    /// For a resource whose readback answers a narrower question than the command
    /// asks. A television reports which input it is showing, and reports it
    /// correctly, while the picture settings that belong to that input have been
    /// changed by something else -- so "it is already on HDMI 1" is true and is
    /// nonetheless not a reason to skip the command.
    ///
    /// Exactly one dispatch is owed to this flag. It suppresses the shortcut that
    /// would have returned early, and then nothing further: the attempt it forces
    /// is verified, settled, retried and bounded like any other, and a resource
    /// that was genuinely already correct confirms on the first readback rather
    /// than being commanded repeatedly for the same reason.
    ///
    /// False for everything shipped today. It is a statement that a readback is
    /// known to be incomplete, and a provider whose readback is complete would be
    /// asserting something untrue about itself by setting it.
    /// </remarks>
    public bool DispatchAtLeastOnce { get; init; }

    /// <summary>
    /// Whether this policy describes something that can actually happen, and if
    /// not, what is wrong with it.
    /// </summary>
    /// <remarks>
    /// Asked for every resource of a transition before the first one is
    /// dispatched. A policy that cannot converge -- verification with no poll,
    /// a retry with no pause -- would otherwise be discovered by a transition
    /// hanging or spinning, and by then the machine is already half moved.
    /// </remarks>
    public bool IsUsable(out string? problem)
    {
        if (MaxAttempts < 1)
        {
            problem = "it allows no attempt at all.";
            return false;
        }

        if (RetryInterval < TimeSpan.Zero ||
            PollInterval < TimeSpan.Zero ||
            Settle < TimeSpan.Zero ||
            PostDispatchDelay < TimeSpan.Zero)
        {
            problem = "it asks to wait for a negative length of time.";
            return false;
        }

        if (MaxAttempts > 1 && RetryInterval <= TimeSpan.Zero)
        {
            // Repeating the same call in the same instant is not a retry. It
            // gives whatever failed no opportunity to have stopped failing.
            problem = "it retries without leaving any time between attempts.";
            return false;
        }

        if (!Verify)
        {
            if (Settle > TimeSpan.Zero)
            {
                // Settling is a statement about a readback holding still. With
                // nothing to read, it would be a blind sleep dressed up as
                // evidence.
                problem = "it waits for a value to settle but never reads one.";
                return false;
            }

            problem = null;
            return true;
        }

        if (PostDispatchDelay > TimeSpan.Zero)
        {
            // Waiting a fixed time and then measuring is not two pieces of
            // evidence, it is one measurement that started late. Whatever the
            // delay was for, the poll interval and the attempt window already
            // express it, and honestly.
            problem = "it waits a fixed time after dispatching something it is going to read back anyway.";
            return false;
        }

        if (PollInterval <= TimeSpan.Zero)
        {
            problem = "it verifies by readback but never says how often to read.";
            return false;
        }

        if (RetryInterval <= TimeSpan.Zero)
        {
            problem = "it verifies by readback but gives the machine no time to get there.";
            return false;
        }

        if (PollInterval > RetryInterval)
        {
            problem = "it gives up on an attempt before it would read it back once.";
            return false;
        }

        problem = null;
        return true;
    }
}

/// <summary>
/// A provider that knows how long it takes and how to tell that it worked.
/// </summary>
/// <remarks>
/// Defaulted, like <see cref="IDependentProvider"/>, so a provider implements it
/// only when it has something to say. Keyed by target for the same reason
/// requirements are: what a resource costs depends on where it is going. Sending
/// sound to the desk speakers is instant and sending it to a television waits for
/// that television's endpoint to appear, and one number for both would either
/// give up too early or make every mode feel slow.
/// </remarks>
public interface IConvergentProvider : IModeResourceProvider
{
    /// <summary>
    /// What reaching <paramref name="target"/> is expected to take.
    /// </summary>
    /// <remarks>
    /// Resolved while the transition is being planned, so a policy that cannot
    /// converge -- or a provider that throws when asked for one -- refuses the
    /// transition with the machine untouched, rather than stalling it half way.
    /// </remarks>
    ConvergencePolicy PolicyFor(string target) => ConvergencePolicy.DispatchOnce;
}

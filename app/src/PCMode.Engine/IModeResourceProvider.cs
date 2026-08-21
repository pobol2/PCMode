namespace PCMode.Engine;

/// <summary>
/// One thing about the machine that a mode can set.
/// </summary>
/// <remarks>
/// Deliberately small. A provider validates a desired value, reads what the
/// machine is at now, and puts it somewhere else -- nothing more. Dependencies
/// between resources, retries and rollback are engine concerns and stay out of
/// this interface.
///
/// Values are strings because that is what the configuration holds. Turning
/// "Dark" into whatever the underlying API wants is the provider's business,
/// and keeping it there is what stops the engine from growing a switch over
/// resource kinds.
/// </remarks>
public interface IModeResourceProvider
{
    /// <summary>
    /// The configuration key this provider answers to, for example "theme".
    /// </summary>
    /// <remarks>
    /// Compared case-insensitively, and unique across providers: it is how a
    /// mode's resources are matched to the code that applies them, and how an
    /// unknown key in the configuration is refused before anything is changed.
    /// </remarks>
    string Key { get; }

    /// <summary>What to call this in a status line.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether <paramref name="value"/> is something this provider can apply,
    /// and if not, what is wrong with it.
    /// </summary>
    /// <remarks>
    /// Asked for every resource of every mode when the configuration is loaded,
    /// and again for the requested mode before a transition dispatches
    /// anything. Validation that only happens at load time would let a mode
    /// half-apply after somebody edited the file while the shell was running.
    /// </remarks>
    bool IsValidValue(string value, out string? problem);

    /// <summary>
    /// The machine's current value, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Null is an ordinary answer, not a failure: it means "apply it anyway".
    /// A provider that cannot read is still allowed to write.
    /// </remarks>
    string? Read();

    /// <summary>
    /// Puts the machine at <paramref name="value"/>.
    /// </summary>
    /// <remarks>
    /// Throws on failure, and the exception's message is shown to the user.
    /// Called only after <see cref="IsValidValue"/> has accepted the value and
    /// only when <see cref="Read"/> did not already report it.
    /// </remarks>
    void Apply(string value, CancellationToken cancellationToken);
}

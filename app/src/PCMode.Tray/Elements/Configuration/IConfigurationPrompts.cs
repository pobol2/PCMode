using PCMode.Tray.Elements;

namespace PCMode.Tray.Elements.Configuration;

/// <summary>
/// The modal interactions a configuration ViewModel needs but a Functional
/// Block cannot provide: naming something, confirming a destructive action,
/// and the "you have unsaved changes" guard.
/// </summary>
/// <remarks>
/// A block is lookless -- it owns no visual tree and no code-behind -- so it
/// cannot open a <see cref="System.Windows.Window"/>. Rather than pushing
/// these back into a page-shaped control, the ViewModel owns the whole flow
/// and reaches a dialog through this. The WPF implementation lives in the
/// Tray's composition layer; a test substitutes a scripted one, which is what
/// makes the unsaved-changes guard testable at all -- it was not, while it
/// lived inside the old page shell's own click handlers.
/// </remarks>
public interface IConfigurationPrompts
{
    /// <summary>
    /// Asks for a name, then hands it to <paramref name="apply"/>, keeping the
    /// dialog open and showing the returned problem until it returns null.
    /// </summary>
    void PromptForName(string title, string message, string initialValue, Func<string, string?> apply);

    /// <summary>Asks the same question asynchronously, for an <paramref name="apply"/> that has real work to do.</summary>
    void PromptForNameAsync(string title, string message, string initialValue, Func<string, Task<string?>> apply);

    /// <summary>A yes/no confirmation before something destructive. False means the caller must do nothing.</summary>
    bool Confirm(string message);

    /// <summary>
    /// The shared "this has unsaved changes -- save them first?" guard, run
    /// before anything that would otherwise discard an in-progress edit.
    /// Saves, discards, or cancels according to the answer; false means the
    /// caller must abandon what it was about to do.
    /// </summary>
    bool ConfirmDiscardIfDirty(IUnsavedChanges source);
}

/// <summary>
/// What <see cref="IConfigurationPrompts.ConfirmDiscardIfDirty"/> needs from
/// whatever it is guarding -- deliberately narrower than a whole ViewModel, so
/// the guard has no opinion about what is being edited.
/// </summary>
public interface IUnsavedChanges
{
    bool HasUnsavedChanges { get; }

    /// <summary>Attempts a save; false when it could not be saved.</summary>
    bool TrySaveChanges();

    void DiscardChanges();
}

/// <summary>
/// Answers every prompt the way an unattended caller must be treated: no name
/// is ever supplied, nothing destructive is confirmed, and the unsaved-changes
/// guard refuses to discard.
/// </summary>
/// <remarks>
/// The default when no real implementation was supplied (a ViewModel
/// constructed outside a hosted process). Refusing is the safe answer: a
/// caller that cannot ask must not silently destroy an edit.
/// </remarks>
public sealed class NoConfigurationPrompts : IConfigurationPrompts
{
    public static NoConfigurationPrompts Instance { get; } = new();

    public void PromptForName(string title, string message, string initialValue, Func<string, string?> apply) { }

    public void PromptForNameAsync(
        string title,
        string message,
        string initialValue,
        Func<string, Task<string?>> apply
    ) { }

    public bool Confirm(string message) => false;

    public bool ConfirmDiscardIfDirty(IUnsavedChanges source) => source is null || !source.HasUnsavedChanges;
}

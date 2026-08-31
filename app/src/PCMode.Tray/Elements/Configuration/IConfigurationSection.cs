using PCMode.Tray.Elements;

namespace PCMode.Tray.Elements.Configuration;

/// <summary>
/// One independently maintained facet of a page-level <see cref="ConfigurationPageViewModelBase"/>'s
/// selected entity -- e.g. a Displays page's Layout or Cursor section for
/// whichever display is selected.
/// </summary>
/// <remarks>
/// A section owns its own fields, validation, and store-level save exactly
/// like a full feature ViewModel does; it just does not own a selector, a
/// Save/Discard footer, or dirty-tracking bookkeeping of its own -- the
/// composing page ViewModel owns all three, once, on behalf of every section.
/// A section reports an edit to the page through the same injected
/// <c>Action onChanged</c> callback shape <see cref="ObservableEditableRow"/>
/// already uses for a row reporting to its owning ViewModel, one level up:
/// the page hands each section a callback that calls its own
/// <see cref="ConfigurationPageViewModelBase.MarkDirty"/> (already a no-op
/// under <see cref="ConfigurationPageViewModelBase.RunWithoutDirtyTracking"/>,
/// so loading a section on selection change never marks the page dirty).
/// </remarks>
public interface IConfigurationSection
{
    /// <summary>
    /// Loads this section's fields for <paramref name="entityKey"/>, replacing
    /// whatever was loaded before. Null means nothing is selected -- the same
    /// case <see cref="ConfigurationPageViewModelBase.SelectedKey"/> itself
    /// allows -- and a section clears itself to an empty/disabled state rather
    /// than refusing the call. Called by the page ViewModel inside
    /// <see cref="ConfigurationPageViewModelBase.RunWithoutDirtyTracking"/>, so
    /// a section's own property setters do not need to suppress dirty
    /// tracking themselves during a load.
    /// </summary>
    void Load(object? entityKey);

    /// <summary>
    /// Persists this section's current fields. Returns the problem to show
    /// the user, or null on success. Called for every section on the page's
    /// Save, in the page's declared order -- a section whose fields have not
    /// changed since <see cref="Load"/> is expected to save cleanly as a
    /// no-op rather than require its own dirty check.
    /// </summary>
    string? Save();

    /// <summary>
    /// Why this section's current fields could not be saved, or null when
    /// they could. Checked for every section before any section's
    /// <see cref="Save"/> is called, so a page never partially saves when one
    /// section is already known to be invalid.
    /// </summary>
    string? ValidationProblem => null;
}

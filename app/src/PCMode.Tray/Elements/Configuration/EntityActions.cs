using PCMode.Tray.Elements;

namespace PCMode.Tray.Elements.Configuration;

/// <summary>
/// The Create/Rename/Duplicate/Delete/Activate surface for one configuration
/// page's selected entity. A page's <see cref="ConfigurationPageViewModelBase.Actions"/>
/// returns a fresh instance whenever asked, so <see cref="Blocks.ConfigurationPageBlock"/>
/// always reads current selection/gating state; the shell owns the dialog
/// wording, the unsaved-changes guard, and which standard actions are shown,
/// so a page supplies only what a name means to it and what each operation
/// does -- never a click handler.
/// </summary>
/// <remarks>
/// A null member hides that action entirely (no Create button, no Rename
/// entry, and so on) rather than showing it disabled -- the same
/// "unsupported" contract <see cref="IHardwareInventoryRow.ShowActivate"/>
/// already uses.
/// </remarks>
public sealed class EntityActions
{
    /// <summary>The lowercase singular noun used in generated dialog copy -- "sample", "layout", "scene".</summary>
    public required string EntityNoun { get; init; }

    /// <summary>The selected entity's current display name, for Rename/Duplicate/Delete dialog defaults and messages.</summary>
    public string? CurrentDisplayName { get; init; }

    /// <summary>Creates a new entity named by the argument. Returns the problem to show in the dialog, or null on success.</summary>
    public Func<string, string?>? Create { get; init; }

    /// <summary>Renames the selected entity. Returns the problem to show in the dialog, or null on success.</summary>
    public Func<string, string?>? Rename { get; init; }

    /// <summary>Creates a copy of the selected entity named by the argument. Returns the problem to show in the dialog, or null on success.</summary>
    public Func<string, string?>? Duplicate { get; init; }

    /// <summary>Deletes the selected entity, after the shell's own Yes/No confirmation.</summary>
    public Action? Delete { get; init; }

    /// <summary>
    /// Runs a full activation of the selected entity. The page's own
    /// implementation reports its own status/error through the base VM (see
    /// <see cref="ConfigurationPageViewModelBase.RunActivationAsync"/>); the
    /// shell awaits it but reads nothing back from it directly.
    /// </summary>
    public Func<Task>? Activate { get; init; }

    /// <summary>The primary-action button's label while <see cref="Activate"/> is set. Defaults to "Activate".</summary>
    public string ActivateLabel { get; init; } = "Activate";

    /// <summary>Whether <see cref="Activate"/> can be used right now (selection, dirty, and busy state already folded in).</summary>
    public bool CanActivateNow { get; init; }
}

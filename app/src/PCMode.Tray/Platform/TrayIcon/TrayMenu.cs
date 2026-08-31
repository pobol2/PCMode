using System.Windows.Forms;
using PCMode.Engine;

namespace PCMode.Tray.Platform.TrayIcon;

/// <summary>
/// What the tray menu is built from, read directly off the running engine --
/// Target has no <c>ShellViewModel</c>-equivalent status surface yet, so
/// there is nothing between <see cref="PCMode.Engine.ModeEngine"/> and the
/// menu.
/// </summary>
/// <param name="Targets">Every Space, Activity and Source, in configuration order.</param>
/// <param name="CurrentKey">
/// The recorded target's key, or null when nothing is recorded. The key rather
/// than the name, because two targets may read alike and can never share a key.
/// </param>
/// <param name="CurrentLabel">What to call the recorded target, or "Unknown".</param>
/// <param name="CanShowPicker">Whether the picker started, so "Show Picker" has something to show.</param>
public sealed record TrayMenuState(
    IReadOnlyList<ConfiguredTarget> Targets,
    string? CurrentKey,
    string CurrentLabel,
    bool IsIdle,
    bool CanShowPicker);

public sealed record TrayMenuActions(
    Func<string, Task> ApplyTarget,
    Action ShowPicker,
    Action OpenPCMode,
    Func<Task> Exit);

/// <summary>
/// Builds the native tray menu from the running engine's targets, without
/// owning hardware or windows.
/// </summary>
/// <remarks>
/// One item per target, grouped under the Space it belongs to. Ported from
/// legacy PCMode.Tray's <c>TrayMenuController</c>, with its state read
/// straight from <see cref="ModeEngine"/> instead of a <c>ShellViewModel</c>.
/// </remarks>
public static class TrayMenu
{
    public static void Rebuild(ContextMenuStrip menu, TrayMenuState state, TrayMenuActions actions)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(actions);

        ToolStripItem[] discarded = [.. menu.Items.Cast<ToolStripItem>()];
        menu.Items.Clear();

        foreach (ToolStripItem item in discarded)
        {
            item.Dispose();
        }

        menu.Items.Add(new ToolStripMenuItem($"Current: {state.CurrentLabel}") { Enabled = false });

        if (state.Targets.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
        }

        foreach (IGrouping<string, ConfiguredTarget> space in state.Targets.GroupBy(SpaceOf))
        {
            var group = new ToolStripMenuItem(space.Key)
            {
                Enabled = state.IsIdle && space.Any(target => target.Enabled),
            };

            foreach (ConfiguredTarget target in space)
            {
                var item = new ToolStripMenuItem(LabelOf(target))
                {
                    Checked = string.Equals(target.Key, state.CurrentKey, StringComparison.OrdinalIgnoreCase),
                    Enabled = state.IsIdle && target.Enabled,
                    ToolTipText = target.Enabled
                        ? target.DisplayPath
                        : $"{target.DisplayPath} (disabled in Spaces)",
                };

                string captured = target.Key;
                item.Click += async (_, _) => await actions.ApplyTarget(captured).ConfigureAwait(true);
                group.DropDownItems.Add(item);
            }

            // Ticked when something inside it is, so the current Space is
            // visible without opening every submenu.
            group.Checked = space.Any(target =>
                string.Equals(target.Key, state.CurrentKey, StringComparison.OrdinalIgnoreCase));

            menu.Items.Add(group);
        }

        if (state.Targets.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
        }

        if (state.CanShowPicker)
        {
            var picker = new ToolStripMenuItem("Show Picker") { Enabled = state.IsIdle };
            picker.Click += (_, _) => actions.ShowPicker();
            menu.Items.Add(picker);
        }

        var show = new ToolStripMenuItem("Open PCMode");
        show.Click += (_, _) => actions.OpenPCMode();
        menu.Items.Add(show);

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += async (_, _) => await actions.Exit().ConfigureAwait(true);
        menu.Items.Add(exit);
    }

    /// <summary>Which submenu a target belongs under.</summary>
    private static string SpaceOf(ConfiguredTarget target) => target.SpaceName ?? target.DisplayPath;

    /// <summary>
    /// What an item is called inside its Space: the Activity, or the Source with
    /// the Activity it belongs to, so a Source is never a bare word with no
    /// context.
    /// </summary>
    private static string LabelOf(ConfiguredTarget target) => target switch
    {
        { SourceName: not null } => $"{target.ActivityName} — {target.SourceName}",
        _ => target.ActivityName!,
    };

    /// <summary>The tray icon's tooltip text, capped to what Windows allows.</summary>
    public static string StatusText(string currentLabel)
    {
        string status = $"PCMode — {currentLabel}";
        return status.Length <= 63 ? status : status[..60] + "...";
    }
}

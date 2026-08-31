using System.Collections.ObjectModel;

namespace PCMode.Tray.Elements;

/// <summary>
/// One row whose standard card title selects an item from a pool.
/// </summary>
/// <remarks>
/// Rows may keep domain-named aliases such as <c>Output</c> or
/// <c>MonitorName</c>. The shared editor binds these generic members so pages
/// do not have to describe their row shape with property-path strings.
/// </remarks>
public interface ISelectionRow
{
    /// <summary>Which pool item this row currently is.</summary>
    string? Selection { get; set; }

    /// <summary>
    /// What this row's picker offers. <see cref="SelectionList{TRow}"/> uses
    /// a per-row collection that excludes every other row's choice; simpler
    /// owners may expose one shared collection when that is their UI behavior.
    /// </summary>
    ObservableCollection<string> AvailableSelections { get; }
}

using System.Runtime.CompilerServices;

namespace PCMode.Tray.Elements;

/// <summary>
/// An <see cref="ObservableObject"/> row that reports every change to its
/// owning ViewModel through one <see cref="Action"/>, in addition to raising
/// PropertyChanged for WPF bindings -- the shape shared by every plain
/// editable collection row (Applications' requirement rows, Television's
/// input/setting/profile rows, Rgb's LED/device/scene rows) so the owner can
/// mark itself dirty, recompute derived state, or both, after any field
/// changes.
/// </summary>
/// <remarks>
/// Layouts' <c>LayoutMonitorRow</c> is not built on this: its owner needs to
/// know which row and which property changed (<c>Action&lt;LayoutMonitorRow,
/// string&gt;</c>), a shape only that one row uses. Generalizing a second,
/// generic base for a single occurrence would be machinery ahead of
/// demonstrated need (Main Dev Guide rule 5); it keeps its own callback.
/// </remarks>
public abstract class ObservableEditableRow : ObservableObject
{
    private readonly Action _onChanged;

    protected ObservableEditableRow(Action onChanged)
    {
        _onChanged = onChanged;
    }

    /// <summary>Sets a field and, if it changed, notifies both bindings and the owner.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        _onChanged();
        return true;
    }

    /// <summary>
    /// Reports a change to the owner directly -- for a setter that raises its
    /// own extra PropertyChanged notifications and needs the owner notified
    /// only after those, not immediately when <see cref="Set{T}"/> would.
    /// </summary>
    protected void NotifyChanged() => _onChanged();
}

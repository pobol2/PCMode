using System.Collections.ObjectModel;

namespace PCMode.Tray.Elements;

/// <summary>
/// A short, variable-length list of hardware or resources the user picked from
/// a shared pool -- the shape a configuration page's primary editor uses when
/// what it configures is "which of these, and how is each set up".
/// </summary>
/// <remarks>
/// THE SHARED CONVENTION
///
/// Every configuration page that selects items from a pool presents them the
/// same way: one <c>ConfigurationItemCard</c> per selected item, a picker in
/// the card header choosing which pool item it is, Remove in the header's
/// action slot, that item's own settings in the card body, and one
/// "Add another ..." action below the collection. This class owns the state
/// behind that: the pool, the chosen rows, and what each row's picker may
/// still offer.
///
/// DUPLICATE PREVENTION IS STRUCTURAL
///
/// <see cref="RefreshAvailableSelections"/> gives each row the pool minus every
/// other row's choice, so the same item cannot be picked twice at all --
/// rather than being caught after the fact by a page's validation. A row always
/// keeps its own current choice in its list, or selecting it would immediately
/// remove it from the options that justify the selection.
/// </remarks>
public sealed class SelectionList<TRow>
    where TRow : class, ISelectionRow
{
    private readonly Func<string?, TRow> _createRow;
    private readonly Action _onChanged;
    private readonly int _maximumRows;
    private readonly int _minimumRows;

    /// <param name="createRow">
    /// Builds a row for a chosen pool item. The owner supplies this because a
    /// row's other fields -- and the change callbacks wiring it back to the
    /// page -- are the owner's business, not this list's.
    /// </param>
    /// <param name="onChanged">
    /// Called after an Add or Remove actually changed the list, so the owning
    /// page can mark itself dirty and recompute whatever derives from
    /// membership. Loading (<see cref="Load"/>) deliberately does not call it.
    /// </param>
    /// <param name="maximumRows">A cap the hardware itself imposes, if any.</param>
    /// <param name="minimumRows">
    /// How many rows must remain. Use 0 where an empty list is a legitimate
    /// starting state that the page's own validation then refuses to save.
    /// </param>
    public SelectionList(
        Func<string?, TRow> createRow,
        Action onChanged,
        int maximumRows = int.MaxValue,
        int minimumRows = 0)
    {
        ArgumentNullException.ThrowIfNull(createRow);
        ArgumentNullException.ThrowIfNull(onChanged);

        _createRow = createRow;
        _onChanged = onChanged;
        _maximumRows = maximumRows;
        _minimumRows = minimumRows;
    }

    /// <summary>Everything that could be picked, whether or not it already was.</summary>
    public ObservableCollection<string> Choices { get; } = [];

    /// <summary>What the user actually picked, in the order shown.</summary>
    public ObservableCollection<TRow> Rows { get; } = [];

    /// <summary>Whether the cap and the remaining pool both still allow another row.</summary>
    public bool CanAdd => Rows.Count < _maximumRows && Choices.Count > Rows.Count;

    public bool CanRemove => Rows.Count > _minimumRows;

    /// <summary>Replaces the pool in place, then re-offers it to every row.</summary>
    public void SetChoices(IReadOnlyList<string> desired)
    {
        ArgumentNullException.ThrowIfNull(desired);

        Reconcile(Choices, desired);
        RefreshAvailableSelections();
    }

    /// <summary>Adds a row defaulted to the first pool item nothing else uses.</summary>
    public TRow? Add()
    {
        if (!CanAdd)
        {
            return null;
        }

        TRow row = _createRow(FirstUnusedChoice());
        Rows.Add(row);

        RefreshAvailableSelections();
        _onChanged();

        return row;
    }

    public bool Remove(TRow row)
    {
        if (row is null || !CanRemove || !Rows.Remove(row))
        {
            return false;
        }

        RefreshAvailableSelections();
        _onChanged();

        return true;
    }

    /// <summary>
    /// Rebuilds the rows from saved state. Silent by design: a load is not an
    /// edit, so it never reports a change.
    /// </summary>
    public void Load(IEnumerable<string?> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);

        Rows.Clear();

        foreach (string? selection in selections)
        {
            Rows.Add(_createRow(selection));
        }

        RefreshAvailableSelections();
    }

    public void Clear()
    {
        Rows.Clear();
        RefreshAvailableSelections();
    }

    /// <summary>
    /// Re-offers the pool to every row, each minus the other rows' choices.
    /// Call after anything that changes what is taken -- including a row's own
    /// choice changing, which this list cannot observe on its own.
    /// </summary>
    public void RefreshAvailableSelections()
    {
        foreach (TRow row in Rows)
        {
            List<string> available =
            [
                .. Choices.Where(choice =>
                    string.Equals(choice, row.Selection, StringComparison.OrdinalIgnoreCase) ||
                    !Rows.Any(other =>
                        !ReferenceEquals(other, row) &&
                        string.Equals(other.Selection, choice, StringComparison.OrdinalIgnoreCase)))
            ];

            Reconcile(row.AvailableSelections, available);
        }
    }

    private string? FirstUnusedChoice() =>
        Choices.FirstOrDefault(choice =>
            !Rows.Any(row => string.Equals(row.Selection, choice, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Patches <paramref name="target"/> in place to hold exactly
    /// <paramref name="desired"/>, in that order -- adding what is missing,
    /// removing what is gone, and moving what only changed position.
    /// </summary>
    /// <remarks>
    /// Both collections this patches are bound, live, as a <c>ComboBox</c>'s
    /// <c>ItemsSource</c> while that same <c>ComboBox</c>'s <c>SelectedItem</c>
    /// is bound TwoWay to the row's own choice.
    /// <see cref="ObservableCollection{T}.Clear"/> followed by re-adding the
    /// same values -- correct as a snapshot, but not as a sequence of change
    /// notifications -- leaves a moment where the selected value is absent from
    /// the list. WPF reacts to that moment, not to the eventual re-add: it nulls
    /// the selection and pushes that null back through the TwoWay binding,
    /// blanking the field and marking the page dirty. Never removing an item
    /// that is still wanted -- only adding, removing what is truly gone, and
    /// reordering via <see cref="ObservableCollection{T}.Move"/> -- means the
    /// selected value's presence is never interrupted.
    /// </remarks>
    private static void Reconcile(ObservableCollection<string> target, IReadOnlyList<string> desired)
    {
        for (int index = target.Count - 1; index >= 0; index--)
        {
            if (!desired.Contains(target[index], StringComparer.OrdinalIgnoreCase))
            {
                target.RemoveAt(index);
            }
        }

        for (int index = 0; index < desired.Count; index++)
        {
            string wanted = desired[index];
            int currentIndex = IndexOf(target, wanted);

            if (currentIndex < 0)
            {
                target.Insert(Math.Min(index, target.Count), wanted);
            }
            else if (currentIndex != index)
            {
                target.Move(currentIndex, index);
            }
        }
    }

    private static int IndexOf(ObservableCollection<string> target, string value)
    {
        for (int index = 0; index < target.Count; index++)
        {
            if (string.Equals(target[index], value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}

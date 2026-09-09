using System.Windows;
using System.Windows.Controls;

namespace PCMode.Tray.UI.Framework;

/// <summary>
/// A repeated-child region: one named slot in a Layout, holding a sequence of
/// children rather than a single one.
/// </summary>
/// <remarks>
/// This is the same concept as <see cref="Region"/> at a different arity, not
/// a second mechanism. Each child still resolves exactly the same way --
/// terminal content, or a view model that an implicit
/// <see cref="DataTemplate"/> turns into the next block down.
///
/// It is a separate type only because WPF makes the arity a type distinction:
/// a Layout binds <see cref="ContentControl.Content"/> on one and
/// <see cref="ItemsControl.ItemsSource"/> on the other. Having both lets a
/// block state which arity it needs and have that enforced, instead of
/// discovering at runtime that a layout supplied a slot which can only hold
/// one child.
/// </remarks>
public class RepeatedRegion : ItemsControl
{
    private RegionAttribute? _contract;

    static RepeatedRegion()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(RepeatedRegion),
            new FrameworkPropertyMetadata(typeof(RepeatedRegion))
        );
    }

    /// <summary>The contract this slot was matched to, once its block applied its template.</summary>
    public RegionAttribute? Contract => _contract;

    internal void Bind(RegionAttribute contract) => _contract = contract;

    protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
    {
        System.Windows.Size result = base.MeasureOverride(constraint);

        if (_contract is not null && Items.Count != 0)
        {
            RegionRuntime.Validate(this, _contract);
        }

        return result;
    }
}

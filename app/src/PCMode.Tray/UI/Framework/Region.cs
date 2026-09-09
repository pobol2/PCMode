using System.Windows;
using System.Windows.Controls;

namespace PCMode.Tray.UI.Framework;

/// <summary>
/// A single-child region: one named slot in a Layout, holding exactly one
/// piece of content.
/// </summary>
/// <remarks>
/// That content is either terminal -- a string, a <see cref="TextBlock"/>, a
/// button -- or a view model that an implicit <see cref="DataTemplate"/>
/// resolves into the next <see cref="FunctionalBlock"/> down. The region is
/// indifferent to which, and that indifference is what makes the tree
/// recursive.
///
/// A region is identified by the name the Layout gives it, which is the same
/// name the block declares in its <see cref="RegionAttribute"/>. There is
/// deliberately no second identity property: one name, declared in one place,
/// checked in one place.
/// </remarks>
public class Region : ContentControl
{
    private RegionAttribute? _contract;

    static Region()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Region), new FrameworkPropertyMetadata(typeof(Region)));
    }

    /// <summary>The contract this slot was matched to, once its block applied its template.</summary>
    public RegionAttribute? Contract => _contract;

    internal void Bind(RegionAttribute contract) => _contract = contract;

    protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
    {
        System.Windows.Size result = base.MeasureOverride(constraint);

        if (_contract is not null && Content is not null)
        {
            RegionRuntime.Validate(this, _contract);
        }

        return result;
    }
}

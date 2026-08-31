using System.Collections;
using System.Windows;
using System.Windows.Input;
using DependencyPropertyGenerator;
using PCMode.Tray.UI.Framework;

namespace PCMode.Tray.UI.Tree;

/// <summary>The generic selector card of a configuration page.</summary>
[Region(HeaderRegion, Terminal = true)]
[Region(ItemsRegion, Arity = RegionArity.Repeated, Terminal = true)]
[DependencyProperty<string>("Title", DefaultValue = "")]
[DependencyProperty<IEnumerable>("Items")]
[DependencyProperty<DataTemplate>("ItemTemplate")]
[DependencyProperty<ICommand>("CreateCommand")]
[DependencyProperty<bool>("ShowCreateButton", DefaultValue = true)]
public partial class ConfigurationSelectorBlock : FunctionalBlock
{
    public const string ItemsRegion = "PART_ItemsRegion";
    public const string HeaderRegion = "PART_HeaderRegion";
}

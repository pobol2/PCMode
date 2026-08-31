using System.Windows.Input;
using DependencyPropertyGenerator;
using PCMode.Tray.UI.Framework;

namespace PCMode.Tray.UI.Tree;

/// <summary>The generic selector/editor configuration-page structure.</summary>
[Region(HeaderRegion, Terminal = true)]
[Region(SelectorRegion, typeof(ConfigurationSelectorBlock))]
[Region(EditorRegion, typeof(ConfigurationEditorBlock))]
[Region(FooterRegion, Terminal = true)]
[DependencyProperty<string>("Title", DefaultValue = "")]
[DependencyProperty<object>("Selector")]
[DependencyProperty<object>("Editor")]
[DependencyProperty<string>("ValidationProblem", DefaultValue = "")]
[DependencyProperty<ICommand>("DiscardCommand")]
[DependencyProperty<ICommand>("SaveCommand")]
[DependencyProperty<bool>("ShowFooter", DefaultValue = true)]
[DependencyProperty<bool>("IsBusy")]
public partial class ConfigurationPageBlock : FunctionalBlock
{
    public const string HeaderRegion = "PART_HeaderRegion";
    public const string SelectorRegion = "PART_SelectorRegion";
    public const string EditorRegion = "PART_EditorRegion";
    public const string FooterRegion = "PART_FooterRegion";
}

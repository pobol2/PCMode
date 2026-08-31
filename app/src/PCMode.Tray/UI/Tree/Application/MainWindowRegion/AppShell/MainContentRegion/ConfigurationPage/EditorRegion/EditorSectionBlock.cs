using System.Windows.Input;
using DependencyPropertyGenerator;
using PCMode.Tray.UI.Framework;

namespace PCMode.Tray.UI.Tree;

/// <summary>One titled editor section whose content either recurses or terminates explicitly.</summary>
[Region(HeaderRegion, Terminal = true)]
[Region(ContentRegion, typeof(FunctionalBlock), Terminal = true)]
[DependencyProperty<string>("Title", DefaultValue = "")]
[DependencyProperty<object>("Content")]
[DependencyProperty<bool>("IsCollapsible")]
[DependencyProperty<bool>("IsExpanded", DefaultValue = true)]
[DependencyProperty<bool>("ShowChrome", DefaultValue = true)]
[DependencyProperty<bool>("ShowEntityActions")]
[DependencyProperty<bool>("ShowActivate")]
[DependencyProperty<string>("ActivateLabel", DefaultValue = "Activate")]
[DependencyProperty<ICommand>("RenameCommand")]
[DependencyProperty<ICommand>("DuplicateCommand")]
[DependencyProperty<ICommand>("DeleteCommand")]
[DependencyProperty<ICommand>("ActivateCommand")]
[DependencyProperty<ICommand>("HeaderCommand")]
[DependencyProperty<string>("HeaderCommandLabel", DefaultValue = "")]
public partial class EditorSectionBlock : FunctionalBlock
{
    public const string HeaderRegion = "PART_HeaderRegion";
    public const string ContentRegion = "PART_ContentRegion";
}

using DependencyPropertyGenerator;
using PCMode.Tray.UI.Framework;

namespace PCMode.Tray.UI.Tree;

/// <summary>
/// The root of the tree. Everything the application shows is built from here.
/// </summary>
/// <remarks>
/// The window hosts this block and owns nothing inside it: Shell hosts, UI
/// decides layout and appearance.
///
/// Navigation and main content are the shell's two structural continuations.
/// The divider between them is layout chrome rather than another region.
/// </remarks>
[Region(NavigationRegion, typeof(PageNavigationBlock))]
[Region(MainContentRegion, typeof(ListPageBlock), typeof(ConfigurationPageBlock))]
[DependencyProperty<object>("Navigation", Description = "The generic page-navigation block.")]
[DependencyProperty<object>("MainContent", Description = "The active generic page-kind block.")]
public partial class AppShellBlock : FunctionalBlock
{
    public const string NavigationRegion = "PART_NavigationRegion";
    public const string MainContentRegion = "PART_MainContentRegion";
}

using DependencyPropertyGenerator;
using PCMode.Tray.UI.Framework;

namespace PCMode.Tray.UI.Tree;

/// <summary>A page whose primary content is presented as a list.</summary>
/// <remarks>
/// A page is an instance of a kind, not a tree of its own. Title and the
/// scroll/header/footer arrangement are this kind's standard structure, owned
/// here once instead of by every page that is one.
/// </remarks>
[Region(HeaderRegion, Terminal = true)]
[Region(ContentRegion, Terminal = true)]
[Region(FooterRegion, Terminal = true)]
[DependencyProperty<string>("Title", DefaultValue = "",
    Description = "The page title, shown above the body.")]
[DependencyProperty<object>("Header",
    Description = "Optional extra content shown to the right of the title.")]
[DependencyProperty<object>("Content",
    Description = "The page's scrolling body.")]
[DependencyProperty<object>("Footer")]
public partial class ListPageBlock : FunctionalBlock
{
    public const string HeaderRegion = "PART_HeaderRegion";
    public const string ContentRegion = "PART_ContentRegion";
    public const string FooterRegion = "PART_FooterRegion";
}

using System.Windows;
using System.Windows.Controls;
using PCMode.Tray.Tests;
using PCMode.Tray.Tests.UI.Construction.Fixtures;
using PCMode.Tray.UI.Framework;

namespace PCMode.Tray.Tests.UI.Construction;

/// <summary>
/// Swapping a Layout changes the arrangement and nothing else. The block's
/// content and the whole tree below it are identical either way, because a
/// block depends on its region contract and never on how the regions are
/// placed.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class LayoutSwapTests
{
    [Fact]
    public void Two_layouts_for_one_block_arrange_the_same_regions_differently()
    {
        Wpf.Run(() =>
        {
            var stacked = new FixtureRootBlock { Header = "Displays" };
            Wpf.Realize(stacked);

            var sideBySide = new FixtureRootBlock { Header = "Displays" };
            sideBySide.Template = Probe.Layout("FixtureRootSideBySideLayout");
            Wpf.Realize(sideBySide);

            Assert.NotEmpty(Wpf.Descendants<StackPanel>(stacked));
            Assert.Empty(Wpf.Descendants<Grid>(stacked));

            Assert.NotEmpty(Wpf.Descendants<Grid>(sideBySide));
        });
    }

    [Fact]
    public void The_content_rendered_under_a_block_is_the_same_whichever_layout_is_applied()
    {
        Wpf.Run(() =>
        {
            var stacked = new ContentControl { Content = Probe.Page() };
            Wpf.Realize(stacked);

            var sideBySide = new ContentControl { Content = Probe.Page() };
            Wpf.Realize(sideBySide);
            Wpf.Descendants<FixtureRootBlock>(sideBySide).Single().Template = Probe.Layout(
                "FixtureRootSideBySideLayout"
            );
            Wpf.Realize(sideBySide);

            Assert.Equal(Rendered(stacked), Rendered(sideBySide));
            Assert.Equal(["Displays", "Dell U2720Q", "LG C2", "Resolution", "Scaling"], Rendered(sideBySide));
        });
    }

    [Fact]
    public void A_swapped_layout_still_hands_the_block_the_regions_it_declared()
    {
        Wpf.Run(() =>
        {
            var page = Probe.Page();
            var block = new FixtureRootBlock
            {
                Header = page.Header,
                Items = page.Items,
                Detail = page.Detail,
                Template = Probe.Layout("FixtureRootSideBySideLayout"),
            };
            Wpf.Realize(block);

            var items = (RepeatedRegion)block.Template.FindName(FixtureRootBlock.ItemsRegion, block);

            Assert.Same(page.Items, items.ItemsSource);
            Assert.Equal(2, items.Items.Count);
        });
    }

    private static string[] Rendered(DependencyObject root) =>
        [.. Wpf.Descendants<TextBlock>(root).Select(text => text.Text)];
}

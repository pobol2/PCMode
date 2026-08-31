using System.Windows.Controls;
using System.Windows.Media;
using PCMode.Tray.UI.Framework;
using PCMode.Tray.Tests.UI.Construction.Fixtures;
using Region = PCMode.Tray.UI.Framework.Region;
using PCMode.Tray.Tests;

namespace PCMode.Tray.Tests.UI.Construction;

/// <summary>
/// A block is lookless: it contributes no visuals of its own, and finds its
/// default Layout in <c>Themes/Generic.xaml</c> from its type alone.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class LooklessBlockTests
{
    [Fact]
    public void A_block_has_no_visual_tree_until_a_layout_is_applied()
    {
        Wpf.Run(() =>
        {
            var block = new FixtureRootBlock { Header = "Displays" };

            Assert.Equal(0, VisualTreeHelper.GetChildrenCount(block));
        });
    }

    [Fact]
    public void A_block_resolves_its_default_layout_with_nothing_merging_or_naming_it()
    {
        Wpf.Run(() =>
        {
            var block = new FixtureRootBlock { Header = "Displays" };
            Wpf.Realize(block);

            Assert.NotNull(block.Template);
            Assert.IsType<Region>(
                block.Template.FindName(FixtureRootBlock.HeaderRegion, block));
        });
    }

    [Fact]
    public void Every_block_type_resolves_its_own_layout_from_the_one_shared_base()
    {
        Wpf.Run(() =>
        {
            var root = new FixtureRootBlock();
            var detail = new FixtureDetailBlock();
            Wpf.Realize(root);
            Wpf.Realize(detail);

            Assert.NotNull(root.Template.FindName(FixtureRootBlock.DetailRegion, root));
            Assert.NotNull(detail.Template.FindName(FixtureDetailBlock.SecondaryRegion, detail));
        });
    }

    [Fact]
    public void A_region_renders_its_content_and_adds_no_visuals_of_its_own()
    {
        Wpf.Run(() =>
        {
            var region = new Region { Content = "Displays" };
            Wpf.Realize(region);

            Assert.Contains(
                Wpf.Descendants<ContentPresenter>(region),
                presenter => Equals("Displays", presenter.Content));
        });
    }
}

using System.Windows.Controls;
using PCMode.Tray.Tests;
using PCMode.Tray.Tests.UI.Construction.Fixtures;
using PCMode.Tray.UI.Framework;
using Region = PCMode.Tray.UI.Framework.Region;

namespace PCMode.Tray.Tests.UI.Construction;

/// <summary>
/// The model end to end: a view model resolves to a block, the block's
/// Layout puts its children in regions, and each child either recurses into
/// another block by exactly the same mechanism or ends in terminal content.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class RecursiveCompositionTests
{
    [Fact]
    public void A_view_model_resolves_to_its_block_with_nothing_wiring_them_by_name()
    {
        Wpf.Run(() =>
        {
            var host = new ContentControl { Content = Probe.Page() };
            Wpf.Realize(host);

            FixtureRootBlock block = Assert.Single(Wpf.Descendants<FixtureRootBlock>(host));
            Assert.Equal("Displays", block.Header);
        });
    }

    [Fact]
    public void A_region_recurses_into_the_next_block_by_the_same_mechanism()
    {
        Wpf.Run(() =>
        {
            var host = new ContentControl { Content = Probe.Page() };
            Wpf.Realize(host);

            // Nothing in FixtureRootBlock names FixtureDetailBlock. The
            // detail view model in its detail region resolved by type.
            FixtureDetailBlock detail = Assert.Single(Wpf.Descendants<FixtureDetailBlock>(host));
            Assert.Equal("Resolution", detail.Primary);
            Assert.Equal("Scaling", detail.Secondary);
        });
    }

    [Fact]
    public void A_repeated_region_resolves_each_child_independently()
    {
        Wpf.Run(() =>
        {
            var host = new ContentControl { Content = Probe.Page() };
            Wpf.Realize(host);

            FixtureRootBlock block = Wpf.Descendants<FixtureRootBlock>(host).Single();
            var items = (RepeatedRegion)block.Template.FindName(FixtureRootBlock.ItemsRegion, block);

            Assert.Equal(2, items.Items.Count);
            Assert.All(items.Items.Cast<object>(), item => Assert.IsType<FixtureItemViewModel>(item));
        });
    }

    [Fact]
    public void A_branch_ends_where_a_child_resolves_to_terminal_content()
    {
        Wpf.Run(() =>
        {
            var host = new ContentControl { Content = Probe.Page() };
            Wpf.Realize(host);

            string[] rendered = [.. Wpf.Descendants<TextBlock>(host).Select(text => text.Text)];

            // Header, primary and secondary are terminal strings; items are
            // terminal TextBlocks. No block appears below any of them,
            // which is what stops the recursion.
            Assert.Equal(["Displays", "Dell U2720Q", "LG C2", "Resolution", "Scaling"], rendered);
        });
    }

    [Fact]
    public void A_single_child_region_holds_one_child_and_a_repeated_region_holds_a_sequence()
    {
        Wpf.Run(() =>
        {
            var host = new ContentControl { Content = Probe.Page() };
            Wpf.Realize(host);

            FixtureRootBlock block = Wpf.Descendants<FixtureRootBlock>(host).Single();

            var header = (Region)block.Template.FindName(FixtureRootBlock.HeaderRegion, block);
            var detail = (Region)block.Template.FindName(FixtureRootBlock.DetailRegion, block);
            var items = (RepeatedRegion)block.Template.FindName(FixtureRootBlock.ItemsRegion, block);

            Assert.Equal("Displays", header.Content);
            Assert.IsType<FixtureDetailViewModel>(detail.Content);
            Assert.Equal(2, items.Items.Count);
        });
    }
}

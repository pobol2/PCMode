using PCMode.Tray.Tests;
using PCMode.Tray.Tests.UI.Construction.Fixtures;
using PCMode.Tray.UI.Framework;
using Region = PCMode.Tray.UI.Framework.Region;

namespace PCMode.Tray.Tests.UI.Construction;

/// <summary>
/// A block owns its regions, and says so on itself. A Layout that does not
/// supply exactly those fails at once, rather than rendering a hole nobody
/// notices.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class RegionContractTests
{
    [Fact]
    public void A_layout_missing_a_declared_region_fails_as_soon_as_it_is_applied()
    {
        Wpf.Run(() =>
        {
            var block = new FixtureRootBlock
            {
                Template = Probe.InvalidLayout("LayoutMissingDetailRegion"),
            };

            var failure = Assert.Throws<TemplateContractException>(() => block.ApplyTemplate());

            Assert.Contains(nameof(FixtureRootBlock), failure.Message, StringComparison.Ordinal);
            Assert.Contains(FixtureRootBlock.DetailRegion, failure.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_layout_supplying_a_repeated_region_as_a_single_child_one_fails()
    {
        Wpf.Run(() =>
        {
            var block = new FixtureRootBlock
            {
                Template = Probe.InvalidLayout("LayoutWithSingleChildItemsRegion"),
            };

            var failure = Assert.Throws<TemplateContractException>(() => block.ApplyTemplate());

            Assert.Contains(FixtureRootBlock.ItemsRegion, failure.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(RepeatedRegion), failure.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_layout_supplying_every_declared_region_applies_without_complaint()
    {
        Wpf.Run(() =>
        {
            var block = new FixtureRootBlock
            {
                Template = Probe.Layout("FixtureRootSideBySideLayout"),
            };

            block.ApplyTemplate();

            Assert.IsType<Region>(
                block.Template.FindName(FixtureRootBlock.HeaderRegion, block));
            Assert.IsType<RepeatedRegion>(
                block.Template.FindName(FixtureRootBlock.ItemsRegion, block));
            Assert.IsType<Region>(
                block.Template.FindName(FixtureRootBlock.DetailRegion, block));
        });
    }

    [Fact]
    public void A_block_enforces_the_regions_it_declares_and_no_others()
    {
        Wpf.Run(() =>
        {
            var detail = new FixtureDetailBlock();
            Wpf.Realize(detail);

            // FixtureDetailBlock declares two regions and knows nothing of the
            // root block's three; enforcement is per block, not global.
            Assert.IsType<Region>(detail.Template.FindName(FixtureDetailBlock.PrimaryRegion, detail));
            Assert.IsType<Region>(detail.Template.FindName(FixtureDetailBlock.SecondaryRegion, detail));
            Assert.Null(detail.Template.FindName(FixtureRootBlock.ItemsRegion, detail));
        });
    }

    [Fact]
    public void The_declared_regions_are_readable_from_the_block_itself()
    {
        Wpf.Run(() =>
        {
            var block = new FixtureRootBlock();

            Assert.Equal(
                [FixtureRootBlock.DetailRegion, FixtureRootBlock.HeaderRegion, FixtureRootBlock.ItemsRegion],
                block.Regions.Select(region => region.Name).Order().ToArray());

            // The contract carries the arity and what may appear, not just a name.
            RegionAttribute items = block.Regions.Single(region => region.Name == FixtureRootBlock.ItemsRegion);
            Assert.Equal(RegionArity.Repeated, items.Arity);
            Assert.True(items.Terminal);

            RegionAttribute detail = block.Regions.Single(region => region.Name == FixtureRootBlock.DetailRegion);
            Assert.Equal(RegionArity.Single, detail.Arity);
            Assert.False(detail.Terminal);
            Assert.True(detail.Admits(typeof(FixtureDetailBlock)));
            Assert.False(detail.Admits(typeof(FixtureRootBlock)));
        });
    }
}

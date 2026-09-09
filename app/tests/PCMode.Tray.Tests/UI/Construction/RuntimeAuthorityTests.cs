using System.Windows.Controls;
using PCMode.Tray.Tests;
using PCMode.Tray.Tests.UI.Construction.Fixtures;
using PCMode.Tray.UI.Framework;

namespace PCMode.Tray.Tests.UI.Construction;

/// <summary>
/// The block, not the markup, is the authority on its own structure.
/// </summary>
/// <remarks>
/// The contract runs in both directions. A Layout must supply every region the
/// block declares, and may supply nothing else -- no invented region, and no
/// child block created directly rather than reached through one. What lands
/// inside a region is then checked against what that region said it admits.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class RuntimeAuthorityTests
{
    [Fact]
    public void A_layout_cannot_add_a_region_the_block_never_declared()
    {
        AssertTemplateFailure(
            "LayoutWithUndeclaredRegion",
            failure => Assert.Contains("does not declare", failure.Message, StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void A_layout_cannot_create_a_child_block_without_a_region()
    {
        AssertTemplateFailure(
            "LayoutWithDirectStructuralBlock",
            failure => Assert.Contains("directly", failure.Message, StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void A_region_rejects_a_child_block_it_does_not_admit()
    {
        Wpf.Run(() =>
        {
            // FixtureStrictBlock's one region admits FixtureDetailBlock. A root
            // view model resolves to FixtureRootBlock, which it does not.
            var host = new ContentControl { Content = new FixtureStrictViewModel(Probe.Page()) };

            TemplateContractException failure = Assert.Throws<TemplateContractException>(() => Wpf.Realize(host));

            Assert.Contains(FixtureStrictBlock.OnlyRegion, failure.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(FixtureRootBlock), failure.Message, StringComparison.Ordinal);
            Assert.Contains("does not admit", failure.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void A_region_that_admits_only_blocks_rejects_terminal_content()
    {
        Wpf.Run(() =>
        {
            var host = new ContentControl { Content = new FixtureStrictViewModel("just a string") };

            TemplateContractException failure = Assert.Throws<TemplateContractException>(() => Wpf.Realize(host));

            Assert.Contains(FixtureStrictBlock.OnlyRegion, failure.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(FinalUiComponent), failure.Message, StringComparison.Ordinal);
            Assert.Contains("only child blocks", failure.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void A_region_that_admits_a_block_accepts_it()
    {
        Wpf.Run(() =>
        {
            var host = new ContentControl
            {
                Content = new FixtureStrictViewModel(new FixtureDetailViewModel("Resolution", "Scaling")),
            };

            Wpf.Realize(host);

            FixtureDetailBlock detail = Assert.Single(Wpf.Descendants<FixtureDetailBlock>(host));
            Assert.Equal("Resolution", detail.Primary);
        });
    }

    [Fact]
    public void A_region_declaring_neither_a_child_block_nor_terminal_content_is_rejected()
    {
        Wpf.Run(() =>
        {
            // Nothing could ever legally appear in such a region, so it is a
            // mistake in the block rather than in any layout.
            var region = new RegionAttribute("PART_Nothing");

            Assert.Empty(region.Blocks);
            Assert.False(region.Terminal);
            Assert.Equal("nothing", region.Describe());
        });
    }

    private static void AssertTemplateFailure(string template, Action<TemplateContractException> assert)
    {
        Wpf.Run(() =>
        {
            var block = new FixtureRootBlock { Template = Probe.InvalidLayout(template) };
            TemplateContractException failure = Assert.Throws<TemplateContractException>(() => block.ApplyTemplate());
            assert(failure);
        });
    }
}

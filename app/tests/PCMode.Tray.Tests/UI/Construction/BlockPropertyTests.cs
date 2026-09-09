using PCMode.Tray.Tests;
using PCMode.Tray.Tests.UI.Construction.Fixtures;

namespace PCMode.Tray.Tests.UI.Construction;

/// <summary>
/// A block declares its inputs as dependency properties, generated from the
/// attribute rather than hand-written, so the declaration and the
/// registration cannot drift apart.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class BlockPropertyTests
{
    [Fact]
    public void A_generated_input_is_a_real_dependency_property_owned_by_its_block()
    {
        Wpf.Run(() =>
        {
            Assert.Equal(nameof(FixtureDetailBlock.Primary), FixtureDetailBlock.PrimaryProperty.Name);
            Assert.Equal(typeof(FixtureDetailBlock), FixtureDetailBlock.PrimaryProperty.OwnerType);
            Assert.Equal(typeof(object), FixtureDetailBlock.PrimaryProperty.PropertyType);
        });
    }

    [Fact]
    public void A_generated_input_round_trips_through_its_clr_wrapper()
    {
        Wpf.Run(() =>
        {
            var block = new FixtureDetailBlock { Primary = "Resolution" };

            Assert.Equal("Resolution", block.Primary);
            Assert.Equal("Resolution", block.GetValue(FixtureDetailBlock.PrimaryProperty));
        });
    }
}

using System.Collections;
using DependencyPropertyGenerator;
using PCMode.Tray.UI.Framework;

namespace PCMode.Tray.Tests.UI.Construction.Fixtures;

/// <summary>
/// A minimal block used only to exercise the framework's mechanics under test:
/// a single-child region, a repeated region, and a region that recurses into
/// <see cref="FixtureDetailBlock"/>. Not production design.
/// </summary>
[Region(HeaderRegion, Terminal = true)]
[Region(ItemsRegion, Arity = RegionArity.Repeated, Terminal = true)]
[Region(DetailRegion, typeof(FixtureDetailBlock))]
[DependencyProperty<object>("Header")]
[DependencyProperty<IEnumerable>("Items")]
[DependencyProperty<object>("Detail")]
public partial class FixtureRootBlock : FunctionalBlock
{
    public const string HeaderRegion = "PART_HeaderRegion";
    public const string ItemsRegion = "PART_ItemsRegion";
    public const string DetailRegion = "PART_DetailRegion";
}

/// <summary>
/// The second level of the fixture tree, proving recursion is general rather
/// than a special case at the top.
/// </summary>
[Region(PrimaryRegion, Terminal = true)]
[Region(SecondaryRegion, Terminal = true)]
[DependencyProperty<object>("Primary")]
[DependencyProperty<object>("Secondary")]
public partial class FixtureDetailBlock : FunctionalBlock
{
    public const string PrimaryRegion = "PART_PrimaryRegion";
    public const string SecondaryRegion = "PART_SecondaryRegion";
}

/// <summary>
/// A block whose region admits only <see cref="FixtureDetailBlock"/>, for the
/// tests that check a region refuses a child it does not admit.
/// </summary>
[Region(OnlyRegion, typeof(FixtureDetailBlock))]
[DependencyProperty<object>("Only")]
public partial class FixtureStrictBlock : FunctionalBlock
{
    public const string OnlyRegion = "PART_OnlyRegion";
}

/// <summary>
/// A block that declares a region no layout is expected to satisfy, for the
/// missing-region case.
/// </summary>
[Region(AbsentRegion, Terminal = true)]
public partial class FixtureUnsatisfiableBlock : FunctionalBlock
{
    public const string AbsentRegion = "PART_AbsentRegion";
}

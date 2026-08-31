using System.Collections;
using DependencyPropertyGenerator;
using PCMode.Tray.UI.Framework;

namespace PCMode.Tray.UI.Tree;

/// <summary>The four generic section groups of a configuration editor.</summary>
[Region(EssentialRegion, typeof(EditorSectionBlock), Arity = RegionArity.Repeated)]
[Region(AdvancedRegion, typeof(EditorSectionBlock), Arity = RegionArity.Repeated)]
[Region(ExtraRegion, typeof(EditorSectionBlock), Arity = RegionArity.Repeated)]
[Region(HardwareRegion, typeof(EditorSectionBlock), Arity = RegionArity.Repeated)]
[DependencyProperty<IEnumerable>("EssentialSections")]
[DependencyProperty<IEnumerable>("AdvancedSections")]
[DependencyProperty<IEnumerable>("ExtraSections")]
[DependencyProperty<IEnumerable>("HardwareSections")]
public partial class ConfigurationEditorBlock : FunctionalBlock
{
    public const string EssentialRegion = "PART_EssentialRegion";
    public const string AdvancedRegion = "PART_AdvancedRegion";
    public const string ExtraRegion = "PART_ExtraRegion";
    public const string HardwareRegion = "PART_HardwareRegion";
}

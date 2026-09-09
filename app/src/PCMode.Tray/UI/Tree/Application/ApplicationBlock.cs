using DependencyPropertyGenerator;
using PCMode.Tray.UI.Framework;

namespace PCMode.Tray.UI.Tree;

[Region(MainWindowRegion, typeof(AppShellBlock))]
[Region(PickerRegion, Terminal = true)]
[Region(SetupRegion, Terminal = true)]
[Region(DialogRegion, Terminal = true)]
[DependencyProperty<object>("MainWindow")]
[DependencyProperty<object>("Picker")]
[DependencyProperty<object>("Setup")]
[DependencyProperty<object>("Dialog")]
public partial class ApplicationBlock : FunctionalBlock
{
    public const string MainWindowRegion = "PART_MainWindowRegion";
    public const string PickerRegion = "PART_PickerRegion";
    public const string SetupRegion = "PART_SetupRegion";
    public const string DialogRegion = "PART_DialogRegion";
}

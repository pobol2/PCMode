using DependencyPropertyGenerator;

namespace PCMode.Tray.UI.Components.Migration;

/// <summary>
/// Reusable terminal rendering for content whose final component has not yet
/// been migrated. Elements supply a description and source; this component
/// alone decides how that temporary leaf is rendered.
/// </summary>
[DependencyProperty<string>("Description", DefaultValue = "")]
[DependencyProperty<object>("Source")]
public partial class MigrationContent : System.Windows.Controls.Control
{
    public MigrationContent()
    {
        DefaultStyleKey = GetType();
    }
}

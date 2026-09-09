using System.Windows.Input;
using DependencyPropertyGenerator;

namespace PCMode.Tray.UI.Components.Selection;

/// <summary>A reusable terminal selector row driven entirely by parameters.</summary>
[DependencyProperty<string>("Title", DefaultValue = "")]
[DependencyProperty<string>("Summary", DefaultValue = "")]
[DependencyProperty<bool>("IsSelected")]
[DependencyProperty<ICommand>("Command")]
[DependencyProperty<object>("CommandParameter")]
public partial class SelectorItem : System.Windows.Controls.Control
{
    public SelectorItem()
    {
        DefaultStyleKey = GetType();
    }
}

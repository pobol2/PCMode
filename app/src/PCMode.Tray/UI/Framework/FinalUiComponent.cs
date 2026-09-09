using System.Windows;
using System.Windows.Controls;

namespace PCMode.Tray.UI.Framework;

/// <summary>
/// The leaf of the Tray construction tree. It may contain any ordinary WPF
/// element, but it cannot continue the semantic block tree.
/// </summary>
/// <remarks>
/// Blocks and Regions describe product structure. Everything inside a final
/// component is local rendering detail: panels, text, buttons, custom controls,
/// or any other ordinary WPF content.
/// </remarks>
public sealed class FinalUiComponent : ContentControl
{
    static FinalUiComponent()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(FinalUiComponent),
            new FrameworkPropertyMetadata(typeof(FinalUiComponent))
        );
    }
}

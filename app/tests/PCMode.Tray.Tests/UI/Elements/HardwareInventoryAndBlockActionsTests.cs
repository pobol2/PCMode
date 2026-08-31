using System.Windows;
using PCMode.Tray.Elements;
using WpfButton = System.Windows.Controls.Button;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using PCMode.Tray.Tests;

namespace PCMode.Tray.Tests.UI.Components;

/// <summary>
/// The "Current hardware" item card (<c>HardwareInventoryItemTemplate</c> in
/// <c>ComponentLayout.xaml</c>), and the click relay it needs.
///
/// A Layout attaches its own handler above a shared, handler-less template --
/// but a ContextMenu opens as a Popup outside its owner's visual tree, so a
/// MenuItem's own Click does not reliably bubble past it.
/// <see cref="ManagedContentCard"/> instead catches it on the ContextMenu
/// itself (still within the Popup's own tree) and re-raises it as an ordinary
/// bubbling event on itself, the same pattern
/// <see cref="EssentialDeviceCard"/>'s own Remove button already used for a
/// same-tree click. What is worth protecting here is that this two-step relay
/// actually reaches a handler attached above the shared markup, not just that
/// the shared markup renders.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class HardwareInventoryAndBlockActionsTests
{
    private sealed class FakeHardwareRow : IHardwareInventoryRow
    {
        public string FriendlyName => "Living Room Speakers";

        public string Name => "Speakers (Realtek)";

        public bool ShowUnderlyingDevice => true;

        public string? BadgeText => "Current";

        public bool HasBadge => true;

        public string ActionLabel => "Rename";

        public bool CanUseActions => true;

        public bool ShowActivate => true;

        public bool CanActivate => true;
    }

    
    private static void ClickMenuItemByTag(WpfContextMenu menu, string tag)
    {
        WpfMenuItem item = FindDescendant<WpfMenuItem>(menu, candidate => Equals(candidate.Tag, tag));
        item.RaiseEvent(new RoutedEventArgs(WpfMenuItem.ClickEvent));
    }

    private static void Arrange(FrameworkElement element)
    {
        element.ApplyTemplate();
        element.Measure(new System.Windows.Size(800, 600));
        element.Arrange(new Rect(0, 0, 800, 600));
        element.UpdateLayout();
    }

    private static T FindDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);

            if (child is T match && predicate(match))
            {
                return match;
            }

            try
            {
                return FindDescendant(child, predicate);
            }
            catch (InvalidOperationException)
            {
            }
        }

        throw new InvalidOperationException($"No matching {typeof(T).Name} was found.");
    }
}

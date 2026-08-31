using System.Windows;
using System.Windows.Media;

namespace PCMode.Tray.UI.Framework;

/// <summary>
/// The one child-checking rule, shared by both arities.
/// </summary>
internal static class RegionRuntime
{
    /// <summary>
    /// Checks what a region actually realized against what its block said may
    /// appear there.
    /// </summary>
    /// <remarks>
    /// Run from layout rather than at bind time because the child is produced
    /// by an implicit <see cref="DataTemplate"/>: until the template has been
    /// applied there is nothing yet to look at. The walk stops at the first
    /// Functional Block on each branch -- what is inside that block is that
    /// block's own contract, not this one's.
    /// </remarks>
    internal static void Validate(DependencyObject regionElement, RegionAttribute region)
    {
        foreach (DependencyObject child in TopLevelChildren(regionElement))
        {
            if (child is FunctionalBlock block)
            {
                if (!region.Admits(block.GetType()))
                {
                    throw new TemplateContractException(
                        $"Region '{region.Name}' realized {block.GetType().Name}, which it does not " +
                        $"admit. Allowed: {region.Describe()}.");
                }

                continue;
            }

            if (child is FinalUiComponent)
            {
                if (!region.Terminal)
                {
                    throw new TemplateContractException(
                        $"Region '{region.Name}' realized a FinalUiComponent, but it is declared to hold " +
                        $"only child blocks. Allowed: {region.Describe()}.");
                }

                continue;
            }

            throw new TemplateContractException(
                $"Region '{region.Name}' realized raw WPF content ({child.GetType().Name}). " +
                "Terminal leaves must be wrapped explicitly in FinalUiComponent.");
        }
    }

    /// <summary>
    /// The nearest descendants that settle the question: each branch yields
    /// its first Functional Block, or its first ordinary visual.
    /// </summary>
    private static IReadOnlyList<DependencyObject> TopLevelChildren(DependencyObject root)
    {
        var found = new List<DependencyObject>();
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int index = 0; index < count; index++)
        {
            Visit(VisualTreeHelper.GetChild(root, index), found);
        }

        return found;
    }

    private static void Visit(DependencyObject current, ICollection<DependencyObject> found)
    {
        if (current is FunctionalBlock or FinalUiComponent)
        {
            found.Add(current);
            return;
        }

        int count = VisualTreeHelper.GetChildrenCount(current);

        // A presenter or a panel is plumbing on the way to the real child; an
        // element with nothing under it is the content itself.
        if (count == 0)
        {
            found.Add(current);
            return;
        }

        for (int index = 0; index < count; index++)
        {
            Visit(VisualTreeHelper.GetChild(current, index), found);
        }
    }
}

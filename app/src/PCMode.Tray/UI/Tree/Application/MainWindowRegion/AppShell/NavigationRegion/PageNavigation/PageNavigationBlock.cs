using System.Collections;
using System.Windows;
using DependencyPropertyGenerator;
using PCMode.Tray.UI.Framework;

namespace PCMode.Tray.UI.Tree;

/// <summary>The list of pages, and which one is open.</summary>
/// <remarks>
/// One terminal region holding the navigation list whole. The list has not
/// been broken into its own item and expansion regions yet, so it stays a
/// Final UI Component and this block simply gives it its place in the tree.
///
/// <see cref="SelectedEntry"/> is typed <see cref="object"/> on purpose: the
/// entries belong to the caller, so the generic tree does not name their type.
/// The caller reads the selection back out and decides what to do with it.
/// </remarks>
[Region(ContentRegion, Terminal = true)]
[DependencyProperty<IEnumerable>("Items",
    Description = "One entry per navigable page.")]
public partial class PageNavigationBlock : FunctionalBlock
{
    public const string ContentRegion = "PART_ContentRegion";

    /// <remarks>
    /// Hand-written rather than generated because it carries a change
    /// callback so the caller can observe that the selection moved.
    /// </remarks>
    public static readonly DependencyProperty SelectedEntryProperty =
        DependencyProperty.Register(
            nameof(SelectedEntry),
            typeof(object),
            typeof(PageNavigationBlock),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSelectedEntryChanged));

    /// <summary>The entry currently selected, or null before the first one is.</summary>
    public object? SelectedEntry
    {
        get => GetValue(SelectedEntryProperty);
        set => SetValue(SelectedEntryProperty, value);
    }

    /// <summary>Raised after <see cref="SelectedEntry"/> changes.</summary>
    public event EventHandler? SelectionChanged;

    private static void OnSelectedEntryChanged(DependencyObject source, DependencyPropertyChangedEventArgs e) =>
        ((PageNavigationBlock)source).SelectionChanged?.Invoke(source, EventArgs.Empty);
}

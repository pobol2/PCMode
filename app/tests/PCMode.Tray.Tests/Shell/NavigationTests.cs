using PCMode.Tray.Elements.Navigation;

namespace PCMode.Tray.Tests.Shell;

/// <summary>
/// The shell's own navigation invariant: the entries form a valid, acyclic
/// hierarchy. Whether each entry has a page to show is the composition root's
/// half of the question, asserted in <c>App/PageConstructionTests</c>.
/// </summary>
public sealed class NavigationTests
{
    [Fact]
    public void The_real_navigation_registry_is_a_valid_hierarchy()
    {
        // Throws on any problem; a clean run is the assertion.
        NavigationRegistry.Validate(NavigationRegistry.Items);
    }

    [Fact]
    public void Validate_rejects_a_page_that_appears_more_than_once()
    {
        NavigationItem[] items = [new(PageId.Home, "Home"), new(PageId.Home, "Home again")];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            NavigationRegistry.Validate(items)
        );

        Assert.Contains("appears more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_a_page_whose_own_parent_it_is()
    {
        NavigationItem[] items = [new(PageId.Home, "Home", PageId.Home)];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            NavigationRegistry.Validate(items)
        );

        Assert.Contains("cannot be its own parent", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_a_reference_to_a_parent_that_is_not_registered()
    {
        NavigationItem[] items = [new(PageId.Applications, "Applications", (PageId)(-1))];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            NavigationRegistry.Validate(items)
        );

        Assert.Contains("missing parent", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_a_cycle_between_two_pages()
    {
        NavigationItem[] items =
        [
            new(PageId.Home, "Home", PageId.Applications),
            new(PageId.Applications, "Applications", PageId.Home),
        ];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            NavigationRegistry.Validate(items)
        );

        Assert.Contains("cycle", exception.Message, StringComparison.Ordinal);
    }
}

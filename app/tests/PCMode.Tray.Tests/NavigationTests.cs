using PCMode.Tray.Navigation;

namespace PCMode.Tray.Tests;

/// <summary>
/// The navigation invariants <c>ARCHITECTURE.md</c> promises: every
/// navigation entry has one page factory and a valid, acyclic parent.
/// </summary>
public sealed class NavigationTests
{
    [Fact]
    public void The_real_navigation_registry_validates_against_the_page_catalog()
    {
        var catalog = new PageCatalog(new FakeWorkspace());

        // Throws on any problem; a clean run is the assertion.
        catalog.Validate(NavigationRegistry.Items);
    }

    [Fact]
    public void Validate_rejects_a_page_that_appears_more_than_once()
    {
        var catalog = new PageCatalog(new FakeWorkspace());
        NavigationItem[] items =
        [
            new(PageId.Home, "Home"),
            new(PageId.Home, "Home again"),
        ];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => catalog.Validate(items));
        Assert.Contains("appears more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_a_page_whose_own_parent_it_is()
    {
        var catalog = new PageCatalog(new FakeWorkspace());
        NavigationItem[] items = [new(PageId.Home, "Home", PageId.Home)];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => catalog.Validate(items));
        Assert.Contains("cannot be its own parent", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_a_reference_to_a_parent_that_is_not_registered()
    {
        var catalog = new PageCatalog(new FakeWorkspace());
        NavigationItem[] items = [new(PageId.Applications, "Applications", PageId.Devices)];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => catalog.Validate(items));
        Assert.Contains("missing parent", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_a_cycle_between_two_pages()
    {
        var catalog = new PageCatalog(new FakeWorkspace());
        NavigationItem[] items =
        [
            new(PageId.Home, "Home", PageId.Devices),
            new(PageId.Devices, "Devices", PageId.Home),
        ];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => catalog.Validate(items));
        Assert.Contains("cycle", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_a_navigable_page_with_no_registered_factory()
    {
        // A page id the catalog was never handed a factory for -- exactly what
        // adding a NavigationItem before wiring PageCatalog would produce, the
        // one step "adding a page should be mechanical" (Main Dev Guide rule
        // 15) depends on this check to catch.
        var catalog = new PageCatalog(new FakeWorkspace());
        NavigationItem[] items = [new((PageId)(-1), "Mystery")];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => catalog.Validate(items));
        Assert.Contains("no registered page factory", exception.Message, StringComparison.Ordinal);
    }
}

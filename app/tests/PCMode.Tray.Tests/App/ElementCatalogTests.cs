using PCMode.Tray.Elements.Navigation;

namespace PCMode.Tray.Tests.App;

public sealed class ElementCatalogTests
{
    [Fact]
    public void Complete_application_composition_matches_navigation()
    {
        ElementCatalog catalog = CompleteCatalog();

        catalog.Validate(NavigationRegistry.Items);

        Assert.Equal(NavigationRegistry.Items.Count, catalog.Elements.Count);

        foreach (NavigationItem item in NavigationRegistry.Items)
        {
            Assert.Same(catalog.Elements[item.Page], catalog.Get(item.Page));
        }
    }

    [Fact]
    public void Navigable_page_without_an_element_is_rejected()
    {
        var catalog = new ElementCatalog(CompletePairs().Where(pair => pair.Key != PageId.Audio));

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            catalog.Validate(NavigationRegistry.Items)
        );

        Assert.Contains("Audio", failure.Message, StringComparison.Ordinal);
        Assert.Contains("no composed Element", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Composed_element_without_navigation_is_rejected()
    {
        KeyValuePair<PageId, object>[] pairs = [.. CompletePairs(), KeyValuePair.Create((PageId)999, new object())];

        var catalog = new ElementCatalog(pairs);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            catalog.Validate(NavigationRegistry.Items)
        );

        Assert.Contains("999", failure.Message, StringComparison.Ordinal);
        Assert.Contains("not navigable", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_elements_cannot_own_the_same_page()
    {
        KeyValuePair<PageId, object>[] pairs =
        [
            KeyValuePair.Create(PageId.Home, new object()),
            KeyValuePair.Create(PageId.Home, new object()),
        ];

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => new ElementCatalog(pairs));

        Assert.Contains("more than one Element", failure.Message, StringComparison.Ordinal);
    }

    private static ElementCatalog CompleteCatalog() => new(CompletePairs());

    private static IEnumerable<KeyValuePair<PageId, object>> CompletePairs() =>
        NavigationRegistry.Items.Select(item => KeyValuePair.Create(item.Page, new object()));
}

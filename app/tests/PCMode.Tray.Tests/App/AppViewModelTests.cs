using PCMode.Tray.Elements;
using PCMode.Tray.Elements.Navigation;

namespace PCMode.Tray.Tests.App;

public sealed class AppViewModelTests
{
    [Fact]
    public void Construction_selects_and_activates_the_composed_home_element()
    {
        (AppViewModel model, IReadOnlyDictionary<PageId, StubPage> pages) = CreateModel();

        Assert.Equal(PageId.Home, model.Navigation.SelectedEntry?.Page);
        Assert.Same(pages[PageId.Home], model.CurrentPage);
        Assert.Equal(1, pages[PageId.Home].Activations);
        Assert.Equal(0, pages[PageId.Home].Deactivations);
    }

    [Fact]
    public void Selection_change_deactivates_the_previous_element_and_activates_the_selected_one()
    {
        (AppViewModel model, IReadOnlyDictionary<PageId, StubPage> pages) = CreateModel();

        model.Navigation.SelectedEntry = Entry(model.Navigation, PageId.Audio);

        Assert.Same(pages[PageId.Audio], model.CurrentPage);
        Assert.Equal(1, pages[PageId.Home].Deactivations);
        Assert.Equal(1, pages[PageId.Audio].Activations);
    }

    [Fact]
    public void Selecting_the_current_page_again_is_idempotent()
    {
        (AppViewModel model, IReadOnlyDictionary<PageId, StubPage> pages) = CreateModel();
        NavigationEntry home = Entry(model.Navigation, PageId.Home);

        model.Navigation.SelectedEntry = home;

        Assert.Equal(1, pages[PageId.Home].Activations);
        Assert.Equal(0, pages[PageId.Home].Deactivations);
    }

    [Fact]
    public void Returning_to_a_page_reuses_its_element_and_preserves_edits()
    {
        (AppViewModel model, IReadOnlyDictionary<PageId, StubPage> pages) = CreateModel();
        StubPage home = pages[PageId.Home];
        home.Edit = "unsaved change";

        model.Navigation.SelectedEntry = Entry(model.Navigation, PageId.Settings);
        model.Navigation.SelectedEntry = Entry(model.Navigation, PageId.Home);

        Assert.Same(home, model.CurrentPage);
        Assert.Equal("unsaved change", home.Edit);
        Assert.Equal(2, home.Activations);
        Assert.Equal(1, home.Deactivations);
    }

    private static (AppViewModel Model, IReadOnlyDictionary<PageId, StubPage> Pages) CreateModel()
    {
        Dictionary<PageId, StubPage> pages = NavigationRegistry.Items
            .ToDictionary(item => item.Page, _ => new StubPage());

        var catalog = new ElementCatalog(
            pages.Select(pair => KeyValuePair.Create<PageId, object>(pair.Key, pair.Value)));

        return (new AppViewModel(new NavigationState(), catalog), pages);
    }

    private static NavigationEntry Entry(NavigationState navigation, PageId page) =>
        navigation.Items.Single(entry => entry.Page == page);

    private sealed class StubPage : IPageLifecycle
    {
        public int Activations { get; private set; }

        public int Deactivations { get; private set; }

        public string? Edit { get; set; }

        public void OnActivated() => Activations++;

        public void OnDeactivated() => Deactivations++;
    }
}

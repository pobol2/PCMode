using PCMode.Tray.Elements;
using PCMode.Tray.Elements.Navigation;

namespace PCMode.Tray;

public sealed class AppViewModel : ObservableObject
{
    private object? _currentPage;

    public AppViewModel(NavigationState navigation, ElementCatalog elements)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(elements);

        Navigation = navigation;
        Elements = elements;

        Navigation.PropertyChanged += OnNavigationPropertyChanged;
        Navigation.SelectedEntry = Navigation.Items.Single(entry => entry.Page == PageId.Home);
    }

    public NavigationState Navigation { get; }

    /// <summary>The App-owned composition available to navigation orchestration.</summary>
    public ElementCatalog Elements { get; }

    public object? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    private void OnNavigationPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NavigationState.SelectedEntry) || Navigation.SelectedEntry is not { } selection)
        {
            return;
        }

        object nextPage = Elements.Get(selection.Page);

        if (ReferenceEquals(CurrentPage, nextPage))
        {
            return;
        }

        if (CurrentPage is IPageLifecycle previousLifecycle)
        {
            previousLifecycle.OnDeactivated();
        }

        CurrentPage = nextPage;

        if (nextPage is IPageLifecycle nextLifecycle)
        {
            nextLifecycle.OnActivated();
        }
    }
}

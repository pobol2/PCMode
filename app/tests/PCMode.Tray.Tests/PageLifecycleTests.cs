using System.Windows;
using PCMode.Tray.Features.Cursor;
using PCMode.Tray.Navigation;

namespace PCMode.Tray.Tests;

/// <summary>
/// <see cref="ShellPages"/>' contract: pages are created lazily, cached, and
/// refreshed through activation/deactivation rather than reconstruction
/// (Main Dev Guide rule 11; <c>ARCHITECTURE.md</c>'s "page refresh does
/// not discard unsaved edits" invariant).
/// </summary>
[Collection(WpfWindowConstructionCollection.Name)]
public sealed class PageLifecycleTests
{
    [Fact]
    public void Get_returns_the_same_cached_instance_for_the_same_page()
    {
        using var root = new TemporaryConfigurationRoot();

        StaThread.Run(() =>
        {
            var pages = new ShellPages(new PageCatalog(Workspaces.At(root.Path)));

            FrameworkElement first = pages.Get(PageId.Spaces);
            FrameworkElement second = pages.Get(PageId.Spaces);

            Assert.Same(first, second);
        });
    }

    [Fact]
    public void Different_pages_get_different_cached_instances()
    {
        using var root = new TemporaryConfigurationRoot();

        StaThread.Run(() =>
        {
            var pages = new ShellPages(new PageCatalog(Workspaces.At(root.Path)));

            Assert.NotSame(pages.Get(PageId.Cursor), pages.Get(PageId.Audio));
        });
    }

    [Fact]
    public void First_activation_loads_the_page_from_disk()
    {
        using var root = new TemporaryConfigurationRoot();

        StaThread.Run(() =>
        {
            var pages = new ShellPages(new PageCatalog(Workspaces.At(root.Path)));

            var cursor = Assert.IsType<CursorPage>(pages.Activate(PageId.Cursor));
            var viewModel = Assert.IsType<CursorViewModel>(cursor.DataContext);

            // Refresh() ran: the status line it always sets is no longer null,
            // and the freshly loaded state is not treated as an unsaved edit.
            Assert.NotNull(viewModel.StatusMessage);
            Assert.False(viewModel.HasUnsavedChanges);
        });
    }

    [Fact]
    public void Reactivating_the_same_page_does_not_discard_unsaved_edits()
    {
        using var root = new TemporaryConfigurationRoot();

        StaThread.Run(() =>
        {
            var pages = new ShellPages(new PageCatalog(Workspaces.At(root.Path)));

            var cursor = Assert.IsType<CursorPage>(pages.Activate(PageId.Cursor));
            var viewModel = Assert.IsType<CursorViewModel>(cursor.DataContext);

            viewModel.XValue = 75;
            Assert.True(viewModel.HasUnsavedChanges);

            // Reactivating the page that is already active must not reload it.
            pages.Activate(PageId.Cursor);

            Assert.True(viewModel.HasUnsavedChanges);
            Assert.Equal(75, viewModel.XValue);
        });
    }

    [Fact]
    public void Switching_away_and_back_does_not_discard_unsaved_edits_either()
    {
        using var root = new TemporaryConfigurationRoot();

        StaThread.Run(() =>
        {
            var pages = new ShellPages(new PageCatalog(Workspaces.At(root.Path)));

            var cursor = Assert.IsType<CursorPage>(pages.Activate(PageId.Cursor));
            var viewModel = Assert.IsType<CursorViewModel>(cursor.DataContext);

            viewModel.XValue = 80;
            Assert.True(viewModel.HasUnsavedChanges);

            pages.Activate(PageId.Home);
            pages.Activate(PageId.Cursor);

            Assert.True(viewModel.HasUnsavedChanges);
            Assert.Equal(80, viewModel.XValue);
        });
    }
}

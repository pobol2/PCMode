using System.Windows;
using PCMode.Tray.Controls.PageShells;
using PCMode.Tray.Features.Applications;
using PCMode.Tray.Features.Audio;
using PCMode.Tray.Features.Cursor;
using PCMode.Tray.Features.Devices;
using PCMode.Tray.Features.Home;
using PCMode.Tray.Features.Diagnostics;
using PCMode.Tray.Features.Hardware;
using PCMode.Tray.Features.Layouts;
using PCMode.Tray.Features.Rgb;
using PCMode.Tray.Features.Settings;
using PCMode.Tray.Features.Spaces;
using PCMode.Tray.Features.Television;
using PCMode.Tray.Features.Theme;
using PCMode.Tray.Navigation;

namespace PCMode.Tray.Tests;

/// <summary>
/// What <see cref="PageCatalog"/> builds for each <see cref="PageId"/>, and
/// that every full page still belongs to a page family (Main Dev Guide rule
/// 1; <c>ARCHITECTURE.md</c>'s "full pages derive from a page-family
/// shell" invariant).
/// </summary>
[Collection(WpfWindowConstructionCollection.Name)]
public sealed class PageConstructionTests
{
    [Fact]
    public void Every_page_id_builds_the_page_and_view_model_its_feature_expects()
    {
        StaThread.Run(() =>
        {
            var catalog = new PageCatalog(new FakeWorkspace());

            Assert.IsType<HomeViewModel>(
                Assert.IsType<HomePage>(catalog.Create(PageId.Home)).DataContext);

            Assert.IsType<HardwareViewModel>(
                Assert.IsType<DevicesPage>(catalog.Create(PageId.Devices)).DataContext);

            Assert.IsType<ApplicationsViewModel>(
                Assert.IsType<ApplicationsPage>(catalog.Create(PageId.Applications)).DataContext);

            Assert.IsType<AudioViewModel>(
                Assert.IsType<AudioPage>(catalog.Create(PageId.Audio)).DataContext);

            Assert.IsType<CursorViewModel>(
                Assert.IsType<CursorPage>(catalog.Create(PageId.Cursor)).DataContext);

            Assert.IsType<RgbViewModel>(
                Assert.IsType<RgbPage>(catalog.Create(PageId.Rgb)).DataContext);

            Assert.IsType<SpacesViewModel>(
                Assert.IsType<SpacesPage>(catalog.Create(PageId.Spaces)).DataContext);

            Assert.IsType<ThemeViewModel>(
                Assert.IsType<ThemePage>(catalog.Create(PageId.Theme)).DataContext);

            Assert.IsType<LayoutsViewModel>(
                Assert.IsType<LayoutsPage>(catalog.Create(PageId.Layouts)).DataContext);

            Assert.IsType<TelevisionViewModel>(
                Assert.IsType<TelevisionPage>(catalog.Create(PageId.Television)).DataContext);

            Assert.IsType<SettingsViewModel>(
                Assert.IsType<SettingsPage>(catalog.Create(PageId.Settings)).DataContext);

            Assert.IsType<DiagnosticsViewModel>(
                Assert.IsType<DiagnosticsPage>(catalog.Create(PageId.Diagnostics)).DataContext);
        });
    }

    [Fact]
    public void Every_full_page_derives_from_a_shared_page_family_shell()
    {
        StaThread.Run(() =>
        {
            var catalog = new PageCatalog(new FakeWorkspace());

            foreach (PageId page in Enum.GetValues<PageId>())
            {
                FrameworkElement element = catalog.Create(page);

                Assert.True(
                    element is ListPageShell or ConfigurationPageShell,
                    $"{page} builds a {element.GetType().Name}, which is not a page-family shell.");
            }
        });
    }

    [Fact]
    public void Create_throws_for_a_page_id_with_no_registered_factory()
    {
        StaThread.Run(() =>
        {
            var catalog = new PageCatalog(new FakeWorkspace());

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => catalog.Create((PageId)(-1)));
            Assert.Contains("No page factory is registered", exception.Message, StringComparison.Ordinal);
        });
    }
}

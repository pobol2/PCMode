using System.Windows;
using System.Windows.Controls;
using PCMode.Tray.Elements.Diagnostics;
using PCMode.Tray.Elements.Home;
using PCMode.Tray.Elements.Settings;
using PCMode.Tray.UI.Framework;
using PCMode.Tray.UI.Tree;
using Region = PCMode.Tray.UI.Framework.Region;

namespace PCMode.Tray.Tests.UI;

/// <summary>
/// The list-style Elements select the legal ListPage branch. Their terminal
/// migration content is still validated by the real RegionRuntime when the
/// block's layout is realized.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ListElementBindingsTests
{
    private const string Bindings =
        "/PCMode.Tray;component/UI/Tree/Application/MainWindowRegion/AppShell/MainContentRegion/ListPage/ListPageBindings.xaml";

    [Fact]
    public void Home_resolves_to_a_valid_list_page() => AssertListPage(new HomeViewModel(runtime: null), "Home");

    [Fact]
    public void Settings_resolves_to_a_valid_list_page() =>
        AssertListPage(new SettingsViewModel(new FakeWorkspace(), store: null), "Settings");

    [Fact]
    public void Diagnostics_resolves_to_a_valid_list_page() =>
        AssertListPage(new DiagnosticsViewModel(new FakeWorkspace(), runtime: null, log: null), "Diagnostics");

    private static void AssertListPage(object element, string title)
    {
        Wpf.Run(() =>
        {
            var host = new ContentControl { Content = element };
            host.Resources.MergedDictionaries.Add(
                new ResourceDictionary { Source = new Uri(Bindings, UriKind.Relative) }
            );

            // Realizing applies ListPageLayout and therefore runs the actual
            // RegionRuntime checks for header, content, and footer.
            Wpf.Realize(host);

            ListPageBlock block = Assert.Single(Wpf.Descendants<ListPageBlock>(host));
            Assert.Equal(title, block.Title);
            Assert.IsType<FinalUiComponent>(
                block.Template.FindName(ListPageBlock.ContentRegion, block) is Region content
                    ? Wpf.Descendants<FinalUiComponent>(content).FirstOrDefault()
                    : null
            );
        });
    }
}

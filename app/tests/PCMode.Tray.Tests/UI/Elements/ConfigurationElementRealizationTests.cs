using System.Windows;
using System.Windows.Controls;
using PCMode.Tray.Elements.Applications;
using PCMode.Tray.Elements.Audio;
using PCMode.Tray.Elements.Configuration;
using PCMode.Tray.Elements.Displays;
using PCMode.Tray.Elements.Rgb;
using PCMode.Tray.Elements.Spaces;
using PCMode.Tray.UI.Components.Migration;
using PCMode.Tray.UI.Framework;
using PCMode.Tray.UI.Tree;

namespace PCMode.Tray.Tests.UI.Elements;

/// <summary>
/// Proves each configuration Element reaches the same legal recursive Tree
/// path. The terminal editor content is intentionally opaque at this migration
/// boundary; these tests stop at FinalUiComponent for that reason.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ConfigurationElementRealizationTests
{
    [Fact]
    public void Every_configuration_element_realizes_the_legal_page_selector_editor_section_path()
    {
        Wpf.Run(() =>
        {
            var applications = new ApplicationsViewModel(new FakeWorkspace());
            applications.SelectorItems.Add(new ConfigurationEntityListItem<string>("browser", "Browser", "Configured"));

            ConfigurationPageViewModelBase[] elements =
            [
                applications,
                new AudioViewModel(new FakeWorkspace()),
                new RgbViewModel(new FakeWorkspace()),
                new DisplaysViewModel(new FakeWorkspace()),
                new SpacesViewModel(new FakeWorkspace()),
            ];

            foreach (ConfigurationPageViewModelBase element in elements)
            {
                var host = new ContentControl { Content = element };
                host.Resources.MergedDictionaries.Add(
                    new ResourceDictionary
                    {
                        Source = new Uri(
                            "/PCMode.Tray;component/UI/Tree/Application/MainWindowRegion/AppShell/MainContentRegion/ConfigurationPage/ConfigurationPageBindings.xaml",
                            UriKind.Relative
                        ),
                    }
                );

                Wpf.Realize(host);

                ConfigurationPageBlock page = Assert.Single(Wpf.Descendants<ConfigurationPageBlock>(host));
                Assert.NotEmpty(page.Title);
                Assert.Single(Wpf.Descendants<ConfigurationSelectorBlock>(page));
                Assert.Single(Wpf.Descendants<ConfigurationEditorBlock>(page));
                Assert.NotEmpty(Wpf.Descendants<EditorSectionBlock>(page));
                Assert.NotEmpty(Wpf.Descendants<FinalUiComponent>(page));
                Assert.Empty(Wpf.Descendants<MigrationContent>(page));
            }
        });
    }

    [Fact]
    public void Presentation_models_do_not_depend_on_tree_or_framework_types()
    {
        Type[] presentationTypes =
        [
            typeof(ConfigurationPagePresentation),
            typeof(ConfigurationSelectorPresentation),
            typeof(ConfigurationEditorPresentation),
            typeof(EditorSectionPresentation),
            typeof(TerminalElementContent),
        ];

        Assert.All(
            presentationTypes,
            type =>
                Assert.DoesNotContain(
                    type.GetProperties(),
                    property => typeof(FunctionalBlock).IsAssignableFrom(property.PropertyType)
                )
        );
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PCMode.Tray;
using PCMode.Tray.Platform.Themes;
using PCMode.Tray.UI.Components.Selection;

namespace PCMode.Tray.Tests.UI.Styles;

/// <summary>
/// The two shipped palettes, loaded the way <c>App.xaml</c> loads them.
/// </summary>
/// <remarks>
/// Every other theme test builds a synthetic dictionary, which is right for
/// asserting the resolution rules but leaves nothing checking the real files:
/// a palette that moved, was renamed, or lost a key would still pass all of
/// them and then fail at startup, because a missing merged dictionary is a
/// XamlParseException out of <c>InitializeComponent</c> and not a compile
/// error. This is the one test that actually resolves the pack URIs.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class ThemePaletteTests
{
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void The_shipped_palette_defines_every_semantic_brush_the_theme_service_reads(string palette)
    {
        Wpf.Run(() =>
        {
            var resources = new ResourceDictionary
            {
                Source = new Uri($"/PCMode.Tray;component/UI/Components/Theme/{palette}.xaml", UriKind.Relative),
            };

            foreach (string key in ApplicationThemeService.SemanticResourceKeys)
            {
                Assert.True(
                    resources.Contains($"{palette}.{key}"),
                    $"UI/Components/Theme/{palette}.xaml does not define '{palette}.{key}'."
                );

                Assert.IsType<SolidColorBrush>(resources[$"{palette}.{key}"]);
            }
        });
    }

    [Fact]
    public void Neither_palette_defines_an_unprefixed_key()
    {
        // Both stay merged for the application's whole life, so an unprefixed
        // key in either would fight the copy ApplicationThemeService writes.
        Wpf.Run(() =>
        {
            foreach (string palette in (string[])["Light", "Dark"])
            {
                var resources = new ResourceDictionary
                {
                    Source = new Uri($"/PCMode.Tray;component/UI/Components/Theme/{palette}.xaml", UriKind.Relative),
                };

                foreach (string key in ApplicationThemeService.SemanticResourceKeys)
                {
                    Assert.False(
                        resources.Contains(key),
                        $"UI/Components/Theme/{palette}.xaml defines the unprefixed '{key}'."
                    );
                }
            }
        });
    }

    [Fact]
    public void Common_controls_realize_owned_semantic_templates_in_dark_mode()
    {
        Wpf.Run(() =>
        {
            ResourceDictionary resources = CompleteTheme();
            using var themes = new ApplicationThemeService(resources, new DarkSystemTheme());
            themes.Apply(ApplicationTheme.Dark);

            var button = new System.Windows.Controls.Button { Content = "Button" };
            var toggle = new ToggleButton { Content = "Toggle" };
            var navigationItem = new ListBoxItem
            {
                Content = "Navigation",
                IsSelected = true,
                Style = (Style)resources["NavigationListItemStyle"],
            };
            var slider = new Slider { Value = 5 };
            var scrollBar = new System.Windows.Controls.Primitives.ScrollBar { Maximum = 100, ViewportSize = 20 };
            var textBox = new System.Windows.Controls.TextBox { Text = "Text" };
            var comboBox = new System.Windows.Controls.ComboBox
            {
                ItemsSource = (string[])["One", "Two"],
                SelectedIndex = 0,
            };
            var toolTip = new System.Windows.Controls.ToolTip
            {
                Content = "Tip",
                Style = (Style)resources[typeof(System.Windows.Controls.ToolTip)],
            };
            var selector = new SelectorItem
            {
                Title = "Selected",
                Summary = "Summary",
                IsSelected = true,
            };

            var host = new StackPanel { Resources = resources };
            host.Children.Add(button);
            host.Children.Add(toggle);
            host.Children.Add(navigationItem);
            host.Children.Add(slider);
            host.Children.Add(scrollBar);
            host.Children.Add(textBox);
            host.Children.Add(comboBox);
            host.Children.Add(selector);

            Wpf.Realize(host);
            Wpf.Realize(toolTip);

            Assert.NotNull(button.Template.FindName("Surface", button));
            Assert.NotNull(toggle.Template.FindName("Surface", toggle));
            Assert.NotNull(navigationItem.Template.FindName("Surface", navigationItem));
            Assert.NotNull(slider.Template.FindName("PART_Track", slider));
            Assert.NotNull(scrollBar.Template.FindName("PART_Track", scrollBar));
            Assert.NotNull(textBox.Template.FindName("Surface", textBox));
            Assert.NotNull(comboBox.Template.FindName("DropDownToggle", comboBox));
            Assert.NotNull(toolTip.Template.FindName("Surface", toolTip));

            var selectedSurface = Assert.IsType<Border>(selector.Template.FindName("Surface", selector));
            Assert.Equal(
                ((SolidColorBrush)resources["AccentBrush"]).Color,
                Assert.IsType<SolidColorBrush>(selectedSurface.BorderBrush).Color
            );

            SolidColorBrush darkSurface = Assert.IsType<SolidColorBrush>(resources["SurfaceBrush"]);
            Assert.Equal(darkSurface.Color, Assert.IsType<SolidColorBrush>(button.Background).Color);
            Assert.Equal(darkSurface.Color, Assert.IsType<SolidColorBrush>(toggle.Background).Color);
            Assert.Equal(darkSurface.Color, Assert.IsType<SolidColorBrush>(textBox.Background).Color);
            Assert.Equal(darkSurface.Color, Assert.IsType<SolidColorBrush>(comboBox.Background).Color);
        });
    }

    private static ResourceDictionary CompleteTheme()
    {
        var resources = new ResourceDictionary();

        foreach (
            string source in (string[])
                [
                    "/PCMode.Tray;component/UI/Components/Theme/Theme.xaml",
                    "/PCMode.Tray;component/UI/Components/Components.xaml",
                    "/PCMode.Tray;component/UI/Components/Theme/Light.xaml",
                    "/PCMode.Tray;component/UI/Components/Theme/Dark.xaml",
                ]
        )
        {
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }

        return resources;
    }

    private sealed class DarkSystemTheme : ISystemThemePreference
    {
        public EffectiveTheme Read() => EffectiveTheme.Dark;
    }
}

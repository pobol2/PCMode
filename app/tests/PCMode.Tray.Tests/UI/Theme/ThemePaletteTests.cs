using System.Windows;
using System.Windows.Media;
using PCMode.Tray.Platform.Themes;
using PCMode.Tray;

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
                    $"UI/Components/Theme/{palette}.xaml does not define '{palette}.{key}'.");

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
                        $"UI/Components/Theme/{palette}.xaml defines the unprefixed '{key}'.");
                }
            }
        });
    }
}

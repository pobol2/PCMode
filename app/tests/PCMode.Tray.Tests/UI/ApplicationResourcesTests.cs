using System.IO;
using System.Text.RegularExpressions;
using PCMode.Tray.Tests.Support;

namespace PCMode.Tray.Tests.UI;

/// <summary>
/// The WPF test host merges the same dictionaries the application does.
/// </summary>
/// <remarks>
/// The host cannot simply construct <c>PCMode.Tray.App</c>: that is
/// process-global state nothing here wants. So it keeps its own list, and this
/// keeps that list honest by reading App.xaml itself. Left unchecked the two
/// drift, and the symptom is a page failing to find a resource in a test for
/// something else entirely.
/// </remarks>
public sealed class ApplicationResourcesTests
{
    [Fact]
    public void The_test_host_merges_exactly_what_App_xaml_merges()
    {
        string[] declared = AppXamlDictionaries();

        Assert.NotEmpty(declared);
        Assert.Equal(declared, Wpf.ApplicationDictionaries.ToArray());
    }

    private static string[] AppXamlDictionaries()
    {
        string path = TraySource.PathTo("App", "App.xaml");
        Assert.True(File.Exists(path), $"App.xaml does not exist at '{path}'.");

        return
        [
            .. Regex
                .Matches(File.ReadAllText(path), @"<ResourceDictionary\s+Source=""([^""]+)""")
                .Select(match => NormalizePackUri(match.Groups[1].Value)),
        ];
    }

    private static string NormalizePackUri(string source) =>
        source.StartsWith("/PCMode.Tray;", StringComparison.Ordinal)
        && !source.StartsWith("/PCMode.Tray;component/", StringComparison.Ordinal)
            ? source.Replace("/PCMode.Tray;", "/PCMode.Tray;component/", StringComparison.Ordinal)
            : source;
}

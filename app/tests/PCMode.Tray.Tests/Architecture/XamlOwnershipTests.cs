using System.IO;
using System.Xml.Linq;
using PCMode.Tray.Tests.Support;

namespace PCMode.Tray.Tests.Architecture;

/// <summary>
/// Enforces ownership expressed in XAML, which CLR reflection cannot see.
/// </summary>
public sealed class XamlOwnershipTests
{
    private static readonly HashSet<string> ElementToTreeSeams =
    [
        Normalize(
            Path.Combine(
                "UI",
                "Tree",
                "Application",
                "MainWindowRegion",
                "AppShell",
                "MainContentRegion",
                "ConfigurationPage",
                "ConfigurationPageBindings.xaml"
            )
        ),
        Normalize(
            Path.Combine(
                "UI",
                "Tree",
                "Application",
                "MainWindowRegion",
                "AppShell",
                "MainContentRegion",
                "ListPage",
                "ListPageBindings.xaml"
            )
        ),
        Normalize(
            Path.Combine(
                "UI",
                "Tree",
                "Application",
                "MainWindowRegion",
                "AppShell",
                "NavigationRegion",
                "PageNavigation",
                "PageNavigationBindings.xaml"
            )
        ),
    ];

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly HashSet<string> ElementBindingMarkup = ["ResourceDictionary", "DataTemplate"];

    [Fact]
    public void Elements_select_tree_nodes_and_components_but_do_not_render_raw_WPF()
    {
        string[] violations =
        [
            .. XamlFiles("Elements")
                .SelectMany(file =>
                    XDocument
                        .Load(file)
                        .Descendants()
                        .Where(element =>
                            element.Name.Namespace == Presentation
                            && !ElementBindingMarkup.Contains(element.Name.LocalName)
                        )
                        .Select(element => $"{Relative(file)} contains raw WPF element <{element.Name.LocalName}>.")
                ),
        ];

        Assert.Empty(violations);
    }

    [Fact]
    public void Tree_xaml_does_not_name_application_elements()
    {
        AssertNoOwnershipReferences(
            XamlFiles(Path.Combine("UI", "Tree"))
                .Where(file => !ElementToTreeSeams.Contains(Normalize(SourceRelative(file)))),
            "PCMode.Tray.Elements",
            "Elements"
        );
    }

    [Fact]
    public void Framework_xaml_does_not_name_tree_components_or_elements()
    {
        string[] files = XamlFiles(Path.Combine("UI", "Framework"));

        AssertNoOwnershipReferences(files, "PCMode.Tray.UI.Tree", "UI/Tree");
        AssertNoOwnershipReferences(files, "PCMode.Tray.UI.Components", "UI/Components");
        AssertNoOwnershipReferences(files, "PCMode.Tray.Elements", "Elements");
    }

    [Fact]
    public void Only_named_element_to_tree_seam_dictionaries_may_reference_both_sides()
    {
        string[] allXaml = XamlFiles(string.Empty);
        string[] crossBoundaryFiles =
        [
            .. allXaml
                .Where(file =>
                {
                    string text = File.ReadAllText(file);
                    return text.Contains("clr-namespace:PCMode.Tray.Elements", StringComparison.Ordinal)
                        && text.Contains("clr-namespace:PCMode.Tray.UI.Tree", StringComparison.Ordinal);
                })
                .Select(SourceRelative)
                .Select(Normalize),
        ];

        Assert.All(crossBoundaryFiles, file => Assert.Contains(file, ElementToTreeSeams));

        Assert.All(
            ElementToTreeSeams,
            seam =>
            {
                Assert.Contains(seam, allXaml.Select(SourceRelative).Select(Normalize));

                string text = File.ReadAllText(TraySource.PathTo(seam.Replace('/', Path.DirectorySeparatorChar)));
                Assert.Contains("clr-namespace:PCMode.Tray.Elements", text, StringComparison.Ordinal);
                Assert.Contains("clr-namespace:PCMode.Tray.UI.Tree", text, StringComparison.Ordinal);
            }
        );
    }

    [Fact]
    public void Element_to_tree_seams_are_resource_dictionaries_of_data_templates()
    {
        foreach (string seam in ElementToTreeSeams)
        {
            string file = TraySource.PathTo(seam.Replace('/', Path.DirectorySeparatorChar));
            XDocument document = XDocument.Load(file);

            Assert.Equal("ResourceDictionary", document.Root?.Name.LocalName);
            Assert.All(document.Root!.Elements(), element => Assert.Equal("DataTemplate", element.Name.LocalName));
        }
    }

    [Fact]
    public void Component_xaml_does_not_name_tree_or_application_elements()
    {
        string[] files = XamlFiles(Path.Combine("UI", "Components"));

        AssertNoOwnershipReferences(files, "PCMode.Tray.UI.Tree", "UI/Tree");
        AssertNoOwnershipReferences(files, "PCMode.Tray.Elements", "Elements");
    }

    [Fact]
    public void App_does_not_own_element_to_tree_bindings()
    {
        string app = File.ReadAllText(TraySource.PathTo("App", "App.xaml"));

        Assert.DoesNotContain("clr-namespace:PCMode.Tray.Elements", app, StringComparison.Ordinal);
    }

    private static string[] XamlFiles(string relativeDirectory)
    {
        string root = TraySource.PathTo(relativeDirectory);

        Assert.True(Directory.Exists(root), $"Tray source directory does not exist: '{root}'.");
        return Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories);
    }

    private static void AssertNoText(IEnumerable<string> files, string forbidden)
    {
        string[] violations =
        [
            .. files
                .Where(file => File.ReadAllText(file).Contains(forbidden, StringComparison.Ordinal))
                .Select(Relative),
        ];

        Assert.Empty(violations);
    }

    private static void AssertNoOwnershipReferences(
        IEnumerable<string> files,
        string forbiddenNamespace,
        string forbiddenPath
    )
    {
        string[] sourceFiles = files.ToArray();
        AssertNoText(sourceFiles, $"clr-namespace:{forbiddenNamespace}");
        AssertNoText(sourceFiles, $"/{forbiddenPath}/");
    }

    private static string Relative(string file) => TraySource.Relative(file);

    private static string SourceRelative(string file) => TraySource.Relative(file);

    private static string Normalize(string path) => path.Replace('\\', '/');
}

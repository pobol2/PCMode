using System;
using System.Windows;
using System.Windows.Controls;
using PCMode.Tray.Tests.UI.Construction.Fixtures;

namespace PCMode.Tray.Tests.UI.Construction;

/// <summary>
/// The fake data every test builds from, and the loader for the layouts
/// they swap in.
/// </summary>
internal static class Probe
{
    /// <summary>
    /// A two-level fixture graph: terminal header, two repeated items, and
    /// a detail that recurses one level further.
    /// </summary>
    public static FixtureRootViewModel Page()
    {
        var page = new FixtureRootViewModel(
            "Displays",
            new FixtureDetailViewModel("Resolution", "Scaling"));

        page.Items.Add(new FixtureItemViewModel("Dell U2720Q"));
        page.Items.Add(new FixtureItemViewModel("LG C2"));
        return page;
    }

    /// <summary>An alternative Layout, from the test fixtures.</summary>
    public static ControlTemplate Layout(string key) =>
        (ControlTemplate)Load("/PCMode.Tray.Tests;component/UI/Construction/Fixtures/AlternateLayouts.xaml")[key];

    /// <summary>A deliberately broken layout, from the test fixtures.</summary>
    public static ControlTemplate InvalidLayout(string key) =>
        (ControlTemplate)Load("/PCMode.Tray.Tests;component/UI/Construction/Fixtures/InvalidLayouts.xaml")[key];

    private static ResourceDictionary Load(string relativeUri) =>
        new() { Source = new Uri(relativeUri, UriKind.Relative) };
}

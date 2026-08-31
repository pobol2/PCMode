using System.Windows;
using System.Windows.Controls;
using PCMode.Tray.Elements;
using PCMode.Tray.Elements.Navigation;
using PCMode.Tray.UI.Framework;
using PCMode.Tray.UI.Tree;
using Region = PCMode.Tray.UI.Framework.Region;

namespace PCMode.Tray.Tests.Shell;

/// <summary>
/// The shell window builds, and opening it lands on the first navigation entry.
/// </summary>
/// <remarks>
/// Cheap, and the only test that resolves MainWindow's four StaticResource
/// lookups against the real shared dictionaries. Those reach application
/// scope, so a style that moved or was renamed is a XamlParseException out of
/// InitializeComponent rather than a compile error -- invisible to every other
/// test here, and fatal at startup.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class MainWindowTests
{
    /// <summary>Stands in for a page's view model.</summary>
    private sealed class StubPage : IPageLifecycle
    {
        public int Activations { get; private set; }

        public int Deactivations { get; private set; }

        public void OnActivated() => Activations++;

        public void OnDeactivated() => Deactivations++;
    }
}

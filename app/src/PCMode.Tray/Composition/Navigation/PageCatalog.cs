using System.Windows;
using PCMode.Engine;
using PCMode.Tray.Configuration;
using PCMode.Tray.Diagnostics;
using PCMode.Tray.Features.Applications;
using PCMode.Tray.Features.Audio;
using PCMode.Tray.Features.Cursor;
using PCMode.Tray.Features.Devices;
using PCMode.Tray.Features.Diagnostics;
using PCMode.Tray.Features.Home;
using PCMode.Tray.Features.Hardware;
using PCMode.Tray.Features.Layouts;
using PCMode.Tray.Features.Rgb;
using PCMode.Tray.Features.Settings;
using PCMode.Tray.Features.Spaces;
using PCMode.Tray.Features.Television;
using PCMode.Tray.Features.Theme;
using PCMode.Tray.Settings;

namespace PCMode.Tray.Navigation;

public sealed class PageCatalog
{
    private readonly IReadOnlyDictionary<PageId, Func<FrameworkElement>> _factories;

    /// <param name="party">
    /// The process's one shared <see cref="PartyAudioHost"/>. Audio does not
    /// need it directly -- <see cref="IConfigurationWorkspace"/> already
    /// builds every <c>AudioProvider</c> with it -- but Spaces builds its own
    /// engine directly to activate a target, so it takes the same host
    /// explicitly to stay consistent with what Audio reports. Null is fine
    /// outside a hosted process (e.g. tests): Spaces simply has nothing live
    /// to hand a Party group it activates.
    /// </param>
    /// <param name="runtime">
    /// The process's one live <see cref="EngineRuntime"/>: Home's target
    /// grid, Diagnostics' dry run, and Spaces' current-target badge all
    /// read it fresh on each activation, and Home applies a target through
    /// it directly. Every other page reads fresh from disk through
    /// <paramref name="workspace"/> instead. Null is fine outside a hosted
    /// process (e.g. tests): these pages simply have nothing live to report.
    /// </param>
    /// <param name="log">
    /// The process's one shared <see cref="LocalLog"/>, for Diagnostics'
    /// recent-log tail and "open logs" action. Null outside a hosted process.
    /// </param>
    /// <param name="settingsStore">
    /// The process's one <see cref="ApplicationSettingsStore"/>, for the
    /// Settings page, for Spaces' quick-access section (picker favorite,
    /// direct shortcut, and the replacement a delete needs when it would
    /// otherwise leave one dangling), and for Home's Favorites group. Null
    /// outside a hosted process (e.g. tests): Settings has nothing to load
    /// or save, Spaces hides quick access entirely, and Home shows no
    /// Favorites group.
    /// </param>
    /// <param name="openSetupWizard">
    /// Opens the guided setup wizard, for Settings' "Guided setup" entry
    /// point. Null hides that entry point entirely (matching legacy) --
    /// outside a hosted process there is nowhere for it to open a window.
    /// </param>
    /// <param name="onSettingsSaved">
    /// Runs after Settings, or Spaces' quick-access section, persists a
    /// change, so the composition root can react to what only it owns --
    /// applying a new app theme, restarting the picker with new
    /// favorites/shortcuts. Null outside a hosted process (e.g. tests):
    /// saves still happen, nothing reacts live.
    /// </param>
    public PageCatalog(
        IConfigurationWorkspace workspace,
        PartyAudioHost? party = null,
        EngineRuntime? runtime = null,
        LocalLog? log = null,
        ApplicationSettingsStore? settingsStore = null,
        Action? openSetupWizard = null,
        Action<ApplicationSettings>? onSettingsSaved = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        _factories = new Dictionary<PageId, Func<FrameworkElement>>
        {
            [PageId.Home] = () => new HomePage(new HomeViewModel(runtime, settingsStore)),

            [PageId.Devices] = () =>
                new DevicesPage(
                    new HardwareViewModel(workspace)),

            [PageId.Applications] = () =>
                new ApplicationsPage(
                    new ApplicationsViewModel(workspace)),

            [PageId.Audio] = () =>
                new AudioPage(
                    new AudioViewModel(workspace)),

            [PageId.Cursor] = () =>
                new CursorPage(
                    new CursorViewModel(workspace)),

            [PageId.Rgb] = () =>
                new RgbPage(
                    new RgbViewModel(workspace)),

            [PageId.Theme] = () =>
                new ThemePage(
                    new ThemeViewModel(workspace)),

            [PageId.Layouts] = () =>
                new LayoutsPage(
                    new LayoutsViewModel(workspace)),

            [PageId.Television] = () =>
                new TelevisionPage(
                    new TelevisionViewModel(
                        workspace,
                        livePowerProvider: runtime is null
                            ? null
                            : name => runtime.Engine.Providers
                                .OfType<TvPowerProvider>()
                                .FirstOrDefault(provider =>
                                    string.Equals(provider.TelevisionName, name, StringComparison.OrdinalIgnoreCase)))),

            [PageId.Spaces] = () =>
                new SpacesPage(
                    new SpacesViewModel(workspace, party, runtime, settingsStore, onSettingsSaved)),

            [PageId.Settings] = () =>
                new SettingsPage(
                    new SettingsViewModel(workspace, settingsStore, onSettingsSaved),
                    openSetupWizard),

            [PageId.Diagnostics] = () =>
                new DiagnosticsPage(
                    new DiagnosticsViewModel(workspace, runtime, log)),
        };
    }

    public FrameworkElement Create(PageId page)
    {
        if (!_factories.TryGetValue(page, out var factory))
        {
            throw new InvalidOperationException(
                $"No page factory is registered for '{page}'.");
        }

        return factory();
    }

    public void Validate(IEnumerable<NavigationItem> navigationItems)
    {
        ArgumentNullException.ThrowIfNull(navigationItems);

        NavigationItem[] items = navigationItems.ToArray();
        var errors = new List<string>();

        ValidateDuplicatePages(items, errors);
        ValidateParents(items, errors);
        ValidateCycles(items, errors);
        ValidateFactories(items, errors);

        if (errors.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Navigation configuration is invalid:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                errors.Select(error => $" - {error}")));
    }

    private static void ValidateDuplicatePages(
        IReadOnlyCollection<NavigationItem> items,
        ICollection<string> errors)
    {
        PageId[] duplicates = items
            .GroupBy(item => item.Page)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(page => page)
            .ToArray();

        foreach (PageId page in duplicates)
        {
            errors.Add(
                $"Page '{page}' appears more than once in navigation.");
        }
    }

    private static void ValidateParents(
        IReadOnlyCollection<NavigationItem> items,
        ICollection<string> errors)
    {
        HashSet<PageId> registeredPages =
            items
                .Select(item => item.Page)
                .ToHashSet();

        foreach (NavigationItem item in items)
        {
            if (item.Parent is not PageId parent)
            {
                continue;
            }

            if (parent == item.Page)
            {
                errors.Add(
                    $"Page '{item.Page}' cannot be its own parent.");

                continue;
            }

            if (!registeredPages.Contains(parent))
            {
                errors.Add(
                    $"Page '{item.Page}' references missing parent '{parent}'.");
            }
        }
    }

    private static void ValidateCycles(
        IReadOnlyCollection<NavigationItem> items,
        ICollection<string> errors)
    {
        Dictionary<PageId, PageId?> parentByPage =
            items
                .GroupBy(item => item.Page)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().Parent);

        var visited = new HashSet<PageId>();

        foreach (PageId page in parentByPage.Keys)
        {
            if (visited.Contains(page))
            {
                continue;
            }

            var path = new List<PageId>();
            var pathIndexes = new Dictionary<PageId, int>();

            PageId current = page;

            while (true)
            {
                if (pathIndexes.TryGetValue(current, out int cycleStartIndex))
                {
                    PageId[] cycle = path
                        .Skip(cycleStartIndex)
                        .Append(current)
                        .ToArray();

                    errors.Add(
                        $"Navigation hierarchy contains a cycle: "
                        + string.Join(" -> ", cycle)
                        + ".");

                    break;
                }

                if (visited.Contains(current))
                {
                    break;
                }

                pathIndexes[current] = path.Count;
                path.Add(current);

                if (!parentByPage.TryGetValue(current, out PageId? parent)
                    || parent is null)
                {
                    break;
                }

                if (parent.Value == current)
                {
                    // Already reported by ValidateParents().
                    break;
                }

                if (!parentByPage.ContainsKey(parent.Value))
                {
                    // Already reported as a missing parent.
                    break;
                }

                current = parent.Value;
            }

            foreach (PageId pathPage in path)
            {
                visited.Add(pathPage);
            }
        }
    }

    private void ValidateFactories(
        IReadOnlyCollection<NavigationItem> items,
        ICollection<string> errors)
    {
        PageId[] missingFactories = items
            .Select(item => item.Page)
            .Distinct()
            .Where(page => !_factories.ContainsKey(page))
            .OrderBy(page => page)
            .ToArray();

        foreach (PageId page in missingFactories)
        {
            errors.Add(
                $"Page '{page}' is navigable but has no registered page factory.");
        }
    }
}
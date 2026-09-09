using System.Windows.Input;
using PCMode.Engine;
using PCMode.Tray.Elements;
using PCMode.Tray.Elements.Configuration;

namespace PCMode.Tray.Elements.Displays.Sections;

/// <summary>One monitor Windows has a slot for, and what PCMode calls it.</summary>
public sealed class DiscoveredDisplayRow(HardwareItem item) : IHardwareInventoryRow
{
    public string Name => item.Name;

    public string? DevicePath => item.Identifier;

    public string? Alias => item.Alias;

    public bool IsRegistered => item.IsConfigured;

    /// <summary>The PCMode alias, or the raw Windows name for a monitor not yet registered.</summary>
    public string FriendlyName => item.Alias ?? item.Name;

    /// <summary>
    /// Whether the underlying Windows device name is worth a line of its own --
    /// only once <see cref="FriendlyName"/> has diverged from it by being aliased.
    /// </summary>
    public bool ShowUnderlyingDevice => !string.Equals(FriendlyName, item.Name, StringComparison.Ordinal);

    public string ActionLabel => IsRegistered ? "Rename" : "Add to PCMode";

    /// <summary>Whether this monitor can be named at all -- it needs a device path to alias.</summary>
    public bool CanAlias => DevicePath is not null;

    /// <summary>
    /// What this card's badge says, mirroring <see cref="Audio.DiscoveredAudioOutputRow.BadgeText"/>.
    /// A monitor has no "currently active" signal the way an audio output does,
    /// so this is always null -- the badge stays in the header shape, just never shown.
    /// </summary>
    public string? BadgeText => null;

    public bool HasBadge => BadgeText is not null;

    /// <summary>IHardwareInventoryRow.CanUseActions: whether the alias action applies to this row.</summary>
    public bool CanUseActions => CanAlias;

    /// <summary>
    /// IHardwareInventoryRow.ShowActivate: a monitor has no "switch to this
    /// one" concept the way an audio output does -- selecting which monitors
    /// are active happens through a display layout, not this row.
    /// </summary>
    public bool ShowActivate => false;

    public bool CanActivate => false;
}

/// <summary>
/// Which monitors this machine has, and what PCMode calls each of them --
/// the registration step every other part of this page depends on.
/// </summary>
/// <remarks>
/// DISPLAY-SCOPED REGISTRATION
///
/// Naming a monitor is the
/// first step of configuring displays, and everything else on this page
/// (layouts, DDC/CI bindings, television links) is addressed by the names it
/// creates.
///
/// A monitor with no picture right now is not an error -- Windows keeps a
/// slot for every target it has ever detected, including a television that
/// is switched off, and <see cref="DisplayDiscoverer"/> reports that as "not
/// detected" rather than leaving it out.
///
/// WRITES IMMEDIATELY, AND THE PAGE RELOADS
///
/// An alias changes which monitors every layout on this page can arrange, so
/// <paramref name="onRegistered"/> asks the page to reload itself rather
/// than this section trying to patch what it cannot see.
/// </remarks>
public sealed class DisplayRegistrationSectionViewModel : IConfigurationSection
{
    private const string Category = "Displays";

    private readonly IConfigurationWorkspace _workspace;
    private readonly Action _onRegistered;
    private readonly IConfigurationPrompts _prompts;
    private readonly Func<bool>? _guardUnsavedChanges;

    private bool _isSavingAlias;

    /// <param name="onRegistered">
    /// Called after an alias is written, so the page can rebuild the monitor
    /// list its layouts are arranged from.
    /// </param>
    /// <param name="prompts">How this section asks for a monitor's PCMode name; see <see cref="IConfigurationPrompts"/>.</param>
    /// <param name="guardUnsavedChanges">The page's own unsaved-changes guard, run before naming a monitor reloads the page. False means the action must not proceed.</param>
    public DisplayRegistrationSectionViewModel(
        IConfigurationWorkspace workspace,
        Action onRegistered,
        IConfigurationPrompts? prompts = null,
        Func<bool>? guardUnsavedChanges = null
    )
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(onRegistered);

        _workspace = workspace;
        _onRegistered = onRegistered;
        _prompts = prompts ?? NoConfigurationPrompts.Instance;
        _guardUnsavedChanges = guardUnsavedChanges;
        Inventory = new HardwareInventoryList<DiscoveredDisplayRow>(ScanRowsAsync);

        RescanCommand = new AsyncRelayCommand(Inventory.ScanAsync, () => Inventory.CanScan);
        AliasCommand = new RelayCommand<DiscoveredDisplayRow>(PromptAlias, row => row.CanUseActions);

        Inventory.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HardwareInventoryList<DiscoveredDisplayRow>.CanScan))
            {
                (RescanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        };
    }

    public ICommand RescanCommand { get; }

    /// <summary>Names one discovered monitor, or renames an already-registered one.</summary>
    public ICommand AliasCommand { get; }

    private void PromptAlias(DiscoveredDisplayRow row)
    {
        if (_guardUnsavedChanges?.Invoke() == false)
        {
            return;
        }

        _prompts.PromptForNameAsync(
            row.IsRegistered ? "Rename monitor" : "Add monitor to PCMode",
            $"Give '{row.Name}' a PCMode name. Layouts, DDC/CI and televisions all address it by this name.",
            row.Alias ?? row.Name,
            alias => SetAliasAsync(row, alias)
        );
    }

    public HardwareInventoryList<DiscoveredDisplayRow> Inventory { get; }

    /// <summary>
    /// Nothing to load per selected entity: this section is about the
    /// machine, not about whichever layout or monitor is selected. The
    /// entity key is accepted and ignored so the page can treat every
    /// section the same way.
    /// </summary>
    public void Load(object? entityKey) { }

    public string? Save() => null;

    /// <summary>
    /// Gives a monitor the PCMode name every layout, DDC/CI binding and
    /// television link on this page addresses it by.
    /// </summary>
    public Task<string?> SetAliasAsync(DiscoveredDisplayRow row, string alias)
    {
        ArgumentNullException.ThrowIfNull(row);
        return SetAliasAsync(row.DevicePath, row.Name, alias);
    }

    /// <summary>
    /// Renames an already-registered monitor by device path directly -- what
    /// the Monitor settings card under the arrangement uses, since it already
    /// knows the device path from <see cref="Layouts.LayoutMonitorRow"/>
    /// without needing a fresh scan of "Monitors on this PC" first.
    /// </summary>
    public Task<string?> RenameAsync(string? devicePath, string currentName, string alias) =>
        SetAliasAsync(devicePath, currentName, alias);

    private async Task<string?> SetAliasAsync(string? devicePath, string currentName, string alias)
    {
        if (devicePath is null)
        {
            return "Windows is not reporting a device path for this monitor.";
        }

        if (string.IsNullOrWhiteSpace(alias))
        {
            return "Give this monitor a name.";
        }

        if (_isSavingAlias || Inventory.IsBusy)
        {
            return "PCMode is already busy with another display action.";
        }

        string trimmed = alias.Trim();
        _isSavingAlias = true;

        try
        {
            HardwareAliasOutcome outcome = await Task.Run(() =>
                    HardwareConfigurationStore.SetDisplayAlias(_workspace.Root, devicePath, trimmed)
                )
                .ConfigureAwait(true);

            if (!outcome.Success)
            {
                string problem = outcome.Problem ?? "That could not be saved.";
                _workspace.Warn(Category, problem);
                return problem;
            }

            _workspace.Apply($"'{currentName}' is now '{trimmed}'.");
        }
        finally
        {
            _isSavingAlias = false;
        }

        // The layouts on this page are arranged from the monitor names this
        // just changed, so the page reloads rather than this section trying
        // to patch what it does not own.
        _onRegistered();
        await Inventory.ScanAsync().ConfigureAwait(true);
        return null;
    }

    private async Task<(IReadOnlyList<DiscoveredDisplayRow> Rows, string? Problem)> ScanRowsAsync()
    {
        try
        {
            HardwareCategoryResult result = await Task.Run(Scan).ConfigureAwait(true);
            return ([.. result.Items.Select(item => new DiscoveredDisplayRow(item))], result.Problem);
        }
        catch (Exception exception)
        {
            _workspace.Fault(Category, "The display scan failed.", exception);
            return ([], $"The monitors could not be read. {exception.Message}");
        }
    }

    private HardwareCategoryResult Scan()
    {
        DisplayProvider display =
            _workspace.ReadProviders().OfType<DisplayProvider>().FirstOrDefault() ?? new DisplayProvider();

        return HardwareInspector.Discover([new DisplayDiscoverer(display)]).Single();
    }
}

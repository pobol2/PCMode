using System.Collections.ObjectModel;
using PCMode.Engine;
using PCMode.Tray.Configuration;
using PCMode.Tray.Presentation;

namespace PCMode.Tray.Features.Hardware;

/// <summary>
/// Reports what PCMode can currently see on this machine -- displays, audio
/// outputs, controllers, and applications already named in configuration --
/// and offers the one narrow write legacy's Hardware page had: giving a
/// display or audio device a PCMode alias.
/// </summary>
/// <remarks>
/// NOT A CONFIGURATION PAGE
///
/// Nothing here is loaded into an editor, there is no unsaved state, and
/// nothing is ever activated against a mode. It refreshes on every visit,
/// not just the first, because the entire point is to say what is true
/// right now.
/// </remarks>
public sealed class HardwareViewModel : ObservableObject
{
    private const string LogCategory = "Hardware";

    private readonly IConfigurationWorkspace _workspace;
    private readonly IGamepadCapabilitiesSource _controllers = XInputCapabilitiesSource.Load();

    private bool _isBusy;
    private string? _statusMessage;

    public HardwareViewModel(IConfigurationWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
    }

    public ObservableCollection<HardwareCategoryRow> Categories { get; } = [];

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            await RunScanAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Sets the item's alias through <see cref="HardwareConfigurationStore"/>, then rescans.</summary>
    public async Task<string?> SetAliasAsync(HardwareItemRow item, string alias)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!item.SupportsAlias)
        {
            return $"'{item.Name}' does not support an alias.";
        }

        if (string.IsNullOrWhiteSpace(alias))
        {
            return "Give it a name.";
        }

        if (IsBusy)
        {
            return "PCMode is already busy with another hardware action.";
        }

        string trimmed = alias.Trim();
        string identifier = item.Identifier!;
        IsBusy = true;

        try
        {
            HardwareAliasOutcome outcome = await Task.Run(() => item.Category switch
            {
                DisplayDiscoverer.CategoryName =>
                    HardwareConfigurationStore.SetDisplayAlias(_workspace.Root, identifier, trimmed),
                AudioOutputDiscoverer.CategoryName =>
                    HardwareConfigurationStore.SetAudioAlias(_workspace.Root, identifier, trimmed),
                _ => HardwareAliasOutcome.Failed($"'{item.Category}' items do not support an alias."),
            }).ConfigureAwait(true);

            if (!outcome.Success)
            {
                StatusMessage = outcome.Problem ?? "That could not be saved.";
                _workspace.Warn(LogCategory, StatusMessage);
                return outcome.Problem;
            }

            StatusMessage = _workspace.Apply($"'{item.Name}' alias saved.");
            await RunScanAsync().ConfigureAwait(true);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunScanAsync()
    {
        StatusMessage = "Scanning hardware...";

        try
        {
            IReadOnlyList<HardwareCategoryResult> results = await Task.Run(RunDiscovery).ConfigureAwait(true);

            Categories.Clear();

            foreach (HardwareCategoryResult result in results)
            {
                Categories.Add(new HardwareCategoryRow(result));
            }

            int totalItems = results.Sum(static result => result.Items.Count);
            int problems = results.Count(static result => result.Problem is not null);

            StatusMessage = problems == 0
                ? $"Found {totalItems} item(s) across {results.Count} categor{(results.Count == 1 ? "y" : "ies")}."
                : $"Found {totalItems} item(s), {problems} categor{(problems == 1 ? "y" : "ies")} with a problem.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Hardware scan failed. {exception.Message}";
            _workspace.Fault(LogCategory, "Hardware scan failed.", exception);
        }
    }

    private IReadOnlyList<HardwareCategoryResult> RunDiscovery()
    {
        IReadOnlyList<IModeResourceProvider> providers = _workspace.ReadProviders();

        DisplayProvider display = providers.OfType<DisplayProvider>().FirstOrDefault() ?? new DisplayProvider();
        AudioProvider audio = providers.OfType<AudioProvider>().FirstOrDefault() ?? new AudioProvider();

        IReadOnlyList<IHardwareDiscoverer> discoverers =
        [
            new DisplayDiscoverer(display),
            new AudioOutputDiscoverer(audio),
            new ControllerDiscoverer(_controllers),
            new ApplicationDiscoverer(providers),
        ];

        return HardwareInspector.Discover(discoverers);
    }
}

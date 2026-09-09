using PCMode.Engine;
using PCMode.Tray.Elements;
using PCMode.Tray.Elements.Configuration;

namespace PCMode.Tray.Elements.Audio;

/// <summary>One Windows playback endpoint and the PCMode alias, if any, that names it.</summary>
public sealed class DiscoveredAudioOutputRow(HardwareItem item) : IHardwareInventoryRow
{
    public string Name => item.Name;

    public string? EndpointName => item.Identifier;

    public string? Alias => item.Alias;

    public string Summary => item.Summary;

    public IReadOnlyList<HardwareDetail> Details => item.Details;

    public bool HasDetails => Details.Count > 0;

    public bool IsConfigured => item.IsConfigured;

    public bool SupportsAlias => item.SupportsAlias;

    /// <summary>The PCMode alias, or the raw Windows name for an endpoint not yet added to PCMode.</summary>
    public string FriendlyName => item.Alias ?? item.Name;

    /// <summary>
    /// Whether the underlying Windows device name is worth a line of its own --
    /// only once <see cref="FriendlyName"/> has diverged from it by being aliased.
    /// </summary>
    public bool ShowUnderlyingDevice => !string.Equals(FriendlyName, item.Name, StringComparison.Ordinal);

    public string ActionLabel => IsConfigured ? "Rename" : "Add to PCMode";

    /// <summary>
    /// Whether this is the endpoint Windows currently plays into. Read off
    /// <see cref="AudioOutputDiscoverer.DescribeOutputs"/>'s own "Default"
    /// detail rather than duplicating that computation here.
    /// </summary>
    public bool IsDefault => item.Details.Any(detail => detail.Label == "Default" && detail.Value == "Yes");

    /// <summary>
    /// Whether Switch makes sense for this row: it needs an alias to hand
    /// <see cref="PCMode.Engine.AudioProvider.Apply"/>, and there is nothing to
    /// switch to when Windows is already playing into it.
    /// </summary>
    public bool CanSwitch => IsConfigured && !IsDefault;

    /// <summary>Shown instead of Switch once this output already is the current one.</summary>
    public bool ShowCurrentState => IsConfigured && IsDefault;

    /// <summary>
    /// Whether this is PCMode's own virtual endpoint -- read off the same
    /// "PCMode virtual endpoint" detail <see cref="AudioOutputDiscoverer"/>
    /// adds for it, the same way <see cref="IsDefault"/> reads "Default".
    /// </summary>
    public bool IsVirtualEndpoint =>
        item.Details.Any(detail => detail.Label == "PCMode virtual endpoint" && detail.Value == "Yes");

    /// <summary>
    /// What this card's badge says, or null for a plain configured output with
    /// nothing further to call out. Every active audio endpoint currently
    /// reports <see cref="HardwareItemStatus.Ok"/> -- a health dot here would
    /// never actually vary -- so this replaces it with the one fact that does
    /// vary: whether this is the one Windows currently plays into.
    /// </summary>
    public string? BadgeText => ShowCurrentState ? "Current" : null;

    public bool HasBadge => BadgeText is not null;

    /// <summary>IHardwareInventoryRow.CanUseActions: whether the alias action applies to this row.</summary>
    public bool CanUseActions => SupportsAlias;

    /// <summary>IHardwareInventoryRow.ShowActivate: an audio output always offers Switch.</summary>
    public bool ShowActivate => true;

    /// <summary>IHardwareInventoryRow.CanActivate: whether Switch can be used right now.</summary>
    public bool CanActivate => CanSwitch;
}

/// <summary>
/// Discovers Windows playback endpoints and owns their PCMode aliases. Audio targets and
/// configured mappings remain in <see cref="AudioViewModel"/>; this section is the machine-facing
/// registration/status view that feeds those mappings.
/// </summary>
public sealed class AudioDeviceDiscoveryViewModel
{
    private const string Category = "Audio";

    private readonly IConfigurationWorkspace _workspace;
    private readonly Action _onAliasSaved;
    private readonly Func<HardwareCategoryResult>? _scan;

    private bool _isSavingAlias;

    public AudioDeviceDiscoveryViewModel(
        IConfigurationWorkspace workspace,
        Action onAliasSaved,
        Func<HardwareCategoryResult>? scan = null
    )
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(onAliasSaved);

        _workspace = workspace;
        _onAliasSaved = onAliasSaved;
        _scan = scan;
        Inventory = new HardwareInventoryList<DiscoveredAudioOutputRow>(ScanRowsAsync);
    }

    public HardwareInventoryList<DiscoveredAudioOutputRow> Inventory { get; }

    /// <summary>Uses a snapshot the Audio editor already read, avoiding a second Core Audio query.</summary>
    public void Update(AudioSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_isSavingAlias || Inventory.IsBusy)
        {
            return;
        }

        HardwareCategoryResult result = snapshot.Available
            ? new HardwareCategoryResult(
                AudioOutputDiscoverer.CategoryName,
                AudioOutputDiscoverer.DescribeOutputs(snapshot),
                Problem: null
            )
            : new HardwareCategoryResult(
                AudioOutputDiscoverer.CategoryName,
                [],
                snapshot.Problem ?? "The audio outputs could not be read."
            );

        Apply(result);
    }

    public void ReportProblem(string problem) =>
        Apply(new HardwareCategoryResult(AudioOutputDiscoverer.CategoryName, [], problem));

    public async Task<string?> SetAliasAsync(DiscoveredAudioOutputRow row, string alias)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.EndpointName is not { } endpointName)
        {
            return "Windows is not reporting a unique name for this audio output.";
        }

        if (string.IsNullOrWhiteSpace(alias))
        {
            return "Give this audio output a name.";
        }

        if (_isSavingAlias || Inventory.IsBusy)
        {
            return "PCMode is already busy with another audio action.";
        }

        string trimmed = alias.Trim();
        _isSavingAlias = true;

        try
        {
            HardwareAliasOutcome outcome = await Task.Run(() =>
                    HardwareConfigurationStore.SetAudioAlias(_workspace.Root, endpointName, trimmed)
                )
                .ConfigureAwait(true);

            if (!outcome.Success)
            {
                string problem = outcome.Problem ?? "That could not be saved.";
                _workspace.Warn(Category, problem);
                return problem;
            }

            _workspace.Apply($"'{row.Name}' is now '{trimmed}'.");
        }
        finally
        {
            _isSavingAlias = false;
        }

        _onAliasSaved();
        await Inventory.ScanAsync().ConfigureAwait(true);
        return null;
    }

    private async Task<(IReadOnlyList<DiscoveredAudioOutputRow> Rows, string? Problem)> ScanRowsAsync()
    {
        try
        {
            return Project(await Task.Run(Scan).ConfigureAwait(true));
        }
        catch (Exception exception)
        {
            _workspace.Fault(Category, "The audio output scan failed.", exception);
            return ([], $"The audio outputs could not be read. {exception.Message}");
        }
    }

    private HardwareCategoryResult Scan()
    {
        if (_scan is not null)
        {
            return _scan();
        }

        AudioProvider audio =
            _workspace.ReadProviders().OfType<AudioProvider>().FirstOrDefault() ?? new AudioProvider();

        return HardwareInspector.Discover([new AudioOutputDiscoverer(audio)]).Single();
    }

    private void Apply(HardwareCategoryResult result)
    {
        (IReadOnlyList<DiscoveredAudioOutputRow> rows, string? problem) = Project(result);
        Inventory.Apply(rows, problem);
    }

    private static (IReadOnlyList<DiscoveredAudioOutputRow> Rows, string? Problem) Project(
        HardwareCategoryResult result
    )
    {
        var rows = new List<DiscoveredAudioOutputRow>();

        // PCMode's own virtual endpoint is where audio targets send sound in,
        // not a piece of hardware someone can Rename or Switch to -- it has no
        // place in a list of discovered devices.
        foreach (HardwareItem item in result.Items)
        {
            var row = new DiscoveredAudioOutputRow(item);

            if (!row.IsVirtualEndpoint)
            {
                rows.Add(row);
            }
        }

        return (rows, result.Problem);
    }
}

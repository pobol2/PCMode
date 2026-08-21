using System.Collections.ObjectModel;
using PCMode.Engine;
using PCMode.Tray.Configuration;
using PCMode.Tray.Presentation;

namespace PCMode.Tray.Features.Television;

/// <summary>
/// Edits named televisions graphically: connection details, inputs, and picture
/// profiles for the television currently selected in the setup list.
/// </summary>
/// <remarks>
/// NAMED TELEVISIONS
///
/// <see cref="LgTvConfigurationStore"/> owns the collection on disk. This view
/// model owns selection and edit state only. Switching selection reloads that
/// television's saved body; any unsaved edit is silently discarded, the same
/// contract every other configuration page here already has.
///
/// PAIRING IS A REAL NETWORK OPERATION
///
/// "Pair" opens a socket to the selected real device and puts a prompt on its
/// screen. It is never called from <see cref="Refresh"/>, only from an explicit
/// action, so merely opening or switching this page cannot surprise somebody
/// with a pairing prompt.
///
/// EVERY NETWORK OPERATION GOES THROUGH THE RUNNING ENGINE'S OWN PROVIDER
///
/// Pairing, reading a status and the ambient refresh all ask
/// <see cref="_livePowerProvider"/> for the television, rather than building a
/// second one out of the configuration file. Two providers for one television
/// would be two connections to one address -- and the whole of what serializes
/// webOS operations is that a television's four resources share one. A status
/// check that opened its own socket could land in the middle of a transition
/// already talking to the same device.
///
/// The configuration is only read directly when the runtime does not have the
/// television yet, which is a page open before the engine exists.
///
/// NO ALL-TELEVISIONS PROVIDER
///
/// The engine only ever builds resources for the catalog's one default
/// television (projected onto the historical tvPower/tvInput/... keys), so
/// unlike a page such as Cursor or Layouts, the full named catalog is read
/// directly through <see cref="LgTvConfigurationStore.Read"/> rather than
/// through <see cref="ConfigurationPageViewModelBase.ReadProviders{TProvider}"/>.
/// </remarks>
public sealed class TelevisionViewModel : ConfigurationPageViewModelBase
{
    private const string Category = "Television";

    private readonly Func<string?, TvPowerProvider?>? _livePowerProvider;

    private string? _defaultTelevision;

    private bool _enabled;
    private string _host = string.Empty;
    private string _macAddress = string.Empty;
    private string _safeDisplayTarget = string.Empty;
    private string? _powerStateText;

    /// <param name="livePowerProvider">
    /// The running engine's own television, by name. Null when nothing is
    /// running -- a test, or a page built before the runtime exists.
    /// </param>
    public TelevisionViewModel(
        IConfigurationWorkspace workspace,
        Func<string?, TvPowerProvider?>? livePowerProvider = null)
        : base(workspace)
    {
        _livePowerProvider = livePowerProvider;
    }

    /// <summary>Presentation-ready selector items, including which television is the default.</summary>
    public ObservableCollection<TelevisionListItem> TelevisionItems { get; } = [];

    public ObservableCollection<TelevisionInputRow> Inputs { get; } = [];

    public ObservableCollection<TelevisionProfileRow> Profiles { get; } = [];

    public string? SelectedTelevision
    {
        get => (string?)SelectedKey;
        set
        {
            string? normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

            if (string.Equals((string?)SelectedKey, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Reload(normalized);
        }
    }

    public override void SelectKey(object key) => SelectedTelevision = key as string;

    public string? DefaultTelevision
    {
        get => _defaultTelevision;
        private set
        {
            if (SetProperty(ref _defaultTelevision, value))
            {
                OnPropertyChanged(nameof(IsSelectedDefault));
                OnPropertyChanged(nameof(CanSetSelectedAsDefault));
            }
        }
    }

    public bool HasSelectedTelevision => SelectedTelevision is not null;

    public bool IsSelectedDefault =>
        SelectedTelevision is not null &&
        _defaultTelevision is not null &&
        string.Equals(SelectedTelevision, _defaultTelevision, StringComparison.OrdinalIgnoreCase);

    public bool CanSetSelectedAsDefault => HasSelectedTelevision && !IsSelectedDefault;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetDirtyProperty(ref _enabled, value))
            {
                RaiseValidation();
            }
        }
    }

    public string Host
    {
        get => _host;
        set
        {
            if (SetDirtyProperty(ref _host, value))
            {
                RaiseValidation();
            }
        }
    }

    public string MacAddress
    {
        get => _macAddress;
        set
        {
            if (SetDirtyProperty(ref _macAddress, value))
            {
                RaiseValidation();
            }
        }
    }

    /// <summary>The display layout to return to before the selected television is switched off.</summary>
    public string SafeDisplayTarget
    {
        get => _safeDisplayTarget;
        set => SetDirtyProperty(ref _safeDisplayTarget, value);
    }

    public bool HasMultipleInputs => Inputs.Count > 1;

    /// <summary>What was last read from the selected television.</summary>
    public string? PowerStateText
    {
        get => _powerStateText;
        private set => SetProperty(ref _powerStateText, value);
    }

    public override string? ValidationProblem
    {
        get
        {
            if (!HasSelectedTelevision || !_enabled)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(_host))
            {
                return "Give the television's address on your network, or turn it off.";
            }

            if (string.IsNullOrWhiteSpace(_macAddress))
            {
                return "Give the television's MAC address -- switching it on needs it -- or turn it off.";
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (TelevisionInputRow row in Inputs)
            {
                if (string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Input))
                {
                    return "Every input needs both a name and a socket. Remove an empty row instead of leaving it blank.";
                }

                if (!names.Add(row.Name.Trim()))
                {
                    return $"'{row.Name}' is used for more than one input.";
                }
            }

            var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (TelevisionProfileRow profile in Profiles)
            {
                if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.PictureMode))
                {
                    return "Every profile needs both a name and a picture mode.";
                }

                if (!profileNames.Add(profile.Name.Trim()))
                {
                    return $"'{profile.Name}' is used for more than one profile.";
                }

                // Only a profile that actually sets something per HDMI socket
                // needs to say which input it is for -- a profile that changes
                // only the picture, or a machine with one input to mean, needs
                // nothing named here at all.
                bool needsInput = profile.Signal.Any(setting => LgTvProfile.SignalUsesHdmi(setting.Key));

                if (needsInput)
                {
                    if (Inputs.Count == 0)
                    {
                        return $"'{profile.Name}' needs an input before using HDMI-specific advanced options.";
                    }

                    if (Inputs.Count > 1)
                    {
                        if (string.IsNullOrWhiteSpace(profile.Input))
                        {
                            return $"Choose which input '{profile.Name}' targets for its HDMI-specific advanced options.";
                        }

                        if (!Inputs.Any(input =>
                            string.Equals(input.Name.Trim(), profile.Input.Trim(), StringComparison.OrdinalIgnoreCase)))
                        {
                            return $"'{profile.Name}' targets '{profile.Input}', which is not one of the configured inputs.";
                        }
                    }
                }

                if (profile.Signal.Concat(profile.Settings)
                    .Any(row => string.IsNullOrWhiteSpace(row.Key) || string.IsNullOrWhiteSpace(row.Value)))
                {
                    return $"'{profile.Name}' has an advanced setting with no name or value. Remove it instead of leaving it blank.";
                }
            }

            return null;
        }
    }

    public override bool CanSave =>
        HasSelectedTelevision && HasUnsavedChanges && !IsBusy && ValidationProblem is null;

    public override void Refresh() => Reload(SelectedTelevision);

    public void AddInput()
    {
        Inputs.Add(new TelevisionInputRow(string.Empty, string.Empty, OnRowChanged));
        OnPropertyChanged(nameof(HasMultipleInputs));
        OnRowChanged();
    }

    public void RemoveInput(TelevisionInputRow row)
    {
        if (Inputs.Remove(row))
        {
            OnPropertyChanged(nameof(HasMultipleInputs));
            OnRowChanged();
        }
    }

    public void AddProfile()
    {
        Profiles.Add(new TelevisionProfileRow(string.Empty, "standard", string.Empty, CurrentInputNames, OnRowChanged));
        OnRowChanged();
    }

    public void RemoveProfile(TelevisionProfileRow row)
    {
        if (Profiles.Remove(row))
        {
            OnRowChanged();
        }
    }

    /// <summary>Creates a disabled blank television and selects it.</summary>
    public string? CreateTelevision(string name)
    {
        ClearError();

        string trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return SetError("Give the television a name.");
        }

        var edit = new LgTvEdit(
            Enabled: false,
            Host: string.Empty,
            MacAddress: string.Empty,
            SafeDisplayTarget: null,
            Inputs: [],
            Profiles: []);

        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = LgTvConfigurationStore.Create(
                Workspace.Root,
                trimmed,
                edit,
                makeDefault: TelevisionItems.Count == 0);

            if (!outcome.Success)
            {
                return ReportSaveFailure(outcome, "That television could not be created.");
            }

            Reload(trimmed);
            return ReportSuccess($"'{trimmed}' created.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Creates a saved copy of the selected television and selects the copy.</summary>
    public string? DuplicateSelected(string newName)
    {
        ClearError();

        if (SelectedTelevision is not { } source)
        {
            return SetError("Choose a television first.");
        }

        string trimmed = newName?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return SetError("Give the television a name.");
        }

        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = LgTvConfigurationStore.Duplicate(Workspace.Root, source, trimmed);

            if (!outcome.Success)
            {
                return ReportSaveFailure(outcome, "That television could not be duplicated.");
            }

            Reload(trimmed);
            return ReportSuccess($"'{trimmed}' created from '{source}'.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Renames the selected saved television.</summary>
    public string? RenameSelected(string newName)
    {
        ClearError();

        if (SelectedTelevision is not { } oldName)
        {
            return SetError("Choose a television first.");
        }

        string trimmed = newName?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return SetError("Give the television a name.");
        }

        if (string.Equals(oldName, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = LgTvConfigurationStore.Rename(Workspace.Root, oldName, trimmed);

            if (!outcome.Success)
            {
                return ReportSaveFailure(outcome, "That television could not be renamed.");
            }

            Reload(trimmed);
            return ReportSuccess($"'{oldName}' renamed to '{trimmed}'.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Deletes the selected saved television.</summary>
    public string? DeleteSelected()
    {
        ClearError();

        if (SelectedTelevision is not { } removed)
        {
            return SetError("Choose a television first.");
        }

        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = LgTvConfigurationStore.Delete(Workspace.Root, removed);

            if (!outcome.Success)
            {
                return ReportSaveFailure(outcome, "That television could not be removed.");
            }

            Reload(preferredSelection: null);
            return ReportSuccess($"'{removed}' removed.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Makes the selected television the compatibility/default television.</summary>
    public string? SetSelectedAsDefault()
    {
        ClearError();

        if (SelectedTelevision is not { } selected)
        {
            return SetError("Choose a television first.");
        }

        if (IsSelectedDefault)
        {
            return null;
        }

        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = LgTvConfigurationStore.SetDefault(Workspace.Root, selected);

            if (!outcome.Success)
            {
                return ReportSaveFailure(outcome, "The default television could not be changed.");
            }

            Reload(selected);
            return ReportSuccess($"'{selected}' is now the default television.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Enables or disables the selected television, saving whatever else is
    /// currently in the editor along with it -- the same commit-and-reload
    /// every other quick action here already does.
    /// </summary>
    public string? ToggleSelectedEnabled()
    {
        ClearError();

        if (SelectedTelevision is not { } selected)
        {
            return SetError("Choose a television first.");
        }

        bool enabled = !Enabled;
        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = LgTvConfigurationStore.Save(
                Workspace.Root,
                selected,
                BuildEdit(enabled));

            if (!outcome.Success)
            {
                return ReportSaveFailure(
                    outcome,
                    enabled
                        ? "That television could not be enabled."
                        : "That television could not be disabled.");
            }

            Reload(selected);
            return ReportSuccess(
                enabled
                    ? $"'{selected}' enabled."
                    : $"'{selected}' disabled.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Refreshes the observed power state for every configured television.
    /// Never reloads configuration, edits the form, or initiates pairing.
    /// </summary>
    public async Task RefreshLivePowerStatusAsync()
    {
        if (IsBusy || TelevisionItems.Count == 0)
        {
            return;
        }

        TelevisionListItem[] items = [.. TelevisionItems];

        Task<(TelevisionListItem Item, string Compact, string? Detail)>[] reads =
            [.. items.Select(async item =>
            {
                TvPowerProvider? provider = _livePowerProvider?.Invoke(item.Name);

                if (provider is null)
                {
                    return (item, "—", (string?)null);
                }

                if (!provider.Settings.Enabled)
                {
                    return (item, "Disabled", "This television is disabled.");
                }

                try
                {
                    string? state = await Task.Run(provider.Read).ConfigureAwait(false);
                    return (item, CompactPowerState(state), DescribePowerState(state));
                }
                catch
                {
                    return (item, "—", (string?)null);
                }
            })];

        (TelevisionListItem Item, string Compact, string? Detail)[] results =
            await Task.WhenAll(reads).ConfigureAwait(true);

        foreach ((TelevisionListItem item, string compact, string? detail) in results)
        {
            item.PowerState = compact;

            if (string.Equals(item.Name, SelectedTelevision, StringComparison.OrdinalIgnoreCase) &&
                detail is not null)
            {
                PowerStateText = detail;
            }
        }
    }

    /// <summary>
    /// Reads the selected television's power now, rather than waiting for the
    /// next ambient refresh.
    /// </summary>
    /// <remarks>
    /// Off the calling thread, because it is a socket. A television in standby
    /// accepts a connection and then never registers, so this takes seconds on
    /// exactly the occasions somebody is most likely to press it, and doing it
    /// inline would freeze the window for all of them.
    /// </remarks>
    public async Task<string> CheckStatusAsync()
    {
        (TvPowerProvider? provider, string? problem) = LoadProvider();

        if (provider is null)
        {
            PowerStateText = problem ?? "The configuration could not be read.";
            return PowerStateText;
        }

        if (!provider.Settings.Enabled)
        {
            PowerStateText = "This television is disabled.";
            SetSelectorPowerState(SelectedTelevision, "Disabled");
            return PowerStateText;
        }

        IsBusy = true;

        try
        {
            string? state = await Task.Run(provider.Read).ConfigureAwait(true);
            PowerStateText = DescribePowerState(state);
            SetSelectorPowerState(SelectedTelevision, CompactPowerState(state));
            return PowerStateText;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string DescribePowerState(string? state) => state switch
    {
        TvPowerProvider.On => "On.",
        TvPowerProvider.Off => "Off, or not answering.",
        _ => "Not paired yet. Use Pair.",
    };

    private static string CompactPowerState(string? state) => state switch
    {
        TvPowerProvider.On => "On",
        TvPowerProvider.Off => "Off",
        _ => "Unknown",
    };

    private void SetSelectorPowerState(string? televisionName, string state)
    {
        if (string.IsNullOrWhiteSpace(televisionName))
        {
            return;
        }

        TelevisionListItem? item = TelevisionItems.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, televisionName, StringComparison.OrdinalIgnoreCase));

        if (item is not null)
        {
            item.PowerState = state;
        }
    }

    /// <summary>
    /// Opens a pairing prompt on the selected television and waits for somebody
    /// to accept it there.
    /// </summary>
    /// <remarks>
    /// Off the calling thread and awaited, because the wait is a person finding a
    /// remote control and is bounded by a minute. Done inline it would freeze the
    /// window for that minute -- including the very message that tells them to go
    /// and accept the prompt.
    /// </remarks>
    public async Task<string?> PairAsync()
    {
        ClearError();

        (TvPowerProvider? provider, string? problem) = LoadProvider();

        if (provider is null)
        {
            return SetError(problem ?? "The configuration could not be read.");
        }

        if (!provider.Settings.Enabled)
        {
            return SetError("Save this television's address and MAC address first.");
        }

        string name = SelectedTelevision ?? "Television";
        LgTvConnection connection = provider.Connection;

        IsBusy = true;
        StatusMessage = $"Waiting for the pairing prompt to be accepted on '{name}'...";

        try
        {
            (bool paired, string? pairingProblem) = await Task.Run(() =>
            {
                bool ok = connection.TryPair(out string? failure);
                return (ok, failure);
            }).ConfigureAwait(true);

            if (!paired)
            {
                StatusMessage = pairingProblem ?? "Pairing failed.";
                Workspace.Warn(Category, StatusMessage);
                return SetError(StatusMessage);
            }

            StatusMessage = $"'{name}' paired.";
            Workspace.Note(Category, $"Paired with '{name}'.");
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected override string? SaveChangesCore()
    {
        if (SelectedTelevision is not { } selected)
        {
            return "Choose a television first.";
        }

        if (ValidationProblem is string problem)
        {
            StatusMessage = problem;
            return problem;
        }

        LgTvEdit edit = BuildEdit();

        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = LgTvConfigurationStore.Save(Workspace.Root, selected, edit);

            if (!outcome.Success)
            {
                StatusMessage = outcome.Problem ?? "That could not be saved.";
                Workspace.Warn(Category, StatusMessage);
                return StatusMessage;
            }

            Reload(selected);
            StatusMessage = Workspace.Apply($"'{selected}' saved.");
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected override void DiscardChangesCore() => Reload(SelectedTelevision);

    private LgTvEdit BuildEdit(bool? enabledOverride = null) => new(
        enabledOverride ?? Enabled,
        Host.Trim(),
        MacAddress.Trim(),
        string.IsNullOrWhiteSpace(SafeDisplayTarget) ? null : SafeDisplayTarget.Trim(),
        [.. Inputs.Select(row => new LgTvInputEdit(row.Name.Trim(), row.Input.Trim()))],
        [.. Profiles.Select(profile => new LgTvProfileEdit(
            profile.Name.Trim(),
            profile.PictureMode.Trim(),
            string.IsNullOrWhiteSpace(profile.Input) ? null : profile.Input.Trim(),
            [.. profile.Signal.Select(row => new LgTvSettingEdit(row.Key.Trim(), row.Value.Trim()))],
            [.. profile.Settings.Select(row => new LgTvSettingEdit(row.Key.Trim(), row.Value.Trim()))]))]);

    /// <summary>The television's currently configured input names, live.</summary>
    /// <remarks>
    /// Handed to every <see cref="TelevisionProfileRow"/> so each profile's
    /// input picker always reflects the inputs as they stand right now, without
    /// every add, remove or rename of an input having to reach into every
    /// profile row to hand it a fresh copy.
    /// </remarks>
    private IReadOnlyList<string> CurrentInputNames() =>
        [.. Inputs.Select(row => row.Name)];

    private void Reload(string? preferredSelection)
    {
        LgTvCatalog catalog;

        try
        {
            catalog = LgTvConfigurationStore.Read(Workspace.Root);
        }
        catch (ModeConfigurationException exception)
        {
            Workspace.Fault(Category, "The televisions could not read the configuration.", exception);
            StatusMessage = $"The configuration could not be read. {exception.Message}";
            return;
        }

        string? selected = ResolveSelection(catalog, preferredSelection);

        TelevisionItems.Clear();

        foreach (string name in catalog.Names)
        {
            TelevisionItems.Add(new TelevisionListItem(
                name,
                catalog.DefaultName is not null &&
                string.Equals(name, catalog.DefaultName, StringComparison.OrdinalIgnoreCase)));
        }

        DefaultTelevision = catalog.DefaultName;
        SelectedKey = selected;
        RaiseSelectionState();

        if (selected is null || catalog.SettingsFor(selected) is not { } settings)
        {
            ClearForm();
            StatusMessage = "No televisions are configured.";
            return;
        }

        LoadForm(selected, settings);
    }

    private static string? ResolveSelection(LgTvCatalog catalog, string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            string? existing = catalog.Names.FirstOrDefault(name =>
                string.Equals(name, preferred.Trim(), StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                return existing;
            }
        }

        return catalog.DefaultName ?? catalog.Names.FirstOrDefault();
    }

    private void LoadForm(string name, LgTvSettings settings)
    {
        RunWithoutDirtyTracking(() =>
        {
            Enabled = settings.Enabled;
            Host = settings.Host;
            MacAddress = settings.MacAddress;
            SafeDisplayTarget = settings.SafeDisplayTarget ?? string.Empty;

            Inputs.Clear();

            foreach (string inputName in settings.InputNames)
            {
                Inputs.Add(new TelevisionInputRow(
                    inputName,
                    settings.InputIdFor(inputName) ?? string.Empty,
                    OnRowChanged));
            }

            OnPropertyChanged(nameof(HasMultipleInputs));
            Profiles.Clear();

            foreach (string profileName in settings.ProfileNames)
            {
                LgTvProfile profile = settings.ProfileFor(profileName)!;

                // Shown only with more than one input: on the ordinary machine the
                // resolved input is implied and would be nothing but a field nobody
                // needed to look at.
                string input = Inputs.Count > 1 ? profile.InputName ?? string.Empty : string.Empty;

                var row = new TelevisionProfileRow(profileName, profile.PictureMode, input, CurrentInputNames, OnRowChanged);

                foreach (LgTvSetting setting in profile.Signal)
                {
                    string editableKey = LgTvProfile.WrittenSignalName(setting.Key) ?? setting.Key;
                    row.Signal.Add(new TelevisionSettingRow(editableKey, setting.Value, OnRowChanged));
                }

                foreach (LgTvSetting setting in profile.Settings)
                {
                    row.Settings.Add(new TelevisionSettingRow(setting.Key, setting.Value, OnRowChanged));
                }

                Profiles.Add(row);
            }
        });

        ClearDirtyState();

        StatusMessage = settings.Enabled
            ? $"'{name}' configured, with {Inputs.Count} input(s) and {Profiles.Count} profile(s)."
            : $"'{name}' is disabled.";

        PowerStateText = null;
        RaiseValidation();
    }

    private void ClearForm()
    {
        RunWithoutDirtyTracking(() =>
        {
            Enabled = false;
            Host = string.Empty;
            MacAddress = string.Empty;
            SafeDisplayTarget = string.Empty;
            Inputs.Clear();
            OnPropertyChanged(nameof(HasMultipleInputs));
            Profiles.Clear();
            PowerStateText = null;
        });

        ClearDirtyState();
        RaiseValidation();
    }

    /// <summary>
    /// The provider that owns the selected television's connection.
    /// </summary>
    /// <remarks>
    /// The running engine's, whenever there is one, so that everything this page
    /// does over the network shares the gate every transition shares. Reading the
    /// configuration is the fallback for a page with no runtime behind it, and it
    /// builds the same providers the engine would.
    /// </remarks>
    private (TvPowerProvider? Provider, string? Problem) LoadProvider()
    {
        if (SelectedTelevision is not { } selected)
        {
            return (null, "Choose a television first.");
        }

        if (_livePowerProvider?.Invoke(selected) is TvPowerProvider live)
        {
            return (live, null);
        }

        (IReadOnlyList<TvPowerProvider> providers, string? problem) = ReadProviders<TvPowerProvider>();

        if (problem is not null)
        {
            return (null, problem);
        }

        TvPowerProvider? named = providers.FirstOrDefault(provider =>
            string.Equals(provider.TelevisionName, selected, StringComparison.OrdinalIgnoreCase));

        return named is not null
            ? (named, null)
            : (null, $"Television '{selected}' is not active in the current configuration.");
    }

    private void OnRowChanged()
    {
        MarkDirty();

        // Broad rather than targeted: an input's name can change from any row on
        // this page (added, removed or renamed), and every profile's input picker
        // needs to keep showing the current list either way.
        foreach (TelevisionProfileRow profile in Profiles)
        {
            profile.NotifyInputsChanged();
        }

        RaiseValidation();
    }

    private string? ReportSaveFailure(ModeSaveOutcome outcome, string fallback)
    {
        StatusMessage = outcome.Problem ?? fallback;
        Workspace.Warn(Category, StatusMessage);
        return SetError(StatusMessage);
    }

    private string? ReportSuccess(string message)
    {
        StatusMessage = Workspace.Apply(message);
        return null;
    }

    private void RaiseSelectionState()
    {
        OnPropertyChanged(nameof(SelectedTelevision));
        OnPropertyChanged(nameof(HasSelectedTelevision));
        OnPropertyChanged(nameof(IsSelectedDefault));
        OnPropertyChanged(nameof(CanSetSelectedAsDefault));
    }

    private void RaiseValidation() => NotifyValidationChanged(nameof(ValidationProblem), nameof(CanSave));

    protected override void OnBusyStateChanged()
    {
        base.OnBusyStateChanged();
        OnPropertyChanged(nameof(CanSetSelectedAsDefault));
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using PCMode.Engine;
using PCMode.Tray.Elements.Configuration;
using PCMode.Tray.Elements;

namespace PCMode.Tray.Elements.Displays.Sections;

/// <summary>
/// The whole DDC/CI feature, scoped to whichever physical monitor is selected
/// in <see cref="LayoutSectionViewModel"/>: which alias (if any) is bound to
/// that monitor, creating/renaming/removing that binding, whether the monitor
/// actually answers DDC/CI, and live brightness and input control over it.
/// </summary>
/// <remarks>
/// THE SELECTED MONITOR SUPPLIES THE DEVICE PATH
///
/// This replaces the standalone DDC/CI page, which was a collection editor
/// over 'providers.ddcMonitors' keyed by alias: it had its own selector, its
/// own device-path text box, and a Discover list to fill that box from. None
/// of those survive the move, because none of them have anything left to do
/// -- the monitor selected on the Displays canvas IS the device path, so a
/// binding here is created FOR that monitor rather than typed at it, and
/// there is nothing to discover that the canvas has not already resolved.
/// What the Discover button genuinely told you -- whether this particular
/// screen answers DDC/CI at all -- is kept, as <see cref="CapabilityStatus"/>.
///
/// BINDINGS ARE WRITTEN IMMEDIATELY; NOTHING HERE IS PART OF PAGE SAVE
///
/// <see cref="DdcMonitorConfigurationStore"/> revalidates the whole candidate
/// document on every write, so a binding is committed (or refused) the moment
/// it is made, exactly as the standalone page committed create/rename/delete.
/// The page's Save button owns the layout, not this -- hence
/// <see cref="Save"/> is a no-op, and the page guards these actions behind
/// its unsaved-changes prompt the same way the standalone page did.
///
/// LIVE VALUES ARE NEVER CONFIGURATION
///
/// The brightness slider and input picker talk to the real monitor through
/// <see cref="DdcBrightnessProvider"/> and <see cref="DdcInputProvider"/>,
/// off the UI thread, and never touch 'providers.ddcMonitors'.
/// </remarks>
public sealed class MonitorDdcSectionViewModel : ObservableObject, IConfigurationSection
{
    private static readonly TimeSpan CapabilityProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly IConfigurationWorkspace _workspace;

    private string? _devicePath;
    private string? _monitorName;
    private string? _alias;

    private string _capabilityStatus = "Not checked yet.";
    private string? _capabilityBadgeText;

    private bool _brightnessSupported;
    private string _brightnessStatus = "Not checked yet.";
    private double _pendingBrightness = 50;

    private bool _inputSupported;
    private string _inputStatus = "Not checked yet.";
    private string? _pendingInput;

    private bool _isBusy;
    private string? _statusMessage;

    private readonly IConfigurationPrompts _prompts;
    private readonly Action? _onBindingChanged;

    /// <summary>
    /// The page's own "you have unsaved changes" guard, run before a binding
    /// write the way the page shell ran it before these became commands. False
    /// means the action must not proceed.
    /// </summary>
    private readonly Func<bool>? _guardUnsavedChanges;

    /// <param name="prompts">
    /// How this section asks for a DDC/CI name and confirms removing one. A
    /// Layout is a <see cref="System.Windows.ControlTemplate"/> and a block is
    /// lookless, so neither can open a dialog; the section that owns the write
    /// owns the whole flow instead. Left null outside a hosted process
    /// (tests), where <see cref="NoConfigurationPrompts"/> refuses every
    /// prompt.
    /// </param>
    /// <param name="onBindingChanged">
    /// Called after a binding is created, renamed or removed, since that is
    /// what makes this monitor's live controls reachable -- or takes them
    /// away -- and the sections beside this one have to re-resolve against it.
    /// </param>
    public MonitorDdcSectionViewModel(
        IConfigurationWorkspace workspace,
        IConfigurationPrompts? prompts = null,
        Action? onBindingChanged = null,
        Func<bool>? guardUnsavedChanges = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
        _prompts = prompts ?? NoConfigurationPrompts.Instance;
        _onBindingChanged = onBindingChanged;
        _guardUnsavedChanges = guardUnsavedChanges;

        CreateBindingCommand = new RelayCommand(PromptCreateBinding, () => CanBind);
        RenameBindingCommand = new RelayCommand(PromptRenameBinding, () => HasBinding && !IsBusy);
        RemoveBindingCommand = new RelayCommand(ConfirmRemoveBinding, () => HasBinding && !IsBusy);
        ApplyBrightnessCommand = new AsyncRelayCommand(async () => await ApplyBrightnessAsync().ConfigureAwait(true), () => CanUseLiveControls);
        ApplyInputCommand = new AsyncRelayCommand(async () => await ApplyInputAsync().ConfigureAwait(true), () => CanUseLiveControls);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CanBind) or nameof(CanUseLiveControls) or nameof(HasBinding) or nameof(IsBusy))
            {
                RefreshCommands();
            }
        };
    }

    /// <summary>Names this monitor for DDC/CI, which is what makes modes able to drive it.</summary>
    public ICommand CreateBindingCommand { get; }

    public ICommand RenameBindingCommand { get; }

    public ICommand RemoveBindingCommand { get; }

    /// <summary>Sends <see cref="PendingBrightness"/> to the real monitor.</summary>
    public ICommand ApplyBrightnessCommand { get; }

    /// <summary>Sends <see cref="PendingInput"/> to the real monitor.</summary>
    public ICommand ApplyInputCommand { get; }

    private void PromptCreateBinding()
    {
        if (!Guard())
        {
            return;
        }

        _prompts.PromptForName(
            "Add DDC/CI control",
            "Name this monitor for DDC/CI. Modes use this name to set its brightness and input.",
            SuggestedAlias,
            name => Write(() => CreateBinding(name)));
    }

    private void PromptRenameBinding()
    {
        if (_alias is not string current || !Guard())
        {
            return;
        }

        _prompts.PromptForName(
            "Rename DDC/CI monitor",
            $"Rename '{current}'. PCMode refuses the change if it would leave the configuration invalid.",
            current,
            name => Write(() => RenameBinding(name)));
    }

    private void ConfirmRemoveBinding()
    {
        if (_alias is not string current || !Guard())
        {
            return;
        }

        if (_prompts.Confirm($"Remove DDC/CI control for '{current}'? The monitor itself is not changed."))
        {
            Write(RemoveBinding);
        }
    }

    /// <summary>
    /// Runs a binding write and lets whoever owns the sections beside this one
    /// re-resolve against what it changed.
    /// </summary>
    private string? Write(Func<string?> write)
    {
        string? problem = write();

        if (problem is null)
        {
            _onBindingChanged?.Invoke();
        }

        return problem;
    }

    private bool Guard() => _guardUnsavedChanges?.Invoke() ?? true;

    private void RefreshCommands()
    {
        (CreateBindingCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RenameBindingCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveBindingCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ApplyBrightnessCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ApplyInputCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    public ObservableCollection<string> InputChoices { get; } = [.. DdcInputProvider.KnownInputNames.Keys];

    /// <summary>Whether a monitor is selected at all and Windows currently reports a device path for it.</summary>
    public bool HasMonitor => _devicePath is not null;

    /// <summary>Whether the selected monitor's device path is bound to a configured DDC/CI alias.</summary>
    public bool HasBinding => _alias is not null;

    public string? Alias => _alias;

    /// <summary>The device path the binding is (or would be) made against.</summary>
    public string? DevicePath => _devicePath;

    /// <summary>What a capability probe last said about this monitor, or why it has not been asked.</summary>
    public string CapabilityStatus
    {
        get => _capabilityStatus;
        private set => SetProperty(ref _capabilityStatus, value);
    }

    public string? CapabilityBadgeText
    {
        get => _capabilityBadgeText;
        private set
        {
            if (SetProperty(ref _capabilityBadgeText, value))
            {
                OnPropertyChanged(nameof(HasCapabilityBadge));
            }
        }
    }

    public bool HasCapabilityBadge =>
        CapabilityBadgeText is not null;

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool BrightnessSupported
    {
        get => _brightnessSupported;
        private set => SetProperty(ref _brightnessSupported, value);
    }

    public string BrightnessStatus
    {
        get => _brightnessStatus;
        private set => SetProperty(ref _brightnessStatus, value);
    }

    /// <summary>The value the slider is at right now, applied only on request.</summary>
    public double PendingBrightness
    {
        get => _pendingBrightness;
        set => SetProperty(ref _pendingBrightness, Math.Clamp(value, 0, 100));
    }

    public bool InputSupported
    {
        get => _inputSupported;
        private set => SetProperty(ref _inputSupported, value);
    }

    public string InputStatus
    {
        get => _inputStatus;
        private set => SetProperty(ref _inputStatus, value);
    }

    public string? PendingInput
    {
        get => _pendingInput;
        set => SetProperty(ref _pendingInput, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanUseLiveControls));
                OnPropertyChanged(nameof(CanBind));
                OnPropertyChanged(nameof(CanProbe));
            }
        }
    }

    public bool CanUseLiveControls => HasBinding && !IsBusy;

    /// <summary>Whether "Add DDC/CI control" can run: a detected monitor that has no binding yet.</summary>
    public bool CanBind => HasMonitor && !HasBinding && !IsBusy;

    public bool CanProbe => HasMonitor && !IsBusy;

    /// <summary>
    /// Resolves which alias (if any) binds the selected monitor's device path,
    /// and resets everything read from the real monitor. Always reloads --
    /// see <see cref="ReResolve"/> for the ambient-tick version that does not.
    /// </summary>
    public void Load(object? entityKey)
    {
        LayoutMonitorRow? monitor = entityKey as LayoutMonitorRow;

        _devicePath = monitor?.DevicePath;
        _monitorName = monitor?.MonitorName;
        _alias = ResolveAlias(_devicePath);

        RaiseBindingState();

        CapabilityStatus = HasMonitor
            ? "Not checked yet."
            : "Windows is not currently reporting a device path for this monitor.";
        CapabilityBadgeText = null;
        BrightnessSupported = false;
        BrightnessStatus = "Not checked yet.";
        InputSupported = false;
        InputStatus = "Not checked yet.";
        StatusMessage = null;
    }

    /// <summary>
    /// Re-resolves the binding on an ambient tick, reloading only if the
    /// answer actually changed.
    /// </summary>
    /// <remarks>
    /// A monitor can go from undetected to detected between ticks, so the
    /// resolution genuinely has to be redone -- but a tick that resolves the
    /// same alias must not throw away the capability and live readings taken
    /// a moment ago, which a full <see cref="Load"/> would.
    /// </remarks>
    public void ReResolve(LayoutMonitorRow? monitor)
    {
        if (string.Equals(monitor?.DevicePath, _devicePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ResolveAlias(monitor?.DevicePath), _alias, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Load(monitor);
    }

    public string? Save() => null;

    /// <summary>Binds a new alias to the selected monitor, so modes can address it over DDC/CI.</summary>
    public string? CreateBinding(string alias)
    {
        if (_devicePath is not { } devicePath)
        {
            return "Windows is not currently reporting a device path for this monitor.";
        }

        string trimmed = alias?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return "Give this monitor a DDC/CI name.";
        }

        if (ConfiguredAliases().Any(existing =>
            string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{trimmed}' is already a configured DDC/CI monitor.";
        }

        ModeSaveOutcome outcome = DdcMonitorConfigurationStore.Save(_workspace.Root, trimmed, devicePath);

        if (!outcome.Success)
        {
            return Warn(outcome.Problem ?? "That could not be saved.");
        }

        _alias = trimmed;
        RaiseBindingState();
        StatusMessage = _workspace.Apply($"'{trimmed}' can now be controlled over DDC/CI.");
        return null;
    }

    /// <summary>Renames the alias bound to this monitor, keeping the binding itself.</summary>
    public string? RenameBinding(string newAlias)
    {
        if (_alias is not { } oldAlias)
        {
            return "This monitor has no DDC/CI name yet.";
        }

        string trimmed = newAlias?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return "Give this monitor a DDC/CI name.";
        }

        if (string.Equals(oldAlias, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ModeSaveOutcome outcome = DdcMonitorConfigurationStore.Rename(_workspace.Root, oldAlias, trimmed);

        if (!outcome.Success)
        {
            return Warn(outcome.Problem ?? "That could not be renamed.");
        }

        _alias = trimmed;
        RaiseBindingState();
        StatusMessage = _workspace.Apply($"'{oldAlias}' renamed to '{trimmed}'.");
        return null;
    }

    /// <summary>
    /// Removes this monitor's DDC/CI binding. Refused by the store while a
    /// mode still addresses the alias, in the engine's own words -- the
    /// monitor itself is never changed either way.
    /// </summary>
    public string? RemoveBinding()
    {
        if (_alias is not { } alias)
        {
            return "This monitor has no DDC/CI name yet.";
        }

        ModeSaveOutcome outcome = DdcMonitorConfigurationStore.Delete(_workspace.Root, alias);

        if (!outcome.Success)
        {
            return Warn(outcome.Problem ?? "That could not be removed.");
        }

        _alias = null;
        RaiseBindingState();
        BrightnessSupported = false;
        BrightnessStatus = "Not checked yet.";
        InputSupported = false;
        InputStatus = "Not checked yet.";
        CapabilityBadgeText = null;
        StatusMessage = _workspace.Apply($"'{alias}' removed. The monitor itself is unchanged.");
        return null;
    }

    /// <summary>
    /// Asks this one monitor whether it answers DDC/CI at all -- what the
    /// standalone page's Discover button reported for every monitor at once,
    /// narrowed to the one selected here.
    /// </summary>
    public async Task ProbeCapabilityAsync()
    {
        if (_devicePath is not { } devicePath)
        {
            return;
        }

        IsBusy = true;
        CapabilityStatus = "Asking this monitor...";
        CapabilityBadgeText = "Checking";

        try
        {
            bool capable = await Task.Run(() =>
                new WindowsDdcMonitors()
                    .KnownDevicePaths(CapabilityProbeTimeout)
                    .Any(path => string.Equals(path, devicePath, StringComparison.OrdinalIgnoreCase)))
                .ConfigureAwait(true);

            CapabilityStatus = capable
                ? "This monitor answers DDC/CI."
                : "This monitor did not answer DDC/CI. It may need DDC/CI enabled in its own menu.";
            CapabilityBadgeText = capable
                ? "Supported"
                : "Not supported";
        }
        catch (Exception exception)
        {
            CapabilityStatus = $"The capability check failed. {exception.Message}";
            CapabilityBadgeText = "Unavailable";
            _workspace.Fault("Ddc", "DDC/CI capability probe failed.", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Reads live brightness and input state for the bound alias, if any.</summary>
    public async Task RefreshLiveStatusAsync()
    {
        if (_alias is not { } alias)
        {
            return;
        }

        (DdcBrightnessProvider? brightness, DdcInputProvider? input) = LoadLiveProviders(alias);

        if (brightness is null || input is null)
        {
            BrightnessSupported = false;
            BrightnessStatus = "This monitor is not active in the current configuration.";
            InputSupported = false;
            InputStatus = BrightnessStatus;
            return;
        }

        (bool brightnessOk, string brightnessText, double? brightnessValue,
         bool inputOk, string inputText, string? inputValue) = await Task.Run(() =>
        {
            ResourceAvailability brightnessAvailability = brightness.AvailabilityOf("0");
            bool brightnessSupported = brightnessAvailability.State == AvailabilityState.Available;
            string? current = brightnessSupported ? brightness.Read() : null;

            string brightnessMessage = brightnessSupported
                ? current is not null
                    ? $"Currently {current}."
                    : "Supported, but the current value could not be read."
                : brightnessAvailability.Explain();

            ResourceAvailability inputAvailability = input.AvailabilityOf("HDMI1");
            bool inputSupported = inputAvailability.State == AvailabilityState.Available;
            string? currentInput = inputSupported ? input.Read() : null;

            string inputMessage = inputSupported
                ? currentInput is not null
                    ? $"Currently {currentInput}."
                    : "Supported, but the current value could not be read."
                : inputAvailability.Explain();

            return (
                brightnessSupported, brightnessMessage, current is not null && double.TryParse(
                    current, NumberStyles.None, CultureInfo.InvariantCulture, out double parsed) ? parsed : (double?)null,
                inputSupported, inputMessage, currentInput);
        }).ConfigureAwait(true);

        if (!string.Equals(_alias, alias, StringComparison.OrdinalIgnoreCase))
        {
            // The selection moved on while this was in flight.
            return;
        }

        BrightnessSupported = brightnessOk;
        BrightnessStatus = brightnessText;

        if (brightnessValue is double value)
        {
            PendingBrightness = value;
        }

        InputSupported = inputOk;
        InputStatus = inputText;

        if (inputValue is not null && !InputChoices.Contains(inputValue, StringComparer.OrdinalIgnoreCase))
        {
            InputChoices.Add(inputValue);
        }

        PendingInput = inputValue ?? PendingInput ?? InputChoices.FirstOrDefault();
    }

    /// <summary>Writes the slider's value to the real monitor now.</summary>
    public async Task<string?> ApplyBrightnessAsync()
    {
        if (_alias is not { } alias)
        {
            return "This monitor has no DDC/CI name yet.";
        }

        (DdcBrightnessProvider? provider, _) = LoadLiveProviders(alias);

        if (provider is null)
        {
            return "This monitor is not active in the current configuration.";
        }

        string value = ((int)Math.Round(PendingBrightness, MidpointRounding.AwayFromZero))
            .ToString(CultureInfo.InvariantCulture);

        IsBusy = true;

        try
        {
            await Task.Run(() => provider.Apply(value, CancellationToken.None)).ConfigureAwait(true);
            await RefreshLiveStatusAsync().ConfigureAwait(true);
            _workspace.Note("Ddc", $"'{alias}' brightness set to {value}.");
            StatusMessage = $"Brightness set to {value}.";
            return null;
        }
        catch (Exception exception)
        {
            return Warn($"Could not set brightness. {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Writes the picker's value to the real monitor now.</summary>
    public async Task<string?> ApplyInputAsync()
    {
        if (_alias is not { } alias)
        {
            return "This monitor has no DDC/CI name yet.";
        }

        if (string.IsNullOrWhiteSpace(PendingInput))
        {
            return "Choose an input first.";
        }

        (_, DdcInputProvider? provider) = LoadLiveProviders(alias);

        if (provider is null)
        {
            return "This monitor is not active in the current configuration.";
        }

        string value = PendingInput;
        IsBusy = true;

        try
        {
            await Task.Run(() => provider.Apply(value, CancellationToken.None)).ConfigureAwait(true);
            await RefreshLiveStatusAsync().ConfigureAwait(true);
            _workspace.Note("Ddc", $"'{alias}' input switched to {value}.");
            StatusMessage = $"Input switched to {value}.";
            return null;
        }
        catch (Exception exception)
        {
            return Warn($"Could not switch input. {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>A suggested alias for a monitor with no binding yet: its layout name.</summary>
    public string SuggestedAlias => _monitorName ?? string.Empty;

    private void RaiseBindingState()
    {
        OnPropertyChanged(nameof(HasMonitor));
        OnPropertyChanged(nameof(HasBinding));
        OnPropertyChanged(nameof(Alias));
        OnPropertyChanged(nameof(DevicePath));
        OnPropertyChanged(nameof(SuggestedAlias));
        OnPropertyChanged(nameof(CanUseLiveControls));
        OnPropertyChanged(nameof(CanBind));
        OnPropertyChanged(nameof(CanProbe));
    }

    private string Warn(string message)
    {
        StatusMessage = message;
        _workspace.Warn("Ddc", message);
        return message;
    }

    private IReadOnlyCollection<string> ConfiguredAliases()
    {
        try
        {
            return [.. DdcMonitorConfigurationStore.Read(_workspace.Root).Keys];
        }
        catch (ModeConfigurationException)
        {
            return [];
        }
    }

    private string? ResolveAlias(string? devicePath)
    {
        if (devicePath is null)
        {
            return null;
        }

        IReadOnlyDictionary<string, string> monitors;

        try
        {
            monitors = DdcMonitorConfigurationStore.Read(_workspace.Root);
        }
        catch (ModeConfigurationException)
        {
            return null;
        }

        foreach ((string alias, string boundPath) in monitors)
        {
            if (string.Equals(boundPath, devicePath, StringComparison.OrdinalIgnoreCase))
            {
                return alias;
            }
        }

        return null;
    }

    private (DdcBrightnessProvider? Brightness, DdcInputProvider? Input) LoadLiveProviders(string alias)
    {
        (IReadOnlyList<DdcBrightnessProvider> brightnessProviders, string? brightnessProblem) =
            _workspace.ReadProviders<DdcBrightnessProvider>();

        if (brightnessProblem is not null)
        {
            return (null, null);
        }

        (IReadOnlyList<DdcInputProvider> inputProviders, string? inputProblem) =
            _workspace.ReadProviders<DdcInputProvider>();

        if (inputProblem is not null)
        {
            return (null, null);
        }

        DdcBrightnessProvider? brightness = brightnessProviders.FirstOrDefault(provider =>
            string.Equals(provider.Alias, alias, StringComparison.OrdinalIgnoreCase));

        DdcInputProvider? input = inputProviders.FirstOrDefault(provider =>
            string.Equals(provider.Alias, alias, StringComparison.OrdinalIgnoreCase));

        return (brightness, input);
    }
}

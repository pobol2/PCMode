using System.Collections.ObjectModel;
using System.Globalization;
using PCMode.Engine;
using PCMode.Tray.Configuration;
using PCMode.Tray.Presentation;

namespace PCMode.Tray.Features.Audio;

/// <summary>One configured audio output, shown when it is the selected resource.</summary>
public sealed record AudioOutputInfo(
    string Alias,
    string EndpointName,
    AudioMappingState State,
    string StateText,
    string? PartyRole)
{
    public bool IsReady => State == AudioMappingState.Ready;

    public bool HasProblem => State is AudioMappingState.Missing or AudioMappingState.Ambiguous;

    public bool IsInParty => PartyRole is not null;
}

/// <summary>One item in the Audio page's selector: a configured output, or the one Party group.</summary>
public sealed record AudioResourceItem(
    string Key,
    string Name,
    bool IsParty,
    bool IsCurrent,
    AudioOutputInfo? Output) : IConfigurationSelectorItem
{
    public bool IsOutput => !IsParty;

    object IConfigurationSelectorItem.Key => Key;
}

/// <summary>
/// The Audio page's state: the configured outputs, and the one Party group.
/// </summary>
/// <remarks>
/// Party's live state -- whether the mirror is actually running and its health --
/// is tracked by the one <see cref="PartyAudioHost"/> <see cref="Configuration.ConfigurationWorkspace"/>
/// hands to every <see cref="AudioProvider"/> it builds, not by any particular
/// provider instance. This page still builds a fresh provider for every
/// operation (the same boundary <c>ApplicationsViewModel</c> uses); the
/// shared host is what lets a freshly built provider's
/// <see cref="AudioProvider.Discover"/> and <see cref="AudioProvider.AdjustParty"/>
/// still see and steer whatever the host is actually running.
/// </remarks>
public sealed class AudioViewModel : ConfigurationPageViewModelBase
{
    /// <summary>What an unset endpoint volume starts at when it is switched on.</summary>
    private const double DefaultVolume = 0.5;

    private const string Category = "Audio";

    private bool _partyEnabled;
    private string _partyName = "Party";
    private string? _referenceOutput;
    private string? _mirroredOutput;
    private int _delayMs;
    private double _gain = 1.0;
    private bool _useReferenceVolume;
    private double _referenceVolume = DefaultVolume;
    private bool _useMirroredVolume;
    private double _mirroredVolume = DefaultVolume;
    private AudioResourceItem? _selectedResource;
    private bool _isPartyRunning;
    private string _partyStateText = string.Empty;
    private string? _partyHealthText;

    public AudioViewModel(IConfigurationWorkspace workspace) : base(workspace)
    {
    }

    /// <summary>Configured outputs plus the Party group, for the selector.</summary>
    public ObservableCollection<AudioResourceItem> Resources { get; } = [];

    /// <summary>The output names a Party member can be chosen from.</summary>
    public ObservableCollection<string> OutputChoices { get; } = [];

    public AudioResourceItem? SelectedResource
    {
        get => _selectedResource;
        private set
        {
            if (!SetProperty(ref _selectedResource, value))
            {
                return;
            }

            SelectedKey = value?.Key;
            OnPropertyChanged(nameof(SelectedOutput));
            OnPropertyChanged(nameof(IsOutputSelected));
            OnPropertyChanged(nameof(IsPartySelected));
            OnPropertyChanged(nameof(CanActivateSelected));
        }
    }

    public AudioOutputInfo? SelectedOutput => SelectedResource?.Output;

    public bool IsOutputSelected => SelectedResource?.IsOutput == true;

    public bool IsPartySelected => SelectedResource?.IsParty == true;

    public bool CanActivateSelected =>
        !IsBusy &&
        SelectedResource is not null &&
        (!IsPartySelected || (PartyEnabled && !HasUnsavedChanges));

    /// <summary>Whether a Party group should exist at all.</summary>
    public bool PartyEnabled
    {
        get => _partyEnabled;
        set
        {
            if (SetDirtyProperty(ref _partyEnabled, value))
            {
                RaiseValidation();
            }
        }
    }

    /// <summary>What a mode asks for to get the group. Never a device's own name.</summary>
    public string PartyName
    {
        get => _partyName;
        set
        {
            if (SetDirtyProperty(ref _partyName, value))
            {
                RaiseValidation();
            }
        }
    }

    /// <summary>The output Windows plays through and the mirror is captured from.</summary>
    public string? ReferenceOutput
    {
        get => _referenceOutput;
        set
        {
            if (SetDirtyProperty(ref _referenceOutput, value))
            {
                RaiseValidation();
            }
        }
    }

    /// <summary>The copy -- the only one of the two that can be delayed.</summary>
    public string? MirroredOutput
    {
        get => _mirroredOutput;
        set
        {
            if (SetDirtyProperty(ref _mirroredOutput, value))
            {
                RaiseValidation();
            }
        }
    }

    /// <summary>How far the mirrored output is held back, in milliseconds.</summary>
    public int DelayMs
    {
        get => _delayMs;
        set
        {
            int clamped = Math.Clamp(value, 0, PartySettings.MaxDelayMs);

            if (SetDirtyProperty(ref _delayMs, clamped))
            {
                RaiseValidation();
                PushLiveAdjustment();
            }
        }
    }

    /// <summary>Digital gain on the mirrored stream, 1 being unchanged.</summary>
    public double Gain
    {
        get => _gain;
        set
        {
            double clamped = double.IsNaN(value) ? _gain : Math.Clamp(value, 0, PartySettings.MaxGain);

            if (SetDirtyProperty(ref _gain, clamped))
            {
                OnPropertyChanged(nameof(GainText));
                RaiseValidation();
                PushLiveAdjustment();
            }
        }
    }

    public string GainText => Gain.ToString("0.00", CultureInfo.CurrentCulture);

    public bool UseReferenceVolume
    {
        get => _useReferenceVolume;
        set
        {
            if (SetDirtyProperty(ref _useReferenceVolume, value))
            {
                RaiseValidation();
            }
        }
    }

    /// <summary>The reference endpoint's own Windows volume, 0 to 1.</summary>
    public double ReferenceVolume
    {
        get => _referenceVolume;
        set
        {
            if (SetDirtyProperty(ref _referenceVolume, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(ReferenceVolumeText));
                RaiseValidation();
            }
        }
    }

    public string ReferenceVolumeText => Percent(_referenceVolume);

    public bool UseMirroredVolume
    {
        get => _useMirroredVolume;
        set
        {
            if (SetDirtyProperty(ref _useMirroredVolume, value))
            {
                RaiseValidation();
            }
        }
    }

    /// <summary>The mirrored endpoint's own Windows volume, 0 to 1.</summary>
    public double MirroredVolume
    {
        get => _mirroredVolume;
        set
        {
            if (SetDirtyProperty(ref _mirroredVolume, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(MirroredVolumeText));
                RaiseValidation();
            }
        }
    }

    public string MirroredVolumeText => Percent(_mirroredVolume);

    /// <summary>Whether the Party mirror is actually capturing and rendering right now.</summary>
    public bool IsPartyRunning
    {
        get => _isPartyRunning;
        private set => SetProperty(ref _isPartyRunning, value);
    }

    /// <summary>What the Party group is doing, in a sentence.</summary>
    public string PartyStateText
    {
        get => _partyStateText;
        private set => SetProperty(ref _partyStateText, value);
    }

    /// <summary>The running mirror's health, or null when nothing is running.</summary>
    public string? PartyHealthText
    {
        get => _partyHealthText;
        private set
        {
            if (SetProperty(ref _partyHealthText, value))
            {
                OnPropertyChanged(nameof(HasPartyHealth));
            }
        }
    }

    public bool HasPartyHealth => _partyHealthText is not null;

    /// <summary>
    /// Why what is on screen could not be saved, or null when it could.
    /// </summary>
    public override string? ValidationProblem
    {
        get
        {
            if (!_partyEnabled)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(_partyName))
            {
                return "Give the Party group a name. A mode asks for it by that name.";
            }

            if (OutputChoices.Any(choice => string.Equals(choice, _partyName.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return $"'{_partyName.Trim()}' is already one of your outputs. Give the group a name of its own.";
            }

            if (OutputChoices.Count < 2)
            {
                return "A Party group needs two outputs. Add them under Hardware first.";
            }

            if (string.IsNullOrWhiteSpace(_referenceOutput) || string.IsNullOrWhiteSpace(_mirroredOutput))
            {
                return "Choose both of the Party group's outputs.";
            }

            if (string.Equals(_referenceOutput, _mirroredOutput, StringComparison.OrdinalIgnoreCase))
            {
                return $"'{_referenceOutput}' cannot be both outputs. Choose two different outputs.";
            }

            return null;
        }
    }

    public override bool CanSave =>
        IsPartySelected &&
        HasUnsavedChanges &&
        !IsBusy &&
        ValidationProblem is null;

    public override void SelectKey(object key)
    {
        AudioResourceItem? match = Resources.FirstOrDefault(item => Equals(item.Key, key));

        if (match is not null)
        {
            SelectedResource = match;
        }
    }

    /// <summary>(Re)reads the outputs and the Party group.</summary>
    public override void Refresh()
    {
        (AudioProvider? provider, string? problem) = ReadProvider<AudioProvider>();

        if (provider is null)
        {
            Resources.Clear();
            OutputChoices.Clear();
            SelectedResource = null;
            StatusMessage = problem ?? "The configuration could not be read.";
            return;
        }

        AudioSnapshot snapshot;

        try
        {
            snapshot = provider.Discover();
        }
        catch (Exception exception)
        {
            Resources.Clear();
            OutputChoices.Clear();
            SelectedResource = null;
            StatusMessage = $"Audio could not be read. {exception.Message}";
            return;
        }

        // The output choices before the resource list, and the resource list
        // before the Party form: the two member combo boxes take their items
        // from OutputChoices, and the form's ReferenceOutput/MirroredOutput
        // selections only stick once those items exist.
        LoadResources(snapshot, provider.Party);
        LoadGroup(provider.Party);
        LoadLiveState(snapshot);

        StatusMessage = snapshot.Available
            ? $"{snapshot.Devices.Count} configured output(s)."
            : $"Windows audio could not be read. {snapshot.Problem}";
    }

    /// <summary>
    /// Applies only the selected audio target. No Space/Activity/Source
    /// transition is run.
    /// </summary>
    public async Task<string?> ActivateSelectedAsync(CancellationToken cancellationToken = default)
    {
        ClearError();

        if (SelectedResource is not { } selected)
        {
            return SetError("Choose an audio output first.");
        }

        if (selected.IsParty)
        {
            if (!PartyEnabled)
            {
                return SetError("Configure and save the Party group before activating it.");
            }

            if (HasUnsavedChanges)
            {
                return SetError("Save or discard Party changes before activating it.");
            }
        }

        (AudioProvider? provider, string? problem) = ReadProvider<AudioProvider>();

        if (provider is null)
        {
            StatusMessage = problem ?? "The configuration could not be read.";
            return SetError(StatusMessage);
        }

        IsBusy = true;
        StatusMessage = $"Activating '{selected.Name}'...";

        try
        {
            await Task.Run(
                () => provider.Apply(selected.Name, cancellationToken),
                cancellationToken).ConfigureAwait(true);

            string? observed = SafeRead(provider);
            bool reachedTarget = string.Equals(observed, selected.Name, StringComparison.OrdinalIgnoreCase);

            Refresh();

            StatusMessage = reachedTarget
                ? $"'{selected.Name}' activated."
                : $"'{selected.Name}' requested. Windows is still settling.";

            Workspace.Note(Category, StatusMessage);
            return null;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Audio activation was cancelled.";
            return SetError(StatusMessage);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not activate '{selected.Name}'. {exception.Message}";
            Workspace.Fault(Category, $"Could not activate '{selected.Name}'.", exception);
            return SetError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected override string? SaveChangesCore()
    {
        if (!IsPartySelected)
        {
            return "Choose the Party resource first.";
        }

        if (ValidationProblem is string problem)
        {
            StatusMessage = problem;
            return problem;
        }

        return Persist();
    }

    protected override void DiscardChangesCore() => Refresh();

    private string? Persist()
    {
        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = PartyEnabled
                ? AudioConfigurationStore.SaveParty(Workspace.Root, BuildEdit())
                : AudioConfigurationStore.RemoveParty(Workspace.Root);

            if (!outcome.Success)
            {
                StatusMessage = outcome.Problem ?? "That could not be saved.";
                Workspace.Warn(Category, StatusMessage);
                return outcome.Problem;
            }

            string message = PartyEnabled
                ? $"'{PartyName.Trim()}' saved."
                : "The Party group was removed.";

            Refresh();
            StatusMessage = Workspace.Apply(message);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadLiveState(AudioSnapshot snapshot)
    {
        if (snapshot.Party is not AudioPartyStatus status)
        {
            IsPartyRunning = false;
            PartyStateText = "No Party group is configured.";
            PartyHealthText = null;
            return;
        }

        IsPartyRunning = status.State == PartyState.Running;

        PartyStateText = status.State switch
        {
            PartyState.Running =>
                $"'{status.Name}' is playing through both outputs. Adjusting the delay or gain here is heard immediately.",
            PartyState.Failed =>
                $"'{status.Name}' stopped on its own. {status.Problem}",
            _ =>
                $"'{status.Name}' is configured and not running. Activate a resource whose audio output is '{status.Name}' to start it.",
        };

        PartyHealthText = status.Health is AudioMirrorHealth health
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"Delay {health.DelayMs} ms, gain {health.Gain:0.00}, effective latency {health.EffectiveLatencyMs:0} ms, " +
                $"clock offset {health.ClockOffsetMs:0.0} ms, {health.DriftCorrections} drift correction(s), " +
                $"{health.Underruns} underrun(s).")
            : null;
    }

    /// <summary>
    /// Puts the delay and gain on screen into the running mirror, without
    /// writing anything. Called on every change while Party is running, which
    /// is what makes the sliders a calibration control rather than a form.
    /// </summary>
    private void PushLiveAdjustment()
    {
        if (!IsPartyRunning)
        {
            return;
        }

        (AudioProvider? provider, _) = ReadProvider<AudioProvider>();
        provider?.AdjustParty(TimeSpan.FromMilliseconds(_delayMs), _gain);
    }

    private void LoadGroup(PartySettings? party)
    {
        RunWithoutDirtyTracking(() =>
        {
            PartyEnabled = party is not null;
            PartyName = party?.Name ?? "Party";
            ReferenceOutput = party?.Reference;
            MirroredOutput = party?.Mirror;
            DelayMs = party?.DelayMs ?? 0;
            Gain = party?.Gain ?? 1.0;

            UseReferenceVolume = party?.ReferenceVolume is not null;
            ReferenceVolume = party?.ReferenceVolume ?? DefaultVolume;
            UseMirroredVolume = party?.MirrorVolume is not null;
            MirroredVolume = party?.MirrorVolume ?? DefaultVolume;
        });

        ClearDirtyState();
        RaiseValidation();
    }

    private void LoadResources(AudioSnapshot snapshot, PartySettings? party)
    {
        string? selectedKey = SelectedResource?.Key;

        List<AudioDeviceMapping> devices =
            [.. snapshot.Devices.OrderBy(device => device.LogicalName, StringComparer.OrdinalIgnoreCase)];

        OutputChoices.Clear();

        foreach (AudioDeviceMapping device in devices)
        {
            OutputChoices.Add(device.LogicalName);
        }

        string? playing = snapshot.Endpoints.FirstOrDefault(endpoint => endpoint.IsDefault)?.Name;

        Resources.Clear();

        foreach (AudioDeviceMapping mapping in devices)
        {
            string? role = party is null
                ? null
                : string.Equals(party.Reference, mapping.LogicalName, StringComparison.OrdinalIgnoreCase)
                    ? "Party reference"
                    : string.Equals(party.Mirror, mapping.LogicalName, StringComparison.OrdinalIgnoreCase)
                        ? "Party mirrored"
                        : null;

            Resources.Add(new AudioResourceItem(
                mapping.LogicalName,
                mapping.LogicalName,
                IsParty: false,
                IsCurrent: string.Equals(playing, mapping.EndpointName, StringComparison.OrdinalIgnoreCase),
                new AudioOutputInfo(mapping.LogicalName, mapping.EndpointName, mapping.State, Describe(mapping), role)));
        }

        Resources.Add(new AudioResourceItem(
            "party",
            party?.Name ?? "Party",
            IsParty: true,
            IsCurrent: false,
            Output: null));

        SelectedResource =
            Resources.FirstOrDefault(item => string.Equals(item.Key, selectedKey, StringComparison.OrdinalIgnoreCase))
            ?? Resources.FirstOrDefault(item => item.IsOutput)
            ?? Resources.FirstOrDefault();
    }

    private PartyGroupEdit BuildEdit() => new(
        PartyName.Trim(),
        ReferenceOutput ?? string.Empty,
        MirroredOutput ?? string.Empty,
        DelayMs,
        Gain,
        UseReferenceVolume ? ReferenceVolume : null,
        UseMirroredVolume ? MirroredVolume : null);

    private static string Describe(AudioDeviceMapping mapping) => mapping.State switch
    {
        AudioMappingState.Ready => "Available",
        AudioMappingState.Missing => "Not connected right now",
        AudioMappingState.Ambiguous =>
            $"{mapping.Matches} devices share this name -- rename one in Windows Sound settings",
        _ => "Could not be checked",
    };

    private static string Percent(double level) =>
        (level * 100).ToString("0", CultureInfo.CurrentCulture) + "%";

    private void RaiseValidation()
    {
        NotifyValidationChanged(
            nameof(ValidationProblem),
            nameof(CanSave),
            nameof(CanActivateSelected));
    }

    protected override void OnDirtyStateChanged()
    {
        base.OnDirtyStateChanged();
        OnPropertyChanged(nameof(CanActivateSelected));
    }

    protected override void OnBusyStateChanged()
    {
        base.OnBusyStateChanged();
        OnPropertyChanged(nameof(CanActivateSelected));
    }
}

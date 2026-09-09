using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using PCMode.Audio.Core.Models;
using PCMode.Audio.Core.Processing;
using PCMode.Engine;
using PCMode.Tray.Elements.Configuration;

namespace PCMode.Tray.Elements.Audio;

public sealed class AudioEqBandRow(Action onChanged) : ObservableEditableRow(onChanged)
{
    private EqFilterType _type;
    private double _frequencyHz;
    private double _gainDb;
    private double _q;

    public IReadOnlyList<EqFilterType> AvailableTypes { get; } = Enum.GetValues<EqFilterType>();

    public EqFilterType Type
    {
        get => _type;
        set => Set(ref _type, value);
    }

    public double FrequencyHz
    {
        get => _frequencyHz;
        set => Set(ref _frequencyHz, value);
    }

    public double GainDb
    {
        get => _gainDb;
        set => Set(ref _gainDb, value);
    }

    public double Q
    {
        get => _q;
        set => Set(ref _q, value);
    }

    public EqBand ToBand() => new(Type, (float)FrequencyHz, (float)GainDb, (float)Q);

    /// <summary>
    /// <see cref="ToBand"/>, without throwing for a combination that is
    /// invalid mid-edit (a low-pass band with a nonzero gain left over from
    /// switching its type, for instance) -- for the Visual response graph,
    /// which redraws on every keystroke and must not crash the page over a
    /// value the user has not finished typing.
    /// </summary>
    public bool TryToBand(out EqBand band)
    {
        try
        {
            band = ToBand();
            return true;
        }
        catch (ArgumentException)
        {
            band = default;
            return false;
        }
    }
}

public sealed class AudioRouteRow(
    ObservableCollection<string> availableSelections,
    Action onChanged,
    Action onCalibrationChanged,
    Action onProfileChanged
) : ObservableEditableRow(onChanged), ISelectionRow
{
    public const double DefaultVolume = 0.5;
    public const string NoProfile = "(None)";

    private string? _output;
    private int _delayMs;
    private double _gain = 1.0;
    private double _volume = DefaultVolume;

    // Kept in the model for compatibility, but no longer exposed in the GUI.
    private bool _required = true;

    private string _profileName = NoProfile;

    private bool _isProfileEditorExpanded;
    private double _profilePreampGainDb;
    private bool _isProfileDirty;

    private IReadOnlyList<ResponseSample> _responseCurve = [];
    private double _peakResponseDb;
    private double _headroomDb;
    private bool _isHeadroomWarning;

    private string _runtimeText = string.Empty;

    private bool _isReference;

    /// <summary>
    /// Whether this is the Multi-output alignment section's reference route --
    /// the one left out of that section's list, and against which "Level
    /// offset" and "Delay" read as 0. Set by <see cref="AudioViewModel"/> from
    /// this row's position in <see cref="AudioViewModel.Routes"/> (the first
    /// route is the reference); not itself part of what is saved.
    /// </summary>
    public bool IsReference
    {
        get => _isReference;
        internal set => SetProperty(ref _isReference, value);
    }

    public string? Output
    {
        get => _output;
        set
        {
            if (Set(ref _output, value))
            {
                OnPropertyChanged(nameof(Selection));
            }
        }
    }

    public string? Selection
    {
        get => Output;
        set => Output = value;
    }

    public ObservableCollection<string> AvailableSelections { get; } =
        availableSelections ?? throw new ArgumentNullException(nameof(availableSelections));

    public int DelayMs
    {
        get => _delayMs;
        set
        {
            if (Set(ref _delayMs, Math.Clamp(value, 0, AudioTargetSettings.MaxDelayMs)))
            {
                onCalibrationChanged();
            }
        }
    }

    public double Gain
    {
        get => _gain;
        set
        {
            double next = double.IsNaN(value) ? _gain : Math.Clamp(value, 0, AudioTargetSettings.MaxGain);

            if (Set(ref _gain, next))
            {
                OnPropertyChanged(nameof(LevelOffsetDb));
                onCalibrationChanged();
            }
        }
    }

    /// <summary>
    /// <see cref="Gain"/> as the decibel value the GUI's direct-entry field
    /// reads and writes -- the same linear-property-plus-conversion shape as
    /// <see cref="VolumePercent"/>. The reference route's own <see cref="Gain"/>
    /// is unity (0 dB) by convention, so this needs no separate
    /// "relative to reference" value: a route's own gain already reads as an
    /// offset from that baseline.
    /// </summary>
    public double LevelOffsetDb
    {
        get => AudioTargetSettings.GainDecibels(_gain);
        set
        {
            if (double.IsFinite(value))
            {
                Gain = Math.Pow(10.0, value / 20.0);
            }
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            if (Set(ref _volume, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(VolumePercent));

                onCalibrationChanged();
            }
        }
    }

    /// <summary>
    /// <see cref="Volume"/> as the 0-100 percentage the GUI's direct-entry
    /// field reads and writes. Rounded for display only: the underlying
    /// <see cref="Volume"/> keeps its full precision until this property is
    /// itself the one being set.
    /// </summary>
    public double VolumePercent
    {
        get => Math.Round(_volume * 100.0, MidpointRounding.AwayFromZero);
        set
        {
            if (double.IsFinite(value))
            {
                Volume = value / 100.0;
            }
        }
    }

    public bool Required
    {
        get => _required;
        set => Set(ref _required, value);
    }

    public string ProfileName
    {
        get => _profileName;
        set
        {
            string next = string.IsNullOrWhiteSpace(value) ? NoProfile : value;

            if (Set(ref _profileName, next))
            {
                IsProfileEditorExpanded = false;

                OnPropertyChanged(nameof(HasProcessingProfile));

                onCalibrationChanged();
            }
        }
    }

    public string? SavedProfileName =>
        string.Equals(_profileName, NoProfile, StringComparison.Ordinal) ? null : _profileName;

    public bool HasProcessingProfile => SavedProfileName is not null;

    public bool IsProfileEditorExpanded
    {
        get => _isProfileEditorExpanded;
        set => SetProperty(ref _isProfileEditorExpanded, value);
    }

    public double ProfilePreampGainDb
    {
        get => _profilePreampGainDb;
        set
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            if (
                SetProperty(
                    ref _profilePreampGainDb,
                    Math.Clamp(value, ParametricEqProfile.MinPreampGainDb, ParametricEqProfile.MaxPreampGainDb)
                )
            )
            {
                OnPropertyChanged(nameof(PreampGainDbText));

                onProfileChanged();
            }
        }
    }

    public string PreampGainDbText => FormatSignedDb(_profilePreampGainDb);

    public ObservableCollection<AudioEqBandRow> ProfileBands { get; } = [];

    /// <summary>
    /// Whether this row's processing-profile fields (<see cref="ProfilePreampGainDb"/>,
    /// <see cref="ProfileBands"/>) hold edits that have not been written to the
    /// profile's own store entry -- distinct from the route's own dirty state
    /// (which output it uses, which profile it references), since the two save
    /// to different places and a live-status refresh must protect this one
    /// without confusing it for the other.
    /// </summary>
    public bool IsProfileDirty
    {
        get => _isProfileDirty;
        private set => SetProperty(ref _isProfileDirty, value);
    }

    public void MarkProfileDirty() => IsProfileDirty = true;

    public void ClearProfileDirty() => IsProfileDirty = false;

    /// <summary>How many points each response curve is sampled at, across roughly 20 Hz-20 kHz.</summary>
    /// <remarks>
    /// Resolution of the data, not of any plot: it decides how finely the
    /// filter math is evaluated, and stays here for that reason. How those
    /// samples are then drawn is not this class's business.
    /// </remarks>
    private const int ResponsePointCount = 121;

    /// <summary>
    /// The profile's total response -- Preamp plus every band, combined
    /// exactly as the signal chain applies them in series -- sampled across
    /// roughly 20 Hz-20 kHz. Recomputed by <see cref="RecomputeResponse"/>
    /// after every edit to <see cref="ProfilePreampGainDb"/> or
    /// <see cref="ProfileBands"/>.
    /// </summary>
    public IReadOnlyList<ResponseSample> ResponseCurve
    {
        get => _responseCurve;
        private set => SetProperty(ref _responseCurve, value);
    }

    /// <summary>
    /// Each band's own response alone -- no preamp, no other band -- which the
    /// graph draws as the lighter overlays behind <see cref="ResponseCurve"/>.
    /// </summary>
    public ObservableCollection<IReadOnlyList<ResponseSample>> BandCurves { get; } = [];

    /// <summary>
    /// The highest point <see cref="ProfileBands"/> alone reach anywhere in
    /// the graphed range -- the combined EQ shaping's own peak boost, before
    /// Preamp is added. Used, together with <see cref="ProfilePreampGainDb"/>,
    /// to compute <see cref="HeadroomDb"/> the same way the graph's total
    /// curve is built, rather than from whichever single band has the
    /// largest gain.
    /// </summary>
    public double PeakResponseDb
    {
        get => _peakResponseDb;
        private set
        {
            if (SetProperty(ref _peakResponseDb, value))
            {
                OnPropertyChanged(nameof(PeakResponseText));
            }
        }
    }

    public string PeakResponseText => FormatSignedDb(_peakResponseDb);

    /// <summary>
    /// The headroom left before the profile's total response -- Preamp plus
    /// every band, at whichever frequency that total peaks -- would exceed
    /// 0 dBFS. Positive is safe; negative means that peak clips.
    /// </summary>
    public double HeadroomDb
    {
        get => _headroomDb;
        private set
        {
            if (SetProperty(ref _headroomDb, value))
            {
                OnPropertyChanged(nameof(HeadroomText));
            }
        }
    }

    public string HeadroomText => FormatSignedDb(_headroomDb);

    public bool IsHeadroomWarning
    {
        get => _isHeadroomWarning;
        private set
        {
            if (SetProperty(ref _isHeadroomWarning, value))
            {
                OnPropertyChanged(nameof(HeadroomStatusText));
                OnPropertyChanged(nameof(HeadroomStatusState));
            }
        }
    }

    public string HeadroomStatusText => IsHeadroomWarning ? "Warning" : "OK";

    public StatusLevel HeadroomStatusState => IsHeadroomWarning ? StatusLevel.Warning : StatusLevel.Ok;

    /// <summary>
    /// Rebuilds <see cref="ResponseCurve"/>, <see cref="BandCurves"/>
    /// and the headroom summary from the profile fields currently in memory --
    /// dirty or not, saved or not, exactly what is on screen right now.
    /// </summary>
    /// <remarks>
    /// R_total(f) = Preamp + sum_i R_i(f), the same series combination the
    /// real signal chain applies (<see cref="ParametricEqProcessor"/>: preamp
    /// first, then every band's <see cref="Biquad"/> in turn), which is why
    /// this reuses <see cref="EqResponseCurve"/> rather than approximating.
    /// R_max is the highest point that total curve reaches anywhere in the
    /// graphed range, and Headroom is -R_max: positive when the profile never
    /// reaches 0 dBFS, negative by however much it would clip.
    /// </remarks>
    public void RecomputeResponse()
    {
        double[] frequencies = EqResponseCurve.LogFrequencies(ResponsePointCount);

        EqBand[] bands =
        [
            .. ProfileBands
                .Select(bandRow => (Ok: bandRow.TryToBand(out EqBand band), Band: band))
                .Where(candidate => candidate.Ok)
                .Select(candidate => candidate.Band),
        ];

        var totalDb = new double[frequencies.Length];
        double peakBandsOnlyDb = double.NegativeInfinity;

        for (int index = 0; index < frequencies.Length; index++)
        {
            double bandsOnlyDb = 0.0;

            foreach (EqBand band in bands)
            {
                bandsOnlyDb += EqResponseCurve.MagnitudeDb(band, frequencies[index]);
            }

            peakBandsOnlyDb = Math.Max(peakBandsOnlyDb, bandsOnlyDb);
            totalDb[index] = ProfilePreampGainDb + bandsOnlyDb;
        }

        ResponseCurve = Samples(frequencies, totalDb);

        BandCurves.Clear();

        foreach (EqBand band in bands)
        {
            var bandOnlyDb = new double[frequencies.Length];

            for (int index = 0; index < frequencies.Length; index++)
            {
                bandOnlyDb[index] = EqResponseCurve.MagnitudeDb(band, frequencies[index]);
            }

            BandCurves.Add(Samples(frequencies, bandOnlyDb));
        }

        PeakResponseDb = peakBandsOnlyDb;

        double totalPeakDb = ProfilePreampGainDb + peakBandsOnlyDb;

        HeadroomDb = -totalPeakDb;
        IsHeadroomWarning = totalPeakDb > 0.0;
    }

    private static ResponseSample[] Samples(double[] frequencies, double[] valuesDb)
    {
        var samples = new ResponseSample[frequencies.Length];

        for (int index = 0; index < frequencies.Length; index++)
        {
            samples[index] = new ResponseSample(frequencies[index], valuesDb[index]);
        }

        return samples;
    }

    private static string FormatSignedDb(double value) =>
        value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + " dB";

    public string RuntimeText
    {
        get => _runtimeText;
        set
        {
            if (SetProperty(ref _runtimeText, value))
            {
                OnPropertyChanged(nameof(HasRuntimeText));
            }
        }
    }

    public bool HasRuntimeText => _runtimeText.Length > 0;

    public AudioRouteEdit ToEdit() => new(Output ?? string.Empty, DelayMs, Gain, Volume, Required, SavedProfileName);
}

public sealed class AudioViewModel : ConfigurationPageViewModelBase, IPageLifecycle
{
    public const int MaxRoutes = 2;

    private const string Category = "Audio";

    private readonly ConfigurationEditingSession<string, AudioTargetStatus> _session;
    private readonly SelectionList<AudioRouteRow> _routes;
    private readonly AudioTargetEditorViewModel _editor;
    private readonly AudioNoTargetViewModel _noTarget = new("No audio targets yet. Use Create above to add one.");

    private string _targetName = string.Empty;

    private AudioTargetState _targetState = AudioTargetState.Stopped;

    private string _targetStateText = string.Empty;

    private string? _targetHealthText;
    private string? _engineText;

    private bool _recalibrationInFlight;
    private AudioRouteRow? _recalibrationPending;

    private static readonly TimeSpan LiveStatusInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Roughly 30 redraws a second for the top-of-page waveform -- far
    /// faster than <see cref="LiveStatusInterval"/>, and deliberately its own
    /// timer rather than a shorter <see cref="LiveStatusInterval"/>: nothing
    /// about a waveform's redraw rate should make the much larger Health
    /// payload behind the rest of this page refetch that often.
    /// </summary>
    private static readonly TimeSpan WaveformRefreshInterval = TimeSpan.FromMilliseconds(33);

    private readonly LiveRefreshTimer _liveRefresh;
    private readonly LiveRefreshTimer _waveformRefresh;
    private bool _loaded;

    /// <param name="prompts">
    /// How this ViewModel asks for a name, confirms a deletion, and runs the
    /// unsaved-changes guard. Left null outside a hosted process (tests), where
    /// <see cref="NoConfigurationPrompts"/> refuses every prompt rather than
    /// silently discarding an edit nobody could be asked about.
    /// </param>
    public AudioViewModel(IConfigurationWorkspace workspace, IConfigurationPrompts? prompts = null)
        : base(workspace, prompts)
    {
        _liveRefresh = new LiveRefreshTimer(LiveStatusInterval, RefreshLiveStatusAsync);
        _waveformRefresh = new LiveRefreshTimer(WaveformRefreshInterval, RefreshWaveformAsync);

        _session = new ConfigurationEditingSession<string, AudioTargetStatus>(
            keyOf: status => TargetKey(status.Name),
            titleOf: status => status.Name,
            summaryOf: status =>
                status.State is AudioTargetState.Running or AudioTargetState.Degraded ? "Current" : null
        );

        _routes = new SelectionList<AudioRouteRow>(
            NewRoute,
            OnRouteMembershipChanged,
            maximumRows: MaxRoutes,
            minimumRows: 1
        );

        DeviceDiscovery = new AudioDeviceDiscoveryViewModel(workspace, Refresh);

        SaveCommand = new RelayCommand(() => TrySaveChanges(), () => CanSave);
        DiscardCommand = new RelayCommand(DiscardChanges, () => CanDiscard);
        CreateTargetCommand = new RelayCommand(PromptCreateTarget);
        SelectTargetCommand = new RelayCommand<string>(RequestSelect);
        RenameTargetCommand = new RelayCommand(PromptRenameTarget, () => IsTargetSelected);
        DuplicateTargetCommand = new RelayCommand(PromptDuplicateTarget, () => IsTargetSelected);
        DeleteTargetCommand = new RelayCommand(PromptDeleteTarget, () => IsTargetSelected);
        ActivateTargetCommand = new AsyncRelayCommand(ActivateSelectedForShellAsync, () => CanActivateSelected);
        AddRouteCommand = new RelayCommand(AddRoute, () => CanAddRoute);
        RemoveRouteCommand = new RelayCommand<AudioRouteRow>(RemoveRoute, _ => CanRemoveRoute);
        SetReferenceRouteCommand = new RelayCommand<AudioRouteRow>(SelectReferenceRoute);

        Selector = new AudioSelectorViewModel(CreateTargetCommand);
        _editor = new AudioTargetEditorViewModel(this);

        TreePresentation = new ConfigurationPagePresentation(
            "Audio",
            new ConfigurationSelectorPresentation(
                "Audio targets",
                SelectorItems,
                CreateTargetCommand,
                SelectCommand: SelectItemCommand
            ),
            ConfigurationEditorPresentation.Create(
                essential:
                [
                    new("", new TerminalElementContent("Audio editor migration boundary", _editor), ShowChrome: false),
                ]
            )
        );

        // One place keeps the selector projection current, rather than every
        // action that touches the session remembering to rebuild it.
        _session.SelectorItems.CollectionChanged += (_, _) => RebuildSelectorItems();
        PropertyChanged += OnSelfPropertyChanged;
    }

    public AudioDeviceDiscoveryViewModel DeviceDiscovery { get; }

    // ------------------------------------------------------------
    // The block tree's own shape.
    //
    // These expose presentation models, never controls: nothing in this file
    // names a Functional Block. Views/AudioBlockTemplates.xaml is the only
    // place a view model and a block meet, so the structure described here
    // stays independent of whatever draws it.
    // ------------------------------------------------------------

    /// <summary>The selector region's content.</summary>
    public AudioSelectorViewModel Selector { get; }

    /// <summary>
    /// The editor region's content: the open target, or the empty state.
    /// Both are view models, so the region resolves each the same way and
    /// never has to be told which mode it is in.
    /// </summary>
    public object Editor => IsTargetSelected ? _editor : _noTarget;

    /// <summary>One processing-profile editor per route, for the Advanced region.</summary>
    public ObservableCollection<AudioProfileCard> ProfileCards { get; } = [];

    public ICommand SaveCommand { get; }

    public ICommand DiscardCommand { get; }

    public ICommand CreateTargetCommand { get; }

    public ICommand SelectTargetCommand { get; }

    public ICommand RenameTargetCommand { get; }

    public ICommand DuplicateTargetCommand { get; }

    public ICommand DeleteTargetCommand { get; }

    public ICommand ActivateTargetCommand { get; }

    public ICommand AddRouteCommand { get; }

    public ICommand RemoveRouteCommand { get; }

    /// <summary>Makes one route the alignment reference the others are measured against.</summary>
    public ICommand SetReferenceRouteCommand { get; }

    public ObservableCollection<ConfigurationEntityListItem<string>> SelectorItems => _session.SelectorItems;

    public ObservableCollection<string> OutputChoices => _routes.Choices;

    public ObservableCollection<string> ProfileChoices { get; } = [];

    public ObservableCollection<AudioRouteRow> Routes => _routes.Rows;

    public ObservableCollection<AudioAlignmentCard> AlignmentCards { get; } = [];

    /// <summary>Whether an audio target is currently selected in the selector.</summary>
    public bool IsTargetSelected => _session.HasSelection;

    /// <summary>Whether the current selection is a not-yet-saved Create/Duplicate draft.</summary>
    public bool IsNewTarget => _session.IsDraft;

    public bool CanAddRoute => IsTargetSelected && _routes.CanAdd;

    public bool CanRemoveRoute => IsTargetSelected && _routes.CanRemove;

    /// <summary>
    /// Whether the Multi-output alignment section has anything to show. A
    /// single-route target has nothing to align, so Level offset and Delay
    /// are not shown for it at all -- see <see cref="AudioRouteRow.IsReference"/>.
    /// </summary>
    public bool ShowAlignment => Routes.Count >= 2;

    /// <summary>The alignment section's reference output: the first route.</summary>
    public string? ReferenceOutputName => Routes.FirstOrDefault()?.Output;

    public bool CanActivateSelected => !IsBusy && IsTargetSelected && !HasUnsavedChanges;

    public string TargetName
    {
        get => _targetName;
        set
        {
            if (SetDirtyProperty(ref _targetName, value))
            {
                RaiseValidation();
            }
        }
    }

    public bool IsTargetRunning => _targetState is AudioTargetState.Running or AudioTargetState.Degraded;

    public string TargetStateText
    {
        get => _targetStateText;
        private set => SetProperty(ref _targetStateText, value);
    }

    public string? TargetHealthText
    {
        get => _targetHealthText;
        private set
        {
            if (SetProperty(ref _targetHealthText, value))
            {
                OnPropertyChanged(nameof(HasTargetHealth));
            }
        }
    }

    public bool HasTargetHealth => _targetHealthText is not null;

    public string? EngineText
    {
        get => _engineText;
        private set => SetProperty(ref _engineText, value);
    }

    public override string? ValidationProblem
    {
        get
        {
            if (!IsTargetSelected)
            {
                return null;
            }

            string name = _targetName?.Trim() ?? string.Empty;

            if (name.Length == 0)
            {
                return "Give the audio target a name. " + "A mode asks for it by that name.";
            }

            if (OutputChoices.Any(choice => string.Equals(choice, name, StringComparison.OrdinalIgnoreCase)))
            {
                return $"'{name}' is already one of your outputs. " + "Give the target a name of its own.";
            }

            if (Routes.Count == 0)
            {
                return "An audio target needs at least one output.";
            }

            if (OutputChoices.Count == 0)
            {
                return "No audio outputs are configured. " + "Add and name one under Audio outputs first.";
            }

            foreach (AudioRouteRow route in Routes)
            {
                if (string.IsNullOrWhiteSpace(route.Output))
                {
                    return "Choose every one of the audio target's outputs.";
                }
            }

            if (!Routes.Any(route => route.Required))
            {
                return "At least one output has to be required, " + "or nothing could fail the target.";
            }

            return null;
        }
    }

    public override bool CanSave => IsTargetSelected && HasUnsavedChanges && !IsBusy && ValidationProblem is null;

    /// <summary>
    /// The route wiring's own dirty state (which outputs, which profile each
    /// references) OR-ed with every row's <see cref="AudioRouteRow.IsProfileDirty"/>,
    /// so every shared piece of unsaved-changes protection -- the footer's
    /// Save/Discard buttons, the shell's navigation guard, <see cref="CanActivateSelected"/> --
    /// treats an open, edited processing profile the same as any other unsaved
    /// edit, without the two kinds of dirty state being the same flag.
    /// </summary>
    public override bool HasUnsavedChanges => base.HasUnsavedChanges || Routes.Any(route => route.IsProfileDirty);

    // EntityActions and EssentialCollectionActions are deliberately not
    // overridden. Those exist so ConfigurationPageBlock can build a page's
    // Actions menu and Add button out of delegates it then mirrors onto its
    // own dependency properties. Audio's blocks bind commands instead, which
    // carry their own enablement through CanExecute, so the whole surface --
    // and the shell-side syncing behind it -- has nothing left to do here.

    public override void SelectKey(object key)
    {
        _session.Select((string)key);
        SelectedKey = _session.SelectedKey;

        LoadEditor();
    }

    // ------------------------------------------------------------
    // What the commands above actually do.
    //
    // Each of these is the whole flow for one action -- guard, ask, apply --
    // held in one place instead of split between a page shell's click handler
    // and a ViewModel method. The blocks never see any of it: they bind a
    // command and nothing more.
    // ------------------------------------------------------------

    private void RequestSelect(string key)
    {
        if (Equals(key, SelectedKey) || !GuardUnsavedChanges())
        {
            return;
        }

        SelectKey(key);
    }

    private void PromptCreateTarget()
    {
        if (!GuardUnsavedChanges())
        {
            return;
        }

        Prompts.PromptForName("Create audio target", "Name the new audio target.", string.Empty, CreateTarget);
    }

    private void PromptRenameTarget()
    {
        if (!TryBeginSelectedTargetAction(out string current))
        {
            return;
        }

        Prompts.PromptForName("Rename audio target", $"Rename '{current}'.", current, RenameSelected);
    }

    private void PromptDuplicateTarget()
    {
        if (!TryBeginSelectedTargetAction(out string current))
        {
            return;
        }

        Prompts.PromptForName(
            "Duplicate audio target",
            $"Name the copy of '{current}'.",
            $"{current} copy",
            DuplicateSelected
        );
    }

    private void PromptDeleteTarget()
    {
        if (!TryBeginSelectedTargetAction(out string current))
        {
            return;
        }

        if (Prompts.Confirm($"Delete '{current}'? This cannot be undone."))
        {
            DeleteSelected();
        }
    }

    /// <summary>
    /// Runs the unsaved-changes guard, then confirms the same target is still
    /// selected afterwards.
    /// </summary>
    /// <remarks>
    /// Saving or discarding inside the guard can rebuild the selector, and a
    /// discarded draft changes the selection outright. Acting on the name
    /// captured before the guard would then rename, duplicate or delete
    /// something nobody chose.
    /// </remarks>
    private bool TryBeginSelectedTargetAction(out string currentName)
    {
        currentName = string.Empty;

        if (!IsTargetSelected)
        {
            return false;
        }

        object? before = SelectedKey;

        if (!GuardUnsavedChanges() || !Equals(before, SelectedKey) || !IsTargetSelected)
        {
            return false;
        }

        currentName = TargetName;
        return true;
    }

    public void PromptCreateProfile(AudioRouteRow row) =>
        Prompts.PromptForName(
            "Create processing profile",
            "Name the new processing profile. It starts with no EQ bands.",
            string.Empty,
            name => CreateProfile(row, name)
        );

    public void PromptRenameProfile(AudioRouteRow row)
    {
        if (row.SavedProfileName is not string current)
        {
            return;
        }

        Prompts.PromptForName(
            "Rename processing profile",
            $"Rename '{current}'. Every audio target using it is updated to match.",
            current,
            name => RenameProfile(row, name)
        );
    }

    public void PromptDeleteProfile(AudioRouteRow row)
    {
        if (row.SavedProfileName is not string profileName)
        {
            return;
        }

        if (
            Prompts.Confirm(
                $"Delete processing profile '{profileName}'? Audio targets using it will keep their outputs, "
                    + "but that processing profile will be removed from them."
            )
        )
        {
            DeleteProfile(row);
        }
    }

    /// <summary>
    /// Opens the inline profile editor, or closes it when it is already open.
    /// Collapsing is not a discard -- see <see cref="LoadProfileEditor"/>.
    /// </summary>
    public void ToggleProfileEditor(AudioRouteRow row)
    {
        if (row.IsProfileEditorExpanded)
        {
            row.IsProfileEditorExpanded = false;
            return;
        }

        LoadProfileEditor(row);
    }

    public void PromptSwitchOutput(DiscoveredAudioOutputRow row)
    {
        if (row?.Alias is not string alias || !GuardUnsavedChanges())
        {
            return;
        }

        _ = SwitchOutputAsync(alias);
    }

    public void PromptAliasOutput(DiscoveredAudioOutputRow row)
    {
        if (row is null || !GuardUnsavedChanges())
        {
            return;
        }

        Prompts.PromptForNameAsync(
            row.IsConfigured ? "Rename audio output" : "Add audio output to PCMode",
            $"Give '{row.Name}' a PCMode name. Audio targets and spaces use this name.",
            row.Alias ?? row.Name,
            alias => DeviceDiscovery.SetAliasAsync(row, alias)
        );
    }

    /// <summary>
    /// Loads the configuration the first time this page is shown, then only
    /// refreshes live machine state on later visits -- reloading the editor
    /// would discard an edit left in progress.
    /// </summary>
    public void OnActivated()
    {
        if (!_loaded)
        {
            _loaded = true;
            Refresh();
        }
        else
        {
            _ = RefreshLiveStatusAsync();
        }

        _liveRefresh.Start();
        _waveformRefresh.Start();
    }

    public void OnDeactivated()
    {
        _liveRefresh.Stop();
        _waveformRefresh.Stop();
    }

    /// <summary>
    /// Pushes the reference route's current waveform envelope into
    /// <see cref="AudioTargetEditorViewModel.Waveform"/>, or marks it
    /// unavailable when there is nothing to show -- no provider, no Host, or
    /// no route reported at all. Never touches <see cref="Refresh"/> or the
    /// route wiring: this is purely a redraw of already-flowing telemetry.
    /// </summary>
    private async Task RefreshWaveformAsync()
    {
        (AudioProvider? provider, _) = ReadProvider<AudioProvider>();

        if (provider is null)
        {
            SetWaveformUnavailable();
            return;
        }

        IReadOnlyList<AudioRouteWaveform> waveforms = await provider
            .WaveformAsync(CancellationToken.None)
            .ConfigureAwait(true);

        // The first route is the same "reference" route the alignment
        // section already measures everything else against -- see
        // AudioRouteRow.IsReference -- so the waveform tracks whichever
        // output a user would call this target's primary one.
        AudioRouteWaveform? reference = waveforms.Count > 0 ? waveforms[0] : null;

        if (reference is not { Available: true })
        {
            SetWaveformUnavailable();
            return;
        }

        _editor.Waveform.IsAvailable = true;
        _editor.Waveform.Buckets = reference.Buckets;
    }

    private void SetWaveformUnavailable()
    {
        _editor.Waveform.IsAvailable = false;
        _editor.Waveform.Buckets = AudioWaveformViewModel.Idle();
    }

    public override void Refresh()
    {
        if (!TryReadSnapshot(out AudioSnapshot snapshot))
        {
            return;
        }

        RefreshProfileChoices();

        LoadResources(snapshot);
        LoadEditor();
        DeviceDiscovery.Update(snapshot);

        ApplyGlobalStatus(snapshot);
    }

    /// <summary>
    /// Refreshes machine/runtime state while the page is open without
    /// replacing an in-progress edit. Never rebuilds <see cref="Routes"/> --
    /// unlike <see cref="Refresh"/>, which a deliberate reload (selecting a
    /// different target, an explicit Discard) is allowed to do -- so an open
    /// processing-profile editor, dirty or not, is never collapsed or
    /// silently reloaded out from under whoever is editing it.
    /// </summary>
    public async Task RefreshLiveStatusAsync()
    {
        if (IsBusy || base.HasUnsavedChanges)
        {
            // Either route wiring itself is being edited (a different
            // output, a changed delay/gain, a pending create/rename/
            // duplicate) and the whole editor could disagree with what is on
            // screen. Either way, touch nothing but the outputs panel:
            // rereading the snapshot's target/output list
            // (RefreshLiveStatusOnly's LoadResources call) would rebuild the
            // selector from what is saved, which does not include an unsaved
            // draft, and silently drop it.
            await DeviceDiscovery.Inventory.ScanAsync().ConfigureAwait(true);
            return;
        }

        RefreshLiveStatusOnly();
    }

    /// <summary>
    /// The live-tick half of <see cref="Refresh"/>: rereads the snapshot and
    /// updates the resource list, profile choices, and each route's runtime
    /// text/target state, but leaves every <see cref="AudioRouteRow"/>
    /// instance and its processing-profile fields untouched.
    /// </summary>
    private void RefreshLiveStatusOnly()
    {
        if (!TryReadSnapshot(out AudioSnapshot snapshot))
        {
            return;
        }

        RefreshProfileChoices();

        LoadResources(snapshot);
        UpdateLiveRouteStatus(snapshot);
        DeviceDiscovery.Update(snapshot);

        ApplyGlobalStatus(snapshot);
    }

    private bool TryReadSnapshot(out AudioSnapshot snapshot)
    {
        (AudioProvider? provider, string? problem) = ReadProvider<AudioProvider>();

        if (provider is null)
        {
            ResetToProblemState(problem ?? "The configuration could not be read.");

            snapshot = default!;

            return false;
        }

        try
        {
            snapshot = provider.Discover();

            return true;
        }
        catch (Exception exception)
        {
            ResetToProblemState($"Audio could not be read. {exception.Message}");

            snapshot = default!;

            return false;
        }
    }

    private void ResetToProblemState(string message)
    {
        _session.Clear();
        _routes.Clear();
        _routes.SetChoices([]);
        ProfileChoices.Clear();

        SelectedKey = null;

        StatusMessage = message;
        DeviceDiscovery.ReportProblem(StatusMessage);
    }

    private void ApplyGlobalStatus(AudioSnapshot snapshot)
    {
        EngineText = snapshot.EngineAvailable
            ? "The PCMode audio engine is running."
            : "The PCMode audio engine is not available, "
                + $"so audio targets cannot play. {snapshot.Runtime.Problem}";

        StatusMessage = snapshot.Available
            ? $"{snapshot.Devices.Count} configured output(s), " + $"{snapshot.Targets.Count} audio target(s)."
            : $"Windows audio could not be read. {snapshot.Problem}";
    }

    /// <summary>
    /// Updates only what changes on its own while a target is open: each
    /// route's runtime text and the target's overall running/health state.
    /// Deliberately does not touch route wiring or any row's processing-profile
    /// fields -- see <see cref="RefreshLiveStatusOnly"/>.
    /// </summary>
    private void UpdateLiveRouteStatus(AudioSnapshot snapshot)
    {
        if (SelectedKey is not string selectedKey)
        {
            return;
        }

        AudioTargetStatus? status = snapshot.Targets.FirstOrDefault(target =>
            string.Equals(TargetKey(target.Name), selectedKey, StringComparison.Ordinal)
        );

        foreach (AudioRouteRow route in Routes)
        {
            AudioTargetRouteStatus? routeStatus = status?.Routes.FirstOrDefault(candidate =>
                string.Equals(candidate.Output, route.Output, StringComparison.OrdinalIgnoreCase)
            );

            route.RuntimeText = routeStatus is null ? string.Empty : FormatRuntimeText(routeStatus);
        }

        TargetState(status?.State ?? AudioTargetState.Stopped, status?.Problem, status?.Detail);
    }

    /// <summary>What the shell's Rescan button calls -- DeviceDiscovery's own ScanAsync, exposed as a delegate for CurrentHardwareRescan to bind to.</summary>
    public Func<Task> RescanOutputs => DeviceDiscovery.Inventory.ScanAsync;

    private void RefreshProfileChoices()
    {
        (AudioProvider? provider, _) = ReadProvider<AudioProvider>();

        if (provider is null)
        {
            return;
        }

        ReconcileChoices(
            ProfileChoices,
            [
                AudioRouteRow.NoProfile,
                .. provider
                    .Profiles.Select(profile => profile.Name)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
            ]
        );
    }

    /// <summary>
    /// Patches <paramref name="target"/> in place to hold exactly
    /// <paramref name="desired"/>, in that order -- adding what is missing,
    /// removing what is gone, and moving what only changed position.
    /// </summary>
    /// <remarks>
    /// A live-status tick rereads the profile list on an interval while an
    /// <see cref="AudioRouteRow"/>'s Processing-profile <c>ComboBox</c> stays
    /// bound, TwoWay, to this same collection as its
    /// <c>ItemsSource</c>. <see cref="ObservableCollection{T}.Clear"/> followed
    /// by re-adding the same values -- correct as a snapshot, but not as a
    /// sequence of change notifications -- leaves a moment where the bound
    /// <c>SelectedItem</c>'s current value is absent from the list. WPF reacts
    /// to that moment, not to the eventual re-add: it nulls the selection and
    /// pushes that null back through the TwoWay binding into the row, blanking
    /// the field and marking the route dirty. Never removing an item that is
    /// still wanted -- only adding, removing what is truly gone, and
    /// reordering via <see cref="ObservableCollection{T}.Move"/> -- means the
    /// selected value's presence in the list is never interrupted.
    /// </remarks>
    private static void ReconcileChoices(ObservableCollection<string> target, IReadOnlyList<string> desired)
    {
        for (int index = target.Count - 1; index >= 0; index--)
        {
            if (!desired.Contains(target[index], StringComparer.OrdinalIgnoreCase))
            {
                target.RemoveAt(index);
            }
        }

        for (int index = 0; index < desired.Count; index++)
        {
            string wanted = desired[index];

            int currentIndex = IndexOf(target, wanted);

            if (currentIndex < 0)
            {
                target.Insert(Math.Min(index, target.Count), wanted);
            }
            else if (currentIndex != index)
            {
                target.Move(currentIndex, index);
            }
        }
    }

    private static int IndexOf(ObservableCollection<string> target, string value)
    {
        for (int index = 0; index < target.Count; index++)
        {
            if (string.Equals(target[index], value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private async Task ActivateSelectedForShellAsync() => await ActivateSelectedAsync().ConfigureAwait(true);

    public async Task<string?> ActivateSelectedAsync(CancellationToken cancellationToken = default)
    {
        ClearError();

        if (!IsTargetSelected)
        {
            return SetError("Choose an audio target first.");
        }

        if (HasUnsavedChanges)
        {
            return SetError("Save the audio target before activating it.");
        }

        // Not dirty and something is selected: it must be a clean, persisted
        // target -- an unsaved draft is always dirty from the moment it exists.
        string name = _session.Selected!.Name;

        return await ApplyAsync(name, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Switches Windows' default playback device straight to a configured
    /// output alias, from the Audio outputs section's own Switch button --
    /// the direct-output counterpart to <see cref="ActivateSelectedAsync"/>,
    /// which is reserved for Audio Targets.
    /// </summary>
    public async Task<string?> SwitchOutputAsync(string alias, CancellationToken cancellationToken = default)
    {
        ClearError();

        return await ApplyAsync(alias, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Applies one value of the audio resource -- an Audio Target's name or a
    /// device alias -- and reports what came back. Shared by
    /// <see cref="ActivateSelectedAsync"/> and <see cref="SwitchOutputAsync"/>,
    /// which differ only in where the value comes from and what they validate
    /// first.
    /// </summary>
    private async Task<string?> ApplyAsync(string value, CancellationToken cancellationToken)
    {
        (AudioProvider? provider, string? problem) = ReadProvider<AudioProvider>();

        if (provider is null)
        {
            StatusMessage = problem ?? "The configuration could not be read.";

            return SetError(StatusMessage);
        }

        IsBusy = true;

        StatusMessage = $"Activating '{value}'...";

        try
        {
            await Task.Run(() => provider.Apply(value, cancellationToken), cancellationToken).ConfigureAwait(true);

            string? observed = SafeRead(provider);

            bool reachedTarget = string.Equals(observed, value, StringComparison.OrdinalIgnoreCase);

            Refresh();

            StatusMessage = reachedTarget
                ? $"'{value}' activated."
                : $"'{value}' requested. Windows is still settling.";

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
            StatusMessage = $"Could not activate '{value}'. " + exception.Message;

            Workspace.Fault(Category, $"Could not activate '{value}'.", exception);

            return SetError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Starts a named, unsaved audio target with no outputs chosen yet --
    /// an editing-session draft (<see cref="ConfigurationEditingSession{TKey, TEntity}.BeginCreate"/>)
    /// that exists only in the selector until Save writes it. Validated the
    /// same way <see cref="RenameSelected"/> and <see cref="DuplicateSelected"/>
    /// validate a name, so a name that collides with an output or another
    /// target is refused consistently no matter which of the three actions is
    /// asking.
    /// </summary>
    public string? CreateTarget(string name)
    {
        ClearError();

        if (TargetNameProblem(name) is string problem)
        {
            return SetError(problem);
        }

        string trimmed = name.Trim();

        _session.BeginCreate(TargetKey(trimmed), trimmed);
        SelectedKey = _session.SelectedKey;

        RunWithoutDirtyTracking(() =>
        {
            TargetName = trimmed;

            _routes.Load([OutputChoices.FirstOrDefault()]);
        });

        // Unsaved from the moment it exists -- matching Displays' and Rgb's
        // own "create" actions -- so switching away goes through the same
        // Save/Discard/Cancel protection as any other unsaved edit, rather
        // than silently dropping a target nobody chose to discard yet.
        MarkDirty();

        RaiseRouteCounts();
        RaiseValidation();

        TargetState(AudioTargetState.Stopped, null, null);

        StatusMessage = "Choose the outputs this target plays through, then save it.";

        return null;
    }

    /// <summary>
    /// Retitles the selected target's working copy -- a draft, or an already
    /// saved target -- without writing anything; the page's own Save is what
    /// persists it, under whichever name is current at that point.
    /// </summary>
    public string? RenameSelected(string newName)
    {
        ClearError();

        if (!IsTargetSelected)
        {
            return SetError("Choose an audio target first.");
        }

        string trimmed = newName?.Trim() ?? string.Empty;

        if (string.Equals(TargetName, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (TargetNameProblem(trimmed, _session.Selected?.Name) is string problem)
        {
            return SetError(problem);
        }

        bool wasDraft = _session.IsDraft;

        _session.RenameCurrent(TargetKey(trimmed), trimmed);
        SelectedKey = _session.SelectedKey;

        RunWithoutDirtyTracking(() => TargetName = trimmed);

        MarkDirty();
        RaiseValidation();

        StatusMessage = wasDraft
            ? $"New audio target will be saved as '{trimmed}'."
            : $"'{trimmed}' will be saved as the new name.";

        return null;
    }

    /// <summary>
    /// Copies the target currently open in the editor -- saved or not -- into
    /// a new, unsaved draft named <paramref name="newName"/>. Nothing is
    /// written until Save.
    /// </summary>
    public string? DuplicateSelected(string newName)
    {
        ClearError();

        if (!IsTargetSelected)
        {
            return SetError("Choose an audio target first.");
        }

        string trimmed = newName?.Trim() ?? string.Empty;

        if (TargetNameProblem(trimmed) is string problem)
        {
            return SetError(problem);
        }

        string source = TargetName;

        _session.BeginDuplicate(TargetKey(trimmed), trimmed);
        SelectedKey = _session.SelectedKey;

        RunWithoutDirtyTracking(() =>
        {
            List<(
                string? Output,
                int DelayMs,
                double Gain,
                double Volume,
                bool Required,
                string ProfileName
            )> clonedRoutes =
            [
                .. Routes.Select(route =>
                    (route.Output, route.DelayMs, route.Gain, route.Volume, route.Required, route.ProfileName)
                ),
            ];

            TargetName = trimmed;

            _routes.Load(clonedRoutes.Select(route => route.Output));

            for (int index = 0; index < clonedRoutes.Count; index++)
            {
                var source = clonedRoutes[index];
                AudioRouteRow row = Routes[index];

                row.DelayMs = source.DelayMs;
                row.Gain = source.Gain;
                row.Volume = source.Volume;
                row.Required = source.Required;
                row.ProfileName = source.ProfileName;
            }
        });

        MarkDirty();

        RaiseRouteCounts();
        RaiseValidation();

        StatusMessage = $"'{trimmed}' will be saved as a copy of '{source}'.";

        return null;
    }

    public string? DeleteSelected()
    {
        ClearError();

        if (!IsTargetSelected)
        {
            return SetError("Choose an audio target first.");
        }

        if (_session.HasOverlay)
        {
            // An unsaved draft, or a pending rename of an existing target:
            // either way there is nothing on disk yet that reflects the
            // current selector entry, so abandoning it is enough.
            _session.Discard();
            Refresh();

            return null;
        }

        string removed = _session.Selected!.Name;

        ModeSaveOutcome outcome = AudioConfigurationStore.RemoveTarget(Workspace.Root, removed);

        if (!outcome.Success)
        {
            StatusMessage = outcome.Problem ?? "That could not be removed.";

            Workspace.Warn(Category, StatusMessage);

            return SetError(StatusMessage);
        }

        Refresh();

        StatusMessage = Workspace.Apply($"'{removed}' was removed.");

        return null;
    }

    public void LoadProfileEditor(AudioRouteRow row)
    {
        ClearError();

        if (row?.SavedProfileName is not string profileName)
        {
            return;
        }

        if (row.IsProfileDirty)
        {
            // Re-expanding a row with unsaved profile edits shows what is
            // still in memory rather than fetching the last-saved values
            // back over it -- collapsing the card was never a discard.
            row.IsProfileEditorExpanded = true;

            return;
        }

        (AudioProvider? provider, string? problem) = ReadProvider<AudioProvider>();

        if (provider is null)
        {
            SetError(problem ?? "The audio configuration could not be read.");

            return;
        }

        AudioProcessingProfileSettings? profile = provider.Profile(profileName);

        if (profile is null)
        {
            SetError($"Processing profile '{profileName}' could not be found.");

            return;
        }

        RunWithoutDirtyTracking(() =>
        {
            row.ProfileBands.Clear();

            row.ProfilePreampGainDb = profile.PreampGainDb;

            foreach (EqBand band in profile.Bands)
            {
                row.ProfileBands.Add(
                    new AudioEqBandRow(() => OnProfileFieldChanged(row))
                    {
                        Type = band.Type,

                        FrequencyHz = band.FrequencyHz,

                        GainDb = band.GainDb,

                        Q = band.Q,
                    }
                );
            }
        });

        row.RecomputeResponse();

        row.IsProfileEditorExpanded = true;
    }

    public string? CreateProfile(AudioRouteRow row, string name)
    {
        ClearError();

        string trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return SetError("Give the processing profile a name.");
        }

        if (
            ProfileChoices.Any(existing =>
                !string.Equals(existing, AudioRouteRow.NoProfile, StringComparison.Ordinal)
                && string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return SetError($"A processing profile named '{trimmed}' already exists.");
        }

        var edit = new AudioProcessingProfileEdit(trimmed, 0, []);

        ModeSaveOutcome outcome = AudioConfigurationStore.SaveProfile(Workspace.Root, edit);

        if (!outcome.Success)
        {
            return SetError(outcome.Problem ?? "The processing profile could not be created.");
        }

        RefreshProfileChoices();

        row.ProfileName = trimmed;

        LoadProfileEditor(row);

        StatusMessage = Workspace.Apply($"Processing profile '{trimmed}' created.");

        return null;
    }

    public string? RenameProfile(AudioRouteRow row, string newName)
    {
        ClearError();

        if (row.SavedProfileName is not string oldName)
        {
            return SetError("Choose a processing profile first.");
        }

        string trimmed = newName?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return SetError("Give the processing profile a name.");
        }

        if (string.Equals(oldName, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (
            ProfileChoices.Any(existing =>
                !string.Equals(existing, AudioRouteRow.NoProfile, StringComparison.Ordinal)
                && !string.Equals(existing, oldName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return SetError($"A processing profile named '{trimmed}' already exists.");
        }

        ModeSaveOutcome outcome = AudioConfigurationStore.RenameProfile(Workspace.Root, oldName, trimmed);

        if (!outcome.Success)
        {
            return SetError(outcome.Problem ?? "The processing profile could not be renamed.");
        }

        RefreshProfileChoices();

        foreach (AudioRouteRow route in Routes)
        {
            if (string.Equals(route.SavedProfileName, oldName, StringComparison.OrdinalIgnoreCase))
            {
                route.ProfileName = trimmed;
            }
        }

        row.ProfileName = trimmed;

        LoadProfileEditor(row);

        StatusMessage = Workspace.Apply($"Processing profile '{oldName}' renamed to '{trimmed}'.");

        return null;
    }

    public string? DeleteProfile(AudioRouteRow row)
    {
        ClearError();

        if (row.SavedProfileName is not string profileName)
        {
            return SetError("Choose a processing profile first.");
        }

        ModeSaveOutcome outcome = AudioConfigurationStore.RemoveProfile(Workspace.Root, profileName);

        if (!outcome.Success)
        {
            return SetError(outcome.Problem ?? "The processing profile could not be deleted.");
        }

        foreach (AudioRouteRow route in Routes)
        {
            if (string.Equals(route.SavedProfileName, profileName, StringComparison.OrdinalIgnoreCase))
            {
                route.ProfileName = AudioRouteRow.NoProfile;

                ClearRowProfileDirty(route);
            }
        }

        row.ProfileBands.Clear();
        row.IsProfileEditorExpanded = false;

        RefreshProfileChoices();

        StatusMessage = Workspace.Apply($"Processing profile '{profileName}' deleted.");

        return null;
    }

    public void AddProfileBand(AudioRouteRow row)
    {
        if (row is null)
        {
            return;
        }

        row.ProfileBands.Add(
            new AudioEqBandRow(() => OnProfileFieldChanged(row))
            {
                Type = EqFilterType.Peak,

                FrequencyHz = 1000,

                GainDb = 0,

                Q = 0.70710677,
            }
        );

        OnProfileFieldChanged(row);
    }

    public void RemoveProfileBand(AudioRouteRow row, AudioEqBandRow band)
    {
        if (row is null || band is null)
        {
            return;
        }

        if (row.ProfileBands.Remove(band))
        {
            OnProfileFieldChanged(row);
        }
    }

    public string? SaveProfile(AudioRouteRow row)
    {
        ClearError();

        if (row?.SavedProfileName is not string profileName)
        {
            return SetError("Choose a processing profile first.");
        }

        if (SaveProfileCore(row, profileName) is string problem)
        {
            return SetError(problem);
        }

        StatusMessage = Workspace.Apply($"Processing profile '{profileName}' saved.");

        OnCalibrationChanged(row);

        return null;
    }

    /// <summary>
    /// Writes <paramref name="row"/>'s in-memory profile fields to the profile
    /// store and clears its dirty state. Shared by <see cref="SaveProfile"/>
    /// (the row's own "Save profile" button) and <see cref="SaveChangesCore"/>
    /// (the page's Save button, which must not silently drop a dirty profile
    /// on its way to saving the target's route wiring).
    /// </summary>
    private string? SaveProfileCore(AudioRouteRow row, string profileName)
    {
        EqBand[] bands;

        try
        {
            bands = [.. row.ProfileBands.Select(band => band.ToBand())];

            _ = new ParametricEqProfile((float)row.ProfilePreampGainDb, bands);
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }

        var edit = new AudioProcessingProfileEdit(profileName, (float)row.ProfilePreampGainDb, bands);

        ModeSaveOutcome outcome = AudioConfigurationStore.SaveProfile(Workspace.Root, edit);

        if (!outcome.Success)
        {
            StatusMessage = outcome.Problem ?? "The processing profile could not be saved.";

            Workspace.Warn(Category, StatusMessage);

            return StatusMessage;
        }

        ClearRowProfileDirty(row);

        return null;
    }

    public void AddRoute()
    {
        if (!IsTargetSelected)
        {
            return;
        }

        _routes.Add();
    }

    public void RemoveRoute(AudioRouteRow row)
    {
        if (!IsTargetSelected)
        {
            return;
        }

        _routes.Remove(row);
    }

    public void SelectReferenceRoute(AudioRouteRow row)
    {
        if (row is null || Routes.Count != MaxRoutes)
        {
            return;
        }

        int selectedIndex = Routes.IndexOf(row);

        if (selectedIndex <= 0)
        {
            return;
        }

        AudioRouteRow previousReference = Routes[0];

        int selectedDelay = row.DelayMs;

        double selectedGain = row.Gain;

        row.DelayMs = previousReference.DelayMs;

        row.Gain = previousReference.Gain;

        previousReference.DelayMs = selectedDelay;

        previousReference.Gain = selectedGain;

        Routes.Move(selectedIndex, 0);

        MarkDirty();
        RaiseRouteCounts();
        RaiseValidation();
    }

    protected override string? SaveChangesCore()
    {
        if (!IsTargetSelected)
        {
            return "Choose an audio target first.";
        }

        bool savedAnyProfile = false;

        foreach (AudioRouteRow route in Routes)
        {
            if (route.IsProfileDirty && route.SavedProfileName is string profileName)
            {
                if (SaveProfileCore(route, profileName) is string profileProblem)
                {
                    StatusMessage = profileProblem;

                    return profileProblem;
                }

                OnCalibrationChanged(route);

                savedAnyProfile = true;
            }
        }

        // Nothing but a processing profile was dirty: the route wiring itself
        // has nothing new to write, so saving it again would only be a
        // no-op file write.
        if (!base.HasUnsavedChanges)
        {
            StatusMessage = Workspace.Apply(
                savedAnyProfile ? "Processing profile changes saved." : $"'{TargetName}' saved."
            );

            return null;
        }

        if (ValidationProblem is string problem)
        {
            StatusMessage = problem;

            return problem;
        }

        IsBusy = true;

        try
        {
            // A fresh draft has no persisted entity to overwrite; a rename or
            // an ordinary edit writes over whatever this selection's origin
            // was actually loaded from.
            string? oldName = _session.IsDraft ? null : _session.Selected?.Name;

            var edit = new AudioTargetEdit(TargetName.Trim(), [.. Routes.Select(route => route.ToEdit())]);

            ModeSaveOutcome outcome = AudioConfigurationStore.SaveTarget(Workspace.Root, edit, oldName);

            if (!outcome.Success)
            {
                StatusMessage = outcome.Problem ?? "That could not be saved.";

                Workspace.Warn(Category, StatusMessage);

                return outcome.Problem;
            }

            _session.Commit();

            Refresh();

            StatusMessage = Workspace.Apply($"'{edit.Name}' saved.");

            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected override void DiscardChangesCore()
    {
        if (_session.HasOverlay)
        {
            _session.Discard();
        }

        Refresh();
    }

    private static string TargetKey(string name) => "target:" + name;

    private AudioRouteRow NewRoute(string? output)
    {
        AudioRouteRow row = null!;

        row = new AudioRouteRow([], OnRouteChanged, () => OnCalibrationChanged(row), () => OnProfileFieldChanged(row))
        {
            Output = output,
        };

        return row;
    }

    private void OnRouteChanged()
    {
        _routes.RefreshAvailableSelections();
        MarkDirty();
        RaiseValidation();

        // The reference route's own Output can change (its ComboBox is the
        // same TwoWay-bound choice as any other route's), and the alignment
        // section names it by that value.
        OnPropertyChanged(nameof(ReferenceOutputName));
    }

    private void OnRouteMembershipChanged()
    {
        MarkDirty();
        RaiseRouteCounts();
        RaiseValidation();
    }

    /// <summary>
    /// A processing-profile field (Preamp, or an EQ band's type/frequency/gain/Q)
    /// changed on <paramref name="row"/>. Only marks the row's own profile dirty
    /// state -- unlike <see cref="OnRouteChanged"/>, this must not touch the
    /// route's dirty state or push a live recalibration, both of which belong
    /// to the moment the profile is actually saved (<see cref="SaveProfile"/>).
    /// </summary>
    private void OnProfileFieldChanged(AudioRouteRow row)
    {
        // Always recomputed, even once the row is already marked dirty below
        // (and so would otherwise short-circuit) -- the Visual response graph
        // and headroom summary must keep tracking every keystroke, not just
        // the edit that first dirtied the profile.
        row.RecomputeResponse();

        if (IsSuppressingDirtyTracking || row.IsProfileDirty)
        {
            return;
        }

        row.MarkProfileDirty();

        RaiseProfileDirtyChanged();
    }

    private void ClearRowProfileDirty(AudioRouteRow row)
    {
        if (!row.IsProfileDirty)
        {
            return;
        }

        row.ClearProfileDirty();

        RaiseProfileDirtyChanged();
    }

    private void RaiseProfileDirtyChanged()
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));

        OnPropertyChanged(nameof(CanDiscard));

        OnDirtyStateChanged();
    }

    private void OnCalibrationChanged(AudioRouteRow row)
    {
        if (IsSuppressingDirtyTracking)
        {
            return;
        }

        if (
            _session.IsDraft
            || _session.Selected?.Name is not string targetName
            || !IsTargetRunning
            || string.IsNullOrWhiteSpace(row.Output)
        )
        {
            return;
        }

        if (_recalibrationInFlight)
        {
            _recalibrationPending = row;

            return;
        }

        _ = LiveRecalibrateAsync(targetName, row);
    }

    private async Task LiveRecalibrateAsync(string targetName, AudioRouteRow row)
    {
        _recalibrationInFlight = true;

        try
        {
            (AudioProvider? provider, _) = ReadProvider<AudioProvider>();

            if (provider is null || row.Output is not string output)
            {
                return;
            }

            try
            {
                await Task.Run(() =>
                        provider.Recalibrate(
                            targetName,
                            output,
                            TimeSpan.FromMilliseconds(row.DelayMs),
                            row.Gain,
                            row.Volume,
                            row.SavedProfileName,
                            CancellationToken.None
                        )
                    )
                    .ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                StatusMessage = "Live calibration could not reach " + $"the audio engine. {exception.Message}";
            }
        }
        finally
        {
            _recalibrationInFlight = false;

            if (_recalibrationPending is AudioRouteRow pending)
            {
                _recalibrationPending = null;

                OnCalibrationChanged(pending);
            }
        }
    }

    private string? TargetNameProblem(string name, string? ignoredName = null)
    {
        string trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return "Give the audio target a name.";
        }

        if (OutputChoices.Any(output => string.Equals(output, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{trimmed}' is already one of your outputs. " + "Give the target a name of its own.";
        }

        if (
            _session.Items.Any(target =>
                !string.Equals(target.Name, ignoredName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(target.Name, trimmed, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return $"An audio target named '{trimmed}' already exists.";
        }

        return null;
    }

    private void LoadResources(AudioSnapshot snapshot)
    {
        List<AudioDeviceMapping> devices =
        [
            .. snapshot.Devices.OrderBy(device => device.LogicalName, StringComparer.OrdinalIgnoreCase),
        ];

        _routes.SetChoices([.. devices.Select(device => device.LogicalName)]);

        _session.Load(snapshot.Targets, keep: SelectedKey as string);

        SelectedKey = _session.SelectedKey;
    }

    private void LoadEditor()
    {
        if (_session.Selected is not { } status)
        {
            _routes.Clear();

            ClearDirtyState();

            RaiseRouteCounts();
            RaiseValidation();

            TargetState(AudioTargetState.Stopped, null, null);

            return;
        }

        RunWithoutDirtyTracking(() =>
        {
            TargetName = status.Name;

            _routes.Load(status.Routes.Select(route => route.Output));

            for (int index = 0; index < status.Routes.Count; index++)
            {
                AudioTargetRouteStatus route = status.Routes[index];
                AudioRouteRow row = Routes[index];

                row.DelayMs = route.DelayMs;
                row.Gain = route.Gain;
                row.Volume = route.Volume ?? AudioRouteRow.DefaultVolume;
                row.Required = route.Required;
                row.ProfileName = route.ProfileName ?? AudioRouteRow.NoProfile;
                row.RuntimeText = FormatRuntimeText(route);
            }
        });

        ClearDirtyState();

        RaiseRouteCounts();
        RaiseValidation();

        TargetState(status.State, status.Problem, status.Detail);
    }

    private void TargetState(AudioTargetState state, string? problem, string? detail)
    {
        _targetState = state;

        TargetStateText = state switch
        {
            AudioTargetState.Running => $"'{TargetName}' is playing " + "through every one of its outputs.",

            AudioTargetState.Degraded => $"'{TargetName}' is playing, "
                + $"and one of its outputs is not healthy. {problem}",

            AudioTargetState.Failed => $"'{TargetName}' stopped " + $"on its own. {problem}",

            AudioTargetState.Unavailable => "The PCMode audio engine is not available, so "
                + $"'{TargetName}' cannot play. {problem}",

            _ => $"'{TargetName}' is configured "
                + "and not running. Activate it, or a target whose "
                + "audio output is this one, to start it.",
        };

        TargetHealthText = detail;

        OnPropertyChanged(nameof(IsTargetRunning));
    }

    private void RaiseRouteCounts()
    {
        UpdateReferenceFlags();
        RebuildRouteCards();
        RefreshCommands();

        OnPropertyChanged(nameof(CanAddRoute));

        OnPropertyChanged(nameof(CanRemoveRoute));

        OnPropertyChanged(nameof(IsNewTarget));

        OnPropertyChanged(nameof(ShowAlignment));

        OnPropertyChanged(nameof(ReferenceOutputName));
    }

    /// <summary>
    /// Keeps the per-route Advanced cards -- one profile editor and one
    /// alignment card each -- matching <see cref="Routes"/>' current membership
    /// and order.
    /// </summary>
    /// <remarks>
    /// Patched in place rather than rebuilt: a card holds live editing state
    /// (an open, possibly dirty processing profile), so replacing the instance
    /// for a route that merely moved would discard exactly the work the
    /// live-status refresh is careful to protect elsewhere.
    /// </remarks>
    private void RebuildRouteCards()
    {
        Reconcile(ProfileCards, card => card.Route, route => new AudioProfileCard(this, route));
        Reconcile(AlignmentCards, card => card.Route, route => new AudioAlignmentCard(route));
    }

    private void Reconcile<TCard>(
        ObservableCollection<TCard> cards,
        Func<TCard, AudioRouteRow> routeOf,
        Func<AudioRouteRow, TCard> create
    )
        where TCard : IDisposable
    {
        for (int index = cards.Count - 1; index >= 0; index--)
        {
            if (!Routes.Contains(routeOf(cards[index])))
            {
                cards[index].Dispose();
                cards.RemoveAt(index);
            }
        }

        for (int index = 0; index < Routes.Count; index++)
        {
            AudioRouteRow route = Routes[index];
            int currentIndex = -1;

            for (int candidate = 0; candidate < cards.Count; candidate++)
            {
                if (ReferenceEquals(routeOf(cards[candidate]), route))
                {
                    currentIndex = candidate;
                    break;
                }
            }

            if (currentIndex < 0)
            {
                cards.Insert(index, create(route));
            }
            else if (currentIndex != index)
            {
                cards.Move(currentIndex, index);
            }
        }
    }

    /// <summary>
    /// Projects the editing session's selector entries into the rows the
    /// selector block repeats, preserving which one reads as selected.
    /// </summary>
    private void RebuildSelectorItems()
    {
        Selector.Items.Clear();

        foreach (ConfigurationEntityListItem<string> item in _session.SelectorItems)
        {
            Selector.Items.Add(new AudioSelectorItem(item.Key, item.Title, item.Summary, RequestSelect));
        }

        SyncSelectorSelection();
    }

    private void SyncSelectorSelection()
    {
        foreach (AudioSelectorItem item in Selector.Items)
        {
            item.IsSelected = Equals(item.Key, SelectedKey);
        }
    }

    /// <summary>
    /// Reacts to this ViewModel's own state changing, so the block tree's
    /// shape and every command's enablement follow without a control having
    /// to mirror them.
    /// </summary>
    private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SelectedKey):
                SyncSelectorSelection();
                OnPropertyChanged(nameof(Editor));
                RefreshCommands();
                break;

            case nameof(TargetName):
            case nameof(ShowAlignment):
                _editor.Refresh();
                break;
        }
    }

    private void RefreshCommands()
    {
        (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DiscardCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RenameTargetCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DuplicateTargetCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteTargetCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ActivateTargetCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AddRouteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveRouteCommand as RelayCommand<AudioRouteRow>)?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// The first route is the Multi-output alignment section's reference --
    /// see <see cref="AudioRouteRow.IsReference"/> -- kept in sync with
    /// <see cref="Routes"/>' current membership and order every time either
    /// changes.
    /// </summary>
    private void UpdateReferenceFlags()
    {
        for (int index = 0; index < Routes.Count; index++)
        {
            Routes[index].IsReference = index == 0;
        }
    }

    private static string FormatRuntimeText(AudioTargetRouteStatus route) =>
        route.Runtime is null
            ? string.Empty
            : $"{route.Runtime}" + (route.Problem is null ? string.Empty : $" -- {route.Problem}");

    private void RaiseValidation()
    {
        NotifyValidationChanged(nameof(ValidationProblem), nameof(CanSave), nameof(CanActivateSelected));

        RefreshCommands();
    }

    protected override void OnDirtyStateChanged()
    {
        base.OnDirtyStateChanged();

        OnPropertyChanged(nameof(CanActivateSelected));

        RefreshCommands();
    }

    protected override void OnBusyStateChanged()
    {
        base.OnBusyStateChanged();

        OnPropertyChanged(nameof(CanActivateSelected));

        RefreshCommands();
    }
}

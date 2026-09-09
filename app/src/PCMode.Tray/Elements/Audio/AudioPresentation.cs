using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using PCMode.Audio.Core.Buffers;
using PCMode.Tray.Elements;

namespace PCMode.Tray.Elements.Audio;

/// <summary>
/// The presentation models Audio's block tree is built from.
/// </summary>
/// <remarks>
/// Nothing in this file names a Functional Block, and nothing in it touches
/// WPF beyond <see cref="ICommand"/>. Each of these types is what lands in one
/// region; an implicit <see cref="System.Windows.DataTemplate"/> -- declared
/// once, in Views/AudioBlockTemplates.xaml -- is the only thing that knows
/// which block a given type resolves into. That indirection is what keeps
/// <see cref="AudioViewModel"/> able to describe its own structure without
/// depending on the controls that draw it.
///
/// These are projections, not a second copy of the editor: every one of them
/// reads and writes through the <see cref="AudioViewModel"/> it was built
/// from, which remains the single owner of Audio's state and behaviour.
/// </remarks>
public sealed class AudioSelectorItem : ObservableObject
{
    private readonly Action<string> _select;
    private bool _isSelected;

    public AudioSelectorItem(string key, string title, string? summary, Action<string> select)
    {
        Key = key;
        Title = title;
        Summary = summary;
        _select = select;
        SelectCommand = new RelayCommand(() => _select(Key));
    }

    public string Key { get; }

    public string Title { get; }

    public string? Summary { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public ICommand SelectCommand { get; }
}

/// <summary>The audio-target list, and the one action that adds to it.</summary>
public sealed class AudioSelectorViewModel(ICommand createCommand)
{
    public string Title => "Audio targets";

    public ObservableCollection<AudioSelectorItem> Items { get; } = [];

    public ICommand CreateCommand { get; } = createCommand;
}

/// <summary>
/// What the editor region holds when no audio target is selected.
/// </summary>
/// <remarks>
/// A distinct type rather than a null editor, so the empty state resolves
/// through the same implicit-template mechanism as everything else: the region
/// is handed a view model either way and never has to be told which of two
/// modes it is in.
/// </remarks>
public sealed class AudioNoTargetViewModel(string message)
{
    public string Message { get; } = message;
}

/// <summary>
/// The Audio page's top-of-page waveform: a small, live snapshot of what one
/// route is actually playing right now.
/// </summary>
/// <remarks>
/// Neutral state only -- frequency and channel count mean nothing here, and
/// neither does the audio engine. <see cref="AudioViewModel"/> is the only
/// thing that pushes into <see cref="Buckets"/>/<see cref="IsAvailable"/>,
/// on its own poll separate from the page's ordinary live-status refresh; how
/// that becomes a picture is entirely <c>UI/Components/Visualizations</c>'s.
/// </remarks>
public sealed class AudioWaveformViewModel : ObservableObject
{
    /// <summary>Within the 128-256 buckets a visualization needs; not itself part of any contract.</summary>
    internal const int BucketCount = 128;

    private bool _isAvailable;
    private IReadOnlyList<WaveformEnvelopeSample> _buckets = Idle();

    /// <summary>
    /// Whether the Host actually reported waveform telemetry for this route
    /// just now -- distinct from <see cref="Buckets"/> being flat, which is
    /// what a route that is running and genuinely silent looks like too.
    /// False means there is nothing to show, not that nothing is playing.
    /// </summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        internal set => SetProperty(ref _isAvailable, value);
    }

    /// <summary>The current envelope, oldest bucket first. Every bucket sits at (0, 0) while <see cref="IsAvailable"/> is false.</summary>
    public IReadOnlyList<WaveformEnvelopeSample> Buckets
    {
        get => _buckets;
        internal set => SetProperty(ref _buckets, value);
    }

    internal static WaveformEnvelopeSample[] Idle() => new WaveformEnvelopeSample[BucketCount];
}

/// <summary>One audio target, open for editing.</summary>
public sealed class AudioTargetEditorViewModel : ObservableObject
{
    private readonly AudioViewModel _audio;

    internal AudioTargetEditorViewModel(AudioViewModel audio)
    {
        _audio = audio;
        Hardware = new AudioOutputsViewModel(audio);
    }

    /// <summary>The top-of-page waveform. A stable instance -- see <see cref="AudioViewModel"/>'s poll.</summary>
    public AudioWaveformViewModel Waveform { get; } = new();

    public string Title => _audio.TargetName.Length == 0 ? "No audio target selected" : _audio.TargetName;

    public ObservableCollection<AudioRouteRow> EssentialItems => _audio.Routes;

    public ObservableCollection<AudioProfileCard> ProfileItems => _audio.ProfileCards;

    public ObservableCollection<AudioAlignmentCard> AlignmentItems => _audio.AlignmentCards;

    public ObservableCollection<string> ProfileChoices => _audio.ProfileChoices;

    public AudioOutputsViewModel Hardware { get; }

    public bool ShowAlignment => _audio.ShowAlignment;

    public string AddLabel => "Add output";

    public string ActivateLabel => "Activate";

    public ICommand AddCommand => _audio.AddRouteCommand;

    public ICommand RemoveCommand => _audio.RemoveRouteCommand;

    public ICommand SetReferenceCommand => _audio.SetReferenceRouteCommand;

    public ICommand RenameCommand => _audio.RenameTargetCommand;

    public ICommand DuplicateCommand => _audio.DuplicateTargetCommand;

    public ICommand DeleteCommand => _audio.DeleteTargetCommand;

    public ICommand ActivateCommand => _audio.ActivateTargetCommand;

    /// <summary>Re-reads whatever this projects that is not itself a live collection.</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ShowAlignment));
    }
}

/// <summary>
/// One route's processing profile, as the Advanced section presents it.
/// </summary>
/// <remarks>
/// A wrapper rather than the <see cref="AudioRouteRow"/> itself because that
/// same row already appears in the Essential region, and an implicit template
/// maps one type to exactly one block. The row is the model in both places;
/// what differs is which block it is being shown as, so the second appearance
/// gets a type of its own. <see cref="AudioAlignmentCard"/> exists for the
/// same reason.
/// </remarks>
public sealed class AudioProfileCard : ObservableObject, IDisposable
{
    private readonly AudioViewModel _audio;

    internal AudioProfileCard(AudioViewModel audio, AudioRouteRow route)
    {
        _audio = audio;
        Route = route;
        Route.PropertyChanged += OnRoutePropertyChanged;

        CreateProfileCommand = new RelayCommand(() => _audio.PromptCreateProfile(Route));
        EditProfileCommand = new RelayCommand(() => _audio.ToggleProfileEditor(Route));
        RenameProfileCommand = new RelayCommand(
            () => _audio.PromptRenameProfile(Route),
            () => Route.HasProcessingProfile
        );
        DeleteProfileCommand = new RelayCommand(
            () => _audio.PromptDeleteProfile(Route),
            () => Route.HasProcessingProfile
        );
        AddBandCommand = new RelayCommand(() => _audio.AddProfileBand(Route), () => Route.HasProcessingProfile);
        SaveProfileCommand = new RelayCommand(() => _audio.SaveProfile(Route), () => Route.HasProcessingProfile);
        RemoveBandCommand = new RelayCommand<AudioEqBandRow>(band => _audio.RemoveProfileBand(Route, band));
    }

    public AudioRouteRow Route { get; }

    public string Title => Route.ProfileName;

    public string? Subtitle => Route.Output;

    public bool HasProfile => Route.HasProcessingProfile;

    /// <summary>The preamp field, the response readout and the action row all bind the route directly.</summary>
    public AudioRouteRow Preamp => Route;

    public AudioRouteRow Response => Route;

    public AudioRouteRow Actions => Route;

    public ObservableCollection<AudioEqBandRow> Bands => Route.ProfileBands;

    public bool IsExpanded
    {
        get => Route.IsProfileEditorExpanded;
        set => Route.IsProfileEditorExpanded = value;
    }

    public ICommand CreateProfileCommand { get; }

    public ICommand EditProfileCommand { get; }

    public ICommand RenameProfileCommand { get; }

    public ICommand DeleteProfileCommand { get; }

    public ICommand AddBandCommand { get; }

    public ICommand SaveProfileCommand { get; }

    public ICommand RemoveBandCommand { get; }

    public void Dispose() => Route.PropertyChanged -= OnRoutePropertyChanged;

    private void OnRoutePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AudioRouteRow.ProfileName):
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(HasProfile));
                RaiseProfileCommandState();
                break;

            case nameof(AudioRouteRow.Output):
                OnPropertyChanged(nameof(Subtitle));
                break;

            case nameof(AudioRouteRow.IsProfileEditorExpanded):
                OnPropertyChanged(nameof(IsExpanded));
                break;
        }
    }

    private void RaiseProfileCommandState()
    {
        (RenameProfileCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteProfileCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AddBandCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveProfileCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}

/// <summary>
/// One non-reference route's offset and delay against the reference output.
/// </summary>
/// <remarks>See <see cref="AudioProfileCard"/> for why this wraps the row rather than being it.</remarks>
public sealed class AudioAlignmentCard : ObservableObject, IDisposable
{
    internal AudioAlignmentCard(AudioRouteRow route)
    {
        Route = route;
        Route.PropertyChanged += OnRoutePropertyChanged;
    }

    public AudioRouteRow Route { get; }

    public string Title => Route.Output ?? string.Empty;

    /// <summary>The offset/delay fields bind the route directly.</summary>
    public AudioRouteRow Fields => Route;

    /// <summary>The reference route is the baseline the others align to, so it has nothing of its own to show.</summary>
    public bool IsVisible => !Route.IsReference;

    public void Dispose() => Route.PropertyChanged -= OnRoutePropertyChanged;

    private void OnRoutePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioRouteRow.Output))
        {
            OnPropertyChanged(nameof(Title));
        }

        if (e.PropertyName == nameof(AudioRouteRow.IsReference))
        {
            OnPropertyChanged(nameof(IsVisible));
        }
    }
}

/// <summary>The machine's own Windows playback endpoints, and what can be done to one.</summary>
public sealed class AudioOutputsViewModel : ObservableObject
{
    private readonly AudioViewModel _audio;

    internal AudioOutputsViewModel(AudioViewModel audio)
    {
        _audio = audio;

        RescanCommand = new AsyncRelayCommand(
            () => audio.DeviceDiscovery.Inventory.ScanAsync(),
            () => audio.DeviceDiscovery.Inventory.CanScan
        );

        SwitchCommand = new RelayCommand<DiscoveredAudioOutputRow>(
            row => _audio.PromptSwitchOutput(row),
            row => row.CanSwitch
        );

        AliasCommand = new RelayCommand<DiscoveredAudioOutputRow>(row => _audio.PromptAliasOutput(row));

        audio.DeviceDiscovery.Inventory.PropertyChanged += OnInventoryPropertyChanged;
    }

    public string Title => "Current hardware";

    public ObservableCollection<DiscoveredAudioOutputRow> Items => _audio.DeviceDiscovery.Inventory.Rows;

    public string? Problem => _audio.DeviceDiscovery.Inventory.Problem;

    public bool HasProblem => _audio.DeviceDiscovery.Inventory.HasProblem;

    public ICommand RescanCommand { get; }

    public ICommand SwitchCommand { get; }

    public ICommand AliasCommand { get; }

    private void OnInventoryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName
            is nameof(HardwareInventoryList<DiscoveredAudioOutputRow>.Problem)
                or nameof(HardwareInventoryList<DiscoveredAudioOutputRow>.HasProblem)
        )
        {
            OnPropertyChanged(nameof(Problem));
            OnPropertyChanged(nameof(HasProblem));
        }

        if (e.PropertyName == nameof(HardwareInventoryList<DiscoveredAudioOutputRow>.CanScan))
        {
            (RescanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}

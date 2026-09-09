using System.Collections.ObjectModel;
using PCMode.Engine;
using PCMode.Tray.Elements;
using PCMode.Tray.Elements.Configuration;

namespace PCMode.Tray.Elements.Applications;

/// <summary>One persisted application and its latest runtime status.</summary>
public sealed class ApplicationConfigurationEntry : ObservableObject
{
    private string _statusText;

    public ApplicationConfigurationEntry(string name, ApplicationDefinition definition, HardwareItem status)
    {
        Name = name;
        Definition = definition;
        _statusText = status.Summary;
    }

    public string Name { get; }

    public ApplicationDefinition Definition { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public void Update(HardwareItem status)
    {
        StatusText = status.Summary;
    }
}

/// <summary>One row of a "requires" block being edited: a resource, and the value it must hold.</summary>
public sealed class RequirementRowViewModel : ObservableEditableRow
{
    private string _resourceKey;
    private string _value;

    public RequirementRowViewModel(string resourceKey, string value, Action onChanged)
        : base(onChanged)
    {
        _resourceKey = resourceKey;
        _value = value;
    }

    public string ResourceKey
    {
        get => _resourceKey;
        set => Set(ref _resourceKey, value);
    }

    public string Value
    {
        get => _value;
        set => Set(ref _value, value);
    }
}

public sealed class ApplicationsViewModel : ConfigurationPageViewModelBase
{
    private const string Category = "Applications";

    /// <summary>The default a new readiness action starts at, matching <see cref="ApplicationCatalog"/>.</summary>
    private const int DefaultReadyWithinMs = 8000;

    private const int DefaultStartWithinMs = 3000;
    private const int DefaultStopWithinMs = 5000;

    private readonly ConfigurationEditingSession<string, ApplicationConfigurationEntry> _session;

    private bool _isLaunchOnly;
    private string _processName = string.Empty;
    private bool _isStartUri;
    private string _startPath = string.Empty;
    private string _startArgumentsText = string.Empty;
    private string _startUri = string.Empty;
    private bool _hasStop;
    private bool _isStopUri;
    private string _stopPath = string.Empty;
    private string _stopArgumentsText = string.Empty;
    private string _stopUri = string.Empty;
    private bool _reactivate;
    private string _restartWhenText = string.Empty;
    private bool _readyEnabled;
    private bool _readyActivate;
    private string _readyKeys = string.Empty;
    private int _readyWithinMs = DefaultReadyWithinMs;
    private int _startWithinMs = DefaultStartWithinMs;
    private int _stopWithinMs = DefaultStopWithinMs;

    private string? _availabilityMessage;

    public ApplicationsViewModel(IConfigurationWorkspace workspace, IConfigurationPrompts? prompts = null)
        : base(workspace, prompts)
    {
        _session = new ConfigurationEditingSession<string, ApplicationConfigurationEntry>(
            keyOf: application => application.Name,
            titleOf: application => application.Name,
            summaryOf: application => application.StatusText
        );

        AddRequirementCommand = new RelayCommand(AddRequirement);
        RemoveRequirementCommand = new RelayCommand<RequirementRowViewModel>(RemoveRequirement);

        TreePresentation = new ConfigurationPagePresentation(
            "Applications",
            new ConfigurationSelectorPresentation(
                "Applications",
                SelectorItems,
                Commands.Create,
                Commands.ShowCreate,
                SelectItemCommand
            ),
            ConfigurationEditorPresentation.Create(
                essential:
                [
                    new("Application", new TerminalElementContent("Application launch and detection settings", this)),
                ],
                advanced:
                [
                    new(
                        "Readiness and dependencies",
                        new TerminalElementContent("Readiness rules and resource requirements", this),
                        IsCollapsible: true
                    ),
                ]
            )
        );
    }

    public ObservableCollection<ConfigurationEntityListItem<string>> SelectorItems => _session.SelectorItems;

    public ObservableCollection<RequirementRowViewModel> Requires { get; } = [];

    public System.Windows.Input.ICommand AddRequirementCommand { get; }

    public System.Windows.Input.ICommand RemoveRequirementCommand { get; }

    public void AddRequirement()
    {
        Requires.Add(new RequirementRowViewModel(string.Empty, string.Empty, OnRequirementsChanged));

        OnRequirementsChanged();
    }

    public void RemoveRequirement(RequirementRowViewModel requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        if (Requires.Remove(requirement))
        {
            OnRequirementsChanged();
        }
    }

    public string? SelectedApplication
    {
        get => (string?)SelectedKey;
        set
        {
            if (Equals(SelectedKey, value))
            {
                return;
            }

            if (value is not null)
            {
                SelectKey(value);
            }
        }
    }

    public override void SelectKey(object key)
    {
        _session.Select((string)key);
        SelectedKey = _session.SelectedKey;
        LoadEditor();
    }

    /// <summary>Whether PCMode launches this application without runtime detection.</summary>
    public bool IsLaunchOnly
    {
        get => _isLaunchOnly;
        set
        {
            if (SetDirtyProperty(ref _isLaunchOnly, value))
            {
                RaiseValidation();
                OnPropertyChanged(nameof(ActivateActionLabel));
                OnPropertyChanged(nameof(ActivateActionToolTip));
            }
        }
    }

    public string ActivateActionLabel => IsLaunchOnly ? "Launch" : "Activate";

    public string ActivateActionToolTip =>
        IsLaunchOnly ? "Launch this application now" : "Start or stop this application now without switching spaces";

    /// <summary>The process name Windows reports, without ".exe".</summary>
    public string ProcessName
    {
        get => _processName;
        set
        {
            if (SetDirtyProperty(ref _processName, value))
            {
                RaiseValidation();
            }
        }
    }

    /// <summary>Whether the start command opens a web address rather than a program.</summary>
    public bool IsStartUri
    {
        get => _isStartUri;
        set
        {
            if (SetDirtyProperty(ref _isStartUri, value))
            {
                RaiseValidation();
            }
        }
    }

    public string StartPath
    {
        get => _startPath;
        set
        {
            if (SetDirtyProperty(ref _startPath, value))
            {
                RaiseValidation();
            }
        }
    }

    /// <summary>One argument per line. Ignored when <see cref="IsStartUri"/> is set.</summary>
    public string StartArgumentsText
    {
        get => _startArgumentsText;
        set => SetDirtyProperty(ref _startArgumentsText, value);
    }

    public string StartUri
    {
        get => _startUri;
        set
        {
            if (SetDirtyProperty(ref _startUri, value))
            {
                RaiseValidation();
            }
        }
    }

    /// <summary>Whether this application is asked to close itself, rather than simply ended.</summary>
    public bool HasStop
    {
        get => _hasStop;
        set
        {
            if (SetDirtyProperty(ref _hasStop, value))
            {
                RaiseValidation();
            }
        }
    }

    public bool IsStopUri
    {
        get => _isStopUri;
        set
        {
            if (SetDirtyProperty(ref _isStopUri, value))
            {
                RaiseValidation();
            }
        }
    }

    public string StopPath
    {
        get => _stopPath;
        set
        {
            if (SetDirtyProperty(ref _stopPath, value))
            {
                RaiseValidation();
            }
        }
    }

    public string StopArgumentsText
    {
        get => _stopArgumentsText;
        set => SetDirtyProperty(ref _stopArgumentsText, value);
    }

    public string StopUri
    {
        get => _stopUri;
        set
        {
            if (SetDirtyProperty(ref _stopUri, value))
            {
                RaiseValidation();
            }
        }
    }

    /// <summary>Whether the start command is re-issued even when it is already running.</summary>
    public bool Reactivate
    {
        get => _reactivate;
        set => SetDirtyProperty(ref _reactivate, value);
    }

    /// <summary>Resource keys, comma-separated, that leave a running copy stale.</summary>
    public string RestartWhenText
    {
        get => _restartWhenText;
        set => SetDirtyProperty(ref _restartWhenText, value);
    }

    public bool ReadyEnabled
    {
        get => _readyEnabled;
        set
        {
            if (SetDirtyProperty(ref _readyEnabled, value))
            {
                RaiseValidation();
            }
        }
    }

    public bool ReadyActivate
    {
        get => _readyActivate;
        set => SetDirtyProperty(ref _readyActivate, value);
    }

    /// <summary>Plain printable characters to type once the application has a window.</summary>
    public string ReadyKeys
    {
        get => _readyKeys;
        set => SetDirtyProperty(ref _readyKeys, value);
    }

    public int ReadyWithinMs
    {
        get => _readyWithinMs;
        set => SetDirtyProperty(ref _readyWithinMs, value);
    }

    public int StartWithinMs
    {
        get => _startWithinMs;
        set => SetDirtyProperty(ref _startWithinMs, value);
    }

    public int StopWithinMs
    {
        get => _stopWithinMs;
        set => SetDirtyProperty(ref _stopWithinMs, value);
    }

    /// <summary>Why the application does not look installed, or null when it does.</summary>
    public string? AvailabilityMessage
    {
        get => _availabilityMessage;
        private set
        {
            if (SetProperty(ref _availabilityMessage, value))
            {
                OnPropertyChanged(nameof(HasAvailabilityMessage));
            }
        }
    }

    public bool HasAvailabilityMessage => _availabilityMessage is not null;

    /// <summary>
    /// Why what is on screen could not be saved, or null when it could.
    /// </summary>
    /// <remarks>
    /// Repeats only what a person can act on immediately -- a command with
    /// neither a program nor an address, an empty dependency row. Everything
    /// else -- an unknown resource key, a wait out of range, the process name
    /// already taken -- is the shared parser's to refuse, with a message that
    /// can name the provider that refused it.
    /// </remarks>
    public override string? ValidationProblem
    {
        get
        {
            if (SelectedApplication is null)
            {
                return null;
            }

            if (!_isLaunchOnly && string.IsNullOrWhiteSpace(_processName))
            {
                return "Give the process name to monitor, without '.exe', or turn off process monitoring.";
            }

            if (_isStartUri ? string.IsNullOrWhiteSpace(_startUri) : string.IsNullOrWhiteSpace(_startPath))
            {
                return _isStartUri
                    ? "Give the web address to open when this application starts."
                    : "Give the program to run when this application starts.";
            }

            if (_hasStop && (_isStopUri ? string.IsNullOrWhiteSpace(_stopUri) : string.IsNullOrWhiteSpace(_stopPath)))
            {
                return _isStopUri
                    ? "Give the web address that stops this application, or turn the stop command off."
                    : "Give the program that stops this application, or turn the stop command off.";
            }

            if (Requires.Any(row => string.IsNullOrWhiteSpace(row.ResourceKey) || string.IsNullOrWhiteSpace(row.Value)))
            {
                return "Every dependency needs both a resource and a value. Remove an empty row instead of leaving it blank.";
            }

            if (_readyEnabled && !_readyActivate && string.IsNullOrWhiteSpace(_readyKeys))
            {
                return "The readiness action does nothing. Turn on 'Bring to front', give it keys to type, or turn it off.";
            }

            return null;
        }
    }

    public override bool CanSave => HasSelectedApplication && HasUnsavedChanges && !IsBusy && ValidationProblem is null;

    public bool HasSelectedApplication => SelectedApplication is not null;

    public string? SelectedApplicationStatus =>
        _session.IsDraft
            ? "Not yet saved."
            : _session
                .Items.FirstOrDefault(item =>
                    string.Equals(item.Name, _session.Selected?.Name, StringComparison.OrdinalIgnoreCase)
                )
                ?.StatusText;

    public bool CanActivate =>
        SelectedApplication is not null
        && !_session.IsDraft
        && !HasUnsavedChanges
        && !IsBusy
        && ValidationProblem is null;

    public override EntityActions Actions =>
        new()
        {
            EntityNoun = "application",
            CurrentDisplayName = SelectedApplication,
            Create = CreateApplication,
            Rename = HasSelectedApplication ? RenameSelected : null,
            Duplicate = HasSelectedApplication ? DuplicateSelected : null,
            Delete = HasSelectedApplication ? () => DeleteSelected() : null,
            Activate = HasSelectedApplication ? ActivateSelectedForShellAsync : null,
            ActivateLabel = ActivateActionLabel,
            CanActivateNow = CanActivate,
        };

    public override void Refresh()
    {
        string? keep = SelectedKey as string;
        bool preserveOverlayEditor = _session.HasOverlay && HasSelectedApplication;

        (IReadOnlyList<ApplicationProvider> providers, string? problem) = ReadProviders<ApplicationProvider>();

        if (problem is not null)
        {
            _session.Clear();
            SelectedKey = null;
            ClearForm();
            SetError(problem);
            StatusMessage = ErrorMessage;
            NotifySelectionChanged();
            return;
        }

        IReadOnlyDictionary<string, HardwareItem> statuses = DiscoverApplications(providers);

        _session.Load(
            providers
                .OrderBy(provider => provider.Key, StringComparer.OrdinalIgnoreCase)
                .Select(provider => new ApplicationConfigurationEntry(
                    provider.Key,
                    provider.Definition,
                    statuses.GetValueOrDefault(provider.Key) ?? UnknownStatus(provider.Key)
                )),
            keep
        );
        SelectedKey = _session.SelectedKey;

        if (!preserveOverlayEditor && SelectedApplication is not null)
        {
            LoadEditor();
        }
        else if (!preserveOverlayEditor)
        {
            ClearForm();
        }

        StatusMessage = $"{SelectorItems.Count} configured application(s).";
        ClearError();
        NotifySelectionChanged();
    }

    public async Task RefreshApplicationStatusesAsync()
    {
        if (IsBusy || _session.Items.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, HardwareItem>? statuses = await Task.Run(CaptureApplicationStatuses)
            .ConfigureAwait(true);

        if (statuses is null || IsBusy)
        {
            return;
        }

        foreach (ApplicationConfigurationEntry item in _session.Items)
        {
            if (statuses.TryGetValue(item.Name, out HardwareItem? status))
            {
                item.Update(status);
            }
        }

        RefreshSelectorProjection();
        OnPropertyChanged(nameof(SelectedApplicationStatus));
    }

    private IReadOnlyDictionary<string, HardwareItem>? CaptureApplicationStatuses()
    {
        (IReadOnlyList<ApplicationProvider> providers, string? problem) = ReadProviders<ApplicationProvider>();

        if (problem is not null)
        {
            return null;
        }

        return DiscoverApplications(providers);
    }

    private static IReadOnlyDictionary<string, HardwareItem> DiscoverApplications(
        IReadOnlyList<ApplicationProvider> providers
    ) =>
        new ApplicationDiscoverer([.. providers])
            .Discover()
            .ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);

    private static HardwareItem UnknownStatus(string name) =>
        new(name, HardwareItemStatus.Error, "Could not be checked.", []);

    public string? CreateApplication(string name)
    {
        if (ApplicationNameProblem(name) is string problem)
        {
            return problem;
        }

        string trimmed = name.Trim();
        _session.BeginCreate(trimmed, trimmed);
        SelectedKey = _session.SelectedKey;
        ClearForm();
        MarkDirty();
        NotifySelectionChanged();
        StatusMessage = $"New application '{trimmed}' created. Configure its launch command, then Save.";
        return null;
    }

    /// <summary>Creates an unsaved full copy of the selected application under a new name.</summary>
    public string? DuplicateSelected(string name)
    {
        if (!HasSelectedApplication)
        {
            return "Choose an application first.";
        }

        if (ApplicationNameProblem(name) is string problem)
        {
            return problem;
        }

        string source = SelectedApplication!;
        string trimmed = name.Trim();
        _session.BeginDuplicate(trimmed, trimmed);
        SelectedKey = _session.SelectedKey;
        MarkDirty();
        NotifySelectionChanged();
        StatusMessage = $"Copy of '{source}' ready as '{trimmed}'. Save to keep it.";
        return null;
    }

    /// <summary>
    /// Saves whatever is on screen for the selected application. Returns null
    /// on success, or the reason it was refused.
    /// </summary>
    protected override string? SaveChangesCore()
    {
        if (SelectedApplication is null)
        {
            return "Choose an application first.";
        }

        if (ValidationProblem is string problem)
        {
            StatusMessage = problem;
            return problem;
        }

        string selectedName = SelectedApplication;
        string? previousName = _session.IsDraft ? null : _session.Selected?.Name;
        return Persist(previousName, BuildEdit(selectedName), selectedName, $"'{selectedName}' saved.");
    }

    private async Task ActivateSelectedForShellAsync() => await ActivateSelectedAsync().ConfigureAwait(true);

    public async Task<string?> ActivateSelectedAsync(CancellationToken cancellationToken = default)
    {
        ClearError();

        if (SelectedApplication is null || _session.IsDraft)
        {
            return SetError("Choose a saved application first.");
        }

        if (HasUnsavedChanges)
        {
            return SetError("Save or discard changes before activating an application.");
        }

        string name = SelectedApplication;
        (IReadOnlyList<ApplicationProvider> providers, string? problem) = ReadProviders<ApplicationProvider>();

        if (problem is not null)
        {
            StatusMessage = problem;
            return SetError(problem);
        }

        ApplicationProvider? provider = providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, name, StringComparison.OrdinalIgnoreCase)
        );

        if (provider is null)
        {
            StatusMessage = $"'{name}' is not a configured application.";
            return SetError(StatusMessage);
        }

        string? current = SafeRead(provider);
        string target;

        if (!provider.Definition.CanObserve)
        {
            target = ApplicationProvider.Running;
        }
        else if (string.Equals(current, ApplicationProvider.Running, StringComparison.OrdinalIgnoreCase))
        {
            target = ApplicationProvider.Stopped;
        }
        else if (string.Equals(current, ApplicationProvider.Stopped, StringComparison.OrdinalIgnoreCase))
        {
            target = ApplicationProvider.Running;
        }
        else
        {
            StatusMessage = $"Could not determine whether '{name}' is running.";
            UpdateApplicationStatus(name);
            return SetError(StatusMessage);
        }

        bool starting = string.Equals(target, ApplicationProvider.Running, StringComparison.OrdinalIgnoreCase);
        string activatingStatus =
            !provider.Definition.CanObserve ? $"Launching '{name}'..."
            : starting ? $"Starting '{name}'..."
            : $"Stopping '{name}'...";

        await RunActivationAsync(
                Category,
                activatingStatus,
                async token =>
                {
                    if (starting)
                    {
                        ResourceAvailability availability = provider.AvailabilityOf(ApplicationProvider.Running);
                        AvailabilityMessage = Describe(availability);

                        if (availability.State == AvailabilityState.Unavailable)
                        {
                            return new ActivationOutcome(false, availability.Explain());
                        }
                    }

                    await Task.Run(() => provider.Apply(target, token), token).ConfigureAwait(true);

                    string? observed = SafeRead(provider);
                    UpdateApplicationStatus(name);
                    AvailabilityMessage = Describe(provider.AvailabilityOf(ApplicationProvider.Running));

                    if (!provider.Definition.CanObserve)
                    {
                        return new ActivationOutcome(true, $"Launch requested for '{name}'. Runtime is not monitored.");
                    }

                    bool reachedTarget = string.Equals(observed, target, StringComparison.OrdinalIgnoreCase);
                    return reachedTarget
                        ? new ActivationOutcome(
                            true,
                            starting ? $"'{name}' started and verified." : $"'{name}' stopped and verified."
                        )
                        : new ActivationOutcome(
                            false,
                            starting
                                ? $"Start requested for '{name}', but the running state could not be verified."
                                : $"Stop requested for '{name}', but the stopped state could not be verified."
                        );
                },
                cancellationToken
            )
            .ConfigureAwait(true);

        return ErrorMessage;
    }

    /// <summary>
    /// Renames the selected application -- moving the resource key itself in
    /// every mode that has an opinion about it, not just a value that named it.
    /// </summary>
    public string? RenameSelected(string newName)
    {
        if (SelectedApplication is null)
        {
            return "Choose an application first.";
        }

        string trimmed = (newName ?? string.Empty).Trim();
        string oldName = SelectedApplication;

        if (string.Equals(oldName, trimmed, StringComparison.Ordinal))
        {
            return null;
        }

        if (ApplicationNameProblem(trimmed, _session.Selected?.Name) is string problem)
        {
            return problem;
        }

        _session.RenameCurrent(trimmed, trimmed);
        SelectedKey = _session.SelectedKey;
        MarkDirty();
        NotifySelectionChanged();
        StatusMessage = $"'{oldName}' renamed to '{trimmed}'. Save to keep the change.";
        return null;
    }

    /// <summary>
    /// Removes the selected application. Refused, with nothing changed, while
    /// any mode still has an opinion about it -- Running or Stopped both count.
    /// </summary>
    public string? DeleteSelected()
    {
        ClearError();

        if (SelectedApplication is null)
        {
            return SetError("Choose an application first.");
        }

        string name = SelectedApplication;

        if (_session.HasOverlay)
        {
            _session.Discard();
            SelectedKey = _session.SelectedKey;
            Refresh();
            StatusMessage = "Unsaved application discarded.";
            return null;
        }

        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = ApplicationConfigurationStore.RemoveApplication(Workspace.Root, name);

            if (!outcome.Success)
            {
                StatusMessage = outcome.Problem ?? "That could not be removed.";
                Workspace.Warn(Category, StatusMessage);
                return SetError(StatusMessage);
            }

            Refresh();
            return ReportSuccess($"'{name}' removed.");
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
            SelectedKey = _session.SelectedKey;
        }

        Refresh();
        StatusMessage = "Changes discarded.";
    }

    private string? Persist(string? previousName, ApplicationEdit edit, string selectAfter, string message)
    {
        ClearError();
        IsBusy = true;

        try
        {
            ModeSaveOutcome outcome = ApplicationConfigurationStore.SaveApplication(Workspace.Root, previousName, edit);

            if (!outcome.Success)
            {
                StatusMessage = outcome.Problem ?? "That could not be saved.";
                Workspace.Warn(Category, StatusMessage);
                return outcome.Problem;
            }

            _session.Commit();
            Refresh();

            if (SelectorItems.Any(item => string.Equals(item.Key, selectAfter, StringComparison.OrdinalIgnoreCase)))
            {
                SelectKey(selectAfter);
            }

            return ReportSuccess(message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadEditor()
    {
        if (_session.Selected is not { } found)
        {
            return;
        }

        ApplicationDefinition definition = found.Definition;

        RunWithoutDirtyTracking(() =>
        {
            IsLaunchOnly = !definition.CanObserve;
            ProcessName = definition.ProcessName;
            IsStartUri = definition.Start.Uri is not null;
            StartPath = definition.Start.Executable ?? string.Empty;
            StartArgumentsText = string.Join(Environment.NewLine, definition.Start.Arguments);
            StartUri = definition.Start.Uri ?? string.Empty;

            HasStop = definition.Stop is not null;
            IsStopUri = definition.Stop?.Uri is not null;
            StopPath = definition.Stop?.Executable ?? string.Empty;
            StopArgumentsText = definition.Stop is null
                ? string.Empty
                : string.Join(Environment.NewLine, definition.Stop.Arguments);
            StopUri = definition.Stop?.Uri ?? string.Empty;

            Reactivate = definition.Reactivate;

            Requires.Clear();

            foreach (ResourceRequirement requirement in definition.Requires)
            {
                Requires.Add(new RequirementRowViewModel(requirement.Key, requirement.Target, OnRequirementsChanged));
            }

            RestartWhenText = string.Join(", ", definition.RestartWhen);

            ReadyEnabled = definition.Ready is not null;
            ReadyActivate = definition.Ready?.Activate ?? false;
            ReadyKeys = definition.Ready?.Keys ?? string.Empty;
            ReadyWithinMs = (int)(definition.Ready?.Within.TotalMilliseconds ?? DefaultReadyWithinMs);

            StartWithinMs = (int)definition.StartWithin.TotalMilliseconds;

            StopWithinMs = (int)definition.StopWithin.TotalMilliseconds;
        });

        ClearDirtyState();

        UpdateApplicationStatus(found.Name);
        AvailabilityMessage = ReadProviders<ApplicationProvider>()
            .Providers.FirstOrDefault(provider =>
                string.Equals(provider.Key, found.Name, StringComparison.OrdinalIgnoreCase)
            )
            is { } provider
            ? Describe(provider.AvailabilityOf(ApplicationProvider.Running))
            : null;

        NotifySelectionChanged();
        RaiseValidation();
    }

    private void ClearForm()
    {
        RunWithoutDirtyTracking(() =>
        {
            IsLaunchOnly = false;
            ProcessName = string.Empty;
            IsStartUri = false;
            StartPath = string.Empty;
            StartArgumentsText = string.Empty;
            StartUri = string.Empty;

            HasStop = false;
            IsStopUri = false;
            StopPath = string.Empty;
            StopArgumentsText = string.Empty;
            StopUri = string.Empty;

            Reactivate = false;

            Requires.Clear();

            RestartWhenText = string.Empty;

            ReadyEnabled = false;
            ReadyActivate = false;
            ReadyKeys = string.Empty;
            ReadyWithinMs = DefaultReadyWithinMs;

            StartWithinMs = DefaultStartWithinMs;
            StopWithinMs = DefaultStopWithinMs;
        });

        ClearDirtyState();

        AvailabilityMessage = null;
    }

    private void OnRequirementsChanged()
    {
        MarkDirty();
        RaiseValidation();
    }

    private ApplicationEdit BuildEdit(string name) =>
        new(
            name,
            IsLaunchOnly ? ApplicationObservationKind.None : ApplicationObservationKind.Process,
            ProcessName.Trim(),
            new LaunchCommandEdit(
                IsStartUri ? null : StartPath.Trim(),
                IsStartUri ? [] : SplitLines(StartArgumentsText),
                IsStartUri ? StartUri.Trim() : null
            ),
            HasStop
                ? new LaunchCommandEdit(
                    IsStopUri ? null : StopPath.Trim(),
                    IsStopUri ? [] : SplitLines(StopArgumentsText),
                    IsStopUri ? StopUri.Trim() : null
                )
                : null,
            Reactivate,
            [.. Requires.Select(row => new ApplicationRequirementEdit(row.ResourceKey.Trim(), row.Value.Trim()))],
            SplitList(RestartWhenText),
            ReadyEnabled
                ? new ReadinessEdit(ReadyActivate, string.IsNullOrEmpty(ReadyKeys) ? null : ReadyKeys, ReadyWithinMs)
                : null,
            StartWithinMs,
            StopWithinMs
        );

    /// <summary>
    /// A freshly built provider's own read of the machine, or null when the
    /// process list could not be queried at all.
    /// </summary>
    private void UpdateApplicationStatus(string name)
    {
        ApplicationConfigurationEntry? item = _session.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)
        );

        if (item is not null)
        {
            HardwareItem current =
                new ApplicationDiscoverer([.. ReadProviders<ApplicationProvider>().Providers])
                    .Discover()
                    .FirstOrDefault(status => string.Equals(status.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? UnknownStatus(name);

            item.Update(current);
            RefreshSelectorProjection();
            OnPropertyChanged(nameof(SelectedApplicationStatus));
        }
    }

    private void RefreshSelectorProjection()
    {
        ApplicationConfigurationEntry[] applications = [.. _session.Items];
        _session.Load(applications, keep: _session.SelectedKey);
        SelectedKey = _session.SelectedKey;
        OnPropertyChanged(nameof(SelectorItems));
    }

    private string? ApplicationNameProblem(string? name, string? allowedExisting = null)
    {
        string trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return "Give the application a name.";
        }

        if (
            SelectorItems.Any(item =>
                !string.Equals(item.Title, allowedExisting, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Title, trimmed, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return $"An application named '{trimmed}' already exists.";
        }

        return null;
    }

    private static string? Describe(ResourceAvailability availability) =>
        availability.State == AvailabilityState.Available ? null : availability.Explain();

    private static IReadOnlyList<string> SplitLines(string text) =>
        [.. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static IReadOnlyList<string> SplitList(string text) =>
        [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private string? ReportSuccess(string message)
    {
        StatusMessage = Workspace.Apply(message);
        return null;
    }

    private void RaiseValidation()
    {
        NotifyValidationChanged(nameof(ValidationProblem), nameof(CanSave), nameof(CanActivate));
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedApplication));
        OnPropertyChanged(nameof(SelectedApplicationStatus));
        OnPropertyChanged(nameof(HasSelectedApplication));
        OnPropertyChanged(nameof(Actions));
        RaiseValidation();
    }

    protected override void OnDirtyStateChanged()
    {
        base.OnDirtyStateChanged();
        OnPropertyChanged(nameof(CanActivate));
    }

    protected override void OnBusyStateChanged()
    {
        base.OnBusyStateChanged();
        OnPropertyChanged(nameof(CanActivate));
    }
}

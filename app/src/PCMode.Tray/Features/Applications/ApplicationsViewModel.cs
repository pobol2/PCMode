using System.Collections.ObjectModel;
using PCMode.Engine;
using PCMode.Tray.Configuration;
using PCMode.Tray.Presentation;

namespace PCMode.Tray.Features.Applications;

/// <summary>One configured application as shown in the selector.</summary>
public sealed class ApplicationListItem : ObservableObject, IConfigurationSelectorItem
{
    private bool _isRunning;

    public ApplicationListItem(
        string name,
        bool isRunning)
    {
        Name = name;
        _isRunning = isRunning;
    }

    public string Name { get; }

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    object IConfigurationSelectorItem.Key => Name;
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

    public ApplicationsViewModel(IConfigurationWorkspace workspace) : base(workspace)
    {
    }

    public ObservableCollection<ApplicationListItem> ApplicationItems { get; } = [];

    public ObservableCollection<RequirementRowViewModel> Requires { get; } = [];

    public void AddRequirement()
    {
        Requires.Add(
            new RequirementRowViewModel(
                string.Empty,
                string.Empty,
                OnRequirementsChanged));

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

            SelectedKey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanActivate));
            OnPropertyChanged(nameof(HasSelectedApplication));

            if (value is not null)
            {
                Load(value);
            }
        }
    }

    public override void SelectKey(object key) => SelectedApplication = key as string;

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

    public string ActivateActionToolTip => IsLaunchOnly
        ? "Launch this application now"
        : "Start or stop this application now without switching spaces";

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

    public override bool CanSave =>
        HasSelectedApplication &&
        HasUnsavedChanges &&
        !IsBusy &&
        ValidationProblem is null;

    public bool HasSelectedApplication => SelectedApplication is not null;

    public bool CanActivate =>
        SelectedApplication is not null &&
        !HasUnsavedChanges &&
        !IsBusy &&
        ValidationProblem is null;

    public override void Refresh()
    {
        string? keep = SelectedApplication;

        (IReadOnlyList<ApplicationProvider> providers, string? problem) = ReadProviders<ApplicationProvider>();

        if (problem is not null)
        {
            ApplicationItems.Clear();
            ClearForm();
            StatusMessage = problem;
            return;
        }

        ApplicationItems.Clear();

        foreach (ApplicationProvider provider in providers.OrderBy(provider => provider.Key, StringComparer.OrdinalIgnoreCase))
        {
            string? running = SafeRead(provider);
            ApplicationItems.Add(new ApplicationListItem(
                provider.Key,
                string.Equals(running, ApplicationProvider.Running, StringComparison.OrdinalIgnoreCase)));
        }

        StatusMessage = $"{ApplicationItems.Count} configured application(s).";

        SelectedKey = keep is not null && ApplicationItems.Any(item => item.Name == keep)
            ? keep
            : ApplicationItems.FirstOrDefault()?.Name;
        OnPropertyChanged(nameof(SelectedApplication));
        OnPropertyChanged(nameof(CanDiscard));
        OnPropertyChanged(nameof(HasSelectedApplication));

        if (SelectedApplication is not null)
        {
            Load(SelectedApplication);
        }
        else
        {
            ClearForm();
        }
    }

    public async Task RefreshRunningStatesAsync()
    {
        if (IsBusy || ApplicationItems.Count == 0)
        {
            return;
        }

        Dictionary<string, string?>? running = await Task.Run(CaptureRunningStates).ConfigureAwait(true);

        if (running is null || IsBusy)
        {
            return;
        }

        foreach (ApplicationListItem item in ApplicationItems)
        {
            if (running.TryGetValue(item.Name, out string? state))
            {
                item.IsRunning = string.Equals(state, ApplicationProvider.Running, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private Dictionary<string, string?>? CaptureRunningStates()
    {
        (IReadOnlyList<ApplicationProvider> providers, string? problem) = ReadProviders<ApplicationProvider>();

        if (problem is not null)
        {
            return null;
        }

        var running = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (ApplicationProvider provider in providers)
        {
            running[provider.Key] = SafeRead(provider);
        }

        return running;
    }

    public string? CreateApplication(string name, bool launchOnly, string processName, LaunchCommandEdit start)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Give the application a name.";
        }

        string trimmed = name.Trim();

        var edit = new ApplicationEdit(
            trimmed,
            launchOnly ? ApplicationObservationKind.None : ApplicationObservationKind.Process,
            processName, start, Stop: null, Reactivate: false,
            Requires: [], RestartWhen: [], Ready: null, DefaultStartWithinMs, DefaultStopWithinMs);

        return Persist(previousName: null, edit, trimmed, $"'{trimmed}' created.");
    }

    /// <summary>Creates a full copy of <paramref name="sourceName"/> under a new name.</summary>
    public string? DuplicateApplication(string name, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Give the application a name.";
        }

        string trimmed = name.Trim();

        (IReadOnlyList<ApplicationProvider> providers, string? problem) = ReadProviders<ApplicationProvider>();

        if (problem is not null)
        {
            return problem;
        }

        ApplicationProvider? source = providers.FirstOrDefault(provider =>
            string.Equals(provider.Key, sourceName, StringComparison.OrdinalIgnoreCase));

        if (source is null)
        {
            return $"'{sourceName}' is not a configured application.";
        }

        ApplicationEdit edit = ToEdit(trimmed, source.Definition);

        return Persist(previousName: null, edit, trimmed, $"'{trimmed}' created from '{sourceName}'.");
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

        return Persist(SelectedApplication, BuildEdit(SelectedApplication), SelectedApplication, $"'{SelectedApplication}' saved.");
    }


    public async Task<string?> ActivateSelectedAsync(CancellationToken cancellationToken = default)
    {
        ClearError();
        if (SelectedApplication is null)
        {
            return SetError("Choose an application first.");
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
            string.Equals(candidate.Key, name, StringComparison.OrdinalIgnoreCase));

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
            UpdateRunningBadge(name, current);
            return SetError(StatusMessage);
        }

        if (string.Equals(target, ApplicationProvider.Running, StringComparison.OrdinalIgnoreCase))
        {
            ResourceAvailability availability = provider.AvailabilityOf(ApplicationProvider.Running);

            if (availability.State == AvailabilityState.Unavailable)
            {
                StatusMessage = availability.Explain();
                AvailabilityMessage = Describe(availability);
                return SetError(StatusMessage);
            }
        }

        bool starting = string.Equals(target, ApplicationProvider.Running, StringComparison.OrdinalIgnoreCase);
        IsBusy = true;
        StatusMessage = !provider.Definition.CanObserve
            ? $"Launching '{name}'..."
            : starting ? $"Starting '{name}'..." : $"Stopping '{name}'...";

        try
        {
            await Task.Run(
                () => provider.Apply(target, cancellationToken),
                cancellationToken).ConfigureAwait(true);

            string? observed = SafeRead(provider);
            UpdateRunningBadge(name, observed);
            AvailabilityMessage = Describe(provider.AvailabilityOf(ApplicationProvider.Running));

            if (!provider.Definition.CanObserve)
            {
                StatusMessage = $"Launch requested for '{name}'. Runtime is not monitored.";
            }
            else
            {
                bool reachedTarget = string.Equals(observed, target, StringComparison.OrdinalIgnoreCase);
                StatusMessage = reachedTarget
                    ? starting ? $"'{name}' started." : $"'{name}' stopped."
                    : starting ? $"Start requested for '{name}'." : $"Stop requested for '{name}'.";
            }

            Workspace.Note(Category, StatusMessage);
            return null;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"Activation of '{name}' was cancelled.";
            return SetError(StatusMessage);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not {(starting ? "start" : "stop")} '{name}'. {exception.Message}";
            Workspace.Fault(Category, $"Could not {(starting ? "start" : "stop")} '{name}'.", exception);
            return SetError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
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

        if (string.IsNullOrWhiteSpace(newName))
        {
            return "Give the application a name.";
        }

        string trimmed = newName.Trim();
        string oldName = SelectedApplication;

        if (ValidationProblem is string problem)
        {
            StatusMessage = problem;
            return problem;
        }

        return Persist(oldName, BuildEdit(trimmed), trimmed, $"'{oldName}' renamed to '{trimmed}'.");
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

    protected override void DiscardChangesCore() => Refresh();

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

            Refresh();
            SelectedApplication = selectAfter;
            return ReportSuccess(message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Load(string name)
    {
        (IReadOnlyList<ApplicationProvider> providers, string? problem) = ReadProviders<ApplicationProvider>();

        if (problem is not null)
        {
            StatusMessage = problem;
            return;
        }

        ApplicationProvider? found = providers.FirstOrDefault(provider =>
            string.Equals(provider.Key, name, StringComparison.OrdinalIgnoreCase));

        if (found is null)
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
            StartArgumentsText =
                string.Join(Environment.NewLine, definition.Start.Arguments);
            StartUri = definition.Start.Uri ?? string.Empty;

            HasStop = definition.Stop is not null;
            IsStopUri = definition.Stop?.Uri is not null;
            StopPath = definition.Stop?.Executable ?? string.Empty;
            StopArgumentsText =
                definition.Stop is null
                    ? string.Empty
                    : string.Join(
                        Environment.NewLine,
                        definition.Stop.Arguments);
            StopUri = definition.Stop?.Uri ?? string.Empty;

            Reactivate = definition.Reactivate;

            Requires.Clear();

            foreach (ResourceRequirement requirement in definition.Requires)
            {
                Requires.Add(
                    new RequirementRowViewModel(
                        requirement.Key,
                        requirement.Target,
                        OnRequirementsChanged));
            }

            RestartWhenText =
                string.Join(", ", definition.RestartWhen);

            ReadyEnabled = definition.Ready is not null;
            ReadyActivate = definition.Ready?.Activate ?? false;
            ReadyKeys = definition.Ready?.Keys ?? string.Empty;
            ReadyWithinMs =
                (int)(
                    definition.Ready?.Within.TotalMilliseconds
                    ?? DefaultReadyWithinMs);

            StartWithinMs =
                (int)definition.StartWithin.TotalMilliseconds;

            StopWithinMs =
                (int)definition.StopWithin.TotalMilliseconds;
        });

        ClearDirtyState();

        string? running = SafeRead(found);
        UpdateRunningBadge(name, running);
        AvailabilityMessage = Describe(found.AvailabilityOf(ApplicationProvider.Running));

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

    private ApplicationEdit BuildEdit(string name) => new(
        name,
        IsLaunchOnly ? ApplicationObservationKind.None : ApplicationObservationKind.Process,
        ProcessName.Trim(),
        new LaunchCommandEdit(
            IsStartUri ? null : StartPath.Trim(),
            IsStartUri ? [] : SplitLines(StartArgumentsText),
            IsStartUri ? StartUri.Trim() : null),
        HasStop
            ? new LaunchCommandEdit(
                IsStopUri ? null : StopPath.Trim(),
                IsStopUri ? [] : SplitLines(StopArgumentsText),
                IsStopUri ? StopUri.Trim() : null)
            : null,
        Reactivate,
        [.. Requires.Select(row => new ApplicationRequirementEdit(row.ResourceKey.Trim(), row.Value.Trim()))],
        SplitList(RestartWhenText),
        ReadyEnabled ? new ReadinessEdit(ReadyActivate, string.IsNullOrEmpty(ReadyKeys) ? null : ReadyKeys, ReadyWithinMs) : null,
        StartWithinMs,
        StopWithinMs);

    private static ApplicationEdit ToEdit(string name, ApplicationDefinition definition) => new(
        name,
        definition.Observation.Kind,
        definition.ProcessName,
        new LaunchCommandEdit(definition.Start.Executable, definition.Start.Arguments, definition.Start.Uri),
        definition.Stop is null
            ? null
            : new LaunchCommandEdit(definition.Stop.Executable, definition.Stop.Arguments, definition.Stop.Uri),
        definition.Reactivate,
        [.. definition.Requires.Select(requirement => new ApplicationRequirementEdit(requirement.Key, requirement.Target))],
        definition.RestartWhen,
        definition.Ready is null
            ? null
            : new ReadinessEdit(definition.Ready.Activate, definition.Ready.Keys, (int)definition.Ready.Within.TotalMilliseconds),
        (int)definition.StartWithin.TotalMilliseconds,
        (int)definition.StopWithin.TotalMilliseconds);

    /// <summary>
    /// A freshly built provider's own read of the machine, or null when the
    /// process list could not be queried at all.
    /// </summary>
    private void UpdateRunningBadge(string name, string? state)
    {
        ApplicationListItem? item = ApplicationItems.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

        if (item is not null)
        {
            item.IsRunning = string.Equals(state, ApplicationProvider.Running, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? Describe(ResourceAvailability availability) =>
        availability.State == AvailabilityState.Available
            ? null
            : availability.Explain();

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
        NotifyValidationChanged(
            nameof(ValidationProblem),
            nameof(CanSave),
            nameof(CanActivate));
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

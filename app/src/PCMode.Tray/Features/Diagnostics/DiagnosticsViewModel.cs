using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using PCMode.Engine;
using PCMode.Tray.Configuration;
using PCMode.Tray.Diagnostics;
using PCMode.Tray.Presentation;

namespace PCMode.Tray.Features.Diagnostics;

/// <summary>
/// A support/status snapshot: dry-run a target, see the current target and
/// its last-known health, the loaded providers, where PCMode's files live,
/// and a tail of the log.
/// </summary>
/// <remarks>
/// SNAPSHOT, NOT A SUBSCRIPTION
///
/// Like <see cref="ModeHealth"/> itself says: every section here reflects
/// the moment it was last read, refreshed on every visit rather than kept
/// live by a timer. Target has no shell-wide status view model to mirror,
/// and does not need one -- <see cref="ModeEngine"/> already exposes
/// everything this page reads directly.
///
/// A DRY RUN IS NEVER AUTOMATIC
///
/// <see cref="ModeEngine"/> runs one transition at a time, and a new request
/// supersedes and cancels whatever is already running. Refreshing this page
/// must never be able to cancel a real transition happening elsewhere, so
/// the dry run only ever runs from an explicit click, and only when the
/// engine is idle.
/// </remarks>
public sealed class DiagnosticsViewModel : ObservableObject
{
    private const string LogCategory = "Diagnostics";

    private readonly IConfigurationWorkspace _workspace;
    private readonly EngineRuntime? _runtime;
    private readonly LocalLog? _log;

    private string? _currentTargetKey;
    private bool _isEngineBusy;
    private string? _selectedDryRunTargetKey;
    private ModeHealthRow? _lastHealth;
    private string? _statusMessage;
    private string? _recentLogText;

    public DiagnosticsViewModel(IConfigurationWorkspace workspace, EngineRuntime? runtime, LocalLog? log)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        _workspace = workspace;
        _runtime = runtime;
        _log = log;

        ApplicationVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unknown";

        ConfigurationPath = PCModePaths.ConfigurationFile(workspace.Root ?? PCModePaths.DefaultRoot);
        StatePath = PCModePaths.StateFile(workspace.Root ?? PCModePaths.DefaultRoot);
        LogFolder = PCModePaths.LogFolder(workspace.Root ?? PCModePaths.DefaultRoot);
    }

    public string ApplicationVersion { get; }

    public string? DataRoot => _workspace.Root;

    public string ConfigurationPath { get; }

    public string StatePath { get; }

    public string LogFolder { get; }

    public ObservableCollection<DryRunTargetRow> Targets { get; } = [];

    public ObservableCollection<ProviderRow> Providers { get; } = [];

    public string? RecentLogText
    {
        get => _recentLogText;
        private set => SetProperty(ref _recentLogText, value);
    }

    public string? CurrentTargetKey
    {
        get => _currentTargetKey;
        private set => SetProperty(ref _currentTargetKey, value);
    }

    public bool IsEngineBusy
    {
        get => _isEngineBusy;
        private set
        {
            if (SetProperty(ref _isEngineBusy, value))
            {
                OnPropertyChanged(nameof(CanDryRun));
                OnPropertyChanged(nameof(EngineStatusText));
            }
        }
    }

    public string EngineStatusText => IsEngineBusy ? "Busy" : "Idle";

    public string? SelectedDryRunTargetKey
    {
        get => _selectedDryRunTargetKey;
        set
        {
            if (SetProperty(ref _selectedDryRunTargetKey, value))
            {
                OnPropertyChanged(nameof(CanDryRun));
            }
        }
    }

    public bool CanDryRun => _runtime is not null && !IsEngineBusy && SelectedDryRunTargetKey is not null;

    public ModeHealthRow? LastHealth
    {
        get => _lastHealth;
        private set
        {
            if (SetProperty(ref _lastHealth, value))
            {
                OnPropertyChanged(nameof(HasHealth));
                OnPropertyChanged(nameof(ShowEmptyHealthMessage));
            }
        }
    }

    public bool HasHealth => LastHealth is not null;

    public bool ShowEmptyHealthMessage => !HasHealth;

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public Task RefreshAsync()
    {
        if (_runtime is not null)
        {
            ModeEngine engine = _runtime.Engine;

            CurrentTargetKey = engine.ReadCurrentTargetKey(out _);
            IsEngineBusy = engine.IsBusy;

            string? keep = SelectedDryRunTargetKey;
            Targets.Clear();

            foreach (ConfiguredTarget target in engine.Targets.Where(static target => target.Enabled))
            {
                Targets.Add(new DryRunTargetRow(target.Key, target.DisplayPath));
            }

            SelectedDryRunTargetKey = keep is not null && Targets.Any(target => target.Key == keep)
                ? keep
                : Targets.FirstOrDefault()?.Key;
        }

        Providers.Clear();

        foreach (IModeResourceProvider provider in _workspace.ReadProviders())
        {
            Providers.Add(new ProviderRow(provider.DisplayName, provider.Key));
        }

        IReadOnlyList<string> lines = _log?.ReadRecentLines(80) ?? [];
        RecentLogText = lines.Count == 0
            ? "No log entries yet."
            : string.Join(Environment.NewLine, lines);

        return Task.CompletedTask;
    }

    /// <summary>Applies the selected target as a dry run, changing nothing on the machine.</summary>
    public async Task DryRunAsync()
    {
        if (_runtime is null || SelectedDryRunTargetKey is not { } key)
        {
            return;
        }

        if (IsEngineBusy)
        {
            StatusMessage = "PCMode is busy with another transition. Try again once it finishes.";
            return;
        }

        StatusMessage = "Running dry run...";

        try
        {
            TransitionOutcome outcome = await _runtime.Engine.ApplyAsync(key, dryRun: true).ConfigureAwait(true);
            LastHealth = new ModeHealthRow(ModeHealth.From(outcome, DateTimeOffset.UtcNow));
            StatusMessage = outcome.Message;

            if (outcome.Succeeded)
            {
                _workspace.Note(LogCategory, $"Dry run of '{key}' succeeded. {outcome.Message}");
            }
            else
            {
                _workspace.Warn(LogCategory, $"Dry run of '{key}': {outcome.Message}");
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"Dry run failed. {exception.Message}";
            _workspace.Fault(LogCategory, $"Dry run of '{key}' failed.", exception);
        }
        finally
        {
            IsEngineBusy = _runtime.Engine.IsBusy;
        }
    }

    /// <summary>Opens the folder holding PCMode's log files.</summary>
    public void OpenLogFolder()
    {
        if (_log is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_log.Folder);
            ShellFolderOpener.Open(_log.Folder);
            _workspace.Note(LogCategory, "Opened the log folder.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            StatusMessage = $"Could not open the log folder. {exception.Message}";
            _workspace.Fault(LogCategory, "Could not open the log folder.", exception);
        }
    }
}

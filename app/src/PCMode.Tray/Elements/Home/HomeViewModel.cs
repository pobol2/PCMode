using System.Collections.ObjectModel;
using PCMode.Engine;
using PCMode.Tray.Elements;
using PCMode.Tray.Elements.Settings;

namespace PCMode.Tray.Elements.Home;

/// <summary>
/// The primary mode-switching surface: favorites first, then every enabled
/// target grouped by Space, each a clickable card.
/// </summary>
/// <remarks>
/// Target has no shell-wide status view model to read from (see
/// <c>ARCHITECTURE.md</c>'s "Permanently excluded" -- legacy's
/// <c>RuntimeStateMonitor</c>/<c>ShellViewModel</c> was deliberately not
/// ported), so this reads <see cref="ModeEngine"/> directly on each
/// activation and after every apply, the same one-shot-snapshot pattern
/// <c>DiagnosticsViewModel</c> already uses for its own status section --
/// rather than the live Switching/Result/health tracking legacy's Home
/// carried through <c>ShellViewModel</c>. A target card only ever shows
/// whether it is the current target, not an in-flight or failed transition;
/// there is no Retry/Repair here for the same reason.
/// </remarks>
public sealed class HomeViewModel : ObservableObject, IPageLifecycle
{
    private readonly EngineRuntime? _runtime;
    private readonly ApplicationSettingsStore? _settingsStore;
    private readonly Dictionary<string, bool> _spaceExpansion = new(StringComparer.OrdinalIgnoreCase);

    private string? _currentTargetKey;
    private bool _isBusy;
    private string? _statusMessage;

    private static readonly TimeSpan LiveStatusInterval = TimeSpan.FromSeconds(3);

    private readonly LiveRefreshTimer _liveRefresh;

    public HomeViewModel(EngineRuntime? runtime, ApplicationSettingsStore? settingsStore = null)
    {
        _liveRefresh = new LiveRefreshTimer(LiveStatusInterval, () => { Refresh(); return Task.CompletedTask; });

        _runtime = runtime;
        _settingsStore = settingsStore;
    }

    public bool HasRuntime => _runtime is not null;

    /// <summary>Favorites first, followed by every enabled target grouped by Space.</summary>
    public ObservableCollection<HomeTargetGroup> TargetGroups { get; } = [];

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
            }
        }
    }

    /// <summary>Whether a card is allowed to start a new switch right now.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>The outcome message from the most recent switch, if any.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Re-reads the current target and rebuilds the card groups from the live engine.</summary>
    /// <summary>
    /// Home shows live target state, so it reloads on every visit and keeps
    /// polling while it is the page on screen.
    /// </summary>
    public void OnActivated()
    {
        Refresh();
        _liveRefresh.Start();
    }

    public void OnDeactivated() => _liveRefresh.Stop();

    public void Refresh()
    {
        TargetGroups.Clear();

        if (_runtime is null)
        {
            return;
        }

        ModeEngine engine = _runtime.Engine;
        _currentTargetKey = engine.ReadCurrentTargetKey(out _);
        IsBusy = engine.IsBusy;

        IReadOnlyList<string> favoriteKeys = _settingsStore is null
            ? []
            : NormalizeFavoriteKeys(_settingsStore.Load(engine.Catalog).Favorites.All.Select(favorite => favorite.ModeName));

        RebuildTargetGroups(engine, favoriteKeys);
    }

    /// <summary>Applies a target, then refreshes so the current-target card and busy state catch up.</summary>
    public async Task ApplyAsync(string targetKey)
    {
        if (_runtime is null || IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            TransitionOutcome outcome = await _runtime.Engine.ApplyAsync(targetKey).ConfigureAwait(true);
            StatusMessage = outcome.Message;
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not switch targets. {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private void RebuildTargetGroups(ModeEngine engine, IReadOnlyList<string> favoriteKeys)
    {
        ConfiguredTarget[] enabled = [.. engine.Targets.Where(target => target.Enabled)];
        Dictionary<string, ConfiguredTarget> byKey = enabled.ToDictionary(
            target => target.Key,
            StringComparer.OrdinalIgnoreCase);
        var shownFavorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<ConfiguredTarget> favorites = [];

        foreach (string key in favoriteKeys)
        {
            if (byKey.TryGetValue(key, out ConfiguredTarget? target) && shownFavorites.Add(target.Key))
            {
                favorites.Add(target);
            }
        }

        if (favorites.Count > 0)
        {
            bool expanded = !_spaceExpansion.TryGetValue("favorites", out bool saved) || saved;
            var favoritesGroup = new HomeTargetGroup(
                "favorites",
                "Favorites",
                BuildCards(favorites, favoritesGroup: true),
                isFavorite: true,
                isExpanded: expanded);
            favoritesGroup.PropertyChanged += OnTargetGroupPropertyChanged;
            TargetGroups.Add(favoritesGroup);
        }

        foreach (IGrouping<string, ConfiguredTarget> space in enabled
            .GroupBy(target => target.Identity.SpaceId, StringComparer.OrdinalIgnoreCase))
        {
            ConfiguredTarget[] targets = [.. space];
            ConfiguredTarget first = targets[0];
            var group = new HomeTargetGroup(
                first.Identity.SpaceId,
                first.SpaceName ?? first.Identity.SpaceId,
                BuildCards(targets, favoritesGroup: false),
                isFavorite: false,
                isExpanded: _spaceExpansion.TryGetValue(first.Identity.SpaceId, out bool spaceExpanded) && spaceExpanded);
            group.PropertyChanged += OnTargetGroupPropertyChanged;
            TargetGroups.Add(group);
        }
    }

    private void OnTargetGroupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeTargetGroup.IsExpanded) &&
            sender is HomeTargetGroup group)
        {
            _spaceExpansion[group.Key] = group.IsExpanded;
        }
    }

    /// <summary>
    /// Uses no context for a unique name. For collisions, picks the shortest
    /// hierarchy slice that makes every card in that collision unambiguous.
    /// </summary>
    private IReadOnlyList<HomeTargetCard> BuildCards(
        IReadOnlyList<ConfiguredTarget> targets,
        bool favoritesGroup)
    {
        var contexts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, ConfiguredTarget> collision in targets.GroupBy(
            target => target.DisplayName,
            StringComparer.OrdinalIgnoreCase))
        {
            ConfiguredTarget[] sameName = [.. collision];

            if (sameName.Length == 1)
            {
                contexts[sameName[0].Key] = null;
                continue;
            }

            Func<ConfiguredTarget, string>[] candidates = favoritesGroup
                ? [SpaceContext, ActivityContext, SpaceAndActivityContext]
                : [ActivityContext];
            Func<ConfiguredTarget, string>? chosen = candidates.FirstOrDefault(candidate =>
                IsUnique(sameName.Select(candidate)));

            foreach (ConfiguredTarget target in sameName)
            {
                contexts[target.Key] = chosen is null
                    ? IdentityContext(target, favoritesGroup)
                    : chosen(target);
            }
        }

        return [.. targets.Select(target => new HomeTargetCard(
            target,
            contexts[target.Key],
            IsCurrent: string.Equals(target.Key, _currentTargetKey, StringComparison.OrdinalIgnoreCase)))];

        static bool IsUnique(IEnumerable<string> values)
        {
            string[] all = [.. values];
            return all.All(value => !string.IsNullOrWhiteSpace(value)) &&
                all.Distinct(StringComparer.OrdinalIgnoreCase).Count() == all.Length;
        }

        static string SpaceContext(ConfiguredTarget target) =>
            target.SpaceName ?? target.Identity.SpaceId;

        static string ActivityContext(ConfiguredTarget target) =>
            target.SourceName is null
                ? "Activity"
                : target.ActivityName ?? target.Identity.ActivityId;

        static string SpaceAndActivityContext(ConfiguredTarget target) =>
            target.SourceName is null
                ? $"{SpaceContext(target)} â€º Activity"
                : $"{SpaceContext(target)} â€º {ActivityContext(target)}";

        static string IdentityContext(ConfiguredTarget target, bool includeSpace)
        {
            string leaf = target.SourceName is null
                ? $"Activity {target.Identity.ActivityId}"
                : $"{ActivityContext(target)} â€º Source {target.Identity.SourceId}";

            return includeSpace ? $"{SpaceContext(target)} â€º {leaf}" : leaf;
        }
    }

    private static IReadOnlyList<string> NormalizeFavoriteKeys(IEnumerable<string> targetKeys) =>
        [.. targetKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
}

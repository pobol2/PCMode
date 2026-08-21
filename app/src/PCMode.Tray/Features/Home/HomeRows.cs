using System.ComponentModel;
using PCMode.Engine;

namespace PCMode.Tray.Features.Home;

/// <summary>A target as one card on Home, with its already-resolved disambiguating context.</summary>
public sealed record HomeTargetCard(ConfiguredTarget Target, string? Context, bool IsCurrent)
{
    public string Key => Target.Key;

    public string Name => Target.DisplayName;

    public string DisplayPath => Target.DisplayPath;

    public bool Enabled => Target.Enabled;
}

/// <summary>
/// One section of target cards on Home: Favorites, or one Space.
/// </summary>
/// <remarks>
/// Raises <see cref="IsExpanded"/> changes so <see cref="HomeViewModel"/> can
/// remember which sections were open across the rebuild every
/// <see cref="HomeViewModel.Refresh"/> does -- expansion is view state, not
/// something the engine has any notion of.
/// </remarks>
public sealed class HomeTargetGroup(
    string key,
    string name,
    IReadOnlyList<HomeTargetCard> targets,
    bool isFavorite,
    bool isExpanded) : INotifyPropertyChanged
{
    private bool _isExpanded = isExpanded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; } = key;

    public string DisplayName { get; } = isFavorite ? "Favorites" : name;

    public IReadOnlyList<HomeTargetCard> Targets { get; } = targets;

    public bool IsFavorite { get; } = isFavorite;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }
}

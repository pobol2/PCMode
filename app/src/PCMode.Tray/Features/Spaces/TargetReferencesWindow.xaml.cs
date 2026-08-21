using System.Windows;
using PCMode.Engine;

namespace PCMode.Tray.Features.Spaces;

/// <summary>One picker direction's replacement choice.</summary>
public sealed class FavoriteReplacementRow(
    string label, string direction, IReadOnlyList<ConfiguredTarget> choices)
{
    public string Label { get; } = label;

    public string Direction { get; } = direction;

    public IReadOnlyList<ConfiguredTarget> Choices { get; } = choices;

    /// <summary>The chosen target's key: a wedge points at an identity, not a name.</summary>
    public string Selected { get; set; } = choices.Count > 0 ? choices[0].Key : string.Empty;
}

/// <summary>
/// Asks for an explicit replacement for every picker favorite and startup
/// target that still names something about to be deleted. Nothing here
/// chooses one on its own -- every combo box starts on a real other target
/// or, for startup, on turning the behaviour off, and the caller reads back
/// exactly what was picked.
/// </summary>
public partial class TargetReferencesWindow : Window
{
    private const string TurnOffStartup = "(Turn off automatic startup)";

    private readonly bool _hasStartup;

    public TargetReferencesWindow(
        string what,
        IReadOnlyList<string> favoriteDirections,
        bool isStartupTarget,
        IReadOnlyList<ConfiguredTarget> alternatives)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(favoriteDirections);
        ArgumentNullException.ThrowIfNull(alternatives);

        InitializeComponent();

        _hasStartup = isStartupTarget;

        Prompt.Text = $"'{what}' is still assigned to the picker or startup. " +
            "Choose what should take its place before it can be deleted.";

        FavoritesList.ItemsSource = favoriteDirections
            .Select(direction => new FavoriteReplacementRow($"{direction} favorite", direction, alternatives))
            .ToList();

        if (isStartupTarget)
        {
            StartupBox.DisplayMemberPath = "DisplayPath";
            StartupBox.ItemsSource = new object[] { TurnOffStartup }.Concat(alternatives).ToList();
            StartupBox.SelectedIndex = 0;
        }
        else
        {
            StartupRow.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Direction name to replacement target key, for every favorite that needed one.</summary>
    public IReadOnlyDictionary<string, string> FavoriteReplacements { get; private set; } =
        new Dictionary<string, string>();

    /// <summary>The chosen startup target's key, or null to turn startup switching off.</summary>
    public string? StartupReplacement { get; private set; }

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.ItemsSource is IEnumerable<FavoriteReplacementRow> rows)
        {
            var replacements = new Dictionary<string, string>();

            foreach (FavoriteReplacementRow row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Selected))
                {
                    ErrorText.Text = $"Choose a replacement for the {row.Direction} favorite.";
                    ErrorText.Visibility = Visibility.Visible;
                    return;
                }

                replacements[row.Direction] = row.Selected;
            }

            FavoriteReplacements = replacements;
        }

        if (_hasStartup)
        {
            StartupReplacement = StartupBox.SelectedItem is ConfiguredTarget target ? target.Key : null;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}

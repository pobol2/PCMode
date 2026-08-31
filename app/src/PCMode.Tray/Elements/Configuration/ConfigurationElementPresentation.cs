using System.Collections;
using System.Windows.Input;

namespace PCMode.Tray.Elements.Configuration;

/// <summary>
/// Element-owned parameters for the generic configuration branch of the UI tree.
/// These models name no WPF control: the Elements resource dictionary is the
/// single place that maps them to the legal Functional Blocks.
/// </summary>
public sealed record ConfigurationPagePresentation(
    string Title,
    ConfigurationSelectorPresentation Selector,
    object Editor);

public sealed record ConfigurationSelectorPresentation(
    string Title,
    IEnumerable Items,
    ICommand? CreateCommand = null,
    bool ShowCreateButton = true,
    ICommand? SelectCommand = null);

public sealed record ConfigurationEditorPresentation(
    IReadOnlyList<EditorSectionPresentation> EssentialSections,
    IReadOnlyList<EditorSectionPresentation> AdvancedSections,
    IReadOnlyList<EditorSectionPresentation> ExtraSections,
    IReadOnlyList<EditorSectionPresentation> HardwareSections)
{
    public static ConfigurationEditorPresentation Create(
        IEnumerable<EditorSectionPresentation>? essential = null,
        IEnumerable<EditorSectionPresentation>? advanced = null,
        IEnumerable<EditorSectionPresentation>? extra = null,
        IEnumerable<EditorSectionPresentation>? hardware = null) =>
        new(
            [.. essential ?? []],
            [.. advanced ?? []],
            [.. extra ?? []],
            [.. hardware ?? []]);
}

/// <summary>
/// Parameters for one legal EditorSection branch. Content remains deliberately
/// opaque during migration, below the Tree's explicit FinalUiComponent boundary.
/// </summary>
public sealed record EditorSectionPresentation(
    string Title,
    TerminalElementContent Content,
    bool IsCollapsible = false,
    bool IsExpanded = true,
    bool ShowChrome = true);

/// <summary>
/// A temporary terminal leaf. <see cref="Source"/> keeps the owning Element
/// available to the future component without letting it define structure now.
/// </summary>
public sealed record TerminalElementContent(string Description, object Source);

public sealed record ConfigurationMigrationEditorPresentation(
    string Description,
    object Source);

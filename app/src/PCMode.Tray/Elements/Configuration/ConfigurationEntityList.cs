using System.Collections.ObjectModel;
using PCMode.Tray.Elements;

namespace PCMode.Tray.Elements.Configuration;

/// <summary>One entity's selector-list projection, built by <see cref="ConfigurationEntityList{TKey, TEntity}"/>.</summary>
public sealed class ConfigurationEntityListItem<TKey> : IConfigurationSelectorItem
    where TKey : notnull
{
    public ConfigurationEntityListItem(TKey key, string title, string? summary)
    {
        Key = key;
        Title = title;
        Summary = summary;
    }

    object IConfigurationSelectorItem.Key => Key;

    public TKey Key { get; }

    public string Title { get; }

    public string? Summary { get; }
}

/// <summary>
/// The selector-backed entity collection a flat-list configuration page
/// (one named entity picked from a pool -- a layout, a target, a scene) owns:
/// the entities themselves, their selector projection, and which key is
/// selected -- restoring the previous selection on <see cref="Load"/> when it
/// still exists, else falling back to the first entity. This is the "keep the
/// selected key, rebuild the list, restore-or-fall-back" skeleton every flat
/// configuration page's own <c>Refresh()</c> otherwise repeats by hand.
/// </summary>
/// <remarks>
/// Deliberately has no <c>Selected</c>-changed callback: the page calls
/// <see cref="Load"/> or <see cref="Select"/> and then reads <see cref="Selected"/>
/// itself to load its own editor fields, the same explicit sequencing
/// <c>ConfigurationPageViewModelBase.SelectKey</c> already uses. A tree- or
/// document-shaped selector (Spaces) does not fit this shape and keeps its own.
/// </remarks>
public sealed class ConfigurationEntityList<TKey, TEntity>
    where TKey : notnull
{
    private readonly Func<TEntity, TKey> _keyOf;
    private readonly Func<TEntity, string> _titleOf;
    private readonly Func<TEntity, string?>? _summaryOf;

    public ConfigurationEntityList(
        Func<TEntity, TKey> keyOf,
        Func<TEntity, string> titleOf,
        Func<TEntity, string?>? summaryOf = null
    )
    {
        ArgumentNullException.ThrowIfNull(keyOf);
        ArgumentNullException.ThrowIfNull(titleOf);

        _keyOf = keyOf;
        _titleOf = titleOf;
        _summaryOf = summaryOf;
    }

    public ObservableCollection<TEntity> Items { get; } = [];

    public ObservableCollection<ConfigurationEntityListItem<TKey>> SelectorItems { get; } = [];

    public TKey? SelectedKey { get; private set; }

    public bool HasSelection => SelectedKey is not null;

    public TEntity? Selected =>
        SelectedKey is null ? default : Items.FirstOrDefault(entity => Equals(_keyOf(entity), SelectedKey));

    /// <summary>
    /// Rebuilds <see cref="Items"/> and <see cref="SelectorItems"/> from fresh
    /// data. <paramref name="keep"/> stays selected when it is still present;
    /// otherwise the first entity is selected, or nothing when the list is
    /// empty.
    /// </summary>
    public void Load(IEnumerable<TEntity> entities, TKey? keep)
    {
        ArgumentNullException.ThrowIfNull(entities);

        Items.Clear();
        SelectorItems.Clear();

        foreach (TEntity entity in entities)
        {
            Items.Add(entity);
            SelectorItems.Add(
                new ConfigurationEntityListItem<TKey>(_keyOf(entity), _titleOf(entity), _summaryOf?.Invoke(entity))
            );
        }

        SelectedKey =
            keep is not null && Items.Any(entity => Equals(_keyOf(entity), keep)) ? keep
            : Items.Count > 0 ? _keyOf(Items[0])
            : default;
    }

    public void Select(TKey key) => SelectedKey = key;

    public void Clear()
    {
        Items.Clear();
        SelectorItems.Clear();
        SelectedKey = default;
    }
}

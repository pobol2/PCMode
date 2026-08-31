using System.Collections.ObjectModel;
using PCMode.Tray.Elements;

namespace PCMode.Tray.Elements.Configuration;

/// <summary>
/// The general "pick something from a pool, edit a working copy, persist only on
/// Save" lifecycle a flat-list configuration page's entity management follows once
/// Create/Duplicate/Rename must not touch disk until Save:
///
///  - <see cref="Load"/> fills the pool from what is actually persisted, and never
///    discards an in-progress overlay -- a background/live reload must not lose
///    unsaved work.
///  - <see cref="BeginCreate"/>/<see cref="BeginDuplicate"/> open an unsaved overlay
///    with no persisted backing, selected immediately, that exists only in
///    <see cref="SelectorItems"/> until the page's own Save writes it.
///  - <see cref="RenameCurrent"/> retitles whatever is selected -- a fresh overlay or
///    an existing persisted entity -- without writing anything; the page's own Save
///    is what actually persists a rename.
///  - <see cref="Commit"/> ends overlay mode once the page has actually written it
///    (the page then reloads from the persisted source, which now includes it).
///  - <see cref="Discard"/> abandons an overlay outright, for the page's own Discard:
///    a fresh Create/Duplicate falls back to the first remaining entity, a rename
///    falls back to the entity it was renaming.
///
/// A page's own field values (Audio's TargetName/Routes, for instance) stay the
/// page's responsibility throughout: this class owns only the pool/selection/overlay
/// bookkeeping, the same division <see cref="ConfigurationEntityList{TKey, TEntity}"/>
/// already keeps from a page's editor fields. Use that simpler class instead when a
/// page's Create/Rename/Duplicate write through immediately and there is never an
/// unsaved, not-yet-persisted entity in the selector.
/// </summary>
public sealed class ConfigurationEditingSession<TKey, TEntity>
    where TKey : notnull
{
    private readonly Func<TEntity, TKey> _keyOf;
    private readonly Func<TEntity, string> _titleOf;
    private readonly Func<TEntity, string?>? _summaryOf;

    private Overlay? _overlay;

    public ConfigurationEditingSession(
        Func<TEntity, TKey> keyOf,
        Func<TEntity, string> titleOf,
        Func<TEntity, string?>? summaryOf = null)
    {
        ArgumentNullException.ThrowIfNull(keyOf);
        ArgumentNullException.ThrowIfNull(titleOf);

        _keyOf = keyOf;
        _titleOf = titleOf;
        _summaryOf = summaryOf;
    }

    /// <summary>The persisted entities, as of the last <see cref="Load"/>. Never includes an unsaved overlay.</summary>
    public ObservableCollection<TEntity> Items { get; } = [];

    /// <summary>The selector's projection: every persisted entity, with an active overlay's key/title substituted or appended.</summary>
    public ObservableCollection<ConfigurationEntityListItem<TKey>> SelectorItems { get; } = [];

    public TKey? SelectedKey { get; private set; }

    public bool HasSelection => SelectedKey is not null;

    /// <summary>Whether the current selection is a from-scratch Create/Duplicate with no persisted backing at all yet.</summary>
    public bool IsDraft => _overlay is { HasOrigin: false };

    /// <summary>Whether the current selection -- draft or a persisted entity being renamed -- has unsaved selector-identity edits.</summary>
    public bool HasOverlay => _overlay is not null;

    /// <summary>
    /// The persisted entity behind the current selection: the entity itself when
    /// selected cleanly, the entity being renamed when a rename overlay is active, or
    /// <see langword="default"/> for a fresh Create/Duplicate draft with nothing
    /// persisted yet.
    /// </summary>
    public TEntity? Selected
    {
        get
        {
            TKey? lookupKey = _overlay is { } overlay
                ? (overlay.HasOrigin ? overlay.OriginKey : default)
                : SelectedKey;

            return lookupKey is null
                ? default
                : Items.FirstOrDefault(entity => Equals(_keyOf(entity), lookupKey));
        }
    }

    /// <summary>
    /// Rebuilds the persisted pool from fresh data. An active overlay is never
    /// discarded by this -- it stays selected regardless of <paramref name="keep"/>.
    /// Call <see cref="Discard"/> first to abandon one outright.
    /// </summary>
    public void Load(IEnumerable<TEntity> entities, TKey? keep)
    {
        ArgumentNullException.ThrowIfNull(entities);

        Items.Clear();

        foreach (TEntity entity in entities)
        {
            Items.Add(entity);
        }

        if (_overlay is { } overlay)
        {
            SelectedKey = overlay.Key;
        }
        else
        {
            SelectedKey = keep is not null && Items.Any(entity => Equals(_keyOf(entity), keep))
                ? keep
                : Items.Count > 0 ? _keyOf(Items[0]) : default;
        }

        RebuildSelectorItems();
    }

    /// <summary>Selects an existing item by key. Silently drops a stale overlay that belonged to a different key.</summary>
    public void Select(TKey key)
    {
        if (_overlay is { } overlay && !Equals(overlay.Key, key))
        {
            _overlay = null;
        }

        SelectedKey = key;
        RebuildSelectorItems();
    }

    /// <summary>Opens a new, unsaved overlay named <paramref name="title"/> under <paramref name="key"/>, selected immediately.</summary>
    public void BeginCreate(TKey key, string title)
    {
        _overlay = new Overlay(HasOrigin: false, OriginKey: default!, key, title);
        SelectedKey = key;
        RebuildSelectorItems();
    }

    /// <summary>Opens a new, unsaved overlay copied from whatever is currently selected -- same shape as <see cref="BeginCreate"/>, named for intent.</summary>
    public void BeginDuplicate(TKey key, string title) => BeginCreate(key, title);

    /// <summary>Retitles whichever entry -- an overlay already in progress, or a clean persisted selection -- is currently selected, without writing anything.</summary>
    public void RenameCurrent(TKey key, string title)
    {
        _overlay = _overlay is { } existing
            ? existing with { Key = key, Title = title }
            : new Overlay(HasOrigin: true, OriginKey: SelectedKey!, key, title);

        SelectedKey = key;
        RebuildSelectorItems();
    }

    /// <summary>Ends overlay mode once the page has actually persisted it. The page reloads afterward via <see cref="Load"/>.</summary>
    public void Commit() => _overlay = null;

    /// <summary>Abandons an unsaved overlay outright, for the page's own Discard.</summary>
    public void Discard()
    {
        if (_overlay is { HasOrigin: true } rename)
        {
            SelectedKey = rename.OriginKey;
        }
        else if (_overlay is not null)
        {
            SelectedKey = Items.Count > 0 ? _keyOf(Items[0]) : default;
        }

        _overlay = null;
        RebuildSelectorItems();
    }

    public void Clear()
    {
        Items.Clear();
        SelectorItems.Clear();
        SelectedKey = default;
        _overlay = null;
    }

    private void RebuildSelectorItems()
    {
        SelectorItems.Clear();

        foreach (TEntity entity in Items)
        {
            TKey key = _keyOf(entity);

            if (_overlay is { HasOrigin: true } rename && Equals(rename.OriginKey, key))
            {
                SelectorItems.Add(new ConfigurationEntityListItem<TKey>(rename.Key, rename.Title, "Not yet saved"));
                continue;
            }

            SelectorItems.Add(new ConfigurationEntityListItem<TKey>(key, _titleOf(entity), _summaryOf?.Invoke(entity)));
        }

        if (_overlay is { HasOrigin: false } draft)
        {
            SelectorItems.Add(new ConfigurationEntityListItem<TKey>(draft.Key, draft.Title, "Not yet saved"));
        }
    }

    private readonly record struct Overlay(bool HasOrigin, TKey OriginKey, TKey Key, string Title);
}

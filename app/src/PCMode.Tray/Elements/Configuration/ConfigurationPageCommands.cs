using System.Windows.Input;

namespace PCMode.Tray.Elements.Configuration;

/// <summary>
/// The standard Create / Rename / Duplicate / Delete / Activate / Add / Save /
/// Discard surface of a configuration page, as bindable commands.
/// </summary>
/// <remarks>
/// This is the orchestration the old page shell did in its own click handlers:
/// run the unsaved-changes guard, re-read <see cref="ConfigurationPageViewModelBase.Actions"/>
/// afterwards because saving can rebuild the selector and change what is
/// selected, then prompt for a name or a confirmation and apply. None of that
/// was ever about arrangement, so none of it belongs to a control -- a Layout
/// binds a button to a command here and owns nothing but where the button sits.
///
/// Every delegate is resolved at invocation rather than captured, for the same
/// reason the shell re-read them: an action captured for an entity that is no
/// longer selected must never run.
/// </remarks>
public sealed class ConfigurationPageCommands : ObservableObject
{
    private readonly ConfigurationPageViewModelBase _page;
    private readonly IConfigurationPrompts _prompts;

    internal ConfigurationPageCommands(
        ConfigurationPageViewModelBase page,
        IConfigurationPrompts prompts)
    {
        _page = page;
        _prompts = prompts;

        Save = new RelayCommand(() => _page.TrySaveChanges(), () => _page.CanSave);
        Discard = new RelayCommand(_page.DiscardChanges, () => _page.CanDiscard);

        Create = new RelayCommand(
            () => Prompt(
                actions => actions.Create,
                actions => ($"New {actions.EntityNoun}", $"Name the new {actions.EntityNoun}.", string.Empty),
                guard: true),
            () => Actions?.Create is not null);

        Rename = new RelayCommand(
            () => Prompt(
                actions => actions.Rename,
                actions => (
                    $"Rename {actions.EntityNoun}",
                    $"Rename '{actions.CurrentDisplayName}'.",
                    actions.CurrentDisplayName ?? string.Empty),
                guard: false),
            () => Actions?.Rename is not null);

        Duplicate = new RelayCommand(
            () => Prompt(
                actions => actions.Duplicate,
                actions => (
                    $"Duplicate {actions.EntityNoun}",
                    $"Name the copy of '{actions.CurrentDisplayName}'.",
                    $"{actions.CurrentDisplayName} copy"),
                guard: false),
            () => Actions?.Duplicate is not null);

        Delete = new RelayCommand(DeleteSelected, () => Actions?.Delete is not null);

        Activate = new AsyncRelayCommand(
            async () =>
            {
                if (Actions?.Activate is { } activate)
                {
                    await activate().ConfigureAwait(true);
                }
            },
            () => Actions is { Activate: not null, CanActivateNow: true });

        AddEssential = new RelayCommand(
            () => EssentialActions?.Add?.Invoke(),
            () => EssentialActions is { Add: not null, CanAdd: true });

        RemoveEssential = new RelayCommand<object>(
            item => EssentialActions?.Remove?.Invoke(item),
            _ => EssentialActions is { Remove: not null, CanRemove: true });
    }

    private EntityActions? Actions => _page.Actions;

    private EssentialCollectionActions? EssentialActions => _page.EssentialActions;

    public ICommand Save { get; }

    public ICommand Discard { get; }

    public ICommand Create { get; }

    public ICommand Rename { get; }

    public ICommand Duplicate { get; }

    public ICommand Delete { get; }

    public ICommand Activate { get; }

    public ICommand AddEssential { get; }

    public ICommand RemoveEssential { get; }

    /// <summary>A null action hides its control entirely rather than disabling it.</summary>
    public bool ShowCreate => Actions?.Create is not null;

    public bool ShowRename => Actions?.Rename is not null;

    public bool ShowDuplicate => Actions?.Duplicate is not null;

    public bool ShowDelete => Actions?.Delete is not null;

    public bool ShowActivate => Actions?.Activate is not null;

    public bool ShowAddEssential => EssentialActions?.Add is not null;

    /// <summary>Whether the Actions menu has anything in it at all.</summary>
    public bool ShowActions => ShowRename || ShowDuplicate || ShowDelete;

    public string ActivateLabel => Actions?.ActivateLabel ?? "Activate";

    public string AddEssentialLabel =>
        EssentialActions is { ItemNoun: { } noun } ? $"Add {noun}" : "Add";

    /// <summary>
    /// The text a page shows when nothing is selected, generated from the
    /// entity noun so no page has to write it.
    /// </summary>
    public string EmptyStateText =>
        Actions is { EntityNoun: { } noun }
            ? $"No {noun} selected. Choose one on the left, or use Create to add one."
            : "Nothing selected.";

    /// <summary>Re-reads everything after the page reports a change.</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(ShowCreate));
        OnPropertyChanged(nameof(ShowRename));
        OnPropertyChanged(nameof(ShowDuplicate));
        OnPropertyChanged(nameof(ShowDelete));
        OnPropertyChanged(nameof(ShowActivate));
        OnPropertyChanged(nameof(ShowAddEssential));
        OnPropertyChanged(nameof(ShowActions));
        OnPropertyChanged(nameof(ActivateLabel));
        OnPropertyChanged(nameof(AddEssentialLabel));
        OnPropertyChanged(nameof(EmptyStateText));

        (Save as RelayCommand)?.RaiseCanExecuteChanged();
        (Discard as RelayCommand)?.RaiseCanExecuteChanged();
        (Create as RelayCommand)?.RaiseCanExecuteChanged();
        (Rename as RelayCommand)?.RaiseCanExecuteChanged();
        (Duplicate as RelayCommand)?.RaiseCanExecuteChanged();
        (Delete as RelayCommand)?.RaiseCanExecuteChanged();
        (Activate as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AddEssential as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveEssential as RelayCommand<object>)?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// The guard, then a name, then apply -- in that order, re-reading the
    /// actions in between because the guard can save, and saving can move the
    /// selection.
    /// </summary>
    private void Prompt(
        Func<EntityActions, Func<string, string?>?> select,
        Func<EntityActions, (string Title, string Message, string Initial)> wording,
        bool guard)
    {
        if (Actions is not { } actions || select(actions) is null)
        {
            return;
        }

        if (guard)
        {
            object? selectedBefore = _page.SelectedKey;

            if (!_prompts.ConfirmDiscardIfDirty(_page) || !Equals(selectedBefore, _page.SelectedKey))
            {
                return;
            }

            if (Actions is not { } refreshed || select(refreshed) is null)
            {
                return;
            }

            actions = refreshed;
        }

        Func<string, string?> apply = select(actions)!;
        (string title, string message, string initial) = wording(actions);

        _prompts.PromptForName(title, message, initial, apply);
    }

    private void DeleteSelected()
    {
        if (Actions is not { Delete: { } delete } actions)
        {
            return;
        }

        if (_prompts.Confirm($"Delete '{actions.CurrentDisplayName}'? This cannot be undone."))
        {
            delete();
        }
    }
}

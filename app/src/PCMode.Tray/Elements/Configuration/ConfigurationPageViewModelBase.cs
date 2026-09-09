using System.Windows.Input;
using PCMode.Engine;
using PCMode.Tray.Elements;
using PCMode.Tray.Elements.Configuration;

namespace PCMode.Tray.Elements.Configuration;

/// <summary>The result of one <see cref="ConfigurationPageViewModelBase.RunActivationAsync"/> attempt.</summary>
/// <param name="Verified">Whether the activation was confirmed to have taken effect.</param>
/// <param name="Message">Shown as <see cref="ConfigurationPageViewModelBase.StatusMessage"/> either way.</param>
public sealed record ActivationOutcome(bool Verified, string Message);

public abstract class ConfigurationPageViewModelBase : ObservableObject, IUnsavedChanges
{
    private string? _statusMessage;
    private string? _errorMessage;
    private bool _hasUnsavedChanges;
    private bool _suppressDirtyTracking;
    private bool _isBusy;
    private object? _selectedKey;

    protected ConfigurationPageViewModelBase(IConfigurationWorkspace workspace, IConfigurationPrompts? prompts = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;
        Prompts = prompts ?? NoConfigurationPrompts.Instance;
        Commands = new ConfigurationPageCommands(this, Prompts);
        SelectItemCommand = new RelayCommand<object>(SelectItem);

        // Actions and EssentialActions are recomputed on every read, so any
        // change at all can have altered what the page may currently do.
        PropertyChanged += (_, _) => Commands.Refresh();
    }

    /// <summary>
    /// The parameters with which this Element selects the generic
    /// ConfigurationPage branch. Derived Elements set this once while they are
    /// composed; the Tree remains unaware of every feature type.
    /// </summary>
    public ConfigurationPagePresentation? TreePresentation { get; protected set; }

    /// <summary>The shared selector behavior; selector rows remain state-only objects.</summary>
    public ICommand SelectItemCommand { get; }

    private void SelectItem(object item)
    {
        if (item is not IConfigurationSelectorItem selectorItem || Equals(SelectedKey, selectorItem.Key))
        {
            return;
        }

        if (GuardUnsavedChanges())
        {
            SelectKey(selectorItem.Key);
        }
    }

    /// <summary>
    /// The modal interactions this page needs -- naming, confirming, and the
    /// unsaved-changes guard. Never null; unattended callers get
    /// <see cref="NoConfigurationPrompts"/>, which refuses everything.
    /// </summary>
    protected IConfigurationPrompts Prompts { get; }

    /// <summary>
    /// The standard entity and collection actions, as commands a Layout can
    /// bind a button to.
    /// </summary>
    public ConfigurationPageCommands Commands { get; }

    /// <summary>
    /// The presentation-to-engine boundary every configuration page reads
    /// providers and the configuration document through.
    /// </summary>
    protected IConfigurationWorkspace Workspace { get; }

    public bool IsBusy
    {
        get => _isBusy;
        protected set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnBusyStateChanged();
            }
        }
    }

    /// <summary>
    /// Whether the editor holds changes that have not been written to disk.
    /// Virtual so a page whose editor owns a second, narrower kind of edit --
    /// Audio's processing-profile fields, dirty independently of the target's
    /// own route wiring -- can fold that into the one flag every shared piece
    /// of unsaved-changes protection (the footer's Save/Discard buttons,
    /// <see cref="Blocks.ConfigurationPageBlock.ConfirmDiscardIfDirty"/>,
    /// a feature's own activation guard) already reads.
    /// </summary>
    public virtual bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            if (SetProperty(ref _hasUnsavedChanges, value))
            {
                OnPropertyChanged(nameof(CanDiscard));
                OnDirtyStateChanged();
            }
        }
    }

    public bool CanDiscard => HasUnsavedChanges;

    /// <summary>
    /// Runs the shared "this has unsaved changes -- save them first?" question
    /// before something that would discard an edit in progress. False means the
    /// caller must abandon what it was about to do.
    /// </summary>
    public bool GuardUnsavedChanges() => Prompts.ConfirmDiscardIfDirty(this);

    /// <summary>
    /// Runs the guard, asks for a name, and hands it to <paramref name="apply"/>,
    /// keeping the dialog open on a returned problem.
    /// </summary>
    /// <remarks>
    /// Exposed for the pages whose create/rename wording depends on what is
    /// selected, which the generated wording in
    /// <see cref="ConfigurationPageCommands"/> cannot produce.
    /// </remarks>
    public void PromptAndApply(string title, string message, string initialValue, Func<string, string?> apply)
    {
        if (GuardUnsavedChanges())
        {
            Prompts.PromptForName(title, message, initialValue, apply);
        }
    }

    /// <summary>A yes/no confirmation, for a page whose own handler needs one.</summary>
    public bool Confirm(string message) => Prompts.Confirm(message);

    /// <summary>
    /// Whether the editor's current contents can be saved. Every configuration
    /// page binds its Save button to this, so the rule -- dirty, not busy, and
    /// whatever else a feature requires -- lives with the feature that knows it.
    /// </summary>
    public abstract bool CanSave { get; }

    /// <summary>
    /// Why what is on screen could not be saved, or null when it could. Shown
    /// under the footer's Save button. Most pages have a real check here;
    /// pages that never refuse a save leave the default.
    /// </summary>
    public virtual string? ValidationProblem => null;

    /// <summary>
    /// The Create/Rename/Duplicate/Delete/Activate surface for whatever is
    /// currently selected, or null to leave those entirely to the page's
    /// custom routed-event path. Read fresh by the block on every use, so it always
    /// reflects the current selection and gating state -- a page never
    /// caches an instance across a selection change.
    /// </summary>
    public virtual EntityActions? Actions => null;

    /// <summary>
    /// The Add/Remove surface for this page's repeated "Essential settings"
    /// cards, or null when this page has no such collection.
    /// </summary>
    public virtual EssentialCollectionActions? EssentialActions => null;

    /// <summary>
    /// The key of whatever is currently loaded into the editor, or null when
    /// nothing is selected. A configuration page's selector list matches its
    /// items against this to stay in sync without page-specific code.
    /// </summary>
    public object? SelectedKey
    {
        get => _selectedKey;
        protected set
        {
            if (!Equals(_selectedKey, value))
            {
                _selectedKey = value;
                OnPropertyChanged();
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        protected set => SetProperty(ref _statusMessage, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        protected set => SetProperty(ref _errorMessage, value);
    }

    public bool TrySaveChanges()
    {
        ErrorMessage = SaveChangesCore();
        return ErrorMessage is null;
    }

    public void DiscardChanges()
    {
        ErrorMessage = null;
        DiscardChangesCore();
    }

    protected void ClearError()
    {
        ErrorMessage = null;
    }

    protected string SetError(string message)
    {
        ErrorMessage = message;
        return message;
    }

    protected abstract string? SaveChangesCore();

    protected abstract void DiscardChangesCore();

    /// <summary>
    /// Loads the item identified by <paramref name="key"/> into the editor,
    /// replacing whatever is currently loaded. Called by the shell when the
    /// user picks a different selector item and any unsaved changes have
    /// already been resolved.
    /// </summary>
    public abstract void SelectKey(object key);

    /// <summary>
    /// Loads (or reloads) this page's configuration, e.g. from disk. Called
    /// once by the shell the first time the page becomes active.
    /// </summary>
    public abstract void Refresh();

    protected bool SetDirtyProperty<T>(
        ref T field,
        T value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null
    )
    {
        bool changed = SetProperty(ref field, value, propertyName);

        if (changed && !_suppressDirtyTracking)
        {
            HasUnsavedChanges = true;
        }

        return changed;
    }

    protected void ClearDirtyState()
    {
        HasUnsavedChanges = false;
    }

    protected void MarkDirty()
    {
        if (!_suppressDirtyTracking)
        {
            HasUnsavedChanges = true;
        }
    }

    /// <summary>
    /// Whether a row's setters are currently mirroring loaded state rather
    /// than a person editing -- for a page whose fields do more on a real
    /// change than mark themselves dirty, and need to skip that extra work
    /// during the same load <see cref="RunWithoutDirtyTracking"/> already
    /// silences for <see cref="MarkDirty"/>.
    /// </summary>
    protected bool IsSuppressingDirtyTracking => _suppressDirtyTracking;

    protected void RunWithoutDirtyTracking(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        bool previous = _suppressDirtyTracking;
        _suppressDirtyTracking = true;

        try
        {
            action();
        }
        finally
        {
            _suppressDirtyTracking = previous;
        }
    }

    protected void NotifyValidationChanged(params string[] dependentProperties)
    {
        foreach (string propertyName in dependentProperties)
        {
            OnPropertyChanged(propertyName);
        }
    }

    /// <summary>
    /// The configured providers of one kind, built fresh from the file -- the
    /// same providers a real startup would build, so what a page shows and
    /// what a transition would do can never disagree. Empty with a problem
    /// message when the configuration itself could not be read.
    /// </summary>
    protected (IReadOnlyList<TProvider> Providers, string? Problem) ReadProviders<TProvider>()
        where TProvider : IModeResourceProvider => Workspace.ReadProviders<TProvider>();

    /// <summary>The single configured provider of one kind, when a page has at most one.</summary>
    protected (TProvider? Provider, string? Problem) ReadProvider<TProvider>()
        where TProvider : IModeResourceProvider => Workspace.ReadProvider<TProvider>();

    /// <summary>
    /// The parsed configuration document alongside its providers, for pages
    /// that need the document's own structure and not just its providers.
    /// </summary>
    protected (
        ModeConfiguration? Configuration,
        IReadOnlyList<IModeResourceProvider> Providers,
        string? Problem
    ) ReadConfiguration()
    {
        try
        {
            (ModeConfiguration configuration, IReadOnlyList<IModeResourceProvider> providers) = Workspace.Read();
            return (configuration, providers, null);
        }
        catch (ModeConfigurationException exception)
        {
            return (null, [], $"The configuration could not be read. {exception.Message}");
        }
    }

    /// <summary>A provider's own read of the machine, or null when it could not be read at all.</summary>
    protected static string? SafeRead(IModeResourceProvider provider)
    {
        try
        {
            return provider.Read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The guard/busy/status/log skeleton every feature's own Activate
    /// currently repeats: refuses while dirty, sets <see cref="IsBusy"/> and a
    /// provisional <see cref="StatusMessage"/>, runs <paramref name="activate"/>,
    /// and reports its <see cref="ActivationOutcome"/> through
    /// <see cref="StatusMessage"/> plus <see cref="IConfigurationWorkspace.Note"/>/
    /// <see cref="IConfigurationWorkspace.Warn"/>. A thrown exception (other
    /// than cancellation) is reported through
    /// <see cref="IConfigurationWorkspace.Fault"/> instead of propagating.
    /// </summary>
    protected async Task RunActivationAsync(
        string category,
        string activatingStatus,
        Func<CancellationToken, Task<ActivationOutcome>> activate,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(activate);

        if (HasUnsavedChanges)
        {
            SetError("Save or discard changes before activating.");
            return;
        }

        ClearError();
        IsBusy = true;
        StatusMessage = activatingStatus;

        try
        {
            ActivationOutcome outcome = await activate(cancellationToken).ConfigureAwait(true);
            StatusMessage = outcome.Message;

            if (outcome.Verified)
            {
                Workspace.Note(category, outcome.Message);
            }
            else
            {
                Workspace.Warn(category, outcome.Message);
                SetError(outcome.Message);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Activation was cancelled.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not activate. {exception.Message}";
            Workspace.Fault(category, StatusMessage, exception);
            SetError(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Notifies that <see cref="CanSave"/> may have changed. Every configuration
    /// ViewModel derives its own save eligibility from busy/dirty state, so the
    /// base case is covered here; a feature overrides to also notify whatever
    /// else -- an Activate command, most often -- depends on the same state.
    /// </summary>
    protected virtual void OnBusyStateChanged()
    {
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>See <see cref="OnBusyStateChanged"/>.</summary>
    protected virtual void OnDirtyStateChanged()
    {
        OnPropertyChanged(nameof(CanSave));
    }
}

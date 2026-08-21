using PCMode.Engine;
using PCMode.Tray.Configuration;

namespace PCMode.Tray.Presentation;

public abstract class ConfigurationPageViewModelBase : ObservableObject
{
    private string? _statusMessage;
    private string? _errorMessage;
    private bool _hasUnsavedChanges;
    private bool _suppressDirtyTracking;
    private bool _isBusy;
    private object? _selectedKey;

    protected ConfigurationPageViewModelBase(IConfigurationWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;
    }

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

    public bool HasUnsavedChanges
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
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
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
        where TProvider : IModeResourceProvider
    {
        try
        {
            return ([.. Workspace.ReadProviders().OfType<TProvider>()], null);
        }
        catch (ModeConfigurationException exception)
        {
            return ([], $"The configuration could not be read. {exception.Message}");
        }
    }

    /// <summary>The single configured provider of one kind, when a page has at most one.</summary>
    protected (TProvider? Provider, string? Problem) ReadProvider<TProvider>()
        where TProvider : IModeResourceProvider
    {
        (IReadOnlyList<TProvider> providers, string? problem) = ReadProviders<TProvider>();
        return (providers.FirstOrDefault(), problem);
    }

    /// <summary>
    /// The parsed configuration document alongside its providers, for pages
    /// that need the document's own structure and not just its providers.
    /// </summary>
    protected (ModeConfiguration? Configuration, IReadOnlyList<IModeResourceProvider> Providers, string? Problem) ReadConfiguration()
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
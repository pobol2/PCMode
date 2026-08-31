using System.Windows.Input;

namespace PCMode.Tray.Elements;

/// <summary>
/// The standard delegate command, so a Layout can bind a button straight to
/// what it does.
/// </summary>
/// <remarks>
/// This is what lets a Functional Block stay lookless. A block declares an
/// <see cref="ICommand"/> input as a dependency property; its Layout binds a
/// button's <c>Command</c> to it and nothing else. Enablement rides along on
/// <see cref="CanExecute"/>, so no block, layout or host has to mirror
/// "can this be used right now" onto a separate IsEnabled property -- which
/// is the bulk of what the old page shell's own ViewModel-to-control sync
/// existed to do.
/// </remarks>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Action _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    /// <summary>
    /// Re-asks every bound control whether this can be used. The owning
    /// ViewModel raises this from wherever the answer changes, rather than
    /// relying on <see cref="CommandManager.RequerySuggested"/>, whose timing
    /// is tied to input events and not to a ViewModel's own state.
    /// </summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// <see cref="RelayCommand"/> for a command that takes the bound item as its
/// parameter -- a card's own row, a selector entry's key.
/// </summary>
public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    private readonly Action<T> _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        parameter is T typed ? canExecute?.Invoke(typed) ?? true : parameter is null && canExecute is null;

    public void Execute(object? parameter)
    {
        if (parameter is T typed)
        {
            _execute(typed);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// A command whose work is asynchronous, kept from being started twice while
/// the first run is still in flight.
/// </summary>
/// <remarks>
/// The result is deliberately not awaited by the caller: WPF's
/// <see cref="ICommand.Execute"/> is void, and a button that has already
/// disabled itself through <see cref="CanExecute"/> is the visible feedback.
/// Exceptions surface through the ViewModel's own status/error reporting,
/// exactly as they did when the shell invoked these delegates directly.
/// </remarks>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Func<Task> _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        RaiseCanExecuteChanged();

        try
        {
            await _execute().ConfigureAwait(true);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

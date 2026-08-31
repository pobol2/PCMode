using System.Collections.ObjectModel;

namespace PCMode.Tray.Elements;

/// <summary>
/// The scan/rows/problem/status shape shared by every "Current hardware"
/// panel (Audio's discovered outputs, RGB's OpenRGB controllers, Displays'
/// monitor registry): owns <see cref="IsBusy"/>, the discovered rows, and a
/// scan failure's problem text, so a feature supplies only how to discover
/// its rows. Bind a page's <c>CurrentHardwareDataContext</c> directly to an
/// instance of this.
/// </summary>
public sealed class HardwareInventoryList<TRow> : ObservableObject
    where TRow : class, IHardwareInventoryRow
{
    private readonly Func<Task<(IReadOnlyList<TRow> Rows, string? Problem)>> _scan;
    private bool _isBusy;
    private string? _problem;
    private string? _statusMessage;

    public HardwareInventoryList(Func<Task<(IReadOnlyList<TRow> Rows, string? Problem)>> scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        _scan = scan;
    }

    public ObservableCollection<TRow> Rows { get; } = [];

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanScan));
            }
        }
    }

    public bool CanScan => !IsBusy;

    public string? Problem
    {
        get => _problem;
        private set
        {
            if (SetProperty(ref _problem, value))
            {
                OnPropertyChanged(nameof(HasProblem));
            }
        }
    }

    public bool HasProblem => Problem is not null;

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Runs the scan delegate and applies its result. A no-op while a scan is already running.</summary>
    public async Task ScanAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            (IReadOnlyList<TRow> rows, string? problem) = await _scan().ConfigureAwait(true);
            Apply(rows, problem);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Applies a result read some other way (e.g. a snapshot a page's own Refresh already read), without running <see cref="ScanAsync"/>.</summary>
    public void Apply(IReadOnlyList<TRow> rows, string? problem)
    {
        ArgumentNullException.ThrowIfNull(rows);

        Rows.Clear();

        foreach (TRow row in rows)
        {
            Rows.Add(row);
        }

        Problem = problem;
        StatusMessage = problem is null ? $"{Rows.Count} device(s) found." : null;
    }
}

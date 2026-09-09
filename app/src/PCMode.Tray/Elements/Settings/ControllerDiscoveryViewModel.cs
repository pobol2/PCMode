using System.Collections.ObjectModel;
using PCMode.Engine;
using PCMode.Tray.Elements;
using PCMode.Tray.Elements.Configuration;

namespace PCMode.Tray.Elements.Settings;

public sealed class ControllerStatusRow(HardwareItem item)
{
    public string Name => item.Name;

    public string Summary => item.Summary;

    public IReadOnlyList<HardwareDetail> Details => item.Details;

    public bool HasDetails => Details.Count > 0;

    public StatusLevel StatusState =>
        item.Status switch
        {
            HardwareItemStatus.Ok => StatusLevel.Ok,
            HardwareItemStatus.Error => StatusLevel.Error,
            _ => StatusLevel.Neutral,
        };
}

/// <summary>Live XInput slot status shown beside the setting that enables controller input.</summary>
public sealed class ControllerDiscoveryViewModel : ObservableObject
{
    private const string Category = "Controllers";

    private readonly IConfigurationWorkspace _workspace;
    private readonly IGamepadCapabilitiesSource _controllers;

    private bool _isBusy;
    private string? _statusMessage;
    private string? _problem;

    public ControllerDiscoveryViewModel(
        IConfigurationWorkspace workspace,
        IGamepadCapabilitiesSource? controllers = null
    )
    {
        ArgumentNullException.ThrowIfNull(workspace);

        _workspace = workspace;
        _controllers = controllers ?? XInputCapabilitiesSource.Load();
    }

    public ObservableCollection<ControllerStatusRow> Controllers { get; } = [];

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

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

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

    public async Task ScanAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Scanning controllers...";

        try
        {
            HardwareCategoryResult result = await Task.Run(Scan).ConfigureAwait(true);

            Controllers.Clear();

            foreach (HardwareItem item in result.Items)
            {
                Controllers.Add(new ControllerStatusRow(item));
            }

            Problem = result.Problem;
            int connected = result.Items.Count(item => item.Status == HardwareItemStatus.Ok);

            StatusMessage = result.Problem is null ? $"{connected} controller(s) connected." : null;
        }
        catch (Exception exception)
        {
            Problem = $"Controllers could not be checked. {exception.Message}";
            StatusMessage = null;
            _workspace.Fault(Category, "The controller scan failed.", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private HardwareCategoryResult Scan() =>
        HardwareInspector.Discover([new ControllerDiscoverer(_controllers)]).Single();
}

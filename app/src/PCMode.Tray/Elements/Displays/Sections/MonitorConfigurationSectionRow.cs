using System.ComponentModel;
using PCMode.Tray.Elements;

namespace PCMode.Tray.Elements.Displays.Sections;

/// <summary>
/// Which control mechanism a monitor's card is showing: at most one, since a
/// physical screen is either a DDC/CI-capable monitor or a network-controlled
/// television, never both.
/// </summary>
/// <remarks>
/// A string choice rather than a real enum, matching how
/// <see cref="LayoutDirectionChoice"/> is bound from XAML (a ComboBox over
/// <see cref="Options"/>, `DisplayMemberPath`/`SelectedValuePath`/`SelectedValue`).
/// </remarks>
public static class MonitorControlTypeChoice
{
    public const string None = "None";
    public const string Ddc = "Ddc";
    public const string Television = "Television";

    public static IReadOnlyList<MonitorControlTypeOption> Options { get; } =
    [new(None, "None"), new(Ddc, "DDC/CI"), new(Television, "Television")];
}

public sealed record MonitorControlTypeOption(string Value, string Label);

/// <summary>
/// Composes every monitor-scoped editor for one configured display. The page
/// presents a collection of these rows so DDC/CI and television configuration
/// are not coupled to whichever monitor happens to be selected on the layout
/// canvas.
/// </summary>
/// <remarks>
/// ONE CONTROL TYPE AT A TIME
///
/// <see cref="ControlType"/> decides which of <see cref="Ddc"/>'s or
/// <see cref="Television"/>'s fields the card shows. It is not a separate
/// piece of configuration: it is computed from the two facts that already
/// exist (<see cref="Sections.MonitorDdcSectionViewModel.HasBinding"/>,
/// <see cref="Sections.MonitorTelevisionSectionViewModel.HasAssociation"/>)
/// the moment either becomes true, which is also what locks the picker via
/// <see cref="CanChooseControlType"/> -- once a monitor is genuinely bound to
/// one mechanism, switching to the other must go through removing that
/// binding first, the same as it always has. Before either is true, it is
/// just the user's transient pick of which "Add..." prompt to see.
/// </remarks>
public sealed class MonitorConfigurationSectionRow : ObservableObject, ISelectionRow
{
    private string _controlType = MonitorControlTypeChoice.None;

    public MonitorConfigurationSectionRow(
        LayoutMonitorRow monitor,
        MonitorDdcSectionViewModel ddc,
        MonitorTelevisionSectionViewModel television
    )
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(ddc);
        ArgumentNullException.ThrowIfNull(television);

        Monitor = monitor;
        Ddc = ddc;
        Television = television;

        Monitor.PropertyChanged += OnMonitorChanged;
        Ddc.PropertyChanged += OnChildStateChanged;
        Television.PropertyChanged += OnChildStateChanged;

        RecomputeControlType();
    }

    public LayoutMonitorRow Monitor { get; }

    public string MonitorName => Monitor.MonitorName;

    public string? Selection
    {
        get => Monitor.Selection;
        set => Monitor.Selection = value;
    }

    public System.Collections.ObjectModel.ObservableCollection<string> AvailableSelections =>
        Monitor.AvailableSelections;

    public MonitorDdcSectionViewModel Ddc { get; }

    public MonitorTelevisionSectionViewModel Television { get; }

    public IReadOnlyList<MonitorControlTypeOption> ControlTypeOptions => MonitorControlTypeChoice.Options;

    /// <summary>
    /// Which control mechanism this card is showing. Settable while neither
    /// side is bound yet (see <see cref="CanChooseControlType"/>); forced to
    /// match reality the moment either one becomes real.
    /// </summary>
    public string ControlType
    {
        get => _controlType;
        set
        {
            if (!CanChooseControlType)
            {
                return;
            }

            if (SetProperty(ref _controlType, value))
            {
                OnPropertyChanged(nameof(ShowDdc));
                OnPropertyChanged(nameof(ShowDdcCapabilityBadge));
                OnPropertyChanged(nameof(ShowTelevision));
                OnPropertyChanged(nameof(ShowNone));
            }
        }
    }

    /// <summary>Whether the picker can still be changed -- locked once either side is actually bound.</summary>
    public bool CanChooseControlType => !Ddc.HasBinding && !Television.HasAssociation;

    public bool ShowDdc => string.Equals(_controlType, MonitorControlTypeChoice.Ddc, StringComparison.Ordinal);

    public bool ShowDdcCapabilityBadge => ShowDdc && Ddc.HasCapabilityBadge;

    public bool ShowTelevision =>
        string.Equals(_controlType, MonitorControlTypeChoice.Television, StringComparison.Ordinal);

    /// <summary>Whether nothing is picked yet -- shown in Advanced as a pointer back to Essential settings.</summary>
    public bool ShowNone => string.Equals(_controlType, MonitorControlTypeChoice.None, StringComparison.Ordinal);

    private void OnChildStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, Ddc) && e.PropertyName == nameof(MonitorDdcSectionViewModel.HasCapabilityBadge))
        {
            OnPropertyChanged(nameof(ShowDdcCapabilityBadge));
        }

        if (
            e.PropertyName
            is nameof(MonitorDdcSectionViewModel.HasBinding)
                or nameof(MonitorTelevisionSectionViewModel.HasAssociation)
        )
        {
            RecomputeControlType();
        }
    }

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LayoutMonitorRow.MonitorName))
        {
            OnPropertyChanged(nameof(MonitorName));
            OnPropertyChanged(nameof(Selection));
        }
    }

    private void RecomputeControlType()
    {
        string next =
            Ddc.HasBinding ? MonitorControlTypeChoice.Ddc
            : Television.HasAssociation ? MonitorControlTypeChoice.Television
            : MonitorControlTypeChoice.None;

        if (SetProperty(ref _controlType, next, nameof(ControlType)))
        {
            OnPropertyChanged(nameof(ShowDdc));
            OnPropertyChanged(nameof(ShowDdcCapabilityBadge));
            OnPropertyChanged(nameof(ShowTelevision));
            OnPropertyChanged(nameof(ShowNone));
            OnPropertyChanged(nameof(CanChooseControlType));
        }
    }
}

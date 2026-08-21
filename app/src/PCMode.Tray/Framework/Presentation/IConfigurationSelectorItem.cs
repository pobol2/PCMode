namespace PCMode.Tray.Presentation;

/// <summary>
/// A selector-list item whose <see cref="Key"/> can be compared against
/// <see cref="ConfigurationPageViewModelBase.SelectedKey"/> to keep the
/// selector list and the editor in sync.
/// </summary>
public interface IConfigurationSelectorItem
{
    object Key { get; }
}

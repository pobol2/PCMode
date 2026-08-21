namespace PCMode.Tray.Navigation;

public enum PageId
{
    Home,
    Devices,
    Applications,
    Audio,
    Cursor,
    Rgb,
    Theme,
    Layouts,
    Television,
    Spaces,
    Settings,
    Diagnostics
}

public sealed record NavigationItem(
    PageId Page,
    string Label,
    PageId? Parent = null)
{
    public bool IsChild => Parent is not null;
}

public static class NavigationRegistry
{
    public static IReadOnlyList<NavigationItem> Items { get; } =
    [
        new(PageId.Home, "Home"),
        new(PageId.Devices, "Devices"),
        new(PageId.Applications, "Applications", PageId.Devices),
        new(PageId.Audio, "Audio output", PageId.Devices),
        new(PageId.Cursor, "Cursor positions", PageId.Devices),
        new(PageId.Rgb, "Case lighting", PageId.Devices),
        new(PageId.Layouts, "Display layouts", PageId.Devices),
        new(PageId.Theme, "Theme", PageId.Devices),
        new(PageId.Television, "Television", PageId.Devices),
        new(PageId.Spaces, "Spaces"),
        new(PageId.Settings, "Settings"),
        new(PageId.Diagnostics, "Diagnostics")
    ];
}

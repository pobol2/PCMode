using System.Drawing;
using System.IO;

namespace PCMode.Tray.Platform.TrayIcon;

/// <summary>Loads the same embedded multi-size icon used by the executable.</summary>
public static class PCModeIcon
{
    public const string ResourceUri = "/PCMode.Tray;component/Platform/Assets/PCMode.ico";

    public static Icon LoadTrayIcon()
    {
        System.Windows.Resources.StreamResourceInfo resource = System.Windows.Application.GetResourceStream(
            new Uri(ResourceUri, UriKind.Relative))
            ?? throw new InvalidOperationException("The embedded PCMode icon resource could not be found.");

        using Stream stream = resource.Stream;
        using var embedded = new Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        return (Icon)embedded.Clone();
    }
}

using System.Runtime.InteropServices;
using PCMode.Picker;

namespace PCMode.Tray.Platform.Picker;

/// <summary>
/// One monitor, described the way the picker needs it.
/// </summary>
/// <param name="WorkArea">Physical pixels, in virtual-screen coordinates.</param>
/// <param name="Scale">Physical pixels per drawing unit. 1.5 at 150%.</param>
public readonly record struct MonitorSurface(PixelRect WorkArea, double Scale);

/// <summary>
/// Finds the monitor to put the picker on, and how much it is scaled.
/// </summary>
/// <remarks>
/// Ported from legacy <c>PCMode.Tray.Picker.MonitorLayout</c>, minus its own
/// copy of <c>PickerMonitorChoice</c> -- Target already defines that enum in
/// <see cref="PCMode.Tray.Elements.Settings"/> (<c>ApplicationSettings.PickerMonitor</c>
/// persists it), ported ahead of the picker itself.
///
/// The work area rather than the full bounds, so the wheel is centred in the
/// space a user sees rather than partly behind the taskbar.
///
/// The DPI is asked of the MONITOR, not of the process or the primary display.
/// PCMode exists to move a desktop between a monitor and a television, and
/// those are almost never the same scale factor -- so on the machine this was
/// written for, a per-monitor answer is the ordinary case and a system-wide one
/// is simply wrong.
/// </remarks>
public static class MonitorLayout
{
    private const int MonitorDefaultToPrimary = 0x00000001;
    private const int MonitorDefaultToNearest = 0x00000002;

    /// <summary>Effective DPI, which is the one that includes the user's scaling.</summary>
    private const int MdtEffectiveDpi = 0;

    /// <summary>What Windows calls 100%.</summary>
    public const double BaselineDpi = 96.0;

    /// <summary>Describes the monitor a choice selects.</summary>
    public static MonitorSurface Find(PickerMonitorChoice choice)
    {
        nint monitor = choice switch
        {
            PickerMonitorChoice.UnderPointer => FromPointer(),
            PickerMonitorChoice.WithForegroundWindow => MonitorFromWindow(
                GetForegroundWindow(),
                MonitorDefaultToNearest
            ),
            _ => MonitorFromPoint(default, MonitorDefaultToPrimary),
        };

        if (monitor == 0)
        {
            monitor = MonitorFromPoint(default, MonitorDefaultToPrimary);
        }

        return Describe(monitor);
    }

    /// <summary>Where the pointer is, in physical pixels.</summary>
    public static PickerPoint PointerPosition() =>
        GetCursorPos(out POINT point) ? new PickerPoint(point.X, point.Y) : default;

    private static nint FromPointer() =>
        GetCursorPos(out POINT point) ? MonitorFromPoint(point, MonitorDefaultToNearest) : 0;

    private static MonitorSurface Describe(nint monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
        {
            // No monitor to ask. A plausible 1080p desktop at 100% keeps the
            // picker constructible; it is the wrong size on nothing, because
            // there is nothing to be right about.
            return new MonitorSurface(new PixelRect(0, 0, 1920, 1080), 1.0);
        }

        RECT work = info.rcWork;

        return new MonitorSurface(
            new PixelRect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top),
            ScaleOf(monitor)
        );
    }

    private static double ScaleOf(nint monitor)
    {
        try
        {
            // S_OK. Anything else leaves the out parameters untouched, so the
            // values must not be used.
            if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / BaselineDpi;
            }
        }
        catch (DllNotFoundException)
        {
            // Shcore arrived in Windows 8.1. Falling through to 100% gives a
            // picker of the wrong size rather than no picker.
        }
        catch (EntryPointNotFoundException) { }

        return 1.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, int flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}

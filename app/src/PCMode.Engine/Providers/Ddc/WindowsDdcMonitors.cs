using System.Runtime.InteropServices;
using System.Text;

namespace PCMode.Engine;

/// <summary>
/// The real thing: <c>dxva2.dll</c>'s Monitor Configuration API, correlated to
/// the same monitor identity <see cref="WindowsDisplayDevices"/> already uses.
/// </summary>
/// <remarks>
/// WHY IT SHARES ITS IDENTITY WITH THE DISPLAY PROVIDER
///
/// dxva2.dll addresses a monitor by an <c>HMONITOR</c>/physical-monitor handle
/// pair that exists only for as long as this call needs it. The Display
/// Config API <see cref="WindowsDisplayDevices"/> uses addresses the same
/// physical monitor by a stable device interface path. Reusing that path as
/// the identity here -- rather than inventing a second one -- is what lets a
/// user configure a DDC/CI monitor with the exact string a display layout
/// already names, and what lets the two providers' Health lines agree about
/// which monitor they mean.
///
/// The correlation itself is the same <c>EnumDisplayDevices</c> lookup every
/// DDC/CI tool that needs a stable identity uses: the adapter's GDI device
/// name from <c>GetMonitorInfo</c>, handed to <c>EnumDisplayDevices</c> with
/// <see cref="EddGetDeviceInterfaceName"/> set, is what turns it into the same
/// <c>\\?\DISPLAY#...#{GUID}</c> path <c>DisplayConfigGetDeviceInfo</c> reports
/// -- see <see cref="TryGetDevicePath"/>'s remarks for the wrong-seeming but
/// real gotcha in how that flag has to be asked for.
///
/// ONLY THE FIRST PHYSICAL MONITOR BEHIND EACH OUTPUT
///
/// An <c>HMONITOR</c> can carry more than one physical monitor -- behind an
/// MST hub or certain KVMs -- and <c>GetPhysicalMonitorsFromHMONITOR</c> would
/// return all of them. This takes the first and destroys the rest unused.
/// That is the bounded, ordinary case this step targets; a monitor reached
/// through such a hub is out of scope, per the roadmap's "not exhaustive
/// support" for v1.
///
/// EVERY CALL RUNS ON ITS OWN THREAD, BOUNDED BY A JOIN
///
/// None of the Win32 calls here are cancellable once issued, and a monitor
/// that is asleep, disconnected, or DDC/CI-non-compliant can leave one
/// hanging indefinitely. Running the work on a background thread and bounding
/// it with <see cref="Thread.Join(TimeSpan)"/> is what lets a caller give up
/// on schedule regardless: a timeout abandons the thread rather than the call,
/// because Windows offers no safe way to interrupt a native call already in
/// flight. An abandoned thread that never returns costs one idle thread for
/// the life of the process, which is the honest price of bounding something
/// that cannot be cancelled -- not silence about the risk.
/// </remarks>
public sealed class WindowsDdcMonitors : IDdcMonitors
{
    private const uint EddGetDeviceInterfaceName = 0x00000001;

    public IReadOnlyList<string> KnownDevicePaths(TimeSpan within)
    {
        return RunBounded(
            within,
            static () => (IReadOnlyList<string>)
                [.. EnumerateMonitorHandles().Select(monitor => monitor.DevicePath).Distinct(StringComparer.OrdinalIgnoreCase)],
            out IReadOnlyList<string>? result) == BoundedOutcome.Completed
            ? result!
            : [];
    }

    public DdcCapabilities? ReadCapabilities(string devicePath, TimeSpan within)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);

        return RunBounded(within, () => ReadCapabilitiesCore(devicePath), out DdcCapabilities? result) ==
               BoundedOutcome.Completed
            ? result
            : null;
    }

    public DdcVcpValue? GetVcpFeature(string devicePath, DdcVcpCode code, TimeSpan within)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);

        return RunBounded(within, () => GetVcpCore(devicePath, code), out DdcVcpValue? result) ==
               BoundedOutcome.Completed
            ? result
            : null;
    }

    public void SetVcpFeature(string devicePath, DdcVcpCode code, uint value, TimeSpan within)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);

        BoundedOutcome outcome = RunBounded(
            within, () => SetVcpCore(devicePath, code, value), out bool? found, out Exception? failure);

        switch (outcome)
        {
            case BoundedOutcome.TimedOut:
                throw new InvalidOperationException(
                    $"The monitor did not respond to VCP 0x{(byte)code:X2} within " +
                    $"{within.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}s. " +
                    "It may be asleep, disconnected, or not DDC/CI compliant.");

            case BoundedOutcome.Failed:
                throw new InvalidOperationException(
                    $"DDC/CI could not set VCP 0x{(byte)code:X2}. {failure!.Message}", failure);

            default:
                if (found != true)
                {
                    throw new InvalidOperationException($"No DDC/CI monitor is currently present at '{devicePath}'.");
                }

                return;
        }
    }

    private static DdcCapabilities? ReadCapabilitiesCore(string devicePath)
    {
        if (!TryResolvePhysicalMonitor(devicePath, out nint handle))
        {
            return null;
        }

        try
        {
            if (!GetCapabilitiesStringLength(handle, out uint length) || length == 0)
            {
                return DdcCapabilities.None;
            }

            var buffer = new byte[length];

            if (!CapabilitiesRequestAndCapabilitiesReply(handle, buffer, length))
            {
                return DdcCapabilities.None;
            }

            int terminator = Array.IndexOf(buffer, (byte)0);
            string raw = Encoding.ASCII.GetString(buffer, 0, terminator >= 0 ? terminator : buffer.Length);

            return DdcCapabilityParser.Parse(raw);
        }
        finally
        {
            _ = DestroyPhysicalMonitor(handle);
        }
    }

    private static DdcVcpValue? GetVcpCore(string devicePath, DdcVcpCode code)
    {
        if (!TryResolvePhysicalMonitor(devicePath, out nint handle))
        {
            return null;
        }

        try
        {
            return GetVCPFeatureAndVCPFeatureReply(handle, (byte)code, nint.Zero, out uint current, out uint maximum)
                ? new DdcVcpValue(current, maximum)
                : null;
        }
        finally
        {
            _ = DestroyPhysicalMonitor(handle);
        }
    }

    /// <summary>Whether a monitor was found at all, distinct from the command failing on one that was.</summary>
    private static bool SetVcpCore(string devicePath, DdcVcpCode code, uint value)
    {
        if (!TryResolvePhysicalMonitor(devicePath, out nint handle))
        {
            return false;
        }

        try
        {
            if (!SetVCPFeature(handle, (byte)code, value))
            {
                throw new InvalidOperationException("the monitor rejected the command.");
            }
        }
        finally
        {
            _ = DestroyPhysicalMonitor(handle);
        }

        return true;
    }

    /// <summary>
    /// The physical monitor handle behind <paramref name="devicePath"/>, resolved fresh.
    /// </summary>
    /// <remarks>
    /// Every sibling physical monitor handle behind the same output that is not
    /// the one returned is destroyed here rather than left open: this method
    /// asked the API for all of them, and only ever uses the first.
    /// </remarks>
    private static bool TryResolvePhysicalMonitor(string devicePath, out nint handle)
    {
        foreach ((nint hMonitor, string candidatePath) in EnumerateMonitorHandles())
        {
            if (!string.Equals(candidatePath, devicePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
            {
                handle = nint.Zero;
                return false;
            }

            var physical = new PhysicalMonitor[count];

            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physical))
            {
                handle = nint.Zero;
                return false;
            }

            handle = physical[0].Handle;

            for (int i = 1; i < physical.Length; i++)
            {
                _ = DestroyPhysicalMonitor(physical[i].Handle);
            }

            return true;
        }

        handle = nint.Zero;
        return false;
    }

    private static IEnumerable<(nint HMonitor, string DevicePath)> EnumerateMonitorHandles()
    {
        var found = new List<(nint, string)>();

        bool Callback(nint hMonitor, nint hdcMonitor, ref RectStruct rect, nint data)
        {
            if (TryGetDevicePath(hMonitor, out string? path, out _))
            {
                found.Add((hMonitor, path!));
            }

            return true;
        }

        _ = EnumDisplayMonitors(nint.Zero, nint.Zero, Callback, nint.Zero);
        return found;
    }

    /// <summary>
    /// The same stable device interface path <see cref="WindowsDisplayDevices"/>
    /// reports for this monitor, via the adapter device name
    /// <c>GetMonitorInfo</c> gives and a single <c>EnumDisplayDevices</c> call
    /// against that adapter with <see cref="EddGetDeviceInterfaceName"/> set.
    /// </summary>
    /// <remarks>
    /// NOT a two-step lookup through an intermediate monitor sub-device name.
    /// An earlier version of this method fetched the monitor's own device name
    /// first (<c>\\.\DISPLAY1\Monitor0</c>) and then re-queried
    /// <c>EnumDisplayDevices</c> using THAT as <c>lpDevice</c> -- which returns
    /// <see langword="false"/> with <c>GetLastError() == 0</c> on real hardware,
    /// because a monitor sub-device name is not a valid <c>lpDevice</c> for this
    /// flag. <c>lpDevice</c> must stay the adapter's own name throughout;
    /// <paramref name="hMonitor"/>'s monitor index on that adapter is 0 for the
    /// ordinary one-monitor-per-output case this targets, matching the index
    /// used everywhere else <c>EnumDisplayDevices</c> is called against an
    /// adapter in this class.
    /// </remarks>
    /// <param name="trace">
    /// What happened at each stage, win32 error codes included -- for
    /// <see cref="DiagnoseEnumeration"/>. Built even on the success path, since
    /// a caller diagnosing a monitor that resolved on one machine but not
    /// another needs to see what agreed as much as what did not.
    /// </param>
    private static bool TryGetDevicePath(nint hMonitor, out string? devicePath, out string trace)
    {
        devicePath = null;
        var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>() };

        if (!GetMonitorInfo(hMonitor, ref info))
        {
            trace = $"GetMonitorInfo failed (error {Marshal.GetLastWin32Error()}).";
            return false;
        }

        var monitorDevice = new DisplayDeviceW { Size = (uint)Marshal.SizeOf<DisplayDeviceW>() };

        if (!EnumDisplayDevices(info.DeviceName, 0, ref monitorDevice, EddGetDeviceInterfaceName))
        {
            trace =
                $"GetMonitorInfo -> adapter '{info.DeviceName}'. " +
                $"EnumDisplayDevices(adapter, 0, EDD_GET_DEVICE_INTERFACE_NAME) failed " +
                $"(error {Marshal.GetLastWin32Error()}).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(monitorDevice.DeviceID))
        {
            trace =
                $"GetMonitorInfo -> adapter '{info.DeviceName}'. " +
                "EnumDisplayDevices with EDD_GET_DEVICE_INTERFACE_NAME succeeded but returned an empty DeviceID.";
            return false;
        }

        devicePath = monitorDevice.DeviceID;
        trace = $"'{info.DeviceName}' -> '{devicePath}'.";
        return true;
    }

    /// <summary>
    /// A trace of what <see cref="KnownDevicePaths"/> found and, for every
    /// monitor handle Windows reported that did not resolve to a device path,
    /// exactly which stage refused it and with what Win32 error.
    /// </summary>
    /// <remarks>
    /// For <see cref="DdcHardwareTest"/>: "0 monitors" on its own says only that
    /// something in a four-stage lookup did not agree, and every stage fails the
    /// same quiet way -- a returned <see langword="false"/> -- by design, since
    /// <see cref="IDdcMonitors"/> is meant to leave a monitor out rather than
    /// throw over an ordinary "not this one". A human debugging an unexpectedly
    /// empty result needs the stage and the error code back, which this exists
    /// to give without changing what every other caller sees.
    /// </remarks>
    internal static IReadOnlyList<string> DiagnoseEnumeration()
    {
        var lines = new List<string>();

        bool Callback(nint hMonitor, nint hdcMonitor, ref RectStruct rect, nint data)
        {
            _ = TryGetDevicePath(hMonitor, out _, out string trace);
            lines.Add(trace);
            return true;
        }

        if (!EnumDisplayMonitors(nint.Zero, nint.Zero, Callback, nint.Zero))
        {
            lines.Insert(0, $"EnumDisplayMonitors itself returned false (error {Marshal.GetLastWin32Error()}).");
        }
        else if (lines.Count == 0)
        {
            lines.Add("EnumDisplayMonitors succeeded but its callback was never invoked -- Windows reports no monitors at all.");
        }

        return lines;
    }

    private enum BoundedOutcome
    {
        Completed,
        Failed,
        TimedOut,
    }

    private static BoundedOutcome RunBounded<T>(TimeSpan within, Func<T> work, out T? result) =>
        RunBounded(within, work, out result, out _);

    /// <summary>
    /// Runs <paramref name="work"/> on its own thread and waits up to
    /// <paramref name="within"/> for it. See the class remarks for why a thread
    /// rather than a cancellation token.
    /// </summary>
    private static BoundedOutcome RunBounded<T>(
        TimeSpan within, Func<T> work, out T? result, out Exception? failure)
    {
        T? captured = default;
        Exception? caught = null;
        var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                captured = work();
            }
            catch (Exception exception)
            {
                caught = exception;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "PCMode DDC/CI",
        };

        thread.Start();

        if (!done.Wait(within))
        {
            result = default;
            failure = null;
            return BoundedOutcome.TimedOut;
        }

        result = captured;
        failure = caught;
        return caught is null ? BoundedOutcome.Completed : BoundedOutcome.Failed;
    }

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref RectStruct lprcMonitor, nint dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectStruct
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public RectStruct Monitor;
        public RectStruct WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDeviceW
    {
        public uint Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public nint Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(
        nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumDisplayDevices(
        string lpDevice, uint iDevNum, ref DisplayDeviceW lpDisplayDevice, uint dwFlags);

    [DllImport("dxva2.dll")]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
        nint hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(
        nint hMonitor, uint dwPhysicalMonitorArraySize, [Out] PhysicalMonitor[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll")]
    private static extern bool DestroyPhysicalMonitor(nint hMonitor);

    [DllImport("dxva2.dll")]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(
        nint hMonitor, byte bVCPCode, nint pvct, out uint pdwCurrentValue, out uint pdwMaximumValue);

    [DllImport("dxva2.dll")]
    private static extern bool SetVCPFeature(nint hMonitor, byte bVCPCode, uint dwNewValue);

    [DllImport("dxva2.dll")]
    private static extern bool GetCapabilitiesStringLength(
        nint hMonitor, out uint pdwCapabilitiesStringLengthInCharacters);

    [DllImport("dxva2.dll", CharSet = CharSet.Ansi)]
    private static extern bool CapabilitiesRequestAndCapabilitiesReply(
        nint hMonitor, [Out] byte[] pszASCIICapabilitiesString, uint dwCapabilitiesStringLengthInCharacters);
}

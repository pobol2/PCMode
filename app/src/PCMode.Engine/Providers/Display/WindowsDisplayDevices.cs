using System.Globalization;
using System.Runtime.InteropServices;

namespace PCMode.Engine;

/// <summary>
/// The real thing: the Windows Display Config (CCD) API.
/// </summary>
/// <remarks>
/// MECHANISM ONLY
///
/// This knows how to enumerate monitors, activate a set of them, position them
/// and read back what happened. It knows nothing about layouts, logical names or
/// configuration -- that is <see cref="DisplayProvider"/>, its only caller. The
/// division is what keeps every rule about placement testable without a monitor.
///
/// EVERY QUERY STARTS OVER
///
/// No adapter or target identifier survives a call. A monitor can be unplugged,
/// replaced or asleep between two of them, which is the same rule the audio
/// endpoints keep and about the same television.
///
/// ALL PATHS, NOT ONLY ACTIVE ONES
///
/// Activating a monitor queries QDC_ALL_PATHS. Unlike an audio endpoint, which
/// disappears entirely while its device is off, Windows keeps a slot for every
/// target it has ever detected -- which is exactly what lets a television be
/// switched to without it having been on a moment before.
///
/// REFRESH RATES ARE ASKED FOR EXPLICITLY, OR NOT AT ALL
///
/// A path returned for a target that is not currently active reports its refresh
/// rate as 0/0. Handed back to Windows untouched that is an unconstrained
/// request, and Windows is free to answer it with 60 Hz on a monitor whose native
/// rate is far higher. So a positive rate is stamped into the request and a zero
/// leaves the queried value alone. Mode indices stay invalid either way: Windows
/// still chooses the resolution and the timings, and only the rate is constrained.
/// </remarks>
public sealed class WindowsDisplayDevices : IDisplayDevices
{
    public IReadOnlyList<DisplayTarget> KnownTargets()
    {
        // QDC_ALL_PATHS returns one row per possible source/target combination,
        // including empty slots with nothing attached and the same real monitor
        // repeated once per source. Reduced to one entry per physical monitor,
        // which is the only thing any caller means by "a monitor".
        var seen = new Dictionary<string, DisplayTarget>(StringComparer.OrdinalIgnoreCase);

        foreach (PathInfo path in QueryPaths(QdcAllPaths))
        {
            string? devicePath = TargetName(path, out string? friendlyName);

            if (string.IsNullOrEmpty(devicePath))
            {
                continue;
            }

            bool active = (path.Flags & PathActive) != 0;

            if (seen.TryGetValue(devicePath, out DisplayTarget? existing))
            {
                if (active && !existing.Active)
                {
                    seen[devicePath] = existing with { Active = true };
                }

                continue;
            }

            seen[devicePath] = new DisplayTarget(devicePath, friendlyName ?? string.Empty, active);
        }

        return [.. seen.Values];
    }

    public IReadOnlyList<ActiveDisplay> ActiveDisplays()
    {
        Query(QdcOnlyActivePaths, out PathInfo[] paths, out ModeInfo[] modes);

        var displays = new List<ActiveDisplay>(paths.Length);

        foreach (PathInfo path in paths)
        {
            string? devicePath = TargetName(path, out string? friendlyName);

            if (string.IsNullOrEmpty(devicePath))
            {
                continue;
            }

            int x = 0;
            int y = 0;
            int width = 0;
            int height = 0;
            double refreshHz = 0;

            int source = FindSourceMode(modes, path);

            if (source >= 0)
            {
                x = modes[source].Mode.SourceMode.Position.X;
                y = modes[source].Mode.SourceMode.Position.Y;
                width = (int)modes[source].Mode.SourceMode.Width;
                height = (int)modes[source].Mode.SourceMode.Height;
            }

            int target = FindTargetMode(modes, path);

            if (target >= 0)
            {
                // The rate the monitor achieved, not the one anything asked for.
                Rational rate = modes[target].Mode.TargetMode.TargetVideoSignalInfo.VSyncFreq;

                if (rate.Denominator != 0)
                {
                    refreshHz = (double)rate.Numerator / rate.Denominator;
                }
            }

            displays.Add(new ActiveDisplay(
                devicePath,
                friendlyName ?? string.Empty,
                x,
                y,
                width,
                height,
                refreshHz));
        }

        return displays;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<int>> SupportedRefreshRates()
    {
        // Only an active monitor can be asked, and only about the resolution it
        // is currently at: this goes through the GDI device name, which does not
        // exist for a target that is not driving a picture.
        Query(QdcOnlyActivePaths, out PathInfo[] paths, out ModeInfo[] modes);

        var rates = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (PathInfo path in paths)
        {
            string? devicePath = TargetName(path, out _);

            if (string.IsNullOrEmpty(devicePath) || rates.ContainsKey(devicePath))
            {
                continue;
            }

            int source = FindSourceMode(modes, path);

            if (source < 0)
            {
                continue;
            }

            uint width = modes[source].Mode.SourceMode.Width;
            uint height = modes[source].Mode.SourceMode.Height;

            var request = new SourceDeviceName
            {
                Header = new DeviceInfoHeader
                {
                    Type = GetSourceName,
                    Size = (uint)Marshal.SizeOf<SourceDeviceName>(),
                    AdapterId = path.SourceInfo.AdapterId,
                    Id = path.SourceInfo.Id,
                },
            };

            if (GetSourceDeviceInfo(ref request) != 0)
            {
                continue;
            }

            var found = new SortedSet<int>();

            var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };

            for (uint index = 0; EnumDisplaySettingsEx(request.ViewGdiDeviceName, index, ref mode, 0); index++)
            {
                if (mode.BitsPerPixel >= 32 &&
                    mode.PelsWidth == width &&
                    mode.PelsHeight == height &&
                    mode.DisplayFrequency > 0)
                {
                    found.Add((int)mode.DisplayFrequency);
                }

                mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            }

            rates[devicePath] = [.. found];
        }

        return rates;
    }

    public void Activate(IReadOnlyList<string> devicePaths, IReadOnlyList<int> refreshHz)
    {
        ArgumentNullException.ThrowIfNull(devicePaths);
        ArgumentNullException.ThrowIfNull(refreshHz);

        if (devicePaths.Count == 0)
        {
            throw new InvalidOperationException("A display layout must contain at least one monitor.");
        }

        if (refreshHz.Count != devicePaths.Count)
        {
            throw new InvalidOperationException("Every monitor needs a refresh rate, or 0 for whichever Windows picks.");
        }

        PathInfo[] all = QueryPaths(QdcAllPaths);
        var selected = new List<PathInfo>(devicePaths.Count);

        if (!TrySelect(devicePaths, refreshHz, all, 0, selected))
        {
            throw new InvalidOperationException(
                "Windows could not build a valid arrangement from those monitors. " +
                "One or more of them may not be connected or awake.");
        }

        PathInfo[] request = [.. selected];

        // Validated first, so a request Windows was never going to accept does
        // not blank the screens on its way to being refused.
        Check(
            SetDisplayConfig((uint)request.Length, request, 0, nint.Zero, SdcValidate | CommonFlags),
            "accept that arrangement of monitors");

        Check(
            SetDisplayConfig((uint)request.Length, request, 0, nint.Zero, SdcApply | CommonFlags),
            "switch to that arrangement of monitors");
    }

    public void Arrange(IReadOnlyList<string> devicePaths, IReadOnlyList<int> x, IReadOnlyList<int> y)
    {
        ArgumentNullException.ThrowIfNull(devicePaths);
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);

        if (devicePaths.Count != x.Count || devicePaths.Count != y.Count)
        {
            throw new InvalidOperationException("Every monitor being moved needs both an X and a Y.");
        }

        if (devicePaths.Count == 0)
        {
            return;
        }

        // The whole active configuration is queried and handed straight back with
        // only the positions rewritten, so every mode Windows chose when the
        // monitors were activated is preserved exactly as it chose it.
        Query(QdcOnlyActivePaths, out PathInfo[] paths, out ModeInfo[] modes);

        for (int index = 0; index < devicePaths.Count; index++)
        {
            bool found = false;

            foreach (PathInfo path in paths)
            {
                string? devicePath = TargetName(path, out _);

                if (string.IsNullOrEmpty(devicePath) ||
                    !string.Equals(devicePath, devicePaths[index], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int source = FindSourceMode(modes, path);

                if (source < 0)
                {
                    throw new InvalidOperationException(
                        "A monitor that is showing a picture reports no mode, so it cannot be moved.");
                }

                modes[source].Mode.SourceMode.Position.X = x[index];
                modes[source].Mode.SourceMode.Position.Y = y[index];
                found = true;
                break;
            }

            if (!found)
            {
                throw new InvalidOperationException(
                    "A monitor that was meant to be moved is not showing a picture.");
            }
        }

        Check(
            SetDisplayConfigWithModes(
                (uint)paths.Length, paths, (uint)modes.Length, modes, SdcValidate | CommonFlags),
            "accept that arrangement of the monitors");

        Check(
            SetDisplayConfigWithModes(
                (uint)paths.Length, paths, (uint)modes.Length, modes, SdcApply | CommonFlags),
            "move the monitors into place");
    }

    /// <summary>
    /// Chooses one path per requested monitor, backtracking when a combination
    /// cannot be completed.
    /// </summary>
    /// <remarks>
    /// A monitor can be reachable through several sources and two monitors cannot
    /// share one, so picking greedily can strand the last monitor with nothing
    /// left. Already-active paths are preferred, which keeps a monitor that is
    /// already on where it is rather than moving it to a different source for no
    /// reason.
    /// </remarks>
    private static bool TrySelect(
        IReadOnlyList<string> devicePaths,
        IReadOnlyList<int> refreshHz,
        PathInfo[] all,
        int index,
        List<PathInfo> selected)
    {
        if (index >= devicePaths.Count)
        {
            return true;
        }

        for (int pass = 0; pass < 2; pass++)
        {
            foreach (PathInfo candidate in all)
            {
                string? devicePath = TargetName(candidate, out _);

                if (string.IsNullOrEmpty(devicePath) ||
                    !string.Equals(devicePath, devicePaths[index], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // A target that is not available cannot be activated at all --
                // the monitor is off, or gone.
                if (!candidate.TargetInfo.TargetAvailable)
                {
                    continue;
                }

                bool active = (candidate.Flags & PathActive) != 0;

                if ((pass == 0 && !active) || (pass == 1 && active))
                {
                    continue;
                }

                if (selected.Any(taken => SameSource(candidate, taken)))
                {
                    continue;
                }

                PathInfo path = candidate;

                // Windows chooses the resolution and the timings.
                path.SourceInfo.ModeInfoIdx = ModeIndexInvalid;
                path.TargetInfo.ModeInfoIdx = ModeIndexInvalid;

                if (refreshHz[index] > 0)
                {
                    // See the class remarks: this is what turns an otherwise
                    // empty request into a specific one.
                    path.TargetInfo.RefreshRate.Numerator = (uint)refreshHz[index];
                    path.TargetInfo.RefreshRate.Denominator = 1;
                    path.TargetInfo.ScanLineOrdering = ScanLineOrderingProgressive;
                }

                path.Flags = PathActive;

                selected.Add(path);

                if (TrySelect(devicePaths, refreshHz, all, index + 1, selected))
                {
                    return true;
                }

                selected.RemoveAt(selected.Count - 1);
            }
        }

        return false;
    }

    private static bool SameSource(PathInfo left, PathInfo right) =>
        left.SourceInfo.AdapterId.LowPart == right.SourceInfo.AdapterId.LowPart &&
        left.SourceInfo.AdapterId.HighPart == right.SourceInfo.AdapterId.HighPart &&
        left.SourceInfo.Id == right.SourceInfo.Id;

    /// <summary>
    /// Matched on adapter and source identity rather than by following
    /// modeInfoIdx.
    /// </summary>
    /// <remarks>
    /// On Windows 10 and later that field is a packed union carrying a clone
    /// group in its high bits whenever the path supports virtual modes, so
    /// reading it as a plain index is wrong on exactly the machines that have
    /// them. The identity match is correct on every version.
    /// </remarks>
    private static int FindSourceMode(ModeInfo[] modes, PathInfo path)
    {
        for (int index = 0; index < modes.Length; index++)
        {
            if (modes[index].InfoType == ModeInfoTypeSource &&
                modes[index].AdapterId.LowPart == path.SourceInfo.AdapterId.LowPart &&
                modes[index].AdapterId.HighPart == path.SourceInfo.AdapterId.HighPart &&
                modes[index].Id == path.SourceInfo.Id)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>The same identity match, for the target side.</summary>
    private static int FindTargetMode(ModeInfo[] modes, PathInfo path)
    {
        for (int index = 0; index < modes.Length; index++)
        {
            if (modes[index].InfoType == ModeInfoTypeTarget &&
                modes[index].AdapterId.LowPart == path.TargetInfo.AdapterId.LowPart &&
                modes[index].AdapterId.HighPart == path.TargetInfo.AdapterId.HighPart &&
                modes[index].Id == path.TargetInfo.Id)
            {
                return index;
            }
        }

        return -1;
    }

    private static string? TargetName(PathInfo path, out string? friendlyName)
    {
        var request = new TargetDeviceName
        {
            Header = new DeviceInfoHeader
            {
                Type = GetTargetName,
                Size = (uint)Marshal.SizeOf<TargetDeviceName>(),
                AdapterId = path.TargetInfo.AdapterId,
                Id = path.TargetInfo.Id,
            },
        };

        friendlyName = null;

        if (GetTargetDeviceInfo(ref request) != 0)
        {
            return null;
        }

        friendlyName = request.MonitorFriendlyDeviceName;
        return request.MonitorDevicePath;
    }

    private static PathInfo[] QueryPaths(uint flags)
    {
        Query(flags, out PathInfo[] paths, out _);
        return paths;
    }

    private static void Query(uint flags, out PathInfo[] outPaths, out ModeInfo[] outModes)
    {
        // The buffer can grow between being sized and being filled -- a monitor
        // appearing mid-call -- which is what ERROR_INSUFFICIENT_BUFFER means
        // here. Size again and retry rather than fail.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Check(
                GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount),
                "count the monitors");

            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];

            int result = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, nint.Zero);

            if (result == 0)
            {
                Array.Resize(ref paths, (int)pathCount);
                Array.Resize(ref modes, (int)modeCount);

                outPaths = paths;
                outModes = modes;
                return;
            }

            if (result != ErrorInsufficientBuffer)
            {
                Check(result, "read the monitors");
            }
        }

        throw new InvalidOperationException(
            "Windows kept reporting a different number of monitors than it then returned.");
    }

    private static void Check(int result, string attempted)
    {
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"Windows could not {attempted} " +
                $"(error {result.ToString(CultureInfo.InvariantCulture)}).");
        }
    }

    private const uint QdcAllPaths = 0x00000001;
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const uint SdcUseSuppliedDisplayConfig = 0x00000020;
    private const uint SdcValidate = 0x00000040;
    private const uint SdcApply = 0x00000080;
    private const uint SdcAllowChanges = 0x00000400;
    private const uint CommonFlags = SdcUseSuppliedDisplayConfig | SdcAllowChanges;

    private const uint PathActive = 0x00000001;
    private const uint ScanLineOrderingProgressive = 1;
    private const uint ModeIndexInvalid = 0xFFFFFFFF;
    private const uint GetSourceName = 1;
    private const uint GetTargetName = 2;
    private const uint ModeInfoTypeSource = 1;
    private const uint ModeInfoTypeTarget = 2;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        [Out] PathInfo[] paths,
        ref uint modeCount,
        [Out] ModeInfo[] modes,
        nint topologyId);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(
        uint pathCount,
        [In] PathInfo[] paths,
        uint modeCount,
        nint modes,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "SetDisplayConfig")]
    private static extern int SetDisplayConfigWithModes(
        uint pathCount,
        [In] PathInfo[] paths,
        uint modeCount,
        [In] ModeInfo[] modes,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSourceDeviceInfo(ref SourceDeviceName request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetTargetDeviceInfo(ref TargetDeviceName request);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsEx(string deviceName, uint modeNumber, ref DevMode mode, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;

        [MarshalAs(UnmanagedType.Bool)]
        public bool TargetAvailable;

        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Region
    {
        public uint Cx;
        public uint Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoSignalInfo
    {
        public ulong PixelRate;
        public Rational HSyncFreq;
        public Rational VSyncFreq;
        public Region ActiveSize;
        public Region TotalSize;
        public uint VideoStandard;
        public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetMode
    {
        public VideoSignalInfo TargetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceMode
    {
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public PointL Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectL
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DesktopImageInfo
    {
        public PointL PathSourceSize;
        public RectL DesktopImageRegion;
        public RectL DesktopImageClip;
    }

    /// <summary>
    /// The union a mode carries. Sized explicitly, because the three members
    /// overlap and the largest of them is what Windows allocates.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct ModeUnion
    {
        [FieldOffset(0)]
        public TargetMode TargetMode;

        [FieldOffset(0)]
        public SourceMode SourceMode;

        [FieldOffset(0)]
        public DesktopImageInfo DesktopImageInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
        public ModeUnion Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    /// <summary>
    /// Win32's DEVMODEW, only as far as it is needed.
    /// </summary>
    /// <remarks>
    /// Every field is declared even though few are read: this is a fixed layout
    /// the caller states the size of, and a missing field would move every one
    /// after it.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TtOption;
        public short Collate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;

        public ushort LogPixels;
        public uint BitsPerPixel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }
}

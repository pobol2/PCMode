namespace PCMode.Engine.Tests;

/// <summary>One monitor of a machine that does not exist.</summary>
internal sealed class FakeMonitor
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    /// <summary>The rates it will accept. A request for anything else is ignored.</summary>
    public IReadOnlyList<int> Supported { get; init; } = [60, 120, 144];

    /// <summary>
    /// Whether it can be switched on at all. False is a television that is off:
    /// Windows still has a slot for it, and still cannot show a picture on it.
    /// </summary>
    public bool Available { get; set; } = true;

    public bool Active { get; set; }

    /// <summary>
    /// How many readbacks still report it dark after it has been switched on.
    /// </summary>
    /// <remarks>
    /// A television on the end of an HDMI cable. Windows accepts the topology
    /// and returns; the picture arrives seconds later, and until it does the
    /// configuration reads as though nothing happened.
    /// </remarks>
    public int WakesAfter { get; init; }

    /// <summary>How many of those readbacks are left.</summary>
    public int Waking { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public double Hz { get; set; } = 60;
}

/// <summary>What was asked for.</summary>
internal sealed record ActivationCall(IReadOnlyList<string> Paths, IReadOnlyList<int> RefreshHz);

internal sealed record ArrangementCall(IReadOnlyList<string> Paths, IReadOnlyList<int> X, IReadOnlyList<int> Y);

/// <summary>
/// A machine's monitors, made up.
/// </summary>
/// <remarks>
/// The reason <see cref="IDisplayDevices"/> is an interface. A television that is
/// switched off, a refresh rate Windows quietly declines to give, a monitor that
/// does not end up where it was put -- none of those can be arranged on the
/// machine running the tests, and all of them are the cases where the rules
/// actually matter. Nor may a test suite blank somebody's screens to find out.
/// </remarks>
internal sealed class FakeDisplayDevices : IDisplayDevices
{
    private readonly List<FakeMonitor> _monitors = [];

    /// <summary>Thrown from every call, when set.</summary>
    public Exception? Fails { get; set; }

    /// <summary>
    /// When set, arrangement requests are recorded and then quietly ignored --
    /// which is what makes the difference between a provider that verifies its
    /// own work and one that reports whatever it asked for as done.
    /// </summary>
    public bool IgnoresArrange { get; set; }

    public List<ActivationCall> Activations { get; } = [];

    public List<ArrangementCall> Arrangements { get; } = [];

    /// <summary>Whether anything at all was done to the screens.</summary>
    public bool Untouched => Activations.Count == 0 && Arrangements.Count == 0;

    public FakeDisplayDevices Add(FakeMonitor monitor)
    {
        _monitors.Add(monitor);
        return this;
    }

    public FakeMonitor this[string path] => _monitors.First(monitor => monitor.Path == path);

    public IReadOnlyList<DisplayTarget> KnownTargets() =>
        Fails is not null
            ? throw Fails
            : [.. _monitors.Select(monitor => new DisplayTarget(monitor.Path, monitor.Name, monitor.Active))];

    public IReadOnlyList<ActiveDisplay> ActiveDisplays() =>
        Fails is not null
            ? throw Fails
            :
            [
                .. _monitors
                    .Where(StillDark)
                    .Select(monitor => new ActiveDisplay(
                        monitor.Path,
                        monitor.Name,
                        monitor.X,
                        monitor.Y,
                        monitor.Width,
                        monitor.Height,
                        monitor.Hz
                    )),
            ];

    /// <summary>
    /// Whether this monitor is showing a picture by the time somebody looks.
    /// </summary>
    /// <remarks>
    /// Counting down here rather than on a clock, because what the provider can
    /// see is readbacks and not seconds: it asks, and the answer changes at some
    /// point between one asking and another.
    /// </remarks>
    private static bool StillDark(FakeMonitor monitor)
    {
        if (!monitor.Active)
        {
            return false;
        }

        if (monitor.Waking <= 0)
        {
            return true;
        }

        monitor.Waking--;
        return false;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<int>> SupportedRefreshRates() =>
        Fails is not null
            ? throw Fails
            : _monitors
                .Where(monitor => monitor.Active)
                .ToDictionary(monitor => monitor.Path, monitor => monitor.Supported, StringComparer.OrdinalIgnoreCase);

    public void Activate(IReadOnlyList<string> devicePaths, IReadOnlyList<int> refreshHz)
    {
        if (Fails is not null)
        {
            throw Fails;
        }

        Activations.Add(new ActivationCall([.. devicePaths], [.. refreshHz]));

        foreach (string path in devicePaths)
        {
            FakeMonitor monitor =
                _monitors.FirstOrDefault(candidate => candidate.Path == path)
                ?? throw new InvalidOperationException($"Windows has never seen a monitor at '{path}'.");

            if (!monitor.Available)
            {
                throw new InvalidOperationException(
                    "Windows could not build a valid arrangement from those monitors. "
                        + "One or more of them may not be connected or awake."
                );
            }
        }

        foreach (FakeMonitor monitor in _monitors)
        {
            int index = devicePaths.ToList().IndexOf(monitor.Path);

            if (index < 0)
            {
                monitor.Active = false;
                continue;
            }

            if (!monitor.Active)
            {
                // A monitor Windows has just switched on lands wherever Windows
                // felt like putting it, which is why arrangement is a second
                // phase rather than something a topology request can carry.
                monitor.Active = true;
                monitor.Waking = monitor.WakesAfter;
                monitor.X = 0;
                monitor.Y = 0;
            }

            // A rate that was asked for and is not supported is not refused: the
            // request is accepted and something else is delivered. Reading back
            // is the only way to find out, which is the whole reason the provider
            // verifies rather than trusts.
            monitor.Hz = refreshHz[index] > 0 && monitor.Supported.Contains(refreshHz[index]) ? refreshHz[index] : 60;
        }
    }

    public void Arrange(IReadOnlyList<string> devicePaths, IReadOnlyList<int> x, IReadOnlyList<int> y)
    {
        if (Fails is not null)
        {
            throw Fails;
        }

        Arrangements.Add(new ArrangementCall([.. devicePaths], [.. x], [.. y]));

        if (IgnoresArrange)
        {
            return;
        }

        for (int index = 0; index < devicePaths.Count; index++)
        {
            FakeMonitor monitor =
                _monitors.FirstOrDefault(candidate => candidate.Path == devicePaths[index] && candidate.Active)
                ?? throw new InvalidOperationException(
                    "A monitor that was meant to be moved is not showing a picture."
                );

            monitor.X = x[index];
            monitor.Y = y[index];
        }
    }
}

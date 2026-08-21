using System.Globalization;
using System.Text.Json;

namespace PCMode.Engine;

/// <summary>
/// Chooses which monitors are on and how they are arranged.
/// </summary>
/// <remarks>
/// LOGICAL NAMES, NOT DEVICE PATHS
///
/// A mode says "TVOnly". What that means on this particular machine is
/// configuration, because a device path is a fact about somebody's hardware that
/// nothing shipped can guess. Modes name intentions; settings say what they mean
/// here. That is the same division the cursor and audio providers already keep.
///
/// TWO PHASES, BECAUSE ONE CANNOT BE COMPUTED UNTIL THE OTHER HAS HAPPENED
///
/// Applying a layout activates the right monitors, and only then positions them:
/// a monitor's size is not knowable until Windows has picked a mode for it, and
/// the arithmetic that places it needs that size. Each phase is skipped when it
/// is already right, which is what makes re-applying a converged layout a genuine
/// no-op rather than a screen that blanks for no reason.
///
/// ACCEPTED IS NOT THE SAME AS FINISHED
///
/// Windows accepts a topology request and then does what it can, over seconds:
/// monitors wake, links are renegotiated, and the configuration this provider can
/// read lags behind the one it asked for. So a readback taken immediately after a
/// request cannot tell a change that failed from a change that has not landed
/// yet, and treating the two the same would mean issuing the whole topology
/// again -- another round of black screens -- for the sole reason that Windows
/// had not finished the first one.
///
/// This provider therefore reports only what it is entitled to be sure of. A call
/// that the API itself refused is a failure and is thrown, with whatever Windows
/// said. A request that was accepted and has not yet arrived is neither: the call
/// returns, and the engine's convergence loop -- which polls, waits and settles,
/// and which a single call cannot do -- decides whether it arrived. That loop is
/// also what catches a layout that was reached and then drifted, which is not
/// visible from inside one call however carefully that call checks itself.
///
/// WHAT IS CHECKED WHEN
///
/// That a layout is well formed and names monitors the settings define is a fact
/// about the document, checked when the configuration is read and again before a
/// transition dispatches. That a monitor is plugged in and awake is a fact about
/// the room, checked only in <see cref="Apply"/>. Refusing a mode at load because
/// a television was off would make the configuration valid or invalid depending
/// on what was switched on at the time.
/// </remarks>
public sealed class DisplayProvider : IConfigurableProvider, IDependentProvider, IConvergentProvider
{
    /// <summary>The configuration key.</summary>
    public const string ResourceKey = "display";

    /// <summary>Where this provider's settings live in the document.</summary>
    public const string SettingsPath = "providers.display";

    /// <summary>Where the logical monitor names live.</summary>
    public const string MonitorsPath = "providers.display.monitors";

    /// <summary>Where the named layouts live.</summary>
    public const string LayoutsPath = "providers.display.layouts";

    /// <summary>
    /// How far a rate may drift and still count as the one that was asked for.
    /// </summary>
    /// <remarks>
    /// Windows commonly reports NTSC-derived rates as N * 1000 / 1001 rather than
    /// the whole number a layout names: 119.88 for 120, 59.94 for 60, 143.856 for
    /// 144. This comfortably covers every such case -- the largest is about
    /// 0.144 Hz out, at 144 -- without conflating two genuinely different rates.
    /// </remarks>
    private const double RefreshTolerance = 0.6;

    private readonly IDisplayDevices _devices;

    private IReadOnlyDictionary<string, string> _monitors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// In configuration order, which is the order <see cref="Read"/> asks in, so
    /// that its answer is the first layout the machine matches rather than
    /// whichever a hash table happened to yield first.
    /// </summary>
    private IReadOnlyList<DisplayLayout> _layouts = [];

    public DisplayProvider()
        : this(new WindowsDisplayDevices())
    {
    }

    public DisplayProvider(IDisplayDevices devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
    }

    public string Key => ResourceKey;

    public string DisplayName => "Display layout";

    /// <summary>
    /// After the television has been switched to the right input and had its HDMI
    /// link configured, when a mode changes those too.
    /// </summary>
    /// <remarks>
    /// A television showing a different input has not yet accepted the machine's
    /// signal, so a topology sent before the switch is negotiated against
    /// something that is about to change underneath it. Windows frequently
    /// recovers, and just as frequently settles on a resolution nobody asked for.
    ///
    /// The signal settings are the sharper version of the same problem. They make
    /// the television re-handshake the link, and a re-handshake while the
    /// television is the only active display leaves Windows with nothing to draw
    /// on -- from which it recovers by restoring the desk monitors itself, undoing
    /// whatever this had just applied. See <see cref="TvSignalProvider"/>.
    ///
    /// An ordering preference and not a requirement, deliberately. Whether a given
    /// layout needs the television at all is a fact about somebody's cabling, and
    /// where a layout does need it, the layout says so itself through 'requires'.
    /// Stating it here as a need would drag the television into every mode that
    /// rearranges two desk monitors.
    /// </remarks>
    public IReadOnlyList<string> RunsAfter => [TvInputProvider.ResourceKey, TvSignalProvider.ResourceKey];

    /// <summary>
    /// Whatever the layout being applied says it needs.
    /// </summary>
    /// <remarks>
    /// Configuration, because the answer is different in every house: which
    /// television, which input, whether there is one. A layout with nothing to
    /// declare requires nothing, which is every layout on a machine with no
    /// television.
    /// </remarks>
    public IReadOnlyList<ResourceRequirement> RequirementsFor(string target) =>
        Find(target)?.Requires ?? [];

    /// <summary>
    /// The slowest thing here, and the one most worth being sure about.
    /// </summary>
    /// <remarks>
    /// A topology change wakes monitors, renegotiates links and moves the
    /// desktop under everything that was on it. Windows returns long before that
    /// has finished, and a television on the end of an HDMI cable can take
    /// several seconds to show a picture, so an attempt is given a long window.
    ///
    /// It settles for well over a second because the readback genuinely wobbles:
    /// monitors report a mode, then report a different one as the link is
    /// negotiated. Everything ordered after the displays -- the pointer, the
    /// sound -- was ordered there because it depends on a desktop that has
    /// stopped moving, and this is where "stopped moving" is actually enforced.
    ///
    /// Three attempts, because re-issuing a topology is disruptive: every one is
    /// another round of black screens for whoever is watching. It is worth doing
    /// when the first did not take, and not worth doing all afternoon.
    /// </remarks>
    public ConvergencePolicy PolicyFor(string target) => new()
    {
        Verify = true,
        MaxAttempts = 3,
        RetryInterval = TimeSpan.FromSeconds(8),
        PollInterval = TimeSpan.FromMilliseconds(500),
        Settle = TimeSpan.FromMilliseconds(1500),
    };

    /// <summary>The configured layout names, in name order.</summary>
    public IReadOnlyList<string> LayoutNames =>
        [.. _layouts.Select(layout => layout.Name).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The configured logical monitor names, in name order.</summary>
    public IReadOnlyList<string> MonitorNames =>
        [.. _monitors.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The configured layouts themselves, in name order -- for a graphical
    /// editor that needs to show a layout's monitors, primary and placements,
    /// not merely its name.
    /// </summary>
    public IReadOnlyList<DisplayLayout> Layouts =>
        [.. _layouts.OrderBy(layout => layout.Name, StringComparer.OrdinalIgnoreCase)];

    public bool TryConfigure(JsonElement? settings, out string? problem)
    {
        // No settings at all is allowed and leaves this provider with nothing it
        // can apply. Displays are optional: a machine with one monitor has no
        // reason to define anything here, and refusing that document would make a
        // feature nobody asked for mandatory.
        if (settings is not JsonElement element || element.ValueKind == JsonValueKind.Null)
        {
            _monitors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _layouts = [];
            problem = null;
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = $"'{SettingsPath}' must be an object.";
            return false;
        }

        var monitors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        JsonElement? layoutsElement = null;

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.NameEquals("monitors"))
            {
                if (!TryReadMonitors(property.Value, monitors, out problem))
                {
                    return false;
                }
            }
            else if (property.NameEquals("layouts"))
            {
                layoutsElement = property.Value;
            }
            else
            {
                problem =
                    $"'{SettingsPath}' does not understand '{property.Name}'. " +
                    "It takes 'monitors' and 'layouts'.";

                return false;
            }
        }

        if (!TryReadLayouts(layoutsElement, monitors, out List<DisplayLayout>? layouts, out problem))
        {
            return false;
        }

        _monitors = monitors;
        _layouts = layouts!;
        problem = null;
        return true;
    }

    public bool IsValidValue(string value, out string? problem)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problem = "the display layout has no name.";
            return false;
        }

        if (_layouts.Count == 0)
        {
            problem =
                $"'{value.Trim()}' cannot be applied because no display layouts are defined. " +
                $"Add one under '{LayoutsPath}'.";

            return false;
        }

        if (Find(value) is null)
        {
            problem =
                $"'{value.Trim()}' is not a display layout. " +
                $"'{LayoutsPath}' defines: {string.Join(", ", LayoutNames)}.";

            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>
    /// The layout the machine is currently in, when it is in one.
    /// </summary>
    /// <remarks>
    /// A layout only reads back as current when exactly its monitors are active,
    /// every one of them sits where it says, and every rate it asks for is being
    /// run. Anything less is a different arrangement that happens to share some
    /// monitors, and reporting it as the layout would make a half-applied desktop
    /// look settled.
    ///
    /// Null covers a machine mid-transition, an arrangement nothing describes,
    /// and displays that could not be read at all. They are folded together
    /// because to the engine null means one thing -- "apply it anyway" -- and
    /// that is the right answer to all three. Telling them apart is what
    /// <see cref="Discover"/> is for.
    /// </remarks>
    public string? Read()
    {
        if (_layouts.Count == 0)
        {
            return null;
        }

        IReadOnlyList<ActiveDisplay> live;

        try
        {
            live = _devices.ActiveDisplays();
        }
        catch (Exception)
        {
            return null;
        }

        foreach (DisplayLayout layout in _layouts)
        {
            if (Difference(layout, live) is null)
            {
                return layout.Name;
            }
        }

        return null;
    }

    public void Apply(string value, CancellationToken cancellationToken)
    {
        DisplayLayout layout = Find(value)
            ?? throw new ArgumentException($"'{value}' is not a display layout.", nameof(value));

        string[] paths = [.. layout.Monitors.Select(monitor => _monitors[monitor])];

        int[] rates =
        [
            .. layout.Monitors.Select(monitor =>
                layout.Refresh.TryGetValue(monitor, out double hz)
                    ? (int)Math.Round(hz, MidpointRounding.AwayFromZero)
                    : 0),
        ];

        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ActiveDisplay> live = _devices.ActiveDisplays();

        // ---- phase one: the right monitors, at the right rates ---------------
        //
        // Skipped when they already are, so a layout that is merely arranged
        // wrongly is repositioned without the screens going black first. A rate
        // that has drifted does force this phase, because the rate is chosen when
        // a monitor is activated and there is nowhere else to ask for it.
        if (TopologyDifference(layout, live) is not null)
        {
            _devices.Activate(paths, rates);
            live = _devices.ActiveDisplays();
        }

        if (TopologyDifference(layout, live) is LayoutDifference topology)
        {
            if (topology.Waiting)
            {
                // Accepted, and still happening. Saying "it did not work" here
                // would make the engine issue the identical topology again in a
                // few seconds, for no better reason than that a television takes
                // longer to wake than an API call takes to return.
                return;
            }

            // Not waiting: the monitors are up and running something other than
            // what the layout asked for. Windows does not refuse an unsupported
            // rate for a topology change -- strict validation is unavailable for
            // one -- it accepts the request and delivers what it can, so reading
            // back is the only place this can be caught, and it will read the
            // same in eight seconds as it does now.
            throw new InvalidOperationException($"Display layout '{layout.Name}' was not applied: {topology.Problem}");
        }

        // ---- phase two: where they sit ---------------------------------------
        //
        // Only computable now: the positions come from the sizes Windows just
        // chose, which is why no layout has to know a monitor's resolution.
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyDictionary<string, (int X, int Y)> expected = layout.Positions(Sizes(layout, live));

        if (ArrangementDifference(layout, live, expected) is not null)
        {
            _devices.Arrange(
                paths,
                [.. layout.Monitors.Select(monitor => expected[monitor].X)],
                [.. layout.Monitors.Select(monitor => expected[monitor].Y)]);

            live = _devices.ActiveDisplays();
        }

        if (Difference(layout, live) is LayoutDifference problem && !problem.Waiting)
        {
            // A position is not negotiated with anything: it is accepted or it is
            // not. So unlike the topology above, a monitor that is not where it
            // was just put is not going to arrive there.
            throw new InvalidOperationException($"Display layout '{layout.Name}' was not applied: {problem.Problem}");
        }
    }

    /// <summary>
    /// What the monitors look like on this machine right now.
    /// </summary>
    /// <remarks>
    /// The one call anything configuring displays needs, and the reason it is
    /// here rather than in a settings screen: which device paths exist, what they
    /// are called, what rates they will take, and whether what is already written
    /// down still resolves are questions this provider answers by the same rules
    /// it applies by. Answering them somewhere else would be a second
    /// implementation of the matching rule, and the two would disagree exactly
    /// when it mattered.
    /// </remarks>
    public DisplaySnapshot Discover()
    {
        IReadOnlyList<DisplayTarget> targets;
        IReadOnlyList<ActiveDisplay> live;
        IReadOnlyDictionary<string, IReadOnlyList<int>> supported;

        try
        {
            targets = _devices.KnownTargets();
            live = _devices.ActiveDisplays();
            supported = _devices.SupportedRefreshRates();
        }
        catch (Exception exception)
        {
            // Unavailable, and said so. Reporting the configured monitors as
            // missing here would be a claim about the machine that nothing
            // actually checked.
            return new DisplaySnapshot(
                Available: false,
                exception.Message,
                [],
                [.. _monitors.Select(monitor =>
                    new DisplayMonitorMapping(monitor.Key, monitor.Value, DisplayMappingState.Unknown))],
                LayoutNames,
                CurrentLayout: null);
        }

        Dictionary<string, ActiveDisplay> activeByPath = ByPath(live);

        Dictionary<string, string> logicalByPath = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string logical, string path) in _monitors)
        {
            logicalByPath[path] = logical;
        }

        var monitors = new List<DisplayMonitorInfo>(targets.Count);

        foreach (DisplayTarget target in targets)
        {
            activeByPath.TryGetValue(target.DevicePath, out ActiveDisplay? display);

            monitors.Add(new DisplayMonitorInfo(
                target.FriendlyName,
                target.DevicePath,
                display is not null,
                display?.IsPrimary ?? false,
                display?.X ?? 0,
                display?.Y ?? 0,
                display?.Width ?? 0,
                display?.Height ?? 0,
                display?.RefreshHz ?? 0,
                supported.TryGetValue(target.DevicePath, out IReadOnlyList<int>? rates) ? rates : [],
                logicalByPath.TryGetValue(target.DevicePath, out string? logical) ? logical : null));
        }

        var known = new HashSet<string>(
            targets.Select(target => target.DevicePath),
            StringComparer.OrdinalIgnoreCase);

        var mappings = new List<DisplayMonitorMapping>(_monitors.Count);

        foreach ((string logical, string path) in _monitors)
        {
            mappings.Add(new DisplayMonitorMapping(
                logical,
                path,
                known.Contains(path) ? DisplayMappingState.Ready : DisplayMappingState.Missing));
        }

        string? current = _layouts.FirstOrDefault(layout => Difference(layout, live) is null)?.Name;

        return new DisplaySnapshot(Available: true, Problem: null, monitors, mappings, LayoutNames, current);
    }

    /// <summary>The layout of that name, matched the way a mode names one.</summary>
    private DisplayLayout? Find(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : _layouts.FirstOrDefault(layout =>
                string.Equals(layout.Name, value.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Everything that is not as the layout says, or null when nothing is.
    /// </summary>
    /// <remarks>
    /// One implementation, three callers: the readback asks whether it is null,
    /// discovery asks the same, and applying uses the sentence it returns as the
    /// reason a transition failed. A separate "is it current" check would be a
    /// second opinion about the same question, and the two would eventually
    /// disagree.
    /// </remarks>
    private LayoutDifference? Difference(DisplayLayout layout, IReadOnlyList<ActiveDisplay> live)
    {
        if (TopologyDifference(layout, live) is LayoutDifference topology)
        {
            return topology;
        }

        return ArrangementDifference(layout, live, layout.Positions(Sizes(layout, live)));
    }

    /// <summary>
    /// Whether exactly the layout's monitors are on, at the rates it asks for.
    /// </summary>
    private LayoutDifference? TopologyDifference(DisplayLayout layout, IReadOnlyList<ActiveDisplay> live)
    {
        Dictionary<string, ActiveDisplay> activeByPath = ByPath(live);

        foreach (string monitor in layout.Monitors)
        {
            if (!_monitors.TryGetValue(monitor, out string? path))
            {
                // Refused when the configuration is read, so this is unreachable
                // through the engine. Kept so a direct caller cannot get a
                // confident answer about a layout that names nothing. Nothing
                // about it will change by waiting.
                return new LayoutDifference($"'{monitor}' has no entry under '{MonitorsPath}'.", Waiting: false);
            }

            if (!activeByPath.TryGetValue(path, out ActiveDisplay? display))
            {
                // The commonest thing there is, and the one that must never be
                // called a failure on the spot: a television on the end of an
                // HDMI cable takes seconds to show a picture, and the request to
                // wake it has already been accepted.
                return new LayoutDifference($"'{monitor}' is not showing a picture.", Waiting: true);
            }

            if (layout.Refresh.TryGetValue(monitor, out double wanted) &&
                Math.Abs(display.RefreshHz - wanted) > RefreshTolerance)
            {
                // The monitor is up, so Windows has already chosen a mode for it,
                // and this is the mode it chose. Reported as settled: an
                // unsupported rate reads the same in ten seconds as it does now,
                // and calling it "still arriving" would turn the most useful
                // sentence this provider produces into a transition that
                // eventually times out without saying why.
                return new LayoutDifference(
                    $"'{monitor}' is running at {Hz(display.RefreshHz)}Hz, not the {Hz(wanted)}Hz the " +
                    "layout asks for. It may not support that rate at its current resolution.",
                    Waiting: false);
            }
        }

        // Exactly this set: a layout is not what the machine is in while a
        // monitor nobody asked for is also lit up.
        if (live.Count != layout.Monitors.Count)
        {
            var wanted = new HashSet<string>(
                layout.Monitors.Select(monitor => _monitors[monitor]),
                StringComparer.OrdinalIgnoreCase);

            IEnumerable<string> extra = live
                .Where(display => !wanted.Contains(display.DevicePath))
                .Select(display => $"'{display.FriendlyName}'");

            // A monitor being torn down is as gradual as one being brought up.
            return new LayoutDifference(
                $"{string.Join(", ", extra)} is also showing a picture, and the layout does not include it.",
                Waiting: true);
        }

        return null;
    }

    /// <summary>Whether every monitor sits where the layout puts it.</summary>
    private LayoutDifference? ArrangementDifference(
        DisplayLayout layout,
        IReadOnlyList<ActiveDisplay> live,
        IReadOnlyDictionary<string, (int X, int Y)> expected)
    {
        Dictionary<string, ActiveDisplay> activeByPath = ByPath(live);

        foreach (string monitor in layout.Monitors)
        {
            ActiveDisplay display = activeByPath[_monitors[monitor]];
            (int x, int y) = expected[monitor];

            if (display.X != x || display.Y != y)
            {
                return new LayoutDifference(
                    $"'{monitor}' is at ({display.X}, {display.Y}) rather than ({x}, {y})" +
                    (string.Equals(monitor, layout.Primary, StringComparison.OrdinalIgnoreCase)
                        ? ", so it is not the primary display."
                        : "."),
                    Waiting: false);
            }
        }

        return null;
    }

    /// <summary>
    /// Something the layout asks for that the machine is not doing, and whether
    /// that is still true in ten seconds.
    /// </summary>
    /// <param name="Waiting">
    /// True when the machine is on its way and the difference is a stage of
    /// getting there rather than a verdict about it. This is the whole
    /// distinction that stops a transition from re-issuing a topology, and every
    /// round of black screens that goes with it, because a television was slow.
    /// </param>
    private sealed record LayoutDifference(string Problem, bool Waiting);

    /// <summary>
    /// Each layout monitor's live size, which is where its position comes from.
    /// </summary>
    /// <remarks>
    /// Only ever called once the topology has been established, so every monitor
    /// is present. That ordering is the point: a size is not knowable until
    /// Windows has picked a mode for the monitor.
    /// </remarks>
    private Dictionary<string, (int Width, int Height)> Sizes(
        DisplayLayout layout,
        IReadOnlyList<ActiveDisplay> live)
    {
        Dictionary<string, ActiveDisplay> activeByPath = ByPath(live);

        var sizes = new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);

        foreach (string monitor in layout.Monitors)
        {
            ActiveDisplay display = activeByPath[_monitors[monitor]];
            sizes[monitor] = (display.Width, display.Height);
        }

        return sizes;
    }

    private static Dictionary<string, ActiveDisplay> ByPath(IReadOnlyList<ActiveDisplay> live)
    {
        var byPath = new Dictionary<string, ActiveDisplay>(StringComparer.OrdinalIgnoreCase);

        foreach (ActiveDisplay display in live)
        {
            byPath[display.DevicePath] = display;
        }

        return byPath;
    }

    /// <summary>A rate as somebody would say it: 60, not 59.94021.</summary>
    private static string Hz(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool TryReadMonitors(
        JsonElement element,
        Dictionary<string, string> monitors,
        out string? problem)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            problem =
                $"'{MonitorsPath}' must be an object mapping a name you choose to the exact " +
                "device path of a monitor.";

            return false;
        }

        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty monitor in element.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(monitor.Name))
            {
                problem = $"A monitor in '{MonitorsPath}' has no name.";
                return false;
            }

            string named = monitor.Name.Trim();

            if (monitor.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(monitor.Value.GetString()))
            {
                // An empty mapping is worse than a missing one: the logical name
                // exists, so every layout using it validates, and the failure
                // waits until a transition is already under way.
                problem =
                    $"'{MonitorsPath}.{named}' is empty. It needs the exact device path of a " +
                    "monitor, which discovery reports.";

                return false;
            }

            // Only the surrounding whitespace, which is what a copied value picks
            // up. Nothing about the path itself is interpreted: it is matched
            // against what Windows reports.
            string path = monitor.Value.GetString()!.Trim();

            if (!monitors.TryAdd(named, path))
            {
                // Case-insensitively, because that is how a layout looks one up.
                // Two entries differing only in case means one of them can never
                // be named and nothing would say which.
                problem = $"'{MonitorsPath}' defines '{named}' more than once.";
                return false;
            }

            if (!byPath.TryAdd(path, named))
            {
                // The same monitor under two names. Every layout using the second
                // one would be indistinguishable from the same layout using the
                // first, so nothing could say which of them the machine is in.
                problem =
                    $"'{MonitorsPath}' points both '{byPath[path]}' and '{named}' at the same " +
                    "monitor. One monitor cannot be two.";

                return false;
            }
        }

        problem = null;
        return true;
    }

    private static bool TryReadLayouts(
        JsonElement? element,
        Dictionary<string, string> monitors,
        out List<DisplayLayout>? layouts,
        out string? problem)
    {
        layouts = null;

        var parsed = new List<DisplayLayout>();

        if (element is JsonElement declared)
        {
            if (declared.ValueKind != JsonValueKind.Object)
            {
                problem = $"'{LayoutsPath}' must be an object of named layouts.";
                return false;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var shapes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (JsonProperty layout in declared.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(layout.Name))
                {
                    problem = $"A layout in '{LayoutsPath}' has no name.";
                    return false;
                }

                string named = layout.Name.Trim();

                if (!names.Add(named))
                {
                    problem = $"'{LayoutsPath}' defines '{named}' more than once.";
                    return false;
                }

                if (!DisplayLayout.TryParse(named, layout.Value, out DisplayLayout? read, out problem))
                {
                    return false;
                }

                foreach (string monitor in read!.Monitors)
                {
                    if (!monitors.ContainsKey(monitor))
                    {
                        // A layout is only applicable if its monitors resolve to
                        // something. This is about the document rather than the
                        // machine, so it is refused now rather than at the moment
                        // a mode is selected.
                        problem =
                            $"Display layout '{named}' names monitor '{monitor}', but " +
                            $"'{MonitorsPath}' has no entry for it.";

                        return false;
                    }
                }

                if (!shapes.TryAdd(Shape(read), named))
                {
                    // Two names for one arrangement. The readback would have to
                    // pick one of them, and whichever it picked would make the
                    // other report as not current no matter what the machine did.
                    problem =
                        $"'{LayoutsPath}' defines '{shapes[Shape(read)]}' and '{named}' as the same " +
                        "arrangement, so nothing could say which of them the machine is in.";

                    return false;
                }

                parsed.Add(read);
            }
        }

        layouts = parsed;
        problem = null;
        return true;
    }

    /// <summary>
    /// A layout reduced to what it actually asks of the machine, so that two
    /// layouts asking for the identical thing can be told apart from two that
    /// merely look similar.
    /// </summary>
    private static string Shape(DisplayLayout layout) => string.Join(
        '|',
        string.Join(',', layout.Monitors.Order(StringComparer.OrdinalIgnoreCase)),
        layout.Primary,
        string.Join(
            ',',
            layout.Placements
                .Select(placement => $"{placement.Key} {placement.Value.Direction} {placement.Value.Anchor}")
                .Order(StringComparer.OrdinalIgnoreCase)),
        string.Join(
            ',',
            layout.Refresh
                .Select(rate => $"{rate.Key} {Hz(rate.Value)}")
                .Order(StringComparer.OrdinalIgnoreCase)));
}

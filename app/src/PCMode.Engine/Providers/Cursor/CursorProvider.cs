using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PCMode.Engine;

/// <summary>A rectangle in screen pixels.</summary>
public readonly record struct DisplayRect(int Left, int Top, int Width, int Height);

/// <summary>
/// Where the screen is, behind a seam so tests need no screen.
/// </summary>
/// <remarks>
/// Primary only, and that is a decision rather than an oversight. Choosing a
/// non-primary monitor sensibly means naming it, and a monitor cannot be named
/// until something discovers monitors -- which is the display provider, a later
/// step. Until then the cursor works on its own, which is the point: a mode that
/// parks the pointer must not wait for a feature that does not exist yet.
/// </remarks>
public interface IDisplaySurface
{
    /// <summary>
    /// The primary display's bounds, in the same coordinates the cursor uses.
    /// </summary>
    /// <remarks>Throws when the screen cannot be measured.</remarks>
    DisplayRect PrimaryBounds();
}

/// <summary>
/// Moving the pointer, behind a seam so tests never move the real one.
/// </summary>
/// <remarks>
/// The reason this interface exists at all. A test that exercised the real
/// implementation would take the mouse away from whoever was using the machine,
/// which is not a thing a test suite is allowed to do.
/// </remarks>
public interface ICursorDevice
{
    /// <summary>Puts the pointer at a screen coordinate. Throws on failure.</summary>
    void MoveTo(int x, int y);
}

/// <summary>
/// One named place to park the pointer.
/// </summary>
/// <remarks>
/// Each axis has exactly one rule: a fraction of the display, or a pixel offset
/// from the far edge. Percent is for places that should stay put when the
/// resolution changes ("the middle"); an edge offset is for tucking the pointer
/// out of shot, where what matters is the distance from the corner and not the
/// screen's size.
/// </remarks>
public sealed record CursorPosition(
    string Name,
    double? XPercent,
    int? XFromRight,
    double? YPercent,
    int? YFromBottom)
{
    /// <summary>The screen coordinate this position means on a given display.</summary>
    public (int X, int Y) Resolve(DisplayRect bounds)
    {
        int x = XPercent is double xPercent
            ? bounds.Left + (int)Math.Round(bounds.Width * xPercent / 100.0, MidpointRounding.AwayFromZero)
            : bounds.Left + bounds.Width - XFromRight!.Value;

        int y = YPercent is double yPercent
            ? bounds.Top + (int)Math.Round(bounds.Height * yPercent / 100.0, MidpointRounding.AwayFromZero)
            : bounds.Top + bounds.Height - YFromBottom!.Value;

        // Clamped to a pixel that is actually on this display. Both rules can
        // land one past the far edge -- 100 percent, or an offset larger than
        // the screen -- and a pointer one pixel outside the primary display is
        // not a near miss: on a multi-monitor desktop it is on the next monitor,
        // which looks like the position being wrong rather than out of range.
        return (
            Math.Clamp(x, bounds.Left, bounds.Left + bounds.Width - 1),
            Math.Clamp(y, bounds.Top, bounds.Top + bounds.Height - 1));
    }
}

/// <summary>
/// Parks the pointer somewhere sensible for the mode.
/// </summary>
/// <remarks>
/// APPLIED ONCE, AND NEVER DEFENDED
///
/// <see cref="Read"/> answers null on purpose, so the engine always dispatches
/// and never compares. A cursor position read back is true for an instant: the
/// user moves the mouse a moment later and it is not where the mode put it, and
/// that is entirely correct behaviour by the user. A provider that reported the
/// pointer as out of place would turn every mouse movement into a resource that
/// looks unsatisfied, and anything built on top of that would end up fighting
/// the person holding the mouse for control of their own pointer.
///
/// So this parks the pointer when the mode is applied and then leaves it alone.
/// It is genuinely a resource -- it is planned, it is reported, and it fails
/// honestly if the pointer could not be moved -- but it is not one that has a
/// current value worth asking about.
/// </remarks>
public sealed class CursorProvider : IConfigurableProvider, IDependentProvider, IConvergentProvider
{
    /// <summary>The configuration key.</summary>
    public const string ResourceKey = "cursor";

    /// <summary>Where this provider's settings live in the document.</summary>
    public const string SettingsPath = "providers.cursor.positions";

    private readonly ICursorDevice _cursor;
    private readonly IDisplaySurface _display;

    private IReadOnlyDictionary<string, CursorPosition> _positions =
        new Dictionary<string, CursorPosition>(StringComparer.OrdinalIgnoreCase);

    public CursorProvider()
        : this(new WindowsCursorDevice(), new WindowsPrimaryDisplay())
    {
    }

    public CursorProvider(ICursorDevice cursor, IDisplaySurface display)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(display);

        _cursor = cursor;
        _display = display;
    }

    public string Key => ResourceKey;

    public string DisplayName => "Cursor position";

    /// <summary>
    /// After the displays, when a mode changes those too.
    /// </summary>
    /// <remarks>
    /// A position is worked out from the desktop as it is at the moment it is
    /// applied, and the desktop is a different shape once a monitor has come or
    /// gone. Parking the pointer first would put it in the corner of a screen
    /// that is about to stop existing -- and Windows would then move it
    /// somewhere of its own choosing, which is the one outcome this provider is
    /// for avoiding.
    ///
    /// An ordering preference and not a requirement: a mode that only moves the
    /// pointer must not start rearranging monitors to satisfy it.
    /// </remarks>
    public IReadOnlyList<string> RunsAfter => [DisplayProvider.ResourceKey];

    /// <summary>
    /// Dispatched once, and never chased.
    /// </summary>
    /// <remarks>
    /// Stated rather than inherited, because this is the provider the decision
    /// is actually about. <see cref="Read"/> answers null by design (see the
    /// class remarks), and the tempting reading of that -- "it never confirms,
    /// so try again" -- is exactly wrong: nothing will ever confirm it, so
    /// retrying would mean retrying forever, and every retry would be another
    /// grab at a pointer whose owner has since moved it somewhere they wanted
    /// it.
    ///
    /// If the move itself fails, that is a real failure and it is reported as
    /// one on the first attempt. What is refused here is treating an absent
    /// readback as evidence of anything at all.
    /// </remarks>
    public ConvergencePolicy PolicyFor(string target) => ConvergencePolicy.DispatchOnce;

    /// <summary>The configured position names, in name order.</summary>
    public IReadOnlyList<string> PositionNames =>
        [.. _positions.Keys.Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The configured positions themselves, in name order -- for a graphical
    /// editor that needs to show each position's rule, not merely its name.
    /// </summary>
    public IReadOnlyList<CursorPosition> Positions =>
        [.. _positions.Values.OrderBy(position => position.Name, StringComparer.OrdinalIgnoreCase)];

    public bool TryConfigure(JsonElement? settings, out string? problem)
    {
        // No settings at all is allowed, and leaves this provider with nothing
        // it can apply. That is not a failure on its own: it is only a problem
        // if a mode then names a position, and that is refused by name when the
        // mode is validated, a few lines later and with the mode to blame.
        if (settings is not JsonElement element || element.ValueKind == JsonValueKind.Null)
        {
            _positions = new Dictionary<string, CursorPosition>(StringComparer.OrdinalIgnoreCase);
            problem = null;
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = "'providers.cursor' must be an object.";
            return false;
        }

        var parsed = new Dictionary<string, CursorPosition>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!property.NameEquals("positions"))
            {
                problem =
                    $"'providers.cursor' does not understand '{property.Name}'. It takes 'positions'.";

                return false;
            }

            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                problem = $"'{SettingsPath}' must be an object of named positions.";
                return false;
            }

            foreach (JsonProperty position in property.Value.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(position.Name))
                {
                    problem = $"A position in '{SettingsPath}' has no name.";
                    return false;
                }

                if (!TryReadPosition(position.Name.Trim(), position.Value, out CursorPosition? read, out problem))
                {
                    return false;
                }

                if (!parsed.TryAdd(read!.Name, read))
                {
                    // Case-insensitively, because that is how a mode looks one
                    // up. Two entries differing only in case means one of them
                    // can never be selected and nothing would say which.
                    problem = $"'{SettingsPath}' defines '{read.Name}' more than once.";
                    return false;
                }
            }
        }

        _positions = parsed;
        problem = null;
        return true;
    }

    public bool IsValidValue(string value, out string? problem)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problem = "the cursor position has no name.";
            return false;
        }

        if (_positions.Count == 0)
        {
            problem =
                $"'{value.Trim()}' cannot be applied because no cursor positions are defined. " +
                $"Add one under '{SettingsPath}'.";

            return false;
        }

        if (!_positions.ContainsKey(value.Trim()))
        {
            problem =
                $"'{value.Trim()}' is not a cursor position. " +
                $"'{SettingsPath}' defines: {string.Join(", ", PositionNames)}.";

            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>Always null. See the class remarks: this is deliberate.</summary>
    public string? Read() => null;

    public void Apply(string value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !_positions.TryGetValue(value.Trim(), out CursorPosition? position))
        {
            // Unreachable through the engine, which validates first. Kept so a
            // direct caller cannot park the pointer at a place nobody defined.
            throw new ArgumentException($"'{value}' is not a cursor position.", nameof(value));
        }

        cancellationToken.ThrowIfCancellationRequested();

        DisplayRect bounds = _display.PrimaryBounds();

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException(
                $"The display reported no size ({bounds.Width}x{bounds.Height}), " +
                "so there is nowhere to put the pointer.");
        }

        (int x, int y) = position.Resolve(bounds);

        _cursor.MoveTo(x, y);
    }

    /// <summary>
    /// Reads one position, insisting on exactly one rule per axis.
    /// </summary>
    /// <remarks>
    /// A position needs an X rule and a Y rule, and refusing the ones that do
    /// not have both is the whole reason this check is separate. An entry
    /// missing an axis has an obvious-looking default -- zero, the top-left
    /// corner -- which is indistinguishable from somebody deliberately parking
    /// the pointer there, so it can sit in a configuration being wrong for
    /// months without ever looking wrong.
    ///
    /// Two rules for one axis are refused for the neighbouring reason: one of
    /// them is being ignored and nothing says which.
    /// </remarks>
    private static bool TryReadPosition(
        string name,
        JsonElement element,
        out CursorPosition? position,
        out string? problem)
    {
        position = null;
        problem = null;

        string where = $"'{SettingsPath}.{name}'";

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = $"{where} must be an object, for example {{ \"xPercent\": 50, \"yPercent\": 50 }}.";
            return false;
        }

        double? xPercent = null;
        double? yPercent = null;
        int? xFromRight = null;
        int? yFromBottom = null;

        foreach (JsonProperty rule in element.EnumerateObject())
        {
            if (rule.Value.ValueKind != JsonValueKind.Number)
            {
                problem = $"{where} gives '{rule.Name}' a value that is not a number.";
                return false;
            }

            if (rule.NameEquals("xPercent"))
            {
                if (!TryPercent(where, rule, out xPercent, out problem))
                {
                    return false;
                }
            }
            else if (rule.NameEquals("yPercent"))
            {
                if (!TryPercent(where, rule, out yPercent, out problem))
                {
                    return false;
                }
            }
            else if (rule.NameEquals("xFromRight"))
            {
                if (!TryOffset(where, rule, out xFromRight, out problem))
                {
                    return false;
                }
            }
            else if (rule.NameEquals("yFromBottom"))
            {
                if (!TryOffset(where, rule, out yFromBottom, out problem))
                {
                    return false;
                }
            }
            else
            {
                problem =
                    $"{where} does not understand '{rule.Name}'. " +
                    "Use xPercent or xFromRight, and yPercent or yFromBottom.";

                return false;
            }
        }

        if (xPercent is not null && xFromRight is not null)
        {
            problem = $"{where} sets both xPercent and xFromRight. It needs one of them, not both.";
            return false;
        }

        if (yPercent is not null && yFromBottom is not null)
        {
            problem = $"{where} sets both yPercent and yFromBottom. It needs one of them, not both.";
            return false;
        }

        if (xPercent is null && xFromRight is null)
        {
            problem = $"{where} needs either xPercent or xFromRight.";
            return false;
        }

        if (yPercent is null && yFromBottom is null)
        {
            problem = $"{where} needs either yPercent or yFromBottom.";
            return false;
        }

        position = new CursorPosition(name, xPercent, xFromRight, yPercent, yFromBottom);
        return true;
    }

    private static bool TryPercent(string where, JsonProperty rule, out double? value, out string? problem)
    {
        value = null;

        if (!rule.Value.TryGetDouble(out double read) || double.IsNaN(read) || read < 0 || read > 100)
        {
            problem =
                $"{where} sets {rule.Name} to " +
                $"{rule.Value.GetRawText()}, which is not a percentage between 0 and 100.";

            return false;
        }

        value = read;
        problem = null;
        return true;
    }

    private static bool TryOffset(string where, JsonProperty rule, out int? value, out string? problem)
    {
        value = null;

        if (!rule.Value.TryGetInt32(out int read) || read < 0)
        {
            problem =
                $"{where} sets {rule.Name} to " +
                $"{rule.Value.GetRawText()}, which is not a whole number of pixels from the edge.";

            return false;
        }

        value = read;
        problem = null;
        return true;
    }
}

/// <summary>
/// The real pointer.
/// </summary>
/// <remarks>
/// SetCursorPos takes physical screen pixels, which is the same space
/// <see cref="WindowsPrimaryDisplay"/> measures in, because the shell declares
/// itself per-monitor DPI aware. If it ever stopped doing that, Windows would
/// begin virtualising both calls and they would still agree with each other --
/// which is why the two live next to each other rather than being sourced
/// separately.
/// </remarks>
public sealed class WindowsCursorDevice : ICursorDevice
{
    public void MoveTo(int x, int y)
    {
        if (!SetCursorPos(x, y))
        {
            throw new InvalidOperationException(
                $"Windows refused to move the pointer to {x.ToString(CultureInfo.InvariantCulture)}, " +
                $"{y.ToString(CultureInfo.InvariantCulture)} " +
                $"(error {Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture)}).");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}

/// <summary>The real primary display.</summary>
/// <remarks>
/// The primary monitor's origin is (0, 0) by definition, so the offsets are
/// carried rather than assumed away: the arithmetic then reads the same whether
/// the rectangle came from here or from a monitor discovered later.
/// </remarks>
public sealed class WindowsPrimaryDisplay : IDisplaySurface
{
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    public DisplayRect PrimaryBounds()
    {
        int width = GetSystemMetrics(SmCxScreen);
        int height = GetSystemMetrics(SmCyScreen);

        if (width <= 0 || height <= 0)
        {
            // GetSystemMetrics answers zero rather than failing when there is
            // no usable display -- a session with no monitor attached, for
            // instance. Refusing here means the mode reports that it could not
            // park the pointer, instead of parking it at (0, 0).
            throw new InvalidOperationException(
                "Windows reported no primary display, so there is nowhere to put the pointer.");
        }

        return new DisplayRect(0, 0, width, height);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

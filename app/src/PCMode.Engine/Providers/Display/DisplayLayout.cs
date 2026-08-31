using System.Globalization;
using System.Text.Json;

namespace PCMode.Engine;

/// <summary>Where one monitor sits, relative to another.</summary>
public enum DisplayDirection
{
    LeftOf,
    RightOf,
    Above,
    Below,
}

/// <summary>
/// Where a monitor sits along the edge it shares with its anchor -- the axis
/// <see cref="DisplayDirection"/> does not already pin down. <see cref="Start"/>
/// is the top edge for <see cref="DisplayDirection.LeftOf"/>/<see cref="DisplayDirection.RightOf"/>
/// and the left edge for <see cref="DisplayDirection.Above"/>/<see cref="DisplayDirection.Below"/>
/// -- which is also exactly what every layout written before this existed
/// already meant, so it is the default.
/// </summary>
public enum DisplayAlignment
{
    Start,
    Center,
    End,
}

/// <summary>
/// One monitor's place in a layout: a direction, what it is next to, and where
/// along that shared edge it sits.
/// </summary>
/// <param name="Alignment">
/// Named the way dragging one monitor onto another in the visual arrangement
/// snaps it -- flush with the near edge, centered, or flush with the far edge
/// -- rather than as a raw number, so a layout stays exactly as intended when
/// the anchor's resolution later changes and its edge moves.
/// </param>
/// <param name="Offset">
/// A further nudge, in pixels, applied after <paramref name="Alignment"/> --
/// how a drag that does not land on one of the three named alignments is
/// still represented exactly, and how a precise pixel adjustment is made
/// without abandoning the resolution-independent alignment it is relative to.
/// </param>
public sealed record DisplayPlacement(
    DisplayDirection Direction,
    string Anchor,
    DisplayAlignment Alignment = DisplayAlignment.Start,
    int Offset = 0);

/// <summary>
/// One named arrangement of monitors: which are on, which is primary, and where
/// each of the others sits.
/// </summary>
/// <remarks>
/// NO PIXEL COORDINATES, EVER
///
/// A layout says "the second monitor is to the right of the first", never "the
/// second monitor is at 2560, 0". Windows defines the primary display as the one
/// whose source is at the virtual desktop's origin, so the primary IS the origin
/// here and every other monitor's position is derived by walking outward from it
/// using the sizes the monitors actually report. That is what lets a layout
/// survive a resolution change, and why no monitor's size appears in the
/// configuration.
///
/// PURE, AND DELIBERATELY SO
///
/// Nothing in here touches Windows. The same object answers "is this layout
/// well-formed?" when the configuration is read and "where does each monitor go?"
/// when one is applied, so a layout can never be accepted by one and rejected by
/// the other.
/// </remarks>
public sealed class DisplayLayout
{
    /// <summary>
    /// The direction words, in their canonical spelling.
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively but reported back in this spelling, so an error
    /// message shows the form the documentation uses rather than whatever was
    /// typed.
    /// </remarks>
    private static readonly string[] Directions = ["LeftOf", "RightOf", "Above", "Below"];

    private DisplayLayout(
        string name,
        IReadOnlyList<string> monitors,
        string primary,
        IReadOnlyDictionary<string, DisplayPlacement> placements,
        IReadOnlyDictionary<string, double> refresh,
        IReadOnlyList<string> order,
        IReadOnlyList<ResourceRequirement> requires)
    {
        Name = name;
        Monitors = monitors;
        Primary = primary;
        Placements = placements;
        Refresh = refresh;
        Order = order;
        Requires = requires;
    }

    /// <summary>The name a mode asks for.</summary>
    public string Name { get; }

    /// <summary>The logical monitor names this layout switches on, as written.</summary>
    public IReadOnlyList<string> Monitors { get; }

    /// <summary>The one that sits at the origin.</summary>
    public string Primary { get; }

    /// <summary>Where each non-primary monitor sits, keyed by monitor.</summary>
    public IReadOnlyDictionary<string, DisplayPlacement> Placements { get; }

    /// <summary>The Hz each monitor was asked to run at. A monitor absent from
    /// this has no rate requested, and whatever Windows chooses is accepted.</summary>
    public IReadOnlyDictionary<string, double> Refresh { get; }

    /// <summary>
    /// The monitors in dependency order: the primary first, and every other one
    /// after whatever it is placed relative to.
    /// </summary>
    public IReadOnlyList<string> Order { get; }

    /// <summary>
    /// What else has to be true before this layout means anything.
    /// </summary>
    /// <remarks>
    /// Empty for almost every layout, and the reason it exists for the others: a
    /// layout that lights up a television is asking Windows to drive a screen that
    /// is switched off and showing somebody else's set-top box. Which television,
    /// which input, and indeed whether there is one, are facts about one house's
    /// cabling, so they are written beside the layout rather than guessed at here.
    /// See <see cref="TargetRequirements"/>.
    /// </remarks>
    public IReadOnlyList<ResourceRequirement> Requires { get; }

    /// <summary>
    /// Where every monitor goes, given how big each of them currently is.
    /// </summary>
    /// <param name="sizes">
    /// Live sizes keyed by logical monitor name, matched case-insensitively. Must
    /// contain every monitor in this layout; the caller has already established
    /// that they are all active.
    /// </param>
    /// <remarks>
    /// The primary is at (0, 0) because that is precisely what Windows means by
    /// primary. Anything to its left or above it therefore gets a negative
    /// coordinate, which is a normal virtual desktop and is deliberately not
    /// normalised back into positive space -- doing that would move the primary
    /// off the origin and it would stop being the primary.
    ///
    /// <see cref="DisplayDirection"/> fixes the coordinate along the axis the
    /// two monitors touch on; <see cref="DisplayPlacement.Alignment"/> and
    /// <see cref="DisplayPlacement.Offset"/> fix the other one, computed from
    /// the anchor's and the monitor's own live sizes so that, like the touching
    /// axis, an alignment survives a resolution change exactly as intended --
    /// only a raw <see cref="DisplayPlacement.Offset"/> is a pixel count that
    /// does not move with a resized anchor.
    /// </remarks>
    public IReadOnlyDictionary<string, (int X, int Y)> Positions(
        IReadOnlyDictionary<string, (int Width, int Height)> sizes)
    {
        ArgumentNullException.ThrowIfNull(sizes);

        var positions = new Dictionary<string, (int X, int Y)>(StringComparer.OrdinalIgnoreCase)
        {
            [Primary] = (0, 0),
        };

        foreach (string monitor in Order.Skip(1))
        {
            DisplayPlacement placement = Placements[monitor];

            (int anchorX, int anchorY) = positions[placement.Anchor];
            (int anchorWidth, int anchorHeight) = sizes[placement.Anchor];
            (int width, int height) = sizes[monitor];

            int along(int anchorSize, int monitorSize) => placement.Alignment switch
            {
                DisplayAlignment.Center => (anchorSize - monitorSize) / 2,
                DisplayAlignment.End => anchorSize - monitorSize,
                _ => 0,
            } + placement.Offset;

            positions[monitor] = placement.Direction switch
            {
                DisplayDirection.RightOf => (anchorX + anchorWidth, anchorY + along(anchorHeight, height)),
                DisplayDirection.LeftOf => (anchorX - width, anchorY + along(anchorHeight, height)),
                DisplayDirection.Below => (anchorX + along(anchorWidth, width), anchorY + anchorHeight),
                _ => (anchorX + along(anchorWidth, width), anchorY - height),
            };
        }

        return positions;
    }

    /// <summary>
    /// Reads one layout, refusing every way it can be malformed.
    /// </summary>
    /// <remarks>
    /// Every check here is about the document alone -- names, membership, the
    /// shape of the placement graph, whether a Hz value is a number. Whether the
    /// monitors exist on this machine is a separate question, asked when the
    /// layout is applied, because a monitor that is switched off must not make
    /// the configuration invalid.
    /// </remarks>
    public static bool TryParse(
        string name,
        JsonElement element,
        out DisplayLayout? layout,
        out string? problem)
    {
        layout = null;

        string where = $"Display layout '{name}'";

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = $"{where} must be an object with a 'monitors' list.";
            return false;
        }

        List<string>? monitors = null;
        string? primary = null;
        List<string> placementEntries = [];
        List<string> refreshEntries = [];
        IReadOnlyList<ResourceRequirement> requires = [];

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.NameEquals("monitors"))
            {
                if (!TryReadStrings(where, property, out monitors, out problem))
                {
                    return false;
                }
            }
            else if (property.NameEquals("primary"))
            {
                if (property.Value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    problem = $"{where} has a 'primary' that does not name a monitor.";
                    return false;
                }

                primary = property.Value.GetString()!.Trim();
            }
            else if (property.NameEquals("placement"))
            {
                if (!TryReadStrings(where, property, out List<string>? entries, out problem))
                {
                    return false;
                }

                placementEntries = entries!;
            }
            else if (property.NameEquals("refresh"))
            {
                if (!TryReadStrings(where, property, out List<string>? entries, out problem))
                {
                    return false;
                }

                refreshEntries = entries!;
            }
            else if (property.NameEquals(TargetRequirements.FieldName))
            {
                if (!TargetRequirements.TryParse(
                        where,
                        DisplayProvider.ResourceKey,
                        property.Value,
                        out IReadOnlyList<ResourceRequirement>? declared,
                        out problem))
                {
                    return false;
                }

                if (declared!.Any(requirement =>
                    string.Equals(requirement.Key, CursorProvider.ResourceKey, StringComparison.OrdinalIgnoreCase)))
                {
                    // CursorProvider.RunsAfter always names the display: the
                    // pointer is placed once the desktop has finished changing
                    // shape. A layout that required the cursor would need it
                    // BEFORE the display too, which the planner can never
                    // satisfy -- see TransitionPlan.TryOrder. The association
                    // still exists; it just belongs to whichever Space,
                    // Activity or Source sets this layout, the same place a
                    // mode already sets 'cursor' directly.
                    problem =
                        $"{where} requires 'cursor', but the cursor always runs after the display layout, " +
                        "so a layout can never require it. Set 'cursor' on the Space, Activity or Source " +
                        "that uses this layout instead.";

                    return false;
                }

                requires = declared!;
            }
            else
            {
                problem =
                    $"{where} does not understand '{property.Name}'. " +
                    "It takes 'monitors', 'primary', 'placement', 'refresh' and 'requires'.";

                return false;
            }
        }

        if (monitors is null || monitors.Count == 0)
        {
            problem = $"{where} lists no monitors. Add \"monitors\": [\"SomeMonitor\"].";
            return false;
        }

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string monitor in monitors)
        {
            if (!known.Add(monitor))
            {
                problem = $"{where} lists monitor '{monitor}' more than once.";
                return false;
            }
        }

        // A single-monitor layout should not have to state the obvious.
        primary ??= monitors[0];

        if (!known.Contains(primary))
        {
            problem =
                $"{where} makes '{primary}' primary, which is not one of its monitors " +
                $"({string.Join(", ", monitors)}).";

            return false;
        }

        if (!TryReadPlacements(where, placementEntries, out Dictionary<string, DisplayPlacement>? placements, out problem) ||
            !TryReadRefresh(where, refreshEntries, monitors, known, out Dictionary<string, double>? refresh, out problem) ||
            !TryOrder(where, monitors, known, primary, placements!, out List<string>? order, out problem))
        {
            return false;
        }

        layout = new DisplayLayout(name, monitors, primary, placements!, refresh!, order!, requires);
        problem = null;
        return true;
    }

    /// <summary>
    /// Reads a list of strings, refusing anything that is not one.
    /// </summary>
    /// <remarks>
    /// A LIST, not an object keyed by monitor, for placement and refresh alike.
    /// JSON allows a duplicate key and this reader keeps the last of them
    /// silently, so a monitor placed twice in an object form would lose one of
    /// its entries without anything ever saying so. As a list, a monitor named
    /// twice is refused by name. It also reads like a sentence.
    /// </remarks>
    private static bool TryReadStrings(
        string where,
        JsonProperty property,
        out List<string>? values,
        out string? problem)
    {
        values = null;

        if (property.Value.ValueKind != JsonValueKind.Array)
        {
            problem = $"{where} has a '{property.Name}' that is not a list.";
            return false;
        }

        var read = new List<string>();

        foreach (JsonElement item in property.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                problem = $"{where} has an entry in '{property.Name}' that is not a name.";
                return false;
            }

            read.Add(item.GetString()!.Trim());
        }

        values = read;
        problem = null;
        return true;
    }

    /// <summary>
    /// Parses entries reading "Desktop2 RightOf Desktop1", optionally followed
    /// by an alignment ("Desktop2 RightOf Desktop1 Center") and/or a signed
    /// pixel offset ("Desktop2 RightOf Desktop1 Center +40").
    /// </summary>
    /// <remarks>
    /// The direction word is located rather than assumed to be the second
    /// token, so a logical monitor name may contain spaces -- as long as it is
    /// not itself one of the direction words. The trailing alignment and
    /// offset are found the same way, working in from the end: an entry
    /// without them behaves exactly as it always has, which is what lets every
    /// layout ever written keep parsing unchanged. A monitor named "Center" or
    /// "+40" at the very end of an anchor is the one case this cannot tell
    /// apart from an actual alignment or offset -- the same trade a direction
    /// word makes for a monitor named "RightOf".
    /// </remarks>
    private static bool TryReadPlacements(
        string where,
        List<string> entries,
        out Dictionary<string, DisplayPlacement>? placements,
        out string? problem)
    {
        placements = null;

        var parsed = new Dictionary<string, DisplayPlacement>(StringComparer.OrdinalIgnoreCase);

        foreach (string entry in entries)
        {
            string[] tokens = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            int index = -1;

            for (int position = 0; position < tokens.Length; position++)
            {
                int found = Array.FindIndex(
                    Directions,
                    direction => string.Equals(direction, tokens[position], StringComparison.OrdinalIgnoreCase));

                if (found >= 0)
                {
                    index = position;
                    break;
                }
            }

            if (index < 0)
            {
                problem =
                    $"{where} placement '{entry}' contains none of " +
                    $"{string.Join("/", Directions)}. Write it as 'ThisMonitor RightOf ThatMonitor'.";

                return false;
            }

            string direction = Directions.First(candidate =>
                string.Equals(candidate, tokens[index], StringComparison.OrdinalIgnoreCase));

            if (index == 0)
            {
                problem =
                    $"{where} placement '{entry}' does not say which monitor is {direction} " +
                    "the other. Write it as 'ThisMonitor RightOf ThatMonitor'.";

                return false;
            }

            if (index == tokens.Length - 1)
            {
                problem =
                    $"{where} placement '{entry}' puts a monitor {direction} nothing -- " +
                    "name the monitor it sits next to.";

                return false;
            }

            string monitor = string.Join(' ', tokens[..index]);
            string[] rest = tokens[(index + 1)..];
            int anchorEnd = rest.Length;

            int offset = 0;

            if (anchorEnd > 1 && TryParseOffset(rest[anchorEnd - 1], out int parsedOffset))
            {
                offset = parsedOffset;
                anchorEnd--;
            }

            DisplayAlignment alignment = DisplayAlignment.Start;

            if (anchorEnd > 1 && TryParseAlignment(rest[anchorEnd - 1], out DisplayAlignment parsedAlignment))
            {
                alignment = parsedAlignment;
                anchorEnd--;
            }

            string anchor = string.Join(' ', rest[..anchorEnd]);

            if (!parsed.TryAdd(
                monitor,
                new DisplayPlacement(Enum.Parse<DisplayDirection>(direction), anchor, alignment, offset)))
            {
                problem = $"{where} places '{monitor}' more than once. Each monitor gets one placement.";
                return false;
            }
        }

        placements = parsed;
        problem = null;
        return true;
    }

    /// <summary>Matches "Start"/"Center"/"End" case-insensitively.</summary>
    private static bool TryParseAlignment(string token, out DisplayAlignment alignment)
    {
        foreach (DisplayAlignment candidate in Enum.GetValues<DisplayAlignment>())
        {
            if (string.Equals(candidate.ToString(), token, StringComparison.OrdinalIgnoreCase))
            {
                alignment = candidate;
                return true;
            }
        }

        alignment = default;
        return false;
    }

    /// <summary>
    /// Matches an explicitly signed integer, such as "+40" or "-12" -- the sign
    /// is required so a plain number is free to be part of an anchor name
    /// instead of being mistaken for an offset.
    /// </summary>
    private static bool TryParseOffset(string token, out int offset)
    {
        if (token.Length > 1 && (token[0] == '+' || token[0] == '-') &&
            int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed))
        {
            offset = parsed;
            return true;
        }

        offset = 0;
        return false;
    }

    /// <summary>Parses entries reading "Desktop1 144".</summary>
    private static bool TryReadRefresh(
        string where,
        List<string> entries,
        List<string> monitors,
        HashSet<string> known,
        out Dictionary<string, double>? refresh,
        out string? problem)
    {
        refresh = null;

        var parsed = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (string entry in entries)
        {
            string[] tokens = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length < 2)
            {
                problem =
                    $"{where} refresh entry '{entry}' does not name both a monitor and a rate. " +
                    "Write it as 'SomeMonitor 144'.";

                return false;
            }

            if (!double.TryParse(tokens[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double hz) ||
                double.IsNaN(hz) ||
                hz <= 0)
            {
                problem = $"{where} refresh entry '{entry}' does not end with a positive number of Hz.";
                return false;
            }

            string monitor = string.Join(' ', tokens[..^1]);

            if (!known.Contains(monitor))
            {
                problem =
                    $"{where} asks for a refresh rate for '{monitor}', which is not one of its " +
                    $"monitors ({string.Join(", ", monitors)}).";

                return false;
            }

            if (!parsed.TryAdd(monitor, hz))
            {
                problem = $"{where} gives '{monitor}' more than one refresh rate.";
                return false;
            }
        }

        refresh = parsed;
        problem = null;
        return true;
    }

    /// <summary>
    /// Walks the placement graph outward from the primary, refusing anything that
    /// does not reach it.
    /// </summary>
    /// <remarks>
    /// This is what makes a layout applicable at all: a monitor can only be
    /// positioned once whatever it is placed against has been, so the order this
    /// produces is the order the arithmetic runs in. A cycle, or a chain hanging
    /// off nothing, simply never terminates in a position -- caught here, with
    /// the monitors named, rather than as a missing key half way through a
    /// transition.
    /// </remarks>
    private static bool TryOrder(
        string where,
        List<string> monitors,
        HashSet<string> known,
        string primary,
        Dictionary<string, DisplayPlacement> placements,
        out List<string>? order,
        out string? problem)
    {
        order = null;

        foreach ((string monitor, DisplayPlacement placement) in placements)
        {
            if (!known.Contains(monitor))
            {
                problem =
                    $"{where} places '{monitor}', which is not one of its monitors " +
                    $"({string.Join(", ", monitors)}).";

                return false;
            }

            if (string.Equals(monitor, placement.Anchor, StringComparison.OrdinalIgnoreCase))
            {
                problem = $"{where} places '{monitor}' relative to itself.";
                return false;
            }

            if (!known.Contains(placement.Anchor))
            {
                problem =
                    $"{where} places '{monitor}' {placement.Direction} '{placement.Anchor}', which is " +
                    $"not one of its monitors ({string.Join(", ", monitors)}).";

                return false;
            }
        }

        if (placements.ContainsKey(primary))
        {
            problem =
                $"{where} places '{primary}', but that is its primary and always sits at the " +
                "origin. Remove the placement, or make a different monitor primary.";

            return false;
        }

        List<string> placed = [primary];
        var settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primary };

        List<string> remaining = [.. monitors.Where(monitor =>
            !string.Equals(monitor, primary, StringComparison.OrdinalIgnoreCase))];

        while (remaining.Count > 0)
        {
            bool progressed = false;

            foreach (string monitor in remaining.ToList())
            {
                if (!placements.TryGetValue(monitor, out DisplayPlacement? placement))
                {
                    problem =
                        $"{where} has no placement for '{monitor}'. Every monitor except the primary " +
                        $"('{primary}') needs one, for example '{monitor} RightOf {primary}'.";

                    return false;
                }

                // The anchor may still be settled on a later sweep, so this is
                // not a failure until nothing moves at all.
                if (!settled.Contains(placement.Anchor))
                {
                    continue;
                }

                placed.Add(monitor);
                settled.Add(monitor);
                remaining.Remove(monitor);
                progressed = true;
            }

            if (!progressed)
            {
                problem =
                    $"{where} cannot place {string.Join(", ", remaining)} -- their placements form a " +
                    $"loop, or a chain that never reaches the primary ('{primary}').";

                return false;
            }
        }

        order = placed;
        problem = null;
        return true;
    }
}

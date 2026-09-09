namespace PCMode.Picker.Tests;

/// <summary>
/// Where the picker goes, and what a click lands on.
/// </summary>
/// <remarks>
/// The interesting cases -- a scaled monitor, a secondary monitor with a
/// negative origin, a click in the ring that only the selected wedge occupies
/// -- are ones nobody would find by opening the picker and looking.
/// </remarks>
public sealed class PickerGeometryTests
{
    // 1920x1040 work area at 100%: a 1080p monitor with a taskbar.
    private static PickerGeometry Standard() =>
        PickerGeometry.Create(new PixelRect(0, 0, 1920, 1040), 1.0, FavoriteSet.Default);

    // ---- placement ---------------------------------------------------------

    [Fact]
    public void Centres_the_wheel_in_the_work_area()
    {
        PickerGeometry geometry = Standard();
        PixelRect window = geometry.WindowBounds;

        Assert.Equal(1920 / 2, window.Left + (window.Width / 2));
        Assert.Equal(1040 / 2, window.Top + (window.Height / 2));
    }

    [Fact]
    public void Sizes_the_wheel_from_the_short_side()
    {
        // Otherwise an ultrawide monitor gets a wheel taller than the screen.
        PickerGeometry wide = PickerGeometry.Create(new PixelRect(0, 0, 3840, 1080), 1.0, FavoriteSet.Default);

        Assert.True(
            wide.WindowBounds.Height <= 1080,
            $"The picker is {wide.WindowBounds.Height} tall on a 1080-tall monitor."
        );
    }

    [Fact]
    public void Leaves_room_for_the_selected_wedge_to_protrude()
    {
        PickerGeometry geometry = Standard();

        // If the canvas were merely the wheel's size, the selected wedge would
        // be clipped along its outer edge at exactly the moment it is meant to
        // stand out.
        Assert.True(
            geometry.CanvasSize > geometry.SelectedOuterDiameter,
            "The selected wedge reaches the edge of the canvas and will be clipped."
        );
    }

    [Fact]
    public void Places_the_window_on_the_monitor_it_was_given()
    {
        // A second monitor to the left of the primary one has a negative
        // origin. Anything that assumed a screen starts at zero puts the
        // picker on the wrong display, or off every display.
        PickerGeometry geometry = PickerGeometry.Create(
            new PixelRect(-2560, -300, 2560, 1440),
            1.0,
            FavoriteSet.Default
        );

        PixelRect window = geometry.WindowBounds;

        Assert.True(window.Left >= -2560, $"Left edge {window.Left} is off the monitor.");
        Assert.True(window.Right <= 0, $"Right edge {window.Right} spills onto the next monitor.");
        Assert.True(window.Top >= -300);
        Assert.True(window.Bottom <= 1140);
    }

    // ---- scaling -----------------------------------------------------------

    [Fact]
    public void Keeps_the_same_physical_size_whatever_the_scale_factor()
    {
        // The same panel, described twice: 3840x2160 at 200% is the same piece
        // of glass as 1920x1080 at 100%, and the picker should cover the same
        // amount of it.
        PickerGeometry hundred = PickerGeometry.Create(new PixelRect(0, 0, 1920, 1080), 1.0, FavoriteSet.Default);

        PickerGeometry twoHundred = PickerGeometry.Create(new PixelRect(0, 0, 3840, 2160), 2.0, FavoriteSet.Default);

        // Within a pixel or two rather than exactly, and the slack is the
        // point rather than a concession. Every circle is rounded to an even
        // number of PHYSICAL pixels so its centre lands on a pixel boundary,
        // and rounding 2160 * 0.58 does not give twice the rounding of
        // 1080 * 0.58. The residue is two device pixels on a 4K panel, which
        // is the correct thing to be wrong by: it buys a wheel that is not
        // half a pixel out of centre at any scale.
        Assert.InRange(
            twoHundred.WindowBounds.Width,
            (hundred.WindowBounds.Width * 2) - 4,
            (hundred.WindowBounds.Width * 2) + 4
        );

        // And in drawing units it is the same size, because those units are
        // what WPF lays out in.
        Assert.InRange(twoHundred.CanvasSize, hundred.CanvasSize - 2, hundred.CanvasSize + 2);
    }

    [Fact]
    public void Reports_the_window_in_pixels_and_the_drawing_in_units()
    {
        PickerGeometry geometry = PickerGeometry.Create(new PixelRect(0, 0, 2560, 1440), 1.5, FavoriteSet.Default);

        // The distinction the whole class exists for. A window placed in
        // drawing units lands in the wrong place on a mixed-DPI desktop, which
        // is the normal desktop for anything driving a television.
        Assert.Equal(geometry.WindowBounds.Width / 1.5, geometry.CanvasSize, precision: 6);
    }

    // ---- hit testing -------------------------------------------------------

    [Theory]
    [InlineData(0, -1, PickerDirection.Up)]
    [InlineData(1, 0, PickerDirection.Right)]
    [InlineData(0, 1, PickerDirection.Down)]
    [InlineData(-1, 0, PickerDirection.Left)]
    public void Finds_the_wedge_a_point_is_over(double dx, double dy, PickerDirection expected)
    {
        PickerGeometry geometry = Standard();

        // Half way out along each axis: clear of the hub, well inside the rim.
        double distance = (geometry.WheelDiameter / 2.0) * 0.7;

        PickerHit hit = geometry.HitTest(
            new PickerPoint(geometry.Center + (dx * distance), geometry.Center + (dy * distance)),
            PickerDirection.Up
        );

        Assert.Equal(PickerHitKind.Wedge, hit.Kind);
        Assert.Equal(expected, hit.Direction);
    }

    [Fact]
    public void Treats_the_centre_as_the_hub_rather_than_as_a_wedge()
    {
        PickerGeometry geometry = Standard();

        PickerHit hit = geometry.HitTest(new PickerPoint(geometry.Center, geometry.Center), PickerDirection.Up);

        // Hovering the hub must not move the selection, and clicking it means
        // "the current mode", not "whichever quadrant the maths says".
        Assert.Equal(PickerHitKind.Hub, hit.Kind);
    }

    [Fact]
    public void Finds_nothing_outside_the_wheel()
    {
        PickerGeometry geometry = Standard();

        PickerHit hit = geometry.HitTest(new PickerPoint(0, 0), PickerDirection.Up);

        Assert.Equal(PickerHitKind.None, hit.Kind);
    }

    [Fact]
    public void Gives_the_selected_wedge_the_ring_it_protrudes_into()
    {
        // The selected wedge is drawn bigger, so it has to BE bigger to the
        // pointer as well. A point in that outer ring is over painted colour;
        // reporting nothing there means a click that visibly hit the wedge
        // does nothing at all.
        PickerGeometry geometry = Standard();

        double justPastTheRim = (geometry.WheelDiameter / 2.0) + (geometry.SelectedExtension / 2.0);
        var aboveCentre = new PickerPoint(geometry.Center, geometry.Center - justPastTheRim);

        PickerHit whenSelected = geometry.HitTest(aboveCentre, PickerDirection.Up);
        PickerHit whenNot = geometry.HitTest(aboveCentre, PickerDirection.Down);

        Assert.Equal(PickerHitKind.Wedge, whenSelected.Kind);
        Assert.Equal(PickerDirection.Up, whenSelected.Direction);

        Assert.Equal(PickerHitKind.None, whenNot.Kind);
    }

    [Fact]
    public void Splits_the_wheel_on_the_diagonals()
    {
        // The boundary rule stated as a property rather than by sampling: a
        // point just clockwise of the up-right diagonal belongs to Right, just
        // anticlockwise to Up. Getting this wrong is invisible in a screenshot
        // and obvious under the hand.
        PickerGeometry geometry = Standard();

        double reach = (geometry.WheelDiameter / 2.0) * 0.6;

        PickerHit steeper = geometry.HitTest(
            new PickerPoint(geometry.Center + (reach * 0.9), geometry.Center - reach),
            PickerDirection.Up
        );

        PickerHit shallower = geometry.HitTest(
            new PickerPoint(geometry.Center + reach, geometry.Center - (reach * 0.9)),
            PickerDirection.Up
        );

        Assert.Equal(PickerDirection.Up, steeper.Direction);
        Assert.Equal(PickerDirection.Right, shallower.Direction);
    }

    [Fact]
    public void Hit_tests_the_same_way_at_every_scale_factor()
    {
        // Because the hit test works in drawing units, the same proportional
        // point must land on the same wedge whatever the monitor's scaling.
        // If this ever fails, the picture and the pointer have come apart by
        // exactly the scale factor.
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            PickerGeometry geometry = PickerGeometry.Create(
                new PixelRect(0, 0, (int)(1920 * scale), (int)(1080 * scale)),
                scale,
                FavoriteSet.Default
            );

            double reach = (geometry.WheelDiameter / 2.0) * 0.7;

            PickerHit hit = geometry.HitTest(
                new PickerPoint(geometry.Center + reach, geometry.Center),
                PickerDirection.Up
            );

            Assert.Equal(PickerDirection.Right, hit.Direction);
        }
    }

    // ---- content placement -------------------------------------------------

    [Fact]
    public void Puts_each_wedges_icon_out_along_its_own_compass_point()
    {
        PickerGeometry geometry = Standard();

        Assert.True(geometry.IconCenter(PickerDirection.Up).Y < geometry.Center);
        Assert.True(geometry.IconCenter(PickerDirection.Down).Y > geometry.Center);
        Assert.True(geometry.IconCenter(PickerDirection.Left).X < geometry.Center);
        Assert.True(geometry.IconCenter(PickerDirection.Right).X > geometry.Center);
    }

    [Fact]
    public void Puts_each_label_under_its_own_icon()
    {
        PickerGeometry geometry = Standard();

        foreach (PickerDirection direction in Enum.GetValues<PickerDirection>())
        {
            PickerPoint icon = geometry.IconCenter(direction);
            PickerRect label = geometry.LabelBox(direction);

            Assert.True(label.Y > icon.Y, $"The {direction} label is not below its icon.");
            Assert.Equal(icon.X, label.X + (label.Width / 2), precision: 6);
        }
    }

    [Fact]
    public void Follows_a_favorites_own_cosmetic_nudges()
    {
        // Offsets belong to the favorite, so a set that overrides them moves
        // that favorite's icon and nothing else.
        FavoriteSet nudged = FavoriteSet.Create([
            .. FavoriteSet.Default.All.Select(favorite =>
                favorite.Direction == PickerDirection.Up
                    ? favorite with
                    {
                        IconOffset = new PickerPoint(0.1, 0.0),
                    }
                    : favorite
            ),
        ]);

        PickerGeometry plain = Standard();
        PickerGeometry moved = PickerGeometry.Create(new PixelRect(0, 0, 1920, 1040), 1.0, nudged);

        Assert.True(moved.IconCenter(PickerDirection.Up).X > plain.IconCenter(PickerDirection.Up).X);
        Assert.Equal(plain.IconCenter(PickerDirection.Down).X, moved.IconCenter(PickerDirection.Down).X, precision: 6);
    }
}

namespace PCMode.Picker.Tests;

/// <summary>
/// The picker's state machine, at instants a test can name.
/// </summary>
/// <remarks>
/// These behaviours are otherwise only visible by operating the picker, and
/// several of them -- the stationary-mouse rule above all -- are the sort that
/// break silently and are then blamed on the mouse.
/// </remarks>
public sealed class PickerCoordinatorTests
{
    private static readonly PickerGeometry Layout = PickerGeometry.Create(
        new PixelRect(0, 0, 1920, 1040),
        1.0,
        FavoriteSet.Default
    );

    private static TimeSpan At(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static PickerCoordinator Opened(string? currentMode = "Desktop", PickerPoint? pointer = null)
    {
        var picker = new PickerCoordinator();

        picker.Show(At(0), currentMode, Layout, pointer ?? new PickerPoint(0, 0));

        // Past the opening fade, so tests that are not about it start from a
        // settled picker.
        picker.Advance(At(1000));

        return picker;
    }

    // ---- opening -----------------------------------------------------------

    [Fact]
    public void Opens_onto_the_mode_the_machine_is_actually_in()
    {
        var picker = new PickerCoordinator();

        picker.Show(At(0), "Gaming", Layout, null);

        Assert.Equal(PickerDirection.Down, picker.Selected);
        Assert.Equal("Gaming", picker.Current.ModeName);
    }

    [Fact]
    public void Falls_back_to_the_first_wedge_for_a_mode_that_is_not_on_the_wheel()
    {
        // Ordinary, not exceptional: PCMode can be given any configured mode
        // from the console or the tray, and the wheel only has four places.
        var picker = new PickerCoordinator();

        picker.Show(At(0), "SomeModeOnlyTheTrayLists", Layout, null);

        Assert.Equal(FavoriteSet.Default.All[0].Direction, picker.Selected);
    }

    [Fact]
    public void Falls_back_when_nothing_has_been_recorded_at_all()
    {
        var picker = new PickerCoordinator();

        picker.Show(At(0), null, Layout, null);

        Assert.Equal(FavoriteSet.Default.All[0].ModeName, picker.Current.ModeName);
    }

    [Fact]
    public void Fades_in_rather_than_appearing_at_once()
    {
        var picker = new PickerCoordinator();

        picker.Show(At(0), "Desktop", Layout, null);
        Assert.Equal(0.0, picker.Alpha);

        picker.Advance(At(100));
        Assert.InRange(picker.Alpha, 0.01, 0.99);

        picker.Advance(At(1000));
        Assert.Equal(1.0, picker.Alpha);
        Assert.Equal(PickerPhase.Open, picker.Phase);
    }

    // ---- selection ---------------------------------------------------------

    [Fact]
    public void Moves_the_selection_with_an_arrow()
    {
        PickerCoordinator picker = Opened();

        Assert.True(picker.SelectDirection(PickerDirection.Right, At(1000)));
        Assert.Equal(PickerDirection.Right, picker.Selected);
    }

    [Fact]
    public void Ignores_a_selection_that_is_already_selected()
    {
        PickerCoordinator picker = Opened();

        Assert.False(picker.SelectDirection(picker.Selected, At(1000)));
    }

    [Fact]
    public void Hands_emphasis_from_one_wedge_to_the_next_over_time()
    {
        PickerCoordinator picker = Opened();

        picker.SelectDirection(PickerDirection.Right, At(1000));
        picker.Advance(At(1050));

        // Half way through: the old wedge is on its way out and the new one on
        // its way in, and neither has arrived.
        Assert.InRange(picker.Amount(PickerDirection.Up), 0.01, 0.99);
        Assert.InRange(picker.Amount(PickerDirection.Right), 0.01, 0.99);

        picker.Advance(At(2000));

        Assert.Equal(0.0, picker.Amount(PickerDirection.Up));
        Assert.Equal(1.0, picker.Amount(PickerDirection.Right));
    }

    [Fact]
    public void Snaps_to_a_new_wedge_rather_than_chaining_two_animations()
    {
        // Somebody pressing three arrows quickly is saying where they want to
        // be, not asking to watch three transitions. Retargeting instead would
        // make the picker look like it is lagging behind the input.
        PickerCoordinator picker = Opened();

        picker.SelectDirection(PickerDirection.Right, At(1000));
        picker.Advance(At(1050));

        picker.SelectDirection(PickerDirection.Down, At(1050));

        Assert.Equal(1.0, picker.Amount(PickerDirection.Down));
        Assert.Equal(0.0, picker.Amount(PickerDirection.Right));
        Assert.Equal(0.0, picker.Amount(PickerDirection.Up));
    }

    [Fact]
    public void Cuts_the_opening_fade_short_when_somebody_starts_choosing()
    {
        PickerCoordinator picker = new();

        picker.Show(At(0), "Desktop", Layout, null);
        picker.Advance(At(50));

        Assert.Equal(PickerPhase.Opening, picker.Phase);

        picker.SelectDirection(PickerDirection.Right, At(50));

        // Already choosing means already done waiting for the wheel to arrive.
        Assert.Equal(1.0, picker.Alpha);
        Assert.Equal(PickerPhase.Open, picker.Phase);
    }

    // ---- pointer -----------------------------------------------------------

    [Fact]
    public void Selects_the_wedge_the_pointer_moves_over()
    {
        PickerCoordinator picker = Opened(pointer: new PickerPoint(0, 0));

        double reach = (Layout.WheelDiameter / 2) * 0.7;
        var overRight = new PickerPoint(Layout.Center + reach, Layout.Center);

        Assert.True(picker.PointerMoved(overRight, new PickerPoint(500, 500), At(1000)));
        Assert.Equal(PickerDirection.Right, picker.Selected);
        Assert.Equal(PickerInputSource.Pointer, picker.InputSource);
    }

    [Fact]
    public void Lets_an_arrow_key_win_against_a_mouse_that_is_not_moving()
    {
        // THE rule. The picker opens under wherever the cursor was left; that
        // produces a move message at an unchanged position, and honouring it
        // would snatch the selection back from the arrow key that just set it,
        // every single time, on any machine where the cursor happens to rest
        // over a wedge.
        var resting = new PickerPoint(700, 400);
        PickerCoordinator picker = Opened(pointer: resting);

        double reach = (Layout.WheelDiameter / 2) * 0.7;
        var overDown = new PickerPoint(Layout.Center, Layout.Center + reach);

        picker.SelectDirection(PickerDirection.Left, At(1000));

        Assert.False(picker.PointerMoved(overDown, resting, At(1010)));

        Assert.Equal(PickerDirection.Left, picker.Selected);
        Assert.Equal(PickerInputSource.Keyboard, picker.InputSource);
    }

    [Fact]
    public void Gives_the_selection_back_the_moment_the_mouse_really_moves()
    {
        var resting = new PickerPoint(700, 400);
        PickerCoordinator picker = Opened(pointer: resting);

        picker.SelectDirection(PickerDirection.Left, At(1000));

        double reach = (Layout.WheelDiameter / 2) * 0.7;
        var overDown = new PickerPoint(Layout.Center, Layout.Center + reach);

        // One pixel is a real move. The rule is about unchanged positions, not
        // about ignoring the mouse.
        picker.PointerMoved(overDown, resting with { X = resting.X + 1 }, At(1020));

        Assert.Equal(PickerDirection.Down, picker.Selected);
    }

    [Fact]
    public void Leaves_the_selection_alone_when_the_pointer_crosses_the_hub()
    {
        PickerCoordinator picker = Opened();

        picker.SelectDirection(PickerDirection.Right, At(1000));

        picker.PointerMoved(new PickerPoint(Layout.Center, Layout.Center), new PickerPoint(123, 456), At(1010));

        Assert.Equal(PickerDirection.Right, picker.Selected);
    }

    // ---- launching ---------------------------------------------------------

    [Fact]
    public void An_unassigned_position_stays_visible_and_launches_nothing()
    {
        FavoriteSet favorites = FavoriteSet.Create([
            .. FavoriteSet.Default.All.Select(favorite =>
                favorite.Direction == PickerDirection.Up
                    ? favorite with
                    {
                        ModeName = string.Empty,
                        Label = "Not assigned",
                        Icon = FavoriteIcon.None,
                    }
                    : favorite
            ),
        ]);
        var picker = new PickerCoordinator(favorites);
        picker.Show(At(0), null, Layout, new PickerPoint(0, 0));
        picker.Advance(At(1000));
        bool launched = false;
        picker.LaunchRequested += (_, _) => launched = true;

        picker.SelectDirection(PickerDirection.Up, At(1100));

        Assert.False(picker.Activate(At(1200)));
        Assert.False(launched);
        Assert.Equal(PickerPhase.Open, picker.Phase);
    }

    [Fact]
    public void Applies_nothing_until_the_wheel_has_finished_fading_out()
    {
        // Deferred on purpose. A mode transition rearranges monitors, and a
        // window still trying to paint itself through that is at its worst
        // exactly then.
        PickerCoordinator picker = Opened();
        string? launched = null;

        picker.LaunchRequested += (_, e) => launched = e.ModeName;

        picker.SelectDirection(PickerDirection.Left, At(1000));
        picker.Activate(At(1100));

        Assert.Null(launched);
        Assert.Equal(PickerPhase.Closing, picker.Phase);

        picker.Advance(At(1150));
        Assert.Null(launched);

        picker.Advance(At(2000));

        Assert.Equal("Movie", launched);
        Assert.Equal(PickerPhase.Hidden, picker.Phase);
    }

    [Fact]
    public void Applies_the_mode_name_rather_than_the_label()
    {
        // The one coupling this port exists to break. A favorite may be
        // labelled anything; PCMode is only ever handed the mode name.
        FavoriteSet renamed = FavoriteSet.Create([
            .. FavoriteSet.Default.All.Select(favorite =>
                favorite.Direction == PickerDirection.Up ? favorite with { Label = "Work" } : favorite
            ),
        ]);

        var picker = new PickerCoordinator(renamed);
        string? launched = null;
        picker.LaunchRequested += (_, e) => launched = e.ModeName;

        picker.Show(At(0), "Desktop", Layout, null);
        picker.Advance(At(1000));
        picker.Activate(At(1000));
        picker.Advance(At(2000));

        Assert.Equal("Desktop", launched);
    }

    [Fact]
    public void Applies_the_current_mode_when_the_hub_is_clicked()
    {
        PickerCoordinator picker = Opened("Gaming");
        string? launched = null;
        picker.LaunchRequested += (_, e) => launched = e.ModeName;

        picker.SelectDirection(PickerDirection.Up, At(1000));
        picker.PointerPressed(new PickerPoint(Layout.Center, Layout.Center), At(1010));
        picker.Advance(At(3000));

        // The hub is the mode that IS current, not whatever is selected.
        Assert.Equal("Gaming", launched);
    }

    [Fact]
    public void Applies_the_wedge_that_was_clicked_even_without_hovering_it_first()
    {
        PickerCoordinator picker = Opened();
        string? launched = null;
        picker.LaunchRequested += (_, e) => launched = e.ModeName;

        double reach = (Layout.WheelDiameter / 2) * 0.7;
        picker.PointerPressed(new PickerPoint(Layout.Center, Layout.Center + reach), At(1000));
        picker.Advance(At(3000));

        Assert.Equal("Gaming", launched);
    }

    [Fact]
    public void Applies_nothing_when_a_click_lands_outside_the_wheel()
    {
        PickerCoordinator picker = Opened();
        bool launched = false;
        picker.LaunchRequested += (_, _) => launched = true;

        picker.PointerPressed(new PickerPoint(1, 1), At(1000));
        picker.Advance(At(3000));

        Assert.False(launched);
        Assert.Equal(PickerPhase.Open, picker.Phase);
    }

    // ---- cancelling and reopening ------------------------------------------

    [Fact]
    public void Applies_nothing_when_it_is_cancelled()
    {
        PickerCoordinator picker = Opened();
        bool launched = false;
        picker.LaunchRequested += (_, _) => launched = true;

        picker.Cancel(At(1000));
        picker.Advance(At(3000));

        Assert.Equal(PickerPhase.Hidden, picker.Phase);
        Assert.False(launched);
    }

    [Fact]
    public void Fades_out_from_where_it_had_got_to_rather_than_from_the_top()
    {
        // Cancelled part way through the opening fade. Restarting the fade
        // from fully opaque would make the wheel flash brighter on its way out.
        var picker = new PickerCoordinator();

        picker.Show(At(0), "Desktop", Layout, null);
        picker.Advance(At(60));

        double whenCancelled = picker.Alpha;
        Assert.InRange(whenCancelled, 0.01, 0.99);

        picker.Cancel(At(60));
        picker.Advance(At(61));

        Assert.True(
            picker.Alpha <= whenCancelled,
            $"Opacity rose from {whenCancelled} to {picker.Alpha} on the way out."
        );
    }

    [Fact]
    public void Cancels_a_pending_launch_if_it_is_reopened_before_the_fade_ends()
    {
        // Chosen, then thought better of before it had gone. Applying it
        // anyway, several seconds later and after the wheel is back up, would
        // be a mode change nobody asked for.
        PickerCoordinator picker = Opened();
        bool launched = false;
        picker.LaunchRequested += (_, _) => launched = true;

        picker.Activate(At(1000));
        Assert.Equal(PickerPhase.Closing, picker.Phase);

        picker.Show(At(1050), "Desktop", Layout, null);
        picker.Advance(At(5000));

        Assert.False(launched);
        Assert.Equal(PickerPhase.Open, picker.Phase);
    }

    [Fact]
    public void Closes_when_toggled_while_it_is_up()
    {
        PickerCoordinator picker = Opened();

        picker.Toggle(At(1000), "Desktop", Layout, null);

        Assert.Equal(PickerPhase.Closing, picker.Phase);
    }

    [Fact]
    public void Opens_when_toggled_while_it_is_down()
    {
        var picker = new PickerCoordinator();

        picker.Toggle(At(0), "Movie", Layout, null);

        Assert.Equal(PickerPhase.Opening, picker.Phase);
        Assert.Equal(PickerDirection.Left, picker.Selected);
    }

    [Fact]
    public void Does_nothing_with_input_while_it_is_hidden()
    {
        var picker = new PickerCoordinator();

        Assert.False(picker.SelectDirection(PickerDirection.Down, At(0)));
        Assert.False(picker.Activate(At(0)));
        Assert.False(picker.Cancel(At(0)));
        Assert.False(picker.PointerMoved(new PickerPoint(1, 1), new PickerPoint(1, 1), At(0)));
    }

    [Fact]
    public void Stops_asking_for_frames_once_everything_has_settled()
    {
        // The frame timer is switched off when this goes false. A picker that
        // never settles would repaint a still image a hundred times a second
        // for as long as it is open.
        PickerCoordinator picker = Opened();

        Assert.False(picker.IsAnimating);

        picker.SelectDirection(PickerDirection.Right, At(1000));
        Assert.True(picker.IsAnimating);

        picker.Advance(At(2000));
        Assert.False(picker.IsAnimating);
    }
}

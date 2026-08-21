namespace PCMode.Picker;

/// <summary>Where the picker is in its own lifecycle.</summary>
public enum PickerPhase
{
    Hidden,

    /// <summary>On screen and fading in.</summary>
    Opening,

    Open,

    /// <summary>Fading out, possibly with a mode to apply once it is gone.</summary>
    Closing,
}

/// <summary>
/// Which device made the most recent selection.
/// </summary>
/// <remarks>
/// Not a preference: it decides who currently owns the selection. See
/// <see cref="PickerCoordinator.PointerMoved"/>.
/// </remarks>
public enum PickerInputSource
{
    /// <summary>Keyboard, D-pad or stick -- anything discrete and deliberate.</summary>
    Keyboard,

    Pointer,
}

/// <summary>A mode the picker has decided to apply.</summary>
public sealed class PickerLaunchEventArgs(ModeFavorite favorite) : EventArgs
{
    public ModeFavorite Favorite { get; } = favorite;

    /// <summary>The mode name to hand to PCMode. Never the label.</summary>
    public string ModeName => Favorite.ModeName;
}

/// <summary>
/// The picker's behaviour: what is selected, why, how it is animating, and
/// what happens when somebody chooses something.
/// </summary>
/// <remarks>
/// A state machine over an injected clock. Nothing here draws, owns a window,
/// or knows what a pixel is; <see cref="Advance"/> is called by whatever is
/// driving frames and every method returns whether the picture needs
/// repainting.
///
/// TIME IS A PARAMETER
///
/// Every method takes the current time rather than reading a clock. That is the
/// single thing that makes the animation testable: "interrupting a transition
/// half way snaps to the new wedge instead of chaining two motions" is a claim
/// about a specific instant, and a test that has to sleep to reach that instant
/// is a test that will be flaky forever.
///
/// THE RULES WORTH STATING
///
/// Discrete input takes the selection and KEEPS it until the pointer physically
/// moves. Without that, a wheel opened under a resting cursor would have its
/// arrow-key selection overwritten by whichever wedge the pointer happened to
/// be sitting on.
///
/// A new selection during a running transition CANCELS it and snaps, rather
/// than retargeting it. Chained motions look like the picker is lagging behind
/// the input, and someone pressing three arrow keys quickly is telling you
/// where they want to be, not asking to watch three animations.
///
/// A launch does not happen when it is chosen. It waits for the closing fade to
/// finish, so the wheel is gone before the screen starts changing underneath
/// it -- and a mode transition is exactly the moment when a window trying to
/// repaint itself is at its worst.
/// </remarks>
public sealed class PickerCoordinator
{
    private readonly Dictionary<PickerDirection, double> _amount = [];
    private readonly Dictionary<PickerDirection, double> _animationStart = [];

    private PickerGeometry? _geometry;

    private TimeSpan _selectionStart;
    private bool _selectionRunning;

    private TimeSpan _fadeStart;
    private double _fadeFrom;

    private ModeFavorite? _pendingLaunch;
    private PickerPoint? _lastPointer;

    public PickerCoordinator(FavoriteSet? favorites = null, PickerTuning? tuning = null)
    {
        Favorites = favorites ?? FavoriteSet.Default;
        Tuning = tuning ?? PickerTuning.Default;

        Selected = Favorites.All[0].Direction;
        Current = Favorites.All[0];

        foreach (ModeFavorite favorite in Favorites.All)
        {
            _amount[favorite.Direction] = 0.0;
        }

        _amount[Selected] = 1.0;
    }

    /// <summary>
    /// Raised once a closing fade has finished with a mode waiting behind it.
    /// </summary>
    public event EventHandler<PickerLaunchEventArgs>? LaunchRequested;

    public FavoriteSet Favorites { get; }

    public PickerTuning Tuning { get; }

    public PickerPhase Phase { get; private set; } = PickerPhase.Hidden;

    public bool IsVisible => Phase != PickerPhase.Hidden;

    /// <summary>The wedge the user is currently on.</summary>
    public PickerDirection Selected { get; private set; }

    /// <summary>
    /// The favorite the hub shows: the mode that is actually active.
    /// </summary>
    /// <remarks>
    /// Always one of the four, even when the machine is in a mode that is not
    /// on the wheel. PCMode can be given any configured mode name from the
    /// console or the tray, so "the current mode is not a favorite" is ordinary
    /// rather than exceptional, and falling back to the first wedge is what the
    /// original does.
    /// </remarks>
    public ModeFavorite Current { get; private set; }

    /// <summary>Which device last moved the selection.</summary>
    public PickerInputSource InputSource { get; private set; } = PickerInputSource.Keyboard;

    /// <summary>Whole-picker opacity, 0 to 1, driven by the opening and closing fades.</summary>
    public double Alpha { get; private set; } = 1.0;

    /// <summary>The layout in use while the picker is open.</summary>
    public PickerGeometry? Geometry => _geometry;

    /// <summary>Whether any animation still needs frames.</summary>
    public bool IsAnimating => _selectionRunning || Phase is PickerPhase.Opening or PickerPhase.Closing;

    /// <summary>
    /// How selected a wedge currently looks, from 0 to 1. Drives its
    /// extension, its opacity gradient, and the size of its icon and label.
    /// </summary>
    public double Amount(PickerDirection direction) => _amount[direction];

    /// <summary>
    /// Opens the picker.
    /// </summary>
    /// <param name="currentModeName">
    /// What PCMode has recorded as current. Anything not on the wheel -- an
    /// unrecorded mode, or one only the tray lists -- falls back to the first
    /// favorite.
    /// </param>
    /// <param name="pointer">
    /// Where the pointer is right now, in screen coordinates. Taken before the
    /// window appears: opening a window under a resting cursor produces a move
    /// message at that same position, and treating that as the user moving the
    /// mouse would hand the selection straight to whichever wedge it landed on.
    /// </param>
    public bool Show(TimeSpan now, string? currentModeName, PickerGeometry geometry, PickerPoint? pointer)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        // Reopening cancels a close that may still be fading, and the mode that
        // close was going to apply goes with it.
        _pendingLaunch = null;

        _geometry = geometry;

        Current = Favorites.ByModeName(currentModeName) ??
            Favorites.All.FirstOrDefault(favorite => favorite.IsAssigned) ??
            Favorites.All[0];
        Selected = Current.Direction;
        InputSource = PickerInputSource.Keyboard;
        _lastPointer = pointer;

        ResetSelection(Selected);

        // Drawn complete, then presented fully transparent: the opening fade
        // changes only the opacity, so the wheel never has to be redrawn for it.
        Alpha = 0.0;
        _fadeFrom = 0.0;
        _fadeStart = now;
        Phase = PickerPhase.Opening;

        return true;
    }

    /// <summary>Closes the picker without applying anything.</summary>
    public bool Cancel(TimeSpan now) => BeginClosing(now, launch: null);

    /// <summary>Opens the picker, or closes it if it is already up.</summary>
    public bool Toggle(TimeSpan now, string? currentModeName, PickerGeometry geometry, PickerPoint? pointer) =>
        IsVisible ? Cancel(now) : Show(now, currentModeName, geometry, pointer);

    /// <summary>Applies whatever is selected.</summary>
    public bool Activate(TimeSpan now) =>
        IsVisible && Favorites[Selected].IsAssigned && BeginClosing(now, Favorites[Selected]);

    /// <summary>
    /// Moves the selection with a discrete input: an arrow key, a D-pad press
    /// or a stick deflection.
    /// </summary>
    /// <remarks>
    /// Takes the selection away from the pointer immediately and keeps it,
    /// even if the pointer is still resting over a different wedge.
    /// </remarks>
    public bool SelectDirection(PickerDirection direction, TimeSpan now)
    {
        if (!IsVisible)
        {
            return false;
        }

        InputSource = PickerInputSource.Keyboard;

        return Select(direction, now);
    }

    /// <summary>
    /// Handles a pointer move, in the picker's own drawing units.
    /// </summary>
    /// <param name="screenPosition">
    /// The same move in screen coordinates, used only to tell a real move from
    /// a repeated message at an unchanged position. Screen rather than client
    /// coordinates because the window's own position is not part of the
    /// question being asked.
    /// </param>
    /// <returns>Whether anything changed.</returns>
    public bool PointerMoved(PickerPoint position, PickerPoint screenPosition, TimeSpan now)
    {
        if (!IsVisible || _geometry is null)
        {
            return false;
        }

        // The rule that makes discrete input authoritative. Windows re-sends
        // moves at an unchanged position for reasons that have nothing to do
        // with the user -- a window appearing underneath the cursor, most of
        // all -- and every one of those would otherwise steal the selection
        // back from the arrow key that just set it.
        if (_lastPointer == screenPosition)
        {
            return false;
        }

        _lastPointer = screenPosition;
        InputSource = PickerInputSource.Pointer;

        PickerHit hit = _geometry.HitTest(position, Selected);

        // The hub is not a wedge, so hovering it selects nothing and, just as
        // importantly, does not clear the selection either.
        return hit.Kind == PickerHitKind.Wedge && Select(hit.Direction, now);
    }

    /// <summary>
    /// Handles a click, in drawing units.
    /// </summary>
    /// <remarks>
    /// Clicking the hub re-applies the mode that is already current, which is
    /// how the original offers "put things back the way they are supposed to
    /// be" when something has drifted out from under PCMode.
    /// </remarks>
    public bool PointerPressed(PickerPoint position, TimeSpan now)
    {
        if (!IsVisible || _geometry is null)
        {
            return false;
        }

        PickerHit hit = _geometry.HitTest(position, Selected);

        return hit.Kind switch
        {
            PickerHitKind.Hub when Current.IsAssigned => BeginClosing(now, Current),
            PickerHitKind.Wedge when Favorites[hit.Direction].IsAssigned =>
                BeginClosing(now, Favorites[hit.Direction]),
            _ => false,
        };
    }

    /// <summary>
    /// Advances every running animation to <paramref name="now"/>.
    /// </summary>
    /// <returns>Whether the picture changed and needs repainting.</returns>
    public bool Advance(TimeSpan now)
    {
        bool changed = false;

        if (_selectionRunning)
        {
            double eased = PickerEasing.Ease(now - _selectionStart, Tuning.SelectionDuration);

            foreach ((PickerDirection direction, double start) in _animationStart)
            {
                _amount[direction] = PickerEasing.Lerp(start, direction == Selected ? 1.0 : 0.0, eased);
            }

            changed = true;

            if (eased >= 1.0)
            {
                // Snapped to exact values so rounding cannot accumulate across
                // a long session of selections.
                SettleAmounts(Selected);
                _selectionRunning = false;
            }
        }

        switch (Phase)
        {
            case PickerPhase.Opening:
            {
                Alpha = PickerEasing.Ease(now - _fadeStart, Tuning.OpeningDuration);
                changed = true;

                if (Alpha >= 1.0)
                {
                    FinishOpening();
                }

                break;
            }

            case PickerPhase.Closing:
            {
                double eased = PickerEasing.Ease(now - _fadeStart, Tuning.ClosingDuration);

                Alpha = PickerEasing.Lerp(_fadeFrom, 0.0, eased);
                changed = true;

                if (eased >= 1.0)
                {
                    FinishClosing();
                }

                break;
            }
        }

        return changed;
    }

    private bool Select(PickerDirection direction, TimeSpan now)
    {
        if (direction == Selected)
        {
            return false;
        }

        // Any real selection ends the opening fade at once. Somebody who is
        // already choosing has stopped waiting for the wheel to arrive.
        if (Phase == PickerPhase.Opening)
        {
            FinishOpening();
        }

        Selected = direction;

        if (_selectionRunning)
        {
            // Cancel and snap rather than retarget. Same rule for every input
            // device, which is why it lives here and not in a caller.
            ResetSelection(direction);
            return true;
        }

        _animationStart.Clear();

        foreach ((PickerDirection key, double value) in _amount)
        {
            _animationStart[key] = value;
        }

        _selectionStart = now;
        _selectionRunning = true;

        return true;
    }

    private bool BeginClosing(TimeSpan now, ModeFavorite? launch)
    {
        if (!IsVisible)
        {
            return false;
        }

        // A close takes ownership of the opacity immediately: an opening fade
        // or a selection transition still in flight is cancelled rather than
        // allowed to finish, so the wheel starts leaving the moment it was
        // told to.
        _selectionRunning = false;

        // A second close while already fading keeps fading from where it is,
        // and updates what is waiting behind it.
        _fadeFrom = Phase == PickerPhase.Closing ? Alpha : Math.Min(Alpha, 1.0);
        _fadeStart = now;
        Phase = PickerPhase.Closing;
        _pendingLaunch = launch;

        return true;
    }

    private void FinishOpening()
    {
        Alpha = 1.0;
        Phase = PickerPhase.Open;
    }

    private void FinishClosing()
    {
        Phase = PickerPhase.Hidden;
        _geometry = null;
        _lastPointer = null;

        ModeFavorite? pending = _pendingLaunch;
        _pendingLaunch = null;

        // Back to fully opaque for whatever shows it next, which sets its own
        // starting opacity anyway. Leaving it at zero would mean an invisible
        // picker if anything ever presented without opening.
        Alpha = 1.0;

        if (pending is not null)
        {
            LaunchRequested?.Invoke(this, new PickerLaunchEventArgs(pending));
        }
    }

    private void ResetSelection(PickerDirection selected)
    {
        _selectionRunning = false;
        SettleAmounts(selected);
    }

    private void SettleAmounts(PickerDirection selected)
    {
        foreach (PickerDirection direction in _amount.Keys.ToArray())
        {
            _amount[direction] = direction == selected ? 1.0 : 0.0;
        }
    }
}

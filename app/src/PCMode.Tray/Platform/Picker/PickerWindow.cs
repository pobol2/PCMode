using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PCMode.Picker;
using PCMode.Tray.Settings;
using Brushes = System.Windows.Media.Brushes;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace PCMode.Tray.Picker;

/// <summary>
/// The radial picker on screen.
/// </summary>
/// <remarks>
/// WHAT THIS OWNS, AND WHAT IT DOES NOT
///
/// It owns a window, a frame timer and the translation of real input events
/// into calls. Every decision -- what a click means, which wedge an arrow key
/// moves to, whether a mouse move counts, when a chosen mode is actually
/// applied -- belongs to <see cref="PickerCoordinator"/>, which knows nothing
/// about any of this and is tested without it.
///
/// PLACED IN PHYSICAL PIXELS, DRAWN IN LOGICAL ONES
///
/// The window is positioned with SetWindowPos rather than by setting Left and
/// Top. WPF's own coordinates are device-independent units, and on a desktop
/// where two monitors scale differently there is no single conversion between
/// those and the screen -- so a window placed that way lands somewhere else, or
/// nowhere, whenever the target is not the primary display. That is not an
/// edge case for PCMode: driving a monitor and a television at once is the
/// entire point of the program.
///
/// The bounds are applied twice on purpose. Moving onto a monitor with a
/// different scale factor makes Windows send WM_DPICHANGED, which arrives with
/// a suggested rectangle of its own and resizes the window; re-applying
/// afterwards puts it where it was actually asked to go.
/// </remarks>
public sealed class PickerWindow : Window
{
    private const int SwpNoActivate = 0x0010;
    private const int SwpNoZOrder = 0x0004;

    private readonly PickerCoordinator _picker;
    private readonly PickerCanvas _canvas;
    private readonly DispatcherTimer _frames;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private PickerMonitorChoice _monitorChoice;
    private bool _shuttingDown;

    public PickerWindow(PickerCoordinator picker, PickerMonitorChoice monitorChoice = PickerMonitorChoice.Primary)
    {
        ArgumentNullException.ThrowIfNull(picker);

        _picker = picker;
        _monitorChoice = monitorChoice;
        _canvas = new PickerCanvas(picker);

        Title = "PCMode mode picker";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        Focusable = true;
        Content = _canvas;

        AutomationProperties.SetName(this, "PCMode mode picker");
        AutomationProperties.SetHelpText(
            this,
            "Choose a mode with the arrow keys or the mouse. Enter applies it, Escape closes without changing anything.");

        _frames = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = picker.Tuning.FrameInterval,
        };

        _frames.Tick += (_, _) => Frame();
    }

    /// <summary>Which display the picker will appear on next time it opens.</summary>
    public PickerMonitorChoice MonitorChoice
    {
        get => _monitorChoice;
        set => _monitorChoice = value;
    }

    public bool IsPickerVisible => _picker.IsVisible;

    /// <summary>Opens the picker, or brings a fading one back.</summary>
    public void Open(string? currentModeName)
    {
        MonitorSurface surface = MonitorLayout.Find(_monitorChoice);

        PickerGeometry geometry = PickerGeometry.Create(
            surface.WorkArea,
            surface.Scale,
            _picker.Favorites,
            _picker.Tuning);

        // Before the window appears. A window opening under a resting cursor
        // produces a move message at that same position, and the picker uses
        // this snapshot to know that such a message is not the user moving the
        // mouse.
        PickerPoint pointer = MonitorLayout.PointerPosition();

        _picker.Show(Now, currentModeName, geometry, pointer);

        _canvas.Width = geometry.CanvasSize;
        _canvas.Height = geometry.CanvasSize;

        Width = geometry.CanvasSize;
        Height = geometry.CanvasSize;
        Opacity = _picker.Alpha;

        ApplyBounds(geometry);

        Show();

        // Again, now that the handle exists and any DPI change from the move
        // has been processed.
        ApplyBounds(geometry);

        Activate();
        Focus();

        _frames.Start();
    }

    /// <summary>Opens the picker, or closes it if it is already up.</summary>
    public void Toggle(string? currentModeName)
    {
        if (_picker.IsVisible)
        {
            Cancel();
            return;
        }

        Open(currentModeName);
    }

    /// <summary>
    /// Moves the selection from an input device that is not the keyboard.
    /// </summary>
    /// <remarks>
    /// A D-pad press and an arrow key arrive here identically, which is the
    /// point: the picker behaves the same whichever device drove it, because
    /// there is only one place that decides what a selection does.
    /// </remarks>
    public void SelectFromInput(PickerDirection direction)
    {
        if (_picker.SelectDirection(direction, Now))
        {
            _frames.Start();
            Repaint();
        }
    }

    /// <summary>Applies the selection, as the A button and Enter both do.</summary>
    public void ActivateFromInput()
    {
        if (_picker.Activate(Now))
        {
            _frames.Start();
        }
    }

    /// <summary>Closes the picker without applying anything.</summary>
    public void Cancel()
    {
        if (_picker.Cancel(Now))
        {
            _frames.Start();
        }
    }

    /// <summary>
    /// Stops everything and lets the window really close.
    /// </summary>
    /// <remarks>
    /// Called when the shell is shutting down. A picker still on screen after
    /// that would be a topmost, transparent, full-height window belonging to a
    /// program that has stopped listening to the keyboard.
    /// </remarks>
    public void Shutdown()
    {
        _shuttingDown = true;
        _frames.Stop();

        Hide();
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnKeyDown(e);

        if (!_picker.IsVisible)
        {
            return;
        }

        bool handled = e.Key switch
        {
            Key.Up => _picker.SelectDirection(PickerDirection.Up, Now),
            Key.Down => _picker.SelectDirection(PickerDirection.Down, Now),
            Key.Left => _picker.SelectDirection(PickerDirection.Left, Now),
            Key.Right => _picker.SelectDirection(PickerDirection.Right, Now),
            Key.Enter => _picker.Activate(Now),
            Key.Escape => _picker.Cancel(Now),
            _ => false,
        };

        if (!handled)
        {
            return;
        }

        e.Handled = true;
        _frames.Start();
        Repaint();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnMouseMove(e);

        if (!_picker.IsVisible)
        {
            return;
        }

        Point position = e.GetPosition(_canvas);

        // PointToScreen gives physical pixels, which is the same space the
        // pointer snapshot taken at Open was in. Comparing two positions in
        // different spaces would make every move look like a real one.
        Point screen = _canvas.PointToScreen(position);

        if (_picker.PointerMoved(
                new PickerPoint(position.X, position.Y),
                new PickerPoint(screen.X, screen.Y),
                Now))
        {
            _frames.Start();
            Repaint();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnMouseLeftButtonDown(e);

        if (!_picker.IsVisible)
        {
            return;
        }

        Point position = e.GetPosition(_canvas);

        if (_picker.PointerPressed(new PickerPoint(position.X, position.Y), Now))
        {
            e.Handled = true;
            _frames.Start();
        }
    }

    /// <summary>
    /// Closes the picker when something else takes the foreground.
    /// </summary>
    /// <remarks>
    /// Not in the original, and worth the difference. This window is topmost,
    /// transparent and most of the screen tall; if it survives losing focus it
    /// is an overlay the user can neither see the point of nor easily get rid
    /// of, sitting over whatever they switched to. Closing changes nothing --
    /// a picker only ever applies a mode on a deliberate choice.
    /// </remarks>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        if (!_shuttingDown && _picker.Phase is PickerPhase.Opening or PickerPhase.Open)
        {
            Cancel();
        }
    }

    /// <summary>
    /// Hides rather than closes, unless the shell really is going away.
    /// </summary>
    /// <remarks>
    /// A closed WPF Window cannot be shown again. Without this, one Alt+F4 on
    /// the picker would leave every later hotkey press doing nothing at all.
    /// </remarks>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!_shuttingDown)
        {
            e.Cancel = true;
            Cancel();
            return;
        }

        base.OnClosing(e);
    }

    private TimeSpan Now => _clock.Elapsed;

    private void Frame()
    {
        if (!_picker.Advance(Now))
        {
            if (!_picker.IsAnimating)
            {
                _frames.Stop();
            }

            return;
        }

        Opacity = _picker.Alpha;
        Repaint();

        if (_picker.Phase == PickerPhase.Hidden)
        {
            _frames.Stop();
            Hide();
            return;
        }

        if (!_picker.IsAnimating)
        {
            _frames.Stop();
        }
    }

    private void Repaint() => _canvas.InvalidateVisual();

    private void ApplyBounds(PickerGeometry geometry)
    {
        nint handle = new WindowInteropHelper(this).EnsureHandle();

        if (handle == 0)
        {
            return;
        }

        PixelRect bounds = geometry.WindowBounds;

        SetWindowPos(
            handle,
            0,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SwpNoActivate | SwpNoZOrder);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}

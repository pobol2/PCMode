using System.Windows.Interop;
using System.Windows.Threading;
using PCMode.Picker;
using PCMode.Tray.Platform.Input;
using PCMode.Tray.Elements.Settings;

namespace PCMode.Tray.Picker;

/// <summary>What <see cref="PickerHost"/> needs from the running engine.</summary>
/// <remarks>
/// The composition-root seam that replaces legacy's <c>ShellViewModel</c>
/// dependency -- Target has no shell-wide status view model (see
/// <c>ARCHITECTURE.md</c>'s "Permanently excluded"), so this reads and
/// applies straight through <see cref="PCMode.Engine.EngineRuntime"/>, the
/// same as <c>Platform/TrayIcon/TrayMenu.cs</c>'s <c>TrayMenuActions</c>.
/// </remarks>
public sealed record PickerHostActions(
    Func<string?> CurrentTargetKey,
    Func<string, Task> ApplyTarget);

/// <summary>
/// The native shell's global input: the hotkeys, the controller and the wheel.
/// </summary>
/// <remarks>
/// ALL OR NOTHING
///
/// The hotkeys are registered as a set. If any one of them is refused -- which
/// happens when another program already holds it -- every registration taken so
/// far is given back and the whole thing refuses with the offending key named.
/// Four keys out of five looks exactly like a working PCMode, so nobody reports
/// it; they report months later that one mode "sometimes doesn't switch", while
/// this process sits on registrations somebody else needed.
///
/// A refusal is not a startup failure. The shell still lists modes and applies
/// them from the tray, and the user is told which key it could not have.
///
/// ONE OWNER PER SESSION
///
/// There is no arbitration mechanism here, and deliberately so: a second copy of
/// this application never starts, because SingleInstance refuses it before
/// anything reaches this class.
/// </remarks>
public sealed class PickerHost : IDisposable
{
    private const int WmHotkey = 0x0312;

    /// <summary>HWND_MESSAGE: a window that receives messages and never appears.</summary>
    private static readonly nint MessageOnlyParent = -3;

    private readonly PickerHostActions _actions;
    private readonly HwndSource _messages;
    private readonly GlobalHotkeys _hotkeys;
    private readonly PickerWindow _window;
    private readonly PickerCoordinator _picker;
    private readonly ControllerPoller? _controller;
    private readonly XInputReader? _gamepads;
    private readonly DispatcherTimer? _polling;

    private bool _disposed;

    private PickerHost(
        PickerHostActions actions,
        HwndSource messages,
        GlobalHotkeys hotkeys,
        PickerCoordinator picker,
        PickerWindow window,
        XInputReader? gamepads,
        ControllerPoller? controller,
        DispatcherTimer? polling)
    {
        _actions = actions;
        _messages = messages;
        _hotkeys = hotkeys;
        _picker = picker;
        _window = window;
        _gamepads = gamepads;
        _controller = controller;
        _polling = polling;
    }

    /// <summary>Which XInput library answered, or null if none did.</summary>
    public string? ControllerLibrary => _gamepads?.Library;

    /// <summary>The hotkeys actually held.</summary>
    public IReadOnlyList<HotkeyBinding> Hotkeys => _hotkeys.Registered;

    /// <summary>
    /// Registers the global hotkeys and starts the picker, or explains why it
    /// could not.
    /// </summary>
    /// <param name="refusal">
    /// What to tell the user when the answer is null: which key was already
    /// taken. Windows will not say who took it, so naming the key is the most
    /// that can be offered -- and it is enough to go looking.
    /// </param>
    /// <param name="pollController">
    /// False switches controller polling off entirely, leaving the keyboard and
    /// mouse untouched.
    /// </param>
    public static PickerHost? TryStart(
        PickerHostActions actions,
        FavoriteSet favorites,
        IReadOnlyList<DirectShortcut> shortcuts,
        PickerMonitorChoice monitorChoice,
        bool pollController,
        out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(favorites);
        ArgumentNullException.ThrowIfNull(shortcuts);

        var messages = new HwndSource(new HwndSourceParameters("PCMode input")
        {
            ParentWindow = MessageOnlyParent,
            Width = 0,
            Height = 0,
        });

        var hotkeys = new GlobalHotkeys(messages.Handle);

        if (!hotkeys.TryRegisterAll(PickerHotkeys.BindingsFor(shortcuts), out string? failure))
        {
            // Nothing is held: TryRegisterAll rolled back whatever it had
            // taken, so whichever program owns that key keeps it and the rest
            // are free for a later attempt.
            hotkeys.Dispose();
            messages.Dispose();

            refusal = failure;
            return null;
        }

        var picker = new PickerCoordinator(favorites);
        var window = new PickerWindow(picker, monitorChoice);

        XInputReader? gamepads = null;
        ControllerPoller? controller = null;
        DispatcherTimer? polling = null;

        if (pollController)
        {
            gamepads = XInputReader.Load();
            controller = new ControllerPoller(gamepads);

            polling = new DispatcherTimer(DispatcherPriority.Input)
            {
                // Roughly thirty times a second: ample for a menu, and cheap
                // enough to leave running. A machine with no controller finds
                // nothing to do on every tick.
                Interval = TimeSpan.FromMilliseconds(33),
            };
        }

        var host = new PickerHost(actions, messages, hotkeys, picker, window, gamepads, controller, polling);

        messages.AddHook(host.OnMessage);
        picker.LaunchRequested += host.OnLaunchRequested;

        if (polling is not null)
        {
            polling.Tick += (_, _) => host.PollController();
            polling.Start();
        }

        refusal = null;
        return host;
    }

    /// <summary>Opens the picker, as the tray menu does.</summary>
    public void ShowPicker() => _window.Open(_actions.CurrentTargetKey());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Torn down in the reverse of the order it was built, so nothing can
        // arrive for a part that has already gone.
        _polling?.Stop();

        _picker.LaunchRequested -= OnLaunchRequested;
        _messages.RemoveHook(OnMessage);

        _window.Shutdown();

        // The hotkeys go back before the window they were registered against:
        // once the message-only window is destroyed there is nothing left to
        // unregister them from.
        _hotkeys.Dispose();
        _messages.Dispose();
        _gamepads?.Dispose();
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmHotkey)
        {
            return 0;
        }

        HotkeyBinding? binding = _hotkeys.Find((int)wParam);

        if (binding is null)
        {
            return 0;
        }

        handled = true;

        if (binding.Value.ModeName is string mode)
        {
            // A direct hotkey applies its mode straight away, without the
            // wheel. That is what the letters are for.
            Apply(mode);
            return 0;
        }

        _window.Toggle(_actions.CurrentTargetKey());

        return 0;
    }

    private void PollController()
    {
        if (_controller is null)
        {
            return;
        }

        foreach (ControllerCommand command in _controller.Poll(_picker.IsVisible))
        {
            switch (command.Action)
            {
                case ControllerAction.Open:
                    _window.Open(_actions.CurrentTargetKey());
                    break;

                case ControllerAction.Close:
                    _window.Cancel();
                    break;

                case ControllerAction.Activate:
                    ApplySelected();
                    break;

                case ControllerAction.Select:
                    Select(command.Direction);
                    break;
            }
        }
    }

    private void Select(PickerDirection direction) => _window.SelectFromInput(direction);

    private void ApplySelected() => _window.ActivateFromInput();

    private void OnLaunchRequested(object? sender, PickerLaunchEventArgs e) => Apply(e.ModeName);

    private void Apply(string mode) =>
        // Deliberately not awaited: this is a message handler and a timer tick.
        // ApplyTarget reports everything through the engine's own log, refuses
        // to start a second transition on its own, and does not throw.
        _ = _actions.ApplyTarget(mode);
}

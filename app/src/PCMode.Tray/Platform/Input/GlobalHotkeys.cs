using System.Runtime.InteropServices;

namespace PCMode.Tray.Platform.Input;

/// <summary>Modifier bits, as RegisterHotKey defines them.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0x0000,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,

    /// <summary>
    /// Do not repeat while the key is held.
    /// </summary>
    /// <remarks>
    /// A repeating DIRECT hotkey would mean holding Ctrl+Alt+G asks PCMode to apply
    /// Gaming again every few tens of milliseconds -- each one a real
    /// transition request contending for the same lock, each one preempting
    /// the last under newest-request-wins. There is no reading of a held key
    /// that makes that the user's intent.
    /// </remarks>
    NoRepeat = 0x4000,
}

/// <summary>One hotkey this application wants.</summary>
/// <param name="Id">Unique within the window it is registered on.</param>
/// <param name="Description">What to tell a user when this one is the one that could not be had.</param>
public readonly record struct HotkeyBinding(
    int Id,
    HotkeyModifiers Modifiers,
    uint VirtualKey,
    string Description,
    string? ModeName = null
);

/// <summary>The Win32 calls, behind an interface so a test can refuse one.</summary>
public interface IHotkeyRegistrar
{
    bool Register(nint window, int id, uint modifiers, uint virtualKey);

    bool Unregister(nint window, int id);
}

/// <summary>
/// Registers PCMode's global hotkeys, or none of them.
/// </summary>
/// <remarks>
/// ALL OR NOTHING, AND WHY
///
/// RegisterHotKey succeeds or fails per hotkey, and a partial success is the
/// worst outcome available. Four of five keys working looks exactly like
/// PCMode working, so the user does not report a broken installation -- they
/// report that one mode "sometimes doesn't switch", months later, having
/// blamed the television. Meanwhile this process is holding four registrations
/// that whatever wanted the fifth one could have had.
///
/// So a failure rolls back everything registered so far and reports which
/// binding failed. The shell still starts, still lists modes and still applies
/// them; what the user gets is the name of the key it could not have.
///
/// Any hotkey can be refused: another application had it first, and Windows
/// does not say who.
/// </remarks>
public sealed class GlobalHotkeys : IDisposable
{
    /// <summary>Virtual key code for Space.</summary>
    public const uint VirtualKeySpace = 0x20;

    /// <summary>The picker's own hotkey, distinct from any single mode's.</summary>
    public const int PickerHotkeyId = 1;

    private readonly nint _window;
    private readonly IHotkeyRegistrar _registrar;
    private readonly List<HotkeyBinding> _registered = [];

    private bool _disposed;

    public GlobalHotkeys(nint window, IHotkeyRegistrar? registrar = null)
    {
        _window = window;
        _registrar = registrar ?? new Win32HotkeyRegistrar();
    }

    /// <summary>What is currently registered. Empty unless a claim succeeded.</summary>
    public IReadOnlyList<HotkeyBinding> Registered => _registered;

    /// <summary>
    /// Takes every binding, or leaves the system exactly as it was found.
    /// </summary>
    /// <param name="failure">
    /// Which binding could not be had, when the answer is false.
    /// </param>
    public bool TryRegisterAll(IReadOnlyList<HotkeyBinding> bindings, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_registered.Count > 0)
        {
            failure = "Hotkeys are already registered.";
            return false;
        }

        foreach (HotkeyBinding binding in bindings)
        {
            if (_registrar.Register(_window, binding.Id, (uint)binding.Modifiers, binding.VirtualKey))
            {
                _registered.Add(binding);
                continue;
            }

            // The rollback. Without it this process would sit on whatever it
            // did get, which is worse than having none: the keys are then
            // owned by something that has already reported it cannot do the
            // job, and unavailable to the fallback that can.
            UnregisterAll();

            failure = $"{binding.Description} is already taken by another application.";
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>Gives every registered hotkey back. Safe when there are none.</summary>
    public void UnregisterAll()
    {
        foreach (HotkeyBinding binding in _registered)
        {
            _registrar.Unregister(_window, binding.Id);
        }

        _registered.Clear();
    }

    /// <summary>The binding a WM_HOTKEY id belongs to, if this owns it.</summary>
    public HotkeyBinding? Find(int id)
    {
        foreach (HotkeyBinding binding in _registered)
        {
            if (binding.Id == id)
            {
                return binding;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();
    }

    private sealed class Win32HotkeyRegistrar : IHotkeyRegistrar
    {
        public bool Register(nint window, int id, uint modifiers, uint virtualKey) =>
            RegisterHotKey(window, id, modifiers, virtualKey);

        public bool Unregister(nint window, int id) => UnregisterHotKey(window, id);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(nint hWnd, int id);
    }
}

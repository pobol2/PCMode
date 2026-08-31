using PCMode.Tray.Platform.Input;
using PCMode.Tray.Elements.Settings;

namespace PCMode.Tray.Picker;

/// <summary>
/// Which keys PCMode asks Windows for: the picker's own, plus one per
/// configured direct shortcut.
/// </summary>
/// <remarks>
/// This is application policy, not a mechanism, which is why it lives beside
/// the picker that acts on it rather than in <c>Platform</c>.
/// <see cref="GlobalHotkeys"/> knows how to claim a key and how to give it
/// back; it has no business knowing what a <see cref="DirectShortcut"/> is, and
/// making it know would point Platform at the settings layer above it.
///
/// Separate from registration so the mapping can be checked without claiming a
/// single real key.
/// </remarks>
public static class PickerHotkeys
{
    private const HotkeyModifiers CtrlAlt =
        HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.NoRepeat;

    public static IReadOnlyList<HotkeyBinding> BindingsFor(IReadOnlyList<DirectShortcut> shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);

        List<HotkeyBinding> bindings =
        [
            new HotkeyBinding(
                GlobalHotkeys.PickerHotkeyId,
                CtrlAlt,
                GlobalHotkeys.VirtualKeySpace,
                "Ctrl+Alt+Space (mode picker)"),
        ];

        int id = GlobalHotkeys.PickerHotkeyId;

        foreach (DirectShortcut shortcut in shortcuts)
        {
            id++;

            // A-Z virtual key codes are the uppercase ASCII values, which is
            // why only letters are supported here.
            bindings.Add(new HotkeyBinding(
                id,
                CtrlAlt,
                char.ToUpperInvariant(shortcut.Key),
                $"Ctrl+Alt+{char.ToUpperInvariant(shortcut.Key)} ({shortcut.Label})",
                shortcut.Target));
        }

        return bindings;
    }
}

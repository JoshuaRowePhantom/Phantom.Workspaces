using System;
using Avalonia.Input;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// Maps Win32 virtual-key codes and WebView2 accelerator kinds to Avalonia
/// <see cref="Key"/> and <see cref="KeyModifiers"/> values.
/// </summary>
internal static class VirtualKeyMap
{
    private const int SystemKeyDown = 2;
    private const int SystemKeyUp = 3;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    /// <summary>
    /// Derives the (<see cref="Key"/>, <see cref="KeyModifiers"/>) pair for a WebView2 accelerator
    /// event. The Alt modifier is inferred from the SystemKeyDown/SystemKeyUp kinds (WebView2 raises
    /// these when Alt is held); Ctrl and Shift are read via <paramref name="isKeyDown"/>.
    /// </summary>
    public static (Key key, KeyModifiers modifiers) Map(int kind, int vk, Func<int, bool> isKeyDown)
    {
        var mods = KeyModifiers.None;
        if (kind == SystemKeyDown || kind == SystemKeyUp)
        {
            mods |= KeyModifiers.Alt;
        }

        if (isKeyDown(VkControl))
        {
            mods |= KeyModifiers.Control;
        }

        if (isKeyDown(VkShift))
        {
            mods |= KeyModifiers.Shift;
        }

        return (ToKey(vk), mods);
    }

    /// <summary>Translates a Win32 virtual-key code to an Avalonia <see cref="Key"/>.</summary>
    public static Key ToKey(int vk)
    {
        // Letters A..Z
        if (vk >= 0x41 && vk <= 0x5A)
        {
            return (Key)((int)Key.A + (vk - 0x41));
        }

        // Digits 0..9 (top row)
        if (vk >= 0x30 && vk <= 0x39)
        {
            return (Key)((int)Key.D0 + (vk - 0x30));
        }

        // Function keys F1..F24
        if (vk >= 0x70 && vk <= 0x87)
        {
            return (Key)((int)Key.F1 + (vk - 0x70));
        }

        // Numpad digits
        if (vk >= 0x60 && vk <= 0x69)
        {
            return (Key)((int)Key.NumPad0 + (vk - 0x60));
        }

        return vk switch
        {
            0x08 => Key.Back,
            0x09 => Key.Tab,
            0x0D => Key.Enter,
            VkShift => Key.LeftShift,
            VkControl => Key.LeftCtrl,
            VkMenu => Key.LeftAlt,
            0x13 => Key.Pause,
            0x14 => Key.CapsLock,
            0x1B => Key.Escape,
            0x20 => Key.Space,
            0x21 => Key.PageUp,
            0x22 => Key.PageDown,
            0x23 => Key.End,
            0x24 => Key.Home,
            0x25 => Key.Left,
            0x26 => Key.Up,
            0x27 => Key.Right,
            0x28 => Key.Down,
            0x2C => Key.PrintScreen,
            0x2D => Key.Insert,
            0x2E => Key.Delete,
            0x5B => Key.LWin,
            0x5C => Key.RWin,
            0x6A => Key.Multiply,
            0x6B => Key.Add,
            0x6D => Key.Subtract,
            0x6E => Key.Decimal,
            0x6F => Key.Divide,
            0xBA => Key.OemSemicolon,
            0xBB => Key.OemPlus,
            0xBC => Key.OemComma,
            0xBD => Key.OemMinus,
            0xBE => Key.OemPeriod,
            0xBF => Key.OemQuestion,
            0xC0 => Key.OemTilde,
            0xDB => Key.OemOpenBrackets,
            0xDC => Key.OemPipe,
            0xDD => Key.OemCloseBrackets,
            0xDE => Key.OemQuotes,
            _ => Key.None,
        };
    }
}

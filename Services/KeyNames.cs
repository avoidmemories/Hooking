using System.Windows.Input;

namespace SnapHook.Services;

/// <summary>
/// Converts between the key names stored in settings, Windows virtual keys and
/// scan codes. The worker speaks scan codes because that is the only identifier
/// that survives a keyboard layout change.
/// </summary>
public static class KeyNames
{
    private const uint MapvkVkToVscEx = 4;

    private static readonly Dictionary<string, int> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["Spacebar"] = 0x20,
        ["Tab"] = 0x09,
        ["Enter"] = 0x0D,
        ["Return"] = 0x0D,
        ["Escape"] = 0x1B,
        ["Esc"] = 0x1B,
        ["Backspace"] = 0x08,
        ["CapsLock"] = 0x14,
        ["Up"] = 0x26,
        ["Down"] = 0x28,
        ["Left"] = 0x25,
        ["Right"] = 0x27,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["="] = 0xBB,
        ["+"] = 0xBB,
        ["-"] = 0xBD,
        [","] = 0xBC,
        ["."] = 0xBE,
        ["/"] = 0xBF,
        [";"] = 0xBA,
        ["'"] = 0xDE,
        ["["] = 0xDB,
        ["]"] = 0xDD,
        ["\\"] = 0xDC,
        ["`"] = 0xC0,

        // Left and right modifiers are distinct keys, and games often bind one
        // side only. They must resolve to their own virtual keys rather than the
        // generic shift/ctrl/alt codes.
        ["Shift"] = 0xA0,
        ["LShift"] = 0xA0,
        ["RShift"] = 0xA1,
        ["LeftShift"] = 0xA0,
        ["RightShift"] = 0xA1,
        ["Ctrl"] = 0xA2,
        ["LCtrl"] = 0xA2,
        ["RCtrl"] = 0xA3,
        ["LeftCtrl"] = 0xA2,
        ["RightCtrl"] = 0xA3,
        ["Alt"] = 0xA4,
        ["LAlt"] = 0xA4,
        ["RAlt"] = 0xA5,
        ["LeftAlt"] = 0xA4,
        ["RightAlt"] = 0xA5
    };

    /// <summary>
    /// Resolves a stored key name to a virtual key code, or 0 when unknown.
    /// </summary>
    public static int ToVirtualKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return 0;
        }

        var text = name.Trim();

        if (Aliases.TryGetValue(text, out var aliased))
        {
            return aliased;
        }

        // A single letter or digit.
        if (text.Length == 1)
        {
            var character = char.ToUpperInvariant(text[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return character;
            }
        }

        // A named key such as D1, NumPad4, F7, OemPlus.
        if (Enum.TryParse<Key>(text, true, out var key))
        {
            var virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey != 0)
            {
                return virtualKey;
            }
        }

        return 0;
    }

    /// <summary>
    /// Converts a virtual key to a scan code. MPVK_VK_TO_VSC_EX is used first so
    /// extended keys keep their E0 prefix.
    /// </summary>
    public static int ToScanCode(int virtualKey)
    {
        if (virtualKey == 0)
        {
            return 0;
        }

        var scan = (int)MapVirtualKey((uint)virtualKey, MapvkVkToVscEx);
        if (scan == 0)
        {
            scan = (int)MapVirtualKey((uint)virtualKey, 0);
        }

        // The extended variant may return the E0 prefix as the high byte.
        return scan & 0xFF;
    }

    /// <summary>
    /// Bit added to a scan code to mark an E0-prefixed key.
    ///
    /// Without it several keys collide, because they share a scan code and differ
    /// only by the E0 prefix: right Ctrl and left Ctrl are both 0x1D, right Alt
    /// and left Alt are both 0x38, and the numeric keypad's 0 and 9 are 0x52 and
    /// 0x49, which are Insert and Page Up. Using the bare scan code as the key's
    /// identity would silently make those the same key.
    /// </summary>
    public const int ExtendedFlag = 0x100;

    /// <summary>
    /// A key's full identity: scan code plus the extended marker. This is what
    /// the engine receives, and it is what must be passed on the command line.
    /// </summary>
    public static int ToKeyId(string? name)
    {
        var virtualKey = ToVirtualKey(name);
        if (virtualKey == 0)
        {
            return 0;
        }

        var scanCode = ToScanCode(virtualKey);
        return scanCode == 0 ? 0 : scanCode | (IsExtendedVirtualKey(virtualKey) ? ExtendedFlag : 0);
    }

    /// <summary>
    /// True for keys the keyboard reports with the E0 prefix.
    /// </summary>
    public static bool IsExtendedVirtualKey(int virtualKey) => virtualKey switch
    {
        0xA3 => true, // right ctrl
        0xA5 => true, // right alt
        0x2D => true, // insert
        0x2E => true, // delete
        0x24 => true, // home
        0x23 => true, // end
        0x21 => true, // page up
        0x22 => true, // page down
        0x25 => true, // left
        0x26 => true, // up
        0x27 => true, // right
        0x28 => true, // down
        0x6F => true, // numpad divide
        0x90 => true, // num lock
        0x2C => true, // print screen
        0x5B => true, // left windows
        0x5C => true, // right windows
        0x5D => true, // applications
        _ => false
    };

    /// <summary>
    /// Turns a WPF key into the display name stored in settings. Mirrors the
    /// naming ONEKEY uses so existing muscle memory carries over.
    /// </summary>
    public static string FromKey(Key key, ModifierKeys modifiers)
    {
        var baseName = FromKey(key);

        // A modifier is bound as itself, never as part of a chord: pressing
        // Shift while capturing must record "LShift", not "Shift+something".
        if (IsModifier(key))
        {
            return baseName;
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        parts.Add(baseName);
        return string.Join("+", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static string FromKey(Key key) => key switch
    {
        Key.Space => "Space",
        Key.Tab => "Tab",
        Key.Enter => "Enter",
        Key.Escape => "Escape",
        Key.Back => "Backspace",
        Key.Capital => "CapsLock",
        // Each side keeps its own name. Collapsing them to "Shift" would make the
// two indistinguishable, and a game that binds right shift alone could
// never be matched.
        Key.LeftShift => "LShift",
        Key.RightShift => "RShift",
        Key.LeftCtrl => "LCtrl",
        Key.RightCtrl => "RCtrl",
        Key.LeftAlt => "LAlt",
        Key.RightAlt => "RAlt",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Insert => "Insert",
        Key.Delete => "Delete",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        >= Key.A and <= Key.Z => key.ToString().ToUpperInvariant(),
        >= Key.D0 and <= Key.D9 => key.ToString()[1..],
        >= Key.NumPad0 and <= Key.NumPad9 => "NumPad" + key.ToString()[^1..],
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemTilde => "`",
        _ => key.ToString()
    };

    public static bool IsModifier(Key key) =>
        key is Key.LeftShift or Key.RightShift
            or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.System;

    /// <summary>
    /// True when a stored name identifies a modifier, so the interface can say
    /// that modifiers are bound individually rather than as a chord.
    /// </summary>
    public static bool IsModifierName(string name) =>
        name.Equals("Shift", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LShift", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("RShift", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LCtrl", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("RCtrl", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Alt", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LAlt", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("RAlt", StringComparison.OrdinalIgnoreCase);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}

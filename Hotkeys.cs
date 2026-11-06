using static ReelsOverlay.Native;

namespace ReelsOverlay;

// Hotkey text is "Mod+Mod+Key", e.g. "F2", "Alt+F3", "Ctrl+Shift+PageDown". Modifiers are
// Ctrl, Alt, Shift and Win; the key is a System.Windows.Forms.Keys name, with digits written
// plainly ("5", not "D5").
static class Hotkeys
{
    // Config key, label shown in settings. Order is the order used everywhere.
    public static readonly (string Name, string Label)[] Actions =
    [
        ("PlayPause", "Pause / resume"),
        ("Next", "Next reel"),
        ("Previous", "Previous reel"),
        ("Mute", "Mute / unmute"),
        ("VolumeDown", "Volume down"),
        ("VolumeUp", "Volume up"),
        ("ShowHide", "Hide / show"),
        ("EditMode", "Edit mode"),
    ];

    public static bool TryParse(string text, out uint mods, out Keys key)
    {
        mods = 0;
        key = Keys.None;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts[..^1])
        {
            uint m = ModifierFlag(part);
            if (m == 0) return false;
            mods |= m;
        }
        string name = parts[^1];
        if (name.Length == 1 && char.IsDigit(name[0])) name = "D" + name;
        return !name.All(char.IsDigit)
            && Enum.TryParse(name, true, out key)
            && key != Keys.None
            && ((int)key & ~(int)Keys.KeyCode) == 0
            && !IsModifierKey(key);
    }

    public static uint ModifierFlag(string name) => name.ToLowerInvariant() switch
    {
        "ctrl" or "control" => MOD_CONTROL,
        "alt" => MOD_ALT,
        "shift" => MOD_SHIFT,
        "win" => MOD_WIN,
        _ => 0,
    };

    public static string Format(uint mods, Keys key)
    {
        var parts = new List<string>();
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & MOD_WIN) != 0) parts.Add("Win");
        if (key != Keys.None) parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    // Keys has several names per value (Next/PageDown, Return/Enter); pick the familiar one.
    public static string KeyName(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
        Keys.PageDown => "PageDown",
        Keys.PageUp => "PageUp",
        Keys.Enter => "Enter",
        Keys.CapsLock => "CapsLock",
        _ => key.ToString(),
    };

    public static bool IsModifierKey(Keys key) => key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu
        or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey
        or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;

    // Why a combination should not be a global hotkey, or null if it is fine.
    public static string? Problem(uint mods, Keys key)
    {
        bool typing = key is >= Keys.A and <= Keys.Z or >= Keys.D0 and <= Keys.D9
            or Keys.Space or Keys.Enter or Keys.Tab or Keys.Back or Keys.Escape
            || (key >= Keys.OemSemicolon && key <= Keys.OemBackslash);
        if (typing && (mods & (MOD_CONTROL | MOD_ALT | MOD_WIN)) == 0)
            return $"{Format(mods, key)} would stop that key from typing in every app";
        if (mods == MOD_ALT && key is Keys.F4 or Keys.Tab or Keys.Escape)
            return $"{Format(mods, key)} belongs to Windows";
        return null;
    }

    // Modifier names for the mouse chord ("Alt", "Alt+Shift"); empty means the hook is off.
    public static int[] MouseModifierVks(string text, out bool valid)
    {
        valid = true;
        var vks = new List<int>();
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (ModifierFlag(part))
            {
                case MOD_CONTROL: vks.Add(VK_CONTROL); break;
                case MOD_ALT: vks.Add(VK_MENU); break;
                case MOD_SHIFT: vks.Add(VK_SHIFT); break;
                case MOD_WIN: vks.Add(VK_LWIN); break;
                default: valid = false; return [];
            }
        }
        return vks.ToArray();
    }
}

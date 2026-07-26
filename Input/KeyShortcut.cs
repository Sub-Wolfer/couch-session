namespace CouchMode.Input;

/// <summary>
/// A keyboard combination, stored as a single <see cref="Keys"/> value.
///
/// Named KeyShortcut, not Shortcut: System.Windows.Forms already has a Shortcut — the old
/// menu-accelerator enum — and since this file lives in a project with the WinForms namespace
/// imported everywhere, the bare name is ambiguous at every use site. It pairs with PadShortcut
/// this way too, which is the more useful reading.
///
/// Keys already packs the modifiers into the high bits — Keys.Control | Keys.Alt | Keys.C is one
/// value — so there is nothing to invent here and nothing to keep in sync. It saves as an int,
/// which means the config file survives a rename of anything in this file.
/// </summary>
public static class KeyShortcut
{
    /// <summary>The key itself, with the modifier bits stripped off.</summary>
    public static Keys KeyOf(int shortcut) => (Keys)shortcut & Keys.KeyCode;

    /// <summary>Modifier flags in the form RegisterHotKey wants, which is not the form Keys uses.</summary>
    public static uint ModifiersOf(int shortcut)
    {
        var keys = (Keys)shortcut;
        uint mods = 0;

        if (keys.HasFlag(Keys.Alt)) mods |= ModAlt;
        if (keys.HasFlag(Keys.Control)) mods |= ModControl;
        if (keys.HasFlag(Keys.Shift)) mods |= ModShift;

        return mods;
    }

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>
    /// Whether this is a combination Windows will actually accept.
    ///
    /// A bare letter is refused on purpose rather than by Windows: registering "C" as a global
    /// hotkey takes that key away from every other program on the machine, which is a thing
    /// people do once by accident and then cannot work out why their typing is broken. At least
    /// one modifier is required, and function keys are allowed on their own because that is
    /// what they are for.
    /// </summary>
    public static bool IsUsable(int shortcut)
    {
        var key = KeyOf(shortcut);
        if (key is Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
            return false;

        bool functionKey = key is >= Keys.F1 and <= Keys.F24;

        return ModifiersOf(shortcut) != 0 || functionKey;
    }

    /// <summary>Human form, in the order people say it: Ctrl+Shift+Alt+Key.</summary>
    public static string Describe(int shortcut)
    {
        if (shortcut == 0) return "";

        var keys = (Keys)shortcut;
        var parts = new List<string>();

        if (keys.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (keys.HasFlag(Keys.Shift)) parts.Add("Shift");
        if (keys.HasFlag(Keys.Alt)) parts.Add("Alt");

        parts.Add(NameOf(KeyOf(shortcut)));

        return string.Join(" + ", parts);
    }

    /// <summary>
    /// Readable names for the keys whose enum names are not what is printed on the key.
    /// Anything not listed uses its own name, which is right for letters, digits and F-keys.
    /// </summary>
    private static string NameOf(Keys key) => key switch
    {
        // Shown as ~ rather than ` — the same physical key either way.
        //
        // VK_OEM_3 is one key with two faces: unshifted it types a backtick, shifted a tilde. The
        // shortcut binds the key, not the character, so nothing about the binding changes here and no
        // Shift is involved — but "~" is what is printed largest on the keycap and what people call the
        // key, while "`" is easy to mistake for an apostrophe at small sizes.
        Keys.Oemtilde => "~",
        Keys.OemMinus => "-",
        Keys.Oemplus => "=",
        Keys.OemOpenBrackets => "[",
        Keys.OemCloseBrackets => "]",
        Keys.OemPipe => "\\",
        Keys.OemSemicolon => ";",
        Keys.OemQuotes => "'",
        Keys.Oemcomma => ",",
        Keys.OemPeriod => ".",
        Keys.OemQuestion => "/",
        Keys.Space => "Space",
        Keys.Return => "Enter",
        Keys.Escape => "Esc",
        Keys.Prior => "Page Up",
        Keys.Next => "Page Down",
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => $"Numpad {key - Keys.NumPad0}",
        _ => key.ToString(),
    };
}

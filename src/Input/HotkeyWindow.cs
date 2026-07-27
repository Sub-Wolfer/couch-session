using System.Runtime.InteropServices;

namespace CouchMode.Input;

/// <summary>
/// Holds the global keyboard shortcut and tells someone when it is pressed.
///
/// A hidden window of its own rather than hanging off an existing one. RegisterHotKey delivers
/// WM_HOTKEY to a specific window handle, so whatever registers has to be something that can
/// see that message — and giving the shortcut its own window means registering, re-registering
/// and unregistering never disturbs anything else the app is listening for.
///
/// Registration can fail, and does: another program may already own the combination. That is
/// reported rather than swallowed, because a shortcut that silently does nothing is
/// indistinguishable from a broken app, and the fix — pick a different combination — is
/// something only the user can do.
/// </summary>
public sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private readonly Action _pressed;
    private int _registered;

    /// <summary>
    /// The id RegisterHotKey files this under.
    ///
    /// Has to be within 0x0000–0xBFFF. Anything above that is reserved for the range DLLs get
    /// through GlobalAddAtom, and this was 0xC0DE — a cute constant that is 0xDF past the top of
    /// the legal range. RegisterHotKey accepted it and reported success, the log said the
    /// shortcut was registered, and pressing it did nothing at all: the one failure mode where
    /// every visible signal says the feature is working.
    /// </summary>
    /// <remarks>
    /// Given per instance rather than fixed, because there is more than one keyboard hotkey now.
    /// Windows requires the id to be unique among the hotkeys a thread has registered, and two
    /// windows sharing one id is exactly the sort of collision that fails quietly — the second
    /// registration is refused and the shortcut simply never fires.
    /// </remarks>
    private readonly int _id;

    private const int WM_HOTKEY = 0x0312;

    public HotkeyWindow(Action pressed, int id = 0x0C0D)
    {
        _pressed = pressed;
        _id = id;
        CreateHandle(new CreateParams());
    }

    /// <summary>Whether the current shortcut is actually held by this app.</summary>
    public bool Registered => _registered != 0;

    /// <summary>
    /// Take a new shortcut, giving up the old one first. Zero means "no shortcut".
    /// </summary>
    /// <returns>False when Windows refused it, which almost always means something else has it.</returns>
    public bool Apply(int shortcut)
    {
        Release();

        if (shortcut == 0 || !KeyShortcut.IsUsable(shortcut)) return true;

        uint mods = KeyShortcut.ModifiersOf(shortcut);
        uint key = (uint)KeyShortcut.KeyOf(shortcut);

        // MOD_NOREPEAT: without it, holding the combination fires over and over, which for
        // something that switches displays would mean a stream of session toggles.
        if (!RegisterHotKey(Handle, _id, mods | ModNoRepeat, key))
        {
            Log.Warn($"Could not register the shortcut {KeyShortcut.Describe(shortcut)} — "
                   + "another program is probably using it.");
            return false;
        }

        _registered = shortcut;
        Log.Info($"Shortcut {KeyShortcut.Describe(shortcut)} registered.");
        return true;
    }

    private void Release()
    {
        if (_registered == 0) return;

        UnregisterHotKey(Handle, _id);
        _registered = 0;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == _id)
        {
            try { _pressed(); }
            catch (Exception ex) { Log.Error("The shortcut action failed", ex); }

            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Release();
        DestroyHandle();
    }

    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace CouchMode.Steam;

/// <summary>
/// Keeps the Big Picture window on top of the desktop clutter during a session, and gets it out
/// of the way the moment a game or launcher comes forward.
///
/// The problem it solves: after switching to the TV, Steam's own desktop window, a stray dialog,
/// or an update popup can end up in front of Big Picture on the television, leaving the couch
/// staring at the wrong thing with no easy way back. Pinning Big Picture topmost fixes that — but
/// a topmost window would then cover the very game or launcher the user opens to play. So the pin
/// is conditional: held while Big Picture (or the desktop/Steam) is the foreground, released as
/// soon as something else the user launched takes the front.
///
/// Toast notifications are unaffected: Windows draws them in a band above even topmost windows.
/// </summary>
public sealed class BigPictureFront : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private bool _appliedTopmost;
    private int _otherStreak;

    // Focus is handed to Big Picture once each time it becomes the active couch UI, not every tick —
    // continuously raising it fought a launcher trying to come forward. Reset whenever a game or
    // launcher takes over, so focus is handed back once more when they exit and Big Picture returns.
    private bool _focusedOnce;

    // True once the guard has handed the foreground to a launcher or game this session. When that
    // launch ends, the foreground is left on whatever the closing window handed it to — not Big
    // Picture — so the controller stops driving the library until it is clicked. This flag is the
    // cue to take focus straight back the moment the launch is over.
    private bool _handedToLaunch;

    // Log the topmost decision only when it flips, so a session leaves a readable trail without a
    // line every fifth of a second.
    private bool? _lastKeptOnTop;

    // Polled several times a second: fast enough that a launcher is uncovered almost as soon as it
    // opens and the pointer is yanked back to the corner before the guide-button cursor is noticed,
    // cheap enough to run for a whole session.
    private const int IntervalMs = 200;

    /// <summary>Whether the user wants Big Picture pinned on top at all. Off lets them alt-tab.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The window of the game (or its launcher) the watcher is tracking, or zero when there is none.
    /// Set by the tray app. Used to hand the foreground to a launcher that opened behind Big Picture
    /// without taking it — moving focus off Big Picture is the only thing that stops Steam from
    /// raising Big Picture straight back on top of it.
    /// </summary>
    public Func<IntPtr>? RunningGameWindow { get; set; }

    /// <summary>
    /// Set true while a watched game (or its pre-game launcher) is running, from the process watcher.
    /// This is the dependable release signal for launchers that open behind Big Picture without
    /// taking focus and before Steam's RunningAppID is set — the case that left DOOM's launcher
    /// pinned underneath. Written from the watcher thread, read on the timer; a plain bool is fine.
    /// </summary>
    public volatile bool GameActive;

    /// <summary>
    /// Steam's RunningAppID at the moment the session began. A game carried over from a previous
    /// session is already recorded here, so it can be told apart from something the user launches
    /// once the couch UI is up — see <see cref="LaunchedSinceStart"/>.
    /// </summary>
    private int _baselineAppId;

    public BigPictureFront()
    {
        _timer = new System.Windows.Forms.Timer { Interval = IntervalMs };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Start guarding. Called when a session becomes active.</summary>
    public void Begin()
    {
        _otherStreak = 0;
        _appliedTopmost = false;
        _focusedOnce = false;
        _handedToLaunch = false;
        _lastKeptOnTop = null;

        // Whatever Steam is already running as the couch UI comes up is the baseline: a game left
        // over from a previous session. Only an app that appears after this is a launch the user just
        // made, and only that should drop the pin.
        _baselineAppId = CurrentAppId();

        // The guard runs for the whole session even with always-on-top off. The pin is optional, but
        // keeping the controller able to drive Big Picture — handing focus to a launcher and taking
        // it back when that launcher closes — is not; only the topmost pin itself is gated on Enabled.
        _timer.Start();

        // Clear any pin left from an earlier on-state when starting (or re-entering) with the pin off,
        // so toggling always-on-top off mid-session still lifts Big Picture off the top layer.
        if (!Enabled)
        {
            var bp = BigPictureLauncher.FindWindow();
            if (bp != IntPtr.Zero) BigPictureLauncher.SetAlwaysOnTop(bp, false);
        }

        Tick();   // assert immediately rather than waiting a whole interval
    }

    /// <summary>Stop guarding and release the pin, so nothing is left topmost on the desktop.</summary>
    public void End()
    {
        _timer.Stop();

        var bp = BigPictureLauncher.FindWindow();
        if (bp != IntPtr.Zero) BigPictureLauncher.SetAlwaysOnTop(bp, false);
        _appliedTopmost = false;

        RestoreSystemCursor();   // never leave the session with an invisible pointer
    }

    private void Tick()
    {
        try
        {
            var bp = BigPictureLauncher.FindWindow();
            if (bp == IntPtr.Zero) return;   // not up yet; try again next tick

            // "A launch the user just made this session", by two signals that cover each other:
            //   • GameActive — the process watcher saw a listed game or its pre-game launcher launch.
            //     Fast and dependable (it fires before Steam's RunningAppID and even for launchers
            //     that open without focus), and NOT raised for a game adopted from a previous session,
            //     so it never fires for a leftover game the user still has to reach via the library.
            //   • LaunchedSinceStart — Steam's RunningAppID changed to something that was not already
            //     running when the session began. Covers games not on the watch list too, while a
            //     carried-over game shares the baseline id and so never trips it.
            bool launching = GameActive || LaunchedSinceStart();

            // A closed launcher whose game never started can leave Steam's RunningAppID stuck set, so
            // LaunchedSinceStart never clears and 'launching' would stay true forever — trapping the
            // controller on a game that is gone. So also treat the launch as over once we had handed
            // focus to it, the process watcher sees no tracked game, and no game or launcher window
            // actually exists any more. (The window check is only reached in that narrow case, so it
            // costs nothing on a normal tick.)
            bool launchGone = _handedToLaunch && !GameActive
                           && (RunningGameWindow?.Invoke() ?? IntPtr.Zero) == IntPtr.Zero;

            // The moment a launch ends, take the foreground back to Big Picture. When a launcher or
            // game closes, Windows leaves the foreground on whatever it handed off to — rarely Big
            // Picture — so the controller cannot drive the library until it is clicked. This runs
            // whether or not the pin is enabled, because the controller needs focus either way.
            if (_handedToLaunch && (!launching || launchGone))
            {
                ForceForeground(bp);
                _handedToLaunch = false;
                _focusedOnce = true;   // just focused it; the pin branch need not grab again
                if (launchGone)
                    Log.Info("Launcher closed with no game left; handed the controller back to Big Picture.");
            }

            // Held on top unless the user turned it off, a launch is up, or something is genuinely in
            // front (GameOrLauncherInFront is the backstop for that).
            // Big Picture is the active couch interface right now: nothing is launching and nothing has
            // genuinely come in front of it. This — NOT the pin — is what governs whether the controller
            // should be driving it and the pointer should be hidden, so both keep working with the
            // always-on-top pin turned off. Only the topmost pin itself is gated on Enabled.
            bool couchUiUp = !launching && !GameOrLauncherInFront(bp);
            bool keepOnTop = Enabled && couchUiUp;

            if (_lastKeptOnTop != keepOnTop)
            {
                Log.Info($"Big Picture front: {(keepOnTop ? "on top" : "released")} — "
                       + $"enabled={Enabled}, gameActive={GameActive}, launched={LaunchedSinceStart()}, "
                       + $"appId={CurrentAppId()}, baseline={_baselineAppId}, "
                       + $"foreground={ForegroundClass()}.");
                _lastKeptOnTop = keepOnTop;
            }

            // Lift the pin once when it should no longer be held. While on top, the reassert happens
            // below, after focus is settled, so toasts finish above Big Picture rather than flickering.
            if (!keepOnTop && _appliedTopmost)
            {
                BigPictureLauncher.SetAlwaysOnTop(bp, false);
                _appliedTopmost = false;
            }

            if (couchUiUp)
            {
                // Steam minimises Big Picture when a game launches; when that game quits, Big Picture is
                // the couch UI again but may still be iconic for a moment before Steam restores it — so
                // bring it back itself rather than waiting on Steam, which is what left the TV showing
                // the desktop (and the controller with nothing to drive) after a game was quit.
                if (IsIconic(bp)) ShowWindow(bp, SW_SHOWNOACTIVATE);

                // Hand focus to Big Picture once, not every tick. Steam Input only drives it while it
                // is the focused window, so at session start, or when a game exits and Big Picture
                // returns, focus is handed back if the desktop or a window on another monitor holds it.
                // Doing this only once keeps it from fighting a launcher coming forward on this screen.
                var fg = GetForegroundWindow();
                bool onOther = fg != bp && OnAnotherMonitor(fg, bp);

                // The Start menu, Search, and the taskbar are shell chrome that opens over Big Picture
                // on its own monitor and band above even a topmost window, so take the foreground back
                // whenever any is up (which also dismisses the Start menu) — every tick it is present.
                bool chromeInFront = fg != bp && (IsStartOrSearch(fg) || IsTaskbar(fg));

                // A *different* Steam window holding the foreground while the couch UI should be up — the
                // Steam client, or the Chrome_WidgetWin_1 overlay that pops up when a game exits — is what
                // left the controller unable to drive Big Picture until it was manually tabbed back to.
                // Reclaim it every tick that happens, not just once: there is no game running here (couch
                // UI is up), so it is never a launcher we would be fighting.
                bool steamStole = fg != bp && IsSteamOwned(fg);

                if (chromeInFront
                 || steamStole
                 || onOther
                 || (!_focusedOnce && fg != bp && (fg == IntPtr.Zero || IsShell(fg))))
                    ForceForeground(bp);

                _focusedOnce = true;

                // The topmost pin is the only part gated on the user's always-on-top setting. Reassert
                // it last, after any focus change above, so nothing Steam raised buries it and the
                // toasts end up above Big Picture.
                if (keepOnTop)
                {
                    PinTopmostWithToasts(bp);
                    _appliedTopmost = true;
                }

                // Make the pointer invisible while Big Picture owns the screen. Pressing the Steam guide
                // button makes Big Picture reveal the cursor — a quirk of Big Picture itself — so the
                // system cursor image is swapped for a blank one. Runs whether or not the pin is on,
                // because a stray pointer sitting on the couch UI is just as wrong either way.
                HideSystemCursor();
            }
            else
            {
                _focusedOnce = false;   // hand focus back once more when Big Picture is next in front
                RestoreSystemCursor();

                // While a launch is active, Big Picture becomes the couch backdrop: full-screen,
                // never minimised, with the launcher or game sitting above it. Two moves make that
                // happen, and both matter:
                //
                //   1. Hand the foreground to the launcher if Big Picture still holds it. A launcher
                //      can open behind Big Picture without taking focus (idTech's does), and while
                //      Big Picture stays the foreground window Steam keeps raising it back on top —
                //      so only moving focus onto the launcher makes it stick.
                //   2. Steam answers that by MINIMISING Big Picture (its default "a game launched"
                //      behaviour). We do not want it gone — the TV would show the desktop behind a
                //      small launcher. So if it went iconic, bring it back without stealing focus and
                //      drop it directly beneath the launcher: a full-screen backing, not minimised.
                //
                // Gated on a launch so that releasing simply because the user turned always-on-top
                // off to alt-tab leaves Big Picture alone.
                if (launching && RunningGameWindow?.Invoke() is { } target && target != IntPtr.Zero)
                {
                    if (GetForegroundWindow() == bp) ForceForeground(target);
                    if (IsIconic(bp)) ShowWindow(bp, SW_SHOWNOACTIVATE);
                    SetWindowPos(bp, target, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

                    // Remember we moved focus off Big Picture, so it is reclaimed once this ends.
                    _handedToLaunch = true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Big Picture front guard failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The app Steam is running right now, from the RunningAppID it keeps in the registry, or 0 when
    /// it is running nothing. Set the moment a launch begins — before the game window exists, while a
    /// pre-game launcher is still on screen — and back to 0 when the game exits.
    /// </summary>
    private static int CurrentAppId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("RunningAppID") is int id ? id : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// True once Steam is running an app that was not already running when the session began — a game
    /// or its pre-game launcher the user has just started from Big Picture, which is exactly when the
    /// pin has to drop so that launcher lands in front rather than behind. A game carried over from a
    /// previous session keeps the baseline id and so never trips this; it is reached via the library.
    /// </summary>
    private bool LaunchedSinceStart()
    {
        int id = CurrentAppId();
        return id != 0 && id != _baselineAppId;
    }

    /// <summary>
    /// Whether the thing in front is a game or launcher the user opened — as opposed to Big
    /// Picture itself, the desktop, or the Steam client, none of which should push Big Picture down.
    ///
    /// A window that fills its monitor is taken as a game straight away. Anything else that holds
    /// the foreground for a couple of ticks is taken as a launcher: launchers are windowed, so
    /// there is no size test that catches them, but the user staying on one is signal enough.
    /// </summary>
    private bool GameOrLauncherInFront(IntPtr bp)
    {
        var fg = GetForegroundWindow();

        // A Steam-owned window (the client, its Chrome helper, or the overlay) is never the game or
        // launcher the user opened to play — the game is always its own executable. Treating one as a
        // launcher (via the streak below) is what kept the couch UI "not up" after a game quit, so its
        // grab-back never ran and the controller was stuck until Big Picture was tabbed to by hand.
        if (fg == IntPtr.Zero || fg == bp || IsShell(fg) || IsStartOrSearch(fg) || IsSteamOwned(fg)
            || fg == BigPictureLauncher.FindDesktopWindow())
        {
            _otherStreak = 0;
            return false;
        }

        // A window on another monitor is a desktop app that happens to have focus — Discord, a
        // browser — not a launcher opened for the couch. Releasing Big Picture for it would drop the
        // couch UI off the top on the TV over nothing. The launcher and the game both come up on the
        // TV, where Big Picture is, so only a window there counts.
        if (OnAnotherMonitor(fg, bp)) { _otherStreak = 0; return false; }

        if (FillsMonitor(fg)) { _otherStreak = 0; return true; }

        return ++_otherStreak >= 2;
    }

    /// <summary>Whether a window is on a different monitor than Big Picture — a sign it is not the
    /// launcher we just opened, but a stray desktop window that has stolen focus.</summary>
    private static bool OnAnotherMonitor(IntPtr a, IntPtr b)
        => MonitorFromWindow(a, MonitorDefaultToNearest) != MonitorFromWindow(b, MonitorDefaultToNearest);

    /// <summary>
    /// Give a window the foreground reliably. A plain SetForegroundWindow from a background timer is
    /// refused by Windows' foreground lock, so this borrows the current foreground thread's input
    /// queue for the moment it takes to hand focus over — the standard way around that lock.
    /// </summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        try
        {
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint us = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != us && AttachThreadInput(us, fgThread, true);

            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);

            if (attached) AttachThreadInput(us, fgThread, false);
        }
        catch { /* focus is best-effort */ }
    }

    private static bool IsShell(IntPtr hwnd)
    {
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        var name = cls.ToString();
        return name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>The taskbar (primary or on a second monitor), split out from <see cref="IsShell"/> so
    /// the guard can take the foreground back from it — the desktop (Progman/WorkerW) is left to the
    /// once-only focus hand-off, but the taskbar must be covered every time it is raised.</summary>
    private static bool IsTaskbar(IntPtr hwnd)
    {
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        return cls.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>
    /// Whether a window belongs to the Start menu or Search. Both open as focused overlays on Big
    /// Picture's own monitor and sit above even a topmost window, so the guard has to recognise them
    /// — otherwise the streak logic mistakes the Start menu for a launcher and drops the pin. Matched
    /// by owning process because their window class ("Windows.UI.Core.CoreWindow") is shared with
    /// other UWP surfaces.
    /// </summary>
    private static bool IsStartOrSearch(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;

            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName is "StartMenuExperienceHost" or "SearchHost"
                                 or "SearchApp" or "ShellExperienceHost";
        }
        catch { return false; }
    }

    /// <summary>Whether a window belongs to Steam itself — the client, its Chrome-based helper, or the
    /// overlay — as opposed to a game. Used to reclaim the foreground for Big Picture when one of those
    /// pops up over the couch UI (most often as a game exits), which a game/launcher never should.</summary>
    private static bool IsSteamOwned(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;

            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName is "steam" or "steamwebhelper" or "GameOverlayUI" or "gameoverlayui";
        }
        catch { return false; }
    }

    /// <summary>The foreground window's class, for the log line — enough to tell a launcher from
    /// the shell without dumping a title.</summary>
    private static string ForegroundClass()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return "none";

        var cls = new StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        return cls.Length > 0 ? cls.ToString() : "untitled";
    }

    private static bool FillsMonitor(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var win)) return false;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var s = info.rcMonitor;
        const int Slop = 2;

        return Math.Abs(win.Left - s.Left) <= Slop
            && Math.Abs(win.Top - s.Top) <= Slop
            && Math.Abs(win.Right - s.Right) <= Slop
            && Math.Abs(win.Bottom - s.Bottom) <= Slop;
    }

    public void Dispose()
    {
        RestoreSystemCursor();   // a crash or close must not strand the desktop without a pointer
        _timer.Dispose();
    }

    // ---- hiding the pointer system-wide ----
    //
    // ShowCursor cannot help here: it only affects windows of the calling thread, and Big Picture
    // belongs to Steam. Replacing the system cursor images with a blank one is the reliable way to
    // hide the pointer over another process's window, and it is undone by asking Windows to reload
    // its cursor scheme.

    private bool _cursorHidden;

    private void HideSystemCursor()
    {
        if (_cursorHidden) return;

        var blank = CreateBlankCursor();
        if (blank == IntPtr.Zero) return;

        // SetSystemCursor takes ownership of the handle it is given, so every cursor id gets its
        // own copy and the template is destroyed afterwards.
        foreach (var id in CursorIds)
        {
            var copy = CopyIcon(blank);
            if (copy != IntPtr.Zero) SetSystemCursor(copy, id);
        }

        DestroyCursor(blank);
        _cursorHidden = true;
    }

    private void RestoreSystemCursor()
    {
        if (!_cursorHidden) return;

        // Reload every system cursor from the current scheme, putting the real images back.
        SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_SENDCHANGE);
        _cursorHidden = false;
    }

    private static IntPtr CreateBlankCursor()
    {
        const int w = 32, h = 32;

        // AND = all 1s, XOR = all 0s: every pixel transparent, i.e. nothing drawn.
        var and = new byte[w * h / 8];
        Array.Fill(and, (byte)0xFF);
        var xor = new byte[w * h / 8];

        return CreateCursor(IntPtr.Zero, 0, 0, w, h, and, xor);
    }

    private static readonly uint[] CursorIds =
    {
        32512, // OCR_NORMAL     (arrow)
        32513, // OCR_IBEAM      (text)
        32514, // OCR_WAIT       (busy)
        32515, // OCR_CROSS
        32516, // OCR_UP
        32642, // OCR_SIZENWSE
        32643, // OCR_SIZENESW
        32644, // OCR_SIZEWE
        32645, // OCR_SIZENS
        32646, // OCR_SIZEALL
        32648, // OCR_NO
        32649, // OCR_HAND       (link)
        32650, // OCR_APPSTARTING
    };

    private const uint SPI_SETCURSORS = 0x0057;
    private const uint SPIF_SENDCHANGE = 0x02;

    private const uint MonitorDefaultToNearest = 2;

    /// <summary>
    /// Pin Big Picture topmost and lift any live toast above it in one atomic z-order update.
    ///
    /// Done as two separate calls per tick — Big Picture up, then the toast lifted back over it —
    /// the toast flickered five times a second, because each SetWindowPos presents a frame and the
    /// in-between one has Big Picture covering the toast. A deferred batch commits the final order
    /// (toasts over Big Picture) in a single present, and is a no-op with no repaint on the ticks
    /// where nothing actually moved, which is almost all of them.
    /// </summary>
    private void PinTopmostWithToasts(IntPtr bp)
    {
        var toasts = CouchMode.Ui.Toast.LiveHandles();

        IntPtr hdwp = BeginDeferWindowPos(1 + toasts.Count);
        if (hdwp == IntPtr.Zero)
        {
            BigPictureLauncher.SetAlwaysOnTop(bp, true);   // batching unavailable; fall back
            return;
        }

        // Big Picture first, then each toast above it; applied together so the screen only ever
        // shows the committed result.
        hdwp = DeferWindowPos(hdwp, bp, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        foreach (var toast in toasts)
        {
            if (hdwp == IntPtr.Zero) break;
            hdwp = DeferWindowPos(hdwp, toast, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        if (hdwp != IntPtr.Zero) EndDeferWindowPos(hdwp);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    [DllImport("user32.dll")] private static extern IntPtr BeginDeferWindowPos(int count);
    [DllImport("user32.dll")] private static extern IntPtr DeferWindowPos(IntPtr hdwp, IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool EndDeferWindowPos(IntPtr hdwp);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder buf, int max);

    [DllImport("user32.dll")] private static extern IntPtr CreateCursor(IntPtr hInst, int xHot, int yHot, int width, int height, byte[] andPlane, byte[] xorPlane);
    [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] private static extern bool DestroyCursor(IntPtr hCursor);
    [DllImport("user32.dll")] private static extern bool SetSystemCursor(IntPtr hCursor, uint id);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, IntPtr vparam, uint winIni);
}

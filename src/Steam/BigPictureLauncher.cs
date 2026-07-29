using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CouchMode.Steam;

/// <summary>Launches Steam Big Picture and waits for the session to end.</summary>
public static class BigPictureLauncher
{
    private const string BigPictureClass = "SDL_app";
    private const string BigPictureTitle = "Steam Big Picture Mode";

    /// <summary>
    /// Move the pointer to the top right of the display Big Picture just opened on.
    ///
    /// An auto-hiding taskbar reappears whenever the pointer touches the edge it lives on, and
    /// the pointer is left wherever the mouse last was — very often the bottom of the screen,
    /// where it sits over the TV holding the taskbar open for the whole session. Nobody using a
    /// controller is going to move it away.
    ///
    /// The top right is chosen because the taskbar is almost never there, and it is inset by a
    /// few pixels rather than pinned to the corner, which on Windows is a hot corner of its own.
    /// </summary>
    public static void ParkCursor(Rectangle bounds)
    {
        try
        {
            SetCursorPos(bounds.Right - 4, bounds.Top + 4);
        }
        catch (Exception ex)
        {
            // Cosmetic. Never worth failing a session over.
            Log.Warn($"Could not move the pointer clear of the taskbar: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    public static bool IsSteamInstalled() => SteamExePath() is not null;

    /// <summary>
    /// When the last Big Picture launch was asked for, and the token that cancels the watchdog
    /// below. Both exist because launching is a request, not an action: the steam:// handler
    /// returns immediately and Steam gets round to opening the window whenever it likes, which
    /// on a cold client is several seconds.
    /// </summary>
    private static DateTime _launchRequestedAt = DateTime.MinValue;
    private static CancellationTokenSource? _watchdog;

    /// <summary>How long after a launch request Big Picture might still turn up.</summary>
    private static readonly TimeSpan LaunchWindow = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Whether Steam might still act on a launch we asked for.
    ///
    /// Read by the desktop-window handling on the way out: while this is true, anything done to
    /// Steam's windows can be undone by the launch still landing, so the result has to be held
    /// rather than set once.
    /// </summary>
    public static bool LaunchMayStillArrive => DateTime.UtcNow - _launchRequestedAt < LaunchWindow;

    public static void Launch()
    {
        // Any watchdog from a previous exit is cancelled here, or it would close the window this
        // launch is about to open.
        _watchdog?.Cancel();
        _watchdog = null;

        _launchRequestedAt = DateTime.UtcNow;

        Process.Start(new ProcessStartInfo
        {
            FileName = "steam://open/bigpicture",
            UseShellExecute = true,
        })?.Dispose();
    }



    /// <summary>
    /// Move Big Picture onto a specific display and bring it forward.
    ///
    /// Targets explicit bounds rather than "the primary screen": in keep-other-displays mode
    /// the TV is primary but the other monitors are still live, and relying on primary alone
    /// left Big Picture sitting on whichever monitor it launched on.
    /// </summary>
    public static void PlaceOn(IntPtr hwnd, Rectangle bounds, bool bringToFront = true)
    {
        // Real pixels. The bounds come from the display APIs, which never scale; without this the
        // window call is scaled and the two disagree. See DpiScope.
        using var pixels = DpiScope.PerMonitor();

        // Skipped when a game is already running: taking the foreground would pull a fullscreen game
        // into the background and leave Big Picture covering it, with no way back to the game.
        if (bringToFront)
        {
            SetForegroundWindow(hwnd);
            Thread.Sleep(150);
        }

        SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                     SWP_NOZORDER | SWP_NOACTIVATE);

        // Park the cursor in the corner so it isn't floating over the UI on the TV — but not while a
        // game is running, since moving the pointer would disturb it.
        if (bringToFront)
            Cursor.Position = new Point(bounds.Right - 2, bounds.Bottom - 2);
    }

    /// <summary>
    /// Keep forcing Big Picture to exactly fill the TV until it stops fighting back.
    ///
    /// This exists because a single SetWindowPos isn't enough. Steam receives WM_DISPLAYCHANGE
    /// at the same time we do and re-lays-out its own window from its saved state — which was
    /// sized for whichever monitor it launched on. With mixed resolutions and aspect ratios
    /// that leaves it letterboxed or overflowing the TV. So we re-assert on a short loop and
    /// only stop once the window has held our geometry across consecutive checks, meaning
    /// Steam has finished its own layout pass.
    /// </summary>
    public static bool EnsureFills(IntPtr hwnd, Rectangle bounds, TimeSpan timeout)
    {
        // Both the reads and the writes below have to be in real pixels, or the comparison is
        // between a scaled rectangle and an unscaled one and never agrees — which on a display at
        // a different scale from the primary meant this loop pushed Big Picture to the wrong size
        // and then reported that it had never settled.
        using var pixels = DpiScope.PerMonitor();

        var deadline = DateTime.UtcNow + timeout;
        int consecutiveMatches = 0;

        while (DateTime.UtcNow < deadline)
        {
            if (!IsWindow(hwnd)) return false;

            // A Big Picture that opened minimised is never what a session wants. Steam opens it that
            // way when its own client window was minimised — which it is after a previous session, or
            // right after a wake — and a minimised window's rect never matches the TV, so the sizing
            // below would just shuffle an icon in the taskbar forever. Restore it first; SetWindowPos
            // does not un-minimise on its own.
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

            // Compare with a small tolerance rather than exactly. GetWindowRect reports the
            // DWM frame, which can differ from the requested rect by a pixel or two, and an
            // exact test made the loop re-assert forever and always report failure.
            const int Slop = 4;

            if (GetWindowRect(hwnd, out var rect) &&
                Math.Abs(rect.Left - bounds.Left) <= Slop &&
                Math.Abs(rect.Top - bounds.Top) <= Slop &&
                Math.Abs(rect.Right - bounds.Right) <= Slop &&
                Math.Abs(rect.Bottom - bounds.Bottom) <= Slop)
            {
                // Two clean checks in a row means Steam has settled and isn't going to
                // move it again. One isn't enough — it often re-lays-out ~200ms later.
                if (++consecutiveMatches >= 2)
                {
                    Log.Info($"Big Picture is filling the TV ({bounds.Width}x{bounds.Height}).");
                    return true;
                }
            }
            else if (!GetWindowRect(hwnd, out _))
            {
                return false;
            }
            else
            {
                consecutiveMatches = 0;
                SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                             SWP_NOZORDER | SWP_NOACTIVATE);
            }

            Thread.Sleep(250);
        }

        Log.Warn("Big Picture never settled at the TV's resolution; leaving it as-is.");
        return false;
    }

    /// <summary>
    /// Pin Big Picture above other windows, or let it back down.
    ///
    /// Z-order only — never moves, sizes, or activates the window. Toast notifications live in a
    /// band above even topmost windows, so this does not cover them. Released again the moment a
    /// game or launcher comes forward, so it never sits over what you are actually playing.
    /// </summary>
    public static void SetAlwaysOnTop(IntPtr hwnd, bool onTop)
    {
        if (hwnd == IntPtr.Zero) return;

        var after = onTop ? HWND_TOPMOST : HWND_NOTOPMOST;
        SetWindowPos(hwnd, after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    private const int SW_RESTORE = 9;

    /// <summary>
    /// Bring Big Picture to the foreground, restoring it first if Steam minimised it. Used to hand the
    /// couch back to the Steam shell when resuming a parked session, so its own "Resume game" — and the
    /// overlay it manages — take over rather than us poking the game window directly.
    /// </summary>
    /// <summary>
    /// Bring Big Picture back and keep asking until it is actually there.
    ///
    /// [BUG] A single BringToFront after closing a game did not stick. The call landed while the
    /// game's window was still being torn down, and a dying fullscreen window takes the foreground
    /// with it on the way out — so the couch was handed back to a desktop nobody could see from the
    /// sofa. One press asks once; this asks until it is true.
    ///
    /// Stops as soon as Big Picture is genuinely in front, so the ordinary case costs one attempt.
    /// </summary>
    public static void Reveal(TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;

        while (true)
        {
            var hwnd = FindWindow();

            if (hwnd != IntPtr.Zero)
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                BringToFront();

                if (GetForegroundWindow() == hwnd) return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Log.Info(hwnd == IntPtr.Zero
                             ? "Big Picture was not open to come back to after the game closed."
                             : "Big Picture would not come to the front after the game closed.");
                return;
            }

            Thread.Sleep(400);
        }
    }

    public static void BringToFront()
    {
        var hwnd = FindWindow();
        if (hwnd == IntPtr.Zero) return;

        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        // Not a bare SetForegroundWindow. Windows refuses that from a process that does not already
        // own the foreground — which we do not, having just closed the window that did — so it failed
        // silently and Big Picture only came forward whenever Steam next noticed on its own. That
        // wait is what made the controller feel dead for a second or two after answering a prompt.
        //
        // Attaching to the foreground thread makes Windows treat the two as one input queue and allow
        // it. Ordinary window calls — nothing injected or hooked.
        //
        // Deliberately no SwitchToThisWindow here. It emulates Alt-Tab, and Alt-Tabbing away from a
        // fullscreen window is a request to minimize it — so the call meant to bring Big Picture
        // forward was the reason resuming sometimes minimized it instead.
        try
        {
            AllowSetForegroundWindow(ASFW_ANY);

            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint us = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != us && AttachThreadInput(us, fgThread, true);

            // Z-order as well as focus. The prompt that just closed spends its whole life re-asserting
            // itself as a topmost window, and closing it does not put Big Picture back above the other
            // topmost windows it was pushed under — so Big Picture could take the focus and still not be
            // the thing on screen. Raised explicitly, without stealing activation from the call below.
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);

            if (attached) AttachThreadInput(us, fgThread, false);
        }
        catch { /* best effort — Steam will get there on its own */ }

        // Whatever happened above, this function's one promise is that Big Picture is not left
        // minimized. Cheap to check and it costs nothing when it is already up.
        try { if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE); }
        catch { /* best effort */ }

        HoldUp(hwnd);
    }

    /// <summary>
    /// Keep checking, briefly, that Big Picture really did come back up.
    ///
    /// One call is not enough. Big Picture is a fullscreen shell, and restoring one is not instant —
    /// Steam has to re-acquire the display, and until it has, the window can still be knocked back down
    /// by whatever else is settling at the same moment (a window closing, a z-order shuffle, Steam's own
    /// fullscreen handling). A single restore therefore "worked" and the screen still ended up
    /// minimized a fraction of a second later.
    ///
    /// So it is checked a few times over the next second and put back if it drops. Only ever restores
    /// something already found minimized, so once it is up this does nothing at all.
    /// </summary>
    private static void HoldUp(IntPtr hwnd)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    await Task.Delay(180).ConfigureAwait(false);

                    if (!IsWindow(hwnd)) return;   // Steam replaced or closed it; nothing to hold up

                    if (IsIconic(hwnd))
                    {
                        Log.Info("Big Picture dropped back to minimized after being brought forward; restoring it.");
                        ShowWindow(hwnd, SW_RESTORE);
                    }

                    // Foreground as well as un-minimized, and checked rather than assumed.
                    //
                    // One SetForegroundWindow at the moment the prompt closes is not enough: the window
                    // being destroyed hands the foreground somewhere first, and Steam then takes a beat
                    // to settle its own windows. The result was Big Picture up but not in front, with
                    // the desktop visible behind it for a second and the controller not driving it.
                    // Stops the instant it has it, so this is a few checks in the ordinary case.
                    if (GetForegroundWindow() == hwnd) return;

                    SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    SetForegroundWindow(hwnd);
                }

                Log.Info("Big Picture would not take the foreground back; leaving it to Steam.");
            }
            catch (Exception ex) { Log.Warn($"Could not keep Big Picture up: {ex.Message}"); }
        });
    }

    private const uint ASFW_ANY = 0xFFFFFFFF;

    /// <summary>Top of the ordinary z-order. The SWP flags and SetWindowPos itself are declared with
    /// the rest of the window interop further down.</summary>
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    /// <summary>
    /// Whether Steam's in-game overlay is open and in front right now.
    ///
    /// The overlay puts up its own foreground window, of class GameOverlayUI and belonging to Steam's
    /// process of the same name, so asking what is in front answers it — no hooking, no reading Steam's
    /// memory, just the window that already exists.
    ///
    /// Matched on GameOverlayUI alone and deliberately not on Steam's other windows. The Big Picture
    /// shell is steamwebhelper, and counting that would mean "the overlay is open" was true for the
    /// whole of every session.
    /// </summary>
    public static bool OverlayIsOpen()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            var cls = ClassBuffer;
            cls.Clear();
            GetClassName(fg, cls, cls.Capacity);

            if (cls.ToString().Contains("GameOverlayUI", StringComparison.OrdinalIgnoreCase)) return true;

            GetWindowThreadProcessId(fg, out uint pid);
            return ProcessExeName(pid) is { } exe
                && exe.StartsWith("gameoverlayui", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // not knowing means carrying on as before
    }

    /// <summary>Current Big Picture window handle, or zero if it isn't running.</summary>
    /// <summary>
    /// Ask Big Picture to close, if it is open.
    ///
    /// WM_CLOSE rather than killing Steam: Big Picture is a window of the Steam process, so
    /// ending the process would take the whole client down with it.
    /// </summary>
    public static void Close()
    {
        var hwnd = FindBigPicture();

        // steam://close/bigpicture, but only while a launch could still be in flight.
        //
        // It is the one request that cannot arrive too early: WM_CLOSE needs a window, so quitting
        // a second after entering used to post it at nothing — Steam had not opened Big Picture
        // yet — and then finished opening it over a desktop the user had already gone back to.
        // This goes through the same handler the open went through, so Steam sequences the two
        // itself and a close issued mid-open is still honoured.
        //
        // Not used the rest of the time, because invoking any steam:// URL brings Steam to the
        // foreground. After an ordinary session Big Picture is genuinely open and WM_CLOSE is
        // enough, so sending this as well only made the desktop window flash up for a second
        // before being minimised again.
        //
        // Guarded on Steam actually running too: a steam:// URL starts the client if it is closed,
        // and launching Steam in order to tell it to close something it never opened would be
        // absurd.
        if (LaunchMayStillArrive && SteamIsRunning())
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "steam://close/bigpicture",
                    UseShellExecute = true,
                })?.Dispose();

                Log.Info("Asked Steam to close Big Picture.");
            }
            catch (Exception ex)
            {
                // Not fatal: the window message below and the watchdog both still apply.
                Log.Warn($"Could not send the Big Picture close request: {ex.Message}");
            }
        }

        // Belt and braces. If the window is up right now, this closes it without waiting for
        // Steam to get round to the URL.
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        if (hwnd == IntPtr.Zero && !LaunchMayStillArrive) return;

        StartWatchdog();
    }

    private static bool SteamIsRunning()
    {
        try { return Process.GetProcessesByName("steam").Length > 0; }
        catch { return false; }
    }

    /// <summary>
    /// Keep closing Big Picture for a short while after the session ends.
    ///
    /// A backstop, not the mechanism — steam://close/bigpicture above is what actually settles the
    /// fast in-and-out case. This covers what that cannot promise: the URL handler is fire and
    /// forget with no result to check, Steam is free to ignore it while mid-startup, and WM_CLOSE
    /// posted to a window that is still building its UI is sometimes swallowed. None of that is
    /// worth another window stranded on a television, and polling for it costs nothing.
    ///
    /// So the close is repeated rather than trusted. It stops early once the window has been gone
    /// for a stretch, and it is cancelled outright by the next Launch, so this can never fight a
    /// session the user has just started.
    /// </summary>
    private static void StartWatchdog()
    {
        _watchdog?.Cancel();

        var cts = new CancellationTokenSource();
        _watchdog = cts;

        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + LaunchWindow;
            int quietChecks = 0;
            bool closedLate = false;

            try
            {
                while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
                {
                    await Task.Delay(400, token).ConfigureAwait(false);

                    var late = FindBigPicture();

                    if (late == IntPtr.Zero)
                    {
                        // Gone for two seconds running. Steam is not going to produce it now.
                        if (++quietChecks >= 5) break;
                        continue;
                    }

                    quietChecks = 0;
                    closedLate = true;
                    PostMessage(late, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }

                if (closedLate)
                    Log.Info("Big Picture opened after the session ended; closed it again.");
            }
            catch (OperationCanceledException)
            {
                // A new session started. Big Picture is meant to be open now.
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not finish closing Big Picture: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_watchdog, cts)) _watchdog = null;
                cts.Dispose();
            }
        }, token);
    }

    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    public static IntPtr FindWindow() => FindBigPicture();

    /// <summary>
    /// The Steam desktop client window. Same SDL_app class as Big Picture — Steam's whole UI is
    /// one engine — so it's identified by exclusion: an SDL_app window that isn't Big Picture.
    /// </summary>
    public static IntPtr FindDesktopWindow()
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            var cls = ClassBuffer;
            cls.Clear();
            GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() != BigPictureClass) return true;

            var title = TitleBuffer;
            title.Clear();
            GetWindowText(hwnd, title, title.Capacity);
            var text = title.ToString();

            if (text.StartsWith(BigPictureTitle, StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Length == 0) return true;

            found = hwnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// The window of a Steam game currently running, or zero if none is. Found from Steam's own
    /// RunningAppID rather than the app's HDR watch list, so it works for any game — checked for HDR
    /// or not. Used so a session started or resumed while a game is up drops straight back into it
    /// rather than reopening Big Picture over the top.
    /// </summary>
    /// <summary>
    /// Whichever of two windows covers more of the screen, ignoring any that has gone.
    ///
    /// For telling a game apart from its own launcher. Several titles keep the launcher running
    /// behind the game — DOOM Eternal is one — and both are real windows belonging to real processes
    /// under the same install folder, so nothing about their identity separates them. Their size does:
    /// the game is filling a screen and the launcher is a small window someone clicked Play in.
    /// </summary>
    /// <summary>Whether a window is minimized, for callers deciding whether to trust its size.</summary>
    public static bool IsMinimized(IntPtr hwnd) => hwnd != IntPtr.Zero && IsIconic(hwnd);

    /// <summary>Whether a window handle still refers to a real window.</summary>
    public static bool StillAWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public static IntPtr LargerOf(IntPtr a, IntPtr b)
    {
        if (a == IntPtr.Zero) return b;
        if (b == IntPtr.Zero || a == b) return a;

        return Area(b) > Area(a) ? b : a;
    }

    private static long Area(IntPtr hwnd)
    {
        // A minimized window reports a nonsense rectangle, so it loses to anything real — which is
        // right: a minimized launcher is certainly not the window the player is looking at.
        if (hwnd == IntPtr.Zero || IsIconic(hwnd) || !GetWindowRect(hwnd, out var r)) return 0;

        return (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
    }

    public static IntPtr FindRunningGameWindow()
    {
        if (RunningAppId() == 0) return IntPtr.Zero;   // Steam says nothing is running

        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        IntPtr fallback = IntPtr.Zero;   // any game window, used when the game is parked minimised
        uint self = (uint)Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            // Allow a minimised (iconic) window through as well as a visible one — a parked session
            // deliberately minimises the game, and resume still has to find it to drop back in.
            if (!IsWindowVisible(hwnd) && !IsIconic(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == self) return true;

            // The one hard gate: the window's process must be an actual Steam game — its executable
            // living under a Steam library's "steamapps\common" folder. This is what stops the app from
            // ever mistaking a chat window, Discord, a browser, or any other window for the game (which once got
            // a non-game process frozen), and it already excludes Steam and Big Picture, whose
            // steamwebhelper does not live there — so no window-class check is needed.
            var path = ProcessImagePath(pid);
            if (path is null || path.IndexOf(@"steamapps\common", StringComparison.OrdinalIgnoreCase) < 0)
                return true;

            if (fallback == IntPtr.Zero) fallback = hwnd;   // first game window, whatever its size

            if (!GetWindowRect(hwnd, out var r)) return true;
            long area = (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = hwnd; }
            return true;
        }, IntPtr.Zero);

        // Prefer the largest real window; fall back to any game window (a minimised one reports a tiny
        // size) so a parked game still resumes instead of reopening Big Picture over the top.
        return best != IntPtr.Zero ? best : fallback;
    }

    /// <summary>Whether a process is an actual Steam-installed game — its executable under a Steam
    /// library's "steamapps\common". The last line of defence before anything freezes a process, so a
    /// non-game (a chat window, Discord, a browser) can never be suspended by mistake.</summary>
    public static bool IsSteamGameProcess(uint pid)
    {
        var path = ProcessImagePath(pid);
        return path is not null && path.IndexOf(@"steamapps\common", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>The bare executable name of a process ("Cyberpunk2077.exe"), or null if it can't be
    /// read — used to name a process for a powercfg power-request override.</summary>
    public static string? ProcessExeName(uint pid)
    {
        var path = ProcessImagePath(pid);
        return path is null ? null : System.IO.Path.GetFileName(path);
    }

    /// <summary>The full path of a process's executable, or null if it can't be read. Uses the
    /// limited-information query so it works across 32/64-bit without needing full process rights.</summary>
    private static string? ProcessImagePath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;

        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(h, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder buffer, ref int size);

    /// <summary>Steam's currently-running app id, or 0. Public so the home page can name the
    /// game without duplicating the registry read.</summary>
    public static int RunningAppId()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("RunningAppID") is int id ? id : 0;
        }
        catch { return 0; }
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);


    // Reused across scans. The watcher runs on a timer for the whole life of the app, so
    // allocating two StringBuilders per window per tick is needless GC churn while gaming.
    [ThreadStatic] private static StringBuilder? _classBuffer;
    [ThreadStatic] private static StringBuilder? _titleBuffer;

    private static StringBuilder ClassBuffer => _classBuffer ??= new StringBuilder(64);
    private static StringBuilder TitleBuffer => _titleBuffer ??= new StringBuilder(256);

    private static IntPtr FindBigPicture()
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            var cls = ClassBuffer;
            cls.Clear();
            GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() != BigPictureClass) return true;

            var title = TitleBuffer;
            title.Clear();
            GetWindowText(hwnd, title, title.Capacity);
            // Match on prefix: Steam has appended suffixes to this title across versions.
            if (!title.ToString().StartsWith(BigPictureTitle, StringComparison.OrdinalIgnoreCase))
                return true;

            found = hwnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// Shut Steam down and bring it back straight into Big Picture.
    ///
    /// The point is the audio endpoint: Steam binds it at process start and ignores later
    /// default-device changes, so a running Steam keeps playing its UI sounds through whatever
    /// was default when it launched. Restarting after the switch is the only reliable fix —
    /// Valve has never exposed an output-device setting in Big Picture.
    /// </summary>
    public static async Task<bool> RestartIntoBigPictureAsync(TimeSpan timeout)
    {
        var exe = SteamExePath();
        if (exe is null)
        {
            Log.Warn("Can't restart Steam: its executable wasn't found.");
            return false;
        }

        Log.Info("Restarting Steam so its audio follows the TV.");

        try
        {
            // -shutdown is Steam's supported graceful exit; killing the process risks
            // leaving download or cloud-sync state inconsistent.
            Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't ask Steam to shut down", ex);
            return false;
        }

        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline && Process.GetProcessesByName("steam").Length > 0)
            await Task.Delay(500).ConfigureAwait(false);

        if (Process.GetProcessesByName("steam").Length > 0)
        {
            Log.Warn("Steam didn't shut down in time; leaving it alone.");
            return false;
        }

        await Task.Delay(1500).ConfigureAwait(false);   // let the endpoint settle before Steam grabs it
        Launch();

        while (DateTime.UtcNow < deadline)
        {
            if (FindWindow() != IntPtr.Zero)
            {
                Log.Info("Steam restarted into Big Picture.");
                return true;
            }
            await Task.Delay(500).ConfigureAwait(false);
        }

        Log.Warn("Big Picture didn't come back after the Steam restart.");
        return false;
    }

    private static string? SteamExePath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamExe") as string;
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder buf, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder buf, int max);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

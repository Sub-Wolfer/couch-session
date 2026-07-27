using System.Runtime.InteropServices;

namespace CouchMode.Steam;

/// <summary>
/// Remembers the Steam desktop window's geometry across a Big Picture session.
///
/// The tricky part is *when* to measure. Opening Big Picture destroys the desktop window and
/// builds a new one on exit, and by the time we notice Big Picture has opened, the desktop
/// window is already gone — so measuring at that point finds nothing. Instead this observes
/// continuously while on the desktop and persists the result, so there's always a last-known
/// good geometry to restore, even across an app restart.
///
/// It also can't go through the generic window-layout store: both the desktop UI and Big
/// Picture use the same "SDL_app" class in the same steamwebhelper process, so matching on
/// process+class is ambiguous and restores the wrong window.
/// </summary>
public static class SteamWindowMemory
{
    private static WINDOWPLACEMENT? _observed;

    /// <summary>
    /// The geometry this session is going to restore, frozen at the moment the session began.
    ///
    /// Separate from _observed, and that separation is the whole point. _observed is live — it is
    /// refreshed every few seconds while on the desktop so there is always something current to
    /// fall back on. The restore runs in the background and takes several seconds, during which
    /// the session has already been declared over and the observer has started running again. It
    /// then measures the window Steam has just rebuilt at Steam's own size and writes that over
    /// the value the restore is in the middle of using.
    ///
    /// The log said so plainly once there was something to compare: remembered 2153x1049 at the
    /// start of a session, restored 2222x1320 at the end of it. Whether it happened at all came
    /// down to where the observer's timer landed, which is why it worked about half the time.
    /// </summary>
    private static WINDOWPLACEMENT? _forSession;

    /// <summary>
    /// Record Steam's current geometry if its desktop window is open and in a sane state.
    /// Called on a timer while on the desktop; cheap enough to run every couple of seconds.
    /// </summary>
    public static void Observe(AppConfig config)
    {
        using var pixels = DpiScope.PerMonitor();

        var hwnd = BigPictureLauncher.FindDesktopWindow();
        if (hwnd == IntPtr.Zero) return;

        var placement = WINDOWPLACEMENT.Create();
        if (!GetWindowPlacement(hwnd, ref placement)) return;

        // Ignore minimized: rcNormalPosition is still valid, but showCmd would restore it
        // minimized later, which isn't what the user wants back.
        if (placement.showCmd == SW_SHOWMINIMIZED) return;

        var r = placement.rcNormalPosition;
        if (r.Right - r.Left < 200 || r.Bottom - r.Top < 200) return;   // implausible; ignore

        bool changed = !_observed.HasValue
                       || _observed.Value.rcNormalPosition.Left != r.Left
                       || _observed.Value.rcNormalPosition.Top != r.Top
                       || _observed.Value.rcNormalPosition.Right != r.Right
                       || _observed.Value.rcNormalPosition.Bottom != r.Bottom
                       || _observed.Value.showCmd != placement.showCmd;

        _observed = placement;

        if (!changed) return;

        config.SteamWindowLeft = r.Left;
        config.SteamWindowTop = r.Top;
        config.SteamWindowRight = r.Right;
        config.SteamWindowBottom = r.Bottom;
        config.SteamWindowMaximized = placement.showCmd == SW_SHOWMAXIMIZED;
    }

    /// <summary>Snapshot for this session, falling back to whatever was persisted last run.</summary>
    public static void Remember(AppConfig config)
    {
        if (_observed.HasValue)
        {
            // Copied, not referenced. Everything after this point works from the copy.
            _forSession = _observed;

            var r = _observed.Value.rcNormalPosition;
            Log.Info($"Steam window remembered: {r.Right - r.Left}x{r.Bottom - r.Top} at {r.Left},{r.Top}.");
            return;
        }

        if (config.HasSteamWindowGeometry)
        {
            _observed = new WINDOWPLACEMENT
            {
                length = Marshal.SizeOf<WINDOWPLACEMENT>(),
                showCmd = config.SteamWindowMaximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL,
                rcNormalPosition = new RECT
                {
                    Left = config.SteamWindowLeft,
                    Top = config.SteamWindowTop,
                    Right = config.SteamWindowRight,
                    Bottom = config.SteamWindowBottom,
                },
            };
            _forSession = _observed;
            Log.Info("Using Steam window geometry saved from a previous session.");
            return;
        }

        _forSession = null;
        Log.Info("No Steam window geometry known yet; it won't be resized on exit.");
    }

    /// <summary>
    /// Put the window back and apply the exit action, starting over if Steam destroys it.
    ///
    /// The window handle is the whole difficulty. Opening Big Picture destroys Steam's desktop
    /// window and building a new one on the way back, so a handle grabbed at the wrong moment
    /// refers to a window that is about to cease existing — and everything done to it is thrown
    /// away with it, silently, having appeared to succeed.
    ///
    /// That is what leaving couch mode about a second after entering it hits. The launch request
    /// is still travelling, so the desktop window is still the pre-session one. We size it, we
    /// minimise it, both work — and then Big Picture finally opens, destroys it, closes, and
    /// Steam builds a fresh desktop window at its own size, un-minimised. Both symptoms, one
    /// cause, which is why the geometry and the minimising failed together.
    ///
    /// So rather than trying to predict when Steam has finished, a pass that loses its window
    /// simply runs again on the replacement. It is self-correcting, needs no guess about Steam's
    /// timing, and in the ordinary case — where nothing is destroyed — the first pass returns.
    /// </summary>
    public static async Task RestoreAndApplyAsync(SteamExitAction action, TimeSpan timeout)
    {
        // The frozen copy, so nothing measured after the session ended can change what is
        // applied part way through applying it.
        var wanted = _forSession;

        if (wanted is null && action == SteamExitAction.Leave) return;

        var deadline = DateTime.UtcNow + timeout;
        int pass = 0;

        while (DateTime.UtcNow < deadline)
        {
            var hwnd = BigPictureLauncher.FindDesktopWindow();

            if (hwnd == IntPtr.Zero)
            {
                await Task.Delay(400).ConfigureAwait(false);
                continue;
            }

            if (++pass > 1)
                Log.Info("Steam rebuilt its window; setting it up again.");

            if (await TryApply(hwnd, wanted, action, deadline).ConfigureAwait(false)) return;
        }

        Log.Info("Steam's desktop window never settled; leaving it as Steam left it.");
    }

    /// <summary>
    /// One attempt at a window. False means it was destroyed part way through and the caller
    /// should try again on whatever Steam puts in its place.
    /// </summary>
    private static async Task<bool> TryApply(IntPtr hwnd, WINDOWPLACEMENT? wanted,
                                             SteamExitAction action, DateTime deadline)
    {
        bool minimise = action == SteamExitAction.Minimize;

        // Out of sight first, before waiting for Steam to settle. A window Steam has just rebuilt on
        // the way out of Big Picture appears at its own full size, and the settle wait below would
        // leave it sitting there visible for most of a second — the flash of a full-size Steam window
        // people saw intermittently. Minimising on sight ends that; the remembered size is folded in
        // below and applied while it is already down, so it never shows large.
        if (minimise) Minimise(hwnd);

        // Let Steam finish laying out its UI, or it overwrites whatever we set.
        if (!await Alive(hwnd, 900).ConfigureAwait(false)) return false;

        // Minimising is folded into the geometry rather than done after it.
        //
        // Restoring the size means SetWindowPlacement, and with a normal showCmd that *shows* the
        // window — so it would appear at full size, sit there while the geometry was re-asserted, and
        // only then get minimised. WINDOWPLACEMENT can express both at once: rcNormalPosition is where
        // the window goes when restored and is honoured whatever the current show state is, so a
        // placement of "minimised, and this is your size for later" sets the size without the window
        // becoming visible. WPF_RESTORETOMAXIMIZED carries a maximised window through as maximised.

        if (wanted is { } remembered)
        {
            var placement = remembered;

            if (minimise)
            {
                placement.showCmd = SW_SHOWMINIMIZED;

                if (remembered.showCmd == SW_SHOWMAXIMIZED)
                    placement.flags |= WPF_RESTORETOMAXIMIZED;
            }

            // Steam re-applies its own geometry a beat after the window appears, so a single
            // call loses the race. Assert a few times over a couple of seconds.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                Place(hwnd, placement);

                if (!await Alive(hwnd, 400).ConfigureAwait(false)) return false;
            }

            var r = placement.rcNormalPosition;
            Log.Info($"Restored the Steam window to {r.Right - r.Left}x{r.Bottom - r.Top}"
                   + (minimise ? ", minimized." : "."));

            if (minimise) return await HoldMinimised(hwnd, placement, deadline).ConfigureAwait(false);
        }
        else if (minimise)
        {
            // Nothing remembered, so there is no geometry to fold it into.
            Minimise(hwnd);
            return await HoldMinimised(hwnd, null, deadline).ConfigureAwait(false);
        }

        if (action == SteamExitAction.Close)
        {
            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Log.Info("Closed the Steam window.");
        }

        return true;
    }

    /// <summary>
    /// Whether the window is actually holding the geometry we asked for.
    ///
    /// Read back rather than assumed. SetWindowPlacement returning true says the call was accepted,
    /// not that the value survived whatever the application did next.
    /// </summary>
    private static bool Holds(IntPtr hwnd, WINDOWPLACEMENT? wanted)
    {
        if (wanted is not { } want) return true;

        using var pixels = DpiScope.PerMonitor();

        var now = WINDOWPLACEMENT.Create();
        if (!GetWindowPlacement(hwnd, ref now)) return false;

        // A pixel or two of difference is the frame, not a disagreement.
        const int Slop = 2;

        var a = want.rcNormalPosition;
        var b = now.rcNormalPosition;

        return Math.Abs(a.Left - b.Left) <= Slop
            && Math.Abs(a.Top - b.Top) <= Slop
            && Math.Abs(a.Right - b.Right) <= Slop
            && Math.Abs(a.Bottom - b.Bottom) <= Slop;
    }

    /// <summary>
    /// Apply a placement in real pixels.
    ///
    /// Its own method because both callers are async, and a thread's DPI context must not be held
    /// across an await — the continuation can resume on a different thread, which would leave the
    /// scope restoring a context that thread never had. Wrapping the synchronous call instead
    /// keeps the whole thing on one thread by construction.
    /// </summary>
    private static void Place(IntPtr hwnd, WINDOWPLACEMENT placement)
    {
        using var pixels = DpiScope.PerMonitor();

        var copy = placement;
        SetWindowPlacement(hwnd, ref copy);
    }

    private static void Minimise(IntPtr hwnd)
    {
        using var pixels = DpiScope.PerMonitor();
        ShowWindow(hwnd, SW_MINIMIZE);
    }

    /// <summary>Wait, and report whether the window survived it.</summary>
    private static async Task<bool> Alive(IntPtr hwnd, int ms)
    {
        await Task.Delay(ms).ConfigureAwait(false);
        return IsWindow(hwnd);
    }

    /// <summary>
    /// Make sure it stayed minimised — and stop the moment the user disagrees.
    ///
    /// The state is read back with IsIconic rather than assumed from the call having returned,
    /// which is why the log used to claim the window had been minimized while it sat there in
    /// front of the user. Re-asserting uses the same minimised placement as above, so a window
    /// Steam has pulled back up goes straight down again without passing through a visible normal
    /// state on the way.
    ///
    /// Two things stop it, and both exist because a persistent loop is only acceptable while it is
    /// arguing with Steam rather than with a person.
    ///
    /// The foreground check is the important one. Windows does not let a background process take
    /// the foreground from whoever has it, so a Steam window that is *in front* got there because
    /// somebody clicked it — on the taskbar, most likely. That is a direct instruction, and it
    /// outranks anything decided when the session ended. Without it the app spent up to
    /// twenty-five seconds shoving Steam back down every time it was opened, which is maddening
    /// and looks like the window is broken rather than like a setting is being enforced.
    ///
    /// The attempt cap is the backstop for everything the foreground check cannot see — restoring
    /// the window and immediately clicking elsewhere, say. Three goes is plenty to outlast Steam
    /// settling, and it bounds how annoying being wrong can get.
    /// </summary>
    private static async Task<bool> HoldMinimised(IntPtr hwnd, WINDOWPLACEMENT? minimised,
                                                  DateTime deadline)
    {
        const int MaxPushes = 3;

        // Long enough to outlast Steam settling after Big Picture closes, and longer still while
        // a launch could land, since that is what brings the window back up.
        var until = BigPictureLauncher.LaunchMayStillArrive
                  ? deadline
                  : DateTime.UtcNow + TimeSpan.FromSeconds(8);

        if (until > deadline) until = deadline;

        int pushes = 0;
        int settled = 0;

        while (DateTime.UtcNow < until)
        {
            bool iconic = IsIconic(hwnd);

            if (!iconic)
            {
                if (GetForegroundWindow() == hwnd)
                {
                    Log.Info("Steam was brought back up by hand; leaving it alone.");
                    return true;
                }

                if (++pushes > MaxPushes)
                {
                    Log.Info("Steam keeps reopening its window; leaving it as it is.");
                    return true;
                }
            }

            // Written, then read back, until it has visibly stuck.
            //
            // rcNormalPosition is only a note about where the window goes when it is restored, and
            // Steam writes its own over ours as it settles after Big Picture closes. Asserting for
            // a fixed length of time is a bet on how long that takes, and it was a bet this lost
            // often enough to look intermittent: the size came back right sometimes and as Steam's
            // the rest of the time, with nothing visibly wrong at the moment it was applied.
            //
            // So it stops on evidence instead of on a timer. The value is re-applied and then read
            // back, and only two consecutive clean reads — meaning Steam has had a turn and did not
            // take it — end the loop. The same test EnsureFills already uses for Big Picture's
            // geometry, and for the same reason.
            if (minimised is { } placement) Place(hwnd, placement);
            else if (!iconic) Minimise(hwnd);

            if (!await Alive(hwnd, 350).ConfigureAwait(false)) return false;

            settled = Holds(hwnd, minimised) ? settled + 1 : 0;

            // Two clean reads, and nothing left that could still disturb it.
            if (settled >= 2 && IsIconic(hwnd) && !BigPictureLauncher.LaunchMayStillArrive) break;
        }

        if (!IsIconic(hwnd))
            Log.Info("The Steam window would not stay minimized; Steam keeps bringing it back.");
        else if (!Holds(hwnd, minimised))
            Log.Warn("Steam kept overwriting its remembered size; it may reopen at its own.");

        return true;
    }

    // ---- interop ----

    private const uint WM_CLOSE = 0x0010;
    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMAXIMIZED = 3;
    private const int SW_MINIMIZE = 6;

    /// <summary>Minimized now, but restore straight back to maximized rather than to a window.</summary>
    private const int WPF_RESTORETOMAXIMIZED = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;

        public static WINDOWPLACEMENT Create() => new() { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
}

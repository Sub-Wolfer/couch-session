using System.Runtime.InteropServices;
using System.Text;

namespace CouchMode.WindowLayout;

/// <summary>
/// Saves and restores top-level window geometry across the display change, so the desktop
/// doesn't come back with everything crammed onto one monitor.
/// </summary>
public static class LayoutStore
{
    private sealed record WindowState(
        IntPtr Handle, int ProcessId, string ProcessName, string ClassName,
        WINDOWPLACEMENT Placement);

    private static List<WindowState> _saved = [];

    public static int Capture()
    {
        using var pixels = DpiScope.PerMonitor();

        var saved = new List<WindowState>();

        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!ShouldTrack(hwnd)) return true;

                var placement = WINDOWPLACEMENT.Create();
                if (!GetWindowPlacement(hwnd, ref placement)) return true;

                GetWindowThreadProcessId(hwnd, out int pid);
                saved.Add(new WindowState(hwnd, pid, ProcessNameOf(pid), ClassNameOf(hwnd), placement));
            }
            catch (Exception ex)
            {
                Log.Warn($"Skipped window {hwnd}: {ex.Message}");
            }
            return true;
        }, IntPtr.Zero);

        _saved = saved;
        Log.Info($"Captured layout for {saved.Count} windows.");
        return saved.Count;
    }

    /// <summary>
    /// Put every window we captured back, and touch nothing else.
    ///
    /// Matched by handle alone, deliberately. There used to be a second pass that watched for
    /// windows to "reappear" and matched them on process and window class instead — written for
    /// Steam, whose desktop window really is destroyed and rebuilt. Steam is excluded from this
    /// store entirely now and handled by SteamWindowMemory, so that pass could no longer do the
    /// job it existed for; all it did was apply remembered geometry to any window that happened to
    /// share a signature with one we had seen, for twenty-five seconds after every session.
    ///
    /// That is where a maximised PowerShell and a maximised Excel workbook came from. Both apps
    /// give every window the same class, so one remembered window could be applied to a completely
    /// different one — including one the user had opened since, or deliberately minimised.
    ///
    /// A handle is exact. If the window is gone, it is gone, and inventing a stand-in for it does
    /// more harm than leaving it alone.
    /// </summary>
    public static int Restore()
    {
        int restored = 0, closed = 0, minimized = 0, failed = 0;

        foreach (var win in _saved)
        {
            try
            {
                if (!Addressable(win)) { closed++; continue; }

                // Meant to be minimized: not moved and not a failure, just nothing to do. Counted
                // apart so it does not read as a window that would not go back.
                if (win.Placement.showCmd == SW_SHOWMINIMIZED && IsIconic(win.Handle)) { minimized++; continue; }

                if (Apply(win.Handle, win.Placement)) restored++;
                else
                {
                    failed++;
                    Log.Warn($"Window would not go back to its saved place: {win.ProcessName} "
                           + $"({win.ClassName}) → {Describe(win.Placement)}.");
                }
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warn($"Could not restore {win.ProcessName}: {ex.Message}");
            }
        }

        Log.Info($"Restored {restored}/{_saved.Count} windows"
               + $" (closed {closed}, already minimized {minimized}, would not move {failed}).");

        return restored;
    }

    private static string Describe(WINDOWPLACEMENT p)
    {
        var r = p.rcNormalPosition;
        return $"{r.Right - r.Left}×{r.Bottom - r.Top} at {r.Left},{r.Top}";
    }

    // ================= per-display memory =================
    //
    // Separate from the session capture above, and for a case that one cannot reach.
    //
    // A session captures at the start and restores at the end, which covers a session. It does
    // nothing for a display switched off and on at any other time — and when a display goes away
    // Windows shoves every window that was on it onto whatever is left, resized to fit. Turning
    // it back on does not undo that. The windows simply stay where they were pushed.
    //
    // The awkward part is timing: by the time anything is told a display has gone, the windows
    // have already moved. There is nothing left to measure. So this samples continuously while
    // things are calm and keeps the most recent reading, in the same spirit as the Steam window
    // observer — the answer has to be recorded before it is needed, because at the moment it is
    // needed the truth is gone.
    //
    // Deliberately not persisted to disk. Matching a remembered window to a live one across an app
    // restart means matching on process and window class, and that is precisely what put a
    // maximised Excel workbook on top of a different workbook. Within one run the handle is exact,
    // so this stays in memory and forgets everything when the app closes.

    /// <summary>The latest reading: where each window is, and which display it is on.</summary>
    private static List<(WindowState State, string Display)> _sample = [];

    /// <summary>Windows parked when a display went away, by that display's device path.</summary>
    private static readonly Dictionary<string, List<WindowState>> _parked =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which displays were attached at the last look, so a change can be spotted.</summary>
    private static HashSet<string> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Take a reading of where everything is.
    ///
    /// Called on a timer while nothing is moving. Cheap enough for that: it enumerates top-level
    /// windows and reads a placement each, which is what the session capture does once.
    /// </summary>
    public static void Sample(IReadOnlyDictionary<string, Rectangle> displays)
    {
        if (displays.Count == 0) return;

        // The display rectangles this is matched against come from the display APIs, so the
        // window rectangles have to be in the same units to land on the right screen.
        using var pixels = DpiScope.PerMonitor();

        var taken = new List<(WindowState, string)>();

        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!ShouldTrack(hwnd)) return true;

                var placement = WINDOWPLACEMENT.Create();
                if (!GetWindowPlacement(hwnd, ref placement)) return true;

                // Minimised windows have no meaningful position and are not disturbed by a
                // display going away, so there is nothing to remember for them.
                if (placement.showCmd == SW_SHOWMINIMIZED) return true;

                var on = DisplayHolding(placement.rcNormalPosition, displays);
                if (on is null) return true;

                GetWindowThreadProcessId(hwnd, out int pid);

                taken.Add((new WindowState(hwnd, pid, ProcessNameOf(pid), ClassNameOf(hwnd), placement), on));
            }
            catch { /* one awkward window must not spoil the reading */ }

            return true;
        }, IntPtr.Zero);

        _sample = taken;
    }

    /// <summary>
    /// Notice displays coming and going, parking and restoring windows to match.
    ///
    /// Given the displays attached right now. The first call only establishes what "before" was —
    /// there is nothing to compare against yet, and treating everything as newly arrived would
    /// restore windows nobody had moved.
    /// </summary>
    public static void DisplaysChanged(IReadOnlyDictionary<string, Rectangle> displays)
    {
        var now = new HashSet<string>(displays.Keys, StringComparer.OrdinalIgnoreCase);

        if (_lastSeen.Count == 0) { _lastSeen = now; return; }

        foreach (var gone in _lastSeen.Except(now))
        {
            // Frozen from the last reading taken while it was still attached. Taking one now
            // would record where Windows has just shoved everything, which is the problem rather
            // than the answer.
            var was = _sample.Where(w => w.Display.Equals(gone, StringComparison.OrdinalIgnoreCase))
                             .Select(w => w.State)
                             .ToList();

            if (was.Count == 0) continue;

            _parked[gone] = was;
            Log.Info($"A display went away with {was.Count} window(s) on it; remembering where they were.");
        }

        foreach (var back in now.Except(_lastSeen))
        {
            if (!_parked.Remove(back, out var waiting)) continue;

            int put = 0;

            foreach (var win in waiting)
            {
                try
                {
                    if (!Addressable(win)) continue;
                    if (Apply(win.Handle, win.Placement)) put++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not put {win.ProcessName} back: {ex.Message}");
                }
            }

            if (put > 0) Log.Info($"A display came back; put {put} window(s) back onto it.");
        }

        _lastSeen = now;
    }

    /// <summary>
    /// Which display a window sits on, decided by its centre.
    ///
    /// The centre rather than any overlap: a window straddling two displays belongs to the one
    /// showing most of it, and that is the one whose disappearance would move it.
    /// </summary>
    private static string? DisplayHolding(RECT rect, IReadOnlyDictionary<string, Rectangle> displays)
    {
        int cx = rect.Left + (rect.Right - rect.Left) / 2;
        int cy = rect.Top + (rect.Bottom - rect.Top) / 2;

        foreach (var (path, bounds) in displays)
            if (bounds.Contains(cx, cy)) return path;

        return null;
    }

    /// <summary>Forget everything parked. Used when the session restore is about to take over.</summary>
    public static void ForgetParked()
    {
        if (_parked.Count == 0) return;

        _parked.Clear();
        Log.Info("Dropped the per-display window memory; the session restore owns this now.");
    }

    /// <summary>
    /// Whether this window is still the one we captured and still ours to move.
    /// </summary>
    private static bool Addressable(WindowState win)
    {
        if (!IsWindow(win.Handle)) return false;

        // Handles get recycled. Confirm it's still the same process before moving anything.
        GetWindowThreadProcessId(win.Handle, out int pid);

        return pid == win.ProcessId;
    }

    /// <summary>
    /// Keep the layout put until Windows has finished moving things about.
    ///
    /// A single pass is a bet that the desktop has stopped reflowing by the time it runs, and it
    /// is a bet that loses. Restoring the display topology is not one event: monitors reattach,
    /// the primary changes, and Windows shuffles windows to fit each time — for several seconds
    /// after the call that started it has returned. Anything restored before the last of that
    /// settles is quietly moved again afterwards, which is precisely how windows ended up back on
    /// the wrong monitor while the log reported them restored.
    ///
    /// So the placement is re-applied to whatever has drifted, and only stops once two passes in a
    /// row find nothing to correct. Windows already in the right place are read and left alone, so
    /// the usual case costs one comparison each and touches nothing.
    /// </summary>
    public static async Task SettleAsync(TimeSpan window, CancellationToken token = default)
    {
        if (_saved.Count == 0) return;

        var deadline = DateTime.UtcNow + window;
        int quiet = 0;

        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            await Task.Delay(400, token).ConfigureAwait(false);

            int corrected = 0;

            foreach (var win in _saved)
            {
                try
                {
                    if (!Addressable(win)) continue;
                    if (Holds(win)) continue;

                    // Never against the user. If they are working in the window right now, where
                    // it sits is their decision and it stops being ours to correct.
                    if (GetForegroundWindow() == win.Handle) continue;

                    if (Apply(win.Handle, win.Placement)) corrected++;
                }
                catch { /* one awkward window must not stop the rest settling */ }
            }

            if (corrected > 0)
            {
                Log.Info($"{corrected} window(s) had drifted after the displays settled; put back.");
                quiet = 0;
                continue;
            }

            if (++quiet >= 2) return;
        }
    }

    /// <summary>
    /// Whether a window is already where it was captured.
    ///
    /// Compared on the restored rectangle and the show state, both read back from Windows rather
    /// than assumed from an earlier call having returned true.
    /// </summary>
    private static bool Holds(WindowState win)
    {
        using var pixels = DpiScope.PerMonitor();

        var now = WINDOWPLACEMENT.Create();
        if (!GetWindowPlacement(win.Handle, ref now)) return true;

        if (now.showCmd != win.Placement.showCmd) return false;

        // A minimised window's restore rectangle is not disturbed by a reflow, and comparing it
        // would have us re-applying to windows nobody has touched.
        if (win.Placement.showCmd == SW_SHOWMINIMIZED) return true;

        const int Slop = 2;

        var a = win.Placement.rcNormalPosition;
        var b = now.rcNormalPosition;

        return Math.Abs(a.Left - b.Left) <= Slop
            && Math.Abs(a.Top - b.Top) <= Slop
            && Math.Abs(a.Right - b.Right) <= Slop
            && Math.Abs(a.Bottom - b.Bottom) <= Slop;
    }

    /// <summary>
    /// Put one window back exactly as it was, including having been minimised.
    ///
    /// Minimised used to be skipped outright, on the reasoning that the user had put it there on
    /// purpose — which is right about the intent and wrong about the outcome. If something during
    /// the session brought that window back up, skipping it left it up. Restoring the state it was
    /// actually in is what honours the intent; the foreground check in SettleAsync is what keeps it
    /// from arguing with somebody who has since opened it deliberately.
    /// </summary>
    private static bool Apply(IntPtr hwnd, WINDOWPLACEMENT placement)
    {
        // Every placement read and written here is in real pixels. Mixing scales is how a window
        // restored onto a display at a different scale came back the wrong size.
        using var pixels = DpiScope.PerMonitor();

        // Already minimised and meant to be: nothing to do, and calling SetWindowPlacement anyway
        // would re-assert a restore rectangle Windows is perfectly happy with.
        if (placement.showCmd == SW_SHOWMINIMIZED && IsIconic(hwnd)) return false;

        var copy = placement;
        if (placement.showCmd == SW_SHOWMAXIMIZED)
        {
            // Restore the normal rect first, otherwise un-maximizing later snaps the window
            // to whatever size it had while maximized on the wrong monitor.
            var normal = placement;
            normal.showCmd = SW_SHOWNORMAL;
            SetWindowPlacement(hwnd, ref normal);
        }
        return SetWindowPlacement(hwnd, ref copy);
    }

    /// <summary>
    /// Steam is handled by SteamWindowMemory instead. Its desktop window and Big Picture share
    /// one process and one window class, so generic process+class matching restores the wrong
    /// one — and leaving both systems to fight over it produced exactly that bug.
    /// </summary>
    private static readonly string[] ExcludedProcesses = ["steamwebhelper", "steam"];

    /// <summary>This process. Its own windows are never managed by the layout store.</summary>
    private static readonly int OwnProcessId = Environment.ProcessId;

    private static bool ShouldTrack(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        if (IsCloaked(hwnd)) return false;                        // suspended UWP / other virtual desktops

        var ex = (uint)(long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;           // tray helpers, palettes

        var style = (uint)(long)GetWindowLongPtr(hwnd, GWL_STYLE);
        if ((style & WS_CHILD) != 0) return false;

        if (GetWindowTextLength(hwnd) == 0) return false;

        GetWindowThreadProcessId(hwnd, out int pid);

        // Never our own windows.
        //
        // This is the bug behind "the settings window keeps minimising when the session ends".
        // The settings window is visible, titled, and deliberately given WS_EX_APPWINDOW so it
        // gets a taskbar button — which is exactly the profile this tracker looks for. So it
        // was captured at session start and then had its old placement pushed back onto it at
        // session end, again and again, because the deferred restore keeps reapplying for 25
        // seconds afterwards. Any window opened in that period was dragged back to where it had
        // been half a session ago.
        //
        // The app has no business restoring its own windows through the same mechanism it uses
        // for everyone else's: it already knows where it wants them.
        if (pid == OwnProcessId) return false;

        return !ExcludedProcesses.Contains(ProcessNameOf(pid), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                   && cloaked != 0;
        }
        catch { return false; }
    }

    private static string ProcessNameOf(int pid)
    {
        try { return System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
        catch { return "?"; }
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        var buf = new StringBuilder(256);
        return GetClassName(hwnd, buf, buf.Capacity) > 0 ? buf.ToString() : "";
    }

    // ---- interop ----

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMAXIMIZED = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;

        public static WINDOWPLACEMENT Create() =>
            new() { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);

    // Minimised windows are still "visible" to Win32 — WS_VISIBLE stays set — so this is the
    // only way to ask the question that matters here.
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder buf, int max);

    // Must name the W export explicitly; the unsuffixed symbol doesn't exist on x64.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}

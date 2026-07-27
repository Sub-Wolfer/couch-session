using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CouchMode.Games;

/// <summary>
/// Last-resort close for a game the watcher was never following and Steam could not name — a
/// non-Steam title launched from a Big Picture shortcut, whose folder is in no library we scan.
///
/// It closes exactly one window: the foreground app, and only if it is filling the primary display
/// (the TV during a session). The game the user is playing is the focused, full-screen window on the
/// couch screen, so that is all this touches — a background app someone left full-screen, a window on
/// another monitor, Big Picture, Steam, the desktop and Couch Session itself are all left alone.
///
/// Only reached when the precise passes (the tracked game, and the Steam-by-folder close) found
/// nothing, so it never fires on top of a game that was already closed properly.
/// </summary>
public static class RunningGames
{
    public static int CloseForegroundOnPrimary()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !FillsPrimary(hwnd, PrimaryMonitor())) return 0;

        if (ExcludedClasses.Contains(ClassOf(hwnd))) return 0;   // Big Picture, the desktop

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return 0;

        var name = ProcessName((int)pid);
        if (name is null || ExcludedNames.Contains(name)) return 0;

        try
        {
            using var p = Process.GetProcessById((int)pid);
            // Ask the game to quit first (WM_CLOSE to its window) so it can save, ending it only if it
            // will not go — matching what Steam's "Stop" does rather than a hard kill.
            bool gone = GameClose.CloseGracefully(p, GameClose.DefaultGrace);
            Log.Info($"Closed the foreground game '{name}' as the session ended.");
            return gone ? 1 : 0;
        }
        catch
        {
            return 0;
        }
    }

    // Big Picture (SDL_app) and the two desktop windows Explorer draws behind everything.
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "SDL_app", "Progman", "WorkerW",
    };

    // Process names that legitimately own a full-screen window but must never be killed.
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "explorer", "dwm", "ApplicationFrameHost", "SearchHost",
        "ShellExperienceHost", "StartMenuExperienceHost", "TextInputHost", "sihost",
    };

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string? ProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }

    /// <summary>True if the window is on the primary monitor and covers the whole of it.</summary>
    private static bool FillsPrimary(IntPtr hwnd, IntPtr primary)
    {
        var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (mon != primary) return false;

        if (!GetWindowRect(hwnd, out var r)) return false;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;

        var m = mi.rcMonitor;   // a pixel of slop, so borderless windows still count
        return r.Left <= m.Left + 1 && r.Top <= m.Top + 1 && r.Right >= m.Right - 1 && r.Bottom >= m.Bottom - 1;
    }

    private static IntPtr PrimaryMonitor() =>
        MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);

    // ---- interop ----

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder buf, int max);
}

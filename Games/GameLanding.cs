using System.Runtime.InteropServices;

namespace CouchMode.Games;

/// <summary>
/// Keeps a freshly launched game on the primary monitor.
///
/// A couch session already makes the TV primary, so most games open there on their own. But some
/// remember the monitor they last ran on and open on it instead — on a multi-monitor desk that
/// leaves the couch staring at Big Picture while the game plays on a screen no one is watching.
///
/// The fix is to nudge the game's window onto the primary for the first few seconds after it starts,
/// which is the window during which it creates its window, shows a splash, and settles on a display.
/// A single move on launch is too early (the window may not exist yet) and too fragile (the game
/// repositions itself moments later), so this polls briefly and then stops, rather than fighting the
/// game for the whole session.
/// </summary>
public sealed class GameLanding : IDisposable
{
    private readonly Func<IntPtr> _gameWindow;
    private System.Threading.Timer? _poll;
    private DateTime _deadline;
    private bool _reported;

    private const int PollMs = 500;

    // Long enough to cover a pre-game launcher handing off to the game, short enough that it is not
    // still moving the window minutes into play if the user drags it somewhere on purpose.
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(12);

    public GameLanding(Func<IntPtr> gameWindow) => _gameWindow = gameWindow;

    /// <summary>Start pulling a just-launched game onto the primary. Safe to call again to re-arm.</summary>
    public void Begin()
    {
        _deadline = DateTime.UtcNow + Window;
        _reported = false;
        _poll ??= new System.Threading.Timer(_ => Nudge(), null, 0, PollMs);
    }

    /// <summary>Stop watching — the game has ended, or the window has expired.</summary>
    public void End()
    {
        _poll?.Dispose();
        _poll = null;
    }

    private void Nudge()
    {
        try
        {
            if (DateTime.UtcNow > _deadline) { End(); return; }

            var hwnd = _gameWindow();
            if (hwnd == IntPtr.Zero) return;

            IntPtr primaryMon = PrimaryMonitor();
            IntPtr windowMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (windowMon == primaryMon) return;   // already where it belongs

            if (!GetMonitorRect(primaryMon, out var primary)) return;
            if (!GetMonitorRect(windowMon, out var source)) return;
            if (!GetWindowRect(hwnd, out var win)) return;

            // A window that covers its whole monitor is full-screen or borderless: fill the primary so
            // it does not arrive as a small window stranded in a corner. Anything smaller keeps its
            // size and is shifted across by the offset between the two monitors.
            bool fills = win.Left <= source.Left && win.Top <= source.Top
                      && win.Right >= source.Right && win.Bottom >= source.Bottom;

            if (fills)
                SetWindowPos(hwnd, IntPtr.Zero, primary.Left, primary.Top,
                             primary.Right - primary.Left, primary.Bottom - primary.Top,
                             SWP_NOACTIVATE | SWP_NOZORDER);
            else
                SetWindowPos(hwnd, IntPtr.Zero,
                             primary.Left + (win.Left - source.Left),
                             primary.Top + (win.Top - source.Top),
                             0, 0, SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOSIZE);

            if (!_reported)
            {
                Log.Info("Moved the launching game onto the primary monitor.");
                _reported = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not move the game to the primary monitor: {ex.Message}");
        }
    }

    public void Dispose() => End();

    private static IntPtr PrimaryMonitor() =>
        MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);

    private static bool GetMonitorRect(IntPtr monitor, out RECT rect)
    {
        rect = default;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return false;
        rect = mi.rcMonitor;
        return true;
    }

    // ---- interop ----

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

using System.Diagnostics;

namespace CouchMode.Games;

/// <summary>
/// Close a game the way its own "quit" does, so it can save first — and only force it if it won't go.
///
/// CloseMainWindow posts WM_CLOSE to the game's main window, which is what Alt+F4 and Steam's Big
/// Picture "Stop" do; most titles treat it as "save and exit" (Dead Cells, for one, only writes its
/// save on a clean shutdown like this — a hard kill loses it). If nothing happens within a grace
/// period — no window to ask, the request ignored, or a hung process — the whole tree is ended
/// instead, so the session still tidies up, just a moment later.
/// </summary>
internal static class GameClose
{
    /// <summary>How long to let a game shut itself down before ending it outright.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Ask the process to close, wait up to <paramref name="grace"/> for it to save and exit, then
    /// kill its tree if it is still running. Returns true once the process is gone.
    /// </summary>
    // Never close these, whatever a caller passes in. Ending Steam or its Big Picture helper mid-
    // session is what wedged Steam so it would not reopen; the desktop/shell names are here for the
    // same "must never be killed" reason. A game is never one of these, so nothing is lost.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "steamservice", "gameoverlayui",
        "explorer", "dwm", "csrss", "winlogon", "services", "svchost", "sihost",
    };

    public static bool CloseGracefully(Process p, TimeSpan grace)
    {
        string name = SafeName(p);

        if (Protected.Contains(name))
        {
            Log.Warn($"Refusing to close protected process '{name}' — leaving it alone.");
            return false;
        }

        try
        {
            // Refresh so the main-window handle is current — it is cached on first read, and the
            // window often did not exist yet when this Process object was first created.
            bool asked = false;
            try { p.Refresh(); asked = p.CloseMainWindow(); }
            catch { /* no main window, or the request was refused */ }

            if (asked && p.WaitForExit((int)grace.TotalMilliseconds))
            {
                Log.Info($"{name} saved and closed after being asked to quit.");
                return true;
            }

            if (p.HasExited) return true;

            Log.Info(asked
                ? $"{name} did not quit within {grace.TotalSeconds:0}s of being asked; ending it."
                : $"{name} had no window to ask; ending it.");

            p.Kill(entireProcessTree: true);
            return p.WaitForExit(2000);
        }
        catch (Exception ex)
        {
            Log.Warn($"Closing {name} failed: {ex.Message}");
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* going away anyway */ }
            return false;
        }
    }

    /// <summary>End a process tree outright, but never a protected one. Returns true if it was ended.</summary>
    public static bool KillIfAllowed(Process p)
    {
        string name = SafeName(p);
        if (Protected.Contains(name))
        {
            Log.Warn($"Refusing to end protected process '{name}' — leaving it alone.");
            return false;
        }

        try
        {
            if (p.HasExited) return false;
            p.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Ending {name} failed: {ex.Message}");
            return false;
        }
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; }
        catch { return "the game"; }
    }
}

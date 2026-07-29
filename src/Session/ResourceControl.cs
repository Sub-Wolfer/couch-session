using System.Diagnostics;

namespace CouchMode.Session;

/// <summary>
/// Ask the desktop apps somebody chose to close as a session starts, and open them again after.
///
/// The point is memory and the cores those apps are sitting on. A browser with forty tabs, a chat
/// client and a music player are between one and several gigabytes and a steady trickle of
/// background work, and none of it is doing anything for you while you are on the sofa.
///
/// **Nothing here ever ends a process.** Each app is sent the same close request Alt+F4 sends, given
/// a few seconds, and left alone if it will not go. That is a deliberate limit and not a missing
/// feature: the competing app that inspired this terminates directly and warns that unsaved work may
/// be lost, which is a warning about a design choice rather than about the user's actions. An app
/// refusing to close is almost always an app with something unsaved in it, and the correct response
/// to that is to leave it exactly where it is.
///
/// What is closed is remembered for this session only, in memory. Restarting them reads that list,
/// so a session can never open something that was not already running when it began.
/// </summary>
public sealed class ResourceControl
{
    /// <summary>How long each app gets to close itself before this gives up on it.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(6);

    /// <summary>
    /// What was closed, in order, with how many windows each app had.
    ///
    /// The count matters. A browser with two windows open used to come back with one, because
    /// restarting meant launching the executable once — and one launch is one window. Opening it
    /// as many times as there were windows gives the same number back.
    /// </summary>
    private readonly List<ClosedApp> _closed = [];

    private sealed record ClosedApp(string Path, int Windows);

    /// <summary>Whether anything is waiting to be restarted, for the session preview and the log.</summary>
    public int ClosedCount => _closed.Count;

    /// <summary>
    /// Ask each chosen app that is currently running to close.
    ///
    /// Safe to call when the feature is off or the list is empty, which is the ordinary case.
    /// </summary>
    public void CloseForSession(AppConfig config)
    {
        _closed.Clear();

        if (!config.CloseAppsForSession || config.AppsToClose.Count == 0) return;

        var wanted = new HashSet<string>(config.AppsToClose, StringComparer.OrdinalIgnoreCase);
        int asked = 0, went = 0;

        // One pass per app, not per process. An app is several processes and the request goes to its
        // windows, so asking once per process would ask the same windows over and over.
        foreach (var path in wanted)
        {
            asked++;
            if (AskEverythingToClose(path)) went++;
        }

        Log.Info($"Resource control: asked {asked} app(s) to close, {went} did.");
    }

    /// <summary>
    /// Start the apps that were closed for this session, once it is over.
    ///
    /// Deliberately one instance each and nothing else. An app that was closed with three windows
    /// open comes back with one, because Windows has no way to ask for the rest — the setting says
    /// so rather than this pretending otherwise.
    /// </summary>
    public void ReopenAfterSession(AppConfig config)
    {
        if (!config.ReopenAppsAfterSession || _closed.Count == 0)
        {
            _closed.Clear();
            return;
        }

        int started = 0;

        foreach (var (path, windows) in _closed)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Log.Warn($"Resource control: {Path.GetFileName(path)} is no longer at {path}; not restarting it.");
                    continue;
                }

                // Once per window that was closed, capped so a stray count cannot open a wall of
                // windows. Most apps are single-instance and quietly focus what is already open,
                // which is the right outcome there; a browser opens another window, which is the
                // right outcome for the case this exists for.
                int many = Math.Clamp(windows, 1, 4);

                for (int i = 0; i < many; i++)
                {
                    // UseShellExecute so it starts the way a double-click would, with its own
                    // working directory and without inheriting this process's handles.
                    Process.Start(new ProcessStartInfo(path)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                    });

                    // A beat between them. Launched back to back, the second often arrives before
                    // the first app is listening and is swallowed.
                    if (i < many - 1) Thread.Sleep(700);
                }

                started += many;
            }
            catch (Exception ex)
            {
                Log.Warn($"Resource control: could not restart {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        Log.Info($"Resource control: reopened {started} window(s) across {_closed.Count} app(s).");
        _closed.Clear();
    }

    /// <summary>Forget what was closed without restarting any of it.</summary>
    public void Forget() => _closed.Clear();

    /// <summary>
    /// Ask every window belonging to this executable to close, then wait for it to go.
    ///
    /// [BUG] This used to call CloseMainWindow on each matching process, which closes exactly one
    /// window: the process's "main" one. A browser is several processes and several windows, and
    /// closing one window of Edge closes that window and leaves the rest — two open windows came
    /// back as one closed and one still there. Alt+F4 has the same scope, which is why the request
    /// has to go to every window rather than to the app.
    ///
    /// Still only ever a request. WM_CLOSE is what Alt+F4 sends; a window that puts up "save your
    /// work?" simply stays, and nothing here escalates.
    /// </summary>
    private bool AskEverythingToClose(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);

        var mine = new HashSet<int>();
        var live = Running(path).ToList();

        foreach (var proc in live)
        {
            try { mine.Add(proc.Id); } catch { }
        }

        if (mine.Count == 0) return false;

        int windows = 0;

        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (!mine.Contains((int)pid)) return true;

                // Posted, not sent. SendMessage would block this thread inside another app's
                // window procedure for as long as it wants to think about it, and a browser
                // deciding whether to warn about unsaved tabs can take a while.
                PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                windows++;
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn($"Resource control: could not list {name}'s windows: {ex.Message}");
        }

        if (windows == 0)
        {
            // Only helper processes, whose windows belong to a sibling that is not on the list.
            foreach (var proc in live) proc.Dispose();
            return false;
        }

        Log.Info($"Resource control: asked {windows} {name} window(s) to close.");

        bool allGone = true;

        foreach (var proc in live)
        {
            using (proc)
            {
                try { if (!proc.WaitForExit((int)Grace.TotalMilliseconds)) allGone = false; }
                catch { /* already gone, or cannot be watched */ }
            }
        }

        if (allGone)
        {
            if (!_closed.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)))
                _closed.Add(new ClosedApp(path, windows));

            Log.Info($"Resource control: {name} closed ({windows} window(s)).");
            return true;
        }

        // Almost always an unsaved-changes prompt on one of the windows. Ending it here is exactly
        // the thing this feature will not do.
        Log.Info($"Resource control: {name} did not fully close within {Grace.TotalSeconds:0}s "
               + "(it may be asking about unsaved work); leaving what is left running.");
        return false;
    }

    private const uint WM_CLOSE = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    /// <summary>Every running process started from exactly this executable.</summary>
    private static IEnumerable<Process> Running(string path)
    {
        string name;
        try { name = Path.GetFileNameWithoutExtension(path); }
        catch { yield break; }

        if (name.Length == 0) yield break;

        // Never this app, and never the shell. Somebody can point Browse at anything.
        if (Protected.Contains(name)) yield break;

        Process[] found;
        try { found = Process.GetProcessesByName(name); }
        catch { yield break; }

        foreach (var proc in found)
        {
            bool mine;

            try { mine = proc.Id != Environment.ProcessId && SamePath(proc, path); }
            catch { mine = false; }

            if (mine) yield return proc;
            else proc.Dispose();
        }
    }

    private static bool SamePath(Process proc, string path)
    {
        try
        {
            var actual = proc.MainModule?.FileName;
            return actual is not null && string.Equals(actual, path, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // A process this one cannot open — elevated, or protected by Windows. Not ours to close.
            return false;
        }
    }

    /// <summary>
    /// Never asked to close, whatever is in the list.
    ///
    /// The shell and its parts, because closing Explorer takes the taskbar and the desktop with it,
    /// and Steam, because a session is about to need it.
    /// </summary>
    /// <summary>Whether a process name is one this will never ask to close, so the picker can
    /// show it greyed with a reason instead of silently leaving it out.</summary>
    public static bool IsProtected(string processName) => Protected.Contains(processName);

    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "csrss", "winlogon", "services", "svchost", "sihost",
        "ShellExperienceHost", "StartMenuExperienceHost", "TextInputHost", "SearchHost",
        "steam", "steamwebhelper", "steamservice", "CouchSession",
    };
}

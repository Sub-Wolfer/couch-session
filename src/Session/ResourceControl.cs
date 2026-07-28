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

    /// <summary>What was actually closed, in the order it was closed, for restarting afterwards.</summary>
    private readonly List<string> _closed = [];

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

        foreach (var path in wanted)
        {
            foreach (var proc in Running(path))
            {
                using (proc)
                {
                    asked++;
                    if (Ask(proc, path)) went++;
                }
            }
        }

        if (asked == 0) Log.Info("Resource control: none of the chosen apps were running.");
        else Log.Info($"Resource control: asked {asked} app(s) to close, {went} did.");
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

        foreach (var path in _closed)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Log.Warn($"Resource control: {Path.GetFileName(path)} is no longer at {path}; not restarting it.");
                    continue;
                }

                // UseShellExecute so it starts the way a double-click would, with its own working
                // directory and without inheriting this process's handles.
                Process.Start(new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                });

                started++;
            }
            catch (Exception ex)
            {
                Log.Warn($"Resource control: could not restart {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        Log.Info($"Resource control: restarted {started} of {_closed.Count} closed app(s).");
        _closed.Clear();
    }

    /// <summary>Forget what was closed without restarting any of it.</summary>
    public void Forget() => _closed.Clear();

    /// <summary>
    /// Ask one process to close and wait for it. Never ends it.
    ///
    /// A process with no main window is skipped rather than ended. Chrome and Discord run several
    /// processes each and only one of them owns a window; asking that one closes the family, and
    /// there is nothing useful or safe to do with the rest.
    /// </summary>
    private bool Ask(Process proc, string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);

        try
        {
            proc.Refresh();

            if (proc.MainWindowHandle == IntPtr.Zero)
            {
                // A helper process of an app whose window belongs to a sibling. Closing the one with
                // the window takes these with it, so there is nothing to do here.
                return false;
            }

            if (!proc.CloseMainWindow())
            {
                Log.Info($"Resource control: {name} refused the close request; leaving it running.");
                return false;
            }

            if (proc.WaitForExit((int)Grace.TotalMilliseconds))
            {
                if (!_closed.Contains(path, StringComparer.OrdinalIgnoreCase)) _closed.Add(path);
                Log.Info($"Resource control: {name} closed.");
                return true;
            }

            // Almost always an unsaved-changes prompt. Ending it here is exactly the thing this
            // feature will not do.
            Log.Info($"Resource control: {name} did not close within {Grace.TotalSeconds:0}s "
                   + "(it may be asking about unsaved work); leaving it running.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Resource control: could not ask {name} to close: {ex.Message}");
            return false;
        }
    }

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
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "csrss", "winlogon", "services", "svchost", "sihost",
        "ShellExperienceHost", "StartMenuExperienceHost", "TextInputHost", "SearchHost",
        "steam", "steamwebhelper", "steamservice", "CouchSession",
    };
}

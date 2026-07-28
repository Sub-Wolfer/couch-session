using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CouchMode.Games;

/// <summary>
/// Notices when a watched game starts and when it stops.
///
/// The rule is simple and there are no timers waiting for something to settle: a game is
/// running while at least one process belonging to it exists, and it has stopped the moment the
/// last one goes. Everything below exists to make "belonging to it" accurate.
///
/// Membership is by install folder *or descent*. A game is very often not one process: a
/// launcher starts a bootstrapper which starts an anti-cheat service which starts the game, and
/// several of those exit along the way — sometimes outside the install folder entirely, which
/// is why folder matching alone is not enough. Tracking children of tracked processes follows
/// the whole chain, so the handover from launcher to game never looks like a stop.
///
/// This replaced an earlier design that waited a fixed period after a process exited, in case
/// another was about to appear. That could not be tuned: long enough for a launcher meant HDR
/// stayed on long after someone quit, and short enough to feel responsive meant the screen
/// flickered between launcher and game. Reading the process tree properly removes the question.
///
/// Detection is at process start rather than at the game's window, because a game reads the
/// display's HDR state once while creating its renderer and never looks again. Nothing is
/// injected into any process; this only ever reads the process list.
/// </summary>
public sealed class GameWatcher : IDisposable
{
    private IReadOnlyList<GameEntry> _watched = [];

    /// <summary>Every process id currently considered part of the running game.</summary>
    private readonly HashSet<int> _tracked = [];

    /// <summary>Ids already resolved to a path, so each is only looked up once.</summary>
    private HashSet<int> _examined = [];

    private System.Threading.Timer? _poll;
    private Process? _primary;
    private int _busy;

    /// <summary>A watched game started — a launch seen while watching, from nothing to running.</summary>
    public event Action<GameEntry>? GameStarted;

    /// <summary>
    /// A watched game was found already running and taken over (see <see cref="AdoptRunning"/>).
    /// Kept apart from <see cref="GameStarted"/> on purpose: a game carried over from a previous
    /// session must engage HDR and the priority boost, but must NOT raise the "a game just launched"
    /// signal that pushes Big Picture off the top — otherwise the couch UI drops away before the
    /// user can reach the library to pick that game back up.
    /// </summary>
    public event Action<GameEntry>? GameAdopted;

    /// <summary>The running watched game stopped — its last process has gone.</summary>
    public event Action<GameEntry>? GameStopped;

    public GameEntry? Current { get; private set; }

    /// <summary>The main tracked process, for anything that wants to act on the game itself.</summary>
    public Process? RunningProcess => _primary;

    /// <summary>
    /// Close the running game, if there is one — asking it to quit first so it can save, then ending
    /// anything left of its process tree.
    ///
    /// The game itself (its main window) is sent WM_CLOSE, the same shutdown Steam's "Stop" performs,
    /// and given a few seconds to write its save and exit. Only if it will not go is the tree ended
    /// outright. A game that puts up a save-or-quit prompt a controller cannot answer will sit until
    /// that grace runs out and then be closed regardless, so the session still always tidies up.
    /// </summary>
    public bool CloseRunning()
    {
        if (Current is not { } game || _tracked.Count == 0) return false;

        // The lead process owns the game's window; ask it to quit gracefully first.
        int leadPid = 0;
        try { if (_primary is { HasExited: false } pr) leadPid = pr.Id; } catch { /* stale handle */ }
        if (leadPid == 0) leadPid = _tracked.First();

        bool closed = false;
        try
        {
            using var lead = Process.GetProcessById(leadPid);
            closed = GameClose.CloseGracefully(lead, GameClose.DefaultGrace);
        }
        catch { /* lead already gone */ }

        // End anything else from the tree that outlived the graceful close (stray child processes).
        // Guarded so a protected process (Steam and friends) can never be caught up in it.
        foreach (var pid in _tracked.ToArray())
        {
            if (pid == leadPid) continue;
            try
            {
                using var p = Process.GetProcessById(pid);
                if (GameClose.KillIfAllowed(p)) closed = true;
            }
            catch { /* already gone, or cannot be touched — nothing to do */ }
        }

        if (closed) Log.Info($"Closed {game.Name} as the session ended.");
        return closed;
    }

    /// <summary>
    /// Close every running process whose executable sits inside one of the given folders, tracked or
    /// not. This is how a session ends a game that was never on the Auto-HDR list — the watcher was
    /// not following it, so there is nothing in the tracked set to end.
    /// </summary>
    public bool CloseGamesInside(IEnumerable<string> installDirs)
    {
        var dirs = installDirs.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        if (dirs.Count == 0) return false;

        int closed = 0;

        foreach (var (pid, _) in Snapshot())
        {
            var path = PathOfProcess((uint)pid);
            if (path is null || !dirs.Any(d => IsInside(path, d))) continue;

            try
            {
                using var p = Process.GetProcessById(pid);
                // Ask it to quit first so it can save; falls back to ending the tree. A child with no
                // window is ended straight away, so only the game itself spends the grace period.
                if (GameClose.CloseGracefully(p, GameClose.DefaultGrace)) closed++;
            }
            catch { /* already gone, or cannot be touched — nothing to do */ }
        }

        if (closed > 0) Log.Info($"Closed {closed} game process(es) as the session ended.");
        return closed > 0;
    }


    /// <summary>
    /// Take over a watched game that is already running — a session that begins (or the app being
    /// relaunched) while a game from the list is still up.
    ///
    /// The normal start path deliberately cannot do this: at watch-start every running process is
    /// filed as "already examined" so a mere config change never mistakes a game that was already
    /// open for a fresh launch. That same guard means a game running before the session began is
    /// invisible to the watcher — so the tracked set stays empty and nothing knows the game is there.
    /// This ignores the examined set on purpose, fills the tracked set, and fires the same start
    /// event so the close-on-end option and HDR/priority all engage for it too.
    /// </summary>
    public bool AdoptRunning()
    {
        if (Current is not null) return true;   // already tracking something

        foreach (var (pid, _) in Snapshot())
        {
            var path = PathOfProcess((uint)pid);
            if (path is null) continue;

            var match = _watched.FirstOrDefault(g => IsInside(path, g.InstallDir));
            if (match is null) continue;

            _tracked.Clear();
            _tracked.Add(pid);
            _examined.Add(pid);

            Current = match;
            HoldPrimary(pid);

            // Siblings and children get absorbed on the next tick by FollowRunning.
            Log.Info($"Adopted {match.Name}, already running when the session began (process {pid}).");
            GameAdopted?.Invoke(match);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Which of <paramref name="candidates"/> has something running from inside its folder right
    /// now, or null when none of them does.
    ///
    /// This is the only way to name a game Steam cannot. An Epic, GOG or Browse-added title has no
    /// app id to ask about, so the one thing that identifies it is that a process is running from
    /// its install folder — the same test <see cref="LookForStart"/> applies to the watch list,
    /// offered here against any list at all.
    ///
    /// The deepest folder wins. Browse accepts any folder, so a library root and a game inside it
    /// can both be candidates, and the game is the more precise answer.
    /// </summary>
    public static GameEntry? MatchRunning(IEnumerable<GameEntry> candidates)
    {
        var list = candidates.Where(g => !string.IsNullOrWhiteSpace(g.InstallDir)).ToList();
        if (list.Count == 0) return null;

        GameEntry? best = null;
        int bestDepth = -1;

        foreach (var (pid, _) in Snapshot())
        {
            var path = PathOfProcess((uint)pid);
            if (path is null) continue;

            foreach (var game in list)
            {
                int depth = game.InstallDir.TrimEnd('\\').Length;
                if (depth <= bestDepth) continue;
                if (!IsInside(path, game.InstallDir)) continue;

                best = game;
                bestDepth = depth;
            }
        }

        return best;
    }

    /// <summary>
    /// How often the process tree is read.
    ///
    /// Fast, because this is now the only thing standing between a game launching and HDR being
    /// switched — and because a tick is cheap: one snapshot, and a path lookup only for ids that
    /// were not there last time.
    /// </summary>
    private const int PollInterval = 250;

    public void SetWatchList(IEnumerable<GameEntry> games)
    {
        _watched = games.ToList();

        if (_watched.Count == 0) Stop();
        else Start();
    }

    public void Start()
    {
        if (_poll is not null) return;

        _examined = Snapshot().Keys.ToHashSet();
        _poll = new System.Threading.Timer(_ => Reconcile(), null, PollInterval, PollInterval);

        Log.Info($"Watching {_watched.Count} game(s); reading the process tree every {PollInterval}ms.");
    }

    public void Stop()
    {
        _poll?.Dispose();
        _poll = null;

        _tracked.Clear();
        ReleasePrimary();

        // [BUG] Stopping used to forget the game rather than report it as stopped. The tracked set
        // was cleared and Current was left pointing at whatever had been running, so GameStopped
        // never fired and nothing downstream was ever told to undo itself. Everything engaged on the
        // way in stayed engaged: HDR held on with no watcher left to switch it off, and the game's
        // process priority left raised. The way in is ordinary — turning Auto HDR or the priority
        // boost off, or unticking the last game, while playing — and the way out was restarting the
        // app.
        //
        // Announcing it is also simply true. Nothing is being watched from here, so as far as this
        // app is concerned the game is no longer running, and every listener should hear that.
        if (Current is { } game)
        {
            Current = null;
            GameStopped?.Invoke(game);
        }
    }

    /// <summary>Forget which processes were already running, so a rebuilt list starts clean.</summary>
    public void Rescan() => _examined = Snapshot().Keys.ToHashSet();

    /// <summary>
    /// Bring the tracked set up to date with reality, and raise an event if that changed things.
    ///
    /// One pass does both jobs — finding a game that has started, and noticing one that has
    /// stopped — because both are answered by the same snapshot.
    /// </summary>
    private void Reconcile()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        try
        {
            var live = Snapshot();

            if (Current is null) LookForStart(live);
            else FollowRunning(live);

            _examined.IntersectWith(live.Keys);
        }
        catch (Exception ex)
        {
            Log.Warn($"Process check failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Any new process sitting inside a watched folder starts that game.</summary>
    private void LookForStart(Dictionary<int, int> live)
    {
        foreach (var (pid, _) in live)
        {
            if (!_examined.Add(pid)) continue;   // already seen and rejected

            var path = PathOfProcess((uint)pid);
            if (path is null) continue;

            var match = _watched.FirstOrDefault(g => IsInside(path, g.InstallDir));
            if (match is null) continue;

            _tracked.Clear();
            _tracked.Add(pid);

            Current = match;
            HoldPrimary(pid);

            Log.Info($"Game started: {match.Name} ({Path.GetFileName(path)}, process {pid}).");
            GameStarted?.Invoke(match);
            return;
        }
    }

    /// <summary>
    /// Update the tracked set for the running game, and report a stop when it empties.
    ///
    /// Children are absorbed before dead processes are dropped. That ordering is the point: at
    /// the instant a launcher spawns a game and exits, both exist, so taking the child in first
    /// means the set is never briefly empty and a handover is never mistaken for a stop.
    /// </summary>
    private void FollowRunning(Dictionary<int, int> live)
    {
        if (Current is not { } game) return;

        // Absorb descendants. Repeated until nothing new appears, so a chain of launcher →
        // bootstrapper → game is picked up whole even if it forms between two ticks.
        bool grew = true;

        while (grew)
        {
            grew = false;

            foreach (var (pid, parent) in live)
            {
                if (_tracked.Contains(pid)) continue;
                if (!_tracked.Contains(parent)) continue;

                _tracked.Add(pid);
                _examined.Add(pid);
                grew = true;
            }
        }

        // Absorb anything else running from the game's own folder — a second executable started
        // independently rather than as a child, which some launchers do.
        foreach (var (pid, _) in live)
        {
            if (_tracked.Contains(pid) || !_examined.Add(pid)) continue;

            var path = PathOfProcess((uint)pid);
            if (path is not null && IsInside(path, game.InstallDir)) _tracked.Add(pid);
        }

        _tracked.IntersectWith(live.Keys);

        if (_tracked.Count > 0)
        {
            // Keep the primary handle pointing at something alive, for the priority boost.
            if (_primary is null || _primary.HasExited) HoldPrimary(_tracked.First());
            return;
        }

        Current = null;
        ReleasePrimary();

        Log.Info($"Game stopped: {game.Name}. Nothing from it is running any more.");
        GameStopped?.Invoke(game);
    }

    private void HoldPrimary(int pid)
    {
        ReleasePrimary();

        try
        {
            var process = Process.GetProcessById(pid);
            if (!process.HasExited) _primary = process;
            else process.Dispose();
        }
        catch
        {
            // Not every process can be opened, and nothing here depends on it.
        }
    }

    private void ReleasePrimary()
    {
        _primary?.Dispose();
        _primary = null;
    }

    /// <summary>
    /// Every live process id with its parent, in one call.
    ///
    /// A toolhelp snapshot is used rather than EnumProcesses because it carries the parent id,
    /// which is what makes following a launcher's children possible at all. No handle is opened
    /// and no path is read here — this runs several times a second, so it stays cheap.
    /// </summary>
    private static Dictionary<int, int> Snapshot()
    {
        var found = new Dictionary<int, int>(320);

        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE) return found;

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };

            if (!Process32First(snapshot, ref entry)) return found;

            do found[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return found;
    }

    /// <summary>True when <paramref name="path"/> sits inside <paramref name="folder"/>.</summary>
    private static bool IsInside(string path, string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder).TrimEnd('\\') + '\\';
            return Path.GetFullPath(path).StartsWith(full, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Full image path for a process id, via QueryFullProcessImageName.
    ///
    /// Process.MainModule would be simpler but throws on anything running elevated or as a
    /// different user, which many games and launchers do. This only needs LIMITED_INFORMATION.
    /// </summary>
    private static string? PathOfProcess(uint pid)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        // Silent on the way out. Stop now reports the running game as stopped, which is right when
        // watching is being switched off and wrong when the app is closing: HdrCoordinator puts HDR
        // back itself before disposing this, and a listener firing during shutdown is asking a
        // half-torn-down object to do work it no longer can.
        GameStarted = null;
        GameAdopted = null;
        GameStopped = null;

        Stop();
    }

    // ---- interop ----

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private static readonly IntPtr INVALID_HANDLE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
}

using System.Diagnostics;
using Microsoft.Win32;
using CouchMode.Display;
using CouchMode.Games;
using CouchMode.Ui;

namespace CouchMode.Session;

/// <summary>
/// Turns HDR on for the primary display while a chosen game runs, and puts it back afterwards.
///
/// "Primary" is resolved at the moment the game starts, not configured in advance: during a
/// couch session the TV is primary, and if someone launches a game at their desk the monitor
/// is. Either way exactly one display is touched — see <see cref="HdrControl.SetPrimaryHdr"/>.
/// </summary>
public sealed class HdrCoordinator : IDisposable
{
    private readonly GameWatcher _watcher = new();
    private AppConfig _config;

    /// <summary>Whether this feature turned HDR on, so it only ever turns off what it started.</summary>
    private bool _weTurnedItOn;

    /// <summary>
    /// The display HDR was actually switched on for.
    ///
    /// [BUG] Ownership used to be the bool above and nothing else, and switching off called
    /// SetPrimaryHdr(false), which acts on whatever is primary at that moment. Start a ticked game at
    /// the desk, begin a session while it is still running, quit the game: HDR went off on the
    /// television and the monitor was left in HDR, with a washed-out desktop and nothing saying why.
    /// </summary>
    private string? _hdrOn;

    /// <summary>
    /// The user turned HDR on by hotkey with no game running, so the next game to launch is the one
    /// they turned it on for. It is remembered (and its HDR held until it closes) whether or not it
    /// was already on the list — which is the right order for HDR, since most games read the display
    /// state at launch and need it on beforehand. A short poll and a deadline back it up.
    /// </summary>
    private bool _hdrArmed;
    private DateTime _armUntil;
    private System.Threading.Timer? _armTimer;

    /// <summary>
    /// The games that could be the one that launches, taken once when arming.
    ///
    /// Not rebuilt on every poll: reading every launcher's catalogue six hundred milliseconds apart
    /// would be pure waste, and a game installed inside the forty-five seconds this waits is not a
    /// case worth paying for.
    /// </summary>
    private IReadOnlyList<GameEntry> _armCandidates = [];

    /// <summary>What was already running when HDR was armed, so only something new counts.</summary>
    private string? _armBaselineKey;

    /// <summary>
    /// How many watched games are running, and a signal when that crosses zero.
    ///
    /// The watcher spots a game by its process — including a pre-game launcher like DOOM's, which
    /// Steam's RunningAppID does not always cover — so this is the reliable "something is playing"
    /// signal. Big Picture's front guard listens to it to step off the top layer, which RunningAppID
    /// alone was missing for exactly those launchers.
    /// </summary>
    private int _running;

    /// <summary>Fires true when the first watched game starts, false when the last one stops.</summary>
    public event Action<bool>? GameActivityChanged;

    /// <summary>
    /// Close the running game as a session ends — whether or not it was on the Auto-HDR list, and
    /// whether or not HDR is even in use. A game the watcher was following is ended from the tracked
    /// set; a game it was not (never ticked, or the whole watcher idle) is found through Steam's
    /// RunningAppID and closed by its install folder.
    /// </summary>
    public bool CloseRunningGame()
    {
        // Precise first: a tracked game (any window state), then a Steam game by its install folder —
        // this reaches a game that is minimised or sitting behind Big Picture.
        bool closed = _watcher.CloseRunning();

        int appId = SteamRunningAppId();
        if (appId != 0 && GameLibrary.SteamInstallDir(appId) is { } dir)
            closed |= _watcher.CloseGamesInside([dir]);

        // The window fallback catches a non-Steam title whose folder could not be found — but only
        // when Steam actually reports something running (RunningAppID set). Without that gate, a
        // session that ended with no game ever launched would fall through to here and close whatever
        // the user happened to have full-screen on the TV. No game running means nothing to close.
        if (!closed && appId != 0)
            closed = RunningGames.CloseForegroundOnPrimary() > 0;

        return closed;
    }

    /// <summary>
    /// Remember the HDR choice for the game running right now, so Auto HDR applies it by itself next
    /// time. On adds it to the list (and makes sure Auto HDR is switched on, or the choice would
    /// never fire); off drops it. Returns the game's name, or null when no game is running — or when
    /// one is, but it belongs to no library and no folder on the list, so there is nothing to name it.
    /// </summary>
    public string? RememberHdrForRunningGame(bool on)
        => RunningGame() is { } game ? Remember(game, on) : null;

    /// <summary>
    /// The game running right now, however it was launched, or null when nothing identifiable is.
    ///
    /// Three routes, most exact first. Steam names its own game outright through RunningAppID. A
    /// game the watcher is already following is equally exact and costs nothing. Everything else —
    /// Epic, GOG, or a folder added by Browse — has no id to ask about, so it is found the only way
    /// it can be: by matching a running process back to an install folder.
    ///
    /// That third route is what the games list has always promised and the code could not do. Until
    /// it existed, turning HDR on during an Epic game remembered nothing and said so with silence.
    /// </summary>
    /// <summary>
    /// Whichever game is running right now, if one is. Public because the session-end confirmation
    /// names it: "Close Elden Ring?" is a question about a specific thing the user is doing, and
    /// "Close the game?" is a question about a category.
    /// </summary>
    public GameEntry? RunningGame(IReadOnlyList<GameEntry>? candidates = null)
    {
        int appId = SteamRunningAppId();
        if (appId != 0 && GameLibrary.SteamGameByAppId(appId) is { } steam) return steam;

        if (_watcher.Current is { } tracked) return tracked;

        return GameWatcher.MatchRunning(candidates ?? Candidates());
    }

    /// <summary>
    /// Every game that could be the one running: the installed library, plus whatever is already on
    /// the HDR list. The second is not redundant — Browse accepts a folder no launcher knows about,
    /// and that game still has to be recognisable to be dropped from the list again.
    /// </summary>
    private IReadOnlyList<GameEntry> Candidates()
    {
        var found = new List<GameEntry>();

        try { found.AddRange(GameLibrary.DiscoverAll()); }
        catch (Exception ex) { Log.Warn($"Could not read the game library: {ex.Message}"); }

        found.AddRange(_config.HdrGames
            .Where(g => !string.IsNullOrWhiteSpace(g.InstallDir))
            .Select(g => new GameEntry(g.Name, g.InstallDir, GameSource.Manual)));

        return found;
    }

    private string Remember(GameEntry game, bool on)
    {
        string key = game.Key;
        bool present = _config.HdrGames.Any(g => g.InstallDir.TrimEnd('\\').ToLowerInvariant() == key);

        if (on)
        {
            if (!present)
                _config.HdrGames.Add(new HdrGame { Name = game.Name, InstallDir = game.InstallDir });

            // Otherwise the remembered choice sits in a list nothing reads.
            _config.AutoHdrEnabled = true;
            Log.Info($"Remembered HDR on for {game.Name}; it will come on automatically next time.");
        }
        else
        {
            _config.HdrGames.RemoveAll(g => g.InstallDir.TrimEnd('\\').ToLowerInvariant() == key);
            Log.Info($"Forgot HDR for {game.Name}; it will no longer come on by itself.");
        }

        try { _config.Save(); }
        catch (Exception ex) { Log.Warn($"Saving the HDR choice failed: {ex.Message}"); }

        Reconfigure(_config);

        // Make the running game behave exactly like one Auto HDR caught for itself, so it turns HDR
        // off when it closes. The hotkey flipped HDR directly — which the coordinator would not
        // otherwise know to undo — and the game was already running, so the watcher never saw it
        // "start". Adopting it means its close is noticed; claiming _weTurnedItOn means that close
        // switches HDR back off. Skipped when HDR is on for the whole session, which is the one case
        // the user asked to leave holding it on until the session itself ends.
        if (on && !_config.HdrForWholeSession)
        {
            _watcher.AdoptRunning();
            _weTurnedItOn = true;
            _hdrOn = Display.DisplayManager.CurrentPrimaryDevicePath();
            RepairDisplaysAfterHdr();
        }
        else if (!on)
        {
            _weTurnedItOn = false;   // the hotkey just turned HDR off; nothing is holding it on now
            _hdrOn = null;
        }

        return game.Name;
    }

    /// <summary>
    /// Arm HDR for the next game to launch — called when the hotkey turned HDR on with no game
    /// running, i.e. the user set HDR up before launching, the order HDR actually needs. A watched
    /// game is caught by the coordinator's own launch handling; anything else is caught by the short
    /// poll here, which watches Steam's RunningAppID for something new.
    /// </summary>
    public void ArmHdrForNextGame()
    {
        if (_config.HdrForWholeSession) return;   // the session already holds HDR; nothing to arm

        _hdrArmed = true;
        _armCandidates = Candidates();
        _armBaselineKey = RunningGame(_armCandidates)?.Key;
        _armUntil = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        _armTimer ??= new System.Threading.Timer(_ => PollArmed(), null, 600, 600);
        Log.Info("HDR armed by hotkey: the next game to launch will hold HDR and be remembered.");
    }

    private void PollArmed()
    {
        try
        {
            if (!_hdrArmed || DateTime.UtcNow > _armUntil) { Disarm(); return; }

            // Changed their mind — HDR is back off — so stop waiting.
            try { if (!HdrControl.PrimaryStatus().Enabled) { Disarm(); return; } } catch { }

            // Nothing new has launched yet. Matched against the list taken when arming, so an Epic
            // or GOG title counts here exactly as a Steam one does.
            if (RunningGame(_armCandidates) is not { } game || game.Key == _armBaselineKey) return;

            // A game launched with HDR already on. Remembering it also tracks it and claims the HDR,
            // so it switches back off when the game closes. Under the same setting as the hotkey —
            // it means "remember when HDR is turned on", and this is one of the ways it gets turned on.
            if (_config.HdrHotkeyRemembersGame)
            {
                Remember(game, true);
                Log.Info($"HDR armed: {game.Name} launched; remembered and holding its HDR.");
            }

            Disarm();
        }
        catch (Exception ex)
        {
            Log.Warn($"HDR arm poll failed: {ex.Message}");
            Disarm();
        }
    }

    private void Disarm()
    {
        _hdrArmed = false;
        _armCandidates = [];
        _armBaselineKey = null;
        _armTimer?.Dispose();
        _armTimer = null;
    }

    private static int SteamRunningAppId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("RunningAppID") is int id ? id : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Pick up a game that was already running when the session began, so it is tracked — for the
    /// close-on-end option and for turning HDR/priority on for it — even though the watcher never
    /// saw it launch. Big Picture, not this, is what the user drives to get back into the game.
    /// </summary>
    public bool AdoptRunningGame() => _watcher.AdoptRunning();

    /// <summary>
    /// The main window of the game (or launcher) currently tracked, or zero when there is none or it
    /// has not drawn a window yet. For handing the foreground to a launcher that opened behind Big
    /// Picture.
    /// </summary>
    /// <summary>
    /// The process behind that window, when the running game is one this app is watching.
    ///
    /// Null for a game Steam is running that is not on the HDR list — the window can still be found
    /// (see below), but the process cannot, and anything needing a process id has to cope with that.
    /// </summary>
    public System.Diagnostics.Process? RunningGameProcess
    {
        get
        {
            var p = _watcher.RunningProcess;

            try { return p is not null && !p.HasExited ? p : null; }
            catch { return null; }
        }
    }

    public IntPtr RunningGameWindow()
    {
        IntPtr tracked = IntPtr.Zero;

        var p = _watcher.RunningProcess;
        if (p is not null)
        {
            try
            {
                p.Refresh();
                if (!p.HasExited) tracked = p.MainWindowHandle;
            }
            catch { /* stale handle — leave it at zero */ }
        }

        // Whatever Steam reports running, chosen by size. Also covers a game the watcher is not
        // following at all, and one whose window was not up when the watcher latched on.
        var largest = CouchMode.Steam.BigPictureLauncher.FindRunningGameWindow();

        // [BUG] The tracked process used to win outright, and for a game with its own launcher that
        // is the wrong window. DOOM Eternal keeps its launcher running behind the game, the launcher
        // starts first so the watcher latches onto it, and its executable is under steamapps\common
        // like everything else — so nothing about its identity gives it away. Starting a session moved
        // the launcher to the television and left the game where it was.
        //
        // Size is what separates them: the game fills a screen, the launcher is a small window someone
        // pressed Play in.
        var chosen = CouchMode.Steam.BigPictureLauncher.LargerOf(tracked, largest);

        if (chosen != tracked && tracked != IntPtr.Zero)
            Log.Info("The tracked process owns a smaller window than the game's; using the larger one "
                   + "(a launcher left running behind its game).");

        return chosen;
    }

    public HdrCoordinator(AppConfig config)
    {
        _config = config;

        _watcher.GameStarted += OnGameStarted;
        _watcher.GameAdopted += OnGameAdopted;
        _watcher.GameStopped += OnGameStopped;

        Reconfigure(config);
    }

    /// <summary>
    /// Turn HDR on for the session, before anything has launched.
    ///
    /// This is the belt-and-braces path. Per-game switching has to notice a launch and then
    /// wait for the display to apply the change, and a game that starts rendering quickly can
    /// still read the old state. Nothing is racing anything here: the session begins, HDR goes
    /// on, and every game launched afterwards sees an HDR display from the outset.
    /// </summary>
    public void ArmForSession()
    {
        // Its own feature now, not a mode of the per-game switch. Someone can run HDR for the
        // whole session with no game list at all.
        if (!_config.HdrForWholeSession) return;

        try
        {
            var status = HdrControl.PrimaryStatus();

            if (!status.Found || !status.Supported)
            {
                Log.Info("Auto HDR: the session display reports no HDR support; leaving it alone.");
                return;
            }

            if (status.Enabled) return;

            // [BUG] The display was named twice: SetPrimaryHdr resolved the primary itself, and
            // then CurrentPrimaryDevicePath was asked again afterwards to record which one it
            // had been. Those two answers are not reliably the same. Switching HDR provokes
            // Windows into re-applying its saved topology, so between the change landing and the
            // question being asked, primary can have moved to a display this app never touched —
            // and the switch-off at game close then aimed at that one, found HDR already off
            // there, and left the real screen in HDR. The log said "already off; nothing to do".
            //
            // Named once now, before anything moves, and both the switch-on and the record use
            // that single answer. SetPrimaryHdr is still the fallback for the case where the
            // primary cannot be resolved to a device path at all.
            var target = Display.DisplayManager.CurrentPrimaryDevicePath();

            bool lit = target is { Length: > 0 }
                           ? HdrControl.SetHdrFor(target, true)
                           : HdrControl.SetPrimaryHdr(true);

            if (!lit) return;

            _weTurnedItOn = true;
            _hdrOn = target;
            RepairDisplaysAfterHdr();
            Notify(Words.NoticeHdrOn, Words.NoticeHdrSessionReady, Theme.Good);
        }
        catch (Exception ex)
        {
            Log.Error("Auto HDR could not be armed for the session", ex);
        }
    }

    /// <summary>Drop session HDR on the way back to the desktop.</summary>
    public void DisarmAfterSession()
    {
        Disarm();   // stop waiting for a game to arm HDR — the session is over

        if (!_weTurnedItOn) return;

        // A game still running has a better claim on it than the session does.
        if (_watcher.Current is not null) return;

        _weTurnedItOn = false;
        _hdrOn = null;

        try
        {
            if (HdrControl.SetPrimaryHdr(false))
                Notify(Words.NoticeHdrOff, Words.NoticeHdrDesktop, Theme.TextDim);
        }
        catch (Exception ex) { Log.Warn($"Auto HDR could not be turned off: {ex.Message}"); }
    }

    /// <summary>Rebuild the watch list from config. Called at startup and after every save.</summary>
    public void Reconfigure(AppConfig config)
    {
        _config = config;

        // Either feature is reason enough to watch.
        //
        // The watcher is shared: Auto HDR and the priority boost both act on "a game from the
        // list started", so gating it on Auto HDR alone meant turning that off silently
        // disabled the priority boost too.
        bool wanted = config.AutoHdrEnabled || config.GamePriorityEnabled;

        if (!wanted)
        {
            _watcher.SetWatchList([]);
            return;
        }

        // Two different lists, watched together.
        //
        // [BUG] The watch list was the ticked HDR games and nothing else, and the priority boost
        // rode along on it — so raising a game's priority silently required that game to be ticked
        // for HDR, which is a completely unrelated decision. Nothing said so, and with the HDR list
        // now empty by default the setting did nothing at all for most people.
        //
        // Priority is about any game, so when it is on the whole discovered library is watched.
        // HDR is about the ticked ones, and EngageFor checks membership before touching the
        // display — see IsHdrGame. One watcher, two questions.
        var watched = config.HdrGames
            .Select(g => new GameEntry(g.Name, g.InstallDir, GameSource.Manual))
            .ToList();

        int ticked = watched.Count;

        if (config.GamePriorityEnabled)
        {
            var already = new HashSet<string>(watched.Select(Key), StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var found in Games.GameLibrary.DiscoverAll())
                    if (already.Add(Key(found))) watched.Add(found);
            }
            catch (Exception ex)
            {
                // The ticked games are still watched. Losing discovery costs the priority boost on
                // unticked games and nothing else.
                Log.Warn($"Could not list installed games for the priority boost: {ex.Message}");
            }
        }

        if (watched.Count == 0)
        {
            _watcher.SetWatchList([]);
            return;
        }

        _watcher.SetWatchList(watched);
        _watcher.Rescan();

        Log.Info($"Watching {watched.Count} game(s): {ticked} for HDR"
               + (config.GamePriorityEnabled ? $", all of them for the priority boost." : "."));
    }

    private void OnGameStarted(GameEntry game)
    {
        // A launch seen this session. Raise the running signal before any of the HDR/priority
        // early-returns below, so Big Picture drops off the top for the launcher whatever those
        // settings are.
        if (++_running == 1) GameActivityChanged?.Invoke(true);

        EngageFor(game);
    }

    /// <summary>
    /// A game that was already running when the session began. Give it HDR and the priority boost
    /// exactly as a launch would, but do NOT raise the running signal — Big Picture has to stay on
    /// top so the user can reach the library and pick this game back up. It steps aside on its own
    /// once that game is the window in front.
    /// </summary>
    private void OnGameAdopted(GameEntry game) => EngageFor(game);

    /// <summary>The install folder, normalised, which is what identifies a game everywhere here.</summary>
    private static string Key(GameEntry game) => game.InstallDir.TrimEnd('\\').ToLowerInvariant();

    /// <summary>
    /// Whether this game is one the user ticked for HDR.
    ///
    /// Needed since the watch list stopped being the HDR list. Priority watches everything installed
    /// when it is on, and without this check every game that started would switch the display into
    /// HDR — which is the opposite of the per-game promise the HDR page makes.
    /// </summary>
    private bool IsHdrGame(GameEntry game)
    {
        string key = Key(game);

        foreach (var ticked in _config.HdrGames)
            if (string.Equals(ticked.InstallDir.TrimEnd('\\').ToLowerInvariant(), key,
                              StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private void EngageFor(GameEntry game)
    {
        // Any game, ticked or not. That is the whole point of the setting.
        if (_config.GamePriorityEnabled && _watcher.RunningProcess is { } process)
            GamePriority.Raise(process);

        if (!_config.AutoHdrEnabled) return;

        // HDR only for the games chosen for it.
        if (!_config.HdrForWholeSession && !IsHdrGame(game)) return;

        // Whole-session HDR overrides the game list: the display is already in HDR for the entire
        // session, so there is nothing for a per-game switch to add. This is also why _weTurnedItOn
        // being set by the session arm below makes the rest a no-op — but the check is stated
        // outright so the intent survives a future refactor of that flag.
        if (_config.HdrForWholeSession) return;

        // Already holding it on from the launcher, or from the session. Nothing to do, and
        // saying so again would be a second notification for one launch.
        if (_weTurnedItOn) return;

        try
        {
            var status = HdrControl.PrimaryStatus();
            var display = HdrControl.PrimaryId();

            Log.Info($"Auto HDR: {game.Name} started. Primary display '{display}' — "
                   + $"resolved: {status.Found}, HDR supported: {status.Supported}, "
                   + $"currently on: {status.Enabled}.");

            if (!status.Found)
            {
                Log.Warn("Auto HDR: could not work out which display is primary, so nothing was "
                       + "changed. This is not the same as the display lacking HDR.");
                return;
            }

            if (!status.Supported)
            {
                Log.Info($"Auto HDR: '{display}' reports no HDR support; leaving it alone.");
                return;
            }

            if (status.Enabled)
            {
                // HDR is already on, and this game is one the user ticked for HDR. Claim it either
                // way, so closing the game still switches HDR off.
                //
                // This used to hold HDR only when the hotkey had armed it moments earlier, on the
                // reasoning that anything else was "HDR they run for other reasons" and should be
                // left strictly alone. That reasoning misses the ordinary case: HDR switched on for a
                // film or a video, forgotten about, and then a ticked game launched on top of it. The
                // game closes, HDR stays on, and the desktop sits there washed out — which is the
                // exact outcome ticking the game was meant to prevent.
                //
                // The trade is deliberate. Somebody who runs HDR permanently and plays a ticked game
                // now has it switched off when they quit. That is the smaller surprise: they ticked
                // the game, which is a statement that HDR belongs to that game, and switching it back
                // on is one hotkey press. The other way round leaves a washed-out desktop with no
                // obvious cause.
                _weTurnedItOn = true;
                _hdrOn = Display.DisplayManager.CurrentPrimaryDevicePath();
                RepairDisplaysAfterHdr();

                // The toast is for the armed case only. Announcing "HDR on" when nothing was turned
                // on would be reporting an event that did not happen; the hotkey press is a thing the
                // user just did and expects an answer to.
                if (_hdrArmed)
                {
                    _hdrArmed = false;
                    Log.Info($"Auto HDR: {game.Name} started with HDR already on from the hotkey; holding it.");
                    Notify(Words.NoticeHdrOn, $"{game.Name} {Words.NoticeHdrRunning}", Theme.Good);
                }
                else
                {
                    Log.Info($"Auto HDR: {game.Name} started with HDR already on; holding it so its "
                           + "close switches HDR off.");
                }

                return;
            }

            // [BUG] The display was named twice: SetPrimaryHdr resolved the primary itself, and
            // then CurrentPrimaryDevicePath was asked again afterwards to record which one it
            // had been. Those two answers are not reliably the same. Switching HDR provokes
            // Windows into re-applying its saved topology, so between the change landing and the
            // question being asked, primary can have moved to a display this app never touched —
            // and the switch-off at game close then aimed at that one, found HDR already off
            // there, and left the real screen in HDR. The log said "already off; nothing to do".
            //
            // Named once now, before anything moves, and both the switch-on and the record use
            // that single answer. SetPrimaryHdr is still the fallback for the case where the
            // primary cannot be resolved to a device path at all.
            var target = Display.DisplayManager.CurrentPrimaryDevicePath();

            bool lit = target is { Length: > 0 }
                           ? HdrControl.SetHdrFor(target, true)
                           : HdrControl.SetPrimaryHdr(true);

            if (!lit) return;

            _weTurnedItOn = true;
            _hdrOn = target;
            RepairDisplaysAfterHdr();
            Notify(Words.NoticeHdrOn, $"{game.Name} {Words.NoticeHdrRunning}", Theme.Good);
        }
        catch (Exception ex)
        {
            Log.Error($"Auto HDR failed to turn on for {game.Name}", ex);
        }
    }

    /// <summary>
    /// Turn HDR off when the game ends.
    ///
    /// Always off, never "back to how it was".
    ///
    /// Restoring the previous state sounds careful but is wrong for what this feature is: it is
    /// only ever asked to turn HDR *on* for a game. If HDR happened to be on beforehand, the
    /// restore left it on afterwards and nothing appeared to switch off — the reported bug. And
    /// leaving HDR on at the desktop is the thing worth avoiding, since SDR content on an HDR
    /// desktop looks washed out.
    ///
    /// The one thing it will not do is switch off HDR it never switched on, so someone who runs
    /// their desktop in HDR permanently is left alone.
    /// </summary>
    private void OnGameStopped(GameEntry game)
    {
        if (_running > 0 && --_running == 0) GameActivityChanged?.Invoke(false);

        GamePriority.Restore();

        // Straight off. The watcher only reports a stop once nothing belonging to the game is
        // running at all, so there is nothing left to wait for.
        if (!_weTurnedItOn) return;

        var where = _hdrOn;
        _weTurnedItOn = false;
        _hdrOn = null;

        try
        {
            if (Off(where))
                Notify(Words.NoticeHdrOff, $"{game.Name} {Words.NoticeHdrClosed}", Theme.TextDim);
        }
        catch (Exception ex)
        {
            Log.Error($"Auto HDR failed to turn off after {game.Name}", ex);
        }
    }

    /// <summary>
    /// Switch HDR off on the display it was switched on for, falling back to the primary.
    ///
    /// The fallback covers a recorded display that has since been unplugged, and the case where
    /// nothing was recorded because the claim came from an older build's saved state.
    /// </summary>
    private static bool Off(string? where) =>
        where is { Length: > 0 } ? HdrControl.SetHdrFor(where, false) : HdrControl.SetPrimaryHdr(false);

    /// <summary>
    /// Put the session's displays back after an HDR change disturbed them. Set by the tray.
    ///
    /// Null outside a session, and the handler checks again itself, so this can never fire at a desk.
    /// </summary>
    public Action? ReassertDisplays { get; set; }

    /// <summary>
    /// Give Windows its moment to re-apply a topology, then undo it.
    ///
    /// Delayed because the re-attach does not happen during the HDR call — it arrives a beat later,
    /// as the driver re-enumerates, so checking immediately finds nothing wrong.
    /// </summary>
    private void RepairDisplaysAfterHdr()
    {
        if (ReassertDisplays is not { } repair) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500).ConfigureAwait(false);
                repair();
            }
            catch (Exception ex) { Log.Warn($"Could not repair the displays after HDR: {ex.Message}"); }
        });
    }

    /// <summary>
    /// Whether a game is parked on the couch display with the session over. Set by the tray.
    ///
    /// The one case where HDR must not follow: swapping to the desktop hands primary back to the
    /// monitor, but the game has not moved, so neither should HDR.
    /// </summary>
    public Func<bool>? GameParked { get; set; }

    /// <summary>
    /// Switch HDR off on the display holding it, while that display is still attached.
    ///
    /// Called as a session begins. Ownership is deliberately kept: the display is still the one this
    /// app is responsible for until the follow moves it, and the follow's own switch-off then finds
    /// it already off and says so, which costs one log line and no behaviour.
    /// </summary>
    public void ReleaseBeforeDisplayChange()
    {
        if (!_weTurnedItOn || _hdrOn is not { Length: > 0 } was) return;

        Log.Info("Auto HDR: taking HDR off the current display before the session moves them.");

        try { HdrControl.SetHdrFor(was, false); }
        catch (Exception ex) { Log.Warn($"Auto HDR could not release the display first: {ex.Message}"); }
    }

    /// <summary>When this last moved HDR itself, so its own change is not treated as a new one.</summary>
    private DateTime _followedAtUtc = DateTime.MinValue;

    /// <summary>
    /// Move HDR to the display that has just become primary.
    ///
    /// HDR belongs on the screen the game is being played on, and that is normally the primary — so
    /// starting a session mid-game should carry HDR from the monitor to the television rather than
    /// leave it on a screen nobody is looking at.
    ///
    /// Every exit logs. A previous attempt at this returned silently at its first guard and looked
    /// exactly like an event that never arrived, which cost an evening of chasing the wrong thing.
    /// </summary>
    public void FollowPrimaryDisplay()
    {
        if (!_weTurnedItOn)
        {
            Log.Info("Auto HDR: display changed, but this app is not holding HDR on; nothing to move.");
            return;
        }

        if (_hdrOn is not { Length: > 0 } was)
        {
            Log.Warn("Auto HDR: holding HDR on but no display recorded; cannot move it.");
            return;
        }

        // Switching HDR makes Windows re-apply its topology, which arrives here as another display
        // change. Without this the move would answer its own echo and could ping-pong.
        if (DateTime.UtcNow - _followedAtUtc < TimeSpan.FromSeconds(4))
        {
            Log.Info("Auto HDR: display changed, but that was this app's own HDR change; ignoring it.");
            return;
        }

        if (GameParked?.Invoke() == true)
        {
            Log.Info("Auto HDR: primary moved, but a game is parked on the couch display; "
                   + "leaving HDR where the game is.");
            return;
        }

        var now = Display.DisplayManager.CurrentPrimaryDevicePath();

        if (now is not { Length: > 0 })
        {
            Log.Warn("Auto HDR: could not identify the new primary display; leaving HDR alone.");
            return;
        }

        if (string.Equals(now, was, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("Auto HDR: display changed, but the primary is still the one holding HDR.");
            return;
        }

        try
        {
            _followedAtUtc = DateTime.UtcNow;

            HdrControl.SetHdrFor(was, false);

            // Recorded either way. If the new primary cannot do HDR then nothing is holding it on any
            // more, and still claiming the old display would switch off a screen this app has left.
            _hdrOn = HdrControl.SetHdrFor(now, true) ? now : null;
            _weTurnedItOn = _hdrOn is not null;

            Log.Info(_hdrOn is null
                         ? "Auto HDR: the new primary cannot do HDR, so HDR is no longer held."
                         : "Auto HDR followed the primary display: moved.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Auto HDR could not follow the primary display: {ex.Message}");
        }
    }

    /// <summary>A short toast, only when the user asked for notifications.</summary>
    private void Notify(string title, string detail, Color accent)
    {
        if (!_config.ShowNotifications || !_config.ShowHdrNotifications) return;

        // No settle delay: nothing is being switched between displays here, so the message can
        // appear straight away rather than after the pause a display change needs.
        Toast.Show(title, detail, accent,
                   delay: TimeSpan.FromMilliseconds(250),
                   duration: TimeSpan.FromSeconds(7));
    }

    /// <summary>Put HDR back if we are still holding it on — used when the app exits.</summary>
    public void RestoreNow()
    {
        if (!_weTurnedItOn) return;

        var where = _hdrOn;
        _weTurnedItOn = false;
        _hdrOn = null;

        try { Off(where); }
        catch (Exception ex) { Log.Warn($"Auto HDR could not be turned off on exit: {ex.Message}"); }
    }

    public void Dispose()
    {
        Disarm();
        RestoreNow();
        _watcher.Dispose();
    }
}

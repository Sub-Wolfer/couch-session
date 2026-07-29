using CouchMode.Audio;
using CouchMode.Display;
using CouchMode.Games;
using CouchMode.Steam;
using CouchMode.WindowLayout;
using CouchMode.Ui;

namespace CouchMode.Session;

public enum TvState { Desktop, Switching, Tv }

/// <summary>
/// The switch reported success but the TV isn't actually usable. Carries a message written
/// for the person reading it, not for a log file.
/// </summary>
public sealed class TvNotLiveException(string message) : Exception(message);

/// <summary>
/// Owns the TV-mode state machine. Every entry point — tray menu, auto-detect, settings —
/// goes through here, so there is exactly one place that knows how to enter and leave TV mode
/// and exactly one set of saved state to restore.
/// </summary>
public sealed class SessionController
{
    private readonly object _gate = new();

    private DisplaySnapshot? _displaySnapshot;

    /// <summary>
    /// Put a still-running game back onto the desk display, at the desk display's size.
    ///
    /// The mirror of MoveOntoCouchDisplay. Going out, a game opened at the desk keeps a window
    /// sized for the monitor and has to be resized for the television; coming home it has a window
    /// sized for the television and the same is true in reverse. Without this a game left running
    /// comes back as a 1920x1080 window on a 3440 monitor.
    ///
    /// This cannot make a game re-render at the new resolution — nothing in Windows can ask for
    /// that, and a game reads the display when it builds its renderer. What it can do is give the
    /// game a window that matches the display, which is the whole story for a borderless game and
    /// the best available for an exclusive-fullscreen one.
    ///
    /// After the topology has settled, for the reason everything else here waits: the desk display
    /// is not back at its own resolution until the restore has finished, so its bounds read wrong
    /// if they are asked for too early.
    /// </summary>
    private void ResizeGameForDesktopDisplay()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000).ConfigureAwait(false);

                var game = RunningGameWindow?.Invoke() ?? IntPtr.Zero;
                if (game == IntPtr.Zero) return;

                var primary = DisplayManager.CurrentPrimaryDevicePath();
                if (primary is not { Length: > 0 }) return;

                var bounds = DisplayManager.BoundsOf(primary);
                if (bounds is not { } desk) return;

                // Left alone if it already fits. A game that handled the change itself does not
                // need helping, and resizing it would be a second disruption for nothing.
                if (GetWindowRect(game, out var now)
                 && now.Right - now.Left == desk.Width && now.Bottom - now.Top == desk.Height)
                {
                    return;
                }

                SetWindowPos(game, IntPtr.Zero, desk.Left, desk.Top, desk.Width, desk.Height,
                             SWP_NOZORDER | SWP_NOACTIVATE);

                Log.Info($"Resized {BigPictureLauncher.Describe(game)} for the desk display: "
                       + $"{desk.Width}x{desk.Height}.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not resize the game for the desk display: {ex.Message}");
            }
        });
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WinRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WinRect rect);

    /// <summary>
    /// Whether the couch-audio-is-already-in-use warning has been shown this run.
    ///
    /// Deliberately not saved with the settings. It is a reminder rather than a preference, and one
    /// that should come back if the app is restarted — a fresh launch is the moment somebody is most
    /// likely to be looking at the machine and able to act on it.
    /// </summary>
    private bool _warnedAudioSameDevice;
    private string? _previousAudioId;
    private bool _layoutCaptured;
    private Rectangle? _tvBounds;

    /// <summary>Power, sleep and notification settings held for the session. See PerformanceTuning.</summary>
    private readonly PerformanceTuning _tuning = new();
    private readonly ResourceControl _apps = new();

    public AppConfig Config { get; set; }
    public TvState State { get; private set; } = TvState.Desktop;

    public event Action? StateChanged;

    /// <summary>Run once as the session begins tearing down, before displays move — the hook for the
    /// optional "close the game when the session ends".</summary>
    public Action? BeforeTeardown { get; set; }

    /// <summary>Run as a session begins, while the desktop's displays are all still attached.</summary>
    public Action? BeforeDisplayChange { get; set; }

    /// <summary>The window of the game currently running, or zero when none is. Set by the tray app so
    /// a session started (or resumed) while a game is already up goes straight back to the game rather
    /// than reloading Big Picture over the top of it.</summary>
    public Func<IntPtr>? RunningGameWindow { get; set; }

    public SessionController(AppConfig config) => Config = config;

    public bool IsInTvMode => State == TvState.Tv;

    /// <summary>
    /// End the session but leave the game running: minimise the game and Big Picture so the desktop is
    /// clean, then bring the desk display, audio and window layout back exactly as a normal session end
    /// does — only without closing the game. Nothing is suspended (that was scrapped, to avoid the
    /// anti-cheat risk); the game simply keeps running in the background, and starting a session again
    /// drops straight back into it.
    /// </summary>
    public void MinimizeToDesktop(bool closeBigPicture = true)
    {
        lock (_gate) { if (State != TvState.Tv) return; }

        try
        {
            // Logged either way. This was added once and lost in a merge, and its absence is why
            // the next round of testing could not tell a missing window from a refused minimize.
            var game = RunningGameWindow?.Invoke() ?? IntPtr.Zero;

            if (game == IntPtr.Zero)
            {
                Log.Warn("Swapping to the desktop: no game window was found, so nothing was minimized.");
            }
            else
            {
                bool went = ShowWindow(game, SW_FORCEMINIMIZE);
                Log.Info($"Swapping to the desktop: asked the game window to minimize — {(went ? "it did" : "it refused")}.");
            }

            var bp = BigPictureLauncher.FindWindow();
            if (bp != IntPtr.Zero) ShowWindow(bp, SW_FORCEMINIMIZE);
        }
        catch (Exception ex) { Log.Warn($"Minimising the game and Big Picture failed: {ex.Message}"); }

        // And again once the displays have finished moving.
        //
        // [BUG] Minimizing here worked and did not stick. The log proved it: "asked the game window
        // to minimize — it did", and the game was still on screen afterwards. What undoes it is the
        // topology restore below — a fullscreen game watching for display changes restores itself when
        // one arrives, which is a beat after this runs.
        //
        // So it is asked again once the desktop has settled, and only if the game really did come
        // back, so a game that stayed down is never poked twice.
        ReMinimizeAfterDisplaysSettle();
        ResizeGameForDesktopDisplay();

        // A normal return to the desktop, but skip the teardown hook that would close the game — the
        // whole point of this path is to keep it running. closeBigPicture is passed through so the
        // Big-Picture-only minimize can leave Big Picture alive (minimized) to drop back into.
        //
        // The couch display stays connected too, always, on both variants of this path: a game or Big
        // Picture is still sitting on that screen, and disconnecting a display out from under a
        // fullscreen window is what made games come back at the wrong resolution.
        ReturnToDesktop(runTeardown: false, closeBigPicture: closeBigPicture, keepCouchDisplay: true);
    }

    /// <summary>
    /// The other half of <see cref="MinimizeToDesktop"/>: put the session's windows back on the TV and
    /// in front of everything.
    ///
    /// Called once the display and audio switch has finished, so this only has to deal with windows.
    /// The game gets the foreground when there is one — it is what the user went back for — and Big
    /// Picture only when there is not, since handing focus to the shell over a running game would put
    /// the player back on a menu they did not ask for.
    /// </summary>
    public void ResumeMinimized()
    {
        try
        {
            var bp = BigPictureLauncher.FindWindow();
            var game = RunningGameWindow?.Invoke() ?? IntPtr.Zero;

            if (game != IntPtr.Zero)
            {
                BringToForeground(game);
            }
            else
            {
                // Only when there is no game. The doc above says Big Picture is not given the
                // foreground over a running game, and that was true of the focus call — but the
                // restore above it was unconditional, so a fullscreen shell was un-minimized on top
                // of the game and then had to be fought back down. Left minimized it is exactly where
                // it should be: behind the thing the user came back for, ready for when the game
                // closes.
                if (bp != IntPtr.Zero && IsIconic(bp)) ShowWindow(bp, SW_RESTORE);
                if (bp != IntPtr.Zero) BigPictureLauncher.BringToFront();
            }
        }
        catch (Exception ex) { Log.Warn($"Could not bring the session's windows back: {ex.Message}"); }
    }

    /// <summary>
    /// Put a window that was opened at the desk onto the couch display, at that display's size.
    ///
    /// Only for a game that was already running when the session began. Anything launched afterwards
    /// opens on the television by itself, because by then it is the primary display.
    ///
    /// Restored first if it is minimized: a minimized window ignores a move, and the game this is for
    /// has often just been minimized by the display switch.
    /// </summary>
    private void MoveOntoCouchDisplay(IntPtr window)
    {
        if (window == IntPtr.Zero) return;

        var bounds = _tvBounds ?? DisplayManager.BoundsOf(Config.TvDisplayPath);

        if (bounds is not { } couch)
        {
            Log.Warn("Could not find the couch display's bounds to move the game onto.");
            return;
        }

        try
        {
            if (IsIconic(window)) ShowWindow(window, SW_RESTORE);

            SetWindowPos(window, IntPtr.Zero, couch.Left, couch.Top, couch.Width, couch.Height,
                         SWP_NOZORDER | SWP_NOACTIVATE);

            Log.Info($"Asked {BigPictureLauncher.Describe(window)} onto the couch display: "
                   + $"{couch.Width}x{couch.Height} at {couch.Left},{couch.Top}.");

            // [BUG] It said "Moved" and never checked. A fullscreen game does not have to accept a
            // move: GTA V was a 5760x2520 scaled fullscreen window, the call returned, the log claimed
            // success, and the game stayed on the desk monitor. The same shape of fault as the silent
            // HDR return and the minimize that did not stick — a call with no verification is a call
            // that cannot be told from a failure.
            HoldOntoCouchDisplay(window, couch);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not move the running game onto the couch display: {ex.Message}");
        }
    }

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy,
                                            uint flags);

    /// <summary>
    /// Keep asking the game to minimize until it stays down, or until the teardown is well over.
    ///
    /// [BUG] One retry at 2.5 seconds was not enough. The log showed both attempts reporting
    /// success and the game still on screen: an exclusive-fullscreen game re-acquires its display
    /// every time the topology settles, and the teardown settles more than once — the restore, the
    /// position pass, and the window layout each move things. So it went down, came back, went down
    /// again, and came back after the last attempt had already run.
    ///
    /// Polled instead of timed, and it stops the moment it has stayed down twice in a row, so a
    /// game that minimizes on the first ask costs two cheap checks.
    /// </summary>
    private void ReMinimizeAfterDisplaysSettle()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                int stayedDown = 0;
                int asks = 0;

                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(700).ConfigureAwait(false);

                    var game = RunningGameWindow?.Invoke() ?? IntPtr.Zero;
                    if (game == IntPtr.Zero) return;

                    if (IsIconic(game))
                    {
                        // Twice, not once. A game that is about to bounce back is minimized at the
                        // moment it is checked, so one look is not evidence of anything.
                        if (++stayedDown >= 2)
                        {
                            if (asks > 0) Log.Info($"The game stayed minimized after {asks} more ask(s).");
                            return;
                        }

                        continue;
                    }

                    stayedDown = 0;
                    asks++;
                    ShowWindow(game, SW_FORCEMINIMIZE);
                }

                Log.Warn($"The game kept putting itself back after {asks} attempts; leaving it up. "
                       + "An exclusive-fullscreen game can refuse to stay minimized.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not re-minimize the game after the displays moved: {ex.Message}");
            }
        });
    }

    private const int SW_RESTORE = 9;

    /// <summary>
    /// Write down which displays are attached right now, and whether the couch one is among them.
    ///
    /// Purely diagnostic. Teardown moves displays in several steps and the log only ever recorded that
    /// each step happened, never what it did — so when a screen went away there was no way to tell
    /// which step took it, and the answer got guessed at instead of read. Each of these lines is one
    /// step's before-and-after.
    /// </summary>
    private void ReportDisplays(string moment)
    {
        try
        {
            var attached = DisplayManager.AttachedBounds();
            string couch = Config.TvDisplayPath is { Length: > 0 } path
                         ? (attached.ContainsKey(path) ? "attached" : "GONE")
                         : "not configured";

            Log.Info($"Displays {moment}: {attached.Count} attached, couch display {couch}.");
        }
        catch (Exception ex) { Log.Warn($"Could not read the display list {moment}: {ex.Message}"); }
    }

    /// <summary>Switch to the TV. No-op if already there or mid-transition.</summary>
    /// <summary>
    /// Apply the performance settings around a running game rather than around a session.
    ///
    /// Desk-only mode has no sessions, so the two settings that are switched on at the start of one
    /// and put back at the end would simply never fire. The game is the only event left worth hanging
    /// them on, and it is the better one anyway: a power plan raised for the whole time somebody is
    /// sat at their desk is a power plan raised for reading email.
    ///
    /// The tuner itself is unchanged and still refuses to apply or restore twice, so a game that
    /// reports its start more than once costs nothing.
    /// </summary>
    public void TuneAroundGame(bool running)
    {
        // Sessions do this themselves, and doing it from both would mean a game closing mid-session
        // put the power plan back while the session was still going.
        if (!Config.DeskOnly) return;

        if (running)
        {
            _tuning.Apply(Config);

            // Resource control follows the same rule here as the power plan does. At a desk there is
            // no session to hang it on, and the moment worth freeing memory for is a game starting.
            try { _apps.CloseForSession(Config); }
            catch (Exception ex) { Log.Warn($"Resource control failed: {ex.Message}"); }
        }
        else
        {
            _tuning.Restore();

            // Off the calling thread for the same reason the session path does it: this runs from the
            // game watcher, and starting several applications on that thread would stall the poll
            // that notices the next game.
            _ = Task.Run(() =>
            {
                try { _apps.ReopenAfterSession(Config); }
                catch (Exception ex) { Log.Warn($"Could not restart the closed apps: {ex.Message}"); }
            });
        }
    }

    /// <summary>
    /// Put the session's display arrangement back after something outside this app disturbed it.
    ///
    /// Switching HDR provokes Windows into re-applying its own saved topology: on a machine where a
    /// session had detached the desk monitor, turning HDR on for a game brought that monitor back,
    /// handed it primary, and the game opened there instead of on the television. Reproduced with a
    /// 3440 desk display and a 1080 couch display, and it does not happen with HDR switched off.
    ///
    /// This does not stop Windows doing it. It puts the arrangement back immediately afterwards,
    /// which is the same thing the teardown already does in reverse and the only lever this app has.
    /// </summary>
    public void ReassertCouchDisplay()
    {
        if (State != TvState.Tv || Config.TvDisplayPath is not { Length: > 0 } couch) return;

        try
        {
            if (Config.DisplayMode == TvDisplayMode.TvOnly)
            {
                var attached = DisplayManager.AttachedBounds();

                // Only when something really did come back. Detaching is expensive and visible, so it
                // must never run as a matter of routine.
                foreach (var path in attached.Keys.ToArray())
                {
                    if (string.Equals(path, couch, StringComparison.OrdinalIgnoreCase)) continue;

                    Log.Warn("A display came back during the session; detaching it again.");
                    try { DisplayManager.Detach(path); }
                    catch (Exception ex) { Log.Warn($"Could not detach it: {ex.Message}"); }
                }
            }

            DisplayManager.EnsurePrimary(couch);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not re-assert the couch display: {ex.Message}");
        }
    }

    public void EnterTvMode()
    {
        // The one gate that cannot be got around.
        //
        // Desk-only mode is switched off in the interface as well — the pages go, the button goes,
        // the triggers stop asking — but a mode that depends on every caller remembering to check it
        // is a mode with a hole in it, and there are seven ways into this method. Steam opening Big
        // Picture by itself is one of them, and it does not come through anything this app controls.
        //
        // So the refusal lives at the door rather than at each of the corridors leading to it.
        if (Config.DeskOnly)
        {
            Log.Info("Desk-only mode is on, so the request to switch to the TV was ignored.");
            return;
        }

        lock (_gate)
        {
            if (State != TvState.Desktop) return;
            State = TvState.Switching;
        }
        RaiseStateChanged();

        try
        {
            Log.Session("=== entering TV mode ===");

            // Steam is remembered separately from the general layout — see SteamWindowMemory.
            SteamWindowMemory.Remember(Config);

            LayoutStore.Capture();
            _layoutCaptured = true;

            _displaySnapshot = DisplayManager.Capture();
            Log.Info($"Captured display topology ({_displaySnapshot.ActivePathCount} active).");

            // And to disk, in case this process never gets to use the copy in memory. Armed before
            // anything is detached rather than after, because the window between taking a monitor
            // away and recording how to give it back is exactly the window that costs one.
            DisplayManager.ArmRecovery(_displaySnapshot);

            // What the snapshot contains decides what the restore puts back at the end. If the couch
            // display is missing from it, the restore will remove that display no matter what the
            // detach step is told to do — so this is worth knowing at the start, not deduced at the end.
            ReportDisplays("as the session started");

            // Take HDR off whatever it is on before anything moves.
            //
            // [BUG] Auto HDR follows the primary display, and it did that from the display-change
            // event — which arrives *after* the switch. In TV-only mode the desk monitor has been
            // detached by then, and HDR cannot be changed on a display that is not attached: the call
            // logged "that display is not attached; leaving it alone" and the monitor kept HDR, so
            // both screens ended up in it. Doing it here is the only moment the old display is still
            // there to be switched. The follow then only has to turn the new one on.
            BeforeDisplayChange?.Invoke();

            // Recorded before anything moves, and merged with what is already known so a display
            // that happens to be switched off right now keeps the position it had last time.
            DisplayManager.RememberPositions(Config);

            WarnIfCouchDisplayIsTheDeskMonitor();

            if (Config.SwitchAudio)
                _previousAudioId = CaptureDesktopAudio();

            if (Config.DisplayMode == TvDisplayMode.TvOnly)
            {
                DisplayManager.ActivateOnly(Config.TvDisplayPath);
                DisplayManager.WaitFor(
                    () => DisplayManager.ActiveDisplayCount() == 1,
                    TimeSpan.FromMilliseconds(Config.DisplaySettleTimeoutMs),
                    TimeSpan.FromMilliseconds(600));

                // Checked, because "switched to the TV" and "switched off everything else" turned out
                // not to be the same thing.
                //
                // Resuming a session the user only minimized away starts from a desktop that already
                // has the couch display attached — it is deliberately left connected so a parked game
                // does not lose its screen. From that starting point the preferred switch is refused by
                // Windows and the fallback takes over, and the log shows it: a fresh session reports
                // "CCD (full path array)" while a resume reports "CCD (no mode array)". The fallback
                // brings the television up without reliably putting the other displays down, so the
                // desk monitors stayed on through a session that asked for the TV alone.
                //
                // Rather than depend on which variant Windows accepts, the outcome is confirmed and the
                // stragglers are switched off by name.
                if (DisplayManager.ActiveDisplayCount() > 1)
                {
                    Log.Info("The TV came up but other displays are still on; switching them off.");

                    foreach (var display in DisplayManager.ListDisplays())
                    {
                        if (!display.IsActive || display.DevicePath == Config.TvDisplayPath) continue;

                        try { DisplayManager.Detach(display.DevicePath); }
                        catch (Exception ex)
                        {
                            Log.Warn($"Could not switch off {display.FriendlyName}: {ex.Message}");
                        }
                    }

                    DisplayManager.WaitFor(
                        () => DisplayManager.ActiveDisplayCount() == 1,
                        TimeSpan.FromMilliseconds(Config.DisplaySettleTimeoutMs),
                        TimeSpan.FromMilliseconds(600));
                }
            }
            else
            {
                DisplayManager.MakePrimaryKeepOthers(Config.TvDisplayPath);
                Thread.Sleep(1200);   // no topology change to poll for; just let it settle
            }

            // Cache where the TV actually landed. Big Picture is placed against these bounds
            // rather than "the primary screen", which is wrong in keep-others mode.
            _tvBounds = DisplayManager.BoundsOf(Config.TvDisplayPath);

            // Failsafe: prove the TV really came up before handing the session over. Without
            // this, a TV that's off, on the wrong input, or that failed to attach leaves you
            // staring at a blank screen with no obvious way back.
            VerifyTvIsLive();

            // Audio switching is kept off the critical path. It can wait a second or two for a
            // just-woken TV's HDMI audio endpoint to appear (see SwitchAudioTo), and there is no
            // reason Big Picture and the rest of the switch should sit behind that wait.
            if (Config.RestartSteamForAudio && Config.SwitchAudio &&
                System.Diagnostics.Process.GetProcessesByName("steam").Length > 0)
            {
                // Here it has to be synchronous and first: Steam is about to be restarted, and the
                // restart is what binds the new default device. Only reached when Steam is already up
                // — a cold start binds the device on its own.
                SwitchAudioTo(Config.TvAudioDeviceId, "TV");
                BigPictureLauncher.RestartIntoBigPictureAsync(TimeSpan.FromSeconds(45))
                                  .GetAwaiter().GetResult();
            }
            else if (Config.SwitchAudio)
            {
                // No restart, so nothing depends on the order — move the audio in the background and
                // let the session get on with opening Big Picture.
                System.Threading.Tasks.Task.Run(() => SwitchAudioTo(Config.TvAudioDeviceId, "TV"));
            }

            // Before closing anything, so the machine is already in its performance state while
            // the closing work happens.
            _tuning.Apply(Config);


            // The TV is on and primary by now, so its mode can be set.
            ApplyTvVideoMode();

            // Big Picture is the couch interface, so a session always has it open.
            //
            // Previously it was only launched down the "Start Couch Mode" path, which meant a
            // session begun any other way — a controller switching on, most obviously — moved
            // the display and audio to the TV and then left the user looking at their desktop
            // from across the room with no way to drive it.
            try
            {
                var game = RunningGameWindow?.Invoke() ?? IntPtr.Zero;

                if (game != IntPtr.Zero)
                {
                    // A game is already running — do not reload Big Picture over the top of it; put the
                    // player straight back into the game.
                    //
                    // [BUG] It was only brought to the front, never moved. A game launched at the desk
                    // keeps the window it opened with, so starting a session around it left the game on
                    // the monitor — and in TV-only mode that monitor is then detached out from under it.
                    // Windows puts an orphaned window somewhere of its own choosing, at whatever size it
                    // had, which is a 3440-wide window on a 1080 television.
                    // Big Picture is deliberately NOT opened here.
                    //
                    // It was, for a while, so that something would be on the television when the game
                    // closed. It cannot be made to work. Steam puts Big Picture's window up several
                    // seconds after it is asked for, and it takes the foreground when it does — so the
                    // shell ends up owning the controller while sitting behind the game, on a screen
                    // nobody can see, and the game stops responding. Fronting the game first is too
                    // early, and fronting it repeatedly afterwards is a fight with Steam that Steam
                    // wins often enough to matter.
                    //
                    // It is also not needed. Steam's "Use the Big Picture Overlay when using a
                    // controller" setting, on by default, gives a game the controller-friendly Big
                    // Picture *overlay* when the Guide button is pressed — which is what the session
                    // prompt draws over — with no Big Picture window open at all. Opening one only
                    // added a shell that could take the foreground and the controller with it.
                    //
                    // It is opened when the player asks to stay on the television instead, which is
                    // the moment they actually need it. See the CloseToBigPicture prompt choice.
                    MoveOntoCouchDisplay(game);

                    // Last, so the game ends up in front of the shell just opened behind it.
                    BringToForeground(game);

                    Log.Info("A game is already running; returned straight to it without reopening Big Picture.");
                }
                else if (BigPictureLauncher.FindWindow() == IntPtr.Zero
                    && BigPictureLauncher.IsSteamInstalled())
                {
                    BigPictureLauncher.Launch();
                    Log.Info("Opened Steam Big Picture for the session.");
                }
            }
            catch (Exception ex) { Log.Warn($"Could not open Big Picture: {ex.Message}"); }

            // Once the couch interface is up, not before. Big Picture wants the machine's
            // attention while it starts, and asking six apps to close in the same second makes
            // both of them slower for no reason. Nothing downstream waits on this.
            try { _apps.CloseForSession(Config); }
            catch (Exception ex) { Log.Warn($"Resource control failed: {ex.Message}"); }

            lock (_gate) State = TvState.Tv;
            RaiseStateChanged();
            Log.Info("TV mode active.");

            if (Config.ShowNotifications && Config.ShowSessionNotifications)
            {
                Toast.Show(Ui.Words.NoticeSessionOn, Ui.Words.NoticeSessionOnDetail,
                           Ui.Theme.Good);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Entering TV mode failed", ex);
            lock (_gate) State = TvState.Tv;   // treat as entered so Revert definitely runs
            ReturnToDesktop();
            throw;
        }
    }

    /// <summary>
    /// Put the couch display back on if the user asked for it to stay, and the restore took it off.
    ///
    /// Restoring the topology means restoring it exactly, and exactly is the problem here. The
    /// television is usually a disabled output rather than an idle one, so it is not in the
    /// arrangement captured at the start of a session — the app attaches it itself a moment
    /// later. Putting the arrangement back therefore detaches it again, every time, whatever
    /// "Disconnect the Couch Mode display when session ends" is set to.
    ///
    /// That setting only ever governed an *extra* detach at the very end, so switching it off
    /// changed nothing anybody could see: the display went off regardless, from a different piece
    /// of code. Which is exactly how it was reported — turned the display back on, unticked the
    /// box, still disconnected.
    ///
    /// So when it is off, the display is reattached after the restore. Before the window layout
    /// goes back, deliberately: the layout has to settle against the final arrangement, not one
    /// that is about to change again.
    /// </summary>
    private void KeepCouchDisplayAttachedIfAsked()
    {
        if (Config.TurnOffTvOnExit) return;
        if (Config.TvDisplayPath is not { Length: > 0 } tv) return;

        try
        {
            if (DisplayManager.TryGetGdiName(tv) is not null) return;

            Log.Info("Putting the couch display back on: it was switched off by restoring the "
                   + "arrangement, and the setting says to leave it connected.");

            DisplayManager.AttachAdditional(tv);

            DisplayManager.WaitFor(() => DisplayManager.TryGetGdiName(tv) is not null,
                                   TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(600));

            // Attaching says nothing about where. Windows drops a newly attached display to the
            // right of everything else, so without this a monitor that lived on the left comes
            // back on the wrong side and stays there.
            DisplayManager.RestorePosition(Config, tv);

            // Reasserted afterwards: attaching a display can move primary, and everything about
            // where windows go is measured from it.
            if (_displaySnapshot is not null)
                DisplayManager.EnsurePrimary(_displaySnapshot.PrimaryDevicePath);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not keep the couch display connected: {ex.Message}");
        }
    }

    /// <summary>
    /// A display to make primary that is not the couch display — for the moment before the couch
    /// display is switched off, so primary never sits on a screen about to vanish.
    ///
    /// The captured primary is preferred, unless that is the couch display itself (which is exactly
    /// the case that used to scatter the desktop). Failing that, any other display still attached.
    /// Null means there is nothing else to hand it to, and Windows is left to cope.
    /// </summary>
    private string? PrimaryAwayFromCouchDisplay(string couchPath)
    {
        if (_displaySnapshot?.PrimaryDevicePath is { Length: > 0 } captured
            && !PathEquals(captured, couchPath)
            && DisplayManager.TryGetGdiName(captured) is not null)
            return captured;

        foreach (var d in DisplayManager.ListDisplays())
            if (d.IsActive && !PathEquals(d.DevicePath, couchPath))
                return d.DevicePath;

        return null;
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Say so in the log when the couch display is the screen already being worked at.
    ///
    /// It is not always wrong — a single-display machine can reasonably use couch mode for the
    /// resolution and audio switching alone — so this warns rather than refuses.
    ///
    /// But when there is more than one display and the chosen one is the desk monitor, it is
    /// almost certainly a mistake, and a costly one: every session then rearranges the screen
    /// being used rather than a television. It happens because the picker can only list displays
    /// Windows can currently see, so setting this up with the TV switched off offers exactly one
    /// candidate — the monitor — and picking it looks like the only option rather than the wrong
    /// one. Nothing afterwards ever says otherwise, and the symptom reads as "the app keeps
    /// turning my monitor off".
    /// </summary>
    private void WarnIfCouchDisplayIsTheDeskMonitor()
    {
        try
        {
            if (_displaySnapshot is null || _displaySnapshot.ActivePathCount < 2) return;

            var gdiName = DisplayManager.TryGetGdiName(Config.TvDisplayPath);
            if (gdiName is null || !LegacyDisplay.IsPrimary(gdiName)) return;

            Log.Warn($"The couch display ({Config.TvDisplayName}) is currently the main display. "
                   + "If that is your desk monitor rather than your television, this is set to the "
                   + "wrong screen — switch the TV on and choose it on the Display page.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not check which display is the main one: {ex.Message}");
        }
    }

    /// <summary>
    /// Confirm the switch actually worked, and throw if it didn't so the caller reverts.
    ///
    /// Windows reports success from the display APIs long before anything is visible, so the
    /// checks here are about the end state rather than the return codes: is the TV in the
    /// active set, does it have real bounds, and — in TV-only mode — is it the one screen left.
    /// A TV that's powered off or on the wrong input typically fails the bounds check, because
    /// it never attaches and never gets a desktop region.
    /// </summary>
    private void VerifyTvIsLive()
    {
        var problems = new List<string>();

        var gdiName = DisplayManager.TryGetGdiName(Config.TvDisplayPath);
        if (gdiName is null)
            problems.Add("the TV did not become an active display");

        if (_tvBounds is not { Width: > 0, Height: > 0 })
            problems.Add("the TV has no usable screen area");

        int active = DisplayManager.ActiveDisplayCount();

        if (Config.DisplayMode == TvDisplayMode.TvOnly && active != 1)
            problems.Add($"expected the TV to be the only screen, but {active} are still active");

        // Asked of the driver, not Screen.PrimaryScreen. This runs on the switching thread a
        // moment after the topology changed, and the cached screen list has not caught up yet —
        // so this check was reading the arrangement from before the switch it is verifying.
        // The null check is not redundant: a missing GDI name is already recorded as its own
        // problem above, and the method carries on to collect the rest rather than returning.
        if (Config.DisplayMode == TvDisplayMode.KeepOthers
            && gdiName is not null && !LegacyDisplay.IsPrimary(gdiName))
            problems.Add("the TV did not become the main display");

        if (problems.Count == 0)
        {
            Log.Info($"TV verified live: {gdiName} at {_tvBounds!.Value.Width}×{_tvBounds.Value.Height}.");
            return;
        }

        var detail = string.Join("; ", problems);
        Log.Error($"TV verification failed: {detail}");

        throw new TvNotLiveException(string.Format(Ui.Words.WarnTvNotLive, detail));
    }

    /// <summary>Undo everything and go back to the desktop. Safe to call repeatedly.</summary>
    /// <param name="runTeardown">Whether to run the teardown hook that may close the running game.
    /// False for the "minimize to desktop" path, which deliberately leaves the game running.</param>
    /// <param name="keepCouchDisplay">
    /// Leave the couch display connected even when the "disconnect on exit" setting is on.
    ///
    /// For the paths that deliberately leave something running on that screen. A fullscreen game whose
    /// display is taken away does not wait politely: it falls back to whatever monitor is left, and
    /// writes that resolution into its own settings — so dropping to the desktop with GTA parked came
    /// back with GTA at the desk monitor's resolution, needing to be set to 4K by hand every time. The
    /// display stays attached, primary moves to the desk monitor as usual, and the game never notices
    /// anything happened.
    /// </param>
    public void ReturnToDesktop(bool runTeardown = true, bool closeBigPicture = true,
                                bool keepCouchDisplay = false)
    {
        lock (_gate)
        {
            if (State == TvState.Desktop) return;
            State = TvState.Switching;
        }
        RaiseStateChanged();

        // Before anything moves: give the tray app its chance to close a running game if the user
        // asked for that, so it shuts down while still on the TV rather than mid-restore. Skipped for
        // the minimize-to-desktop path, which keeps the game running.
        if (runTeardown)
        {
            try { BeforeTeardown?.Invoke(); }
            catch (Exception ex) { Log.Warn($"Session teardown hook failed: {ex.Message}"); }
        }

        RestoreTvVideoMode();

        // Closed first, before the displays move.
        //
        // Big Picture is fullscreen on the TV; leaving it up while the TV is disconnected
        // strands a fullscreen window on a display that no longer exists, and Steam does not
        // always recover from that gracefully. Skipped for the minimize-to-desktop path that wants
        // Big Picture kept — it has already been minimized, so it is not stranded fullscreen.
        if (closeBigPicture)
        {
            try { BigPictureLauncher.Close(); }
            catch (Exception ex) { Log.Warn($"Could not close Big Picture: {ex.Message}"); }
        }

        // Whether the desktop arrangement genuinely came back, which decides at the end of this
        // method whether the recovery file on disk is still needed. Assumed true only because a
        // session with no snapshot to restore has nothing that can have gone wrong.
        bool displaysRestored = true;

        // Each step is independently guarded. Failing to restore audio must never prevent the
        // displays coming back — that's the one the user can't fix without a working screen.
        if (_displaySnapshot is not null)
        {
            try
            {
                // Recorded either side of the restore, because this is the step that has twice been
                // guessed at. Whether the couch display survives Restore, or is still there right up
                // until the detach, is the whole question — and nothing in the log answered it, which
                // is precisely why it was guessed. It is answered now.
                ReportDisplays("before the topology restore");

                DisplayManager.Restore(_displaySnapshot);

                // [BUG] The answer to this used to be thrown away and the line below logged
                // regardless, so the log read "Display topology restored." in the one case worth
                // knowing about — the case where they had not come back. A log that reports success
                // it did not check is worse than no line at all: it sends the next person reading it
                // somewhere else entirely.
                bool back = DisplayManager.WaitFor(
                    () => DisplayManager.ActiveDisplayCount() >= _displaySnapshot.ActivePathCount,
                    TimeSpan.FromMilliseconds(Config.DisplaySettleTimeoutMs),
                    TimeSpan.FromMilliseconds(600));

                if (back)
                {
                    Log.Info("Display topology restored.");
                }
                else
                {
                    displaysRestored = false;
                    Log.Warn($"The displays did not come back within "
                           + $"{Config.DisplaySettleTimeoutMs}ms: {DisplayManager.ActiveDisplayCount()} "
                           + $"active where {_displaySnapshot.ActivePathCount} were expected.");
                }

                ReportDisplays("after the topology restore");

                bool turnOff = !keepCouchDisplay
                            && Config.TurnOffTvOnExit
                            && Config.TvDisplayPath is { Length: > 0 };

                // Declining to detach is not enough, and this is now measured rather than assumed.
                //
                // The log of a parked-game run reads: "before the topology restore: 1 attached, couch
                // display attached" then "after the topology restore: 2 attached, couch display GONE",
                // with "will detach=False". So the restore above is what removes it — it reapplies the
                // arrangement captured at session start, and the couch display was not part of that
                // (it is attached by starting a session, not by the desktop). The detach step never
                // even ran. Putting it back afterwards is therefore the only place this can be fixed.
                if (keepCouchDisplay && Config.TvDisplayPath is { Length: > 0 } couch)
                {
                    Log.Info("Not detaching the couch display: something is still running on it.");

                    try
                    {
                        if (!DisplayManager.AttachedBounds().ContainsKey(couch))
                        {
                            Log.Info("The topology restore removed the couch display; extending back onto it.");
                            DisplayManager.AttachAdditional(couch);
                            DisplayManager.WaitFor(
                                () => DisplayManager.AttachedBounds().ContainsKey(couch),
                                TimeSpan.FromMilliseconds(Config.DisplaySettleTimeoutMs),
                                TimeSpan.FromMilliseconds(400));

                            ReportDisplays("after putting the couch display back");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Could not keep the couch display connected: {ex.Message}");
                    }
                }

                Log.Info($"Teardown decision: keepCouchDisplay={keepCouchDisplay}, "
                       + $"TurnOffTvOnExit={Config.TurnOffTvOnExit}, will detach={turnOff}.");

                if (turnOff)
                {
                    // The couch display is about to be switched off, and it may be the primary —
                    // which it is whenever it was already the main screen when the session began, as
                    // the snapshot then captures it as primary. Detaching whatever is primary is
                    // what scattered the desktop: the taskbar and every window on it get thrown onto
                    // some display Windows picks. So primary is handed to a display that will
                    // survive *first*, the couch display is switched off, and only then are windows
                    // placed — against an arrangement that is not about to change under them.
                    var keep = PrimaryAwayFromCouchDisplay(Config.TvDisplayPath);
                    if (keep is not null) DisplayManager.EnsurePrimary(keep);

                    try { DisplayManager.Detach(Config.TvDisplayPath); }
                    catch (Exception ex) { Log.Warn($"Could not switch the couch display off: {ex.Message}"); }

                    // Reasserted: detaching a display can move primary a second time.
                    if (keep is not null) DisplayManager.EnsurePrimary(keep);

                    DisplayManager.RestoreAllPositions(Config);
                    Thread.Sleep(1200);
                }
                else
                {
                    // Make sure the right monitor is primary again. The topology restore asks for
                    // this, but SDC_ALLOW_CHANGES lets Windows keep the current primary, and in
                    // extended mode that is the television. Everything downstream depends on getting
                    // this right: primary is where the taskbar lives, where new windows open, and
                    // what every saved window position is relative to.
                    DisplayManager.EnsurePrimary(_displaySnapshot.PrimaryDevicePath);
                    ReportDisplays("at the end of teardown");

                    KeepCouchDisplayAttachedIfAsked();

                    // Putting a topology back reproduces the captured paths, but anything not in that
                    // capture is placed by Windows — and placing one display can shove the others
                    // along. This puts each one back where it is remembered.
                    DisplayManager.RestoreAllPositions(Config);

                    // Let the desktop settle before anything places a window on it. Windows reflows
                    // windows itself for a second after a topology change, and a layout restored
                    // into the middle of that gets undone by it.
                    Thread.Sleep(1200);
                }
            }
            catch (Exception ex)
            {
                displaysRestored = false;
                Log.Error("Display restore failed", ex);
            }
        }

        // Audio goes to the background; windows do not.
        //
        // Restoring audio now waits for the endpoint to reappear and then holds it for a few
        // seconds against Windows changing its mind — up to about fourteen seconds in the worst
        // case. Doing that on this thread meant the user sat looking at unrestored windows for
        // the whole of it. Nothing about putting windows back depends on the audio device, so
        // the two need not queue behind each other.
        if (Config.SwitchAudio && _previousAudioId is { Length: > 0 } audio)
        {
            _previousAudioId = null;

            _ = Task.Run(() =>
            {
                try { RestoreDesktopAudio(audio); }
                catch (Exception ex) { Log.Error("Audio restore failed", ex); }
            });
        }

        if (_layoutCaptured)
        {
            try
            {
                // Steam is not in here — its window is destroyed and rebuilt by Big Picture, so
                // it is remembered separately by SteamWindowMemory and restored below.
                LayoutStore.Restore();

                // Then held there. Windows goes on shuffling windows for several seconds after a
                // topology change, so the first pass is a start rather than the finish.
                _ = LayoutStore.SettleAsync(TimeSpan.FromSeconds(12))
                               .ContinueWith(t => Log.Error("Settling the window layout failed", t.Exception!),
                                             TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex) { Log.Error("Window layout restore failed", ex); }
            _layoutCaptured = false;
        }

        // Switching the couch display off is no longer done here: when it is asked for it happens
        // inside the display-restore block above, before windows are placed, because a display that
        // is turned off after the fact takes primary and its windows with it.

        _displaySnapshot = null;

        // The displays are back, so the note saying how to put them back has nothing left to say.
        // Anything still on disk after this point means a run that did not reach here.
        //
        // [BUG] It used to be cleared unconditionally. The restore above is wrapped in a catch that
        // logs and carries on, so a restore that threw — or one that quietly never landed — arrived
        // here and deleted the file describing how to undo it. The safety net was thrown away in
        // exactly the situation it exists for, and the next launch had nothing to recover from: it
        // saw a reduced desktop, no recovery file, and treated that arrangement as normal.
        //
        // Left in place instead, and RecoverFromCrash picks it up on the next start. It is harmless
        // if the user puts the displays back by hand in the meantime — that check compares what is
        // active now against what was saved and does nothing when the desktop is already whole.
        if (displaysRestored)
        {
            DisplayManager.DisarmRecovery();
        }
        else
        {
            Log.Warn("The displays were not confirmed back, so the recovery file is being kept. "
                   + "The next launch will put them back if the desktop is still short of one.");
        }

        _ = SteamWindowMemory
                // Generous, because it may have to sit through a Big Picture launch that is
                // still arriving, watch Steam destroy the window, and set up the replacement.
                // It runs in the background and returns as soon as a pass sticks, so a long
                // ceiling costs nothing in the ordinary case.
                .RestoreAndApplyAsync(Config.SteamAfterBigPicture, TimeSpan.FromSeconds(45))
                .ContinueWith(t => Log.Error("Restoring the Steam window failed", t.Exception!),
                              TaskContinuationOptions.OnlyOnFaulted);

        _tvBounds = null;

        lock (_gate) State = TvState.Desktop;
        RaiseStateChanged();
        Log.Session("=== back on desktop ===");

        if (Config.ShowNotifications && Config.ShowSessionNotifications)
            Toast.Show(Ui.Words.NoticeSessionOff,
                       Ui.Words.NoticeSessionOffDetail,
                       Ui.Theme.Accent);

        _tuning.Restore();

        // Last, and off this thread. Starting several apps takes seconds and none of the
        // desktop's restoration depends on them; doing it here would leave the user watching an
        // empty desktop while Chrome works out where its profile is.
        _ = Task.Run(() =>
        {
            try { _apps.ReopenAfterSession(Config); }
            catch (Exception ex) { Log.Warn($"Could not restart the closed apps: {ex.Message}"); }
        });
    }

    /// <summary>What the TV was running at before the session changed it.</summary>
    private (string Gdi, DisplayMode Mode)? _previousVideoMode;

    /// <summary>
    /// Put the TV into the chosen resolution and refresh rate.
    ///
    /// Done after the switch rather than before: the display has to be active and primary for
    /// its mode list to mean anything, and setting a mode on a display Windows is about to
    /// reconfigure achieves nothing.
    /// </summary>
    private void ApplyTvVideoMode()
    {
        if (Config.TvVideoMode.Length == 0) return;

        try
        {
            var wanted = DisplayMode.FromKey(Config.TvVideoMode);
            if (wanted is null) return;

            // DisplayManager, not LegacyDisplay: the legacy lookup can return a phantom match on
            // a different adapter, and this call *changes a display's resolution*. Getting the
            // wrong one here does not show a wrong list — it reconfigures the wrong monitor.
            var gdi = DisplayManager.TryGetGdiName(Config.TvDisplayPath);
            if (gdi is null) { Log.Warn("Could not find the TV to set its refresh rate."); return; }

            var before = DisplayModes.Current(gdi);
            if (before is null) return;

            // Skip the mode-set entirely when the TV is already showing the chosen mode — which is
            // exactly the case where the user picked what Windows was going to use anyway. A mode
            // change is what blanks the screen while the panel re-syncs, so not making one when
            // nothing needs to change is the difference between a black flash and none.
            //
            // Compared with a Hz of tolerance: Windows rounds a 59.94 Hz panel to 59 or 60 from one
            // read to the next, and an exact test would treat that jitter as a real difference and
            // re-apply a mode identical in every way that matters — blanking the screen for nothing.
            if (SameMode(before, wanted))
            {
                Log.Info($"TV is already at {wanted}; leaving the mode alone.");
                return;
            }

            if (DisplayModes.Apply(gdi, wanted)) _previousVideoMode = (gdi, before);
        }
        catch (Exception ex) { Log.Error("Setting the TV refresh rate failed", ex); }
    }

    /// <summary>
    /// Whether two modes are the same picture: identical resolution, and a refresh rate within a
    /// Hz to absorb Windows' rounding of fractional rates.
    /// </summary>
    private static bool SameMode(DisplayMode a, DisplayMode b)
        => a.Width == b.Width && a.Height == b.Height && Math.Abs(a.Refresh - b.Refresh) <= 1;

    /// <summary>Put the TV back to whatever it was on, if this changed it.</summary>
    private void RestoreTvVideoMode()
    {
        if (_previousVideoMode is not { } previous) return;
        _previousVideoMode = null;

        try { DisplayModes.Apply(previous.Gdi, previous.Mode); }
        catch (Exception ex) { Log.Warn($"Could not restore the TV mode: {ex.Message}"); }
    }

    /// <summary>Launch Big Picture. The watcher handles the display switch and window placement.</summary>
    public void LaunchBigPicture()
    {
        if (!BigPictureLauncher.IsSteamInstalled())
            throw new InvalidOperationException("Steam doesn't appear to be installed.");

        BigPictureLauncher.Launch();
        Log.Info("Requested Steam Big Picture.");
    }

    /// <summary>
    /// Check the game actually landed on the couch display, and ask again if it did not.
    ///
    /// A game rendering fullscreen owns its window and can put it straight back, exactly as one can
    /// refuse to stay minimized. Bounded and self-terminating: it stops as soon as the window is where
    /// it was asked to be twice in a row, so a game that moves cleanly costs two cheap checks.
    ///
    /// Position only, deliberately. A game may legitimately render at a different size to the display
    /// it is on, and insisting on an exact match would fight something that is not wrong.
    /// </summary>
    private void HoldOntoCouchDisplay(IntPtr window, Rectangle couch)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
                int settled = 0, asks = 0;

                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(700).ConfigureAwait(false);

                    if (!GetWindowRect(window, out var now)) return;

                    // On the right screen is the test, not at the exact pixel: a game may centre
                    // itself or letterbox, and either is fine as long as it is on the television.
                    bool onCouch = now.Left >= couch.Left - 8 && now.Left < couch.Right
                                && now.Top >= couch.Top - 8 && now.Top < couch.Bottom;

                    if (onCouch)
                    {
                        if (++settled >= 2)
                        {
                            if (asks > 0) Log.Info($"The game settled on the couch display after {asks} more ask(s).");
                            return;
                        }

                        continue;
                    }

                    settled = 0;
                    asks++;

                    SetWindowPos(window, IntPtr.Zero, couch.Left, couch.Top, couch.Width, couch.Height,
                                 SWP_NOZORDER | SWP_NOACTIVATE);
                }

                Log.Warn($"The game would not stay on the couch display after {asks} attempts. "
                       + "A game rendering fullscreen can refuse to be moved; quitting it and starting "
                       + "it again inside the session is the reliable way round.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not hold the game on the couch display: {ex.Message}");
            }
        });
    }

    /// <summary>Called by the watcher once Big Picture is up and the TV is active.</summary>
    public void PlaceBigPicture(IntPtr hwnd)
    {
        try
        {
            var bounds = _tvBounds
                         ?? DisplayManager.BoundsOf(Config.TvDisplayPath)
                         ?? Screen.PrimaryScreen?.Bounds
                         ?? throw new InvalidOperationException("No display to place Big Picture on.");

            // Big Picture always comes up in front and navigable, even with a game left running from
            // a previous session. Getting back into that game is Big Picture's own job now — the user
            // picks it from the library and Steam brings it forward — so all this has to do is not
            // stay pinned on top of it. The front guard handles that: it releases the pin the moment
            // the game is actually the window in front.
            BigPictureLauncher.PlaceOn(hwnd, bounds, bringToFront: true);

            // Not optional: Steam re-lays-out its window from state sized for the display it
            // opened on, so without this it ends up letterboxed or overflowing whenever the
            // TV differs from the desktop. There is no sensible reason to want that.
            BigPictureLauncher.EnsureFills(hwnd, bounds, TimeSpan.FromSeconds(8));

            // On top from the first frame, so the Steam client window or a stray dialog cannot sit
            // in front of it on the TV. The front guard maintains this and releases it once a game
            // or launcher comes forward.
            if (Config.BigPictureAlwaysOnTop)
                BigPictureLauncher.SetAlwaysOnTop(hwnd, true);

            // Last, so it is not undone by the window moving underneath it.
            BigPictureLauncher.ParkCursor(bounds);
        }
        catch (Exception ex)
        {
            Log.Error("Placing Big Picture failed", ex);
        }
    }

    /// <summary>
    /// Work out which device to come back to.
    ///
    /// Refuses to record the couch device as the desktop one. Two different things put the machine
    /// in that state and this cannot tell them apart from here:
    ///
    /// A previous session failed to restore desktop audio — the endpoint was briefly missing — and
    /// left the default on the couch device. Capturing it now would make that permanent, which is
    /// the failure this guard was originally written for.
    ///
    /// Or the couch device and the everyday device are simply the same choice, which is a settings
    /// mistake rather than a fault. It is the likelier of the two and the user can fix it in
    /// seconds, so it is worth saying out loud instead of only muttering into the log.
    ///
    /// Either way the answer is the same: record nothing, and say so where it will be read.
    /// </summary>
    private string? CaptureDesktopAudio()
    {
        if (Config.DesktopAudioDeviceId is { Length: > 0 } pinned)
            return pinned;

        var current = AudioManager.GetDefaultPlaybackDeviceId();

        if (current is not null &&
            string.Equals(current, Config.TvAudioDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"Default audio is already the couch device ({Config.TvAudioDeviceName}), so "
                   + "there is nothing to move and no desktop device to record. Either a previous "
                   + "restore failed, or the couch device and the desktop device are the same "
                   + "choice.");

            // A problem, so it answers to the problem switch rather than the session one. Somebody
            // who has deliberately pointed both settings at one device will turn this off, and the
            // message tells them the other way to stop it as well.
            //
            // Once per run of the app, not once per session. The state it reports cannot change
            // between one session and the next without somebody visiting the settings page, so every
            // session after the first was repeating a message whose answer had not moved — and this
            // is a setting people swap in and out of several times an evening. A warning that arrives
            // every single time is one that gets dismissed without reading, which costs the times it
            // has something new to say. The log still records it on every session, where repetition
            // is evidence rather than noise.
            if (!_warnedAudioSameDevice
                    && Config.ShowNotifications && Config.ShowProblemNotifications)
            {
                _warnedAudioSameDevice = true;

                Toast.Show(Ui.Words.NoticeAudioAlreadyThere,
                           Ui.Words.NoticeAudioAlreadyThereDetail,
                           Ui.Theme.Warn,
                           duration: Toast.ProblemDuration);
            }

            return null;
        }

        return current;
    }

    /// <summary>
    /// Restore desktop audio, retrying briefly. Endpoints disappear and re-enumerate around a
    /// display change, so the device is often missing for a second or two right after the switch.
    /// </summary>
    /// <summary>
    /// Which audio switch is the current one.
    ///
    /// [BUG] Restoring the desktop device does not just set it, it holds it for several seconds
    /// against Windows changing its mind. Start a session inside that window and the teardown of
    /// the *previous* session is still holding: it saw the television device arrive, called it
    /// interference, and put the desk device back. The log showed it plainly — "TV audio device
    /// selected" at 21:51:54, then "putting it back" at 21:51:55, then "Desktop audio restored and
    /// held" two seconds into a session that had only just begun. Sound stayed at the desk for the
    /// whole session.
    ///
    /// Every switch takes a ticket. A hold whose ticket is no longer the current one stops, because
    /// something newer has since decided where the sound should be.
    /// </summary>
    private int _audioTicket;

    private void RestoreDesktopAudio(string deviceId)
    {
        // Two phases, because there are two different ways this fails.
        //
        // First, getting it set at all: the endpoint is often still missing for a second or two
        // after the display topology changes, which the old wait already handled.
        //
        // Then, making it stay. That is the part that was missing, and it is why this only went
        // wrong sometimes. When the TV's HDMI endpoint disappears, Windows picks a replacement
        // default of its own — and that choice can land *after* ours, silently overwriting it.
        // The old code called SetDefaultPlaybackDevice once, never looked again, and reported
        // success. Setting a value is not the same as it being set.
        int ticket = Interlocked.Increment(ref _audioTicket);

        var appear = DateTime.UtcNow + TimeSpan.FromSeconds(8);

        while (DateTime.UtcNow < appear)
        {
            if (Volatile.Read(ref _audioTicket) != ticket)
            {
                Log.Info("Desktop audio restore abandoned: something newer is deciding the audio now.");
                return;
            }

            if (AudioManager.DeviceExists(deviceId) && Apply(deviceId))
            {
                Hold(deviceId, ticket);
                return;
            }
            Thread.Sleep(400);
        }

        Log.Warn("Desktop audio device never came back; leaving audio on whatever Windows chose.");
    }

    /// <summary>Set the default and confirm it took. Several of these calls report success and do nothing.</summary>
    private static bool Apply(string deviceId)
    {
        try
        {
            AudioManager.SetDefaultPlaybackDevice(deviceId);

            return string.Equals(AudioManager.GetDefaultPlaybackDeviceId(), deviceId,
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not set the default audio device: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Watch for a few seconds and put it back if something moves it.
    ///
    /// Bounded rather than indefinite, and it gives up after a handful of corrections: if
    /// something on this machine is determined to own the default device, fighting it forever
    /// would mean the audio device flapping every few seconds for as long as the app runs.
    /// </summary>
    private void Hold(string deviceId, int ticket)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        int corrections = 0;

        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(700);

            // The whole point of the ticket. A session starting mid-hold moves the sound to the
            // television on purpose, and that is not interference to be corrected.
            if (Volatile.Read(ref _audioTicket) != ticket)
            {
                Log.Info("Desktop audio hold released: a session has taken the audio.");
                return;
            }

            var now = AudioManager.GetDefaultPlaybackDeviceId();
            if (string.Equals(now, deviceId, StringComparison.OrdinalIgnoreCase)) continue;

            if (++corrections > 4)
            {
                Log.Warn($"Something keeps moving the default audio device to {now ?? "(none)"}. "
                       + "Leaving it alone rather than fighting over it.");
                return;
            }

            Log.Info($"Default audio moved to {now ?? "(none)"} after the switch; putting it back.");
            Apply(deviceId);
        }

        Log.Info("Desktop audio restored and held.");
    }

    private void SwitchAudioTo(string deviceId, string label)
    {
        // Takes the ticket, which stops any hold left running from the last teardown.
        Interlocked.Increment(ref _audioTicket);
        SwitchAudioCore(deviceId, label);
    }

    private void SwitchAudioCore(string deviceId, string label)
    {
        if (!Config.SwitchAudio || string.IsNullOrWhiteSpace(deviceId)) return;

        // Wait for the endpoint, do not give up on the first look. A TV's HDMI audio device appears
        // a second or two after the display comes on, so checking once — as this used to — left
        // sound on the desktop device for the whole session even though the right one was chosen.
        // Bounded, and it returns the instant the device shows, so it costs nothing in the ordinary
        // case where the endpoint is already there.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);

        while (DateTime.UtcNow < deadline)
        {
            if (AudioManager.DeviceExists(deviceId))
            {
                AudioManager.SetDefaultPlaybackDevice(deviceId);
                Log.Info($"{label} audio device selected.");
                return;
            }

            Thread.Sleep(400);
        }

        Log.Warn($"The {label} audio device never appeared, so audio was left unchanged. If it plays "
               + "over HDMI, the TV may have been slow to wake or on the wrong input.");
    }

    private void RaiseStateChanged() => StateChanged?.Invoke();

    /// <summary>
    /// Restore a window if Steam minimised it, then bring it to the foreground — used to drop straight
    /// back into a running game rather than reopening Big Picture over the top of it.
    ///
    /// A plain SetForegroundWindow from this background thread is refused by Windows' foreground lock,
    /// so it borrows the current foreground thread's input queue for the moment it takes to hand focus
    /// over — the standard way around that lock. Without it the game often stayed behind whatever the
    /// user was looking at (the settings window), which is what left the app sitting over the game.
    /// </summary>
    private static void BringToForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);

            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint us = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != us && AttachThreadInput(us, fgThread, true);

            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);

            if (attached) AttachThreadInput(us, fgThread, false);
        }
        catch { /* focus is best-effort */ }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    // Minimise even a window whose owning thread is busy (e.g. the game mid display-change), without
    // waiting on it — unlike SW_MINIMIZE, which can hang on an unresponsive window.
    private const int SW_FORCEMINIMIZE = 11;
}

using System.Runtime.InteropServices;
using CouchMode.Session;
using CouchMode.Games;
using CouchMode.Input;
using CouchMode.Steam;

namespace CouchMode.Ui;

/// <summary>
/// The resident tray application. Owns the icon, the menu, and the Big Picture watcher,
/// and drives everything through a single <see cref="SessionController"/>.
/// </summary>
public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly SessionController _session;
    private readonly BigPictureWatcher _watcher;
    private readonly HdrCoordinator _hdr;
    private readonly ControllerWatcher _pads = new();
    private readonly PointerControl _pointer = new();
    private readonly BigPictureFront _front = new();
    private readonly GameLanding _landing;

    // Both shortcuts do the same thing, so they share one action. Created in the constructor
    // because they capture it.
    private readonly HotkeyWindow _keyShortcut;

    /// <summary>The second pair, for turning HDR on and off. See ToggleHdr.</summary>
    private readonly HotkeyWindow _keyHdr;
    private readonly PadShortcut _padHdr;
    private readonly PadShortcut _padShortcut;

    // Samples Steam's window geometry while we're on the desktop. It has to be observed
    // continuously: once Big Picture opens, the desktop window is already gone.
    private readonly System.Windows.Forms.Timer _steamObserver;

    /// <summary>Keeps a current reading of which window is on which display. See LayoutStore.Sample.</summary>
    private readonly System.Windows.Forms.Timer _windowSampler;
    private DateTime _lastGeometrySave = DateTime.MinValue;

    /// <summary>
    /// Releases the controller's raw-input stream once the machine has been idle, so a pad that
    /// streams continuously (a DualSense) stops holding the display awake. See CheckControllerIdle.
    /// </summary>
    private readonly System.Windows.Forms.Timer _idleWatch;

    /// <summary>Watches the PS / Guide button, but only while a session is running.</summary>
    private readonly System.Windows.Forms.Timer _guideWatch;

    // Marshals work back to the UI thread. Display switching is slow and blocking, so it runs
    // on the pool; menu and icon updates have to come back here.
    private readonly Control _sync = new();

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _launchItem;
    private readonly ToolStripMenuItem _settingsItem;

    private bool _busy;

    private readonly bool _launchedByWindows;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    // A Big Picture that opens within this window of a start-with-Windows launch is Steam coming up
    // at boot, not the user sitting down to play — so it does not start a couch session. Generous,
    // because Steam's own autostart can be slow on a fresh boot.
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(2);

    public TrayApp(AppConfig config, bool launchedByWindows = false)
    {
        _launchedByWindows = launchedByWindows;

        _sync.CreateControl();
        Toast.BindTo(_sync);

        // Created before the state subscription below, which captures it.
        _watcher = new BigPictureWatcher();
        _watcher.Opened += OnBigPictureOpened;
        _watcher.Closed += OnBigPictureClosed;

        _hdr = new HdrCoordinator(config);

        // Lets the front guard hand the foreground to a launcher that opened behind Big Picture.
        _front.RunningGameWindow = () => _hdr.RunningGameWindow();

        _landing = new GameLanding(() => _hdr.RunningGameWindow());

        // Every subscription below captures _session, so it has to exist first.
        _session = new SessionController(config);

        // So a session started or resumed while a game is already up drops straight into the game
        // rather than reopening Big Picture over it.
        _session.RunningGameWindow = () => _hdr.RunningGameWindow();

        // The process watcher inside the HDR coordinator is the reliable "a game is running" signal,
        // so the Big Picture front guard steps off the top layer for a launcher it would otherwise
        // miss. Fires on the watcher thread; GameActive is a volatile field, so no marshalling.
        _hdr.GameActivityChanged += running =>
        {
            _front.GameActive = running;

            // Pull a just-launched game onto the primary monitor (the TV during a session), but only
            // while a session is active — off the couch the user's monitor choice is their own.
            if (running && _session.IsInTvMode) _landing.Begin();
            else if (!running) _landing.End();

            // A game closing is one of the two ways the machine goes quiet. Marshalled because this
            // fires on the watcher thread and a toast belongs to the UI thread.
            if (!running) OnUi(ReleaseHeldUpdateNotice);
        };

        // Close the game as the session tears down.
        //
        // No longer conditional. This runs on the path that ends a session outright, which is now only
        // reached by choosing to end it — the prompt asks first, and "leave the game running" is one of
        // its answers rather than a setting decided in advance. Getting here means the answer was to
        // close.
        _session.BeforeTeardown = () => _hdr.CloseRunningGame();

        // A pad switching on is usually the moment someone sits down to play.
        // Each refusal says why. A trigger that silently does nothing is indistinguishable
        // from one that is broken, and every one of these conditions is invisible from outside.
        _pads.Connected += device => OnUi(() =>
        {
            // A pad coming back after it dropped out mid-session is not "starting a session". It is
            // picking up where you were, so it happens before the start-on-connect switch is even
            // consulted — that setting is about sitting down at a desktop, and this is not that.
            if (PadReconnected(device)) return;

            if (!_session.Config.StartOnControllerConnect)
            {
                Log.Info($"{device.Name} connected, but starting on connect is switched off.");
                return;
            }

            if (_session.IsInTvMode) { Log.Info($"{device.Name} connected; already on the TV."); return; }
            if (_busy) { Log.Info($"{device.Name} connected while busy switching; ignored."); return; }

            // A pad that "connects" right after a start-with-Windows launch was already on when the PC
            // booted — Bluetooth just took a while to come up — not you sitting down to play. Ignored
            // for a grace window so booting never drops you into a session; because the watcher only
            // reports a fresh connection, nothing then triggers until the pad is actually turned off
            // and back on.
            if (_launchedByWindows && DateTime.UtcNow - _startedAt < StartupGrace)
            {
                Log.Info($"{device.Name} was already on at startup; not switching. Turn it off and on to start a session.");
                return;
            }

            RunBackground("Switching to the TV", () => _session.EnterTvMode());
        });

        _pads.Disconnected += device => OnUi(() =>
        {
            if (!_session.IsInTvMode) { Log.Info($"{device.Name} disconnected; already on the desktop."); return; }
            if (_busy) { Log.Info($"{device.Name} disconnected while busy switching; ignored."); return; }

            // Two events, told apart before either is acted on. Switching a controller off is how
            // people say they have finished; a battery dying is not, and the same answer is wrong for
            // both. See WasPoweredOff.
            var wanted = WasPoweredOff(device) ? _session.Config.OnControllerOff
                                               : _session.Config.OnControllerLost;

            if (wanted == DisconnectAction.Ignore)
            {
                Log.Info($"{device.Name} disconnected; nothing is set to happen for that.");
                return;
            }

            // The one answer that closes something, and the only one that skips the mute, the swap
            // and the prompt — all three exist to keep a game alive, and this is the case where the
            // user has said not to.
            //
            // Not confirmed first, deliberately. Every other route to closing a game asks, because
            // every other route is a button pressed by somebody sitting in front of the screen. This
            // one is reached by switching a controller off and walking away, so a confirmation would
            // be a window nobody is there to answer, sitting over the television until it times out.
            // The confirmation for this answer is having chosen it in the settings.
            if (wanted == DisconnectAction.EndAndClose)
            {
                Log.Info($"{device.Name} was switched off; closing the game and ending the session.");

                _leftRunning = false;
                _swappedOnDisconnect = false;

                // ReturnToDesktop runs the teardown hook, which is what closes the game, and takes
                // Big Picture with it. Nothing else to arrange.
                RunBackground("Ending the session", () => _session.ReturnToDesktop());
                return;
            }

            // Come back to the desk. Nothing is closed, and nothing is asked on the television.
            //
            // [BUG] This used to put the session-end prompt up where it was: on the TV. That prompt
            // is answered with the controller, and the event that raised it is the controller going
            // away — so the one input that could reach it had just stopped existing. The comment
            // said "answered with mouse or keyboard", which is true and useless, because the mouse
            // and keyboard are at the desk and the prompt was on the television.
            //
            // So the swap happens first and the question comes afterwards, on the screen the user is
            // actually sitting at. This is the same path as the prompt's own "Swap to desktop"
            // answer: game and Big Picture minimised and left running, display and sound back at the
            // desk, nothing torn down. Switching the pad back on resumes it — see PadReconnected.
            Log.Info($"{device.Name} disconnected; coming back to the desktop with everything running.");

            // Any prompt already up is on the television, and the pad that could answer it has just
            // gone. Left alone it would sit topmost on a screen nobody is looking at, fighting the
            // desk for the foreground, and its answers would act on a session that no longer exists
            // — including an auto-dismiss three minutes later that restores the game we are about to
            // minimize. OnBigPictureClosed already does this for the same reason.
            if (_endPrompt is { IsDisposed: false } stale) stale.Close();

            _front.End();          // its timer lives on this thread, and we are already on it
            _watcher.Suppress();   // Big Picture stays open; do not let the watcher drag us back in

            _leftRunning = true;
            _swappedOnDisconnect = true;

            // Whether there is anything worth asking about, decided before the swap rather than after
            // it — MinimizeToDesktop minimizes the game, and a minimized window is harder to find.
            bool gameUp = _hdr.RunningGameWindow() != IntPtr.Zero;

            RunBackground("Coming back to the desktop", () =>
            {
                _session.MinimizeToDesktop(closeBigPicture: false);

                // Only when a game is actually running.
                //
                // Sitting in Big Picture and putting the pad down is not a situation that needs a
                // window about it: nothing is mid-save, nothing is lost, and every answer the prompt
                // offers is either meaningless ("close the game" with no game) or already true. The
                // swap on its own is the whole of what is wanted there, and switching the pad back on
                // still takes you straight back — that part is unchanged.
                MuteIfWanted();

                if (!gameUp)
                {
                    Log.Info("No game was running, so no prompt; Big Picture is waiting on the desktop.");
                    return;
                }

                if (wanted == DisconnectAction.ComeBackAndAsk) OnUi(RaiseDisconnectPrompt);
            });
        });

        // Handles held for reading buttons go stale the moment a pad is unplugged, and a new
        // pad will not be read at all until the list is rebuilt. The watcher already knows when
        // either happens, so it says so rather than the reader polling to find out.
        _pads.DevicesChanged += () =>
        {
            _pads.ResumeReports();
            HidPad.Invalidate();
            HidPoll.Invalidate();

            // The second way back in, and the one that covers the pads the first cannot see.
            //
            // [BUG] Resuming after a disconnect swap hung off Connected alone, and Connected is
            // filtered by StartOnControllerLink — "wired only" or "wireless only" — while
            // Disconnected deliberately is not, precisely so nobody is stranded on the television
            // with no way back. So with the link set to wired only, a wireless pad could drop out and
            // trigger the swap, then come back and be filtered out of the event that would have
            // resumed it. The way back was closed by a setting about starting sessions.
            //
            // DevicesChanged is unfiltered, so this catches those. Costs nothing when there is
            // nothing to resume: the first line of PadReconnected is the flag.
            if (!_swappedOnDisconnect) return;

            OnUi(() =>
            {
                if (!_swappedOnDisconnect) return;

                var back = ControllerWatcher.Attached().FirstOrDefault();
                if (back is not null) PadReconnected(back);
            });
        };

        // Button state is read from the reports the watcher already receives, rather than by
        // opening the controller — see HidPad.
        HidPad.Listen(_pads);

        // The mouse control reads the same raw reports the watcher already delivers.
        _pointer.Configure(config);

        // Stand the pad-driven cursor down only while a shortcut is actually being recorded — the one
        // moment the raw button presses must go to the recorder, not the pointer — or while a session-end
        // popup is up, where the stick drives the selection and must not also wander the pointer or scroll
        // behind it. Everywhere else in the app (including the rest of the Controller and Hotkeys pages)
        // the pad steers the cursor, so the whole app is navigable from the couch.
        _pointer.ControllerUiCapturing = () => ShortcutBox.AnyListening
                                            || _endPrompt is { IsDisposed: false };

        // Named, so the pointer stands down in a game whatever shape its window is.
        _pointer.GameWindow = () => _hdr.RunningGameWindow();

        _front.Enabled = config.BigPictureAlwaysOnTop;

        _pads.OnlyDeviceId = config.TriggerControllerId;
        _pads.Link = config.StartOnControllerLink;

        // Recorded at launch as well as at each session, so a machine that has never run a
        // session still knows where its monitors live before anything moves them.
        Display.DisplayManager.RememberPositions(config);

        if (NeedsPadWatching(config)) _pads.Start();

        // Armed as the session begins, so nothing launched afterwards can beat it to the check.
        _session.StateChanged += () =>
        {
            if (_session.IsInTvMode)
            {
                _hdr.ArmForSession();

                // A game already up as the session begins is taken over here, before Big Picture is
                // placed — so the running-game signal is set (keeping Big Picture from pinning over
                // it) and it can be brought back to the front, even if it was never launched through
                // Steam.
                _hdr.AdoptRunningGame();
            }
            else
            {
                _hdr.DisarmAfterSession();

                // The other way the machine goes quiet. Held back if the session ended with the game
                // left running — the game closing will call this again.
                OnUi(ReleaseHeldUpdateNotice);
            }
        };

        _session.StateChanged += () => OnUi(() =>
        {
            UpdateMenu();

            // A session changing hands never leaves the easter egg on screen. It is summoned from a
            // window that is about to go away, and a fullscreen overlay surviving that would sit over
            // the television with nothing left to dismiss it.

            // Poll less often once we are on the TV, and hand memory back to Windows: from
            // here until the session ends this process should be as close to invisible as
            // possible, because a game is using the machine.
            _watcher.SetSessionActive(_session.IsInTvMode);

            if (_session.IsInTvMode)
            {
                // Start listening to Big Picture again. Suppression is how "go to the desktop and
                // leave Big Picture open" stops the watcher dragging us straight back into a session
                // — but it is keyed to a window that is still open, so it never cleared itself, and
                // the *next* session inherited it. That is why quitting Big Picture stopped ending
                // the session: the close was seen and then thrown away as suppressed.
                _watcher.ClearSuppression();

                ResourceTuning.ReleaseMemory();
                StandAsideForSession();
                _front.Begin();   // hold Big Picture on top until a game or launcher takes over

                // Treated as not held, because the trigger is the release. Whatever was down on the way
                // in — including the PS press that started this session — has its release swallowed
                // here rather than read as a request to end what it just began. The grace below covers
                // the same press arriving a moment later.
                _guideHeld = false;
                _guideArmedAt = DateTime.UtcNow;

                // Stamped here rather than when the switch was asked for: what matters to the prompt
                // is how long Steam has had to finish arranging itself, and that clock starts when
                // the session is actually live.
                _sessionLiveSince = DateTime.UtcNow;

                // Nothing is left behind on the desktop any more, whatever was before.
                _leftRunning = false;
                _swappedOnDisconnect = false;

                // Sound back on, whether or not this session is the one that muted it. Restore only
                // touches sessions this app muted itself, so calling it unconditionally here cannot
                // undo a mute the user set by hand in the volume mixer.
                CouchMode.Audio.GameAudio.Restore();

                if (_resumeWhenBack)
                {
                    _resumeWhenBack = false;
                    RunBackground("Going back to the session", () => _session.ResumeMinimized());
                }
            }
            else
            {
                // Same in this direction: the release of the press that ended the session must not
                // immediately start another one.
                _guideHeld = false;
                _guideArmedAt = DateTime.UtcNow;

                _front.End();
                ComeBackAfterSession();
            }
        });

        _statusItem = new ToolStripMenuItem("On the desktop")
        {
            Enabled = false,
            Font = new Font("Segoe UI Semibold", 9F),
        };

        _toggleItem = new ToolStripMenuItem("Switch to the TV", null, (_, _) => ToggleMode())
        {
            ToolTipText = "Switch your display and audio over to the TV.",
        };

        _launchItem = new ToolStripMenuItem("Open Steam Big Picture", null, (_, _) => LaunchBigPicture());

        _settingsItem = new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings());

        var help = new ToolStripMenuItem("Troubleshooting");
        help.DropDownItems.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem("Save a diagnostics report…", null, (_, _) => SaveDiagnostics())
            {
                ToolTipText = "Records what Couch Session can see about your displays and audio devices.",
            },
            new ToolStripMenuItem("Open the log folder", null, (_, _) => OpenDataFolder()),
        });

        _menu = new ContextMenuStrip { ShowItemToolTips = true };
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            _toggleItem,
            _launchItem,
            new ToolStripSeparator(),
            _settingsItem,
            help,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit Couch Session", null, (_, _) => ExitApp()),
        });

        _icon = new NotifyIcon
        {
            Icon = AppIcon.ForTray(active: false),
            Text = "Couch Session — on the desktop",
            ContextMenuStrip = _menu,
        };

        // Made visible after construction, not in the initializer.
        //
        // NotifyIcon defers creating its own message window until it is first shown, and asking
        // for that inside the initializer — before the rest of the object exists and before the
        // message loop is running — is the case where Shell_NotifyIcon can quietly fail. Doing
        // it as a separate step is the documented order and costs nothing.
        _icon.Visible = true;

        _icon.DoubleClick += (_, _) => OpenSettings();

        // Explorer takes every tray icon with it when it restarts or crashes, and it is the
        // application's job to put its own back. Windows broadcasts TaskbarCreated to say so.
        // A second launch asks this copy to show its settings rather than starting another.
        _broadcasts = new BroadcastWatcher(
            RestoreTrayIcon,
            () => OnUi(OpenSettings),

            // Nothing that happens in the seconds after a wake is the user doing anything —
            // unless the thing that woke the machine was the controller itself.
            (why, systemResume) => OnUi(() => OnWake(why, systemResume)),

            () => OnUi(() =>
            {
                Log.Info("Asked to quit by another copy.");
                ExitApp();
            }));

        _steamObserver = new System.Windows.Forms.Timer { Interval = 5000 };
        _steamObserver.Tick += (_, _) => ObserveSteamWindow();
        _steamObserver.Start();

        // Samples where the windows are, so there is an answer available at the moment a display
        // vanishes — by which point Windows has already moved everything and the truth is gone.
        _windowSampler = new System.Windows.Forms.Timer { Interval = 3000 };
        _windowSampler.Tick += (_, _) => SampleWindows();
        _windowSampler.Start();

        _idleWatch = new System.Windows.Forms.Timer { Interval = 3000 };
        _idleWatch.Tick += (_, _) => CheckControllerIdle();
        _idleWatch.Start();

        // Runs all the time, because the button means something on both sides: end the session from the
        // couch, and from the desktop either go back to a session left running or start a new one.
        _guideWatch = new System.Windows.Forms.Timer { Interval = 80 };
        _guideWatch.Tick += (_, _) => CheckGuideButton();

        // Armed from now, so a button held while the app is starting — or Steam's own launch — cannot
        // read as a press the instant the first tick lands.
        _guideArmedAt = DateTime.UtcNow;
        _guideHeld = false;
        _guideWatch.Start();

        // Always watched: opening Big Picture is what starts a session, and closing it is what ends
        // one — the two are the same thing now, so there is nothing to switch off.
        _watcher.Start();

        // Last line of defence: if the app dies while on the TV, put the displays back.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => HandBack();
        Application.ApplicationExit += (_, _) => HandBack();
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => HandBack();

        // Start a session when the PC wakes, if asked. Fires on a system thread, so it is marshalled
        // onto the UI thread, then given a moment for the machine to finish waking before switching.
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // The Game Bar is not applied at launch any more. It is a Windows setting the user changes
        // in our window and Windows keeps — see GameBarControl — so imposing our stored copy of it
        // on every start would overwrite whatever they had done in Windows since.

        // Undo any mute left behind by a run that was killed rather than closed. Nothing else on the
        // machine would ever put it back, so a crash would otherwise leave someone with a game that
        // is silent for good and no idea why. See GameAudio.RecoverFromCrash.
        CouchMode.Audio.GameAudio.RecoverFromCrash();

        // Look for a new version shortly after launch, and once a day after that.
        //
        // [BUG] AppConfig.CheckForUpdates has existed and defaulted to true since the setting was
        // added, and nothing ever read it. The only caller of Updates.CheckAsync was a button on the
        // About page — the one page nobody opens — so in practice an update existed and no one heard
        // about it. This is the switch finally doing what its name says.
        //
        // Delayed rather than immediate: launch is the busiest moment this app has, and a version
        // number is the least urgent thing it could be doing. The daily repeat matters because this
        // lives in the tray for weeks at a time; checking only at launch would mean a machine that is
        // never rebooted never hears about anything.
        StartUpdateWatch(config);

        // Both shortcuts toggle: press once to go to the TV, press again to come back. A
        // shortcut that only started a session would be half a feature, since the moment you
        // most want a keyboard is when you are trying to get *out* of one.
        _keyShortcut = new HotkeyWindow(() => OnUi(ToggleFromShortcut));
        _padShortcut = new PadShortcut(() => OnUi(ToggleFromShortcut));

        // A distinct id, or Windows refuses the second registration and that hotkey never fires.
        _keyHdr = new HotkeyWindow(() => OnUi(ToggleHdr), 0x0C0E);
        _padHdr = new PadShortcut(() => OnUi(ToggleHdr));

        ApplyShortcuts(config);

        // Let go while the settings window is recording a new shortcut, and take them back
        // afterwards. Without this the combination being recorded never arrives: a registered
        // hotkey is consumed by Windows before any window sees it, so pressing the current one
        // would toggle the display instead of being written into the box. See Shortcuts.
        Shortcuts.CaptureChanged += capturing => OnUi(() =>
        {
            if (capturing)
            {
                // Release every registered hotkey, not just the session one: a registered global
                // hotkey is swallowed by Windows before the recording box sees it, so any combo that
                // collided with one already set could never be recorded.
                _keyShortcut.Apply(0);
                _keyHdr.Apply(0);
                _padShortcut.Pause();
                _padHdr.Pause();
            }
            else
            {
                ApplyShortcuts(_session.Config);
                _padShortcut.Resume();
                _padHdr.Resume();
            }
        });

        UpdateMenu();

        // Drop straight onto the couch if the user asked for it. Deferred with a one-shot timer so
        // the message loop, tray and watchers are all up first, and so Steam has a moment to be ready.
        if (config.StartSessionOnLaunch) StartSessionAfter(1500);
    }

    // ---- actions ----

    /// <summary>Start a TV session from outside the tray (command line).</summary>
    public void RequestTvMode()
    {
        if (!_session.IsInTvMode) ToggleMode();
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        // Logged both ways on purpose: if "entering system sleep" never appears, the machine is only
        // blanking the display, not truly sleeping, and start-on-wake has nothing to fire on.
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            Log.Info("The machine is entering system sleep.");
            return;
        }

        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;

        Log.Info("The machine woke from system sleep; start-session-on-wake is "
               + (_session.Config.StartSessionOnWake ? "on." : "off."));

        if (_session.Config.StartSessionOnWake)
            OnUi(() => StartSessionAfter(2500));
    }

    /// <summary>
    /// Start a session after a short delay, on the UI thread. The delay lets things settle — Steam
    /// coming up at launch, or the machine finishing its wake — and the guards make sure a session
    /// that is already running, or a half-finished switch, is never disturbed.
    /// </summary>
    private void StartSessionAfter(int delayMs)
    {
        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, delayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (!_busy && !_session.IsInTvMode && Configured())
                ToggleMode();
            else
                Log.Info($"Auto session not started: busy={_busy}, "
                       + $"already in TV mode={_session.IsInTvMode}, configured={Configured()}.");
        };
        timer.Start();
    }


    private void ToggleMode()
    {
        if (_busy) return;

        if (_session.IsInTvMode)
        {
            EndSession(bigPictureStillUp: true);
        }
        else
        {
            if (!Configured()) return;
            RunBackground("Switching to the TV", () =>
            {
                _session.EnterTvMode();

                // A game already running was brought to the front by EnterTvMode — don't front Big
                // Picture over it.
                if (_hdr.RunningGameWindow() != IntPtr.Zero) return;

                // Already running? Place it now — the watcher's Opened event has been and gone.
                var hwnd = BigPictureLauncher.FindWindow();
                if (hwnd != IntPtr.Zero) _session.PlaceBigPicture(hwnd);
            });
        }
    }

    private void LaunchBigPicture()
    {
        if (_busy || !Configured()) return;

        bool alreadyUp = BigPictureLauncher.FindWindow() != IntPtr.Zero;

        // Big Picture already open, and nothing about to announce it.
        //
        // [BUG] This only ever asked Steam to open Big Picture and left the watcher's Opened event to
        // start the session from there. That is exactly right when Big Picture is closed, and does
        // nothing at all when it is not: the watcher *adopts* a Big Picture that was already up when
        // the app started rather than treating it as newly opened — deliberately, so restarting the
        // app during a session does not drag anyone back to the television — so there is no Opened
        // event coming, and asking Steam to open a window it already has open produces no second one
        // either. Pressing Start Couch Session did nothing whatsoever, silently.
        //
        // The session is started here instead. ToggleMode already knows how to take over a Big
        // Picture that is running: it enters TV mode and places the existing window, because the same
        // situation arises when a session is begun by a controller switching on.
        if (alreadyUp && !_session.IsInTvMode)
        {
            Log.Info("Big Picture was already open, so starting the session directly rather than "
                   + "waiting for an open that will never be announced.");

            ToggleMode();
            return;
        }

        // In a session already, with the shell somewhere behind a game. The tray menu item that lands
        // here means "show me Big Picture", and there is nothing to launch.
        if (alreadyUp)
        {
            BigPictureLauncher.BringToFront();
            return;
        }

        // Nothing open: ask Steam, and let the watcher's Opened event start the session from there.
        RunBackground("Opening Big Picture", () => _session.LaunchBigPicture());
    }

    /// <summary>
    /// End the live session. When a game is running and "close the game when the session ends" is on,
    /// this does not tear down immediately — it asks first, with a controller-navigable popup, so a
    /// dropped or idle controller can never close a game and lose progress. When no game is up, or the
    /// setting is off (the game is meant to survive), it ends the ordinary way and leaves any game
    /// running.
    /// </summary>
    /// <param name="bigPictureStillUp">True when Big Picture is still open (the hotkey path), so the
    /// watcher is muted to stop it bouncing us back to the TV as the teardown closes it. False when Big
    /// Picture has already gone (it was just quit), where there is nothing to mute.</param>
    private void EndSession(bool bigPictureStillUp)
    {
        bool gameUp = _hdr.RunningGameWindow() != IntPtr.Zero;

        // A game is up — ask what to do with it before anything irreversible happens. Always, with no
        // setting in front of it: the prompt's own answers cover everything the setting used to decide,
        // and it asks at the one moment the question can actually be answered.
        if (gameUp)
        {
            OnUi(() => ShowSessionEndPrompt(bigPictureStillUp));
            return;
        }

        // No game, but Big Picture is still up and the user ended the session on purpose — ask what to
        // do with Big Picture itself. Only when it is genuinely still up (the hotkey), never when it
        // was just quit.
        if (bigPictureStillUp)
        {
            OnUi(ShowBigPicturePrompt);
            return;
        }

        // Nothing to ask about: no game, and Big Picture already gone.
        RunBackground("Returning to the desktop", () => _session.ReturnToDesktop());
    }

    /// <summary>The session-end confirmation, if one is on screen — so a second end request reuses it
    /// rather than stacking another.</summary>
    private SessionEndPrompt? _endPrompt;

    /// <summary>
    /// Ask what to do with the running game before ending the session: close it, keep playing, or drop
    /// to the desktop with the game left running. Driven by the app's own controller reading, so it
    /// works even though Steam Input is routing the pad to the game — and defaults to doing nothing, so
    /// no input (a dead or idle controller) never closes anything.
    /// </summary>
    private void ShowSessionEndPrompt(bool bigPictureStillUp)
    {
        if (_endPrompt is { IsDisposed: false } open) { open.BringToFront(); return; }
        if (_promptPending) return;

        _promptPending = true;

        WhenForegroundSettles(() =>
        {
            _promptPending = false;

            // The wait is up to four seconds long, and a session can end by another route inside it —
            // Big Picture being closed, the hotkey again. Asking what to do with a session that is
            // already over is worse than not asking at all.
            if (_session.IsInTvMode) RaiseSessionEndPrompt(bigPictureStillUp);
        });
    }

    private void RaiseSessionEndPrompt(bool bigPictureStillUp)
    {

        // Keeping playing is not in the list — B / Circle already does it, and listing it as well was
        // the same action written twice.
        //
        // Ordered by what people actually come here to do, not gentlest first. This used to lead with
        // the harmless answer so that an accidental A destroyed nothing, which is the right rule for a
        // prompt with nothing behind it — but there is something behind it now. Ending the session is
        // the reason this window is being read nine times out of ten, and the confirmation is what
        // catches the stray press, so making the common answer the far one bought safety that was
        // already paid for and charged everyone else for it.
        var options = new[]
        {
            new SessionEndPrompt.Option(Words.ConfirmCloseYes, SessionEndPrompt.Choice.Close, Theme.Bad, Words.ConfirmCloseYesWhat),
            new SessionEndPrompt.Option(Words.ConfirmCloseToBigPicture, SessionEndPrompt.Choice.CloseToBigPicture, Theme.Warn, Words.ConfirmCloseToBigPictureWhat),
            new SessionEndPrompt.Option(Words.ConfirmCloseMinimize, SessionEndPrompt.Choice.Minimize, Theme.Info, Words.ConfirmCloseMinimizeWhat),
        }.Concat(NavigationOptions()).ToArray();

        var prompt = new SessionEndPrompt(Words.ConfirmCloseTitle, Words.ConfirmCloseBody, options)
        {
            Shortcut = SessionEndPrompt.Choice.Close,

            // Not the plain "End session" the Big Picture prompt uses. Close runs ReturnToDesktop with
            // BeforeTeardown set to CloseRunningGame, so on this prompt — the one that exists because a
            // game is still running — Square closes that game. The hint has to say so.
            ShortcutHint = Words.ConfirmEndGameHint,

            // Last resort if the game will not give up the foreground: minimize it so the pad reaches
            // the prompt. Restored below if the user keeps playing.
            // Keeping playing restores the window that had the foreground when the prompt appeared —
            // the game — which is exactly the prompt's own default, so nothing more is needed here.
            GameWindow = () => _hdr.RunningGameWindow(),
        };
        _endPrompt = prompt;
        prompt.FormClosed += (_, _) => { if (ReferenceEquals(_endPrompt, prompt)) _endPrompt = null; };

        prompt.Chosen += choice =>
        {
            // Anything that shuts the game down is asked about again first.
            //
            // Both answers below close it, and on this prompt so does a single press of Square — which
            // is one button away from the B that keeps playing. The listed rows now say what they cost,
            // but saying it is not the same as making someone look at it, and the thing being spent is
            // whatever the player had not saved.
            if (choice is SessionEndPrompt.Choice.Close or SessionEndPrompt.Choice.CloseToBigPicture
                && _session.Config.ConfirmClosingGame)
            {
                AskBeforeClosing(choice);
                return;
            }

            Act(choice);
        };

        void Act(SessionEndPrompt.Choice choice)
        {
            switch (choice)
            {
                case SessionEndPrompt.Choice.Close:
                    if (bigPictureStillUp) _watcher.Suppress();
                    RunBackground("Returning to the desktop", () => _session.ReturnToDesktop());
                    break;

                case SessionEndPrompt.Choice.Minimize:
                    OnUi(() => _front.End());   // its timer lives on the UI thread
                    if (bigPictureStillUp) _watcher.Suppress();

                    // The game is still there behind the desktop, so the PS button means "back to it".
                    _leftRunning = true;

                    RunBackground("Minimizing to the desktop", () =>
                    {
                        _session.MinimizeToDesktop();
                        MuteIfWanted();
                    });
                    break;

                case SessionEndPrompt.Choice.CloseToBigPicture:
                    // Close the game but stay in the session — hand the couch back to Big Picture.
                    RunBackground("Closing the game", () =>
                    {
                        _hdr.CloseRunningGame();
                        BigPictureLauncher.BringToFront();
                    });
                    break;

                case SessionEndPrompt.Choice.Stay:
                    break;   // keep playing — nothing to do

                default:
                    HandleNavigationChoice(choice);
                    break;
            }
        }

        prompt.Show();

        void AskBeforeClosing(SessionEndPrompt.Choice wanted) => Confirm(wanted, Act);
    }

    /// <summary>
    /// The second question, in front of anything that shuts something down.
    ///
    /// Raised only from the game prompt. It briefly covered the Big Picture prompt as well and was
    /// pulled back on the user's instruction: that path runs only when no game is running, so there is
    /// nothing to lose by taking it and nothing for a confirmation to protect.
    /// </summary>
    private void Confirm(SessionEndPrompt.Choice wanted, Action<SessionEndPrompt.Choice> act)
    {
        string game = _hdr.RunningGame()?.Name ?? Words.SureCloseUnnamed;

        var sure = new SessionEndPrompt(
            string.Format(Words.SureCloseTitle, game), Words.SureCloseBody,
            [
                // Safe answer first, because the first row is the one selected when the window opens —
                // so a press that arrives before anyone has read anything does nothing.
                new SessionEndPrompt.Option(Words.SureCloseStay, SessionEndPrompt.Choice.Stay,
                                            Theme.Info, Words.SureCloseStayWhat),

                new SessionEndPrompt.Option(string.Format(Words.SureCloseYes, game),
                                            SessionEndPrompt.Choice.Close, Theme.Bad,
                                            Words.SureCloseYesWhat),
            ])
        {
            KeepHint = Words.SureCloseStay,

            // A way out of being asked, on the window doing the asking.
            //
            // The setting behind this lives on the General page, which is a thing you cannot reach
            // from a sofa without ending the session first — so somebody who finds this prompt
            // tiresome has no route to switching it off at the moment they are thinking about it.
            // Reachable with the stick and the A button like everything else here; see
            // SessionEndPrompt.Rows for why it could not simply be a mouse target.
            DontAskText = Words.SureCloseDontAsk,

            // No Square shortcut. This window exists because a one-press answer was too easy to reach;
            // giving it one of its own would hand back exactly what it was put here to take.
            GameWindow = () => _hdr.RunningGameWindow(),
        };

        _endPrompt = sure;
        sure.FormClosed += (_, _) => { if (ReferenceEquals(_endPrompt, sure)) _endPrompt = null; };

        // Backing out here backs out of the confirmation, not of the session — the first prompt has
        // already gone, so there is nothing to return to and nothing left to do.
        sure.Chosen += answer =>
        {
            // Honoured whichever way the question was answered, including backing out. Someone who
            // ticks this and then keeps playing has still said they do not want to be asked again —
            // and making the tick conditional on confirming would mean the only way to switch off a
            // warning was to go through with the thing it warns about.
            if (sure.DontAskAgain && _session.Config.ConfirmClosingGame)
            {
                _session.Config.ConfirmClosingGame = false;

                // Applied first, saved second: what matters is the next time a game is closed, and a
                // failed write must not be what decides whether the question comes back.
                try { _session.Config.Save(); }
                catch (Exception ex) { Log.Warn($"Could not save the confirm-close setting: {ex.Message}"); }

                Log.Info("Asked not to confirm closing a game again; the check is now off.");
            }

            if (answer == SessionEndPrompt.Choice.Close) act(wanted);
            else if (answer is not SessionEndPrompt.Choice.Stay) HandleNavigationChoice(answer);
        };

        sure.Show();
    }

    /// <summary>True while a prompt is waiting to be raised, so a second end request does not queue
    /// another one behind it.</summary>
    private bool _promptPending;

    /// <summary>When the current session became live, for the launch-settling window below.</summary>
    private DateTime _sessionLiveSince = DateTime.MinValue;

    /// <summary>
    /// How long after a session starts the foreground is still considered to be moving.
    /// </summary>
    private static readonly TimeSpan LaunchSettling = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Hold a prompt back until the foreground has stopped changing hands.
    ///
    /// [BUG] Opening a prompt in the first seconds of a Big Picture launch left Big Picture responding
    /// to the pad through it. Two earlier attempts at this were both aimed at the wrong thing: fighting
    /// harder for the foreground only gave Steam Input a queue of configuration changes to work
    /// through, and having the prompt ignore the pad stopped the two *both* reacting but did nothing
    /// about Big Picture reacting, which is what was actually being complained about.
    ///
    /// Steam Input feeds whichever window it believes is in front. There is no way to take the pad off
    /// Big Picture except to be that window, and there is no way to be that window while Steam is still
    /// throwing the foreground around finishing its launch. So the prompt waits. It appears a beat
    /// later than the button that asked for it, and in exchange it owns the pad the moment it does.
    ///
    /// "Settled" is the same window in front for four checks running, not a guess at how long a launch
    /// takes — that number would be wrong on a slower machine and wasteful on a faster one. The ceiling
    /// is there because a foreground that never settles must not mean a prompt that never appears.
    /// </summary>
    private void WhenForegroundSettles(Action raise)
    {
        // Skipped once the session has been running a while.
        //
        // [BUG] The wait was unconditional, and it exists for one situation: the first seconds of a
        // Big Picture launch, when Steam is still throwing the foreground about and would take the
        // pad back off the prompt. In a game half an hour later there is nothing to wait for — the
        // foreground has not moved in minutes — and yet every prompt still paid at least 400ms of
        // settling on top of Steam's overlay opening, which reads as the button not having worked
        // and gets pressed again. The protection is kept where it earns its place and dropped where
        // it does not.
        if (_session.IsInTvMode && DateTime.UtcNow - _sessionLiveSince > LaunchSettling)
        {
            raise();
            return;
        }

        var timer = new System.Windows.Forms.Timer { Interval = 100 };

        var startedAt = DateTime.UtcNow;
        var steadySince = DateTime.UtcNow;
        IntPtr last = GetForegroundWindow();

        timer.Tick += (_, _) =>
        {
            var front = GetForegroundWindow();

            if (front != last) { last = front; steadySince = DateTime.UtcNow; }

            // 250ms rather than 400. Four checks at 100ms was the original description and 400 was
            // the number written; the point is "the same window twice in a row and then some", and a
            // quarter of a second is that on every machine this has been run on. The remaining wait
            // is only ever paid during a launch now, where it is the whole reason this exists.
            bool settled = DateTime.UtcNow - steadySince >= TimeSpan.FromMilliseconds(250);
            bool waited = DateTime.UtcNow - startedAt >= TimeSpan.FromSeconds(4);

            if (!settled && !waited) return;

            timer.Stop();
            timer.Dispose();

            if (waited && !settled)
                Log.Info("The foreground never settled; showing the session prompt anyway.");

            raise();
        };

        timer.Start();
    }

    /// <summary>
    /// The row at the foot of both prompts.
    ///
    /// It does not answer what the prompt is asking. It is there for the case the prompt cannot help
    /// with on its own: the pad can reach this window and nothing else, because the pointer is off.
    /// Offering it here is the only place it can be offered — by definition the user has no other way
    /// to drive the screen at that moment.
    ///
    /// It names the state it will move to, not the state it is in, so the row says what pressing it
    /// does rather than what is currently true.
    ///
    /// There was a second row, "Show the on-screen keyboard", and it has gone. It could not keep its
    /// promise: it asked Steam for its keyboard through a steam:// URL that reports nothing back, and
    /// fell through to Windows' TabTip or osk, neither of which a controller can type on without the
    /// pointer — so the row either did nothing visible or produced a keyboard needing the very thing
    /// the row below it exists to turn on. It also sat on the two prompts that come up mid-session,
    /// where nothing is waiting for text. A row that usually fails, on a window it does not belong
    /// on, is worse than not offering it: it spends the reader's attention and teaches them the
    /// prompt cannot be trusted.
    /// </summary>
    private SessionEndPrompt.Option[] NavigationOptions() =>
    [
        new(_session.Config.MouseControlEnabled ? Words.PromptMouseOff : Words.PromptMouseOn,
            SessionEndPrompt.Choice.ToggleMouse, Theme.TextDim),
    ];

    /// <summary>
    /// Silence the game we have just walked away from, if the user wants that.
    ///
    /// Called after the swap rather than before it. The swap moves audio back to the desk device,
    /// and a session only exists on the device it is playing to — muting first would find the
    /// session on the television endpoint and lose it a moment later.
    ///
    /// Only ever for a game this app is watching. A game Steam is running that is not on the HDR
    /// list has a findable window but no findable process, and there is no honest way to guess which
    /// audio session is its.
    /// </summary>
    private void MuteIfWanted()
    {
        if (!_session.Config.MuteGameOnDesktop) return;

        uint pid = 0;
        string label = "the game";

        // The watched process when there is one, and the foreground window's owner when there is not.
        //
        // [BUG] This only ever asked the HDR game watcher, and that watcher follows the games on the
        // HDR list — which is empty on a fresh install now that nothing pre-ticks it. So for most
        // people the mute never even reached the audio code: no Process, early return, silence in
        // the log and a game still shouting at them. The window is the reliable half; every running
        // game has one, and RunningGameWindow already falls back to asking Steam which game is up.
        if (_hdr.RunningGameProcess is { } watched)
        {
            pid = (uint)watched.Id;
            label = watched.ProcessName;
        }
        else
        {
            var window = _hdr.RunningGameWindow();

            if (window != IntPtr.Zero) GetWindowThreadProcessId(window, out pid);
        }

        if (pid == 0)
        {
            Log.Info("Nothing identifiable is running, so there is no game audio to mute.");
            return;
        }

        CouchMode.Audio.GameAudio.MuteProcess(pid, label);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

    /// <summary>
    /// Go back into a session that is waiting behind the desktop.
    ///
    /// Wrapped rather than calling EnterTvMode directly, because <see cref="_resumeWhenBack"/> has to
    /// be set before the switch starts — the state change that consumes it fires part-way through —
    /// and there is no other point at which a failure can clear it again.
    ///
    /// [BUG] Without this a failed resume left the flag set for good. EnterTvMode's own failure path
    /// puts the desktop back and rethrows, which means the state never settles on TV, which means the
    /// one place that clears the flag never runs. The next successful session — possibly days later
    /// and nothing to do with this one — then opened by restoring and fronting Big Picture over
    /// whatever it was actually starting.
    /// </summary>
    private void ResumeIntoSession()
    {
        try
        {
            _session.EnterTvMode();
        }
        catch
        {
            _resumeWhenBack = false;
            throw;   // RunBackground still reports it; this only stops the flag outliving the attempt
        }
    }

    /// <summary>
    /// A controller coming back after it dropped out of a running session. True when that was handled
    /// here and the ordinary start-on-connect trigger should be left alone.
    ///
    /// This is the other half of the disconnect swap. The session's windows are still up behind the
    /// desktop, so there is something to go back to, and the user did not choose to be here — so
    /// switching the pad on means what it plainly looks like it means.
    /// </summary>
    private bool PadReconnected(ControllerDevice device)
    {
        if (!_swappedOnDisconnect) return false;

        if (_session.IsInTvMode) { _swappedOnDisconnect = false; return false; }

        // The swap itself takes seconds — displays settle, audio moves, the window layout is put
        // back — and a pad that re-enumerates inside that window (a dongle re-seating, a pad that
        // reboots itself) would otherwise be thrown away entirely. The watcher only announces a
        // fresh arrival, so nothing would ever announce it again and the user would have to
        // power-cycle the controller to get back in. Asked again shortly instead.
        if (_busy)
        {
            Log.Info($"{device.Name} came back while the swap was still finishing; trying again shortly.");
            RetryReconnect(device, attempts: 6);
            return true;
        }

        // The flag alone is not enough. The game may have been closed from the desktop in the
        // meantime, and going back into nothing is worse than doing nothing at all — the display
        // would move to a television showing a bare desktop.
        bool alive = _hdr.RunningGameWindow() != IntPtr.Zero
                  || BigPictureLauncher.FindWindow() != IntPtr.Zero;

        if (!alive)
        {
            Log.Info($"{device.Name} came back, but the session's windows are gone; not resuming.");
            _swappedOnDisconnect = false;
            _leftRunning = false;
            return false;
        }

        Log.Info($"{device.Name} came back; going straight back into the session.");

        // Take the prompt down first. It is offering choices about a session that is about to be
        // resumed underneath it, and one of them closes the game.
        if (_endPrompt is { IsDisposed: false } open) open.Close();

        _swappedOnDisconnect = false;
        _leftRunning = false;
        _resumeWhenBack = true;

        RunBackground("Switching to the TV", ResumeIntoSession);
        return true;
    }

    /// <summary>
    /// Ask again in a moment whether a pad that arrived mid-swap can be resumed into.
    ///
    /// One-shot timer per attempt rather than a loop, because this has to run on the UI thread and
    /// give it back between tries. Bounded: if the swap has not finished after a few seconds
    /// something else is wrong, and quietly retrying forever would hide it.
    /// </summary>
    private void RetryReconnect(ControllerDevice device, int attempts)
    {
        if (attempts <= 0)
        {
            Log.Warn($"{device.Name} came back but the app never stopped being busy; not resuming.");
            return;
        }

        var timer = new System.Windows.Forms.Timer { Interval = 700 };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();

            if (!_swappedOnDisconnect) return;   // answered at the desk, or resumed another way

            if (_busy) { RetryReconnect(device, attempts - 1); return; }

            PadReconnected(device);
        };

        timer.Start();
    }

    /// <summary>
    /// The question that lands on the desk screen after a disconnect swap.
    ///
    /// Deliberately not the session-end prompt. That one is written for a controller and a
    /// television: it is answered with a pad, and its hint row names pad buttons. Here there is no
    /// pad by definition, so this offers three plain answers and names none of them after a button.
    ///
    /// Going back is the first and the accented one, because it is what most disconnects turn out to
    /// be — a battery swapped, a cable pushed back in — and because it is the only answer that
    /// undoes itself for free.
    /// </summary>
    private void RaiseDisconnectPrompt()
    {
        if (_endPrompt is { IsDisposed: false } open) { open.BringToFront(); return; }

        // The session may have been resumed between the swap finishing and this arriving. A prompt
        // about coming back to a session you are already in is nonsense, and one of its answers
        // would close the game underneath you.
        if (_session.IsInTvMode || !_swappedOnDisconnect) return;

        var options = new[]
        {
            new SessionEndPrompt.Option(Words.DisconnectResume, SessionEndPrompt.Choice.Resume,
                                        Theme.Good, Words.DisconnectResumeWhat),
            new SessionEndPrompt.Option(Words.DisconnectClose, SessionEndPrompt.Choice.Close,
                                        Theme.Bad, Words.DisconnectCloseWhat),
            new SessionEndPrompt.Option(Words.DisconnectLeave, SessionEndPrompt.Choice.Stay,
                                        Theme.Info, Words.DisconnectLeaveWhat),
        };

        // Named screen rather than inferred: the game and Big Picture were minimized a moment ago,
        // and a minimized window's rectangle is parked off in the corner of the desktop, so working
        // the monitor out from the foreground is a coin toss. Primary is the desk again by this
        // point — putting it back is what the swap did.
        var prompt = new SessionEndPrompt(Words.DisconnectTitle, Words.DisconnectBody, options,
                                          on: Screen.PrimaryScreen)
        {
            // Everything this class does for a television is wrong here. See AtTheDesk.
            AtTheDesk = true,

            // The one prompt in the app that arrives without being asked for, so the one prompt with
            // a way to switch itself off.
            DontAskText = Words.DisconnectDontAsk,
        };

        _endPrompt = prompt;
        prompt.FormClosed += (_, _) => { if (ReferenceEquals(_endPrompt, prompt)) _endPrompt = null; };

        prompt.Chosen += choice =>
        {
            // Read before the answer is acted on, and honoured whichever answer it was: someone who
            // ticks this and then chooses to go back to their game has still said they do not want
            // to be asked next time.
            //
            // It drops to ComeBack rather than Ignore. The tick is about the window, not about being
            // stranded on a television with a pad that has stopped working — turning the whole
            // feature off would be answering a question that was never put.
            // Steps whichever of the two settings raised this prompt down a notch. Both are checked
            // because either can be the one asking, and a tick that silenced the wrong one would be
            // worse than no tick.
            if (prompt.DontAskAgain)
            {
                if (_session.Config.OnControllerOff == DisconnectAction.ComeBackAndAsk)
                    _session.Config.OnControllerOff = DisconnectAction.ComeBack;

                if (_session.Config.OnControllerLost == DisconnectAction.ComeBackAndAsk)
                    _session.Config.OnControllerLost = DisconnectAction.ComeBack;

                // Applied first, saved second. What matters is the next disconnect, and a failed
                // write must not be what decides whether the prompt appears again.
                try { _session.Config.Save(); }
                catch (Exception ex) { Log.Warn($"Could not save the disconnect setting: {ex.Message}"); }

                Log.Info("Asked not to show the disconnect prompt again; coming back quietly from now on.");
            }

            switch (choice)
            {
                case SessionEndPrompt.Choice.Resume:
                    _swappedOnDisconnect = false;
                    _leftRunning = false;
                    _resumeWhenBack = true;

                    RunBackground("Switching to the TV", ResumeIntoSession);
                    break;

                case SessionEndPrompt.Choice.Close:
                    // Confirmed first. This is the one answer here that cannot be undone, and the
                    // window it appears in arrived unbidden while the user was doing something else.
                    Confirm(choice, _ =>
                    {
                        _swappedOnDisconnect = false;
                        _leftRunning = false;

                        // Big Picture goes too. It is still running, minimized, from the swap — and
                        // with the game closed and both flags cleared there is nothing left that
                        // would ever bring it back into view, so leaving it would be leaving a
                        // fullscreen shell running invisibly for the rest of the session.
                        RunBackground("Closing the game", () =>
                        {
                            _hdr.CloseRunningGame();
                            BigPictureLauncher.Close();

                            // Nothing is left to unmute — the sessions died with the process — but
                            // the record of what was muted has to go, or it outlives the thing it
                            // describes and the next launch spends a moment looking for ghosts.
                            CouchMode.Audio.GameAudio.Restore();
                        });
                    });
                    break;

                default:
                    // Left as it is. The flag stays set on purpose, so switching the pad back on
                    // still takes you straight in.
                    break;
            }
        };

        prompt.Show();
    }

    private void HandleNavigationChoice(SessionEndPrompt.Choice choice)
    {
        switch (choice)
        {
            case SessionEndPrompt.Choice.ToggleMouse:
                _session.Config.MouseControlEnabled = !_session.Config.MouseControlEnabled;

                // Applied before it is written: the point of pressing this is the next few seconds,
                // and a failed save must not be what decides whether the pointer works.
                _pointer.Configure(_session.Config);

                try { _session.Config.Save(); }
                catch (Exception ex) { Log.Warn($"Could not save the mouse setting: {ex.Message}"); }

                Log.Info($"Controller mouse turned {(_session.Config.MouseControlEnabled ? "on" : "off")} "
                       + "from the session prompt.");
                break;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();


    /// <summary>
    /// Ask what to do with Big Picture when a session is ended (by the hotkey) with Big Picture up
    /// and no game running: keep using it, drop to the desktop with it left open, or close it and
    /// end the session. Same controller-driven popup as the game one — the safe "keep using" default
    /// means a dead or idle pad closes nothing.
    /// </summary>
    private void ShowBigPicturePrompt()
    {
        if (_endPrompt is { IsDisposed: false } open) { open.BringToFront(); return; }
        if (_promptPending) return;

        _promptPending = true;

        WhenForegroundSettles(() =>
        {
            _promptPending = false;
            if (_session.IsInTvMode) RaiseBigPicturePrompt();
        });
    }

    private void RaiseBigPicturePrompt()
    {

        // Resuming Big Picture is B / Circle, named in the hint row rather than listed here.
        var options = new[]
        {
            new SessionEndPrompt.Option(Words.BigPictureEndClose, SessionEndPrompt.Choice.Close, Theme.Bad, Words.BigPictureEndCloseWhat),
            new SessionEndPrompt.Option(Words.BigPictureEndMinimize, SessionEndPrompt.Choice.Minimize, Theme.Info, Words.BigPictureEndMinimizeWhat),
        }.Concat(NavigationOptions()).ToArray();

        var prompt = new SessionEndPrompt(Words.BigPictureEndTitle, Words.BigPictureEndBody, options)
        {
            KeepHint = Words.BigPictureEndResume,
            Shortcut = SessionEndPrompt.Choice.Close,
            ShortcutHint = Words.ConfirmEndSessionHint,

            // Found fresh rather than restoring the handle we saw on the way in: Steam recreates this
            // window, and bringing Big Picture forward by name is what actually hands the pad back to
            // it without waiting for Steam Input to notice a stale window.
            // Called once, deliberately.
            //
            // [BUG] This briefly called BringToFront three times over a second, to beat the race where
            // Windows re-picks a foreground window as this prompt goes away. That was duplicating
            // machinery that already existed and it broke resuming: BringToFront ends in HoldUp, which
            // already re-checks for the next second and restores Big Picture if it drops — so three
            // calls meant three concurrent HoldUp loops and three rounds of AttachThreadInput plus
            // SetForegroundWindow against a fullscreen shell that was still settling. The log shows all
            // three failing in a row ("Big Picture would not take the foreground back") and the visible
            // result was Big Picture minimizing on resume, sometimes momentarily and sometimes for good.
            //
            // One call. The retrying it needs is inside it.
            RestoreFocus = BigPictureLauncher.BringToFront,
        };
        _endPrompt = prompt;
        prompt.FormClosed += (_, _) => { if (ReferenceEquals(_endPrompt, prompt)) _endPrompt = null; };

        prompt.Chosen += choice =>
        {
            // No confirmation on this prompt, on purpose.
            //
            // EndSession only routes here when no game is running, so the most this answer costs is
            // Big Picture closing and the desktop coming back — both of which undo themselves in one
            // press. A second question in front of something with nothing at stake is the kind of
            // friction that teaches people to dismiss confirmations without reading them, which is
            // paid for on the prompt where it does matter.
            Act(choice);
        };

        void Act(SessionEndPrompt.Choice choice)
        {
            switch (choice)
            {
                case SessionEndPrompt.Choice.Close:
                    // End the session and close Big Picture — ReturnToDesktop closes it as part of
                    // the ordinary teardown.
                    _watcher.Suppress();
                    RunBackground("Returning to the desktop", () => _session.ReturnToDesktop());
                    break;

                case SessionEndPrompt.Choice.Minimize:
                    // Go to the desktop but leave Big Picture running (minimized), so a session can
                    // drop straight back into it.
                    OnUi(() => _front.End());   // its timer lives on the UI thread
                    _watcher.Suppress();

                    // Big Picture is still open behind the desktop, so the PS button means "back to it".
                    _leftRunning = true;

                    RunBackground("Minimizing to the desktop",
                                  () => _session.MinimizeToDesktop(closeBigPicture: false));
                    break;

                case SessionEndPrompt.Choice.Stay:
                    break;   // keep using Big Picture — nothing to do

                default:
                    HandleNavigationChoice(choice);
                    break;
            }
        }

        prompt.Show();
    }

    private void OnBigPictureOpened(IntPtr hwnd)
    {
        if (_busy) return;

        // Do not let Steam opening Big Picture on its own at boot drag the desktop into a couch
        // session. Right after a start-with-Windows launch that is the machine starting up, not the
        // user choosing to play — so it is ignored (the watcher moves on) for a short grace window.
        // Opening Big Picture any time after that starts a session as normal.
        if (_launchedByWindows && DateTime.UtcNow - _startedAt < StartupGrace)
        {
            Log.Info("Big Picture opened during startup; leaving the desktop alone.");
            return;
        }

        RunBackground("Switching to the TV", () =>
        {
            if (!_session.IsInTvMode)
                _session.EnterTvMode();

            // With a game already running, EnterTvMode brought it to the front — do not lay Big Picture
            // over the top of it. Only place Big Picture when it is what the user is coming back to.
            if (_hdr.RunningGameWindow() != IntPtr.Zero) return;

            // Re-find the handle: Steam sometimes recreates the window across a display change.
            var current = BigPictureLauncher.FindWindow();
            _session.PlaceBigPicture(current != IntPtr.Zero ? current : hwnd);
        });
    }

    private void OnBigPictureClosed()
    {
        // _busy means we're mid-transition and may have closed Big Picture ourselves — the
        // Steam restart does exactly that. Reacting here would bounce straight back to desktop.
        if (_busy || !_session.IsInTvMode) return;

        // Big Picture has gone without us asking for it — it was quit from the Steam menu, or it fell
        // over. Either way the couch has no shell left on it, so there is nothing to put a prompt in
        // front of and nobody able to answer one: a prompt here would just sit on the TV over a
        // fullscreen game that still has the controller. So this path never asks. The game is closed
        // and the session ends.
        //
        // Closing is the polite shutdown throughout — WM_CLOSE to the game's own window, the same
        // thing Alt+F4 and Steam's Stop button send, so the game saves and exits itself. Killing a
        // process is only ever the fallback for one that ignores it.
        Log.Info("Big Picture went away on its own — closing the game and ending the session.");

        // Nothing survives this, so there is nothing for the PS button to go back to.
        _leftRunning = false;
        _swappedOnDisconnect = false;

        // Take the prompt down if one happens to be up, so it cannot act on a session that is
        // already ending underneath it.
        OnUi(() => { if (_endPrompt is { IsDisposed: false } open) open.Close(); });

        RunBackground("Returning to the desktop", () =>
        {
            _hdr.CloseRunningGame();
            _session.ReturnToDesktop();
        });
    }

    /// <summary>
    /// Open settings once the message loop is running.
    ///
    /// Posted rather than called directly so the tray, the watchers and the session are all
    /// live behind it. Everything this app does in the background belongs to TrayApp, so the
    /// window must open on top of a running app rather than instead of one.
    /// </summary>
    public void ShowSettingsOnLaunch() => _sync.BeginInvoke(OpenSettings);

    /// <summary>The settings window while it is open, so a second request reuses it.</summary>
    private SettingsForm? _settings;

    /// <summary>
    /// Take whichever shortcuts the config asks for, and say so if Windows refuses one.
    ///
    /// A refused keyboard shortcut is the only failure here that the user has to know about,
    /// because the app looks identical either way — a combination that does nothing is
    /// indistinguishable from a combination that was never saved. Windows refuses when another
    /// program already owns it, and the only fix is to pick a different one.
    /// </summary>
    /// <summary>
    /// The machine came back from sleep.
    ///
    /// Two quite different things can be true here, and telling them apart is the whole point.
    ///
    /// Ordinarily a wake is noise: USB re-enumerates on resume, so every attached pad looks like
    /// it has just arrived, and acting on that would throw the display onto the television every
    /// time someone nudged their mouse. That is what the settle is for.
    ///
    /// But a controller can be the thing that *did* the waking — a wired pad or an Xbox wireless
    /// adapter will do it when Device Manager is set to allow it — and that is somebody sitting
    /// down to play. It is the one moment where starting a session unasked is exactly right.
    ///
    /// A restart is not a wake and never reaches here: there is no resume event, and a fresh
    /// start baselines whatever is attached rather than treating it as arrivals.
    /// </summary>
    private void OnWake(string why, bool systemResume)
    {
        // A display coming back on is not a system resume — nothing re-enumerates when a monitor
        // wakes, and a controller turning on is handled in the ordinary way — so there is nothing to
        // do here, and it is important to do nothing. Re-subscribing to the pad on a display wake
        // re-pins the sleep timer, and since the display now sleeps on its own while a pad is idle,
        // that turned into a release/resume loop that kept the whole PC awake. The idle watch takes
        // the pad's stream back on its own the moment there is genuine input (see CheckControllerIdle),
        // so a display wake is left strictly alone.
        if (!systemResume)
        {
            Log.Info($"{why}; left to the idle watch, pad not re-subscribed.");
            return;
        }

        // Suppress first, always. Whatever the answer turns out to be, the re-enumeration is
        // still noise and must not be read as a pad being plugged in.
        _pads.Settle(TimeSpan.FromSeconds(12), why);

        if (_session.IsInTvMode || _busy) return;

        // Start on wake, whatever did the waking. This runs off the power broadcast, not
        // SystemEvents.PowerModeChanged, which does not reliably deliver a Resume after S3 — which is
        // why "start a session on wake" only ever fired when the controller itself was the wake
        // source. The broadcast is dependable, so the setting is honoured here directly. Already on
        // the UI thread (OnWake is dispatched through OnUi), so the timer can be armed straight away.
        if (_session.Config.StartSessionOnWake)
        {
            // "Only when the controller woke it" defers to WakeSource, which asks powercfg what armed
            // the resume. Off the UI thread — powercfg is a process launch and resume is the worst
            // moment to block the message loop.
            if (_session.Config.StartOnWakeControllerOnly)
            {
                Task.Run(() =>
                {
                    if (WakeSource.WasController())
                        OnUi(() =>
                        {
                            Log.Info("The controller woke the machine; starting a session.");
                            StartSessionAfter(2500);
                        });
                    else
                        Log.Info("Woke, but not by the controller; start-on-wake left it alone.");
                });
                return;
            }

            Log.Info("Woke with start-session-on-wake set; starting a session shortly.");
            StartSessionAfter(2500);
            return;
        }

        if (!_session.Config.StartOnControllerConnect) return;

        // Asked off the UI thread. powercfg is a process launch, and resume is the worst moment
        // to block the message loop — the tray icon, the display work and the toasts all live
        // on it.
        Task.Run(() =>
        {
            bool byController = WakeSource.WasController();

            if (!byController) return;

            OnUi(() =>
            {
                if (_session.IsInTvMode || _busy) return;

                Log.Info("The controller woke the machine; starting a session.");
                RunBackground("Switching to the TV", () => _session.EnterTvMode());
            });
        });
    }

    /// <summary>
    /// Whether the controller watcher needs to be running.
    ///
    /// The shortcut counts, not just the connect and disconnect triggers. Button reports arrive
    /// through the watcher's raw input registration, so with it stopped a controller shortcut
    /// would be saved, shown in the settings, and never fire — for the entirely invisible reason
    /// that nothing was listening.
    /// </summary>
    private static bool NeedsPadWatching(AppConfig config)
    {
        // Always, and deliberately not narrowed now that ending on a disconnect is a setting again.
        //
        // The obvious move would be to stop watching when that setting is off, and it would be a bug:
        // the watcher's raw input registration is also how button reports arrive, so switching off
        // one trigger would silently kill the controller shortcuts and pad-as-mouse with it. That is
        // exactly the failure this method's summary was written about. Still takes the config, and
        // still exists as a question, because it is a real one and the answer may narrow again.
        _ = config;
        return true;
    }

    /// <summary>Whether a problem the app hit is worth putting on screen.</summary>
    private static bool Problems(AppConfig config)
        => config.ShowNotifications && config.ShowProblemNotifications;

    private void ApplyShortcuts(AppConfig config)
    {
        if (!_keyShortcut.Apply(config.ShortcutKeyboard) && config.ShortcutKeyboard != 0 && Problems(config))
        {
            Toast.Show(Words.NoticeShortcutTaken,
                       string.Format(Words.ShortcutTaken, KeyShortcut.Describe(config.ShortcutKeyboard)),
                       Theme.Bad, delay: TimeSpan.FromMilliseconds(400));
        }

        _padShortcut.Apply(config.ShortcutController,
                           (ushort)config.ShortcutControllerVendor,
                           (ushort)config.ShortcutControllerProduct);

        if (!_keyHdr.Apply(config.ShortcutKeyboardHdr) && config.ShortcutKeyboardHdr != 0 && Problems(config))
        {
            Toast.Show(Words.NoticeShortcutTaken,
                       string.Format(Words.ShortcutTaken, KeyShortcut.Describe(config.ShortcutKeyboardHdr)),
                       Theme.Bad, delay: TimeSpan.FromMilliseconds(400));
        }

        _padHdr.Apply(config.ShortcutControllerHdr,
                      (ushort)config.ShortcutControllerHdrVendor,
                      (ushort)config.ShortcutControllerHdrProduct);
    }

    /// <summary>
    /// Turn HDR on or off on the main display, from a hotkey.
    ///
    /// Independent of a session on purpose. The reason to reach for this is a game or an app that
    /// looks wrong in HDR, and that happens at the desk as readily as on the television — needing
    /// to start a couch session first to get at it would make it useless for the case it exists
    /// for.
    ///
    /// The current state is read rather than tracked, so it stays right however HDR was last
    /// changed — by Windows, by a game, or by Auto HDR.
    /// </summary>
    private void ToggleHdr()
    {
        try
        {
            var status = Display.HdrControl.PrimaryStatus();

            // Warning, not Info. Notify routes by severity — Info is governed by "when something you
            // pressed is done" and everything else by "when something goes wrong" — so as Info this
            // sat under the wrong switch, two lines from the identical case below it. Somebody who
            // turned problem messages off still got told their monitor cannot do HDR, and somebody
            // who turned them off *because* of that message could not make it stop.
            if (!status.Found || !status.Supported)
            {
                Notify(Words.WarnHdrUnsupported, ToolTipIcon.Warning);
                return;
            }

            bool wanted = !status.Enabled;

            if (!Display.HdrControl.SetPrimaryHdr(wanted))
            {
                Notify(Words.WarnHdrRefused, ToolTipIcon.Warning);
                return;
            }

            Log.Info($"HDR turned {(wanted ? "on" : "off")} by hotkey.");

            // If a game is running and the user asked the hotkey to remember, record this choice for
            // it — so Auto HDR applies it by itself next time — and name the game in the confirmation.
            // The setting still decides whether to remember; what changed is what counts as an
            // occasion worth remembering. It used to be this hotkey and nothing else — now any route
            // that turns HDR on does it, including a game launched with HDR already on.
            string? remembered = null;
            if (_session.Config.HdrHotkeyRemembersGame)
            {
                try { remembered = _hdr.RememberHdrForRunningGame(wanted); }
                catch (Exception ex) { Log.Warn($"Remembering HDR for the running game failed: {ex.Message}"); }

                // Turned HDR on with no game running: the user is setting it up before launching — the
                // right order for HDR — so arm it for whatever game they open next.
                if (wanted && remembered is null)
                    try { _hdr.ArmHdrForNextGame(); }
                    catch (Exception ex) { Log.Warn($"Arming HDR for the next game failed: {ex.Message}"); }
            }

            if (_session.Config.ShowNotifications && _session.Config.ShowHdrNotifications)
            {
                string detail = remembered is { } game
                    ? string.Format(wanted ? Words.NoticeHdrRemembered : Words.NoticeHdrForgotten, game)
                    : Words.NoticeHdrByHotkey;

                Toast.Show(wanted ? Words.NoticeHdrOn : Words.NoticeHdrOff, detail,
                           wanted ? Theme.Good : Theme.Accent);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Toggling HDR from the hotkey failed", ex);
        }
    }

    /// <summary>
    /// Start or end a session from a shortcut.
    ///
    /// Guarded against arriving mid-switch: both shortcuts are easy to press twice, and a
    /// second toggle landing while the displays are still moving is how a machine ends up half
    /// on the television.
    /// </summary>
    private void ToggleFromShortcut()
    {
        if (_busy)
        {
            Log.Info("Shortcut pressed while already switching; ignored.");
            return;
        }

        Log.Info(_session.IsInTvMode ? "Shortcut pressed: returning to the desktop."
                                     : "Shortcut pressed: switching to the TV.");
        ToggleMode();
    }

    /// <summary>
    /// Get out of the way now that a session is running.
    ///
    /// Two things happen here, and they are really one thing. Controller triggers are suspended
    /// for as long as the settings window is open, so that plugging a pad in to choose it from
    /// the list cannot also fling the display over to the television. That is right up until the
    /// moment a session starts — after which the display is already on the television and the
    /// reasoning has expired, while the suspension quietly remains in force.
    ///
    /// The result was a specific and confusing failure: start a session with the controller
    /// button while the settings window happens to be open, then switch the pad off, and nothing
    /// happens. The disconnect that should have ended the session is discarded, and the log line
    /// explaining why — "the settings window is open" — is somewhere the user is not looking,
    /// because they are on the sofa.
    ///
    /// So the window is minimised and watching resumes. Rebaselined as it resumes, so the pads
    /// present right now count as the starting state rather than arriving as new connections.
    /// </summary>
    private void StandAsideForSession()
    {
        _pads.Suspended = false;
        _pads.Rebaseline();

        if (_settings is not { IsDisposed: false } open)
        {
            // Nothing on screen to move, and nothing to put back afterwards.
            _restoreWindowAfterSession = false;
            return;
        }

        // Already out of the way — but by whom?
        //
        // This runs on every session state change, not once per session, and a session flaps more
        // often than it looks: Big Picture reopening and being closed again is enough. The second run
        // found the window minimised — by us, moments earlier — read that as "the user did this" and
        // cleared the flag, so the session ended and nothing put the window back. That is the bug
        // where the settings window kept staying minimised after a session, and the log shows it
        // exactly: "settings minimized" with no "settings window restored" to match.
        //
        // The flag is the answer. It is cleared at the end of every session, so if it is still set
        // here then this is our own earlier minimise and it must stay set. Only a window we found
        // already minimised at the start of a session leaves it false.
        if (open.WindowState == FormWindowState.Minimized) return;

        _restoreWindowAfterSession = true;
        open.WindowState = FormWindowState.Minimized;

        Log.Info("Session started; settings minimized and controller watching resumed.");
    }

    /// <summary>Whether the settings window was put out of the way by us, and so is ours to put back.</summary>
    private bool _restoreWindowAfterSession;

    /// <summary>
    /// Put the settings window back the way the session found it.
    ///
    /// Everything else this app moves is restored when the session ends — the displays, the audio
    /// device, the power plan, the window layout, Steam's own window. Its own window was the
    /// exception: minimised on the way in and left that way, so the one thing that did not come
    /// back was the thing doing the restoring.
    ///
    /// Only what we minimised, and only to where it was. A window the user had already minimised,
    /// or closed, stays that way — the session is not an excuse to put something on screen that
    /// nobody asked for.
    /// </summary>
    private void ComeBackAfterSession()
    {
        if (!_restoreWindowAfterSession) return;
        _restoreWindowAfterSession = false;

        if (_settings is not { IsDisposed: false } open) return;

        // Put back up whatever state it is in. It was minimised by us, so it is ours to restore, and
        // a window someone has already dug out of the taskbar mid-session is simply left where it is
        // by ReturnFromSession's own check.
        if (open.WindowState != FormWindowState.Minimized)
        {
            Log.Info("Session ended; the settings window was already up, so it was left alone.");
            return;
        }

        // Put back where it was, not where the session left it. Couch mode rearranges the desktop
        // underneath this window — the television becomes primary, monitors detach and come back —
        // and Windows shifts whatever is open to fit each time. Coming out of a session to find
        // the window shunted onto a different monitor is exactly the sort of tidying-up this app
        // does for every other window, and it was not doing it for its own.
        try { open.ReturnFromSession(); }
        catch (Exception ex)
        {
            // Named rather than swallowed. The window failing to come back is invisible from the
            // outside except as "it stayed minimised", which is indistinguishable from never having
            // been asked — and that ambiguity is what made this take two rounds to find.
            Log.Warn($"The settings window could not be restored after the session: {ex.Message}");
            return;
        }

        Log.Info("Session ended; settings window restored.");
    }

    private void OpenSettings()
    {
        // Already open: raise it rather than stacking another on top.
        //
        // Every route in — the tray menu, a double-click, and now a second launch of the exe —
        // is a request to see the settings, not a request for another copy of them. The window
        // is modal, so a second one appears over the first and the one underneath cannot be
        // reached until it is closed, which is the worst of both.
        if (_settings is { IsDisposed: false } open)
        {
            if (open.WindowState == FormWindowState.Minimized)
                open.WindowState = FormWindowState.Normal;

            open.Activate();
            open.BringToFront();
            return;
        }

        if (_busy) return;

        // Pausing pad triggers is the settings window's own business now — it does it only while
        // the Controller page is showing, which is the only place the pause was ever for. See
        // SettingsForm.UpdatePadSuspension. Left cleared here so nothing is paused by merely
        // having the window open.
        _pads.Suspended = false;

        try { ShowSettings(); }
        finally
        {
            // In a finally because leaving either of these set silently breaks something with
            // nothing on screen to suggest why: the flag disables every controller trigger,
            // and a stale reference makes the settings refuse to open ever again.
            _settings = null;

            _pads.Suspended = false;
            _pads.Rebaseline();
        }
    }

    /// <summary>The newest release found, when it is newer than what is running. Null otherwise.</summary>
    private UpdateInfo? _pendingUpdate;

    /// <summary>
    /// A "there is a new version" notice found while playing, waiting for a quiet moment to appear.
    ///
    /// The check runs on a daily timer that has no idea what is on screen, so before this it could
    /// land a corner toast over a fullscreen game — and during a session that toast is drawn at TV
    /// size, on the television, in front of whatever is being played. Nothing about a version number
    /// is worth that. It is held instead and shown once the machine is idle: the release is not going
    /// anywhere, and the update is never installed without a button press either way.
    /// </summary>
    private bool _updateNoticeHeld;

    /// <summary>
    /// Whether now is a bad moment to interrupt: on the television, or with a game up at the desk.
    ///
    /// Both halves matter. "Swap to desktop" ends the session with the game still running, so
    /// checking the session alone would pop a notice over a game somebody stepped away from for a
    /// moment and is about to go back to.
    /// </summary>
    private bool Playing => _session.IsInTvMode || _hdr.RunningGameWindow() != IntPtr.Zero;

    private System.Windows.Forms.Timer? _updateWatch;

    private void StartUpdateWatch(AppConfig config)
    {
        if (!config.CheckForUpdates)
        {
            Log.Info("Update checking is switched off.");
            return;
        }

        // Fifteen seconds in, then daily. Long enough that the check is never competing with the
        // display switch of a start-on-launch session.
        var first = new System.Windows.Forms.Timer { Interval = 15_000 };

        first.Tick += (_, _) =>
        {
            first.Stop();
            first.Dispose();
            LookForUpdate();

            _updateWatch = new System.Windows.Forms.Timer { Interval = 24 * 60 * 60 * 1000 };
            _updateWatch.Tick += (_, _) => LookForUpdate();
            _updateWatch.Start();
        };

        first.Start();
    }

    /// <summary>
    /// Ask GitHub, quietly.
    ///
    /// Nothing is installed and nothing is shown as a result of this — it only records what was found
    /// so the Home page can offer it. Replacing the executable restarts the app, and doing that on a
    /// timer, unasked, while someone is mid-game is not an update, it is a crash with a changelog.
    /// </summary>
    /// <summary>When GitHub was last asked, so opening settings repeatedly does not hammer it.</summary>
    private DateTime _lastUpdateCheck = DateTime.MinValue;

    /// <summary>
    /// The shortest gap between two checks.
    ///
    /// Opening the settings window triggers one, and someone fiddling with settings opens and closes
    /// it several times in a row. The unauthenticated GitHub API allows sixty calls an hour from one
    /// address, which is plenty — but a check per window open is a pointless way to spend them, and
    /// nothing published in the last half hour is going to be missed by waiting.
    /// </summary>
    private static readonly TimeSpan CheckNoOftenerThan = TimeSpan.FromMinutes(30);

    private async void LookForUpdate()
    {
        var config = _session.Config;

        if (!config.CheckForUpdates) return;
        if (DateTime.UtcNow - _lastUpdateCheck < CheckNoOftenerThan) return;

        _lastUpdateCheck = DateTime.UtcNow;

        try
        {
            var found = await Updates.CheckAsync();

            if (found.Error.Length > 0) { Log.Info($"Update check: {found.Error}"); return; }
            if (!found.Available) return;

            _pendingUpdate = found;

            // A window already open needs telling; one opened later is handed it on construction.
            // This is not the interrupting part — it only decides what the Home page shows the next
            // time somebody looks at it — so it happens whether or not a game is running.
            if (_settings is { IsDisposed: false } open) OnUi(() => open.PendingUpdate = found);

            if (Playing)
            {
                _updateNoticeHeld = true;
                Log.Info($"Update {found.Version} found while playing; holding the notice until the "
                       + "session ends and no game is running.");
                return;
            }

            Notify(string.Format(Words.NoticeUpdateFound, found.Version), ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Info($"Update check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Show a held "new version" notice, once nothing is being played.
    ///
    /// Called from both events that can make the moment quiet — the session ending and a game
    /// closing — because either can happen last. Ending a session with the game still running leaves
    /// the game to finish; closing a game at the desk leaves nothing at all. The Playing check is
    /// what makes calling this twice harmless.
    /// </summary>
    private void ReleaseHeldUpdateNotice()
    {
        if (!_updateNoticeHeld || Playing) return;
        if (_pendingUpdate is not { Available: true } found) { _updateNoticeHeld = false; return; }

        _updateNoticeHeld = false;

        Log.Info($"Showing the held notice for version {found.Version}.");
        Notify(string.Format(Words.NoticeUpdateFound, found.Version), ToolTipIcon.Info);
    }

    private void ShowSettings()
    {
        // Opening the window is a check.
        //
        // Launch and a daily tick were the only two triggers, which for something that sits in the
        // tray for weeks meant a new version could go unmentioned for the best part of a day. Opening
        // settings is the moment someone is actually looking at this app, so it is the moment the
        // answer should be current. Rate-limited by CheckNoOftenerThan; the result lands on the Home
        // page by itself through PendingUpdate, so nothing here waits on it.
        LookForUpdate();

        using var form = new SettingsForm(_session.Config, _pads)
        {
            SessionActive = _session.IsInTvMode,

            // And a way to ask again. The window outlives the state it was opened with: a session
            // can start or end from the pad while it is sitting there, and a footer button still
            // offering to start one that is already running is worse than no button at all.
            SessionState = () => _session.IsInTvMode,

            // And whether there is something waiting behind the desktop to go back to.
            //
            // The flag alone is not enough, exactly as it is not enough for the PS button: the game
            // may have been closed from the desktop since, and offering to resume into nothing is
            // worse than offering to start fresh.
            LeftRunningState = () => _leftRunning
                                  && (_hdr.RunningGameWindow() != IntPtr.Zero
                                      || BigPictureLauncher.FindWindow() != IntPtr.Zero),

            // The narrower question, for the Close Game button. Big Picture waiting on its own is
            // something to resume into and nothing to close.
            GameRunningState = () => _hdr.RunningGameWindow() != IntPtr.Zero,

            // Whatever the launch check found, so the window shows it the moment it opens rather
            // than only after someone presses the button on the About page.
            PendingUpdate = _pendingUpdate,
        };

        // Recorded before it is shown, since ShowDialog does not return until it closes and
        // the request to reuse it arrives while it is still up.
        _settings = form;

        // Saving applies immediately rather than on close, because the window now stays open
        // after a save.
        form.Saved += ApplyConfig;

        // Pointer settings apply as they are dragged, not when the save lands.
        //
        // The save is debounced and its timer restarts on every step, so during a drag it never fires
        // — which meant the pointer kept its old speed for the whole drag and jumped once, well after
        // the thumb was let go. Only the pointer is reconfigured here; everything else still waits
        // for the save, because nothing else has an effect you can feel while adjusting it.
        form.PointerPreview += cfg => _pointer.Configure(cfg);

        form.ShowDialog();

        if (form.ExitRequested)
        {
            ExitApp();
            return;
        }

        // Closing the window did not close the app, so say where it went.
        //
        // Only when the close was the whole action: starting or ending a session shows its own
        // notification, and the app being resident is the least interesting thing happening at
        // that moment. Two toasts stacked to say one useful thing is noise.
        if (!form.StartRequested && !form.RevertRequested && !form.CloseGameRequested
                                 && _session.Config.ShowNotifications
                                 && _session.Config.ShowMinimizeNotification)
        {
            Toast.Show(Words.NoticeMinimized, Words.NoticeMinimizedDetail, Theme.Accent,
                       delay: TimeSpan.FromMilliseconds(250),
                       anchor: Toast.Spot.Tray);
        }

        // Where the window was left is not a setting anybody presses Save for, so it is written
        // here instead — once, on the way out, rather than on every mouse move during a drag.
        try { _session.Config.Save(); }
        catch (Exception ex) { Log.Warn($"Could not save the window position: {ex.Message}"); }

        // The settings window is by far the largest thing this app ever allocates.
        ResourceTuning.ReleaseMemory();
        UpdateMenu();

        // Deliberately after the dialog has gone: in TV-only mode the desktop displays go
        // dark, and doing that with a modal window still up strands it on a dead screen.
        if (form.CloseGameRequested)
        {
            // Just the game. No session is running, so there is nothing to end — and Big Picture
            // goes with it because it was left minimized by the same swap that left the game, and
            // with nothing pointing back at it there would be no way to reach it again.
            _leftRunning = false;
            _swappedOnDisconnect = false;

            RunBackground("Closing the game", () =>
            {
                _hdr.CloseRunningGame();
                BigPictureLauncher.Close();
                CouchMode.Audio.GameAudio.Restore();
            });
        }
        else if (form.StartRequested)
        {
            // Resume rather than start, when there is something to resume into. Same decision the PS
            // button makes on the desktop, and made in the same place so the two cannot drift apart:
            // launching Big Picture over a game that is already running would put the shell in front
            // of it and the player on a menu they did not ask for.
            if (_leftRunning) ResumeOrStart("The Resume button was pressed");
            else LaunchBigPicture();
        }
        else if (form.RevertRequested)
        {
            RunBackground("Returning to the desktop", () => _session.ReturnToDesktop());
        }
    }

    private void ApplyConfig(AppConfig config)
    {
        ApplyShortcuts(config);
        _pointer.Configure(config);
        _front.Enabled = config.BigPictureAlwaysOnTop;

        // Re-apply the pin state live: turning always-on-top off mid-session should release Big
        // Picture at once, and turning it on should re-assert it.
        if (_session.IsInTvMode) _front.Begin();

        _session.Config = config;
        _hdr.Reconfigure(config);

        // Unconditional, and outside the block below.
        //
        // This was written inside the "did the chosen controller change" test, so it only took
        // effect when the *pad* selection changed as well. Change the connection rule on its own —
        // which is exactly what somebody does when they are trying it out — and the watcher went
        // on enforcing whatever it had been given at startup. The config on disk said one thing
        // and the running app another, with the log insisting only wireless controllers could
        // start a session long after that had been switched back off.
        _pads.Link = config.StartOnControllerLink;

        if (_pads.OnlyDeviceId != config.TriggerControllerId)
        {
            _pads.OnlyDeviceId = config.TriggerControllerId;

            Log.Info(config.TriggerControllerId.Length == 0
                ? "Any controller may now start a session."
                : $"Only {config.TriggerControllerId} may now start a session.");
        }

        if (NeedsPadWatching(config))
        {
            // Rebaseline on every save, so turning the setting on while a pad is already
            // switched on does not require unplugging it before anything responds.
            _pads.Start();
            _pads.Rebaseline();
        }
        else _pads.Stop();
        _watcher.Start();   // always watching; Big Picture is the session
        UpdateMenu();
    }

    /// <summary>
    /// Take a reading of the window layout, and act on any display that has come or gone.
    ///
    /// Stands down entirely while a session is running or a switch is in progress. The session has
    /// its own capture and restore, and two mechanisms both putting windows back would fight —
    /// which is the failure this app has spent a lot of effort removing elsewhere.
    /// </summary>
    private void SampleWindows()
    {
        if (_busy || _session.State != TvState.Desktop)
        {
            // Anything parked before a session belongs to the session restore now.
            WindowLayout.LayoutStore.ForgetParked();
            return;
        }

        try
        {
            var displays = Display.DisplayManager.AttachedBounds();
            if (displays.Count == 0) return;

            WindowLayout.LayoutStore.DisplaysChanged(displays);
            WindowLayout.LayoutStore.Sample(displays);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not sample the window layout: {ex.Message}");
        }
    }

    private void ObserveSteamWindow()
    {
        // Only while on the desktop: during a TV session the "Steam window" is Big Picture,
        // and after it closes we're mid-restore and would capture the geometry we're fixing.
        if (_busy || _session.State != TvState.Desktop) return;

        try
        {
            SteamWindowMemory.Observe(_session.Config);

            // Persist occasionally rather than every tick — this survives an app restart,
            // but it isn't worth writing to disk every two seconds.
            if (DateTime.UtcNow - _lastGeometrySave > TimeSpan.FromMinutes(2))
            {
                _lastGeometrySave = DateTime.UtcNow;
                _session.Config.Save();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Observing the Steam window failed: {ex.Message}");
        }
    }

    private void SaveDiagnostics()
    {
        try
        {
            var path = Display.DisplayDiagnostics.Write(_session.Config);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Writing diagnostics failed", ex);
            Notify(string.Format(Words.WarnDiagnosticsWriteFailed, ex.Message), ToolTipIcon.Error);
        }
    }

    /// <summary>Put the icon back after the shell has restarted.</summary>
    private void RestoreTrayIcon()
    {
        if (_exiting) return;

        try
        {
            _icon.Visible = false;
            _icon.Visible = true;

            Log.Info("Explorer restarted; the notification area icon was re-registered.");
        }
        catch (Exception ex) { Log.Warn($"Could not restore the tray icon: {ex.Message}"); }
    }

    private bool _exiting;

    private readonly BroadcastWatcher _broadcasts;

    /// <summary>
    /// Listens for the shell announcing that the taskbar has been recreated.
    ///
    /// A message-only window: there is nothing to show, only a broadcast to hear.
    /// </summary>
    /// <summary>
    /// Listens for the two broadcasts this app cares about.
    ///
    /// A plain top-level window, not a message-only one. That distinction matters and was
    /// previously wrong: Windows does not deliver broadcast messages to message-only windows,
    /// so the taskbar-restart notice was never actually arriving. The window is never shown —
    /// it has no WS_VISIBLE — so it costs nothing to have a real one.
    /// </summary>
    internal sealed class BroadcastWatcher : NativeWindow, IDisposable
    {
        private readonly Action _shellRestarted;
        private readonly Action _showSettings;
        private readonly Action _quit;

        private readonly uint _taskbarCreated;
        private readonly uint _showSettingsMessage;
        private readonly uint _quitMessage;

        private readonly Action<string, bool> _wokeUp;
        private IntPtr _displayNotify;

        public BroadcastWatcher(Action shellRestarted, Action showSettings, Action<string, bool> wokeUp,
                                Action quit)
        {
            _shellRestarted = shellRestarted;
            _showSettings = showSettings;
            _wokeUp = wokeUp;
            _quit = quit;

            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            _showSettingsMessage = RegisterWindowMessage(ShowSettingsMessage);
            _quitMessage = RegisterWindowMessage(QuitMessage);

            CreateHandle(new CreateParams());

            // Waking the screen is not the same event as waking the machine, and a controller
            // can be re-enumerated by either, so both are worth hearing about.
            var display = GUID_CONSOLE_DISPLAY_STATE;
            _displayNotify = RegisterPowerSettingNotification(Handle, ref display, 0);
        }

        /// <summary>
        /// The name a second copy of the app broadcasts on.
        ///
        /// RegisterWindowMessage turns a string into a message id that is the same for every
        /// process on the machine, which is exactly what two instances need to agree on
        /// without any other channel between them.
        /// </summary>
        public const string ShowSettingsMessage = "CouchSession.ShowSettings";

        /// <summary>
        /// The name a copy launched with --quit broadcasts on.
        ///
        /// Asking the running copy to leave, rather than killing it from outside. That matters
        /// because quite a lot of this app's job is putting things back: the power plan, the
        /// Xbox Game Bar, the display topology, the window layout. All of that hangs off exit
        /// handlers, and TerminateProcess runs none of them — a killed copy leaves the Game Bar
        /// switched off with nothing on the machine to say why.
        /// </summary>
        public const string QuitMessage = "CouchSession.Quit";

        /// <summary>Console display state as last reported: 0 off, 1 on, 2 dimmed.</summary>
        private uint _displayState = DisplayStateUnknown;

        private const uint DisplayStateUnknown = uint.MaxValue;

        /// <summary>True once we have been told the screen is off (asleep). Unknown counts as on.</summary>
        public bool DisplayIsOff => _displayState == 0;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (_taskbarCreated != 0 && m.Msg == _taskbarCreated) { _shellRestarted(); return; }
            if (_showSettingsMessage != 0 && m.Msg == _showSettingsMessage) { _showSettings(); return; }
            if (_quitMessage != 0 && m.Msg == _quitMessage) { _quit(); return; }

            if (m.Msg != WM_POWERBROADCAST) return;

            int what = (int)m.WParam;

            if (what is PBT_APMRESUMESUSPEND or PBT_APMRESUMEAUTOMATIC)
            {
                _wokeUp("The machine woke from sleep", true);
                return;
            }

            if (what != PBT_POWERSETTINGCHANGE || m.LParam == IntPtr.Zero) return;

            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(m.LParam);
            if (setting.PowerSetting != GUID_CONSOLE_DISPLAY_STATE) return;

            // 0 off, 1 on, 2 dimmed. Only the transition *to* on matters, and "transition" is
            // the word that was missing.
            //
            // RegisterPowerSettingNotification delivers the current value immediately, before
            // anything has changed — that is how it is meant to work, so a caller knows the state
            // without having to wait for it to move. Reading that first delivery as an event
            // meant every launch announced that the display had just come back on, which put the
            // controller watcher into its twelve-second settling period at the exact moment the
            // app opened. Plug a pad in during those seconds and it was deliberately ignored, and
            // the rebaseline at the end then adopted it as part of the furniture — so it never
            // fired at all, and the second attempt always worked. Which is precisely how it was
            // reported: never on the first try.
            //
            // Recording the state without acting on it costs nothing and makes the first
            // delivery do what it is for.
            uint before = _displayState;
            _displayState = setting.Data;

            if (before == DisplayStateUnknown) return;
            if (setting.Data == 1 && before != 1) _wokeUp("The display came back on", false);
        }

        public void Dispose()
        {
            if (_displayNotify != IntPtr.Zero)
            {
                UnregisterPowerSettingNotification(_displayNotify);
                _displayNotify = IntPtr.Zero;
            }

            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_APMRESUMESUSPEND = 0x0007;
        private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;

        private static Guid GUID_CONSOLE_DISPLAY_STATE =
            new("6fe69556-704a-47a0-8f24-c28d936fda47");

        [StructLayout(LayoutKind.Sequential)]
        private struct POWERBROADCAST_SETTING
        {
            public Guid PowerSetting;
            public uint DataLength;
            public byte Data;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient,
                                                                      ref Guid setting, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    }

    private void ExitApp()
    {
        _exiting = true;
        _steamObserver.Stop();
        _windowSampler.Stop();
        _idleWatch.Stop();
        _guideWatch.Stop();
        _pads.ResumeReports();   // never leave the pad's input released after we are gone
        _watcher.Stop();
        try { _session.Config.Save(); } catch { /* best effort on the way out */ }
        SafeRevert();
        Application.Exit();
    }

    /// <summary>
    /// How long everything must sit untouched before the controller's raw input is released.
    ///
    /// This is a straight trade and the balance matters. While the pad is released the app cannot see
    /// it at all — that is the entire point, since a DualSense streaming reports pins Windows' input
    /// clock and the display would never sleep — but it also means the controller hotkey is not being
    /// watched for, and pressing it does nothing until a mouse or key wakes the subscription back up.
    ///
    /// Five seconds put the pad to sleep between one action and the next during ordinary use, so the
    /// hotkey routinely missed the first press. Ninety seconds keeps it awake right through a normal
    /// sit-down and only lets go once the couch is genuinely empty. The cost is that the display's own
    /// idle countdown starts ninety seconds later than it otherwise would, which against a screen
    /// timeout measured in minutes is not worth noticing.
    /// </summary>
    private static readonly TimeSpan ControllerReleaseAfter = TimeSpan.FromSeconds(90);

    /// <summary>After a session ends, the pad is kept live this long so it can drive the desktop
    /// straight away — the user was just using it, and the restored settings window is a launcher the
    /// pointer can steer. Using it keeps it live past this; walking away lets it release as usual.
    /// Kept short: while it runs the pad still pins the input clock, so a long grace was what stalled
    /// the machine for half a minute before it could sleep once a session ended.</summary>
    private static readonly TimeSpan AfterSessionGrace = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The fuse for a pad we are waiting on.
    ///
    /// Was five minutes, on the theory that a longer hold let the controller sleep. The log settled
    /// that: twelve minutes with the subscription released and the pad stayed awake regardless, so the
    /// extra hold bought nothing and cost a hotkey that was dead for its first press after a quiet
    /// spell. Back to the same ninety seconds everything else uses — the one real benefit of letting
    /// go is the display's idle timer, and that argues for the shorter fuse.
    /// </summary>
    private static readonly TimeSpan TriggerReleaseAfter = ControllerReleaseAfter;

    /// <summary>
    /// How long the pad is given to actually fall asleep once its input has been let go.
    ///
    /// Letting go is only worth anything if the controller then sleeps: a pad that is still connected
    /// afterwards is costing exactly as much battery as before, and the only thing the release has
    /// achieved is making its buttons invisible. So this is checked rather than assumed, and a pad that
    /// has not gone away by the end of it gets its stream back.
    /// </summary>
    private static readonly TimeSpan SleepCheckAfter = TimeSpan.FromMinutes(3);

    private DateTime _triggerReleasedAt;
    private DateTime _triggerRetryAt = DateTime.MinValue;

    private Point _idleCursor;
    private DateTime _lastCursorActivity = DateTime.UtcNow;
    private uint _inputTickAtRelease;
    private DateTime _lastIdleLog = DateTime.MinValue;
    private bool _wasInSession;
    private DateTime _sessionEndedAt = DateTime.MinValue;

    /// <summary>
    /// Let the display sleep despite a connected controller.
    ///
    /// A pad that streams reports continuously — a DualSense does, even sitting still — resets
    /// Windows' last-input clock for as long as the app is subscribed to it, so the display idle
    /// timer never runs out and GetLastInputInfo is useless for deciding when to let go (it reads
    /// 0s forever; the log proved it). So idleness is judged from signals the stream cannot fake:
    /// the real cursor position, and actual *changes* in the pad's controls. When both have sat
    /// still long enough, the subscription is released — from that moment the pad no longer pins
    /// the clock and the display sleeps on its own schedule. Once released, GetLastInputInfo is
    /// trustworthy again, and the first real input (mouse, keyboard) resumes the subscription.
    /// Keyboard-only activity while subscribed is the one thing this cannot see, and it does not
    /// need to: releasing during typing is harmless, since the typing itself keeps Windows' own
    /// timer fresh, and the resume check brings the pad back within a few seconds.
    /// </summary>
    private void CheckControllerIdle()
    {
        bool inSession = _busy || _session.IsInTvMode;
        if (_wasInSession && !inSession) _sessionEndedAt = DateTime.UtcNow;   // note the moment it ends
        _wasInSession = inSession;

        if (inSession)
        {
            _pads.ResumeReports();
            return;
        }

        // The real pointer: moved means a person, regardless of what the pad is streaming. (The
        // app's own injected moves land here too, deliberately — driving the cursor by pad *is* use.)
        var now = DateTime.UtcNow;

        bool cursorMoved = GetCursorPos(out var p) && p != _idleCursor;
        if (cursorMoved) { _idleCursor = p; _lastCursorActivity = now; }

        // Just off a session: keep the pad live for a short grace so it works the instant you pick it
        // up, without a mouse nudge to wake it. Marking the cursor active too means it does not release
        // the moment the grace ends; genuine idleness (grace passed, nothing touched) still lets go.
        if (now - _sessionEndedAt < AfterSessionGrace)
        {
            _pads.ResumeReports();
            _lastCursorActivity = now;
            return;
        }

        if (_pads.ReportsReleased)
        {
            // Released, so the pad no longer pins Windows' input clock and it reads true again. Take
            // the pad back only on genuinely new input: a real cursor move, or the clock advancing
            // clearly past where it stood when we let go (a key press). Not merely "recent" — right
            // after release the clock still shows the last pad report, which is not a person.
            // Never take the pad back while the screen is off: it was released because everything is
            // idle, and re-subscribing wakes the screen from the pad's own stream, which became an
            // endless wake loop once the display started sleeping on its own.
            // Did letting go work? Asked rather than assumed.
            //
            // The whole reason a pad we are waiting on can be released at all is that a sleeping
            // controller disconnects, and its return wakes the subscription again (DevicesChanged) in
            // time for the release of the very button that woke it. A pad still sitting there
            // connected has not done that, so the release has bought no battery and cost us the
            // button — the worst of both — and is undone here.
            if (_triggerReleasedAt != default && now - _triggerReleasedAt > SleepCheckAfter)
            {
                _triggerReleasedAt = default;

                if (PadStillAttached())
                {
                    Log.Info("The controller was still connected three minutes after its input was "
                           + "released, so releasing it saved nothing and only hid its buttons"
                           + (_broadcasts.DisplayIsOff
                              ? " — leaving it released for now because the screen is off."
                              : " — taking the input back."));

                    // Not while the screen is off. Re-subscribing wakes it from the pad's own stream,
                    // which is the wake loop that made the display unable to stay asleep at all. The
                    // check below brings the pad back the moment there is real input.
                    if (_broadcasts.DisplayIsOff) return;

                    _triggerRetryAt = now + TriggerReleaseAfter;
                    _pads.ResumeReports();
                    return;
                }

                Log.Info("The controller went to sleep after its input was released. Waking it with "
                       + "the PS/Guide button reconnects it, which takes its input back in time for "
                       + "the button's release to register.");
            }

            // Below the sleep check, not above it.
            //
            // This guard used to come first, and it hid the answer: the screen goes off during exactly
            // the quiet stretch the check is about, so for twelve minutes the log said nothing at all
            // about whether releasing the pad had achieved anything. The check reads presence and
            // writes a log line — neither of which can wake a screen — so it belongs above this.
            if (_broadcasts.DisplayIsOff) return;

            uint advanced = unchecked(LastInputTick() - _inputTickAtRelease);
            if (cursorMoved || (advanced > 750 && advanced < 0x8000_0000))
            {
                Log.Info($"Resuming controller input: cursor {(cursorMoved ? "moved" : "still")}, "
                       + $"input clock +{advanced}ms since release, screen "
                       + (_broadcasts.DisplayIsOff ? "off" : "on") + ".");

                // Same reasoning as in WakePadForInput: the pad's activity clock is stale by exactly as
                // long as it was released for, so it has to be given a fresh run before it counts as
                // idle again.
                _triggerRetryAt = now + TriggerReleaseAfter;
                _triggerReleasedAt = default;
                _pads.ResumeReports();
            }
            return;
        }

        // Release on the pad's OWN idleness (real stick/button changes), never the cursor. Drift
        // nudges the cursor through the pointer feature, so judging by the cursor let a drifting pad
        // keep resetting its own idle timer and never let go: the loop that pinned the PC awake. Drift
        // is a steady offset, not a change, so it never counts; a stick genuinely moved does.
        bool padIdle = now - HidPad.LastActivityUtc > ControllerReleaseAfter;

        if (now - _lastIdleLog > TimeSpan.FromSeconds(15))
        {
            _lastIdleLog = now;
            Log.Info($"Idle check: cursor {(now - _lastCursorActivity).TotalSeconds:0}s, "
                   + $"pad {(now - HidPad.LastActivityUtc).TotalSeconds:0}s; controller input active.");
        }

        if (!padIdle) return;

        // The stream is only ever let go when something else can still read the pad.
        //
        // Releasing it makes a HID controller invisible: no reports, no buttons, no hotkey and no PS
        // button. Direct polling was meant to cover that gap, and it may well do so — but "may" is not
        // good enough for the one control that ends a session, and relying on it unproven left the pad
        // dead. So it has to demonstrate it can read this controller first. Until it has, the stream
        // stays open whenever the pad is something we are waiting on, and the only cost is the
        // display's idle timer not running while a controller is connected.
        if (PadIsATrigger() && !HidPoll.ProvenOn(_pads))
        {
            // Held for longer, but not forever.
            //
            // It used to be forever, and that was the wrong call: the stream stayed open for the whole
            // life of the app, so the controller never slept and quietly ran itself flat. The button
            // being seen matters, but not at the price of the pad being dead when it is next picked up.
            //
            // Ninety seconds of an untouched pad, then let go — because a controller that sleeps
            // disconnects, and a disconnected pad announces its own return the moment the PS button
            // wakes it. Since a session is started by the button coming back *up*, the wake, the
            // reconnect and the resubscribe all happen during the press, and the release still lands.
            //
            // This said five minutes, which was the old value of TriggerReleaseAfter. That constant's
            // own summary records why it went back to ninety seconds and what the log proved; this
            // sentence was left behind saying the opposite, three lines from the field it describes.
            if (now - HidPad.LastActivityUtc < TriggerReleaseAfter || now < _triggerRetryAt)
            {
                if (now - _lastHeldForTriggerLog > TimeSpan.FromMinutes(10))
                {
                    _lastHeldForTriggerLog = now;
                    Log.Info("Pad idle, but it starts a session and direct polling has not proven "
                           + "itself on this controller — holding its input open a while longer.");
                }
                return;
            }

            Log.Info($"Controller untouched for {TriggerReleaseAfter.TotalMinutes:0} minutes; releasing "
                   + "its input so it can sleep. Pressing PS/Guide wakes it and reconnects it, which "
                   + "brings its input back before the button is let go.");

            _triggerReleasedAt = now;
        }

        _pads.SuspendReports();
        _inputTickAtRelease = LastInputTick();   // the frozen baseline; only real input moves it now
    }

    private DateTime _lastHeldForTriggerLog = DateTime.MinValue;

    /// <summary>
    /// Whether a controller is physically present, asked of the device stack.
    ///
    /// Deliberately not PadShortcut.AnyConnected here: that falls back to the raw-input view, which is
    /// the very thing that has just been released, so it cannot answer this question. SetupDi's present
    /// -device list is maintained by Windows itself and is told when a Bluetooth link drops.
    /// </summary>
    private static bool PadStillAttached()
    {
        try { return Games.ControllerWatcher.Attached().Count > 0; }
        catch { return false; }
    }

    /// <summary>
    /// Whether anything is waiting on the controller while we sit on the desktop — in which case its
    /// input cannot be handed back unless something else can read it, because letting go means not
    /// seeing the press that starts a session.
    /// </summary>
    private bool PadIsATrigger()
    {
        var config = _session.Config;

        return config.EndSessionOnGuideButton
            || config.ShortcutController != 0
            || config.ShortcutControllerHdr != 0
            || config.StartOnControllerConnect;
    }

    // ---- the PS / Guide button ----

    /// <summary>Whether the PS / Guide button was down on the previous poll, so a hold fires once.</summary>
    private bool _guideHeld;

    /// <summary>When the guide button went down, or default while it is up.</summary>
    private DateTime _guideDownSince;

    /// <summary>The last moment it was seen down, and how long it had been down by then.</summary>
    private DateTime _guideLastDown;
    private TimeSpan _guideHeldFor;

    /// <summary>
    /// Long enough to be a power-off rather than a press.
    ///
    /// An Xbox pad needs about six seconds of holding and a DualSense about ten, so anything over a
    /// second is already well clear of a tap. Kept low deliberately: the cost of being generous here
    /// is treating a slow press as deliberate, and the two answers this chooses between are "come
    /// back to the desktop" and "do nothing" — neither of which closes anything.
    /// </summary>
    private static readonly TimeSpan PowerOffHold = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// How recently the button must have been held for a disconnect to count as caused by it.
    ///
    /// The pad powers off mid-hold, so the last poll that saw the button down is within one poll
    /// interval of the device vanishing. Two and a half seconds is generous enough to survive a slow
    /// device-removal notification and short enough that a hold half a minute ago cannot be blamed
    /// for an unrelated battery dying.
    /// </summary>
    private static readonly TimeSpan PowerOffWindow = TimeSpan.FromMilliseconds(2500);

    /// <summary>
    /// Keep a record of how long the guide button has been held, and when it was last seen down.
    ///
    /// Runs on every poll regardless of what any setting says, because it is evidence rather than
    /// behaviour.
    /// </summary>
    private void NoteGuideHold(bool down)
    {
        var now = DateTime.UtcNow;

        if (!down) { _guideDownSince = default; return; }

        if (_guideDownSince == default) _guideDownSince = now;

        // Held for, rather than held since: the duration has to survive the button going away, and
        // when a controller powers off there is no release to record one from.
        _guideHeldFor = now - _guideDownSince;
        _guideLastDown = now;
    }

    /// <summary>
    /// Whether the controller that just vanished was switched off on purpose.
    ///
    /// Powering a wireless controller off means holding the guide button — six seconds on an Xbox
    /// pad, about ten on a DualSense — and the pad reports that button held right up until it stops
    /// reporting anything. So a disconnect arriving moments after a long hold has exactly one
    /// ordinary explanation. A flat battery or a lost link produces a disconnect with no button held
    /// at all, which is the difference this reads.
    ///
    /// One rule for every controller, wired or wireless. How a controller is attached says nothing
    /// about why it left, and a cable pulled out looks exactly like a battery dying — no button held
    /// in either case, which is what this reads.
    ///
    /// This is a judgement, not a fact, and it is deliberately used only for a choice between two
    /// harmless answers — neither closes a game. The awkward cases are honest ones: a controller
    /// with no hold-to-power-off never looks deliberate, and a battery dying during the seconds
    /// somebody happens to be holding the button looks deliberate when it was not. Both are rare and
    /// neither costs anything.
    /// </summary>
    private bool WasPoweredOff(ControllerDevice device)
    {

        // No special case for a cable, and there were two of them here in turn — first claiming an
        // unplug as always deliberate, then as always an accident. Both were wrong in the same way:
        // they answered from how the controller was attached, which says nothing about intent, when
        // the evidence was already sitting right here. A cable pulled out produces a disconnect with
        // no button held, which is exactly what a flat battery produces, and the test below reads
        // both as what they are. One rule, applied to every controller, and nothing to keep in step.
        var now = DateTime.UtcNow;

        bool held = _guideHeldFor >= PowerOffHold && now - _guideLastDown <= PowerOffWindow;

        Log.Info(held
            ? $"{device.Name} vanished {Math.Round((now - _guideLastDown).TotalMilliseconds)}ms after "
              + $"a {Math.Round(_guideHeldFor.TotalMilliseconds)}ms guide hold; treating it as "
              + "switched off on purpose."
            : $"{device.Name} vanished with no guide hold before it; treating it as an unexpected "
              + "disconnect.");

        return held;
    }

    /// <summary>
    /// Whether Steam's overlay was already open when the button went down — which makes the press a
    /// request to close the overlay rather than anything to do with us. See CheckGuideButton.
    /// </summary>
    private bool _overlayOpenAtPress;


    /// <summary>
    /// Set when the user dropped to the desktop but left the session's windows alive — a game still
    /// running, or Big Picture still open. It is the difference between "there is something to go back
    /// to" and an ordinary desktop, and it is what makes the PS button mean *resume* out here rather
    /// than nothing at all. Cleared the moment a session starts or those windows are deliberately
    /// closed, so the button can never resurrect a session that genuinely ended.
    /// </summary>
    private bool _leftRunning;

    /// <summary>Set alongside a resume request, so the windows are restored once the TV is live.</summary>
    private bool _resumeWhenBack;

    /// <summary>
    /// The desktop we are sitting on was reached by a controller dropping out, not by choice.
    ///
    /// Kept apart from <see cref="_leftRunning"/> because only this one may resume on its own. A user
    /// who chose "Swap to desktop" went to the desktop to do something there, and dragging them back
    /// to the television because a pad woke up would be the app overruling a decision they had just
    /// made. A user whose battery died did not decide anything.
    /// </summary>
    private bool _swappedOnDisconnect;

    /// <summary>When the watch began, for the grace below.</summary>
    private DateTime _guideArmedAt;

    /// <summary>
    /// Big Picture needs a moment to get its overlay up after the button goes down. The prompt waits
    /// that long so it lands on top of the overlay rather than being covered by it a beat later.
    /// </summary>
    private const int GuideOverlayMs = 450;

    /// <summary>Presses within this of the session starting are ignored — see the grace note below.</summary>
    private static readonly TimeSpan GuideGrace = TimeSpan.FromSeconds(2);

    /// <summary>The pending wait for Steam's overlay, so a second press replaces it rather than
    /// queueing a second teardown behind it.</summary>
    private System.Windows.Forms.Timer? _guideSettle;

    /// <summary>
    /// End the session when the PS / Guide button is pressed during one.
    ///
    /// This rides along with Steam rather than replacing it. The button is only *read*; it is not
    /// intercepted, swallowed or synthesised — none of which is possible from user space anyway, and
    /// all of which is the sort of input meddling worth staying well clear of. Steam receives the press
    /// exactly as it always has and opens its Big Picture overlay, which is the one thing that reliably
    /// takes the pad off a running game, because Steam owns the pad and this app does not. A moment
    /// later the prompt appears on top of that overlay.
    ///
    /// So the game is already paused behind Steam's own UI by the time there is anything to answer —
    /// which is what makes this worth doing over any amount of foreground juggling on our side.
    /// </summary>
    private void CheckGuideButton()
    {
        WakePadForInput();

        bool down;
        try { down = PadShortcut.IsControlHeld(PadControl.Home); }
        catch { return; }   // a pad vanishing mid-poll is not worth a crash

        NoteGuideHold(down);

        // Sampled before this gate, not after.
        //
        // The hold record above is what tells a controller being switched off from one whose battery
        // died, and that has to work whether or not the guide button is also a session shortcut —
        // those are unrelated settings. It used to return here first, which meant switching the
        // shortcut off silently took the power-off detection with it.
        if (!_session.Config.EndSessionOnGuideButton) { _guideHeld = false; return; }

        // Mid-transition: track the button so a press made during it cannot fire the moment it clears,
        // but act on nothing.
        if (_busy) { _guideHeld = down; return; }

        // Acted on when the button comes back *up*, not when it goes down.
        //
        // Holding this button is its own gesture on both consoles — the power menu on an Xbox pad, the
        // shutdown sheet on a DualSense — and Steam gives a hold its own meaning too. Firing on the way
        // down put the prompt on screen the instant the button was touched, over the top of whatever
        // the hold was about to do. Waiting for the release leaves a hold free to be a hold, and costs
        // nothing on a quick tap.
        bool released = !down && _guideHeld;

        // A release only counts while the pad is still there.
        //
        // Holding this button is how a DualSense is switched off. The pad then vanishes mid-hold, the
        // button reads as no longer held, and that is byte-for-byte identical to letting go — so
        // switching the controller off started a session, which the log caught in the same second:
        // "Controller disconnected" and "PS/Guide button pressed on the desktop" back to back.
        //
        // Asking whether any pad is still connected separates the two cases, because the one thing that
        // is different about a power-off is that there is nothing left to have released the button.
        if (released && !PadShortcut.AnyConnected())
        {
            Log.Info("PS/Guide button went up because the controller disconnected, not because it was "
                   + "released — ignoring it.");

            _guideHeld = false;
            return;
        }

        // Sampled as the button goes down, not when it comes up: since the trigger is the release,
        // Steam has had the whole duration of the press to open its overlay, and asking afterwards
        // would find an overlay opened by this very press and suppress the prompt we meant to show.
        //
        // Only what can actually be observed. There was a second source here — remembering that our
        // last press opened an overlay and assuming the next one closes it — and it was wrong often
        // enough to be worse than nothing: closing the overlay any other way, with B, left the memory
        // saying "open" and the next press was swallowed. A guess that silently eats the button that
        // starts a session is not worth the case it covers, and the case it covered is handled below
        // anyway, by the prompt itself being on screen.
        if (down && !_guideHeld) _overlayOpenAtPress = BigPictureLauncher.OverlayIsOpen();

        _guideHeld = down;

        if (!released) return;

        // A press just either side of a switch is the one that caused the switch arriving late, not a
        // request to undo it.
        if (DateTime.UtcNow - _guideArmedAt < GuideGrace) return;

        // The prompt is up, so it went up over an overlay this button opened. Pressing it again is
        // "put all this away": Steam closes its overlay and the prompt goes with it, keeping what is
        // on screen and what the button does in step.
        if (_endPrompt is { IsDisposed: false } open)
        {
            Log.Info("PS/Guide button pressed with the session prompt up; dismissing it with the overlay.");
            open.Close();
            return;
        }

        if (_session.IsInTvMode) { GuideEndsSession(); return; }

        GuideStartsSession();
    }

    /// <summary>
    /// Take the pad's raw input back the moment anything at all happens, rather than waiting up to
    /// three seconds for the idle check to come round.
    ///
    /// This is what makes acting on the release work through an idle release. While released the app
    /// cannot see a HID pad's buttons — but the press still reaches Windows and moves its input clock,
    /// so the clock advancing is a reliable "someone just did something". Running at this timer's rate,
    /// the subscription is back within a tick or so of the button going down, comfortably ahead of the
    /// same button coming back up. The press wakes it; the release is caught.
    ///
    /// Skipped while the screen is off, matching the idle check: it was released because everything was
    /// idle, and re-subscribing there wakes the screen from the pad's own stream.
    /// </summary>
    private void WakePadForInput()
    {
        if (!_pads.ReportsReleased || _broadcasts.DisplayIsOff) return;

        uint advanced = unchecked(LastInputTick() - _inputTickAtRelease);
        if (advanced <= 250 || advanced >= 0x8000_0000) return;

        Log.Info($"Input seen {advanced}ms after the pad was released; taking it back so a button "
               + "press is not missed.");

        // And keep it for a while. Its own activity clock has not moved since before the release, so
        // without this the very next idle check would find a five-minute-old pad and let go again
        // immediately — undoing the wake before the button it was woken for could be pressed.
        _triggerRetryAt = DateTime.UtcNow + TriggerReleaseAfter;
        _triggerReleasedAt = default;
        _pads.ResumeReports();
    }

    /// <summary>
    /// The in-session press: Steam is opening its Big Picture overlay on the same press, so wait for
    /// that and put the prompt on top of it.
    /// </summary>
    private void GuideEndsSession()
    {
        // The overlay was already up when the button went down, so this press is closing it — that is
        // what the button does in there, and it is how most people get out. Putting the prompt on
        // screen for that would be answering a question nobody asked, on the way out of a menu they
        // are leaving.
        if (_overlayOpenAtPress)
        {
            Log.Info("PS/Guide button pressed with Steam's overlay already open; "
                   + "that press closes the overlay, so no prompt.");
            return;
        }

        // One wait at a time.
        //
        // The 450ms pause that lets Steam's overlay come up is long enough to press the button again
        // inside it, and each press used to start its own timer. Two timers meant two teardowns queued
        // from what the user experienced as one action — which is how a press could produce no prompt
        // (the second teardown arriving on a session the first had already dealt with), and how the
        // second press appeared to cancel the first. A press while one is already pending replaces it.
        if (_guideSettle is not null)
        {
            Log.Info("PS/Guide button pressed again while the overlay was still coming up; "
                   + "the earlier press is dropped and this one taken instead.");
            _guideSettle.Stop();
            _guideSettle.Dispose();
            _guideSettle = null;
        }

        // No game running means nothing to wait for.
        //
        // The pause exists for one reason: with a game up, this press also opens Steam's overlay, and
        // the overlay is what takes the pad off the game — so the prompt has to arrive after it or be
        // covered a beat later. In Big Picture with no game, that press does not open an overlay at
        // all. It navigates Steam's own menu, there is nothing for the prompt to land on top of, and
        // waiting 450ms for it was a delay bought with nothing.
        if (_hdr.RunningGameWindow() == IntPtr.Zero)
        {
            Log.Info("PS/Guide button pressed in Big Picture with no game running; "
                   + "showing the prompt straight away.");

            EndSession(bigPictureStillUp: true);
            return;
        }

        Log.Info("PS/Guide button pressed during a session; Steam is opening its overlay, "
               + "putting the session prompt over it.");

        var settle = new System.Windows.Forms.Timer { Interval = GuideOverlayMs };
        _guideSettle = settle;

        settle.Tick += (_, _) =>
        {
            settle.Stop();
            settle.Dispose();
            if (ReferenceEquals(_guideSettle, settle)) _guideSettle = null;

            // Re-checked rather than assumed: the overlay delay is long enough for the session to have
            // ended by other means in the meantime.
            if (!_busy && _session.IsInTvMode) EndSession(bigPictureStillUp: true);
        };
        settle.Start();
    }

    /// <summary>
    /// The desktop press: go to the couch.
    ///
    /// Two shapes of the same thing. If a session was only minimized away — a game still running behind
    /// the desktop — this goes back to exactly that, game and all. Otherwise it starts a fresh session,
    /// which opens Big Picture.
    ///
    /// Acting on the button directly is what makes it one press. Steam opens Big Picture from the same
    /// press on its own, and waiting for the watcher to notice that window meant the first press only
    /// ever got Steam's attention and a second was needed to get ours.
    /// </summary>
    private void GuideStartsSession()
    {
        if (!Configured())
        {
            Log.Info("PS/Guide button pressed on the desktop, but the app is not set up yet; ignoring.");
            return;
        }

        ResumeOrStart("PS/Guide button pressed on the desktop");
    }

    /// <summary>
    /// Go back to a session waiting behind the desktop, or start a fresh one when there is none.
    ///
    /// Shared by the PS button and the Resume button in the settings footer. One place on purpose:
    /// the decision is subtle — the flag alone is not enough, because the game may have been closed
    /// from the desktop since, and resuming into nothing is worse than starting fresh — and two
    /// copies of a subtle decision is two copies to keep in step.
    /// </summary>
    private void ResumeOrStart(string why)
    {
        bool resumable = _leftRunning
                      && (_hdr.RunningGameWindow() != IntPtr.Zero
                          || BigPictureLauncher.FindWindow() != IntPtr.Zero);

        _leftRunning = false;
        _swappedOnDisconnect = false;
        _resumeWhenBack = resumable;

        Log.Info(resumable
            ? $"{why} with the session's windows still up; going back to it."
            : $"{why}; starting a session.");

        ToggleMode();
    }

    private static uint LastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? info.dwTime : (uint)Environment.TickCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    // ---- plumbing ----

    private bool Configured()
    {
        if (_session.Config.IsConfigured) return true;

        Notify(Words.NoticeNotSetUp, ToolTipIcon.Info);
        OpenSettings();
        return _session.Config.IsConfigured;
    }

    private void RunBackground(string what, Action work)
    {
        _busy = true;
        UpdateMenu();

        Task.Run(() =>
        {
            try
            {
                work();
            }
            catch (Session.TvNotLiveException ex)
            {
                // The failsafe fired and everything is already back. This message is written
                // for the user and explains what to fix, so show it properly rather than
                // flashing it past in a balloon.
                Log.Error($"{what}: {ex.Message}");
                OnUi(() => MessageBox.Show(ex.Message, AppInfo.Name,
                                           MessageBoxButtons.OK, MessageBoxIcon.Warning));
            }
            catch (Exception ex)
            {
                Log.Error($"{what} failed", ex);
                OnUi(() => Notify(string.Format(Words.WarnBackgroundFailed, what, ex.Message),
                                  ToolTipIcon.Error));
            }
            finally
            {
                OnUi(() => { _busy = false; UpdateMenu(); });
            }
        });
    }

    /// <summary>
    /// Give the machine back exactly as it was borrowed.
    ///
    /// Wired to every exit path there is — a clean quit, the process ending, and Windows
    /// signing out — because the settings this app changes are not ones a user would think to
    /// look for. Somebody whose Game Bar stopped working a week ago will not connect that to a
    /// utility they installed for their television.
    /// </summary>
    private void HandBack()
    {
        try { _keyShortcut.Dispose(); } catch (Exception ex) { Log.Warn($"Shortcut release failed: {ex.Message}"); }
        try { _padShortcut.Dispose(); } catch (Exception ex) { Log.Warn($"Pad shortcut release failed: {ex.Message}"); }
        try { _keyHdr.Dispose(); } catch (Exception ex) { Log.Warn($"HDR hotkey release failed: {ex.Message}"); }
        try { _padHdr.Dispose(); } catch (Exception ex) { Log.Warn($"HDR pad hotkey release failed: {ex.Message}"); }
        try { _pointer.Dispose(); } catch (Exception ex) { Log.Warn($"Pointer control release failed: {ex.Message}"); }
        try { _front.Dispose(); } catch (Exception ex) { Log.Warn($"Big Picture front guard release failed: {ex.Message}"); }

        SafeRevert();

        // The sound goes back before this process does. Same reasoning as the displays above it: a
        // change this app made to the machine must not outlive the app that made it.
        try { CouchMode.Audio.GameAudio.Restore(); }
        catch (Exception ex) { Log.Warn($"Unmuting the game failed: {ex.Message}"); }
    }

    private void SafeRevert()
    {
        try { _hdr.RestoreNow(); } catch (Exception ex) { Log.Warn($"HDR restore failed: {ex.Message}"); }

        try { _session.ReturnToDesktop(); }
        catch (Exception ex) { Log.Error("Revert on exit failed", ex); }
    }

    private void OnUi(Action action)
    {
        if (_sync.IsHandleCreated && _sync.InvokeRequired) _sync.BeginInvoke(action);
        else action();
    }

    private void UpdateMenu()
    {
        bool tv = _session.IsInTvMode;

        _statusItem.Text = _busy ? "Switching…" : tv ? "On the TV" : "On the desktop";
        _toggleItem.Text = tv ? "Return to the desktop" : "Switch to the TV";
        _toggleItem.ToolTipText = tv
            ? "Restore your displays, audio and window layout."
            : "Switch your display and audio over to the TV.";

        _toggleItem.Enabled = !_busy;
        _launchItem.Enabled = !_busy;
        _settingsItem.Enabled = !_busy;

        var previous = _icon.Icon;
        _icon.Icon = AppIcon.ForTray(active: tv);
        previous?.Dispose();
        _icon.Text = _busy ? "Couch Session — switching…"
                   : tv ? "Couch Session — on the TV"
                        : "Couch Session — on the desktop";
    }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppConfig.Directory)
            {
                UseShellExecute = true,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Opening the data folder failed", ex);
        }
    }

    /// <summary>
    /// The tray balloon, for the few things said outside a toast.
    ///
    /// Gated the same way everything else is. These are notifications by any reading — Windows draws
    /// them in the same corner — and a settings page that lists every kind the app shows would be
    /// lying if this one carried on regardless. Info goes with the confirmations, since the only one
    /// is "opening settings for you"; warnings and errors are problems.
    /// </summary>
    private void Notify(string message, ToolTipIcon severity)
    {
        var config = _session.Config;

        bool wanted = severity == ToolTipIcon.Info
                          ? config.ShowActionNotifications
                          : config.ShowProblemNotifications;

        if (!config.ShowNotifications || !wanted) return;

        _icon.ShowBalloonTip(4000, "Couch Session", message, severity);
    }


    public void Dispose()
    {
        _exiting = true;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _broadcasts.Dispose();
        _pads.Dispose();
        _hdr.Dispose();
        _landing.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _watcher.Dispose();
        _steamObserver.Dispose();
        _windowSampler.Dispose();
        _idleWatch.Dispose();
        _guideWatch.Dispose();
        _sync.Dispose();
    }
}

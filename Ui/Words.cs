namespace CouchMode.Ui;

/// <summary>
/// Every word the app says to you, in one place. Edit here, rebuild, done.
///
/// ─────────────────────────────────────────────────────────────────────────────────────────
///  HOW TO CHANGE THE WORDING
///
///  1. Find the line you want below and edit the text between the quotation marks.
///  2. Rebuild.
///
///  That is the whole process. A few rules worth knowing:
///
///   • Keep the quotation marks. Only change what is between them.
///   • Do not change the name on the left of the "=" — that is how the app finds the text.
///   • For long text split across several lines, each piece has its own quotation marks and
///     the lines are joined end to end. Mind the trailing space at the end of a piece, or
///     two words will run together.
///   • A quotation mark inside your text has to be written as \" — a backslash then a quote.
///   • The build will refuse to run if something is wrong, so a mistake here cannot break
///     the app for you. Read the error, fix the line, build again.
///
///  Names ending in "Why" are the small gray explanation under a setting's title. The name
///  before it is the bold heading. So AutoHdr and AutoHdrWhy are the two lines of one row.
///
///  Log messages are deliberately not here — they stay next to the code that writes them,
///  where they are most use when something goes wrong.
/// ─────────────────────────────────────────────────────────────────────────────────────────
/// </summary>
internal static class Words
{
    // Shown next to a setting's title, coloured green (recommended) or red (not recommended) by the
    // titleEmphasis passed with it.
    public const string BadgeRecommended = " **(Recommended)**";
    public const string BadgeNotRecommended = " **(Not recommended)**";

    // ── The window itself ─────────────────────────────────────────────────────────

    public const string PageDisplayAudio = "Display & Audio";
    public const string PageDisplayAudioWhy = "Choose the TV and speakers your couch sessions move to.";
    public const string PageSteam = "Steam";
    public const string PageSteamWhy = "How couch mode and Steam Big Picture work together.";
    public const string PageController = "Controller";
    public const string PageControllerWhy = "Which controller starts a session, and using one as a mouse.";

    public const string PageShortcuts = "Hotkeys";
    public const string PageShortcutsWhy = "Key and button combinations that work anywhere, including over a game.";

    public const string HotkeyHdrNote =
        "The HDR hotkeys act on your **main display only** — the TV during a session, or your "
            + "monitor at the desk. Every other display is left alone.";

    public const string ShortcutHdr = "Turn HDR on and off";
    public const string ShortcutHdrWhy =
        "Toggles HDR on your main display, whether a session is running or not — for the games and "
            + "apps that look wrong in HDR, without going into Windows settings. **Turn it on just "
            + "before you launch a game (or while one is running) and that game is remembered**: added "
            + "to the HDR Switching list so HDR comes on for it by itself next time, and off again when it "
            + "closes. Before launching is the better order — most games read the display at startup.";

    public const string PageAutoHDR = "HDR Switching";
    public const string PageAutoHDRWhy = "Switches your display's HDR on while a game runs, then back off when it closes.";
    public const string PagePerformance = "Performance";
    // Was: "then put your settings back when it ends" — a blanket promise four of the seven settings on
    // the page do not keep. Game Mode is only ever switched on (PerformanceTuning.Restore says so
    // outright), the Game Bar is held for the app's lifetime, and UAC and the firewall stay however
    // they are left. Only the power plan and notification silencing are actually undone at session end.
    public const string PagePerformanceWhy =
        "Speed Windows up for a session. Some of these go back to how you had them when it ends and "
            + "some stay until you change them here — each one says which.";
    public const string PageGeneral = "General";
    public const string PageGeneralWhy = "When the app starts, and the messages it shows you.";
    public const string PageAbout = "About";
    public const string PageAboutWhy = "";

    // ── Home / dashboard ──────────────────────────────────────────────────────────
    public const string PageHome = "Home";
    public const string PageHomeWhy = "Your couch session at a glance — start it here, and set what turns it on.";

    public const string SectionSessionTriggers = "Session triggers";

    public const string HomeStatusReady = "Ready";
    public const string HomeStatusActive = "On the TV";
    public const string HomeReadySub = "Start a couch session on your TV, or set it to start on its own below.";
    public const string HomeActiveSub = "A couch session is running. Send it back to the desktop when you are done.";
    public const string HomeControllerConnected = "Controller connected";
    public const string HomeControllerNone = "No controller connected";
    public const string HomeControllerSetup = "Controller & mouse settings";

    // ── Section headings ──────────────────────────────────────────────────────────

    public const string SectionDisplay = "Display";
    public const string SectionAudio = "Audio";
    public const string SectionBigPicture = "Big Picture";
    public const string SectionAutomaticHDR = "HDR Switching";
    public const string SectionGames = "Games";
    public const string SectionWindows = "Windows";
    public const string SectionStartup = "Startup";
    public const string SectionNotifications = "Notifications";
    public const string SectionController = "Starting a session";
    public const string SectionPointer = "Pointer";
    public const string SectionPointerWhen = "When the pointer works";
    public const string SectionMouse = "Mouse control";
    public const string SectionShortcutKeyboard = "Keyboard";
    public const string SectionShortcutController = "Controller";
    public const string SectionAbout = "About";

    // ── Display & Audio ───────────────────────────────────────────────────────────

    public const string BigPictureIsSessionNote =
        "Steam Big Picture **is** the couch session. Opening it — however you do: a shortcut, Steam "
            + "starting, or the Steam button on your pad — moves everything to the TV. Closing it "
            + "always brings your desktop back, exactly as you left it.";

    // ── Session-end confirmation popup ─────────────────────────────────────────────
    // Each answer is two lines now: what it does, then what that costs. The action stays short enough
    // to scan, because three rows all opening with a verb are read as a shape rather than as words —
    // which is how someone ends up closing a game they meant to leave running.

    public const string ConfirmCloseTitle = "Your game is still running";
    public const string ConfirmCloseBody = "What should happen to it?";

    public const string ConfirmCloseStay = "Keep playing";

    /// <summary>
    /// The Square / X hint on the Big Picture prompt, where ending the session closes Big Picture.
    /// </summary>
    public const string ConfirmEndSessionHint = "End session";

    /// <summary>
    /// The same shortcut on the "a game is still running" prompt, where it does more.
    ///
    /// Both prompts map Square to Choice.Close, and Close runs ReturnToDesktop with BeforeTeardown set
    /// to CloseRunningGame — so on the game prompt one press of Square closes the game the user is in
    /// the middle of. Calling it "End session" named half of the most destructive button on the screen.
    /// </summary>
    public const string ConfirmEndGameHint = "Close game & end";

    // The two navigation options at the foot of both prompts. Neither answers the question the prompt
    // is asking; they are there for when the pad cannot reach the answer — no pointer, or a text field
    // waiting on a keyboard that is not in the room.
    public const string PromptMouseOn = "Turn the controller mouse on";
    public const string PromptMouseOff = "Turn the controller mouse off";
    public const string PromptKeyboard = "Show the on-screen keyboard";

    public const string ConfirmCloseMinimize = "Swap to desktop";
    public const string ConfirmCloseMinimizeWhat = "Your game keeps running.";

    public const string ConfirmCloseToBigPicture = "Close the game, stay on the TV";
    public const string ConfirmCloseToBigPictureWhat = "Big Picture stays up for the next one.";

    public const string ConfirmCloseYes = "Close the game and finish up";
    public const string ConfirmCloseYesWhat = "Unsaved progress is lost.";

    public const string BigPictureEndTitle = "Big Picture is still open";
    public const string BigPictureEndBody = "What should happen to it?";
    // The B / Circle hint on the Big Picture prompt, not a listed option.
    public const string BigPictureEndResume = "Resume Big Picture";

    public const string BigPictureEndMinimize = "Swap to desktop";
    public const string BigPictureEndMinimizeWhat = "Big Picture stays open on the TV.";

    public const string BigPictureEndClose = "Close Big Picture and finish up";
    public const string BigPictureEndCloseWhat = "Everything goes back to your desk.";

    // ── Are you sure? ─────────────────────────────────────────────────────────────
    //
    // The second prompt, shown before anything actually closes a running game. Deliberately blunt: it
    // is the last thing between a press of a button and losing whatever was not saved.

    /// <summary>{0} is the game's name, or a plain word for it when the name is not known.</summary>
    public const string SureCloseTitle = "Close {0}?";
    public const string SureCloseUnnamed = "the game";

    public const string SureCloseBody = "This cannot be undone.";

    public const string SureCloseStay = "No, keep playing";
    public const string SureCloseStayWhat = "Nothing changes.";

    /// <summary>{0} is the game's name.</summary>
    public const string SureCloseYes = "Yes, close {0}";
    public const string SureCloseYesWhat = "Unsaved progress is lost.";

    // The confirmation is deliberately game-only. There were Big Picture variants of these strings for
    // a while; they went when that path did, because closing Big Picture with no game up costs nothing
    // that is not one press away from being undone.

    // ── A new version ─────────────────────────────────────────────────────────────
    //
    // {0} is the version number that was found.

    public const string NoticeUpdateFound = "Version {0} is available.";

    public const string CheckForUpdates = "Look for new versions";
    public const string CheckForUpdatesWhy =
        "Asks GitHub shortly after the app starts, and once a day after that. Nothing is ever "
            + "installed on its own — a new version appears on the Home page with a button, and only "
            + "that button installs it.";

    public const string UpdateCardTitle = "Version {0} is ready";
    public const string UpdateCardBody =
        "You are on {0}. This downloads the new version, swaps it in and restarts — a few seconds, "
            + "and your settings are untouched.";

    public const string UpdateCardButton = "Update now";
    public const string UpdateCardBusy = "Updating…";

    /// <summary>Shown instead of the button while a session is running.</summary>
    public const string UpdateCardDuringSession =
        "Updating restarts the app, so it waits until your session has ended.";

    public const string ConfirmClosingGame = "Double-check before closing a running game";
    public const string ConfirmClosingGameWhy =
        "Puts one more question in the way of anything that would close the game you are playing, "
            + "naming it and saying what you lose. Off makes the first answer final — quicker, and with "
            + "nothing between a mis-pressed button and a closed game.";

    public const string SectionEndingSession = "Ending a session";

    public const string BigPictureOnTop = "Keep Big Picture on top during a session";
    public const string BigPictureOnTopWhy =
        "Holds Big Picture above the desktop and any stray windows on the TV, and tries to step aside "
            + "when you launch a game or its launcher. It works for most games, but with a few the "
            + "launcher opens *behind* Big Picture — Steam and the launcher fight over the top spot — "
            + "so if that happens to you, leave this off. Otherwise you can alt-tab freely and stray "
            + "windows may sit in front of it.";

    public const string TurnOffTvOnExit = "Disconnect the Couch Session display when session ends";
    public const string TurnOffTvOnExitWhy =
        "Windows drops the TV from your desktop, so no windows or the mouse stray onto a dark screen. "
            + "**It comes back with the next session.** Turn off if you also use the TV as a monitor.";

    public const string SwitchAudio = "Move sound to the TV";
    public const string SwitchAudioWhy = "Leave this off to keep hearing everything through your usual speakers or headset.";

    // ── Steam ─────────────────────────────────────────────────────────────────────

    // "Not recommended" is shown the same way everywhere: a red title (the titleEmphasis argument
    // where this is used) and a "**Not recommended.**" lead on the explanation, like DisableUac and
    // DisableFirewall below.
    public const string RestartSteam = "Restart Steam to fix Big Picture menu sound on the TV";
    public const string RestartSteamWhy =
        "Steam locks its audio device at launch, so Big Picture menu sounds can play from the wrong "
            + "speakers. Restarting Steam is the only fix and adds 10–30s per session. Game audio is "
            + "unaffected.";

    // ── Shortcuts ─────────────────────────────────────────────────────────────────

    public const string ShortcutSession = "Start and end a session";
    public const string ShortcutSessionWhy =
        "Starts or ends a session anywhere in Windows, even over a game. Set a keyboard combo, a "
            + "controller one, or both — click a box and press the keys, or hold **two or more** "
            + "buttons together (Back + Start works well) so it can't fire mid-game.";

    public const string ShortcutControllerNote =
        "Works with Xbox, PlayStation and any pad Windows recognises — no extra software. Your "
            + "controller is **never taken over** — it is opened for reading only and never "
            + "exclusively, so it runs alongside Steam Input, DS4Windows and games.";

    // ── First-run welcome ─────────────────────────────────────────────────────────
    public const string WelcomeTitle = "Welcome to Couch Session";
    public const string WelcomeBody =
        "One press moves your PC to the TV: display, sound, HDR and Big Picture, then back again when "
            + "you're done.";
    // Was: "Setup is four quick steps — the list on the Home page walks you through them." That list
    // was removed (see the note in BuildHomePage), so the first sentence every new user reads pointed
    // at something that is not there. What replaced it is the alert strip, which only speaks up when
    // something essential is missing — so that is what this now describes.
    public const string WelcomeSteps =
        "Set your TV and your sound on the Display & Audio page and you are ready. The Home page says "
            + "so if anything essential is still missing.";
    public const string WelcomeGo = "Show me";
    public const string WelcomeSkip = "I'll look around myself";

    // ── Home page guidance ────────────────────────────────────────────────────────
    public const string HomeGuideBannerTitle = "The Guide button controls your session";
    public const string HomeGuideBannerBody =
        "The big button in the middle of your pad. On the desktop it starts a session; in one it asks "
            + "what to do next. Press and let go.";

    public const string HomeGuideButton =
        "**PS / Xbox button** — press it on the desktop to start a session, or in one to ask what to do "
            + "next. It acts when you let go, so holding it still works normally.";

    public const string HomeBorderlessTip =
        "**Set games to borderless fullscreen for the best experience.**  Exclusive fullscreen gives "
            + "the game the whole display, so it minimizes when it loses focus — which is what happens "
            + "when the session prompt appears, or when HDR or the resolution changes mid-session. "
            + "Borderless keeps the game on screen through all of it.";

    // Was: "End a session with the PS / Xbox button". CheckGuideButton returns early when this is off,
    // before GuideStartsSession is ever reached — so switching it off also stops the button starting a
    // session from the desktop and resuming a minimized one. The title named only the half someone
    // would want to switch off, so switching it off took away two things they never agreed to lose.
    public const string GuideEndsSession = "Use the PS / Xbox button to start and end sessions";
    // Kept next to the other session-end wording so the two stay consistent.
    public const string GuideEndsSessionWhy =
        "Press it mid-game and Steam opens its Big Picture overlay, which takes the controller off the "
            + "game — then this asks what to do with the session. **The button is only listened for, "
            + "never intercepted**, so Steam still does everything it normally does with it.";

    public const string ShortcutNone = "Not set — click to choose";
    public const string ShortcutListenKeyboard = "Press a combination…";
    public const string ShortcutListenController = "Hold the buttons, then let go…";
    public const string ShortcutNoPad = "No controller detected";
    public const string ShortcutNeedsModifier = "Add Ctrl, Alt or Shift";
    public const string ShortcutNeedsTwoButtons = "Two buttons or more";
    public const string ShortcutClear = "Clear";
    public const string ShortcutClearWhy = "Remove this shortcut so nothing is bound to it.";

    public const string ShortcutTaken =
        "**{0} is already being used by something else on this machine**, so it could not be "
            + "claimed. Pick a different combination.";

    // ── HDR Switching ─────────────────────────────────────────────────────────────
    //
    // Named "HDR Switching", never "Auto HDR". Windows has its own feature called Auto HDR that does
    // something entirely different — it upscales SDR games to HDR — and sharing the name made it
    // impossible to tell, in a sentence, which of the two was being talked about. This one switches the
    // display's HDR setting on and off around a game; that one invents HDR that was never there.

    public const string HdrMode = "Turn HDR on automatically";
    public const string HdrModeWhy =
        "Primary display only — the TV in a session, or your monitor at the desk — and it only ever "
            + "switches HDR back off if it was the one that turned it on. **Per game** switches it on "
            + "when a game from the list below starts, and off when that game closes. **For the whole "
            + "session** switches it on as the session begins and leaves it there until the session "
            + "ends.";
    public static readonly string[] HdrModeOptions = ["Off", "Per game", "For the whole session"];

    // Both displays are named, not just the primary one. Settings are read at a desk, where the
    // primary display is the monitor — so a warning about "your display" would be about the wrong
    // screen for a page whose whole subject is the television.
    /// <summary>
    /// {0} is the display's name, {1} what it is, {2} what is known about its HDR.
    ///
    /// A dash rather than a second bracket: the name already carries its resolution in brackets, so
    /// "**AW3423DWF** (3440×1440) (main display now)" put two bracketed asides back to back, and
    /// stacking the lines lined those pairs up underneath each other where the repetition shows.
    /// </summary>
    public const string HdrDisplayPart = "**{0}** — {1}: {2}.";
    public const string HdrDisplayMain = "main display now";
    public const string HdrDisplayCouch = "couch display";
    public const string HdrDisplayUnnamed = "Unnamed display";
    // These read the monitor's EDID, which is the honest answer about the panel. What actually gates
    // switching is Windows' own advancedColorSupported (HdrControl.SetPrimaryHdr), and the two can
    // disagree — that disagreement is the entire reason the EDID check was written. So the note says
    // what the display reports and stops short of promising what will or will not happen, which it is
    // not the thing that decides.
    public const string HdrSupported = "HDR supported";
    public const string HdrNotSupported = "does not report HDR support";
    public const string HdrDisplayOff = "not connected, so it cannot be checked";
    public const string HdrDisplaysUnknown =
        "No display could be identified just now, so HDR support is unknown.";

    public const string AutoTickNewGames = "Switch HDR for new games automatically";
    public const string AutoTickNewGamesWhy =
        "Ticks newly found games for HDR automatically. Anything you untick by hand stays off.";

    public const string HdrHotkeyRemember = "Remember games when HDR is turned on — however you turn it on";
    public const string HdrHotkeyRememberWhy =
        "Turning HDR on during a game — or before it launches — adds it to (or drops it from) the "
            + "list, so HDR comes on for it by itself next time and off when it closes. The hotkey, "
            + "the controller shortcut, or a game launched with HDR already on all count. Off, HDR is "
            + "toggled the once and the list is left alone.";
    public const string TipHdrBulk = "Tick every game known to render HDR natively. Press again to untick them.";
    public const string TipHdrBulkOther =
        "Tick everything not known to render HDR natively — **Windows' own Auto HDR** (a different "
            + "feature, which invents HDR from an SDR game) may still improve those. Press again to "
            + "untick them.";
    public const string TipSelectAll = "Tick every game shown.";
    public const string TipClearAll = "Untick every game shown.";
    public const string TipBrowse = "Add a game that was not found automatically.";
    public const string TipScan = "Look for newly installed games right now, instead of waiting for the next check.";

    // Shown in the footer, not a toast — see SettingsForm.ShowFooterNote.
    public const string NoticeScanFound = "Found {0} new game(s)";
    public const string NoticeScanNothing = "No new games found";

    // ── Performance ───────────────────────────────────────────────────────────────

    public const string ChangePowerPlan = "Change the Windows power plan for a session";
    public const string ChangePowerPlanWhy =
        "Switches to the plan below during a session and restores yours after, slider included. "
            + "Off leaves your power settings untouched.";

    public const string PowerPlanPick = "Plan to use";
    public const string PowerPlanPickWhy =
        "**Automatic** picks the fastest plan you have and won't slow you down if you're already "
            + "faster. Or name your own and it's used as-is.";
    public const string PowerPlanAuto = "Automatic — the fastest one you have";

    public const string KeepDisplayAwake = "Keep the TV awake";
    public const string KeepDisplayAwakeWhy =
        "Stops the TV sleeping mid-session. Windows does not count controller input as "
            + "activity, so a long cutscene can otherwise blank the screen.";
    public const string SilenceNotifications = "Silence Windows notifications";
    public const string SilenceNotificationsWhy =
        "Hides Windows notifications until the session ends — a corner card becomes a TV-sized "
            + "banner you can't dismiss with a pad.";
    public const string EnableGameMode = "Turn on Windows Game Mode";
    public const string EnableGameModeWhy =
        "Asks Windows to prioritise the game and hold back background work. Stays on afterwards — "
            + "it's a Windows-wide preference.";
    public const string GamePriority = "Raise the game's priority";
    public const string GamePriorityWhy =
        "Gives the running game more processor time, and ends when that game closes. Applies to games in "
            + "the HDR Switching list. Some block it (often anti-cheat) and it's skipped quietly.";

    // ── General ───────────────────────────────────────────────────────────────────

    public const string StartWithWindows = "Start with Windows";
    public const string StartWithWindowsWhy =
        "Launches to the tray as you sign in — ahead of the usual startup delay — ready to switch "
            + "displays and manage HDR.";

    public const string StartOnLaunch = "Start a session when Couch Session opens";
    public const string StartOnLaunchWhy =
        "Moves to the TV and opens Big Picture as soon as the app starts. With \"Start with Windows\" "
            + "on, signing in drops you straight onto the couch. Off by default.";

    public const string StartOnWake = "Start a session when the PC wakes from sleep";
    public const string StartOnWakeWhy =
        "When the PC resumes from sleep, switch to the TV and open Big Picture — handy if you wake it "
            + "from the sofa. Off by default.";

    public const string StartOnWakeControllerOnly = "Only when the controller wakes the PC";
    public const string StartOnWakeControllerOnlyWhy =
        "Start on wake only when the controller woke the PC. A mouse or keyboard wake then goes to the "
            + "desktop instead. Off by default.";

    public const string MinimizeOnClose = "Minimize to system tray";
    public const string MinimizeOnCloseWhy =
        "Closing this window leaves the app in the tray, where HDR and controller switching keep "
            + "working. Off means closing quits. You can quit from the tray either way.";
    public const string ShowNotifications = "Show notifications";
    public const string ShowNotificationsWhy =
        "The small cards Couch Session shows in the corner of the screen. Off silences **every** one "
            + "of them; nothing else changes, and anything that went wrong is still on the Home page "
            + "and in the log.";

    public const string ShowSessionNotifications = "When a session starts and ends";
    public const string ShowSessionNotificationsWhy =
        "A message on the screen you are moving to, so you know the switch worked — and again when "
            + "your desktop is back.";

    public const string ShowHdrNotifications = "When HDR turns on and off";
    public const string ShowHdrNotificationsWhy =
        "Confirms HDR Switching did its job as each game opens and closes. It keeps working either "
            + "way.";

    public const string ShowMinimizeNotification = "When the app keeps running in the tray";
    public const string ShowMinimizeNotificationWhy =
        "Closing this window does not close the app. Turn this off once you know where it went.";

    public const string ShowProblemNotifications = "When something goes wrong";
    public const string ShowProblemNotificationsWhy =
        "A hotkey another program already holds, or a change Windows refused. Rare, and each one is "
            + "something that stops a feature working.";

    public const string ShowActionNotifications = "When something you pressed is done";
    public const string ShowActionNotificationsWhy =
        "Confirms a bug report was saved, or that UAC or the firewall has changed. Only ever appears "
            + "just after you press the thing that causes it.";
    public const string DisableUac = "Turn off User Account Control (UAC)";
    public const string DisableUacWhy =
        "**This lowers your PC's security.** UAC prompts appear on a secure screen a controller can't "
            + "reach. This elevates admin apps silently so those dead ends go away. Needs an admin "
            + "prompt, **applies right away, no restart.** It is not tied to a session — it stays off "
            + "until you switch it back on here.";
    public const string NoticeUacChanged = "UAC setting changed";
    public const string NoticeUacRestart = "Admin prompts are now silent.";
    public const string NoticeUacOn = "UAC prompts are back on.";

    public const string DisableFirewall = "Turn off Windows Firewall";
    public const string DisableFirewallWhy =
        "**This lowers your PC's security.** Windows Firewall's 'allow this app?' prompt needs a mouse "
            + "and blocks a game's networking until answered. Turning it off stops those prompts. Needs "
            + "an admin prompt, applies right away. It is not tied to a session — it stays off until "
            + "you switch it back on here.";
    public const string NoticeFirewallChanged = "Firewall setting changed";
    public const string NoticeFirewallOff = "Windows Firewall is now off.";
    public const string NoticeFirewallOn = "Windows Firewall is now on.";

    public const string DisableGameBarButton = "Switch off the Xbox Game Bar";
    // Was: "switches it off during a session and back on when you quit". Two things wrong with that.
    // GameBarControl is applied from TrayApp at startup and restored at app exit, not per session — so
    // it is off the whole time Couch Session is running, and "when you quit" read as quitting the game.
    // And it clears AppCaptureEnabled and GameDVR_Enabled as well, which is background clip recording:
    // someone who records their play would have lost it with nothing here to say so.
    public const string DisableGameBarButtonWhy =
        "The Xbox Guide button opens the Game Bar over your game, awkward from the couch. This switches "
            + "the Game Bar off for the whole time Couch Session is running — not just during a session "
            + "— and **puts it back when you close the app**. Background clip recording goes off with "
            + "it. PlayStation pads don't trigger it.";

    public const string StartOnController = "Start a session when a controller connects";
    public const string StartOnControllerWhy =
        "Switching a pad on is usually the moment you sit down to play, so this treats it as "
            + "the cue to move everything to the TV.";

    // ── About ─────────────────────────────────────────────────────────────────────

    public const string TipDonate = "Chip in for Couch Session with a small PayPal donation.";

    // ── Navigation, buttons and column titles ──────────────────────────────────────

    /// <summary>The list down the left. Order here is the order they appear.</summary>
    public static readonly string[] NavItems =
        [
        "Home",
        "Display & Audio",
        "HDR Switching",
        "Controller",
        "Hotkeys",
        "Performance",
        "General",
        "About",
    ];

    public const string ButtonAll = "All";
    public const string ButtonNone = "None";
    public const string ButtonBrowse = "Browse…";
    public const string ButtonScan = "Scan";
    public const string ButtonNativeHdr = "Native HDR";
    public const string ButtonNonNative = "Non-native";
    public const string ButtonSupport = "Support this project!";

    public const string ColumnOn = "On";
    public const string ColumnGame = "Game";
    public const string ColumnLastPlayed = "Last played";
    public const string ColumnInstalled = "Installed";
    public const string ColumnHdr = "HDR";

    public const string BadgeNative = "Native HDR";
    public const string BadgeNonNative = "Non-native HDR";
    // ManualTagKey went with Ui/StarLegend.cs, the only thing that read it.
    public const string ManualTagHint = "You set this HDR tag yourself. Click to change it.";
    public const string BadgeHint = "Click to set this yourself.";

    // ── Notifications ──────────────────────────────────────────────────────────────
    //
    // The game's name is added in front of the "Running"/"Closed" lines automatically, so
    // those read as "Elden Ring is running." — start them lower case and mind the full stop.

    public const string NoticeHdrOn = "HDR on";
    public const string NoticeHdrOff = "HDR off";
    public const string NoticeHdrRunning = "is running.";
    public const string NoticeHdrClosed = "has closed.";
    public const string NoticeHdrSessionReady = "Ready for the session.";
    public const string NoticeHdrDesktop = "Back on the desktop.";
    public const string NoticeHdrByHotkey = "Changed on your main display.";
    public const string NoticeHdrRemembered = "{0} will turn HDR on by itself from now on.";
    public const string NoticeHdrForgotten = "{0} will no longer turn HDR on by itself.";

    public const string ReportBug = "Report a bug";
    public const string ReportBugWhy =
        "Saves one file to your desktop — log, settings, display and audio details — then opens the "
            + "report page. **Attach that file.**";

    public const string WarnBugReportFailed = "The bug report could not be saved. {0}";

    public const string NoticeBugReport = "Bug report saved";
    public const string NoticeBugReportDetail =
        "{0} is on your desktop. Attach it to the report that just opened.";

    public const string NoticeShortcutTaken = "Shortcut not available";
    public const string NoticeMinimized = "Still running";
    public const string NoticeMinimizedDetail =
        "Couch Session is in the notification area. Double-click its icon for settings, or "
            + "right-click to quit.";

    // ── Dropdowns ──────────────────────────────────────────────────────────────────

    // Appended to displays that are known about but not currently switched on.
    public const string DisplayDisconnected = "— disconnected";

    // Appended to whichever active display is currently the Windows primary. Says "currently" so it
    // is not mistaken for the display that becomes primary during a couch session.
    public const string DisplayPrimary = "— currently primary";

    public const string TvDisplay = "Couch Session display";
    // Was: "Only displays Windows can see right now are listed — switch your TV on and it appears
    // here." ListDisplays queries AllPaths and keeps inactive ones, sorted live-first and shown dimmed
    // with DisplayDisconnected after the name. So the television is already in the list with the set
    // switched off — and the old wording sent the user off to switch it on in the app's single most
    // common situation.
    public const string TvDisplayWhy =
        "The screen to play on. A TV that is switched off is still listed, dimmed and marked "
            + "disconnected, so you can set this up without getting off the sofa first.";
    public const string TvVideoMode = "Couch Session resolution and refresh rate";

    public const string TvVideoModeWhy =
        "Windows often runs a TV at 60 Hz when it can do more. The best mode is marked; **scaled** "
            + "modes are the wrong shape and letterbox or stretch. The TV's mode is restored when you "
            + "leave.";

    public const string VideoModeAuto = "Leave it to Windows";

    // Appended to whichever mode is the display's best. Starts with a space on purpose.
    public const string VideoModeBest = "   ← best for this display";

    // Appended to modes that are a different shape from the display. Kept very short: this sits
    // inside a dropdown row, and anything longer is cut off mid-word with no way to read it.
    public const string VideoModeScaled = "   · scaled";

    // Replaces the "switched off" note when remembered modes are being shown instead.
    public const string VideoModeRemembered =
        "**{0} is switched off**, so these are the modes it reported last time. They are "
            + "checked again when it comes back.";

    // Shown under the picker when the chosen display is switched off or disconnected, so an
    // almost-empty list reads as a reason rather than as a fault.
    public const string VideoModeUnavailable =
        "**{0} is not switched on**, so Windows cannot say what it supports. Switch it on and "
            + "this list fills in. Until then a session uses whatever Windows picks.";

    public const string DisplayMode = "Other displays";
    public const string DisplayModeWhy =
        "Off stops games opening on the wrong screen; on keeps a browser or Discord visible. Either "
            + "way they're restored when the session ends.";
    public const string DisplayModeGameNote =
        "Some games reopen on the exact monitor they remember, whatever is primary. If one keeps "
            + "landing on the wrong screen, choose **Turn them off while I'm on the TV** — then it has "
            + "nowhere to go but the TV.";
    public const string TvAudio = "Couch Session audio";
    public const string TvAudioWhy =
        "The HDMI output your TV is connected to — often named after your graphics card.";
    public const string TvAudioHdmiNote =
        "A TV playing sound over HDMI must be on for its audio device to appear. One seen before stays "
            + "selectable — dimmed as **off** — so you can set this up with the TV cold.";

    // Appended to audio devices that are remembered but not switched on right now.
    public const string AudioDeviceOff = "— off";

    public const string DesktopAudio = "Desktop audio";
    public const string DesktopAudioWhy = "The device to return to when you finish.";
    public const string SteamAfter = "Steam window on exit";
    public const string SteamAfterWhy =
        "Steam reopens its desktop window when Big Picture closes. Its previous size and "
            + "position are restored first.";
    /// <summary>Shown in order, top to bottom. Keep the same number of entries.</summary>
    public static readonly string[] DisplayModeOptions =
    [
        "Turn them off while I'm on the TV",
        "Keep them on, with the TV as primary",
    ];

    /// <summary>Shown in order, top to bottom. Keep the same number of entries.</summary>
    public static readonly string[] SteamAfterOptions =
    [
        "Leave it open",
        "Minimize it",
        "Close it (Steam keeps running in the tray)",
    ];

    // ── Messages and odds and ends ─────────────────────────────────────────────────

    public const string AutomaticAudio = "Automatic (restore the previous device)";
    public const string NoDisplaysFound = "No displays found — is the TV on?";

    public const string GamesListNote =
        "Steam, Epic and GOG games are found automatically; add anything else with Browse. The Native "
            + "HDR tag can be wrong for some games — click any tag to change it.";

    /// <summary>{0} is the version, already including "v" and the channel. {1} is the author.</summary>
    public const string AboutVersion = "{0}  ·  Created by {1}";

    public const string AboutBlurb =
        "Sit down, switch a controller on, and your PC moves to the television — display, "
            + "sound, HDR, resolution and power plan, all at once. Get up and it puts every "
            + "one of them back exactly as it was.\nNo mouse, no keyboard, no settling for "
            + "the wrong screen. Just the couch.";

    // Warnings. \n is a line break.

    public const string WarnNoNativeGames =
        "None of your installed games are known to support HDR natively yet.\n\nThe list "
            + "updates in the background, so this may fill in shortly.";

    public const string WarnAllNative = "Every game in your library is already tagged as having native HDR.";
    public const string WarnNotAGame = "That does not look like a game program.";
    public const string WarnPickDisplay = "Choose which display is your TV first.";
    public const string WarnPickControllerFirst = "Choose a controller from the list first.";

    public const string BrowseTitle = "Pick the game's program file";
    public const string BrowseFilter = "Programs (*.exe)|*.exe|All files (*.*)|*.*";

    // ── More messages ──────────────────────────────────────────────────────────────
    //
    // Anything with {0} in it has a number or a message dropped in at that spot when it is
    // shown. Keep the {0} — the text around it is yours to change.

    public const string WarnPickAudio =
        "Choose a Couch Session audio device, or turn off \"Move sound to the TV\".";

    public const string WarnPickDisplayFirst = "Choose your Couch Session display before starting.";

    /// <summary>{0} is the display's name. Shown when the couch display looks like the desk monitor.</summary>
    public const string WarnCouchDisplayIsPrimary =
        "**{0} is your main display**, and Couch Session is set to use it as the television. A "
            + "session will move that screen about and put it back afterwards, which is probably "
            + "not what you want.\nIf your TV is not in the list, switch it on and it appears.";
    public const string WarnSaveFailed = "Could not save your settings.\n\n{0}";
    public const string WarnDiagnosticsFailed = "Could not write the diagnostics report.\n\n{0}";

    public const string DeviceNoteOne = "Device list updated — {0} display known";
    // "available" was wrong: the count comes from every DisplayInfo in the picker, and that list
    // includes displays that are not connected — deliberately, so a television can be chosen with the
    // set switched off. "Known" is what the number actually is.
    public const string DeviceNoteMany = "Device list updated — {0} displays known";

    // Listed in the log when the window is closed but the app keeps running, so you can see
    // which setting is holding it open.

    public const string FeatureAutoHdr = "HDR Switching ({0} game(s))";
    public const string FeatureGamePriority = "Raising the running game's priority";
    public const string FeatureControllerOn = "Starting when a controller connects";
    // Was: "Ending the session when a controller disconnects", which ControllerDisconnectNote
    // contradicts in as many words — a disconnect goes through EndSession and only ever raises a
    // prompt, settling on changing nothing if nobody answers.
    public const string FeatureControllerOff = "Noticing when a controller disconnects mid-session";

    // ── Resetting ──────────────────────────────────────────────────────────────────

    public const string ButtonReset = "Reset all settings";

    public const string TipReset =
        "Puts every setting back to how it was when the app was new, including your displays, "
            + "audio devices and HDR game list.";

    public const string ResetConfirmTitle = "Reset all settings";

    public const string ResetConfirm =
        "This puts every setting back to its default, including your chosen display, your audio "
            + "devices and every game ticked for HDR Switching.\n\nThis cannot be undone. Continue?";

    // ── Resetting one page ─────────────────────────────────────────────────────────
    //
    // Named after the page rather than a generic "Reset", so pressing it in the footer and pressing
    // it here are not two identical-looking buttons that do very different amounts of damage.

    public const string ResetPage = "Reset this page";

    /// <summary>{0} is the page name.</summary>
    public const string ResetPageWhy =
        "Puts the settings on this page — and only this page — back to their defaults. "
            + "Nothing on the other pages changes.";

    /// <summary>{0} is the page name.</summary>
    public const string ResetPageTitle = "Reset {0}";

    /// <summary>{0} is the page name, {1} names what specifically goes.</summary>
    public const string ResetPageConfirm =
        "This puts the settings on {0} back to their defaults.{1}\n\n"
            + "Nothing on the other pages is touched. This cannot be undone. Continue?";

    /// <summary>{0} is the page name.</summary>
    public const string ResetPageDone = "{0} reset to defaults";

    // What each page loses beyond its switches, spelled out in the confirmation because these are
    // the parts someone would not expect a page-level reset to take with it.

    public const string ResetPageLosesDisplay =
        "\n\nYour chosen television, its video mode and both audio devices are cleared, and the app "
            + "goes back to guessing them.";

    public const string ResetPageLosesHdr =
        "\n\nEvery game ticked for HDR Switching is cleared as well.";

    public const string ResetPageLosesController =
        "\n\nThe controller chosen to start a session is cleared, so any connected controller can "
            + "start one again.";

    // The session keyboard shortcut has no default — AppConfig.ShortcutKeyboard is 0, meaning unset —
    // so for that one "goes back to its original buttons" would have been a promise to restore
    // something that never existed.
    public const string ResetPageLosesHotkeys =
        "\n\nThe controller shortcuts and the HDR keyboard shortcut go back to their original buttons, "
            + "and the keyboard shortcut for starting a session is cleared. The PS / Xbox button setting "
            + "goes back to its default too.";

    /// <summary>
    /// Shown under the Performance page's reset, because two of its switches are not the app's to
    /// reset — they change Windows itself and are read back from it.
    /// </summary>
    public const string ResetPageKeepsWindows =
        "User Account Control and the firewall are left exactly as they are — those are Windows' own "
            + "settings, not this app's, so this button will not change them.";

    // ── Defaults ───────────────────────────────────────────────────────────────────
    //
    // {0} is replaced with the two words below it.

    public const string DefaultIs = "Default: {0}";
    public const string DefaultOn = "On";
    public const string DefaultOff = "Off";

    // ── Which controller ───────────────────────────────────────────────────────────

    public const string MouseControl = "Use your controller as a mouse";
    public const string MouseControlWhy =
        "For launchers and windows that ignore a controller. **R1 / RB left-clicks, L1 / LB "
            + "right-clicks.** If Steam Input is emulating a mouse on the same pad, turn one off — "
            + "otherwise the pointer moves twice as far.";

    public const string CursorStick = "Stick that moves the pointer";
    public const string CursorStickWhy =
        "The **other stick scrolls** — push it up or down to roll the wheel.";
    public static readonly string[] CursorStickOptions = ["Left stick", "Right stick"];

    public const string UseTrackpad = "Also use the trackpad (PS5 DualSense)";
    public const string UseTrackpadWhy =
        "A finger on the trackpad moves the pointer and **two fingers scroll**. Press it to "
            + "left-click, press and hold to right-click. Does nothing on pads without one.";

    public const string MouseHold = "Only while holding a button";
    public const string MouseInGames = "Allow pointers while in game";
    public const string MouseInGamesWhy =
        "Normally the pointer switches itself off once a game fills the screen, so it cannot fight "
            + "the game's own controls. Turn this on for games that expect a mouse and ignore a "
            + "controller. Holding the button below does the same thing for a moment, without "
            + "leaving it on all the time.";

    public const string MouseHoldWhy =
        "The pointer responds only while the button below is held — and while it is held it works "
            + "**over a game too**, though not inside Big Picture, which always drives itself. "
            + "**Try this if the pointer is dead in a launcher, "
            + "or moves in a game when you did not want it to**: nothing is guessed about the window "
            + "in front, you say when you want it. It also stops a resting stick nudging the pointer, "
            + "which is what keeps a screen from going to sleep.";

    public const string MouseHoldButton = "Hold button";
    public const string MouseHoldButtonWhy =
        "The button that turns the mouse on while held. Chosen by position, so it is the same "
            + "physical button on any controller.";

    public const string MouseSpeed = "Pointer speed";
    public const string MouseSpeedWhy =
        "How far the cursor travels for the same push. Left places it exactly; right crosses a big "
            + "screen in one flick.";
    public const string MouseDeadzone = "Stick dead zone";
    public const string MouseDeadzoneWhy =
        "How far a stick must move before the pointer does. If the cursor creeps on its own, drag "
            + "right until it stops. Sticks only, not the trackpad.";
    public const string MouseDeadzoneLow = "Smallest";
    public const string MouseDeadzoneHigh = "Largest";

    public const string MouseSpeedDefault = "Reset";
    /// <summary>{0} is the value it goes back to.</summary>
    public const string SliderResetTip = "Put this back to {0}, where it shipped.";
    public const string MouseSpeedSlow = "Slower";
    public const string MouseSpeedFast = "Faster";

    /// <summary>Hold-button choices, in order. SettingsForm.HoldButtons must line up with these.</summary>
    public static readonly string[] MouseHoldOptions =
    [
        "R2 / RT  (right trigger)",
        "L3  (left stick click)",
        "R3  (right stick click)",
        "Cross / A",
        "Circle / B",
        "Square / X",
        "Triangle / Y",
        "D-pad Up",
        "D-pad Down",
        "D-pad Left",
        "D-pad Right",
        "Create / View",
        "Options / Menu",
    ];

    public const string AnyController = "Any controller";

    /// <summary>Added after a chosen controller's name while it is switched off.</summary>
    public const string NotConnected = "(not connected)";

    public const string ClearControllers = "Forget selected";

    public const string TurnDisplayBackOn = "Turn it back on now";
    public const string TurnDisplayBackOnWhy =
        "Reconnects the Couch Session display if the app has switched it off. Handy when a session "
            + "ended with the setting above turned on and you want that screen back without "
            + "starting another one.";

    public const string NoticeDisplayBackOn = "Display reconnected";
    public const string NoticeDisplayBackOnDetail = "{0} is part of your desktop again.";
    public const string WarnDisplayAlreadyOn = "That display is already connected.";
    public const string WarnDisplayWouldNotAttach =
        "Windows would not switch that display on.\n\nCheck it is powered up and on the right input.";

    public const string NoticeDisplayOff = "Display switched off";
    public const string NoticeDisplayOffDetail = "{0} was disconnected from your desktop.";
    public const string WarnLastDisplay =
        "That is the only screen left, so it cannot be switched off — you would have nowhere to look.";
    public const string WarnDisplayWouldNotDetach = "Windows would not switch that display off.";

    public const string DisplayStateOn = "Enabled";
    public const string DisplayStateOff = "Disabled";
    public const string DisplayOnTip =
        "Connects or disconnects the Couch Session display. Off removes it from your desktop; on adds "
            + "it back where it was.";

    public const string SetPrimary = "Set as primary now";
    public const string SetPrimaryTip =
        "Make the Couch Session display your main screen — where the taskbar lives and new windows "
            + "open. The display must be connected first.";
    public const string WarnDisplayOffForPrimary =
        "Switch that display on before making it the main screen.";
    public const string NoticePrimarySet = "Main display changed";
    public const string NoticePrimarySetDetail = "{0} is now your primary screen.";
    public const string WarnPrimaryFailed = "Windows would not make that display the main one.";

    public const string RenameController = "Rename…";
    public const string RenameControllerWhy =
        "Give the selected controller a name of your own, so you can tell which is which.";

    public const string RenameTitle = "Name this controller";
    public const string RenamePrompt =
        "What would you like to call it?\n\nLeave it empty to go back to the model name.";

    public const string RevertControllerName = "Use original name";
    public const string RevertControllerNameWhy =
        "Drops the name you gave this controller and goes back to the model it reports.";

    /// <summary>Shown in light blue under the controller settings. ** ** marks the bold part.</summary>
    public const string ControllerTriggerNote =
        "Responds to a controller connecting or disconnecting, not to one already switched on. "
            + "**Paused while you are on this page or the Hotkeys page**, so switching a controller on "
            + "to set it up does not also start a session. Not paused during a session, where a pad "
            + "going quiet still has to be noticed.";

    // Live state at the top of the card. Nielsen's first heuristic, and the one thing this page could
    // not answer: whether the app can see your controller right now.
    public const string PadsNone = "No controller connected. Switch one on and it appears here.";
    /// <summary>{0} is the list of connected controllers.</summary>
    public const string PadsConnected = "Connected: {0}";
    public const string PadsChosenAway =
        "**Your chosen controller is not connected right now.** Anything else connecting will not "
            + "start a session.";
    /// <summary>
    /// What happens on a disconnect. Worth stating outright rather than leaving to be discovered:
    /// the fear is that a controller going flat mid-game drops the television back to the desktop,
    /// and it does not — the prompt asks, and does nothing at all if nobody answers.
    /// </summary>
    public const string ControllerDisconnectNote =
        "**A disconnect never ends your session on its own.** You are asked what to do, and if nobody "
            + "answers, nothing changes — so a flat battery mid-game costs you nothing.";

    public const string PadOnBattery = "on battery";
    public const string PadCharging = "charging";

    public const string ClearControllersWhy =
        "Forgets the controller picked above; if it was the chosen one, any controller works again. "
            + "Its name is kept — **Use original name** drops that too.";
    // The picker's heading follows its two switches, so it never names something that is
    // switched off — see SettingsForm.UpdateTriggerPadTitle. TriggerPad covers both on, and also
    // neither, where it reads as a description of what the setting is for.
    public const string PadLinkPick = "Connection that counts";
    public const string PadLinkPickWhy =
        "For a controller that lives plugged in to charge. **Only starting is restricted** — "
            + "switching one off always works.";

    /// <summary>Shown in order, top to bottom. Keep the same number of entries.</summary>
    public static readonly string[] PadLinkOptions =
    [
        "Wired or wireless",
        "Only when plugged in",
        "Only over Bluetooth",
    ];

    public const string TriggerPad = "Controller that starts a session";
    public const string TriggerPadEnds = "Controller to watch";

    public const string TriggerPadWhy =
        "Pick one if you have several pads and only want a particular one to control the "
            + "session. Two of the same model are told apart by the code after the name.";

    // ── Checking the performance settings — removed ────────────────────────────────
    //
    // Twenty-six constants used to live here, describing a panel that reported on every performance
    // setting at once. SettingsForm.RunPerformanceCheck had no caller: the card holding its button was
    // taken out and the method was left behind, so none of this had been on screen for some time.
    //
    // Two of them were rewritten earlier the same day for claiming that UAC and the firewall were
    // "tried for a moment and put straight back" when both are only ever read. They were unreachable,
    // so the wording had been wrong in a place nobody could see it — which is the argument for deleting
    // dead text rather than keeping it tidy.
    //
    // The per-toggle checks are unaffected and still live: each Windows row on the Performance page
    // runs its own PerformanceCheck.CheckX and prints the result underneath itself.

    // What happens to each setting after a session. Shown as the third line of a check result.

    // Shown under the results, so nobody wonders whether running the check moved anything.
    // Assembled from parts rather than written as a set of finished sentences.
    //
    // There are two independent facts to report — how many settings are already on, and how
    // many are switched off — and they can occur in any combination. Writing one sentence per
    // combination meant picking whichever seemed more important and silently dropping the
    // other, which is how a machine with three settings already on and one switched off ended
    // up being told only about the one.
    //
    // Phrased so the number agrees whether it is 1 or 6, which is why none of these clauses
    // contain "is" or "are" next to the count.

    // ── Updates ────────────────────────────────────────────────────────────────────
    //
    // {0} is a version number. Keep the marker.

    public const string OpenRepository = "View on GitHub";

    public const string CheckUpdates = "Check for updates";
    public const string CheckUpdatesBusy = "Checking…";

    public const string UpToDate = "Version {0} is the newest.";
    public const string UpdateFound = "Version {0} is available.";

    public const string UpdateAskTitle = "Update available";

    public const string UpdateAsk =
        "Version {0} is available. You are running {1}.\n\nCouch Session will replace "
        + "itself and restart. Any session in progress will end.";

    public const string UpdateFailed =
        "The update could not be installed. Nothing has been changed.\n\nYou can download it "
        + "yourself from the GitHub page under About.";
}

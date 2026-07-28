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
    // Appended to a setting's title and coloured by the titleEmphasis passed with it.
    //
    // There was a BadgeNotRecommended beside this and it was never used once — the two settings it
    // was written for, UAC and the firewall, carry their warning in the first words of their
    // description instead, which is where somebody about to lower their machine's security should
    // meet it. A convention with one half missing is not a convention.
    public const string BadgeRecommended = " **(Recommended)**";

    // ── The window itself ─────────────────────────────────────────────────────────

    public const string PageDisplayAudio = "Display & Audio";
    public const string PageDisplayAudioWhy = "Choose the TV and speakers your couch sessions move to.";
    public const string PageController = "Controller";
    public const string PageControllerWhy =
        "Which controller starts a session, what happens if it disconnects, and using one as a mouse.";

    public const string PageShortcuts = "Hotkeys";
    public const string PageShortcutsWhy = "Key and button combinations that work anywhere, including over a game.";

    public const string ShortcutHdr = "Turn HDR on and off";
    // One sentence. Everything after it described the remembering, which is a setting of its own on
    // the HDR Switching page and explained there — so this row was carrying the documentation for a
    // feature that lives elsewhere, on a page about key combinations.
    public const string ShortcutHdrWhy =
        
            "Toggles HDR on your main display, whether a session is running or not — for "
            + "the games and apps that look wrong in HDR, without going into Windows "
            + "settings.";

    public const string PageAutoHDR = "HDR Switching";
    public const string PageAutoHDRWhy =
        "Switch your display's HDR on for a whole session, or only while chosen games are running.";
    public const string PagePerformance = "Performance";
    // Was: "then put your settings back when it ends" — a blanket promise four of the seven settings on
    // the page do not keep. Game Mode is only ever switched on (PerformanceTuning.Restore says so
    // outright), the Game Bar is held for the app's lifetime, and UAC and the firewall stay however
    // they are left. Only the power plan and notification silencing are actually undone at session end.
    public const string PagePerformanceWhy =
        
            "Speed Windows up for a session. Some of these go back to how you had them when "
            + "it ends and some are Windows' own settings that stay however they are left — "
            + "each one says which.";
    public const string PageGeneral = "General";
    public const string PageGeneralWhy =
        "When the app starts, what it asks before closing a game, and the messages it shows you.";
    public const string PageAbout = "About";
    public const string PageAboutWhy = "";

    // ── Using it at the desk only ─────────────────────────────────────────────────
    //
    // Named for what the reader wants rather than for what it switches off. "Disable couch session
    // features" describes the mechanism to somebody who never wanted them; "just HDR and performance"
    // describes what they came for.
    public const string SectionDeskOnly = "How you use this";
    public const string DeskOnly = "At my desk only";
    public const string DeskOnlyWhy =

            "**No television, no controller, no sessions.** HDR still switches itself around your "
            + "games, the priority boost still runs, and the Windows settings on the Performance "
            + "page still work — everything else is put away.\n\nNothing is lost by switching this "
            + "on. Your display, audio and controller settings are remembered and come straight "
            + "back if you turn it off.";

    /// <summary>Shown under the glance grid while the couch half is switched off.</summary>
    public const string DeskOnlyNote =
        "Couch session features are off. Turn them back on under General.";

    /// <summary>Shown beside the switch while a session is running, which is when it is locked.</summary>
    public const string DeskOnlyDuringSession =
        "**End your session first.** This switch puts away the display, audio and controller "
      + "settings a running session is using, so it waits until you are back at your desk.";

    // ── The same pages, described without sessions ────────────────────────────────
    //
    // Desk-only mode leaves several pages standing whose subtitles and descriptions all explain
    // themselves in terms of a session. A page called Performance that opens "Speed Windows up for a
    // session" is describing something the reader has switched off, which is the same defect as any
    // other wording that does not match what the app does.
    //
    // Written as alternates rather than made vague enough to cover both. "While a game is running"
    // and "for a session" are different promises, and blurring them to save six strings would cost
    // the precision that makes these descriptions worth reading.

    public const string PageHomeWhyDesk = "Everything at a glance, and the switch that turns it all on.";

    public const string PageAutoHDRWhyDesk =
        "Switch your display's HDR on while chosen games are running, and off again when they quit.";

    public const string PagePerformanceWhyDesk =

            "Speed Windows up while a game is running. Some of these go back to how you had them "
            + "when it closes and some are Windows' own settings that stay however they are left — "
            + "each one says which.";

    public const string ShortcutHdrWhyDesk =

            "Toggles HDR on your main display — for the games and apps that look wrong in HDR, "
            + "without going into Windows settings.";

    // HdrModeWhyDesk stood here, describing the picker this mode no longer has. The switch that
    // replaced it carries its own description in HdrAutoWhy.

    // Two choices are a switch, not a list.
    //
    // With whole-session gone this picker offered Off and Per game, which is a yes-or-no question
    // wearing a dropdown: two clicks to answer, and a control that looks like it has more behind it.
    // The list of options is still HdrModeOptionsDesk, because the glance tile and the save path both
    // read the picker — the switch drives it rather than replacing it.
    // "It only ever switches HDR back off if it was the one that turned it on" was in both of these
    // and was not true with Smart HDR on, which is the shipped default. That setting exists to
    // remember HDR you switch on yourself — by hotkey, or by launching a game with HDR already on —
    // and remembering it calls Remember(game, true), which claims _weTurnedItOn and adopts the
    // running game precisely so that closing the game switches HDR off. The comment in
    // HdrCoordinator says as much in the code.
    //
    // So the app does switch off HDR it did not turn on. Both descriptions now name the case, since
    // "my HDR turned itself off" is exactly the surprise this file exists to prevent.
    public const string HdrAuto = "Turn HDR on automatically";
    public const string HdrAutoWhy =

            "**On when a game from the list below starts, off when it closes.** Your main display "
            + "only. HDR you had on beforehand is left alone — unless **Smart HDR** below has tied "
            + "it to the game you are playing, in which case it goes off when that game does.";


    /// <summary>The same choice without the session entry. Indexes match HdrMode: Off, PerGame.</summary>
    public static readonly string[] HdrModeOptionsDesk = [
        "Off",
        "Per game",
    ];

    // ── Home / dashboard ──────────────────────────────────────────────────────────
    public const string PageHome = "Home";
    public const string PageHomeWhy = "Your couch session at a glance — start it here, and set what turns it on.";

    // ── Section headings ──────────────────────────────────────────────────────────

    public const string SectionGlance = "Settings at a glance";
    public const string SectionDisplay = "Display";
    public const string SectionAudio = "Audio";
    public const string SectionBigPicture = "Big Picture";
    // Was "HDR Switching", which is the page's own title and rendered directly under it. Every
    // other page names a sub-topic in its first section rather than repeating itself.
    public const string SectionAutomaticHDR = "When HDR switches";
    public const string SectionGames = "Games";
    public const string SectionWindows = "Windows";
    public const string SectionStartup = "Startup";
    public const string SectionNotifications = "Notifications";
    // The controller page, reorganized into one question per card.
    //
    // It was two headings over five settings, with "Starting a session" sitting above a card that
    // also decided what happens when a controller is lost. Grouping by the question each card
    // answers is the whole of the improvement: a reader looking for one of these can find the right
    // card without reading the one above it, and every heading is now true of everything under it.
    //
    // Which is why "Starting a session" is still here and is no longer a problem. The paragraph that
    // used to open this comment argued it named only half of what sat under it — true when the
    // disconnect setting shared its card, and left standing after the split moved that setting to
    // SectionDisconnect. It read as an unfixed complaint about the line directly beneath it.
    public const string SectionYourController = "Your controller";
    public const string SectionController = "Starting a session";
    public const string SectionDisconnect = "If it disconnects";

    public const string SectionPointer = "How the pointer moves";
    public const string SectionPointerWhen = "When the pointer works";
    public const string SectionMouse = "Using it as a mouse";
    public const string SectionShortcutKeyboard = "Keyboard";
    public const string SectionShortcutController = "Controller";
    public const string SectionAbout = "About";

    // ── Display & Audio ───────────────────────────────────────────────────────────

    // BigPictureIsSessionNote lived here: four lines at the top of the Big Picture card explaining
    // that opening Big Picture is what starts a couch session, and what closing it does.
    //
    // Removed as unnecessary where it stood. The card holds one dropdown about the Steam window, and
    // a reader who has reached the Display & Audio page has already started sessions; a definition of
    // what a session is was answering a question nobody had open at that moment.
    //
    // Worth knowing if it is ever reinstated: the last sentence claimed closing Big Picture brought
    // the desktop back "exactly as you left it", and OnBigPictureClosed calls _hdr.CloseRunningGame()
    // before _session.ReturnToDesktop() (TrayApp.cs:1482) — it closes the game. Any replacement has
    // to say so.

    // ── Session-end confirmation popup ─────────────────────────────────────────────
    // Each answer is two lines now: what it does, then what that costs. The action stays short enough
    // to scan, because three rows all opening with a verb are read as a shape rather than as words —
    // which is how someone ends up closing a game they meant to leave running.

    public const string ConfirmCloseTitle = "Your game is still running";
    public const string ConfirmCloseBody = "What should happen to it?";

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

    // The navigation option at the foot of both prompts. It does not answer the question the prompt is
    // asking; it is there for when the pad cannot reach the answer because the pointer is off. A
    // second row offered an on-screen keyboard and has been removed — see NavigationOptions.
    public const string PromptMouseOn = "Turn the controller mouse on";
    public const string PromptMouseOff = "Turn the controller mouse off";

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

    // BigPictureEndMinimize ("Swap to desktop" / "Big Picture stays open on the TV.") stood here as
    // this prompt's second answer. It went with the option: this prompt only appears when no game is
    // running, so going to the desktop with Big Picture left open behind it and ending the session
    // outright come to the same thing from where the reader is sitting. The game prompt keeps its own
    // version, where leaving something running is the entire point of the answer.

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

    // One sentence for one fact, wherever it appears. UpdateFound sat at the bottom of this file with
    // the identical text and was read by the About page while this one was read by the tray notice —
    // so editing the wording here changed it in one place out of two, silently.
    public const string NoticeUpdateFound = "Version {0} is available.";

    public const string CheckForUpdates = "Look for new versions";
    public const string CheckForUpdatesWhy =
        
            "Asks GitHub shortly after the app starts, when you open this window, and once "
            + "a day otherwise. **Nothing is ever installed on its own.** A new version "
            + "appears on the Home page with a button, and there is one on this page too; an "
            + "update only happens when you press one of them.";

    /// <summary>{0} is the version you are on, {1} the one on offer.</summary>
    public const string UpdateFromTo = "You have {0}  ·  new version {1}";

    public const string UpdateWhatChanged = "WHAT CHANGED";
    public const string UpdateLater = "Not now";

    public const string UpdateNoNotes =
        "This release came without notes.";

    public const string UpdateMoreChanges = "•  …and more";

    public const string UpdateEndsSession =
        "**A session is running.** Updating restarts the app, which will end it.";

    public const string UpdateCardTitle = "Version {0} is ready";
    public const string UpdateCardBody =
        
            "You are on {0}. This downloads the new version, swaps it in and restarts — a "
            + "few seconds, and your settings are untouched.";

    public const string UpdateCardButton = "Update now";
    public const string UpdateCardBusy = "Updating…";

    /// <summary>Shown instead of the button while a session is running.</summary>
    // Was: "so it waits until your session has ended", which promised a deferral nothing performs.
    // AddUpdateCard simply returns before it builds the button (SettingsForm.cs:3378) — there is no
    // queue, nothing retries when the session ends, and the offer only reappears when the window is
    // opened again. Saying the button is not on offer right now is both true and the same length.
    public const string UpdateCardDuringSession =
        "Updating restarts the app, which would end your session. It is offered again at your desk.";

    public const string ConfirmClosingGame = "Double-check before closing a running game";
    public const string ConfirmClosingGameWhy =
        
            "Puts one more question in the way of anything that would close the game you "
            + "are playing, naming it and saying what you lose. That question carries a "
            + "**stop double-checking** tick you can reach with the controller, which "
            + "switches this off from the sofa; here is where you turn it back on. Off makes "
            + "the first answer final — quicker, and with nothing between a mis-pressed "
            + "button and a closed game.";

    public const string SectionEndingSession = "Ending a session";

    public const string TurnOffTvOnExit = "Disconnect the Couch Session display when session ends";
    public const string TurnOffTvOnExitWhy =
        
            "Windows drops the TV from your desktop, so no windows or the mouse stray onto "
            + "a dark screen. **It comes back with the next session.** Turn off if you also "
            + "use the TV as a monitor.";

    public const string SwitchAudio = "Move sound to the TV";
    public const string SwitchAudioWhy = "Leave this off to keep hearing everything through your usual speakers or headset.";

    // ── Steam ─────────────────────────────────────────────────────────────────────

    // "Not recommended" is shown the same way everywhere: a red title (the titleEmphasis argument
    // where this is used) and a "**Not recommended.**" lead on the explanation, like DisableUac and
    // DisableFirewall below.

    // ── Shortcuts ─────────────────────────────────────────────────────────────────

    public const string ShortcutSession = "Start and end a session";
    // Says outright that this is the guide button by another name.
    //
    // The two settings sit on the same page doing the identical thing, and nothing said so — so a
    // reader could reasonably set both, or wonder which of them was the real one. Naming the overlap
    // also answers why anybody would want this: a controller with no guide button, or a preference
    // for a combination of your own.
    public const string ShortcutSessionWhy =
        
            "**The same thing the PS / Xbox button does**, on a key or button combination "
            + "of your own. Worth setting if your controller has no guide button, or if you "
            + "would rather not use it.\n\nWorks anywhere in Windows, even over a game. Click a "
            + "box and press the keys, or hold **two or more** buttons together so it cannot "
            + "fire mid-game.";

    // "Works with any pad Windows recognises" was a claim about compatibility, and only Xbox pads and
    // the PS5 DualSense have actually been tried. The mechanism is standard HID and XInput so the rest
    // should follow, but should-follow and tested are different things and the text now says which.
    public const string ShortcutControllerNote =
        
            "Tested with Xbox pads and the PS5 DualSense; other controllers Windows "
            + "recognizes should work too. No extra software either way. Your controller is "
            + "**never taken over** — it is opened for reading only and never exclusively, so "
            + "it runs alongside Steam Input, DS4Windows and games.";

    // ── First-run welcome ─────────────────────────────────────────────────────────
    public const string WelcomeTitle = "Welcome to Couch Session";

    public const string WelcomeBody =
        
            "One press moves your PC to the television: display, sound, HDR and Big "
            + "Picture. One more brings it all back, exactly as you left it.";

    // The welcome asks one question instead of pointing at one page.
    //
    // It used to say "start on Display & Audio and pick your television", with a second paragraph
    // for people who have no television explaining that they could use it for HDR alone and should
    // "leave the television side switched off". That paragraph was doing the work a choice should
    // do: it described a mode, in prose, and left the reader to assemble it by hand out of settings
    // spread over four pages.
    //
    // Asking outright is better on both sides. Somebody with a TV gets sent to the one page they
    // have to fill in, and somebody without one gets an app with the television half already gone
    // rather than an app they have to prune. It is also the only moment the question is cheap: it is
    // the first thing they see, nothing is set up yet, and either answer can be changed later on the
    // Home page.
    public const string WelcomeChoose = "**How will you use it?**";

    public const string WelcomeCouch = "On my television";
    public const string WelcomeCouchWhy =
        "The full app. Pick your TV and speakers next; everything else has a sensible default.";

    public const string WelcomeDesk = "At my desk only";
    public const string WelcomeDeskWhy =

            "HDR and performance, with the couch half switched off. HDR comes on when a game "
            + "starts and goes off when it quits, so the desktop is never left washed out.";

    /// <summary>Under both answers, so neither reads as a decision that cannot be undone.</summary>
    public const string WelcomeEither = "You can change this at any time on the General page.";

    // ── Home page guidance ────────────────────────────────────────────────────────
    // No ** in either of these, unlike every other string in this file.
    //
    // [BUG] The banner paints its own text with TextRenderer rather than through RichNote, and
    // TextRenderer has never heard of the ** convention — so the markers were drawn as literal
    // asterisks. The first thing a new user read was "the **PS button** on a PlayStation one".
    //
    // The banner is not being converted to RichNote to fix that. Emphasis inside a four-line grey
    // paragraph is decoration anyway; what the paragraph needed was to be shorter, which it now is.
    // Short declarative sentences in a fixed order — what to press, what it is called, what it does
    // at the desk, what it does on the TV, what to do if you have not got one — do the work the bold
    // was there to fake.
    public const string HomeGuideBannerTitle = "Press the button in the middle of your controller to start a Couch Session!";
    public const string HomeGuideBannerBody =
        
            "Guide on an Xbox pad, PS logo on a PlayStation, HOME on a Switch Pro, STEAM on "
            + "a Steam Controller. One press at your desk moves everything to the TV. One "
            + "press on the TV asks what to do next. If your pad has no such button, the "
            + "Hotkeys page does the same job.";

    public const string HomeBorderlessTip =
        
            "**Set games to borderless fullscreen for the best experience.**  Exclusive "
            + "fullscreen gives the game the whole display, so it minimizes when it loses "
            + "focus — which is what happens when the session prompt appears, or when HDR or "
            + "the resolution changes mid-session. Borderless keeps the game on screen "
            + "through all of it.";

    // Was: "End a session with the PS / Xbox button". CheckGuideButton returns early when this is off,
    // before GuideStartsSession is ever reached — so switching it off also stops the button starting a
    // session from the desktop and resuming a minimized one. The title named only the half someone
    // would want to switch off, so switching it off took away two things they never agreed to lose.
    public const string GuideEndsSession = "Use the PS / Xbox button to start and end sessions";
    // Kept next to the other session-end wording so the two stay consistent.
    public const string GuideEndsSessionWhy =
        
            "Press it mid-game and Steam opens its Big Picture overlay, which takes the "
            + "controller off the game — then this asks what to do with the session. **The "
            + "button is only listened for, never intercepted**, so Steam still does "
            + "everything it normally does with it.";

    public const string ShortcutNone = "Not set — click to choose";
    public const string ShortcutListenKeyboard = "Press a combination…";
    public const string ShortcutListenController = "Hold the buttons, then let go…";
    public const string ShortcutNoPad = "No controller detected";
    public const string ShortcutNeedsModifier = "Add Ctrl, Alt or Shift";
    public const string ShortcutNeedsTwoButtons = "Two buttons or more";
    public const string ShortcutClear = "Clear";
    public const string ShortcutClearWhy = "Remove this shortcut so nothing is bound to it.";

    public const string ShortcutTaken =
        
            "**{0} is already being used by something else on this machine**, so it could "
            + "not be claimed. Pick a different combination.";

    // ── HDR Switching ─────────────────────────────────────────────────────────────
    //
    // Named "HDR Switching", never "Auto HDR". Windows has its own feature called Auto HDR that does
    // something entirely different — it upscales SDR games to HDR — and sharing the name made it
    // impossible to tell, in a sentence, which of the two was being talked about. This one switches the
    // display's HDR setting on and off around a game; that one invents HDR that was never there.

    public const string HdrMode = "Turn HDR on automatically";
    public const string HdrModeWhy =
        
            "Primary display only — the TV in a session, or your monitor at the desk. HDR you "
            + "had on beforehand is left alone, unless **Smart HDR** has tied it to the game "
            + "you are playing. **Per "
            + "game** switches it on when a game from the list below starts, and off when "
            + "that game closes. **For the whole session** switches it on as the session "
            + "begins and leaves it there until the session ends.";
    public static readonly string[] HdrModeOptions = [
        "Off",
        "Per game",
        "For the whole session",
    ];

    // Eight strings stood here, describing each display and whether it reported HDR support:
    // HdrDisplayPart and its three role names, the two support answers, the switched-off case and
    // the nothing-found case.
    //
    // Gone with the note that used them. Somebody who owns a display knows whether it does HDR, and
    // a page whose subject is which games to tick opened with four lines telling them about their
    // own hardware. The disagreement those strings were carefully worded around — the panel's EDID
    // answer against Windows' own — has not gone anywhere, and is still in the log once per display
    // per run for anyone diagnosing a display that will not switch.

    // Shown in place of a live games list when HDR is set to run for the whole session.
    // The priority-boost sentence that used to end this is gone. It said this list was what
    // "Raise the game's priority" reads and told the reader to leave whole-session mode to add a
    // game for it — untrue since that setting started acting on every game the app can find, and
    // flatly contradicted by its own description one page away.
    public const string HdrWholeSessionNote =
        
            "**This list is not used in this mode.** HDR comes on as the session starts and "
            + "goes off when it ends, so there is nothing to pick. Switch back to **per "
            + "game** above to edit it.";

    // Yours, restored. I overwrote this once by writing my whole copy of the file over the disk
    // without re-reading it first, and the wording below is what was lost — put back verbatim.
    public const string HdrHotkeyRemember = "Smart HDR - HDR list learns from your input";
    public const string HdrHotkeyRememberWhy =
        
            "Toggle HDR on during a game — or before it launches — adds it to the list and "
            + "enables it. The hotkey, the controller shortcut, or a game launched with HDR "
            + "already on all count. If you toggle HDR off in game it also disables it on the "
            + "list.";
    public const string TipHdrBulk = "Tick every game known to render HDR natively.";
    public const string TipHdrBulkOther =
        
            "Tick everything not known to render HDR natively — **Windows' own Auto HDR** "
            + "(a different feature, which invents HDR from an SDR game) may still improve "
            + "those.";
    public const string TipSelectAll = "Tick every game shown.";
    public const string TipClearAll = "Untick every game shown.";
    public const string TipBrowse = "Add a game that was not found automatically.";
    public const string TipScan = "Look for newly installed games right now, instead of waiting for the next check.";

    // Shown in the footer, not a toast — see SettingsForm.ShowFooterNote.
    public const string NoticeScanFound = "Found {0} new game(s)";
    public const string NoticeScanNothing = "No new games found";

    // ── Performance ───────────────────────────────────────────────────────────────

    // Two of each, because in desk-only mode these hang off a running game rather than a session and
    // the shipped wording would be a plain untruth. The setting is the same setting; what it waits
    // for is not, and a description that names the wrong trigger is the kind of thing this file
    // exists to stop.
    public const string ChangePowerPlanDesk = "Change the Windows power plan while a game runs";
    public const string ChangePowerPlanDeskWhy =

            "Switches to the plan below while a game is running and restores yours when it closes, "
            + "slider included. Off leaves your power settings untouched.";

    public const string SilenceNotificationsDesk = "Silence Windows notifications while a game runs";
    public const string SilenceNotificationsDeskWhy =

            "Hides Windows notifications until the game closes, so a corner card cannot land over "
            + "something fullscreen.";

    public const string ChangePowerPlan = "Change the Windows power plan for a session";
    public const string ChangePowerPlanWhy =
        
            "Switches to the plan below during a session and restores yours after, slider "
            + "included. Off leaves your power settings untouched.";

    public const string PowerPlanPick = "Plan to use";
    public const string PowerPlanPickWhy =
        
            "**Automatic** picks the fastest plan you have and won't slow you down if "
            + "you're already faster. Or name your own and it's used as-is.";
    public const string PowerPlanAuto = "Automatic — the fastest one you have";

    public const string SilenceNotifications = "Silence Windows notifications";
    public const string SilenceNotificationsWhy =
        
            "Hides Windows notifications until the session ends — a corner card becomes a "
            + "TV-sized banner you can't dismiss with a pad.";
    public const string EnableGameMode = "Windows Game Mode";
    // Was "Turn on Windows Game Mode", described as something that "stays on afterwards". Both were
    // written for a switch that only ever turned it on, and only did so when a session started —
    // which is why pressing it appeared to do nothing at all. It is the Windows setting now, read
    // and written directly, so the title is the name of the thing rather than an instruction.
    public const string EnableGameModeWhy =
        
            "Asks Windows to prioritize whichever game is running and hold back background "
            + "work. **This is the same switch as Windows Settings ▸ Gaming ▸ Game Mode**, "
            + "changed here as soon as you press it — it is not tied to a session and nothing "
            + "puts it back afterwards.";

    public const string NoticeGameModeOn = "Windows Game Mode is on";
    public const string NoticeGameModeOff = "Windows Game Mode is off";
    public const string WarnGameModeFailed =
        
            "Windows would not change the Game Mode setting. Nothing has been altered. You "
            + "can set it in Windows Settings under Gaming ▸ Game Mode.";
    public const string GamePriority = "Raise the game's priority";
    public const string GamePriorityWhy =
        
            "Gives whichever game is running more processor time, and gives it back when "
            + "that game closes. Applies to **any game the app can find** — Steam, Epic and "
            + "GOG libraries — not just the ones ticked for HDR. Some games block it (often "
            + "anti-cheat) and it is skipped quietly.";

    // ── General ───────────────────────────────────────────────────────────────────

    public const string StartWithWindows = "Start with Windows";
    // The second sentence is the one catch this setting has, and it had none.
    //
    // Windows records where this file is, not what it is called, so moving the app leaves the
    // sign-in launch pointing at a folder that is no longer there. It repairs itself the next time
    // Couch Session runs — but that has to happen, and somebody who moves the app and then reboots
    // gets one sign-in where nothing starts and nothing explains why. Saying so costs a line.
    public const string StartWithWindowsWhy =

            "Launches to the tray as you sign in — ahead of the usual startup delay — ready "
            + "to switch displays and manage HDR.\n\n**If you move Couch Session to another folder, "
            + "open it once from its new home so Windows knows where to find it.**";

    /// <summary>Shown when a launch found the sign-in entry pointing somewhere the app no longer is.</summary>
    public const string WarnStartupRepointed =
        "Couch Session had moved, so \"Start with Windows\" was pointing at the old folder. "
      + "That is fixed now, though the last sign-in may have missed it.";

    public const string StartOnLaunch = "Start a session when Couch Session opens";
    public const string StartOnLaunchWhy =
        
            "Moves to the TV and opens Big Picture as soon as the app starts. With \"Start "
            + "with Windows\" on, signing in drops you straight onto the couch. Off by "
            + "default.";

    public const string StartOnWake = "Start a session when the PC wakes from sleep";
    public const string StartOnWakeWhy =
        
            "When the PC resumes from sleep, switch to the TV and open Big Picture — handy "
            + "if you wake it from the sofa. Off by default.";

    public const string StartOnWakeControllerOnly = "Only when the controller wakes the PC";
    public const string StartOnWakeControllerOnlyWhy =
        
            "Start on wake only when the controller woke the PC. A mouse or keyboard wake "
            + "then goes to the desktop instead. Off by default.";

    public const string MinimizeOnClose = "Minimize to system tray";
    public const string MinimizeOnCloseWhy =
        
            "Closing this window leaves the app in the tray, where HDR and controller "
            + "switching keep working. Off means closing quits. You can quit from the tray "
            + "either way.";
    public const string ShowNotifications = "Show notifications";
    public const string ShowNotificationsWhy =
        
            "The small cards Couch Session shows in the corner of the screen. Off silences "
            + "**every** one of them; nothing else changes, and anything that went wrong is "
            + "still on the Home page and in the log.";

    public const string ShowSessionNotifications = "When a session starts and ends";
    public const string ShowSessionNotificationsWhy =
        
            "A message on the screen you are moving to, so you know the switch worked — and "
            + "again when your desktop is back.";

    public const string ShowHdrNotifications = "When HDR turns on and off";
    public const string ShowHdrNotificationsWhy =
        "Confirms HDR Switching did its job as each game opens and closes. It keeps working either way.";

    public const string ShowMinimizeNotification = "When the app keeps running in the tray";
    public const string ShowMinimizeNotificationWhy =
        "Closing this window does not close the app. Turn this off once you know where it went.";

    public const string ShowProblemNotifications = "When something goes wrong";
    public const string ShowProblemNotificationsWhy =
        
            "A hotkey another program already holds, or a change Windows refused. Rare, and "
            + "each one is something that stops a feature working.";

    /// <summary>The same row without UAC and the firewall, which desk-only mode does not offer.</summary>
    public const string ShowActionNotificationsWhyDesk =

            "Confirms a bug report was saved, or that a new version is available. Everything "
            + "here is a short message in the corner; nothing waits for an answer.";

    public const string ShowActionNotifications = "When something you pressed is done";
    public const string ShowActionNotificationsWhy =
        
            "Confirms a bug report was saved, that UAC or the firewall has changed, or that "
            + "a new version is available. Everything here is a short message in the corner; "
            + "nothing waits for an answer.";
    public const string DisableUac = "Turn off User Account Control (UAC)";
    public const string DisableUacWhy =
        
            "**This lowers your PC's security.** UAC prompts appear on a secure screen a "
            + "controller can't reach. This elevates admin apps silently so those dead ends "
            + "go away. Needs an admin prompt, **applies right away, no restart.** It is not "
            + "tied to a session — it stays off until you switch it back on here.";
    public const string NoticeUacChanged = "UAC setting changed";
    public const string NoticeUacRestart = "Admin prompts are now silent.";
    public const string NoticeUacOn = "UAC prompts are back on.";

    public const string DisableFirewall = "Turn off Windows Firewall";
    public const string DisableFirewallWhy =
        
            "**This lowers your PC's security.** Windows Firewall's 'allow this app?' "
            + "prompt needs a mouse and blocks a game's networking until answered. Turning it "
            + "off stops those prompts. Needs an admin prompt, applies right away. It is not "
            + "tied to a session — it stays off until you switch it back on here.";
    public const string NoticeFirewallChanged = "Firewall setting changed";
    public const string NoticeFirewallOff = "Windows Firewall is now off.";
    public const string NoticeFirewallOn = "Windows Firewall is now on.";

    // The title says "switch off" and the description used to say "this is the same switch as
    // Windows Settings ▸ Gaming ▸ Xbox Game Bar" — which it is the inverse of. Two adjacent rows,
    // Game Mode and this one, carried that identical sentence with opposite polarity: turning Game
    // Mode on turns the Windows setting on, turning this on turns the Windows setting off.
    public const string DisableGameBarButton = "Switch off the Xbox Game Bar";
    // Was: "switches it off during a session and back on when you quit". Two things wrong with that.
    // GameBarControl is applied from TrayApp at startup and restored at app exit, not per session — so
    // it is off the whole time Couch Session is running, and "when you quit" read as quitting the game.
    // And it clears AppCaptureEnabled and GameDVR_Enabled as well, which is background clip recording:
    // someone who records their play would have lost it with nothing here to say so.
    public const string NoticeGameBarOn = "Xbox Game Bar is on";
    public const string NoticeGameBarOff = "Xbox Game Bar is off";
    public const string WarnGameBarFailed =
        
            "Windows would not change the Game Bar setting. Nothing has been altered. You "
            + "can set it in Windows Settings under Gaming ▸ Xbox Game Bar.";

    public const string DisableGameBarButtonWhy =
        
            "The Xbox Guide button opens the Game Bar over your game, which is awkward from "
            + "the couch. **Switching this on turns the Xbox Game Bar off** in Windows "
            + "Settings ▸ Gaming, as soon as you press it and for good — closing this app "
            + "does not put it back. Background clip recording goes off with it. PlayStation "
            + "pads do not trigger it.";

    // One three-way choice, replacing two switches where the second only meant anything while the
    // first was on — and where one of the three states was reachable two ways, since the prompt's own
    // "leave it for now" answer did the same thing as turning the second switch off. Same shape as
    // HdrModeOptions, for the same reason.
    // Answers that read as answers on their own.
    //
    // These were "Ignore it", "Come back to the desktop" and so on, with a paragraph above the card
    // explaining what each of them meant. If a list of options needs a paragraph to explain it, the
    // options are the thing to fix: "Ignore it" leaves the reader asking *ignore what*, and "come
    // back" never says who is coming back. Every entry is now a plain verb phrase describing what
    // the app will do, so the list can be read without reading anything else.
    //
    // Two lists, and the difference is deliberate. Closing the game is offered for a power-off and
    // nothing else: somebody who held the guide button until their controller shut down has said
    // they are finished and is allowed to mean it, while a controller that went flat has said
    // nothing at all. Putting "close the game" in front of that event would hand a dead battery the
    // power to end somebody's evening.
    public static readonly string[] DisconnectOptions =
        [
        "Do nothing",
        "Switch back to the desktop",
        "Switch back and ask me what to do",
        "Close the game and end the session",
    ];

    public static readonly string[] DisconnectOptionsLost =
        [
        "Do nothing",
        "Switch back to the desktop",
        "Switch back and ask me what to do",
    ];

    // Two questions, not one, because they are two events with two right answers.
    //
    // This was a single choice covering any disconnect, which forced one answer onto both "I have
    // finished playing" and "my battery just died". They are told apart now — powering a controller
    // off means holding the guide button, and the pad reports that right up until it stops
    // reporting anything, so a disconnect moments after a long hold has one ordinary explanation.
    // See TrayApp.WasPoweredOff.
    //
    // Deliberately not a master switch with a sub-option, which is the shape this setting was in
    // once before and had to be rescued from. Neither of these depends on the other; both are always
    // live, and each says which event it is about in its own title.
    public const string DisconnectOff = "When you switch it off";
    // What the event is, in the reader's own terms. Not why it matters, not how it is detected —
    // those were both tried and both made this longer without making it clearer.
    public const string DisconnectOffWhy =
        "You held the guide button until the controller powered down.";

    public const string DisconnectLost = "If it disconnects on its own";
    public const string DisconnectLostWhy =
        
            "The battery ran out, a cable came loose, or the wireless dropped. You may "
            + "still be sitting there, so this does nothing unless you say otherwise.";
    // Three short paragraphs, one per thing worth knowing: what "come back" means, what "and ask"
    // adds, and when to pick neither. It was one dense block that answered all three at once and had
    // to be read twice — on a setting somebody meets on their first run.
    // What the three options mean, said once above both pickers rather than twice inside them.
    // One sentence, and only the part nobody would guess.
    //
    // This was two paragraphs restating every option in the two lists below it — which is the shape
    // a card ends up in when the options are unclear and the fix is attempted above them instead of
    // inside them. With the options rewritten there is exactly one thing left worth saying, and it
    // is the one thing a first-time reader would get wrong: switching back does not close anything.
    //
    // The closing clause was added because the sentence above was true of what it described and
    // untrue of the card it headed. AddNote puts this above both pickers (SettingsForm.cs:2837) and
    // the first of them is fed DisconnectOptions, whose fourth answer closes the game — so a card
    // left on "Close the game and end the session" opened by promising the game would keep running,
    // in bold, an inch above the control set to close it.
    //
    // Qualified rather than moved, and rather than rewritten as the picker changes. The second
    // picker takes DisconnectOptionsLost, which has no close option, so the note is true of that
    // list as it stands and only ever needed the exception named for the first. And text that
    // rewrites itself when you touch a different control is text you have to watch rather than
    // read — see the note above TriggerPad, which is the same argument.
    public const string DisconnectNote =

            "**Switching back to the desktop leaves your game running** — turn the "
            + "controller on again and you are straight back where you were. Closing the "
            + "game is the one answer that does not.";

    // ── The prompt that lands on the desk after a controller disconnects ──────────
    public const string DisconnectTitle = "Your controller disconnected";
    public const string DisconnectBody =
        
            "Everything is still running. Switch the controller back on to go straight back "
            + "in, or pick one of these.";

    public const string DisconnectResume = "Go back to the session";
    public const string DisconnectResumeWhat = "Puts the TV back and returns you to the game.";

    public const string DisconnectClose = "Close the game";
    public const string DisconnectCloseWhat = "Shuts the game down and leaves you at your desk.";

    public const string DisconnectLeave = "Leave it for now";
    public const string DisconnectLeaveWhat = "Stays on the desktop with the game running.";

    // The tick on the "are you sure" prompt. Named for the setting it switches off rather than
    // "don't show this again", so it is clear the check is going away and not just this one window.
    //
    // A second paragraph here belonged to DisconnectDontAsk below and described the disconnect
    // setting dropping "one step, to 'Come back to the desktop'" — an option renamed to "Switch back
    // to the desktop" when that list was rewritten. Two settings' worth of reasoning over one
    // constant, half of it naming something that no longer exists.
    public const string SureCloseDontAsk = "Stop double-checking before closing a game";

    public const string DisconnectDontAsk = "Stop asking me this — just switch back to the desktop";

    public const string StartOnController = "Start a session when it connects";
    public const string StartOnControllerWhy =
        
            "Switching a controller on is usually the moment you sit down to play, so this "
            + "moves everything to the TV and opens Big Picture.";

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

    // ── The session messages, and the rest that were written into the code ─────────
    //
    // These nine were the exception to the promise at the top of this file. They lived as literals in
    // SessionController, TrayApp and Program, so anybody who came here to change what the app says —
    // which is the entire reason this file exists — would have searched, found nothing, and concluded
    // the words were not theirs to change.
    //
    // The first two pairs are the most-seen text in the whole application. They appear every time
    // somebody sits down and every time they get up again.

    /// <summary>Shown on the television as a session begins. Title, then the line under it.</summary>
    public const string NoticeSessionOn = "Couch Session is on";
    public const string NoticeSessionOnDetail = "Display and audio moved to the TV.";

    /// <summary>Shown on the desktop as a session ends.</summary>
    public const string NoticeSessionOff = "Back on the desktop";
    public const string NoticeSessionOffDetail = "Displays, audio and windows restored.";

    /// <summary>The HDR hotkey, when the display cannot do HDR at all.</summary>
    public const string WarnHdrUnsupported = "The main display does not support HDR.";

    /// <summary>The HDR hotkey, when Windows refused the change.</summary>
    public const string WarnHdrRefused = "Windows would not change HDR on the main display.";

    /// <summary>A session was asked for before a television had been chosen.</summary>
    public const string NoticeNotSetUp = "Couch Session is not set up yet. Opening settings.";

    /// <summary>{0} is the reason Windows gave.</summary>
    public const string WarnDiagnosticsWriteFailed = "Could not write the diagnostics report: {0}";

    /// <summary>{0} is what was being attempted, {1} the reason it stopped.</summary>
    public const string WarnBackgroundFailed = "{0} failed.\n{1}";

    /// <summary>
    /// The box shown when the app cannot start at all. {0} is the reason, {1} the settings folder.
    ///
    /// This one is not governed by the notification switches and cannot be: it is shown before the
    /// settings have been read, so there is nothing yet to consult.
    /// </summary>
    public const string FatalStart =
        "Couch Session hit an error and had to stop.\n\n{0}\n\nDetails were written to:\n{1}";

    /// <summary>
    /// The failsafe: the television never showed a picture, so everything was put back. {0} is why.
    ///
    /// Shown as a proper dialog rather than a corner card, because it is the one message that asks
    /// the reader to go and do something about it, and a toast that times out cannot ask anything.
    /// Not governed by the notification switches for the same reason.
    /// </summary>
    public const string WarnTvNotLive =
        "Couch Session could not get a picture on the TV, so your displays and audio "
      + "have been restored.\n\n"
      + "Reason: {0}.\n\n"
      + "Check that the TV is switched on and set to the correct input, then try again.";

    public const string ReportBug = "Report a bug";
    // Shown on the About page, under the row of buttons there — none of which is Report a bug, which
    // lives in the footer. It used to describe that footer button and sat directly beneath "Save
    // diagnostics", which writes a different file, so it read as instructions for the wrong control.
    public const string ReportBugWhy =
        
            "**Open settings folder** shows where your settings and log live. **Save "
            + "diagnostics** writes one file to your desktop with the log, your settings and "
            + "your display and audio details — attach it to a bug report.";

    /// <summary>The tooltip on the footer button, which is the one that files a report.</summary>
    public const string TipReportBug =
        "Saves a diagnostics file to your desktop, then opens the report page. Attach that file.";

    public const string WarnBugReportFailed = "The bug report could not be saved. {0}";

    public const string NoticeBugReport = "Bug report saved";
    public const string NoticeBugReportDetail =
        "{0} is on your desktop. Attach it to the report that just opened.";

    public const string NoticeShortcutTaken = "Shortcut not available";
    public const string NoticeMinimized = "Still running";
    public const string NoticeMinimizedDetail =
        
            "Couch Session is in the notification area. Double-click its icon for settings, "
            + "or right-click to quit.";

    // ── Dropdowns ──────────────────────────────────────────────────────────────────

    // Appended to displays that are known about but not currently switched on.
    public const string DisplayDisconnected = "— disconnected";

    // A comment here described a suffix appended to whichever display was currently the Windows
    // primary. That constant is gone; the comment outlived it and sat directly above TvDisplay,
    // reading as a description of the wrong thing.
    public const string TvDisplay = "Couch Session display";
    // Was: "Only displays Windows can see right now are listed — switch your TV on and it appears
    // here." ListDisplays queries AllPaths and keeps inactive ones, sorted live-first and shown dimmed
    // with DisplayDisconnected after the name. So the television is already in the list with the set
    // switched off — and the old wording sent the user off to switch it on in the app's single most
    // common situation.
    public const string TvDisplayWhy =
        
            "The screen to play on. A TV that is switched off is still listed, dimmed and "
            + "marked disconnected, so you can set this up without getting off the sofa "
            + "first.";
    public const string TvVideoMode = "Couch Session resolution and refresh rate";

    public const string TvVideoModeWhy =
        
            "Windows often runs a TV at 60 Hz when it can do more. The best mode is marked; "
            + "**scaled** modes are the wrong shape and letterbox or stretch. The TV's mode "
            + "is restored when you leave.";

    public const string VideoModeAuto = "Leave it to Windows";

    // Appended to whichever mode is the display's best. Starts with a space on purpose.
    public const string VideoModeBest = "   ← best for this display";

    // Appended to modes that are a different shape from the display. Kept very short: this sits
    // inside a dropdown row, and anything longer is cut off mid-word with no way to read it.
    public const string VideoModeScaled = "   · scaled";

    // Replaces the "switched off" note when remembered modes are being shown instead.
    public const string VideoModeRemembered =
        
            "**{0} is switched off**, so these are the modes it reported last time. They "
            + "are checked again when it comes back.";

    // Shown under the picker when the chosen display is switched off or disconnected, so an
    // almost-empty list reads as a reason rather than as a fault.
    public const string VideoModeUnavailable =
        
            "**{0} is not switched on**, so Windows cannot say what it supports. Switch it "
            + "on and this list fills in. Until then a session uses whatever Windows picks.";

    public const string DisplayMode = "Other displays";
    public const string DisplayModeWhy =
        
            "**Turning them off** stops games opening on the wrong screen. **Keeping them "
            + "on** leaves a browser or Discord visible on your monitor. Either way they are "
            + "put back when the session ends.";
    public const string DisplayModeGameNote =
        
            "Some games reopen on the exact monitor they remember, whatever is primary. If "
            + "one keeps landing on the wrong screen, choose **Turn them off while I'm on the "
            + "TV** — then it has nowhere to go but the TV.";
    public const string TvAudio = "Couch Session audio";
    public const string TvAudioWhy =
        "The HDMI output your TV is connected to — often named after your graphics card.";
    public const string TvAudioHdmiNote =
        
            "A TV playing sound over HDMI must be on for its audio device to appear. One "
            + "seen before stays selectable — dimmed as **off** — so you can set this up with "
            + "the TV cold.";

    // Appended to audio devices that are remembered but not switched on right now.
    public const string AudioDeviceOff = "— off";

    public const string MuteOnDesktop = "Mute a game left running on the desktop";
    public const string MuteOnDesktopWhy =
        
            "**Recommended.** A game does not know you have put it down. When you go back "
            + "to the desk with it still running — from **Swap to desktop**, or because your "
            + "controller disconnected — this silences it until you return, then puts the "
            + "sound back exactly as it was. Nothing else on your PC is affected. Turn it off "
            + "if you leave a game running on purpose to listen to it.";

    // Shown when Windows is already on the couch audio device as a session begins, so there is
    // nothing to move and no earlier device to come back to.
    //
    // The log used to assert "a previous restore must have failed", which is one of two explanations
    // and not the likelier one. The other — the couch device and the everyday device being the same
    // choice — is a settings mistake the user can fix in ten seconds, and they were told nothing
    // about it. Neither cause can be told apart from here, so this names the state, not the cause,
    // and gives the fix that works for both.
    // Written to be read in the two seconds a toast is looked at. It had three sentences covering
    // what happened, why it happened, and two different ways to fix it — which is a paragraph, and a
    // paragraph in the corner of the screen gets dismissed rather than read. The situation and one
    // place to go is all a notification can carry; the settings page explains itself when they get
    // there.
    public const string NoticeAudioAlreadyThere = "Couch audio is already in use";
    public const string NoticeAudioAlreadyThereDetail =
        "It is your desktop device too, so nothing moves. Change it on Display & Audio.";

    public const string DesktopAudio = "Desktop audio";
    public const string DesktopAudioWhy = "The device to return to when you finish.";
    public const string SteamAfter = "Steam window on exit";
    public const string SteamAfterWhy =
        
            "Steam reopens its desktop window when Big Picture closes. Its previous size "
            + "and position are restored first.";
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
    // Was "No displays found — is the TV on?", which sent the reader to switch on a television that
    // would not have changed the answer. ListDisplays keeps inactive displays deliberately, so a
    // switched-off TV is already in this list, dimmed (SettingsForm.cs:4597) — this line only appears
    // when Windows reports no displays whatsoever, which is a fault rather than a cold television.
    public const string NoDisplaysFound = "No displays found — Windows is not reporting any.";

    public const string GamesListNote =
        
            "Steam, Epic and GOG games are found automatically; add anything else with "
            + "Browse. The Native HDR tag can be wrong for some games — click any tag to "
            + "change it.";

    /// <summary>{0} is the version, already including "v" and the channel. {1} is the author.</summary>
    public const string AboutVersion = "{0}  ·  Created by {1}";

    // Was "Sit down, switch a controller on, and your PC moves to the television", which describes a
    // trigger that ships switched off — so the app's own headline sentence was untrue of a fresh
    // install. One press is true of every install: the guide button, the hotkey, or Big Picture.
    // The list was "display, sound, HDR, resolution and power plan, all at once", and three of those
    // five ship switched off: AutoHdrEnabled and ChangePowerPlan both default to false and TvVideoMode
    // defaults to empty, which is "Leave it to Windows". So the sentence directly above the version
    // number was untrue of every new install, in the same way the opening line was before it — see the
    // note this replaces. Display and sound are what one press moves out of the box; the rest is real
    // and worth naming, as something you switch on.
    public const string AboutBlurb =

            "One press and your PC moves to the television — display and sound together, "
            + "and HDR, resolution and the power plan too once you switch them on. Get up and "
            + "it puts every one of them back exactly as it was.\nNo mouse, no keyboard, no "
            + "settling for the wrong screen. Just the couch.";

    // Warnings. \n is a line break.

    public const string WarnNoNativeGames =
        
            "None of your installed games are known to support HDR natively yet.\n\nThe list "
            + "updates in the background, so this may fill in shortly.";

    public const string WarnAllNative = "Every game found is already tagged as having native HDR.";

    // The message above was also shown when the library was empty, which is a perfectly ordinary
    // first-run state now that nothing is discovered until a scan finds it.
    public const string WarnNoGames =
        "No games have been found yet. Use **Scan** to look again, or **Browse** to add one by hand.";
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
    public const string FeatureControllerOff = "Coming back to the desk if a controller disconnects";

    // ── Resetting ──────────────────────────────────────────────────────────────────

    // Shown beside Resume Session, and only while a game is waiting behind the desktop. Named for
    // what it does rather than "End Session", because no session is running at that point — see
    // SettingsForm.RefreshCloseGameButton.
    public const string ButtonCloseGame = "Close Game";
    public const string TipCloseGame =
        
            "Close the game still running behind your desktop. Nothing else changes — you "
            + "are already at your desk.";

    public const string CloseGameTitle = "Close the game?";
    public const string CloseGameBody =
        
            "The game left running behind your desktop will be asked to close, the same way "
            + "Alt+F4 asks it to, so it saves and exits itself. **Anything you have not saved "
            + "in-game is up to the game.**";
    public const string CloseGameYes = "Close the game";
    public const string CloseGameNo = "Leave it running";

    public const string ButtonReset = "Reset all settings";

    public const string TipReset =
        
            "Puts every setting back to how it was when the app was new, including your "
            + "displays, audio devices and HDR game list.";

    public const string AskClose = "Close";

    // Buttons named after what they do, not "Yes" and "No". Someone skimming reads the buttons and
    // nothing else, so the buttons have to carry the decision on their own.
    public const string ResetConfirmYes = "Reset everything";
    public const string ResetConfirmNo = "Keep my settings";

    public const string ResetPageYes = "Reset this page";
    public const string ResetPageNo = "Leave it alone";

    public const string ResetConfirmTitle = "Reset all settings";

    public const string ResetConfirm =
        
            "This puts every setting back to its default, including your chosen display, "
            + "your audio devices and every game ticked for HDR Switching.\n\n**Windows' own "
            + "settings are left alone** — Game Mode, the Xbox Game Bar, User Account "
            + "Control, the firewall and Start with Windows are not this app's to "
            + "reset.\n\nThis cannot be undone. Continue?";

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
        
            "This puts the settings on {0} back to their defaults.{1}\n\nNothing on the other "
            + "pages is touched. This cannot be undone. Continue?";

    /// <summary>{0} is the page name.</summary>
    public const string ResetPageDone = "{0} reset to defaults";

    // What each page loses beyond its switches, spelled out in the confirmation because these are
    // the parts someone would not expect a page-level reset to take with it.

    public const string ResetPageLosesDisplay =
        
            "\n\nYour chosen television, its video mode and both audio devices are cleared, "
            + "and the app goes back to guessing them.";

    public const string ResetPageLosesHdr =
        "\n\nEvery game ticked for HDR Switching is cleared as well.";

    public const string ResetPageLosesController =
        
            "\n\nThe controller chosen to start a session is cleared, so any connected "
            + "controller can start one again — and any names you have given your controllers "
            + "go back to what Windows calls them.";

    // The session keyboard shortcut has no default — AppConfig.ShortcutKeyboard is 0, meaning unset —
    // so for that one "goes back to its original buttons" would have been a promise to restore
    // something that never existed.
    public const string ResetPageLosesHotkeys =
        
            "\n\nThe controller shortcuts and the HDR keyboard shortcut go back to their "
            + "original buttons, and the keyboard shortcut for starting a session is cleared. "
            + "The PS / Xbox button setting goes back to its default too.";

    /// <summary>
    /// Shown under the Performance page's reset, because two of its switches are not the app's to
    /// reset — they change Windows itself and are read back from it.
    /// </summary>
    // All four, where this used to name two. Game Mode and the Game Bar became live mirrors of the
    // Windows settings and are re-read from Windows straight after any reset, so the button leaves
    // them alone in practice — it just did not say so.
    public const string ResetPageKeepsWindows =
        
            "Game Mode, the Xbox Game Bar, User Account Control and the firewall are left "
            + "exactly as they are — those are Windows' own settings, not this app's, so this "
            + "button will not change them.";

    // ── Defaults ───────────────────────────────────────────────────────────────────
    //
    // {0} is replaced with the two words below it.

    public const string DefaultIs = "Default: {0}";
    public const string DefaultOn = "On";
    public const string DefaultOff = "Off";

    // ── Which controller ───────────────────────────────────────────────────────────

    public const string MouseControl = "Move the pointer with your controller";
    public const string MouseControlWhy =
        
            "For launchers and windows that ignore a controller. **R1 / RB left-clicks, L1 "
            + "/ LB right-clicks.**\n\nIf Steam Input is already emulating a mouse on the same "
            + "controller, switch one of them off or the pointer moves twice as far as you "
            + "push it.";

    public const string CursorStick = "Stick that moves the pointer";
    public const string CursorStickWhy =
        "The **other stick scrolls** — push it up or down to roll the wheel.";
    public static readonly string[] CursorStickOptions = [
        "Left stick",
        "Right stick",
    ];

    // Was "Also use the trackpad (PS5 DualSense)". Two things wrong with that. The code accepts any
    // Sony pad, not just the DualSense — a DualShock 4 works — so naming one model told half the
    // owners it was not for them. And every other part of this app calls that surface a touchpad,
    // which is also what Sony calls it: PadControl.Touchpad, and the hold-button list.
    public const string UseTrackpad = "Also use the touchpad";
    public const string UseTrackpadWhy =
        
            "For PlayStation controllers. One finger moves the pointer and **two fingers "
            + "scroll**; press it to left-click, press and hold to right-click. Does nothing "
            + "on a pad without one.";

    public const string MouseHold = "Only while a button is held";
    public const string MouseInGames = "Let the pointer work over a game";
    public const string MouseInGamesWhy =
        
            "Normally the pointer switches itself off once a game fills the screen, so it "
            + "cannot fight the game's own controls. Turn this on for games that expect a "
            + "mouse and ignore a controller. Holding the button below does the same thing "
            + "for a moment, without leaving it on all the time.";

    public const string MouseHoldWhy =
        
            "The pointer only responds while the button below is held. **Worth trying if "
            + "the pointer is dead in a launcher, or moves in a game when you did not want it "
            + "to** — nothing is guessed about the window in front, you say when you want "
            + "it.\n\nWhile held it works over a game too, though never inside Big Picture, "
            + "which always drives itself. It also stops a resting stick nudging the pointer, "
            + "which is what keeps a screen from going to sleep on you.";

    public const string MouseHoldButton = "Button to hold";
    public const string MouseHoldButtonWhy =
        
            "Chosen by position rather than by name, so it is the same physical button "
            + "whichever controller is in your hands.";

    public const string MouseSpeed = "Pointer speed";
    public const string MouseSpeedWhy =
        
            "How far the pointer travels for the same push. Left places it exactly; right "
            + "crosses a big screen in one flick.";
    public const string MouseDeadzone = "Stick dead zone";
    public const string MouseDeadzoneWhy =
        
            "How far a stick must move before the pointer does. **If the pointer creeps on "
            + "its own, drag this right until it stops.** Sticks only, not the touchpad.";
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

    public const string RenameController = "Rename…";
    public const string RenameControllerWhy =
        "Give the selected controller a name of your own, so you can tell which is which.";

    public const string RenameTitle = "Name this controller";
    public const string RenamePrompt =
        "What would you like to call it?\n\nLeave it empty to go back to the model name.";

    public const string RevertControllerName = "Use original name";

    /// <summary>Shown in light blue under the controller settings. ** ** marks the bold part.</summary>
    // Shortened from five sentences to two, and moved next to the picker it qualifies. The line
    // people actually need is that the triggers are paused while you are on this page, and it was
    // third in a paragraph nobody was going to finish.
    public const string ControllerTriggerNote =
        
            "**Paused while you are on this page or the Hotkeys page**, so switching a "
            + "controller on to set it up will not also start a session.";

    // Live state at the top of the card. Nielsen's first heuristic, and the one thing this page could
    // not answer: whether the app can see your controller right now.
    public const string PadsNone = "No controller connected. Switch one on and it appears here.";
    /// <summary>{0} is the list of connected controllers.</summary>
    public const string PadsConnected = "Connected: {0}";
    // {0} is the list of controllers that *are* connected, or the "nothing" line below.
    //
    // The names used to be dropped entirely in this state, which is backwards: the card exists to
    // answer "can the app see my controller?", and it fell silent on exactly the occasion when the
    // answer was interesting. It also only mentioned starting, though the chosen controller governs
    // the disconnect as well.
    public const string PadsChosenAway =
        
            "**Your chosen controller is not connected right now**, so nothing else will "
            + "start or end a session. {0}";

    public const string PadsChosenAwayNothing = "Nothing else is connected either.";

    /// <summary>How a connected controller is powered, shown after its name in the list.</summary>
    // The summary here described what happens on a disconnect, which belonged to a constant that has
    // since gone. Left in place it documented these two as though they were about the disconnect
    // prompt, when they are two words appended to a controller's name.
    public const string PadOnBattery = "on battery";
    public const string PadCharging = "charging";

    // "above" was wrong — these buttons sit on the same line as the picker, to its right — and
    // "Use original name" is not on this page at all; it is a link inside the Rename box, and only
    // when a custom name exists.
    public const string ClearControllersWhy =
        
            "Forgets the controller chosen beside this, so any controller works again. A "
            + "name you have given it is kept; clear that from **Rename**.";
    // The picker's heading follows its two switches, so it never names something that is
    // switched off — see SettingsForm.UpdateTriggerPadTitle. TriggerPad covers both on, and also
    // neither, where it reads as a description of what the setting is for.
    public const string PadLinkPick = "Connection that counts";
    public const string PadLinkPickWhy =
        
            "For a controller that lives plugged in to charge. **Only starting is "
            + "restricted** — switching one off always works.";

    /// <summary>Shown in order, top to bottom. Keep the same number of entries.</summary>
    public static readonly string[] PadLinkOptions =
    [
        "Wired or wireless",
        "Only when plugged in (or on a dongle)",
        "Only over Bluetooth",
    ];

    // One stable title, where there were three that swapped as other switches moved.
    //
    // A label that rewrites itself when you touch a different control is a label you have to watch
    // rather than read — and it was rewriting itself to say what the row *governs*, which is what
    // the description underneath it is for. The description says that; the title names the thing.
    public const string TriggerPad = "Controller to use";
    // Shown under the picker when neither of the cards below it is switched on, so the choice is
    // not deciding anything at all. A greyed-out control with no explanation reads as a fault.
    public const string TriggerPadIdle =
        "Nothing below is switched on, so this is not deciding anything at the moment.";

    public const string TriggerPadWhy =
        
            "Leave this on **Any controller** unless you have several and only want one of "
            + "them to matter. Whatever is chosen here governs both cards below: only that "
            + "controller starts a session, and only that controller counts as the one that "
            + "disconnected. Two of the same model are told apart by the code after the name.";

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
    // UpdateFound was here, word for word the same as NoticeUpdateFound above. Use that one.

    public const string UpdateFailed =
        
            "The update could not be installed. Nothing has been changed.\n\nYou can download "
            + "it yourself from the GitHub page under About.";
}

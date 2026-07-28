using System.Text.Json;
using System.Text.Json.Serialization;

namespace CouchMode;

public enum TvDisplayMode
{
    /// <summary>Disconnect all other monitors while on the TV.</summary>
    TvOnly = 0,
    /// <summary>Leave the other monitors connected; just make the TV primary.</summary>
    KeepOthers = 1,
}

public enum SteamExitAction
{
    /// <summary>Leave the Steam window alone.</summary>
    Leave = 0,
    /// <summary>Minimize it — goes to the tray if Steam is set to do that.</summary>
    Minimize = 1,
    /// <summary>Close the window. Steam keeps running in the tray if configured to.</summary>
    Close = 2,
}

/// <summary>A controller this machine has seen, so it can be chosen while switched off.</summary>
public sealed class KnownController
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>A game the user chose to have HDR enabled for.</summary>
public sealed class HdrGame
{
    public string Name { get; set; } = "";
    public string InstallDir { get; set; } = "";
}

public sealed class AppConfig
{
    /// <summary>Monitor device path (stable across reboots), e.g. \\?\DISPLAY#GSM7766#...</summary>
    public string TvDisplayPath { get; set; } = "";
    public string TvDisplayName { get; set; } = "";   // for display in the UI only

    public string TvAudioDeviceId { get; set; } = "";
    public string TvAudioDeviceName { get; set; } = "";

    /// <summary>Blank means "whatever was default when TV mode started" — the usual right answer.</summary>
    public string DesktopAudioDeviceId { get; set; } = "";
    public string DesktopAudioDeviceName { get; set; } = "";

    public bool SwitchAudio { get; set; } = true;

    /// <summary>
    /// Use the app for HDR and performance only, with every couch session feature switched off.
    ///
    /// There are two audiences here and only one of them owns a television. The HDR switching works
    /// perfectly well at a desk — on when a game starts, off when it quits, so the desktop is not left
    /// washed out — and so does the priority boost, and both are the reason a good number of people
    /// will install this at all. Everything about sessions is noise to them: a display picker they
    /// will never set, a controller page about starting something they do not want started, and a
    /// guide button that hijacks their pad mid-game.
    ///
    /// So this is one switch rather than eleven. Off by default, because the app is named after the
    /// thing it turns off and somebody who wanted only HDR can find the switch on the first page.
    ///
    /// Nothing is deleted or forgotten while it is on. The settings behind the hidden pages keep
    /// their values and come back exactly as they were, because this is a mode rather than a reset.
    /// </summary>
    public bool DeskOnly { get; set; }

    /// <summary>
    /// Keep Big Picture pinned above other windows during a session, until a game or launcher takes
    /// over. Off lets the user alt-tab freely, at the cost of stray windows covering it on the TV.
    /// </summary>
    // Off by default: pinning Big Picture on top works for most games, but a few launchers open
    // behind it because Steam and the launcher fight over the top spot. Opt-in rather than surprise.
    // The "keep Big Picture on top" setting was removed from the UI for now — it caused more trouble
    // than it was worth. The front-guard functions are kept in BigPictureFront for if we bring it
    // back; until then this is forced off, so no saved config can re-enable it. To restore the
    // feature, make this a normal auto-property again and re-add its toggle on the Steam page.
    public bool BigPictureAlwaysOnTop { get => false; set { } }

    // CloseGameOnSessionEnd was removed. It decided in advance what should happen to a running game at
    // the end of a session; the session-end prompt now asks at the moment it happens, and offers more
    // answers than the toggle could — close, keep playing, back to Big Picture, or drop to the desktop
    // with the game left running. A setting that pre-answers a question the user is about to be asked
    // anyway is one the user has to keep in their head for no benefit. Old configs may still carry the
    // key; nothing reads it.


    /// <summary>
    /// TvOnly disconnects the other monitors; KeepOthers leaves them running and just moves
    /// primary to the TV. TvOnly avoids games spilling onto other screens and frees GPU
    /// bandwidth; KeepOthers lets you keep Discord or a guide up while playing.
    /// </summary>
    public TvDisplayMode DisplayMode { get; set; } = TvDisplayMode.TvOnly;

    /// <summary>
    /// Resolution and refresh rate to put the TV into, as "1920x1080@120". Empty means leave
    /// whatever Windows picks.
    ///
    /// Worth having because Windows commonly brings a television up at 60 Hz when it can do
    /// far better — the picture works, so the loss goes unnoticed.
    /// </summary>
    // Named TvVideoMode, not TvDisplayMode: there is already a TvDisplayMode *enum* for
    // whether the other monitors stay on, and a property sharing a type's name is the "Color
    // Color" ambiguity waiting to happen.
    public string TvVideoMode { get; set; } = "";

    /// <summary>
    /// On by default. The app is only useful while it's running — if it isn't, Big Picture
    /// opens on the wrong display and nothing switches, which reads as the app being broken.
    /// </summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Start a couch session as soon as Couch Session launches. Off by default.</summary>
    public bool StartSessionOnLaunch { get; set; }

    /// <summary>Start a couch session when the PC wakes from sleep. Off by default.</summary>
    public bool StartSessionOnWake { get; set; }

    /// <summary>Only start the on-wake session when the controller was what woke the PC (a wired pad
    /// or a wake-armed wireless adapter turning on), not any wake such as a mouse nudge. Applies only
    /// while <see cref="StartSessionOnWake"/> is on. Off by default.</summary>
    public bool StartOnWakeControllerOnly { get; set; }

    /// <summary>Look for a newer version on GitHub when the app starts.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>What to do with the Steam desktop window that appears when Big Picture closes.</summary>
    public SteamExitAction SteamAfterBigPicture { get; set; } = SteamExitAction.Minimize;

    /// <summary>
    /// Turn HDR on for the primary display while a chosen game is running.
    /// Off by default: it changes display state, so it should be opted into.
    /// </summary>
    public bool AutoHdrEnabled { get; set; }

    /// <summary>
    /// Turn HDR on for the whole couch session rather than per game.
    ///
    /// The guaranteed option. A game samples the display's HDR state once, while building its
    /// renderer, so anything that reacts to the game starting is racing it. Switching at the
    /// start of a session cannot lose that race, because nothing has launched yet.
    /// </summary>
    public bool HdrForWholeSession { get; set; }

    /// <summary>
    /// The two switches above as the one choice they always were.
    ///
    /// They were never independent: whole-session HDR returns early from the per-game path, so
    /// turning it on made the other setting do nothing at all and the description had to explain
    /// that in words. A single three-way choice says it structurally instead.
    ///
    /// Stored as those same two booleans rather than as a field of its own, so an older config
    /// still loads and an older build still reads one written by this. Whole-session deliberately
    /// leaves <see cref="AutoHdrEnabled"/> set as well: it is what starts the process watcher, and
    /// the priority boost and Big Picture's front guard both listen to that — clearing it would
    /// quietly take those away too.
    /// </summary>
    [JsonIgnore]
    public HdrMode HdrSwitching
    {
        get => HdrForWholeSession ? HdrMode.WholeSession
             : AutoHdrEnabled     ? HdrMode.PerGame
                                  : HdrMode.Off;
        set
        {
            AutoHdrEnabled = value != HdrMode.Off;
            HdrForWholeSession = value == HdrMode.WholeSession;
        }
    }

    /// <summary>
    /// When the HDR hotkey is pressed while a game is running, also remember the choice for that game
    /// (add it to, or drop it from, the list below). On by default; off makes the hotkey a plain
    /// one-time toggle.
    /// </summary>
    public bool HdrHotkeyRemembersGame { get; set; } = true;

    /// <summary>
    /// Games chosen for automatic HDR, identified by install folder.
    ///
    /// Empty on a new install, and it stays empty until somebody ticks something. There used to be
    /// two mechanisms filling it in on the user's behalf — one that ticked every game the HDR
    /// database called native the first time the list appeared, and a setting that ticked anything
    /// installed later. Both are gone. A fresh install that has already decided which of your games
    /// should change your display is making a claim it cannot support: the database is a best guess,
    /// "native HDR" is not the same as "you want HDR for it", and the ticks appeared with no record
    /// of anyone having agreed to them. An empty list is the honest starting state, and the Native
    /// HDR button next to the list does the same job in one press for anyone who wants it.
    /// </summary>
    public List<HdrGame> HdrGames { get; set; } = [];

    /// <summary>
    /// HDR support you have set by hand, keyed by install folder, overriding the database.
    ///
    /// The bundled list and the wiki are both imperfect and always will be — a game released
    /// after a build shipped cannot be in its seed list, and the wiki may simply be missing an
    /// entry. This is the escape hatch, and it wins over both.
    /// </summary>
    public Dictionary<string, int> HdrTags { get; set; } = [];

    // ---- resource control ----

    /// <summary>
    /// Free the machine up when a session starts by closing open desktop apps.
    ///
    /// Off by default, and the list below starts empty: closing someone's windows without being
    /// asked is the kind of thing that loses work, so it has to be chosen twice, once for the
    /// feature and once for each app.
    ///
    /// Apps are always *asked* to close, exactly as Alt+F4 asks, and one that refuses is left
    /// running. Nothing here ever ends a process. That is the difference between freeing up memory
    /// and losing the document somebody had open, and it is not a setting.
    /// </summary>
    public bool CloseAppsForSession { get; set; }

    /// <summary>
    /// Full paths of the executables to ask to close, chosen by the user and no one else.
    ///
    /// Paths rather than names, so "chrome" means the Chrome you picked and not any process that
    /// happens to share the name. Matching is on the path a running process reports.
    /// </summary>
    public List<string> AppsToClose { get; set; } = [];

    /// <summary>
    /// Start the apps that were closed again once the session ends.
    ///
    /// Only the ones this app actually closed during this run, so ending a session never opens
    /// something that was not already open. Windows and documents do not come back, which the
    /// setting says on its face — Windows offers no way to ask an app to reopen what it had.
    /// </summary>
    public bool ReopenAppsAfterSession { get; set; }

    /// <summary>
    /// Whether couch mode may change the power plan at all.
    ///
    /// Off by default, like every other setting that reaches outside this app. Kept separate
    /// from the plan itself so "don't touch my power settings" is one obvious switch rather
    /// than a particular value hidden in a list — someone who wants to be left alone should not
    /// have to read the options to find out which one means "nothing".
    ///
    /// The Windows power mode slider is governed by this too. It is part of the same question.
    /// </summary>
    public bool ChangePowerPlan { get; set; }

    /// <summary>
    /// Which power plan a session uses, as a scheme GUID, or <see cref="Automatic"/> to pick
    /// the fastest installed. Only consulted when <see cref="ChangePowerPlan"/> is on.
    ///
    /// Naming a plan removes a guess the app cannot otherwise make: people duplicate schemes,
    /// rename them and write their own, and nothing about a custom plan tells you whether it is
    /// faster than the ones Windows ships.
    /// </summary>
    public string SessionPowerPlan { get; set; } = Automatic;

    /// <summary>The value of <see cref="SessionPowerPlan"/> that means "pick the fastest".</summary>
    public const string Automatic = "auto";

    /// <summary>
    /// Leave the power plan alone while the machine is running on battery.
    ///
    /// For a laptop or a handheld docked to the television. High Performance on battery is a real
    /// cost — it holds boost clocks and stops the CPU parking cores — and the machine that most
    /// wants a couch session is often the one least able to afford it.
    ///
    /// Off by default, so the setting behaves as it always has unless somebody asks for this.
    /// A desktop reports itself as permanently on mains, so switching it on there changes nothing.
    /// </summary>
    public bool PowerPlanOnlyWhenPluggedIn { get; set; }

    /// <summary>Stop the TV blanking while a session is running.</summary>
    /// <summary>
    /// Off, like every other performance option.
    ///
    /// These all change something outside the app — the power plan, the sleep timer, Windows'
    /// own notification setting — and a utility should not do any of that until it has been
    /// asked to. Being useful out of the box is not worth silently altering system settings.
    /// </summary>
    public bool KeepDisplayAwake { get; set; }

    /// <summary>Suppress Windows toasts for the duration of a session.</summary>
    public bool SilenceNotifications { get; set; }

    /// <summary>Turn Windows Game Mode on when a session starts.</summary>
    public bool EnableGameMode { get; set; }

    /// <summary>Raise a running game's process priority. See GamePriority.</summary>
    public bool GamePriorityEnabled { get; set; }







    // ---- shortcuts ----

    /// <summary>
    /// Keyboard shortcut that starts or ends a session, as a <see cref="Keys"/> value with its
    /// modifier bits. Zero means none.
    ///
    /// Off by default. A global shortcut takes a combination away from every other program on
    /// the machine, which is not something to claim uninvited.
    /// </summary>
    public int ShortcutKeyboard { get; set; }

    /// <summary>
    /// Controller button combination that starts or ends a session, as an XInput button mask.
    /// Zero means none.
    ///
    /// Select + X, which is Create + Square on a DualSense and View + X on an Xbox pad — the mask is
    /// stored in Xbox terms and matched by button *position*, so it lands on the same two physical
    /// buttons whatever is plugged in. Select is the anchor because nothing binds it as a modifier,
    /// and the face button makes the pair impossible to hit by accident mid-game.
    /// </summary>
    public int ShortcutController { get; set; } = 0x4020;   // Select (0x0020) + Face-left (0x4000)

    /// <summary>
    /// The controller the shortcut above was recorded on.
    ///
    /// Stored because a HID button mask means nothing without it. HID numbers buttons per device,
    /// so button 3 is Circle on a DualSense and something else on the next pad — and with the pad
    /// switched off there is nothing to ask, which is why a saved shortcut used to degrade into
    /// "3 + 13" the moment the controller was unplugged.
    ///
    /// Keeping the ids means the combination can be named without the pad present, and can be
    /// recognised on a different controller by matching the buttons in the same positions.
    /// Zero for an XInput recording, which is already normalised to an Xbox pad.
    /// </summary>
    public int ShortcutControllerVendor { get; set; }
    public int ShortcutControllerProduct { get; set; }

    /// <summary>
    /// Hotkeys that turn HDR on and off, separate from the ones that start a session.
    ///
    /// Its own pair rather than a mode on the existing one, because the two are wanted at
    /// different moments: the session hotkey is pressed when you sit down, and this is pressed
    /// mid-game when something looks wrong in HDR.
    /// </summary>
    public int ShortcutKeyboardHdr { get; set; } = 131264;   // Ctrl + ~ (the backtick key)

    /// <summary>Select + Y — Create + Triangle on a DualSense. Same shape as the session combo, one
    /// face button along, so the two are muscle-memory neighbours rather than unrelated shapes.</summary>
    public int ShortcutControllerHdr { get; set; } = 0x8020;   // Select (0x0020) + Face-up (0x8000)

    /// <summary>
    /// Whether the PS / Guide button also ends a session.
    ///
    /// It rides along with what Steam already does rather than replacing it. Pressing that button in
    /// Big Picture opens Steam's own overlay, and the overlay is the one thing that reliably takes the
    /// controller off a running game — Steam owns the pad, so it can do what this app cannot. The
    /// button is not intercepted or swallowed (there is no way to do that from user space, and trying
    /// would be the kind of input meddling anti-cheat exists to catch): Steam gets the press exactly as
    /// it always did, and the prompt simply appears on top of the overlay Steam just opened.
    ///
    /// The practical effect is one button that pauses the game the way the console you are imitating
    /// would, and asks what to do next.
    /// </summary>
    public bool EndSessionOnGuideButton { get; set; } = true;

    /// <summary>
    /// Put a second, blunter question in front of anything that closes a running game.
    ///
    /// On by default. The session-end prompt's answers are one press apart and one of them is
    /// irreversible, so the cost of a mis-press is whatever the player had not saved. This is the
    /// only thing standing between those two facts.
    /// </summary>
    public bool ConfirmClosingGame { get; set; } = true;

    /// <summary>
    /// Whether the one-time welcome has been shown.
    ///
    /// A flag rather than inferring from IsConfigured, so it shows exactly once ever: an upgrade with
    /// settings intact sees it once too (cheap, and it announces the setup list), and a reset of the
    /// config shows it again, which is the behaviour a "reset all settings" button implies anyway.
    /// </summary>
    public bool FirstRunWelcomeShown { get; set; }

    /// <summary>
    /// The version whose guide banner was dismissed, or empty while it should still be shown.
    ///
    /// A version rather than a flag, so an update brings it back once. The banner explains the guide
    /// button, which is the control the whole app is driven by — worth one reminder after an update
    /// that may have changed how it behaves, and worth nothing on the two hundredth launch of a
    /// version somebody has been using for a month.
    ///
    /// Reset with everything else, because "reset all settings" means the app as it arrived, and it
    /// arrived with the banner up.
    /// </summary>
    public string GuideBannerDismissedAt { get; set; } = "";
    public int ShortcutControllerHdrVendor { get; set; }
    public int ShortcutControllerHdrProduct { get; set; }


    /// <summary>
    /// Drive the mouse pointer with the controller, for launchers and windows that ignore a pad.
    ///
    /// Off by default: it changes what the controller does, which is never a thing to switch on
    /// without asking. When on, the pointer source is chosen by pad — a DualSense uses its
    /// trackpad, everything else uses the right stick with the triggers as the buttons.
    /// </summary>
    public bool MouseControlEnabled { get; set; }

    /// <summary>
    /// Whether the pointer may drive while a game is in front.
    ///
    /// Off by default, which is the behaviour a game expects: the pointer switches itself off when a
    /// game fills the screen and back on at a launcher. On, it drives everywhere — for the games that
    /// want a mouse and do not read a controller, and for anything the window test gets wrong.
    ///
    /// Holding the hold button overrides this in the moment, so someone who wants the pointer only
    /// occasionally in a game does not have to leave it on all the time.
    /// </summary>
    public bool MouseInGames { get; set; }

    /// <summary>
    /// Only steer the mouse while a chosen button is held.
    ///
    /// The safety, and the thing that makes the feature harmless to leave switched on: without it a
    /// resting thumb and stick drift trickle out synthetic movement, a game receives mouse input it
    /// never asked for, and — because every synthetic move resets Windows' idle timer — the display
    /// never sleeps. Holding a button to mean "now I want the mouse" keeps all three from happening.
    ///
    /// Off by default all the same. It is the safer setting rather than the expected one, and a
    /// pointer that does nothing until an undiscovered button is held reads as broken. The row says
    /// plainly what it is for, so it can be found by anyone the default does not suit.
    /// </summary>
    public bool MouseHoldToActivate { get; set; }

    /// <summary>
    /// Which button turns the mouse on while held, as a <see cref="Input.PadControl"/>.
    ///
    /// Stored as a position, not a device button number, so the same choice means R2 on a
    /// DualSense and RT on an Xbox pad — see PadControl. Right trigger by default: the bumpers are
    /// the click buttons now, so a trigger keeps the hold modifier clear of them while still being
    /// easy to hold with a thumb free for the pad.
    /// </summary>
    public int MouseHoldButton { get; set; } = (int)Input.PadControl.R2;

    /// <summary>
    /// Which stick moves the cursor, as <see cref="Input.CursorStick"/>. The other stick scrolls,
    /// so this is a single choice rather than a set. Right by default — the aiming thumb.
    /// </summary>
    public int MouseCursorStick { get; set; } = (int)Input.CursorStick.Right;

    /// <summary>
    /// Whether a DualSense's trackpad also drives the cursor: one finger moves the pointer, two
    /// fingers scroll. On by default, and simply ignored on pads that have no trackpad.
    /// </summary>
    public bool MouseUseTrackpad { get; set; } = true;

    /// <summary>Pointer speed, 1 slow to 10 fast. Five is a sensible middle.</summary>
    // A 1-20 slider position; PointerControl maps it to a speed multiplier. 12 sits mid-track,
    // which the curve places near the old default feel.
    public int MouseSpeed { get; set; } = 12;

    // Stick dead-zone as a percentage of full deflection. Movement closer to centre than this is
    // ignored, so a drifting stick sits still. 20 matches the original fixed value.
    public int MouseDeadzone { get; set; } = 20;

    /// <summary>
    /// Names the user has given their controllers, keyed by device id.
    ///
    /// "PlayStation DualSense · 1A64" tells two identical pads apart but says nothing about which
    /// is which in the room. A name the user chose does — "Living room", "Blue one" — and it is
    /// the only way to make the picker meaningful for somebody who owns several of the same model.
    /// </summary>
    public Dictionary<string, string> ControllerNames { get; set; } = [];

    /// <summary>
    /// Which kinds of connection may start a session, when starting on connect is switched on.
    ///
    /// For the common case of a pad that lives plugged into the desk for charging: without this,
    /// every time it is connected to charge, the television takes over.
    /// </summary>
    public PadLink StartOnControllerLink { get; set; } = PadLink.Either;

    // ---- triggers ----

    /// <summary>Start a session as soon as a game controller is switched on.</summary>
    public bool StartOnControllerConnect { get; set; }

    /// <summary>
    /// Mute a game that has been left running while you are back at the desktop.
    ///
    /// On by default. A game does not know it has been put down: dropping to the desktop with it
    /// still running — from the prompt's "swap to desktop", or because a controller dropped out —
    /// leaves its music and its gunfire playing into whatever speakers are now in front of the
    /// person trying to answer an email. It is unmuted the moment the session comes back.
    ///
    /// Off for anyone who leaves a game running on purpose to listen to it, which is a real thing
    /// people do with a soundtrack they like.
    /// </summary>
    public bool MuteGameOnDesktop { get; set; } = true;

    /// <summary>
    /// What happens when the last controller disconnects mid-session.
    ///
    /// One three-way choice rather than two switches, and it was two switches for about an hour.
    /// They were not independent — the second only meant anything while the first was on — and the
    /// pair could not say the three states out loud: the reader had to work out what a combination
    /// meant. Worse, one of the three was reachable two ways, since the prompt's own "leave it for
    /// now" answer does exactly what turning the second switch off did. The same argument, and the
    /// same fix, as <see cref="HdrSwitching"/> a few pages up.
    ///
    /// Nothing here ever closes anything. Coming back means the display and sound return to the
    /// desk while the game and Big Picture keep running behind it, so switching the pad on again
    /// drops straight back in. The way out cannot be a prompt on the television, because the only
    /// thing that could have answered one is the controller that just vanished.
    /// </summary>
    /// <summary>
    /// What happens when you switch the controller off yourself.
    ///
    /// Coming back to the desktop by default, because switching a controller off is how people say
    /// they have finished playing, and it is worth acting on for exactly that reason. Nothing is
    /// closed: the game keeps running and switching the controller back on returns to it.
    /// </summary>
    public DisconnectAction OnControllerOff { get; set; } = DisconnectAction.ComeBack;

    /// <summary>
    /// What happens when the controller goes away without being switched off — a flat battery, a
    /// knocked cable, an adapter dropping the link.
    ///
    /// Nothing, by default. This is the ambiguous half and it is the one that used to be acted on:
    /// the setting was a single choice covering both events, defaulting to coming back and asking,
    /// which meant the app interrupted everyone to rescue the few whose battery had died. Split
    /// apart, the deliberate case can be acted on confidently and this one can be left alone until
    /// somebody asks for it.
    ///
    /// See TrayApp.WasPoweredOff for how the two are told apart, and for how sure it is.
    /// </summary>
    public DisconnectAction OnControllerLost { get; set; } = DisconnectAction.Ignore;

    /// <summary>
    /// Switch the Xbox Game Bar off while this app is running.
    ///
    /// Off by default, because the Game Bar is how people take screenshots and check frame
    /// rates, and switching off someone's Windows feature is not a thing to do uninvited.
    ///
    /// Held for the whole time the app runs rather than per session, and restored on exit —
    /// see GameBarControl for why clearing only the button binding was not enough.
    /// </summary>
    public bool DisableGameBarButton { get; set; }

    /// <summary>
    /// Which controller may start a session. Empty means any of them.
    ///
    /// Stored as vendor and product id rather than the Windows device path, so the choice
    /// survives moving the pad between USB and Bluetooth or between ports.
    /// </summary>
    public string TriggerControllerId { get; set; } = "";

    /// <summary>
    /// The chosen controller's name, kept so it can still be shown while it is switched off.
    ///
    /// Display only — the id above is what actually matches. Without it a pad that is not
    /// currently attached could only be listed as its raw id, which means nothing to anyone.
    /// </summary>
    public string TriggerControllerName { get; set; } = "";

    /// <summary>
    /// Every controller this machine has seen, so the list does not shrink to whatever is
    /// switched on at that moment.
    ///
    /// Remembering only the chosen one was not enough: a pad that had been seen but not chosen
    /// vanished the moment it was switched off, so it could never be picked without first
    /// switching it on — and switching a second pad on made the first disappear from the list
    /// entirely. Controllers are things people own, not things that exist only while powered.
    /// </summary>
    public List<KnownController> KnownControllers { get; set; } = [];

    /// <summary>
    /// Modes each display reported the last time it was switched on, keyed by device path.
    ///
    /// Windows only enumerates modes for a display it is currently driving, so a television
    /// that is off offers nothing at all — which is precisely when someone is most likely to be
    /// setting this up, sitting at their desk with the TV cold. Remembering what it said last
    /// time keeps the choice available. Refreshed from the live list whenever the display is on,
    /// so it cannot drift for long.
    /// </summary>
    public Dictionary<string, List<string>> KnownVideoModes { get; set; } = [];

    /// <summary>
    /// Playback devices seen before, keyed by endpoint id to friendly name. A TV's HDMI audio
    /// vanishes when the TV is switched off, so this keeps it selectable in the picker while it is
    /// away — the same reason the displays and their modes are remembered. Refreshed from the live
    /// list whenever a device is present.
    /// </summary>
    public Dictionary<string, string> KnownAudioDevices { get; set; } = [];

    /// <summary>
    /// The resolution and refresh rate chosen for each display, keyed by device path.
    ///
    /// <see cref="TvVideoMode"/> holds the choice for whichever display is currently selected,
    /// because that is what a session reads. This keeps the others: someone comparing their
    /// television against their monitor should not have to set 120 Hz again every time they
    /// switch back, and silently losing a setting the moment you look at something else is the
    /// sort of thing that makes people stop trusting a settings window.
    /// </summary>
    public Dictionary<string, string> VideoModeByDisplay { get; set; } = [];

    /// <summary>
    /// Where each display sits on the desktop, as "x,y", keyed by device path.
    ///
    /// Persisted because Windows forgets. The desktop is one coordinate space and a monitor being
    /// "on the left" is a negative X — but that only exists for displays currently attached.
    /// Detach one and the arrangement goes with it; reattach it and Windows drops it to the right
    /// of everything else. A monitor that lived on the left ends up on the right, permanently,
    /// after a single session that switched it off and on again.
    ///
    /// Merged rather than replaced, and never pruned: the whole value of this record is being able
    /// to answer for a display that is switched off, which is exactly when Windows cannot.
    /// </summary>
    public Dictionary<string, string> DisplayPositions { get; set; } = [];

    /// <summary>
    /// Whether the app shows any notification at all.
    ///
    /// The master switch, and every toast in the app now passes through it. It used to mean only
    /// "announce switching to the TV and back", which left it sitting beside two other settings as
    /// though the three were siblings — while several notifications answered to none of them and
    /// could not be turned off from anywhere.
    ///
    /// An older config carries the narrower meaning, and the widening is safe in both directions:
    /// true was already "show them", and false was someone asking for fewer messages, who now gets
    /// none rather than some. What each kind is remains a separate choice below.
    /// </summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>Announce moving to the TV, and moving back.</summary>
    public bool ShowSessionNotifications { get; set; } = true;

    /// <summary>
    /// Whether to announce HDR going on and off.
    ///
    /// Its own switch because it fires on a very different rhythm from the rest: switching happens
    /// when you sit down, while this fires every time any watched game opens or closes. Someone who
    /// wants the others can reasonably not want this one.
    /// </summary>
    public bool ShowHdrNotifications { get; set; } = true;

    /// <summary>
    /// Whether to say so when closing the settings window leaves the app in the tray.
    ///
    /// The one worth keeping on longest: it answers "where did it go?", and it fires precisely when
    /// someone may not realise anything is still running.
    /// </summary>
    public bool ShowMinimizeNotification { get; set; } = true;

    /// <summary>
    /// Whether to report something the app could not do — a hotkey another program holds, a change
    /// Windows refused. Rare, and each one means a feature is not working.
    /// </summary>
    public bool ShowProblemNotifications { get; set; } = true;

    /// <summary>
    /// Whether to confirm an action the user just took from the settings window: a bug report
    /// written, UAC or the firewall changed. Never ambient — each one follows a press.
    /// </summary>
    public bool ShowActionNotifications { get; set; } = true;


    /// <summary>
    /// On by default: closing the settings window leaves the app running in the notification
    /// area. Turned off, the close button quits instead. Either way the tray menu can quit.
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>
    /// Switch the couch display off when the session ends, rather than leaving it attached.
    ///
    /// On by default. Leaving the television attached means Windows keeps a desktop on a screen
    /// nobody is looking at, keeps opening windows onto it, and keeps it in the arrangement — so
    /// a mouse pushed too far runs off onto a dark screen. Switching it off is what someone
    /// leaving couch mode almost always means, and it is undone simply by starting a session
    /// again.
    /// </summary>
    public bool TurnOffTvOnExit { get; set; } = true;

    /// <summary>
    /// Restart Steam after switching audio, so its UI sounds bind to the TV.
    ///
    /// Steam opens its audio endpoint once at startup and never follows later default-device
    /// changes, so if it was already running, its menu sounds stay on the old device. Games are
    /// unaffected — they open their own streams after launch. Off by default: restarting Steam
    /// is disruptive, and this only affects Steam's own interface sounds.
    /// </summary>
    public bool RestartSteamForAudio { get; set; }

    /// <summary>
    /// Last known geometry of the Steam desktop window, observed while on the desktop.
    /// Persisted because Big Picture may already have replaced that window before we ever
    /// get involved — including across an app restart — leaving nothing to measure later.
    /// </summary>
    public int SteamWindowLeft { get; set; }
    public int SteamWindowTop { get; set; }
    public int SteamWindowRight { get; set; }
    public int SteamWindowBottom { get; set; }
    public bool SteamWindowMaximized { get; set; }

    public bool HasSteamWindowGeometry =>
        SteamWindowRight > SteamWindowLeft && SteamWindowBottom > SteamWindowTop;

    /// <summary>
    /// Where the settings window was last left, so it opens where you put it.
    ///
    /// Worth persisting for this app more than most. A couch session rearranges the desktop
    /// underneath the window — the television becomes primary, monitors detach and come back —
    /// and Windows shoves whatever is open around while that happens. Without somewhere to
    /// restore from, the window comes out of a session in whatever spot the reshuffle left it.
    ///
    /// Only ever written while the window is in its normal state. Recording a minimized or
    /// maximized window's bounds means restoring to a size that was never really chosen.
    /// </summary>
    public int WindowLeft { get; set; }
    public int WindowTop { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }

    public bool HasWindowPlacement => WindowWidth > 0 && WindowHeight > 0;

    public int DisplaySettleTimeoutMs { get; set; } = 12000;

    public bool IsConfigured =>
        // Nothing to set up in desk-only mode: there is no television to choose and no speakers to
        // move sound to. Without this the app considers itself half-installed forever — which gates
        // the start-with-Windows repair, and reads as a setup step that can never be completed
        // because the page it would be completed on is not there.
        DeskOnly ||
        (!string.IsNullOrWhiteSpace(TvDisplayPath) &&
         (!SwitchAudio || !string.IsNullOrWhiteSpace(TvAudioDeviceId)));

    // ---- persistence ----

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchSession");

    public static string FilePath => Path.Combine(Directory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            MigrateFromOldName();

            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize(json, ConfigJson.Default.AppConfig);

                if (cfg is not null)
                {
                    CleanSavedNames(cfg);
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            // A corrupt config must not brick the app — fall back to defaults and say so.
            Log.Warn($"Could not read config, using defaults: {ex.Message}");
        }
        return new AppConfig();
    }

    /// <summary>
    /// Take the picker's decoration back out of names written before it was stripped on the way in.
    ///
    /// The display list marks a switched-off television by appending a word to its name, and the
    /// selected item is that decorated copy — so choosing a TV while it was off, which is the case
    /// the list is built to allow, saved the marker as part of the name. It then showed up on the
    /// Home page, in warnings and in the diagnostics report, permanently, because nothing rewrote
    /// the name once the set came back on.
    ///
    /// Fixed at the point of saving in SettingsForm.PlainDisplayName. This is for the settings files
    /// that already have it, and it is deliberately literal: it removes the exact suffix from the end
    /// and touches nothing else, so a television genuinely named after that word keeps its name.
    /// </summary>
    private static void CleanSavedNames(AppConfig cfg)
    {
        cfg.TvDisplayName = Strip(cfg.TvDisplayName, Ui.Words.DisplayDisconnected);
        cfg.TvAudioDeviceName = Strip(cfg.TvAudioDeviceName, Ui.Words.AudioDeviceOff);
        cfg.DesktopAudioDeviceName = Strip(cfg.DesktopAudioDeviceName, Ui.Words.AudioDeviceOff);

        static string Strip(string name, string suffix)
        {
            if (name.Length == 0) return name;

            var trimmed = name.TrimEnd();

            return trimmed.EndsWith(suffix, StringComparison.Ordinal)
                ? trimmed[..^suffix.Length].TrimEnd()
                : name;
        }
    }

    /// <summary>
    /// Carry settings over from earlier names ("TV Mode", "Couch Mode", "Couch Potato", "Couch
    /// Launcher"), so renaming the app does not silently reset anyone already using it. Newest wins.
    /// </summary>
    private static void MigrateFromOldName()
    {
        if (File.Exists(FilePath)) return;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        foreach (var previous in new[] { "CouchLauncher", "CouchPotato", "CouchModeGaming", "CouchMode", "TvMode" })
        {
            var oldPath = Path.Combine(appData, previous, "config.json");
            if (!File.Exists(oldPath)) continue;

            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.Copy(oldPath, FilePath);
                Log.Info($"Brought your settings across from the previous version ({previous}).");
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not migrate settings from {previous}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Put every setting back to how it ships.
    ///
    /// The values are copied onto this instance rather than a fresh object being handed back,
    /// because the tray, the session controller and the HDR coordinator all hold a reference to
    /// this one and would otherwise carry on using the old settings until the app restarted.
    /// </summary>
    public void ResetToDefaults()
    {
        var fresh = new AppConfig();

        foreach (var property in typeof(AppConfig).GetProperties())
        {
            if (!property.CanRead || !property.CanWrite) continue;
            property.SetValue(this, property.GetValue(fresh));
        }

        Log.Info("All settings reset to their defaults.");
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);

        // Write-then-move so a crash mid-write can't leave a truncated config behind.
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, ConfigJson.Default.AppConfig));
        File.Move(temp, FilePath, overwrite: true);

        Log.Info("Config saved.");
    }
}

// Source-generated so serialization survives trimming.
/// <summary>How a controller is attached, for the rule about which may start a session.</summary>
public enum PadLink { Either, WiredOnly, WirelessOnly }

/// <summary>
/// When Auto HDR switches the display. Ordered so the value doubles as the dropdown's index.
/// </summary>
public enum HdrMode { Off, PerGame, WholeSession }

/// <summary>
/// What a controller leaving mid-session does. Used by both halves of that question — see
/// <see cref="AppConfig.OnControllerOff"/> and <see cref="AppConfig.OnControllerLost"/>.
///
/// Order matters: these are the dropdown's rows, and the index is what gets stored.
/// </summary>
public enum DisconnectAction
{
    /// <summary>Nothing. Suits a pad that sleeps on its own, or a link that flickers.</summary>
    Ignore,

    /// <summary>Display and sound come back to the desk. Nothing is closed, nothing is asked.</summary>
    ComeBack,

    /// <summary>The same, then a prompt at the desk — but only ever when a game is running.</summary>
    ComeBackAndAsk,

    /// <summary>
    /// Close the game and end the session outright.
    ///
    /// Last in the list because it is the only irreversible answer here, and offered for a deliberate
    /// power-off only — see Words.DisconnectOptionsLost. Somebody who switches their controller off
    /// has said they are finished and can mean it; a controller that went flat has said nothing, and
    /// closing a game on that signal is the exact disaster the two questions were split apart to
    /// avoid.
    /// </summary>
    EndAndClose,
}



[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(HdrGame))]
[JsonSerializable(typeof(KnownController))]
internal partial class ConfigJson : JsonSerializerContext
{
}

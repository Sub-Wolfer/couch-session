using System.Runtime.InteropServices;
using System.Text;
using CouchMode.Steam;

namespace CouchMode.Input;

/// <summary>
/// Steers the mouse from a controller, so a launcher that ignores a pad can still be driven.
///
/// The pointer source is chosen by what is connected. A DualSense uses its trackpad — drag a
/// finger to move, press the pad to click — because that is the natural pointing device it already
/// has. Everything else uses the right stick to move; on every pad the bumpers are the click
/// buttons — right for left-click, left for right-click.
///
/// Everything is read passively, through the same raw-input path the rest of the app uses, and the
/// only thing written is synthetic mouse input. Nothing is opened, so this sits alongside Steam
/// Input and the games themselves rather than fighting them for the pad — with one honest caveat:
/// if Steam Input is *also* emulating a mouse for the same stick, both move the pointer and it
/// doubles up. That is a matter of not enabling the two at once, and is why this is off by default.
///
/// The hold-to-activate option is the safety. Without it a resting thumb and stick drift move the
/// cursor constantly, and a running game receives mouse input it never asked for. Requiring a held
/// button to mean "now I want the mouse" keeps ordinary play untouched.
/// </summary>
public sealed class PointerControl : IDisposable
{
    private readonly System.Windows.Forms.Timer _poll;

    private bool _enabled;
    private bool _holdToActivate = true;
    private PadControl _holdButton = PadControl.R2;

    /// <summary>Whether the pointer is allowed to drive while a game is in front.</summary>
    private bool _inGames;
    private int _speed = 5;

    private bool _cursorStickLeft;    // which stick moves the cursor; the other scrolls
    private bool _useTrackpad = true;
    private float _deadzone = 0.20f;

    private bool _leftDown;
    private bool _rightDown;

    // The trackpad reports an absolute position; the mouse wants a delta. So the previous touch is
    // kept and subtracted. Reset the moment the finger lifts, or the next touch would jump the
    // cursor by the whole distance between where the finger left and where it landed.
    private bool _hadTouch;
    private int _lastX, _lastY;

    // Two-finger trackpad scroll: the average finger position last tick, and whether two were down.
    private bool _hadTwoFingers;
    private float _lastTwoY;

    // Drift rejection. A controller left sitting there must inject nothing — otherwise its stick
    // drift and trackpad jitter trickle out synthetic moves and wheel events, and each one resets
    // Windows' idle timer, so the display never sleeps even with no one touching it. Each source
    // therefore has to be *deliberately* engaged before it drives: a stick has to be pushed clearly
    // past its deadzone, and a trackpad finger has to actually move, before anything is injected.
    private bool _cursorEngaged;
    private bool _scrollEngaged;
    private bool _touchArmed;
    private int _touchStartX, _touchStartY;

    private const float StickEngage = 0.25f;   // stick magnitude past the deadzone needed to start
    private const int TouchArm = 8;            // trackpad units a finger must move before it drags

    // Fractional wheel notches carried between ticks, so slow scrolling is not lost to rounding.
    // Shared by both scroll sources — they never meaningfully run at the same instant.
    private float _scrollRemainder;

    /// <summary>Roughly 120 times a second: fast enough that the pointer feels attached to the input.</summary>
    private const int PollMs = 8;

    public PointerControl()
    {
        _poll = new System.Windows.Forms.Timer { Interval = PollMs };
        _poll.Tick += (_, _) => Tick();
    }

    /// <summary>Apply the current settings. Starts or stops the loop to match.</summary>
    public void Configure(AppConfig config)
    {
        _enabled = config.MouseControlEnabled;
        _inGames = config.MouseInGames;
        _holdToActivate = config.MouseHoldToActivate;
        _holdButton = (PadControl)config.MouseHoldButton;
        _speed = Math.Clamp(config.MouseSpeed, SpeedMin, SpeedMax);

        _cursorStickLeft = (CursorStick)config.MouseCursorStick == CursorStick.Left;
        _useTrackpad = config.MouseUseTrackpad;
        _deadzone = Math.Clamp(config.MouseDeadzone, 0, 90) / 100f;

        if (_enabled) _poll.Start();
        else
        {
            _poll.Stop();
            ReleaseButtons();
        }
    }

    private void Tick()
    {
        try
        {
            // Off, hold-button up, Big Picture in front, or a game is on screen: do nothing, and
            // let go of anything held.
            //
            // Big Picture stands the pointer down unconditionally — it is the couch interface and
            // drives itself from the pad, so a synthetic cursor only fights its own navigation.
            //
            // The game check is the other half, and the honest answer to "how do we not steer the
            // cursor mid-game". A launcher, a menu, a store page — the windows this feature is for —
            // are ordinary windows that do not cover the screen. A game, once running, fills the
            // display, exclusive or borderless. So a foreground window the exact size of its monitor
            // is taken to be a game and the mouse stands down: it switches itself off in a game and
            // back on at a launcher.
            RefreshForeground();

            // Holding the button is the user saying "I want the pointer, now" — so it wins.
            //
            // It used to be one condition among several, which meant a held button still lost to the
            // game check and the pointer stayed dead in the one situation where the intent could not
            // be clearer. An explicit request beats a guess about what the foreground window is.
            bool asked = _holdToActivate && PadShortcut.IsControlHeld(_holdButton);

            if (!_enabled
                || _bigPictureInFront
                || ControllerUiCapturing?.Invoke() == true
                || (_holdToActivate && !asked)
                || (_gameInFront && !asked && !_inGames))
            {
                ReleaseButtons();
                _hadTouch = false;
                _hadTwoFingers = false;
                _cursorEngaged = false;
                _scrollEngaged = false;
                return;
            }

            // A DualSense drives from the trackpad when one is present; anything else from the
            // stick and triggers. The trackpad is preferred because on a DualSense it is the more
            // precise pointer and leaves the sticks free.
            if (DriveFromTrackpad()) return;

            DriveFromStick();
        }
        catch (Exception ex)
        {
            Log.Warn($"Pointer control tick failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Move and click from a DualSense. Returns false when no DualSense is present, so the caller
    /// can fall through to the stick for an Xbox or generic pad.
    ///
    /// A DualSense stays on this path whether or not a finger is down, because its clicks come from
    /// the triggers — read here so they work while the pad is used as a mouse, exactly as they do
    /// on an Xbox pad. Falling through to the stick would drop them, since XInput cannot see a
    /// DualSense at all.
    /// </summary>
    private bool DriveFromTrackpad()
    {
        var (vendor, _) = HidPad.FirstIds();
        if (vendor != SonyVendor) return false;

        // Trackpad, when it is switched on: one finger moves the pointer, two fingers scroll.
        if (_useTrackpad)
        {
            var (t0, t1) = HidPad.Touches();

            if (t0.Touching && t1.Touching)
            {
                DriveTrackpadScroll(t0, t1);
                _hadTouch = false;   // a one-finger drag afterwards must not jump from here
            }
            else if (t0.Touching)
            {
                if (!_hadTouch)
                {
                    // A new touch: remember where it landed and drive nothing yet. A finger resting
                    // on the pad, or a phantom touch, must not move the cursor — only a finger that
                    // actually travels does.
                    _touchStartX = t0.X;
                    _touchStartY = t0.Y;
                    _lastX = t0.X;
                    _lastY = t0.Y;
                    _touchArmed = false;
                }
                else if (!_touchArmed)
                {
                    // Arm once the finger has moved past the jitter threshold, and start deltas from
                    // here so arming does not jump the cursor.
                    if (Math.Abs(t0.X - _touchStartX) > TouchArm || Math.Abs(t0.Y - _touchStartY) > TouchArm)
                    {
                        _touchArmed = true;
                        _lastX = t0.X;
                        _lastY = t0.Y;
                    }
                }
                else
                {
                    // Trackpad units are pixels of a 1920x1080 pad. Scaled by speed so the same
                    // swipe covers more or less screen, rounded so sub-pixel motion is not lost.
                    float k = Gain();
                    int dx = (int)Math.Round((t0.X - _lastX) * k);
                    int dy = (int)Math.Round((t0.Y - _lastY) * k);
                    if (dx != 0 || dy != 0) NoteDriving("trackpad");
                    MouseInjector.Move(dx, dy);
                    _lastX = t0.X;
                    _lastY = t0.Y;
                }

                _hadTouch = true;
                _hadTwoFingers = false;
            }
            else
            {
                _hadTouch = false;
                _hadTwoFingers = false;
            }
        }
        else { _hadTouch = false; _hadTwoFingers = false; }

        // The cursor stick moves the pointer; the other stick scrolls. Both are additive with the
        // trackpad, so a DualSense can point and scroll with whichever is to hand.
        var (mx, my) = HidPad.SonyStick(_cursorStickLeft, _deadzone);
        MoveByStick(mx, my);

        var (_, sy) = HidPad.SonyStick(!_cursorStickLeft, _deadzone);
        ScrollByStick(sy);

        // Bumpers click, on the DualSense too: R1 left-clicks, L1 right-clicks. They are plain
        // buttons in the HID mask, so the same mapping as the stick pads works here directly.
        bool r1 = PadShortcut.IsControlHeld(PadControl.R1);
        bool l1 = PadShortcut.IsControlHeld(PadControl.L1);

        // The physical trackpad button, told apart by how long it is held: a quick tap is a left
        // click, holding it is a right click. Held is the right one on purpose — a right click is
        // the rarer, more deliberate action, so it takes the gesture that requires intent, while
        // the everyday left click is the effortless tap. Both fire as complete clicks from the
        // button alone, so nothing here has to hold the mouse button down.
        UpdatePadButton(PadShortcut.IsControlHeld(PadControl.Touchpad));

        SetLeft(r1);
        SetRight(l1);

        return true;
    }

    private const ushort SonyVendor = 0x054C;

    private bool _padWasDown;
    private DateTime _padDownAt;
    private bool _padRightFired;

    /// <summary>
    /// Turn the trackpad button's press pattern into a click. Both a tap and a hold fire as a
    /// complete click on their own — nothing is left holding a mouse button down.
    ///
    /// The hold fires the right click the instant the threshold passes, while the button is still
    /// pressed, so the menu appears without waiting for the finger to lift. Once it has fired, the
    /// rest of the press is ignored. The tap is the fallback: a press let go before the threshold
    /// was never a hold, so it becomes a single left click, emitted on release.
    /// </summary>
    private void UpdatePadButton(bool down)
    {
        const int HoldMs = 300;

        if (down)
        {
            if (!_padWasDown) { _padDownAt = DateTime.UtcNow; _padRightFired = false; }
            else if (!_padRightFired && DateTime.UtcNow - _padDownAt >= TimeSpan.FromMilliseconds(HoldMs))
            {
                // Held long enough: a right click, now, not on release.
                MouseInjector.RightDown();
                MouseInjector.RightUp();
                _padRightFired = true;
            }
        }
        else if (_padWasDown)
        {
            // Released before the hold threshold — a tap, so a single left click. Skipped if a
            // trigger is already holding the left button, so the tap cannot fight a sustained click.
            if (!_padRightFired && !_leftDown)
            {
                MouseInjector.LeftDown();
                MouseInjector.LeftUp();
            }

            _padRightFired = false;
        }

        _padWasDown = down;
    }

    private void DriveFromStick()
    {
        _hadTouch = false;

        // No XInput pad at all: nothing to do, and let go of anything held.
        if (PadShortcut.XInputStick(_cursorStickLeft, _deadzone) is not { } cursor)
        {
            ReleaseButtons();
            return;
        }

        MoveByStick(cursor.X, cursor.Y);

        // The other stick scrolls.
        if (PadShortcut.XInputStick(!_cursorStickLeft, _deadzone) is { } scroll)
            ScrollByStick(scroll.Y);

        // Right bumper left-clicks, left bumper right-clicks — the shoulder buttons, so the sticks
        // and triggers stay free.
        SetLeft(PadShortcut.IsControlHeld(PadControl.R1));
        SetRight(PadShortcut.IsControlHeld(PadControl.L1));
    }

    /// <summary>Raw wheel units per pixel of finger travel when scrolling with two fingers.</summary>
    private const float TrackpadScrollGain = 2.4f;

    /// <summary>
    /// Scroll from a two-finger trackpad drag: the fingers' average vertical movement turns the
    /// wheel. Dragging up scrolls up, matching how the pointer follows a one-finger drag.
    /// </summary>
    private void DriveTrackpadScroll(PadTouch a, PadTouch b)
    {
        float avgY = (a.Y + b.Y) / 2f;

        if (_hadTwoFingers)
        {
            // Trackpad Y counts downward, so fingers moving up give a negative delta; negate it so
            // up is a positive (scroll-up) wheel movement.
            float dy = avgY - _lastTwoY;
            EmitWheel(-dy * TrackpadScrollGain);
        }

        _lastTwoY = avgY;
        _hadTwoFingers = true;
    }

    /// <summary>
    /// Raw wheel units a fully deflected scroll stick sends each tick. One notch is 120, so at ~120
    /// ticks a second this is roughly (value / 120 * 120) = value notches per second at full push —
    /// tuned so a gentle lean scrolls at once and a full push moves briskly.
    /// </summary>
    private const float StickScrollUnitsFull = 22f;

    /// <summary>
    /// Scroll from the non-cursor stick's vertical axis. Near-linear so even a small, steady lean
    /// scrolls immediately rather than needing to be held — the stick is already dead-zoned, so the
    /// value is zero until the stick is meaningfully pushed.
    /// </summary>
    private void ScrollByStick(float y)
    {
        // Same engage-then-follow rule as the cursor. Scrolling has no pixel floor to hide behind —
        // even a stick sitting a hair past its deadzone trickles wheel events that reset the idle
        // timer while barely scrolling — so it must be pushed to engage before any wheel is sent.
        if (!_scrollEngaged)
        {
            if (MathF.Abs(y) < StickEngage) return;
            _scrollEngaged = true;
        }
        else if (y == 0f)
        {
            _scrollEngaged = false;
            return;
        }

        NoteDriving("scroll stick");
        EmitWheel(y * StickScrollUnitsFull);
    }

    /// <summary>
    /// Accumulate fractional wheel movement and emit whole raw units as they build up. Sending raw
    /// units rather than whole notches is what makes it smooth: a small lean sends a steady trickle
    /// of a few units per tick instead of jumping a whole line at a time.
    /// </summary>
    private void EmitWheel(float units)
    {
        _scrollRemainder += units;

        int whole = (int)_scrollRemainder;   // truncates toward zero, correct for both signs
        if (whole != 0)
        {
            MouseInjector.WheelRaw(whole);
            _scrollRemainder -= whole;
        }
    }

    /// <summary>
    /// Move the cursor from a stick vector, shared by both pad types.
    ///
    /// The vector is already dead-zoned and up-positive. A response curve — squaring the
    /// magnitude — keeps small pushes slow for placing the pointer precisely while still reaching
    /// a brisk speed at full deflection.
    /// </summary>
    private void MoveByStick(float x, float y)
    {
        float mag = MathF.Sqrt(x * x + y * y);

        // Engage only when the stick is pushed clearly past its deadzone, then follow it all the way
        // back to rest. A stick idling just past the deadzone — drift — never reaches the engage
        // point, so it injects nothing and cannot hold the display awake.
        if (!_cursorEngaged)
        {
            if (mag < StickEngage) return;
            _cursorEngaged = true;
        }
        else if (mag <= 0f)
        {
            _cursorEngaged = false;
            return;
        }

        float speed = MaxStickPixels * Gain();

        // x already carries the magnitude, so x*mag is direction * magnitude², i.e. the squared
        // curve, without dividing out and multiplying back.
        int dx = (int)Math.Round(x * mag * speed);

        // Negated: the stick reports up as positive, the screen counts down as positive.
        int dy = (int)Math.Round(y * mag * speed * -1);

        if (dx != 0 || dy != 0) NoteDriving("cursor stick");
        MouseInjector.Move(dx, dy);
    }

    /// <summary>Pixels-per-poll at full stick, before the speed setting scales it.</summary>
    private const float MaxStickPixels = 22f;

    /// <summary>The slider's range. Its two ends map to <see cref="SlowGain"/> and <see cref="FastGain"/>.</summary>
    private const int SpeedMin = 1;
    private const int SpeedMax = 20;

    // The two extremes the slider reaches. Slow is a crawl for placing the pointer on a small
    // target; fast is well past the old ceiling, enough to throw the cursor clear across a TV in
    // one flick.
    private const float SlowGain = 0.10f;
    private const float FastGain = 5.0f;

    /// <summary>
    /// Turn the slider step into a speed multiplier along a geometric curve rather than a straight
    /// line. Equal drags feel like equal *proportional* changes — a nudge near the slow end barely
    /// moves the crawl, the same nudge near the fast end adds a lot — which is how speed actually
    /// reads to the hand. The middle of the track lands near 1.0, the old default feel.
    /// </summary>
    private float Gain()
    {
        float t = (Math.Clamp(_speed, SpeedMin, SpeedMax) - SpeedMin) / (float)(SpeedMax - SpeedMin);
        return SlowGain * MathF.Pow(FastGain / SlowGain, t);
    }

    private void SetLeft(bool down)
    {
        if (down == _leftDown) return;
        _leftDown = down;
        NoteDriving("buttons");
        if (down) MouseInjector.LeftDown(); else MouseInjector.LeftUp();
    }

    private void SetRight(bool down)
    {
        if (down == _rightDown) return;
        _rightDown = down;
        NoteDriving("buttons");
        if (down) MouseInjector.RightDown(); else MouseInjector.RightUp();
    }

    /// <summary>Let go of anything held, so a button cannot be stranded down.</summary>
    private void ReleaseButtons()
    {
        SetLeft(false);
        SetRight(false);
    }

    private DateTime _lastDriveLog = DateTime.MinValue;

    /// <summary>
    /// A throttled note that the pointer is injecting input. Injected mouse input resets the display
    /// sleep timer, so this makes it plain in the log when the pointer — driven by a resting or
    /// drifting controller — is what is keeping a screen awake, and from which source.
    /// </summary>
    private void NoteDriving(string source)
    {
        if (DateTime.UtcNow - _lastDriveLog < TimeSpan.FromSeconds(5)) return;
        _lastDriveLog = DateTime.UtcNow;
        Log.Info($"Pointer injected input from the {source}; this holds off display sleep.");
    }

    // ---- foreground: is a launcher the thing in front? ----

    private DateTime _fgCheckedAt = DateTime.MinValue;
    /// <summary>Big Picture has the foreground. It drives itself from the pad; never steer over it.</summary>
    private bool _bigPictureInFront;

    /// <summary>A game has the foreground. Overridable — see the hold button and "in games".</summary>
    private bool _gameInFront;

    /// <summary>Set by the tray app: true while the controller or hotkey settings page is capturing
    /// button presses, where a pad-driven cursor would fight the recording. Read live, so it clears
    /// the moment that page is left or the settings window closes.</summary>
    public Func<bool>? ControllerUiCapturing { get; set; }

    /// <summary>
    /// The running game's window, if there is one — supplied by the owner, which tracks it properly.
    ///
    /// The pointer must never be driven into a game, and telling a game apart by its size is a guess
    /// that fails on any title whose window is not exactly its monitor's rectangle.
    /// </summary>
    public Func<IntPtr>? GameWindow { get; set; }

    /// <summary>
    /// Whether the pointer should be driving right now, decided by what is in front.
    ///
    /// The mouse exists for one thing: a launcher window that ignores a controller. So rather than
    /// list everything it must stand down for, this turns the question around and drives only when a
    /// launcher is genuinely in front — an ordinary window that is not Big Picture, not the Steam
    /// client, not the desktop, and not a game filling the screen. Everything else, including the
    /// couch UI itself and the moments of switching into it, leaves the cursor alone.
    ///
    /// This is what stops the pointer flickering over Big Picture as you navigate it: Big Picture is
    /// never a launcher, so the answer there is always no.
    ///
    /// Cached briefly. The pointer loop runs ~120 times a second and this only changes on a window
    /// switch, so a quarter-second-old answer is more than fresh enough and saves hundreds of window
    /// queries a second.
    /// </summary>
    private void RefreshForeground()
    {
        if (DateTime.UtcNow - _fgCheckedAt < TimeSpan.FromMilliseconds(250)) return;
        _fgCheckedAt = DateTime.UtcNow;

        try
        {
            var hwnd = GetForegroundWindow();

            _bigPictureInFront = hwnd != IntPtr.Zero && hwnd == BigPictureLauncher.FindWindow();
            _gameInFront = GameInFront(hwnd);
        }
        catch
        {
            // If the question cannot be answered, assume a game: better a still cursor than one
            // fighting a game. A held hold-button still overrides this, which is the escape hatch.
            _bigPictureInFront = false;
            _gameInFront = true;
        }
    }

    /// <summary>
    /// Whether the window in front looks like a game.
    ///
    /// Asked this way round on purpose. The old question was "is this a launcher", which bundled two
    /// unrelated things together: places the pointer must never go at all (Big Picture, a shortcut
    /// box recording raw buttons) and places it merely should not go uninvited (a running game).
    /// Only the second can be overridden by the user holding the button, so the two had to separate
    /// before that override could exist.
    /// </summary>
    private bool GameInFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        // The Steam desktop client shares Big Picture's window class and can run full-screen on the
        // TV, where it would otherwise be read as a game just below. It is not a game — the pad should
        // steer it. The Windows desktop and taskbar fill the monitor too and would likewise be
        // mistaken for a game, so they are named here as well.
        if (hwnd == BigPictureLauncher.FindDesktopWindow()) return false;
        if (IsShell(hwnd)) return false;

        // A window covering nearly all of its monitor is a game; a launcher is a small window in the
        // middle of one. Size is what separates them, and identity is not: a launcher lives in the same
        // steamapps folder as the game it starts, so asking "is this the running game" answered yes for
        // Cyberpunk's launcher too and stood the pointer down in the one window that needs it most.
        //
        // Nearly all, rather than exactly all. Requiring the window to match the screen within two
        // pixels was the original problem — Cyberpunk's does not, so a running game was being driven
        // like a launcher. Ninety percent of both dimensions separates the two cases with room to
        // spare: a launcher is nowhere near it, and a fullscreen or borderless game clears it easily
        // however its window is inset.
        // Size alone was the test here, and size alone is wrong.
        //
        // "Covers the monitor" catches every fullscreen game, and it also catches a *maximized*
        // launcher — which is the one window this whole feature exists for. Epic, GOG Galaxy,
        // Battle.net and an installer are all ordinary windows someone has maximized, and every one
        // of them was being read as a game and left without a pointer.
        //
        // What actually separates them is the frame. A fullscreen or borderless game has no caption
        // bar and no resizable border; a maximized launcher keeps both, because it is still a normal
        // window that happens to fill the screen. So the size test now has to agree with the frame
        // before anything stands down.
        if (CoversMonitor(hwnd) && HasGameFrame(hwnd))
        {
            NoteStoodDown(hwnd);
            return true;
        }

        // Anything else — a launcher, an ordinary window — is not a game.
        return false;
    }

    /// <summary>
    /// Whether a window is framed the way a game is: no caption, no sizing border.
    ///
    /// Both exclusive fullscreen and borderless windowed strip the frame — that is what "borderless"
    /// means — while a maximized ordinary window keeps it. Read from the window's own style bits,
    /// which is a single call and needs nothing to be installed, running or configured.
    ///
    /// A game left in plain windowed mode and then maximized keeps its caption and so reads as a
    /// launcher here. That is the one case this gets wrong, and it is the harmless direction: the
    /// pointer is driven over a window that has a title bar and a cursor of its own anyway.
    /// </summary>
    private static bool HasGameFrame(IntPtr hwnd)
    {
        int style = GetWindowLong(hwnd, GWL_STYLE);

        // Zero means the call failed. Fall back to the old behaviour — treat it as a game — because
        // a still cursor over a game is a far better failure than one fighting it.
        if (style == 0) return true;

        return (style & (WS_CAPTION | WS_THICKFRAME)) == 0;
    }

    /// <summary>
    /// Say once, per window, why the pointer stopped driving.
    ///
    /// This decision is invisible from the outside — the cursor simply stops — and it is the kind of
    /// thing that is argued about rather than measured. One line naming the class and the style bits
    /// turns the next report of "the pad stopped working in X" into a lookup instead of a guess.
    /// </summary>
    private void NoteStoodDown(IntPtr hwnd)
    {
        if (hwnd == _lastStoodDown) return;
        _lastStoodDown = hwnd;

        try
        {
            var cls = new StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);

            Log.Info($"Pointer: standing down for a full-screen window with no frame "
                   + $"(class '{cls}', style 0x{GetWindowLong(hwnd, GWL_STYLE):X8}).");
        }
        catch { /* a diagnostic must never be the thing that breaks the poll */ }
    }

    private IntPtr _lastStoodDown;

    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    /// <summary>Style bits, on either word size. Only the low 32 bits of GWL_STYLE are defined.</summary>
    private static int GetWindowLong(IntPtr hwnd, int index)
    {
        try { return (int)(long)GetWindowLongPtr(hwnd, index); }
        catch (EntryPointNotFoundException) { return GetWindowLong32(hwnd, index); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    private static bool IsShell(IntPtr hwnd)
    {
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        var name = cls.ToString();
        return name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>How much of its monitor a window must cover before it is taken for a game.</summary>
    private const float GameCoverage = 0.90f;

    private static bool CoversMonitor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var win)) return false;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var screen = info.rcMonitor;

        int screenW = screen.Right - screen.Left;
        int screenH = screen.Bottom - screen.Top;
        if (screenW <= 0 || screenH <= 0) return false;

        int winW = win.Right - win.Left;
        int winH = win.Bottom - win.Top;

        return winW >= screenW * GameCoverage && winH >= screenH * GameCoverage;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private const uint MonitorDefaultToNearest = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buf, int max);

    public void Dispose()
    {
        _poll.Stop();
        ReleaseButtons();
        _poll.Dispose();
    }
}

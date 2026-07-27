using System.Runtime.InteropServices;
using CouchMode.Audio;
using CouchMode.Display;
using CouchMode.Games;
using CouchMode.Input;
using CouchMode.Session;
using CouchMode.Steam;

namespace CouchMode.Ui;

/// <summary>
/// The settings window.
///
/// Laid out as a sidebar of pages rather than tabs: there are now six pages and tabs stop
/// scaling well past four. Each page is a stack of cards, and every row is title, explanation
/// and control — the explanation is not optional, because almost nothing here can be inferred
/// from a label alone.
///
/// Built in code rather than the designer so the whole UI stays reviewable as plain text and
/// builds without Visual Studio.
/// </summary>
public sealed class SettingsForm : Form
{
    public AppConfig Config { get; }

    // ---- outward-facing results, read by the caller once the window closes ----

    /// <summary>Raised after a successful save so the running app can pick the changes up.</summary>
    public event Action<AppConfig>? Saved;

    /// <summary>
    /// Raised the instant a pointer setting moves, ahead of any save.
    ///
    /// A slider is only worth using when the thing it controls responds while you drag it — both
    /// NN/g and Microsoft put the bar at a tenth of a second. Saving is debounced by four hundred
    /// milliseconds and the timer restarts on every step, so during a drag the save never fires at
    /// all and the pointer kept its old speed until well after the thumb was released. The one
    /// control whose effect can be felt directly was the one giving no feedback.
    /// </summary>
    public event Action<AppConfig>? PointerPreview;

    public bool ExitRequested { get; private set; }
    public bool StartRequested { get; private set; }
    public bool RevertRequested { get; private set; }

    /// <summary>Close the game that was left running on the desktop, and nothing else.</summary>
    public bool CloseGameRequested { get; private set; }

    /// <summary>
    /// Asks the app whether a game is still running behind the desktop.
    ///
    /// A third state the footer had no way to see. "On the TV" and "at the desk" are not the only
    /// two things that can be true: dropping to the desktop from the prompt, or because a controller
    /// dropped out, leaves a game alive with the session's windows waiting behind it — and in that
    /// state "Start Couch Session" is the wrong offer, because nothing is going to be started.
    /// </summary>
    public Func<bool>? LeftRunningState { get; init; }

    /// <summary>
    /// Asks the app whether a *game* is running, as opposed to anything at all being left behind.
    ///
    /// Not the same question as LeftRunningState, and conflating them put a Close Game button on
    /// screen when the only thing waiting behind the desktop was Big Picture. There was nothing for
    /// it to close.
    /// </summary>
    public Func<bool>? GameRunningState { get; init; }

    /// <summary>Whether a session was running when this window opened.</summary>
    public bool SessionActive { get; init; }

    /// <summary>Asks the app whether a session is running right now, when it can.</summary>
    public Func<bool>? SessionState { get; init; }

    /// <summary>
    /// Whether a session is running at this moment.
    ///
    /// SessionActive is only ever true of the moment the window opened, and this window stays open
    /// across sessions started from the pad — so anything the user reads has to come from here.
    /// Falls back to the opening state when nothing was wired up, which is what the tests do.
    /// </summary>
    private bool SessionNow
    {
        get
        {
            try { return SessionState?.Invoke() ?? SessionActive; }
            catch { return SessionActive; }
        }
    }

    /// <summary>Whether a game is waiting behind the desktop right now. Never true during a session.</summary>
    private bool LeftRunningNow
    {
        get
        {
            if (SessionNow) return false;

            try { return LeftRunningState?.Invoke() ?? false; }
            catch { return false; }
        }
    }

    /// <summary>Whether there is a game to close right now.</summary>
    private bool GameRunningNow
    {
        get
        {
            try { return GameRunningState?.Invoke() ?? false; }
            catch { return false; }
        }
    }

    // ---- controls ----
    private readonly Dropdown _tvDisplay = NewDropdown();
    private readonly Dropdown _displayMode = NewDropdown();
    private readonly Dropdown _tvAudio = NewDropdown();
    private readonly Dropdown _desktopAudio = NewDropdown();
    private readonly Dropdown _steamAfter = NewDropdown();
    private readonly Dropdown _triggerPad = NewDropdown();
    private readonly Dropdown _tvVideoMode = NewDropdown();

    private readonly ToggleSwitch _switchAudio = new();
    private readonly ToggleSwitch _guideEndsSession = new();
    private readonly ToggleSwitch _restartSteam = new();
    private readonly ToggleSwitch _confirmClosingGame = new();
    private readonly ToggleSwitch _checkForUpdates = new();
    private readonly ToggleSwitch _disableUac = new();
    private bool _settingUac;
    private readonly ToggleSwitch _disableFirewall = new();
    private bool _settingFirewall;

    // Inline performance-check state: the note under each toggle, the last result (so it survives a
    // rebuild), and which toggles have had their check handler wired (so a rebuild does not stack
    // another).
    private readonly Dictionary<string, RichNote> _toggleNotes = [];
    private readonly Dictionary<string, CheckResult> _toggleChecks = [];
    private readonly HashSet<ToggleSwitch> _checkWired = [];
    private readonly Dropdown _hdrMode = new();

    /// <summary>Live text saying which controllers are connected right now.</summary>
    private RichNote? _padPresence;

    /// <summary>Live text naming both displays and what each can do about HDR.</summary>
    private RichNote? _hdrDisplayNote;

    /// <summary>
    /// Groups of rows that only exist on screen while the setting above them is on.
    ///
    /// Greying a nested setting out says "this is here but inert", which is worth saying when the
    /// row is one line. It is not worth saying six times over: a card whose parent is off was most
    /// of a screen of dimmed text explaining settings that cannot do anything, and the one control
    /// that could be used was buried in it. Hidden, the card is the question and its answer.
    /// </summary>
    private readonly List<(Func<bool> When, List<Control> Rows)> _nested = [];
    private InlineCheck _hdrHotkeyRemember = null!;
    private readonly ToggleSwitch _startWithWindows = new();
    private readonly ToggleSwitch _startOnLaunch = new();
    private readonly ToggleSwitch _startOnWake = new();
    private readonly ToggleSwitch _startOnWakeControllerOnly = new();
    private readonly ToggleSwitch _minimizeOnClose = new();
    private readonly ToggleSwitch _showNotifications = new();
    private readonly ToggleSwitch _showSessionNotifications = new();
    private readonly ToggleSwitch _showHdrNotifications = new();
    private readonly ToggleSwitch _showMinimizeNotification = new();
    private readonly ToggleSwitch _showProblemNotifications = new();
    private readonly ToggleSwitch _showActionNotifications = new();
    private readonly ToggleSwitch _turnOffTvOnExit = new();
    private readonly ToggleSwitch _disableGameBarButton = new();
    private readonly ShortcutBox _shortcutKeyboard = new(ShortcutBox.Source.Keyboard);
    private readonly ShortcutBox _shortcutController = new(ShortcutBox.Source.Controller);
    private readonly ShortcutBox _shortcutKeyboardHdr = new(ShortcutBox.Source.Keyboard);
    private readonly ToggleSwitch _mouseControl = new();
    private readonly ToggleSwitch _mouseHold = new();
    private readonly ToggleSwitch _mouseInGames = new();
    private readonly Dropdown _mouseHoldButton = new();
    private readonly Slider _mouseSpeed = new();
    private readonly Slider _mouseDeadzone = new();
    private readonly Dropdown _cursorStick = new();
    private readonly ToggleSwitch _useTrackpad = new();
    private readonly ShortcutBox _shortcutControllerHdr = new(ShortcutBox.Source.Controller);
    private readonly Dropdown _padLink = new();
    private readonly ToggleSwitch _changePowerPlan = new();
    private readonly Dropdown _powerPlan = NewDropdown();

    /// <summary>Explains an empty mode list. Rebuilt with the page, so it is not readonly.</summary>
    private RichNote? _videoModeNote;

    /// <summary>The controller picker's heading, which is reworded as its two switches change.</summary>

    /// <summary>Connected controllers in the title bar. Hides itself when there are none.</summary>
    private ControllerStrip? _controllers;

    /// <summary>Which display the mode picker's current selection belongs to.</summary>
    private string _videoModeOwner = "";
    private readonly ToggleSwitch _keepDisplayAwake = new();
    private readonly ToggleSwitch _silenceNotifications = new();
    private readonly ToggleSwitch _enableGameMode = new();
    private readonly ToggleSwitch _gamePriority = new();
    private readonly ToggleSwitch _startOnController = new();
    private readonly Dropdown _onControllerOff = new();
    private readonly Dropdown _onControllerLost = new();
    private readonly ToggleSwitch _muteOnDesktop = new();

    private readonly GameIcons _icons = new();
    private readonly HashSet<string> _checkedGames = new(StringComparer.OrdinalIgnoreCase);
    private GameListView _gameList = null!;
    private FlatButton _hdrBulk = null!;
    private FlatButton _hdrBulkOther = null!;
    private SearchBox _search = null!;
    private Panel? _listHolder;
    private GameListHeader? _listHeader;
    private Panel? _actingRow;

    /// <summary>
    /// The two buttons that moved up to the search row, held so they can still be greyed out with the
    /// rest of the list when HDR switching is off.
    ///
    /// They used to sit in the row that gets disabled wholesale. Moving them without this would have
    /// quietly left them live on a page where nothing else is — a function changed by a layout tidy-up,
    /// which is exactly the sort of thing a layout tidy-up must not do.
    /// </summary>
    private Control? _browseButton, _scanButton;
    private ThinScrollBar? _listBar;
    private List<GameEntry> _games = [];

    private Label _saveNote = null!;

    /// <summary>
    /// Coalesces a burst of changes into one write.
    ///
    /// Dragging a dropdown through its items fires a change per item; without this, each one
    /// would rewrite the file and notify the running app.
    /// </summary>
    private readonly System.Windows.Forms.Timer _autoSave = new() { Interval = 400 };
    private FlatButton _action = null!;
    private FlatButton? _closeGame;
    private FlowLayoutPanel? _footerButtons;
    private Label _deviceNote = null!;

    private Panel _pageHost = null!;
    private Panel _rail = null!;
    private readonly List<(NavItem Nav, Panel Page)> _pages = [];

    // ---- state ----
    private bool _loading;
    private bool _reloading;
    private string _lastDeviceSignature = "";

    private readonly System.Windows.Forms.Timer _deviceWatch = new() { Interval = 2500 };
    private readonly System.Windows.Forms.Timer _gameWatch = new() { Interval = 5 * 60 * 1000 };
    private readonly System.Windows.Forms.Timer _noteTimer = new() { Interval = 6000 };
    private readonly System.Windows.Forms.Timer _savedNoteTimer = new() { Interval = 2500 };
    /// <summary>
    /// Tooltips, wrapped by hand.
    ///
    /// The native tooltip only wraps at an explicit newline — otherwise it lays a long
    /// description out as one line and stretches most of the way across the display, which is
    /// unreadable and covers whatever it is describing. See <see cref="Wrapped"/>.
    /// </summary>
    /// <summary>
    /// The hover tips, drawn by this app rather than by Windows.
    ///
    /// A stock ToolTip is painted by the shell in the system's own colours — pale yellow or white,
    /// with a system border and the system font. Against this window that arrives looking like a
    /// message from a different program, which is the same objection the prompts, the message boxes
    /// and the update dialog were all rewritten for. Owner-drawing is the only way to change it:
    /// BackColor and ForeColor on a ToolTip are ignored outright unless OwnerDraw is set.
    /// </summary>
    private readonly ToolTip _tips = new()
    {
        // As long as Windows will allow, which is not "forever".
        //
        // AutoPopDelay goes to the shell through TTM_SETDELAYTIME, whose value is a signed 16-bit
        // number — so 32767 milliseconds is the ceiling and anything larger is silently clamped or
        // rejected. There is no flag for a tip that never retires. In practice half a minute of
        // hovering is longer than anyone spends reading one of these, and it is a large multiple of
        // the five seconds Windows uses by default.
        AutoPopDelay = 32767,
        InitialDelay = 400,
        ReshowDelay = 100,
        OwnerDraw = true,
        UseAnimation = false,
        UseFading = false,
    };

    /// <summary>Air inside a tip, around the text.</summary>
    private const int TipPadX = 10, TipPadY = 7;

    /// <summary>How wide a tip is allowed to get before it wraps, in pixels.</summary>
    private const int TipMaxWidth = 380;

    /// <summary>
    /// One set of flags for measuring and for drawing.
    ///
    /// [BUG] They were different — NoPadding when measured, no WordBreak when drawn — and the text
    /// was pre-wrapped by counting *characters* on top of that. Three ways of deciding where a line
    /// ends, none of them agreeing, and the visible result was every line clipped a word short of
    /// its own right edge. Measure and draw now use the same flags on the same string, which is the
    /// only arrangement that cannot disagree.
    /// </summary>
    private const TextFormatFlags TipFlags =
        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak
      | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    /// <summary>
    /// The tip's text as one paragraph, for the layout below to break by pixel.
    ///
    /// The line breaks in it were put there by Wrapped(), which counts characters — so a line of
    /// narrow letters came out far short of the edge and a line of wide ones ran past it. Undone
    /// here rather than at every call site, because a tooltip that measures its own text does not
    /// need help deciding where the lines go.
    /// </summary>
    private static string TipText(string? text) =>
        text is null ? "" : text.Replace("\r\n", " ").Replace('\n', ' ');

    /// <summary>
    /// Measure a tip at the app's own font, so the window Windows makes is the size we will fill.
    ///
    /// Popup runs before the tooltip window is sized: whatever is put in ToolTipSize here is exactly
    /// what the Draw handler gets to paint into, and anything that does not fit is simply lost.
    /// </summary>
    private void OnTipPopup(object? sender, PopupEventArgs e)
    {
        string? raw = _tips.GetToolTip(e.AssociatedControl);
        if (string.IsNullOrEmpty(raw)) return;

        string text = TipText(raw);

        var size = TextRenderer.MeasureText(text, Theme.Small,
                                            new Size(TipMaxWidth, 0), TipFlags);

        // A pixel of slack on each side. GDI's measure and its draw can differ by one at a word
        // boundary, and when they do it is the last word of a line that vanishes.
        e.ToolTipSize = new Size(size.Width + TipPadX * 2 + 2, size.Height + TipPadY * 2 + 2);
    }

    private void OnTipDraw(object? sender, DrawToolTipEventArgs e)
    {
        var g = e.Graphics;

        // Square corners, and not for want of trying rounded ones.
        //
        // [BUG] This drew a rounded card, which left the four corners showing whatever the tooltip
        // window had been filled with before the draw — black. A tooltip is a plain top-level window
        // with no alpha channel and no way to reach its handle from here, so the area outside a
        // rounded path cannot be made transparent: there is nothing behind it to show through. The
        // choices are a rounded shape with black corners or a rectangle with none, and a crisp
        // bordered rectangle is what every tooltip on Windows already looks like.
        //
        // Filled edge to edge for the same reason. Any pixel this handler does not paint is a pixel
        // the shell already painted, and it will not match.
        using (var back = new SolidBrush(Theme.SurfaceHi)) g.FillRectangle(back, e.Bounds);

        using (var edge = new Pen(Theme.Line))
            g.DrawRectangle(edge, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);

        TextRenderer.DrawText(g, TipText(e.ToolTipText), Theme.Small,
                              new Rectangle(e.Bounds.X + TipPadX, e.Bounds.Y + TipPadY,
                                            e.Bounds.Width - TipPadX * 2,
                                            e.Bounds.Height - TipPadY * 2),
                              Theme.Text, TipFlags);
    }

    /// <summary>Characters per line before a tooltip is broken. Roughly 70 is comfortable.</summary>
    private const int TipWidth = 72;

    /// <summary>Strip the ** emphasis markers, for places that cannot render them.</summary>
    private static string Plain(string markup) => markup.Replace("**", "");

    /// <summary>Break a description into lines on word boundaries.</summary>
    private static string Wrapped(string text)
    {
        var lines = new List<string>();
        var line = new System.Text.StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > TipWidth)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0) lines.Add(line.ToString());

        return string.Join(Environment.NewLine, lines);
    }

    private const string AutomaticAudio = Words.AutomaticAudio;
    private const int RailWidth = 208;
    private const int TitleBarHeight = 42;
    /// <summary>
    /// How tall the footer bar is.
    ///
    /// Six pixels taller than it was, to give the primary button its glow back. That glow is painted
    /// inside the control, which therefore has to be taller than the button — and at the old height
    /// the bottom of it ran straight into the window edge and was sliced off. Everything else in the
    /// footer is centred against this constant rather than placed at a literal Y, so the bar can be
    /// retuned here without hunting for four hard-coded offsets that no longer agree.
    /// </summary>
    private const int FooterHeight = 72;

    /// <summary>Height of the name-and-version block above the navigation list.</summary>
    private const int BrandHeight = 74;

    private readonly ControllerWatcher? _pads;

    public SettingsForm(AppConfig config, ControllerWatcher? pads = null)
    {
        Config = config;
        _pads = pads;

        // The list follows what is actually plugged in, without waiting to be asked.
        if (_pads is not null) _pads.DevicesChanged += OnControllersChanged;

        Text = "Couch Session";
        Icon = AppIcon.MonogramIcon(32);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(RailWidth + ContentWidth + 60, 720);   // FitToContent adjusts this

        // Explicit, because this window is the app's face while it is open.
        ShowInTaskbar = true;
        // Small enough to be usable on a 720p television, which is the case this exists for.
        MinimumSize = new Size(RailWidth + MinimumContentWidth + 60, 380);
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;

        var body = BuildBody();
        var footer = BuildFooter();
        var titleBar = BuildTitleBar();

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(titleBar);

        WindowCorners.Round(this);

        // The children cover every edge, so they are what has to notice the pointer nearing one.
        //
        // Resizing matters more than it looks: a couch session makes the TV primary, and the
        // window can end up on a display far smaller than the one it was built for, where a
        // window too wide to reach the edges of is genuinely hard to use with a pad in hand.
        ResizeGrip.Enable(this);

        // The pad triggers follow this window's focus — see UpdatePadSuspension. Minimising counts
        // as losing it, and WinForms reports that through Resize rather than Deactivate.
        Activated += (_, _) => { _windowActive = true; UpdatePadSuspension(); };
        Deactivate += (_, _) => { _windowActive = false; UpdatePadSuspension(); };
        Resize += (_, _) => UpdatePadSuspension();

        _tips.Popup += OnTipPopup;
        _tips.Draw += OnTipDraw;

        LoadDevices();
        LoadGames();
        LoadFromConfig();
        WireDirtyTracking();
        UpdateDependentStates();
        _autoSave.Tick += (_, _) => { _autoSave.Stop(); Save(silent: true); };
        StartWatchers();

        // Advisory data only, so it is fetched quietly and the badges appear when it lands.
        //
        // The refresh itself is kicked off by LoadGames, which the constructor already called.
        // Starting it here as well ran two copies concurrently: every result was logged twice
        // and both raced to write the cache, which is why the log showed
        // "hdr-support.json.tmp ... used by another process".
        HdrDatabase.Updated += OnHdrDataUpdated;
        UpdateHdrBulkLabel();

        ShowPage(0);
        FitToContent();
    }

    /// <summary>
    /// Size the window so the tallest page fits without scrolling.
    ///
    /// Measured rather than hard-coded, because a fixed size silently goes stale the moment any
    /// wording or spacing changes — which is exactly how a scrollbar kept reappearing on pages
    /// that were supposed to fit. The games list is excluded: it is meant to scroll, and sizing
    /// the window to a full library would produce a window taller than the screen.
    /// </summary>

    /// <summary>
    /// Nothing is rebuilt while the mouse is down. That is the whole trick.
    ///
    /// Rebuilding a page means creating twenty-odd controls, and every WinForms control is a
    /// real window — CreateWindowEx, a handle, a message queue. That cost is structural and
    /// cannot be optimised away; caching the text measurement and skipping the sort helped, but
    /// the handles remained, and doing that repeatedly during a drag will never be smooth.
    ///
    /// So during a drag the existing layout simply stays put and the frame moves freely, which
    /// is smooth because nothing is being constructed. The rebuild happens once, on release.
    ///
    /// Resizes that are not a drag — maximise, a snap, a programmatic change — never enter this
    /// state and so rebuild immediately, which is what keeps those feeling instant.
    /// </summary>
    private bool _dragging;

    protected override void WndProc(ref Message m)
    {
        const int WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;

        if (m.Msg == WM_ENTERSIZEMOVE) _dragging = true;

        base.WndProc(ref m);

        if (m.Msg != WM_EXITSIZEMOVE) return;

        _dragging = false;
        if (_pages.Count > 0) Rebuild(WidthForWindow());
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        // Before the early return below, deliberately. Those guards are about whether the pages
        // need rebuilding, which has nothing to do with whether the new size is worth recording.
        RememberPlacement();

        if (_pages.Count == 0 || _rebuilding || _dragging) return;

        Rebuild(WidthForWindow());
    }

    /// <summary>
    /// Rebuild with painting held off until it is finished.
    ///
    /// WM_SETREDRAW stops the window drawing anything at all while controls are torn down and
    /// recreated. Without it each new control paints as it is added, over a background that has
    /// not been repainted yet, which reads as boxes flashing.
    /// </summary>
    private void Rebuild(int width)
    {
        if (width == _contentWidth || _rebuilding) return;

        _rebuilding = true;

        try { Frozen(() => RebuildAt(width)); }
        finally { _rebuilding = false; }
    }

    /// <summary>
    /// Do something to the control tree with the window's painting held still.
    ///
    /// WM_SETREDRAW off means Windows stops painting this window and its children entirely, so
    /// controls being created, destroyed, moved and shown produce no output at all. Switching it
    /// back on does not repaint by itself — nothing was invalidated while it was off — which is
    /// why the redraw afterwards is explicit and includes children.
    ///
    /// RedrawWindow rather than Invalidate: Invalidate marks this window and asks politely,
    /// while RDW_ALLCHILDREN says to walk the whole tree, and RDW_UPDATENOW says to do it before
    /// returning rather than whenever the message queue next idles. The difference is visible —
    /// leaving it to the queue is what showed as a flash of half-painted page.
    /// </summary>
    private void Frozen(Action work)
    {
        if (!IsHandleCreated) { work(); return; }

        // Counted, because these nest: switching pages freezes the window and then rebuilds a stale
        // page inside that freeze, which freezes it again. WM_SETREDRAW is not refcounted by Windows,
        // so the inner unfreeze used to switch drawing back on halfway through the outer one — and
        // the half-built page painted, which is the flash this exists to prevent.
        if (_freezeDepth++ == 0) SendMessage(Handle, WM_SETREDRAW, 0, 0);

        try { work(); }
        finally
        {
            if (--_freezeDepth == 0)
            {
                SendMessage(Handle, WM_SETREDRAW, 1, 0);

                RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero,
                             RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
            }
        }
    }

    private int _freezeDepth;

    private const int WM_SETREDRAW = 0x000B;

    /// <summary>Guards against a rebuild resizing something and re-entering OnResize.</summary>
    private bool _rebuilding;

    /// <summary>The content width that fits the window as it currently is.</summary>
    private int WidthForWindow()
    {
        var pad = _pages.Count > 0 ? _pages[0].Page.Padding : new Padding(30, 22, 30, 18);
        int room = ClientSize.Width - RailWidth - pad.Horizontal;

        return Math.Clamp(room, MinimumContentWidth, PreferredContentWidth);
    }

    /// <summary>
    /// Lay the visible page out again, from outside any layout pass.
    ///
    /// A card's height comes from a SizeChanged on its row stack, and that only fires once the
    /// layout engine has genuinely run over it. During construction it has not, so the cards
    /// start at zero height and stay there until something else provokes a pass — which is what
    /// switching to another page and back was doing.
    /// </summary>
    private void RefreshVisiblePage()
    {
        if (IsDisposed || _pages.Count == 0 || !IsHandleCreated) return;

        var page = _pages[Math.Clamp(_currentPage, 0, _pages.Count - 1)].Page;

        // Hide it and show it again.
        //
        // This is the whole fix, and it is deliberately the same thing a tab switch does.
        //
        // Every page is created hidden, and WinForms does not lay out a control that has never
        // been visible — so the row stacks inside never measure themselves. ShowPage does flip
        // the first page visible during construction, but the form has no handle at that point,
        // so the transition is a no-op and the page stays unmeasured. Switching to another tab
        // and back worked because it repeated the transition once the window was real.
        //
        // Repeating it here, at the first moment the window genuinely exists, is what the
        // layout engine was waiting for. Laying out the parts by hand does not substitute: the
        // stack only measures on a real visibility change, not on a forced pass.
        //
        // Held frozen, because this is a deliberate flash. Hiding and showing a page is exactly
        // the sequence that produces a visible blink, and doing it on every launch and every
        // rebuild is what the user was seeing. The layout still happens; it is simply not
        // painted halfway through.
        Frozen(() =>
        {
            page.Visible = false;
            page.Visible = true;
        });
    }

    /// <summary>
    /// Everything that has to happen once the window is really on screen.
    ///
    /// This is the first moment every handle exists and the layout engine has run for real,
    /// which is exactly what the cards were waiting for.
    /// </summary>
    /// <summary>
    /// Force a taskbar button, whatever this window ends up owned by.
    ///
    /// ShowDialog with no owner argument does not mean "no owner" — WinForms hands the call the
    /// foreground window instead, which at launch is whatever the user happened to be using.
    /// An owned window does not get a taskbar button of its own, so the settings window was
    /// quietly parented to some other application and had no button until it was clicked.
    ///
    /// WS_EX_APPWINDOW says "give this one a button regardless", which is exactly the case this
    /// is for. Fixing it here rather than by hunting for the right owner means it holds however
    /// the window comes to be shown.
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_APPWINDOW = 0x00040000;

            var parameters = base.CreateParams;
            parameters.ExStyle |= WS_EX_APPWINDOW;

            return parameters;
        }
    }

    /// <summary>
    /// Lay the first page out before the window is ever painted.
    ///
    /// This has to happen in Load, not Shown. Shown fires *after* Windows has put the form on
    /// screen, so doing the work there means the first frame the user sees is the page in its
    /// unmeasured state — every row stacked at the origin — with the real layout arriving a
    /// frame later. That one-frame difference is the flash.
    ///
    /// Load runs after the handle exists but before the window is displayed, which is both late
    /// enough for the visibility transition RefreshVisiblePage depends on and early enough that
    /// nothing has been drawn yet.
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RefreshVisiblePage();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Still done here as well. Load covers the ordinary path; this covers the cases where
        // the form is shown by a route that skipped it, and by now it is a no-op that costs one
        // layout pass — the page is already measured, so nothing moves and nothing repaints.
        RefreshVisiblePage();

        // Opened from the tray at startup, so nothing has given it focus. Without this it can
        // come up behind whatever was already on screen.
        Activate();
        BringToFront();

        // The one-time welcome, after the form is on screen so it has something to centre on. Both
        // buttons retire it permanently. Deliberately not a tour — see WelcomeDialog.
        //
        // [BUG] Its button used to call ShowPage(0), which is Home — the page the window already opens
        // on. So the one button offering to take a new user somewhere took them nowhere, and did it
        // silently. The comment here even explained the intent, pointing at "the setup list under the
        // status card", which is the list that was deleted. It goes to Display & Audio now, which is
        // the page that actually has to be filled in and the one the wording names.
        if (!Config.FirstRunWelcomeShown)
        {
            Config.FirstRunWelcomeShown = true;
            try { Config.Save(); } catch { /* shown-once is best effort */ }

            BeginInvoke(() =>
            {
                if (IsDisposed || Disposing) return;

                using var welcome = new WelcomeDialog();
                if (welcome.ShowDialog(this) == DialogResult.OK && welcome.GoToSetup)
                    ShowPage(DisplayPage);
            });
        }
    }

    /// <summary>
    /// Belt and braces for the same problem.
    ///
    /// A window can be given its handle without OnShown running — the tray opens this form in
    /// more than one way — so the refresh is hung off handle creation as well.
    ///
    /// Posted rather than called: at handle creation the form is not laid out yet, and forcing
    /// it here is too early. The cost is that this one lands *after* the first paint, which is
    /// why it is no longer the thing relied on — see OnLoad.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        BeginInvoke(RefreshVisiblePage);
    }

    private void FitToContent()
    {
        int tallest = 0;

        foreach (var (_, page) in _pages)
        {
            var stack = Stack(page);
            stack.PerformLayout();
            tallest = Math.Max(tallest, stack.PreferredSize.Height);
        }

        var pad = _pages[0].Page.Padding;
        int height = TitleBarHeight + FooterHeight + pad.Vertical + tallest;
        int width = RailWidth + pad.Horizontal + ContentWidth;

        var work = Screen.FromControl(this).WorkingArea;

        // The window can never be shorter than its own navigation list.
        //
        // [BUG] The floor used to be a flat 380px, chosen when there were fewer pages. The rail does
        // not scroll — it is a FlowLayoutPanel with AutoScroll off and WrapContents off — so anything
        // that does not fit is simply clipped away with nothing to say it is there. At 380 the rail
        // had room for about four and a half of the eight entries, which meant Performance, General
        // and About could be dragged out of existence and the only way back was to make the window
        // taller again, having no way to know that was the problem.
        //
        // Measured from the entries rather than written down as a number, so adding a ninth page
        // moves the floor by itself instead of quietly re-creating this bug.
        int rail = BrandHeight + _nav.Padding.Vertical;
        foreach (Control item in _nav.Controls) rail += item.Height + item.Margin.Vertical;

        // Clamped to the screen as well as to the content. On a display too short for the full rail
        // the floor has to give way — a minimum taller than the maximum is not a size at all, and
        // Windows resolves that pair in whichever order it happens to apply them.
        MinimumSize = new Size(MinimumSize.Width,
            Math.Min(Math.Max(MinimumSize.Height, TitleBarHeight + FooterHeight + rail),
                     work.Height - 24));

        // Capped as a share of the screen, not a fixed number of pixels.
        //
        // Sizing to the tallest page meant the window opened at the height of whichever page
        // happened to be longest, which is taller than any single page needs. A proportion
        // keeps it sensible on a 1080p television and on a tall desktop monitor alike, where a
        // pixel figure that suited one would be wrong on the other.
        //
        // Raised from 0.72: at that share most pages started scrolling almost immediately,
        // which on a screen with room to spare is a worse trade than a slightly larger window.
        // Raised again, to 0.92. WorkingArea already excludes the taskbar, so the only thing this
        // share is buying is a margin around the window, and a page that has to scroll to show its own
        // checklist is a worse outcome than a window close to the top and bottom of the screen. The
        // second clamp is the real guarantee that it stays on screen; the share only stops a short
        // page opening as a needlessly tall window.
        int cap = (int)(work.Height * 0.92);

        ClientSize = new Size(Math.Min(width, work.Width - 60),
                              Math.Min(Math.Min(height, cap), work.Height - 24));

        // The size the window is designed at, and from here its ceiling.
        //
        // Dragging it wider buys nothing: the content width is fixed and every page is rebuilt to
        // it, so the extra goes into margin. Taller is the same story below the last card. Smaller
        // still earns its keep — getting the window out of the way of something behind it — so only
        // the upper bound is pinned.
        //
        // Set after the size is chosen, never before: a maximum in place first would clamp the very
        // assignment that establishes what the maximum should be. The form is borderless, so Size and
        // ClientSize are the same thing here.
        MaximumSize = Size;

        if (!RestorePlacement())
            Location = new Point(work.Left + (work.Width - Width) / 2,
                                 work.Top + (work.Height - Height) / 2);

        // Whatever the window ended up as, the pages have to agree with it.
        Rebuild(WidthForWindow());
    }

    /// <summary>
    /// Put the window back where it was last left, if that is still somewhere it can be seen.
    ///
    /// The check is the point of this. A couch session can leave the window on the television,
    /// and the television is very often switched off and detached by the time the app is opened
    /// again — so a saved position is quite reasonably a position that no longer exists. Windows
    /// will happily place a window there and it simply never appears, which reads as the app
    /// failing to start.
    ///
    /// So the saved rectangle has to overlap a screen that is actually attached, and by enough to
    /// grab hold of rather than by a single pixel. Anything else falls back to centring, which is
    /// always safe.
    /// </summary>
    private bool RestorePlacement()
    {
        if (!Config.HasWindowPlacement) return false;

        var saved = new Rectangle(Config.WindowLeft, Config.WindowTop,
                                  Config.WindowWidth, Config.WindowHeight);

        // Enough of the title bar reachable to move it with the mouse.
        const int Grabbable = 120;

        foreach (var screen in Screen.AllScreens)
        {
            var overlap = Rectangle.Intersect(screen.WorkingArea, saved);

            if (overlap.Width < Grabbable || overlap.Height < TitleBarHeight) continue;

            // Size first: setting the location against the old size can push it off the edge.
            //
            // Clamped at both ends. A window saved before the ceiling existed, or saved on a larger
            // screen, would otherwise come back bigger than the window is designed to be.
            int maxWidth = MaximumSize.Width > 0 ? MaximumSize.Width : int.MaxValue;
            int maxHeight = MaximumSize.Height > 0 ? MaximumSize.Height : int.MaxValue;

            Size = new Size(Math.Clamp(saved.Width, MinimumSize.Width, maxWidth),
                            Math.Clamp(saved.Height, MinimumSize.Height, maxHeight));

            Location = saved.Location;
            return true;
        }

        Log.Info("The saved window position is no longer on any attached display; centering instead.");
        return false;
    }

    /// <summary>
    /// Come back from a session: un-minimize, and go back where the session found this window.
    ///
    /// Both halves have to happen together and with recording held off, which is why this is one
    /// method rather than the tray doing it in two steps. Un-minimizing raises OnMove and OnResize
    /// for whatever position the session left the window in, and those handlers write it down —
    /// so a restore done afterwards would faithfully read back the shunted position it was
    /// supposed to be undoing.
    /// </summary>
    public void ReturnFromSession()
    {
        _holdPlacement = true;

        try
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;

            // Falls back to leaving it where it is. Wherever that turns out to be it is at least
            // on a screen, which a saved position pointing at a television that has since been
            // switched off is not.
            RestorePlacement();
        }
        finally
        {
            _holdPlacement = false;
        }
    }

    /// <summary>Set while the window is being moved by us rather than by the user.</summary>
    private bool _holdPlacement;

    /// <summary>
    /// Note where the window is, whenever it moves or is resized.
    ///
    /// Kept in the config object rather than written to disk here: this fires throughout a drag,
    /// and writing a file on every mouse move to record a window position would be absurd. The
    /// file is written when the window closes.
    /// </summary>
    private void RememberPlacement()
    {
        // Only the normal state. A minimized window reports a position off the edge of the world,
        // and a maximized one reports a size the user never chose — restoring to either is worse
        // than having nothing saved at all.
        if (_holdPlacement) return;
        if (WindowState != FormWindowState.Normal) return;
        if (!IsHandleCreated || !Visible) return;

        Config.WindowLeft = Bounds.Left;
        Config.WindowTop = Bounds.Top;
        Config.WindowWidth = Bounds.Width;
        Config.WindowHeight = Bounds.Height;
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        RememberPlacement();
    }

    // ================= chrome =================

    private Control BuildTitleBar()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = TitleBarHeight, BackColor = Theme.TitleBar };

        // A hairline along the bottom, so the strip reads as chrome rather than as more window.
        bar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Line });

        // The app's name and the page you are on, in the window's own title strip rather than beside
        // the logo below it. This is the line the eye treats as "where am I", and it is where a window
        // title belongs. Drawn as one control so the space around the dash is a number rather than a
        // negotiation between two auto-sizing labels — see TitleText.
        _pageTitle = new TitleText
        {
            App = "Couch Session",
            Location = new Point(18, 0),
            Height = TitleBarHeight - 1,
        };

        bar.Controls.Add(_pageTitle);

        var close = new FlatButton
        {
            Text = "✕",
            Size = new Size(40, 28),
            Fill = Theme.TitleBar,
            FillHover = Theme.Bad,
            Line = Color.Empty,
            Radius = 6,
            Font = Theme.Body,
        };
        close.Click += (_, _) => Close();

        // Connected controllers, in the chrome so they are on screen whatever page is showing.
        _controllers = new ControllerStrip
        {
            Height = TitleBarHeight,
            Names = Config.ControllerNames,
            Cursor = Cursors.Hand,
        };

        // And a way in. It names your controllers and reports their battery, so it is the first place
        // anyone looks when they want to rename one or change what the pad does — clicking the thing
        // you are already looking at should take you there.
        _controllers.Click += (_, _) => ShowPage(ControllerPage);

        _tips.SetToolTip(_controllers, "Controller settings");

        void PlaceChrome()
        {
            close.Location = new Point(bar.Width - close.Width - 8, 7);

            // Right-aligned against the close button, and only as wide as the pads make it.
            _controllers.Location = new Point(close.Left - _controllers.Width - 14, 0);
        }

        bar.Resize += (_, _) => PlaceChrome();

        // The strip changes width as controllers come and go, so it has to be re-placed then too
        // rather than only when the window is resized.
        _controllers.ContentResized += (_, _) => PlaceChrome();

        PlaceChrome();

        // Borderless windows do not drag themselves; hand the click to the window manager.
        bar.MouseDown += DragWindow;
        _pageTitle.MouseDown += DragWindow;

        bar.Controls.Add(close);
        bar.Controls.Add(_controllers);
        return bar;
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        // The top edge runs straight through the title bar, and resizing takes precedence.
        if (sender is Control surface && ResizeGrip.OnEdge(this, surface, e.Location)) return;
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }

    private Control BuildBody()
    {
        // Flat, deliberately. See the note on Theme.FillGradient: rounded controls fill their
        // corners with the parent's colour, so a gradient behind them shows as a seam.
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window };

        // A plain Panel, deliberately.
        //
        // This was briefly a BufferedPanel — WS_EX_COMPOSITED — to stop controls flashing as a
        // rebuild recreated them. It worked for that and broke something worse: a composited
        // parent does not paint children that were created before its own handle existed, and
        // the pages are all built during construction. The result was a page that stayed blank
        // until something invalidated it, which is exactly what switching tabs and back did.
        //
        // The flashing is handled by holding WM_SETREDRAW across the rebuild instead, which
        // costs nothing and has no such side effect.
        _pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window };

        body.Controls.Add(_pageHost);
        body.Controls.Add(BuildRail());
        return body;
    }

    private Control BuildRail()
    {
        var rail = _rail = new Panel { Dock = DockStyle.Left, Width = RailWidth, BackColor = Theme.Rail };

        var brand = new Panel { Dock = DockStyle.Top, Height = BrandHeight, BackColor = Theme.Brand };

        // Separates the identity block from the navigation below it.
        brand.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Line });

        var badge = new PictureBox
        {
            Size = new Size(34, 34),
            Location = new Point(16, 20),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        try { badge.Image = AppIcon.Monogram(68); } catch { /* the badge is decorative */ }

        brand.Controls.Add(badge);

        var nameLabel = new Label
        {
            Text = AppInfo.Name,
            Font = Theme.Heading,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(58, 22),
            BackColor = Color.Transparent,
        };
        brand.Controls.Add(nameLabel);

        brand.Controls.Add(new Label
        {
            Text = AppInfo.DisplayVersion,
            Font = Theme.Small,
            ForeColor = Theme.TextFaint,
            AutoSize = true,
            Location = new Point(60, 42),
            BackColor = Color.Transparent,
        });

        var nav = _nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Rail,
            Padding = new Padding(11, 6, 11, 0),
        };

        BuildPages();

        rail.Controls.Add(nav);
        rail.Controls.Add(brand);
        return rail;
    }

    private FlowLayoutPanel _nav = null!;
    private int _currentPage;

    /// <summary>
    /// Build every page and its navigation entry, at the current content width.
    ///
    /// Separated out so it can be run again after a resize. Rebuilding is what makes resizing
    /// work at all: the pages are laid out by hand against a fixed width, and the reliable way
    /// to lay them out against a different one is to run the same construction code with a
    /// different number — not to walk the finished tree reassigning sizes, which collapses the
    /// cards, because their height comes from rows that no longer agree with their contents.
    /// </summary>
    /// <summary>
    /// One builder per nav item, in the same order. Words.NavItems is the list the user sees;
    /// these two must stay lined up, since pages are matched to names by position.
    /// </summary>
    private Func<Panel>[] Builders => [BuildHomePage, BuildDisplayPage, BuildHdrPage,
                                       BuildControllerPage, BuildShortcutsPage, BuildPerformancePage,
                                       BuildGeneralPage, BuildAboutPage];

    /// <summary>The sidebar glyph for each page, in the same order as <see cref="Builders"/> and
    /// <see cref="Words.NavItems"/>. All three lists must stay lined up.</summary>
    private static readonly NavGlyph[] NavIcons =
        [NavGlyph.Home, NavGlyph.Display, NavGlyph.Hdr,
         NavGlyph.Controller, NavGlyph.Hotkeys, NavGlyph.Performance,
         NavGlyph.General, NavGlyph.About];

    /// <summary>
    /// Where each page sits in Builders.
    ///
    /// Named, because these were scattered through the file as bare numbers and removing one page
    /// silently re-aimed every link past it — a tile that went to Hotkeys now going to Performance,
    /// with nothing to notice it. The games list was already being rebuilt on the wrong page for
    /// exactly that reason. Numbers here, names everywhere else.
    /// </summary>
    /// <summary>Room taken by the two buttons that share the search row, margins included.</summary>
    private const int BrowseWidth = 78, ScanWidth = 66;

    /// <summary>The selection as it was before the last bulk change, for undo.</summary>
    private HashSet<string>? _undoChecked;

    /// <summary>Showing only the ticked games. A view filter — it never changes what is ticked.</summary>
    private bool _onlyTicked;

    private FlatButton? _selectAll, _clearAll;

    /// <summary>Shown over the list when a filter has left nothing in it.</summary>
    private Label? _emptyNote;

    /// <summary>The result of a scan, shown briefly above the button that started it.</summary>
    private Label? _scanNote;

    private readonly System.Windows.Forms.Timer _scanNoteTimer = new() { Interval = 6000 };

    /// <summary>Room for the item count at the end of the search row.</summary>
    private const int CountWidth = 168;

    /// <summary>
    /// How much of the library you are looking at, and how much of it is on.
    ///
    /// The last element in the toolbar, which is where a count belongs when there is no paging. It
    /// exists because the search box silently changes what is on screen: typing three letters can hide
    /// two hundred games, and without this there is nothing to say it happened or how much was hidden.
    /// It also answers the question the whole page is about — how many games have HDR switching on —
    /// which previously meant counting ticks.
    /// </summary>
    private Label? _gameCount;

    private const int HomePage = 0;
    private const int DisplayPage = 1;
    private const int HdrPage = 2;
    private const int ControllerPage = 3;
    private const int HotkeysPage = 4;
    private const int PerformancePage = 5;
    private const int GeneralPage = 6;
    private const int AboutPage = 7;

    /// <summary>The width each page was last built at, so a stale one can be spotted.</summary>
    private int[] _pageWidth = [];

    private void BuildPages()
    {
        string[] names = Words.NavItems;
        var builders = Builders;

        _pageWidth = new int[names.Length];

        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            var item = new NavItem
            {
                Text = names[i],
                Glyph = i < NavIcons.Length ? NavIcons[i] : NavGlyph.None,
                Margin = new Padding(0, 0, 0, 4),
            };
            item.Click += (_, _) => ShowPage(index);

            var page = builders[i]();
            page.Visible = false;
            _pageHost.Controls.Add(page);

            _pageWidth[i] = _contentWidth;
            _pages.Add((item, page));
            _nav.Controls.Add(item);
        }
    }

    /// <summary>
    /// Rebuild one page at the current width.
    ///
    /// One rather than all five, because this runs while the window is being dragged. Rebuilding
    /// everything took long enough to be visible, which is why it used to wait for the drag to
    /// finish; rebuilding only what is on screen is fast enough to keep up, and the other pages
    /// are marked stale and rebuilt the moment they are opened.
    /// </summary>
    private void RebuildPage(int index)
    {
        if (index < 0 || index >= _pages.Count) return;

        // Every rebuild under one freeze. A page is torn down and built again control by control, and
        // each of those paints on its own — which the eye reads as the page flickering rather than as
        // it being replaced.
        Frozen(() => RebuildPageCore(index));
    }

    private void RebuildPageCore(int index)
    {
        var (nav, old) = _pages[index];

        // Lift the settings controls clear before the old page is destroyed with its children.
        foreach (var keep in Persistent())
            if (keep.Parent is not null && IsInside(old, keep)) keep.Parent.Controls.Remove(keep);

        bool wasVisible = old.Visible;

        _pageHost.SuspendLayout();

        try
        {
            _pageHost.Controls.Remove(old);

            // Hand the tooltips back before the controls holding them are destroyed.
            //
            // A ToolTip keeps its own table of control-to-text, and nothing about disposing a control
            // takes its entry out of that table. Every rebuild therefore added a fresh set and
            // abandoned the last. Rebuilds are not rare either — every resize that is not a drag,
            // every return to a page left stale by one, and every change to the Home page's alerts —
            // so the table grew for as long as the window stayed open.
            //
            // Runs after the persistent controls have been lifted clear above, so their tooltips
            // survive; anything still inside the old page is on its way out with it.
            ForgetToolTips(old);

            old.Dispose();

            var page = Builders[index]();
            page.Visible = wasVisible;

            _pageHost.Controls.Add(page);
            _pages[index] = (nav, page);
            _pageWidth[index] = _contentWidth;

            // Cards size themselves from their rows once a layout pass runs. Forcing one here
            // is not safe: the page is a ScrollHost, whose Reflow guards against re-entry, and
            // a pass driven from inside a rebuild hits that guard and leaves the content
            // unpositioned — which blanked the page entirely. RefreshVisiblePage does it from
            // outside, after the rebuild has finished.
            if (IsHandleCreated) BeginInvoke(RefreshVisiblePage);

            // Only the page just built, rather than the whole form. Walking every control on
            // the window was pure waste on a path that now runs during a drag.
            WireDirtyTracking(page);

            // Restores the values into the freshly built rows. Cheap — it reads fields, and
            // _loading suppresses the dirty marking it would otherwise cause.
            LoadFromConfig();
            UpdateDependentStates();

            if (index == HdrPage) RebuildGameList(resort: !_rebuilding);
        }
        finally
        {
            _pageHost.ResumeLayout(true);
        }
    }

    private static bool IsInside(Control container, Control child)
    {
        for (var c = child.Parent; c is not null; c = c.Parent)
            if (ReferenceEquals(c, container)) return true;

        return false;
    }

    /// <summary>
    /// Adopt a new content width, rebuilding what is on screen immediately.
    ///
    /// The pages the user cannot see are left stale and rebuilt when they are opened, which is
    /// what makes this quick enough to run during a drag rather than after it.
    /// </summary>
    private void RebuildAt(int contentWidth)
    {
        if (contentWidth == _contentWidth || _pages.Count == 0) return;

        _contentWidth = contentWidth;
        RebuildPage(_currentPage);
    }

    /// <summary>
    /// Clear the tooltip entries for a control and everything under it.
    ///
    /// Setting the text to null is how a ToolTip is told to forget a control; there is no Remove.
    /// Deliberately not RemoveAll, which would also wipe the footer and navigation tooltips that are
    /// set once when the window is built and never set again.
    /// </summary>
    private void ForgetToolTips(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            _tips.SetToolTip(child, null);
            if (child.HasChildren) ForgetToolTips(child);
        }
    }

    /// <summary>Controls that outlive a rebuild: everything holding a setting.</summary>
    private IEnumerable<Control> Persistent()
    {
        foreach (var field in GetType().GetFields(System.Reflection.BindingFlags.Instance
                                                | System.Reflection.BindingFlags.NonPublic))
        {
            // Every control held in a field and reused across rebuilds has to be listed here.
            //
            // This is a list of types rather than "any Control field", and that is a trap worth
            // naming: a new kind of settings control compiles, lays out and works, right up
            // until the first window resize disposes it along with the page it was sitting in.
            // The next rebuild then hands out an object that has already been disposed, and the
            // app falls over with ObjectDisposedException naming a control nobody touched.
            //
            // "is ToggleSwitch or Dropdown" alone, then cast — writing it as one pattern with
            // a capture binds as "ToggleSwitch or (Dropdown and Control c)", which leaves the
            // capture unassigned down the first branch and will not compile.
            if (field.GetValue(this) is Control control and (ToggleSwitch or Dropdown or ShortcutBox or Slider))
                yield return control;
        }
    }

    /// <summary>"Couch Session — Display", and so on, in the title strip.</summary>
    private TitleText? _pageTitle;

    /// <summary>
    /// Every warning and error of this run, under the row that counts them.
    ///
    /// Newest first, and one row per distinct warning. Ordered by when each was last seen rather than
    /// by when it first appeared, so something still happening stays at the top where it belongs.
    /// Anything already dismissed is judged by that same last-seen number: a repeat after a dismissal
    /// is news again, which is the whole point of dismissing by count rather than by a flag.
    ///
    /// Indented with an elbow, the same bracket every dependent row on every other page uses, so these
    /// read as belonging to the line above rather than as more alerts in their own right.
    /// </summary>
    private void AddTroubleList(Card card)
    {
        foreach (var note in Log.RecentTrouble.OrderByDescending(n => n.Seq))
        {
            if (note.Seq <= _troubleSeen) continue;

            string when = note.When.ToString("HH:mm:ss");

            AddRow(card, new AlertRow
            {
                Text = note.Message,
                Detail = note.Count > 1 ? $"{when}  ·  {note.Count} times" : when,
                Kind = note.Level == "ERROR" ? AlertKind.Problem : AlertKind.Notice,
                Width = RowWidth - Indent,
            }, Indent);
        }
    }

    /// <summary>The at-a-glance tiles, rebuilt from settings rather than written once.</summary>
    private FlowLayoutPanel? _homeGlance;

    /// <summary>The glance tiles in the order they were made, apart from the group headings.</summary>
    private readonly List<GlanceTile> _glanceTiles = [];

    /// <summary>
    /// Everything the home page reports, in the order it matters from a sofa.
    ///
    /// Re-read rather than written once, because every one of these lives on another page: turning Auto
    /// HDR on and coming back here has to say "on". That was a real bug in the controller line before it
    /// was put on the same timer.
    /// </summary>
    private void RefreshHomeGlance()
    {
        if (_homeGlance is null || _homeGlance.IsDisposed) return;

        // Rebuilt only the first time. After that the tiles are updated in place.
        //
        // Disposing eight controls and making eight more, every two and a half seconds, is what made the
        // card flicker: every rebuild is a fresh layout pass and a full repaint of the area, whether or
        // not a single value changed. Now the values are written into the existing tiles and only a tile
        // whose text actually differs is invalidated, so a page nobody is interacting with sits still.
        bool building = _homeGlance.Controls.Count == 0;
        int index = 0;

        if (building) _glanceTiles.Clear();

        // Four across, and the arithmetic matters.
        //
        // They were stacking one per row, which is why the page was a mile long: a tile of
        // (RowWidth - 16) / 2 plus a 16px right margin comes to RowWidth + 16 for a pair, so the second
        // one wrapped every time. The width has to leave room for its own margin — hence the gap being
        // subtracted per column rather than once.
        const int Columns = 4;
        const int Gap = 12;

        int tileWidth = (RowWidth - Gap * Columns) / Columns;

        // Read from the controls, not from Config.
        //
        // Config is what was last *saved*. Every other page shows the live control, so a toggle changed
        // and not yet written left this page reporting the old value with total confidence — which is
        // exactly the complaint, and worse than showing nothing. These are the same controls the save
        // routine reads, so the tiles and the pages can no longer disagree.
        //
        // The display picker is the one exception, and deliberately: nothing selected does not mean "no
        // display", it usually means the TV is switched off and its entry has vanished from the list, so
        // the saved value is the truthful answer there.
        var display = (_tvDisplay.SelectedItem as DisplayInfo)?.FriendlyName
                   ?? DisplayName(Config.TvDisplayPath)
                   ?? (Config.TvDisplayName is { Length: > 0 } saved ? saved + " (not detected)" : null);

        // Not there is not the same as not set.
        //
        // The picker adds "— disconnected" to a display it remembers but cannot currently see, and the
        // saved fallback says "(not detected)". Either way the name is worth showing — it is still the
        // screen you chose — but showing it in full strength claims a television is ready when it is
        // switched off, so it reads dimmed like every other thing that is configured but not doing
        // anything right now.
        bool displayLive = display is not null
                        && !display.Contains(Words.DisplayDisconnected, StringComparison.Ordinal)
                        && !display.Contains("(not detected)", StringComparison.Ordinal);

        // SafePadNames still called, though no tile shows it any more: the controller line at the top
        // of the Controller page is built from the same read, and dropping it here would only move
        // the work rather than remove it.
        var pads = SafePadNames();

        bool audio = _switchAudio.Checked;
        bool autoHdr = HdrModeOf(_hdrMode) != HdrMode.Off;
        bool guide = _guideEndsSession.Checked;
        bool tvOff = _turnOffTvOnExit.Checked;
        bool onBoot = _startWithWindows.Checked;

        int padCombo = _shortcutController.Value;
        int keyCombo = _shortcutKeyboard.Value;

        // Named devices, not "the TV" and "your speakers".
        //
        // "Sound: moves to the TV" is true of every possible configuration and therefore says nothing —
        // the question anyone actually has is *which output*, and that is the one that goes wrong. Same
        // for the hotkey: the pad combination and the keyboard one are separate settings and each can be
        // unset on its own, so they get a line each rather than being run together.
        var tvDevice = _tvAudio.SelectedItem as AudioDeviceInfo;
        var deskDevice = _desktopAudio.SelectedItem as AudioDeviceInfo;

        var tvSound = tvDevice?.ToString();
        var deskSound = deskDevice?.ToString();

        // Chosen, switched on, and actually there are three different things.
        //
        // A television that is off takes its audio endpoint with it — the log says so every session:
        // "the TV audio device never appeared, so audio was left unchanged". Naming it at full strength
        // while it is not there claims sound will move when it will not, so it dims the same way the
        // display above it does. Switching the feature off dims it too, for the plainer reason.
        bool tvSoundLive = audio && tvDevice is { IsActive: true };
        bool deskSoundLive = deskDevice is { IsActive: true };

        // Display and sound, HDR, and the controller. Nothing else.
        //
        // This grid used to report every setting in the app — thirty-odd tiles, most of them about
        // Windows tuning or notifications, none of which decides whether a session works. The three
        // subjects left are the ones that do: which screen and speakers a session moves to, when HDR
        // switches, and what the controller starts and ends. Everything dropped is still one click
        // away on its own page, and this page is better at its job for not restating it.
        //
        Group("Display and sound");
        Tile("Couch display", display ?? "not chosen", displayLive, page: DisplayPage, anchor: _tvDisplay);
        Tile("Resolution", _tvVideoMode.SelectedItem?.ToString() ?? "whatever it is", true, page: DisplayPage,
             anchor: _tvVideoMode);
        Tile("Displays in session", DisplayModeText(), true, page: DisplayPage, anchor: _displayMode);
        Tile("TV on exit", tvOff ? "disconnects" : "stays connected", true, page: DisplayPage,
             anchor: _turnOffTvOnExit);

        Tile("Sound in session", audio ? tvSound ?? "TV (not detected)" : "unchanged", tvSoundLive,
             page: DisplayPage, anchor: _tvAudio);
        Tile("Sound at the desk", deskSound ?? "unchanged", deskSoundLive, page: DisplayPage,
             anchor: _desktopAudio);
        Tile("Game left running", _muteOnDesktop.Checked ? "muted at the desk" : "keeps playing",
             _muteOnDesktop.Checked, page: DisplayPage, anchor: _muteOnDesktop);

        Group("HDR");
        // One tile, because it is now one setting. Two tiles for two switches that cancelled each
        // other meant the pair could read "on · 12 games" and "whole session: yes" at once, which
        // described a state the app has never actually been in.
        Tile("HDR Switching", HdrModeOf(_hdrMode) switch
        {
            HdrMode.WholeSession => "whole session",
            HdrMode.PerGame => $"per game · {Config.HdrGames.Count} games",
            _ => "off",
        }, autoHdr, page: HdrPage, anchor: _hdrMode);
        // Whether turning HDR on by hand teaches the app anything.
        //
        // On this page because it is the setting that changes the HDR list behind your back — switch
        // HDR on during a game and that game is added to it. Somebody who does not know it is on has
        // no way to account for a list that keeps growing, and somebody who wants that behaviour has
        // no way to tell whether they have it. Neither question was answerable from this page.
        // Read through the same null guard the save routine uses. The field is declared with null!
        // but is genuinely null until the HDR page has been built, and the home page is built first
        // on a cold start — so the saved value is the honest answer until the control exists.
        bool hdrLearns = _hdrHotkeyRemember is not null
                             ? _hdrHotkeyRemember.Checked
                             : Config.HdrHotkeyRemembersGame;

        Tile("Smart HDR", hdrLearns ? "learns as you switch" : "list unchanged",
             hdrLearns, page: HdrPage, anchor: _hdrHotkeyRemember);

        Tile("HDR hotkey (pad)", _shortcutControllerHdr.Value != 0
                                     ? PadText(_shortcutControllerHdr.Value) : "not set",
             _shortcutControllerHdr.Value != 0, page: HotkeysPage, anchor: _shortcutControllerHdr,
             combo: _shortcutControllerHdr.Value);
        Tile("HDR hotkey (keys)", _shortcutKeyboardHdr.Value != 0
                                      ? Input.KeyShortcut.Describe(_shortcutKeyboardHdr.Value) : "not set",
             _shortcutKeyboardHdr.Value != 0, page: HotkeysPage, anchor: _shortcutKeyboardHdr);

        Group("Controller");
        //
        // "Controller — PlayStation DualSense" used to open this group and has gone. Every other tile
        // here reports a setting; that one reported live state, which under a heading reading
        // "Settings at a glance" is the one thing on the grid that is not one. The Controller page
        // already carries a status card saying the same thing, in the place somebody goes to act on
        // it.

        // Which pad, not just whether one is connected.
        //
        // Picking a specific controller is the quietest way to end up with a session that will not
        // start: the pad in your hand is connected, the trigger is switched on, and nothing happens
        // because the app is waiting for a different one. The tile above cannot say that — it reports
        // what is plugged in, not what is allowed to start anything.
        Tile("Controller that starts it", Config.TriggerControllerId.Length == 0
                                       ? "any controller"
                                       : Config.TriggerControllerName.Length > 0
                                             ? Config.TriggerControllerName
                                             : "a specific one",
             true, page: ControllerPage, anchor: _triggerPad);
        // The third thing that can quietly stop a session starting, alongside the two above. Set to
        // Bluetooth only, plug the controller in, and nothing happens with no clue why — which is the
        // argument written above the tile before it, and applies here word for word.
        Tile("Connection that counts", Config.StartOnControllerLink switch
        {
            PadLink.WiredOnly => "cable or dongle only",
            PadLink.WirelessOnly => "Bluetooth only",
            _ => "wired or wireless",
        }, Config.StartOnControllerLink == PadLink.Either, page: ControllerPage, anchor: _padLink);

        // Both halves of each hotkey, keyboard as well as pad.
        //
        // The keyboard ones were dropped when this grid was cut back to display, controller and HDR,
        // on the reasoning that a key combination is not a controller setting. True, and the wrong
        // cut to make: a hotkey is how a session starts and ends whichever device it lives on, and
        // "not set" against one of these is exactly the sort of thing somebody comes to this page to
        // find out. They are back, in pairs, so neither is the odd one missing.
        Tile("Session hotkey (pad)", padCombo != 0 ? PadText(padCombo) : "not set", padCombo != 0,
             page: HotkeysPage, anchor: _shortcutController, combo: padCombo);
        Tile("Session hotkey (keys)", keyCombo != 0 ? Input.KeyShortcut.Describe(keyCombo) : "not set",
             keyCombo != 0, page: HotkeysPage, anchor: _shortcutKeyboard);
        Tile("Guide button", guide ? "starts and ends sessions" : "off", guide, page: HotkeysPage,
             anchor: _guideEndsSession);
        Tile("Start on controller", _startOnController.Checked ? "yes" : "no", _startOnController.Checked,
             page: ControllerPage, anchor: _startOnController);
        // "on" was not the whole answer.
        //
        // With hold-to-activate set, the pointer does nothing until a button is held — and "my cursor
        // will not move" is the commonest thing to come back about. A tile that says "on" while the
        // pointer sits still is a tile that sends someone to look at the wrong page. Naming the button
        // here answers it without opening anything.
        //
        // The other state said "on, always" and now says "on". "Always" was there to mean "without
        // holding anything", which is a meaning nobody can take from the word unless they have
        // already seen the "hold R2" state and worked out what it is being contrasted with. It was
        // also a claim the app does not keep: the pointer stands down over a fullscreen game unless
        // that is allowed separately. "on" beside "hold R2" carries the contrast on its own.
        Tile("Controller as mouse", !_mouseControl.Checked ? "off"
                           : _mouseHold.Checked ? $"hold {HoldButtonName()}"
                                                : "on",
             _mouseControl.Checked, page: ControllerPage, anchor: _mouseControl);

        // The two disconnect answers, beside the controller settings they belong to rather than in
        // the startup group four tiles later. They were down there because the single tile they
        // replaced was about ending a session, and "starting and stopping" was where ending lived —
        // but this grid is read by scanning for a subject, and the subject here is the controller.
        Tile("Switching it off", Says(DisconnectOf(_onControllerOff, DisconnectAction.ComeBack)),
             DisconnectOf(_onControllerOff, DisconnectAction.ComeBack) != DisconnectAction.Ignore,
             page: ControllerPage, anchor: _onControllerOff);

        Tile("If it drops out", Says(DisconnectOf(_onControllerLost, DisconnectAction.Ignore)),
             DisconnectOf(_onControllerLost, DisconnectAction.Ignore) != DisconnectAction.Ignore,
             page: ControllerPage, anchor: _onControllerLost);

        // Short forms of the option labels, for a tile four to a row. Same words where they fit, so
        // the home page and the page it links to do not describe one setting two ways.
        static string Says(DisconnectAction action) => action switch
        {
            DisconnectAction.EndAndClose => "closes the game",
            DisconnectAction.ComeBackAndAsk => "switches back, then asks",
            DisconnectAction.ComeBack => "switches back to the desktop",
            _ => "nothing",
        };

        // Four from the other pages, and only four.
        //
        // This grid was cut back to the three subjects that decide whether a session works, and that
        // is still the right shape — but three settings elsewhere outlive a session and one decides
        // whether the app is running at all, which makes them worth a line here even though nothing
        // about them is about the television.
        //
        // The three Windows ones are the argument: everything else this app touches is put back when
        // a session ends, and these are changed and left changed. Two of them lower the machine's
        // security. Somebody skimming this page to see what the app has done to their PC should not
        // have to open the Performance page to find the part that outlives the session.
        //
        // Start with Windows earns its place differently: none of the rest of this grid means
        // anything if the app is not running when you sit down.
        Group("Windows and startup");

        Tile("Start with Windows", onBoot ? "yes" : "no", onBoot, page: GeneralPage,
             anchor: _startWithWindows);

        Tile("Admin prompts", _disableUac.Checked ? "silenced" : "normal", _disableUac.Checked,
             page: PerformancePage, anchor: _disableUac);

        Tile("Windows Firewall", _disableFirewall.Checked ? "switched off" : "on",
             _disableFirewall.Checked, page: PerformancePage, anchor: _disableFirewall);

        // "off" rather than "off while the app runs": it is a Windows setting now and stays however
        // it is left, so describing it in terms of this app's lifetime would be a plain untruth.
        Tile("Game Bar", _disableGameBarButton.Checked ? "off" : "on",
             !_disableGameBarButton.Checked, page: PerformancePage, anchor: _disableGameBarButton);

        // A heading inside the grid: full width, so it always starts its own row.
        //
        // Full width does two jobs. It reads as a heading rather than as another tile, and it
        // guarantees the wrap — a part-width heading would sit in whatever column the group before
        // it happened to end in, which is the one place a heading must never be.
        //
        // Drawn rather than set as a Label, because bright text alone was not enough separation.
        // Every caption on this grid is small dim text, so a heading in small text differing only in
        // shade is a heading you have to hunt for. Four things set it apart instead: a rule across
        // the full width above it, which *closes* the group before rather than merely labelling the
        // one after; accent ink, which nothing else on this card uses; a point more size and the
        // semibold weight; and air above the rule so the groups read as blocks with gaps between
        // them rather than as a continuous grid with labels sprinkled in.
        //
        // The first heading has no rule. There is nothing above it to close, and a line under the
        // card's own top edge is just a second border.
        void Group(string name)
        {
            if (!building) return;

            bool first = _homeGlance!.Controls.Count == 0;

            var head = new Panel
            {
                Size = new Size(RowWidth, first ? 22 : 36),
                BackColor = Color.Transparent,
                Margin = new Padding(0, first ? 0 : 14, 0, 9),
            };

            string caption = name.ToUpperInvariant();

            head.Paint += (_, e) =>
            {
                var g = e.Graphics;
                Theme.PaintBackdrop(g, head);

                if (!first)
                {
                    using var rule = new Pen(Theme.Hairline);
                    g.DrawLine(rule, 0, 0, head.Width, 0);
                }

                // Color carries this now, not just weight. In Theme.Text these headings were the
                // same ink as every value on the grid and only a point smaller than the captions, so
                // the eye had nothing to lock onto and the rule was doing the whole job on its own.
                // Accent ink appears nowhere else on this card, which is exactly what makes it read
                // as a different kind of line rather than as one more setting.
                TextRenderer.DrawText(g, caption, Theme.SmallBold,
                                      new Rectangle(0, first ? 0 : 15, head.Width, 20),
                                      Theme.Info,
                                      TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
                                    | TextFormatFlags.VerticalCenter);
            };

            _homeGlance!.Controls.Add(head);
        }

        // page:  where clicking this tile goes — the settings page that owns what it reports.
        // anchor: the control on that page holding this setting, so its row can be lit on arrival.
        void Tile(string caption, string value, bool set, int page, Control? anchor = null,
                  int combo = 0)
        {
            if (building)
            {
                var fresh = new GlanceTile
                {
                    Caption = caption,
                    Value = value,
                    Muted = !set,
                    Combo = combo,
                    Vendor = _shortcutController.Vendor,
                    Product = _shortcutController.Product,
                    Actionable = true,
                    Size = new Size(tileWidth, GlanceTile.PreferredHeight()),
                    Margin = new Padding(0, 0, Gap, 10),
                };

                // Every tile is a way in to the thing it reports. The page reads the same either way,
                // and it means the answer to "how do I change that" is always the same gesture.
                fresh.Click += (_, _) => { ShowPage(page); HighlightSetting(anchor); };

                _homeGlance!.Controls.Add(fresh);

                // Kept in a list of its own as well as in the panel.
                //
                // The update pass walks tiles by position and used to index straight into Controls,
                // which worked only while every child was a tile. The headings are children too, so
                // from the second group onwards every tile would have been given the value belonging
                // to a different one.
                _glanceTiles.Add(fresh);

                index++;
                return;
            }

            if (index < _glanceTiles.Count)
            {
                var tile = _glanceTiles[index];

                // Repainted only when something is different. Invalidating regardless is the same
                // flicker by another route.
                // The pad the glyphs are drawn for is re-read every time as well: picking up the other
                // controller has to redraw a combination in that pad's symbols, and the whole point of
                // this row is that it agrees with the controller currently in your hands.
                if (tile.Value != value || tile.Muted != !set || tile.Combo != combo
                 || tile.Vendor != _shortcutController.Vendor
                 || tile.Product != _shortcutController.Product
                 || tile.FamilyChanged())
                {
                    tile.Value = value;
                    tile.Muted = !set;
                    tile.Combo = combo;
                    tile.Vendor = _shortcutController.Vendor;
                    tile.Product = _shortcutController.Product;
                    tile.Invalidate();
                }
            }

            index++;
        }

        // The card's height, worked out from where the tiles actually landed.
        //
        // [BUG] The panel was left to size itself, and FlowLayoutPanel's own preferred-size maths
        // only holds up while every child is the same width. Give one tile two columns and it
        // overestimates how many rows it needs, so the card grew a third of its height in empty
        // space below the last row — the grid was correct and the box around it was not.
        //
        // Measured after a layout pass instead: the tiles are placed, the lowest edge is found, and
        // that is the height. Nothing to be wrong about, and it stays right whatever mix of widths
        // the grid ends up with.
        if (building)
        {
            _homeGlance.PerformLayout();

            int bottom = 0;

            foreach (Control tile in _homeGlance.Controls)
                bottom = Math.Max(bottom, tile.Bottom + tile.Margin.Bottom);

            if (bottom > 0)
            {
                _homeGlance.AutoSize = false;
                _homeGlance.MinimumSize = new Size(RowWidth, bottom);
                _homeGlance.MaximumSize = new Size(RowWidth, bottom);
                _homeGlance.Height = bottom;
            }
        }
    }

    /// <summary>The hold-to-activate button in the short form the glance tile has room for.</summary>
    private string HoldButtonName()
    {
        int at = _mouseHoldButton.SelectedIndex;
        if (at < 0 || at >= Words.MouseHoldOptions.Length) at = HoldButtonToIndex(Config.MouseHoldButton);

        // The picker's own labels carry an explanation in brackets — "R2 / RT  (right trigger)" — which
        // is right where it is chosen and too long for a tile. Everything up to the bracket is the name.
        string full = Words.MouseHoldOptions[Math.Clamp(at, 0, Words.MouseHoldOptions.Length - 1)];
        int bracket = full.IndexOf('(');

        return (bracket > 0 ? full[..bracket] : full).Trim();
    }

    /// <summary>A controller combination as words, drawn from the box that currently holds it.</summary>
    private string PadText(int combo) =>
        Input.PadShortcut.Describe(combo, _shortcutController.Vendor, _shortcutController.Product);

    /// <summary>What the display picker's mode means in a sentence.</summary>
    private string DisplayModeText() =>
        _displayMode.SelectedIndex == (int)TvDisplayMode.TvOnly ? "TV only" : "TV and your monitors";

    /// <summary>What Steam is told to do once Big Picture closes.</summary>
    private string SteamAfterText()
    {
        int i = _steamAfter.SelectedIndex;
        return i >= 0 && i < Words.SteamAfterOptions.Length ? Words.SteamAfterOptions[i] : "unchanged";
    }

    /// <summary>The alert set as one string, so a change can be spotted without rebuilding to find out.</summary>
    private string _alertSignature = "";

    /// <summary>
    /// Whether the alerts differ from the ones on screen.
    ///
    /// Compared as text because that is exactly what the reader sees: two runs that produce the same
    /// words need no rebuild, and rebuilding a page while someone is reading it is worse than being a
    /// couple of seconds late with a warning.
    /// </summary>
    private bool AlertsChanged()
    {
        try
        {
            var now = string.Join("|", Alerts().Select(a => $"{a.Kind}{a.Message}{a.Detail}"));
            if (now == _alertSignature) return false;

            _alertSignature = now;
            return true;
        }
        catch { return false; }
    }

    /// <param name="Page">Where clicking goes, or -1 when there is nowhere useful.</param>
    /// <param name="Expandable">Clicking opens the list underneath instead of going to a page.</param>
    private readonly record struct Alert(string Message, string Detail, AlertKind Kind, int Page,
                                         bool Expandable = false);

    /// <summary>Whether the warnings row is currently showing everything under it.</summary>
    private bool _troubleOpen;

    /// <summary>
    /// How many warnings had been logged when the row was last dismissed.
    ///
    /// A count rather than a flag, so dismissing means "I have read these" instead of "never mention
    /// warnings again": the row comes straight back the moment something new is logged, and says how
    /// many of them are new. Not saved to disk — it describes this run, and so does the count itself.
    /// </summary>
    private int _troubleSeen;

    /// <summary>
    /// Everything true of the machine right now that the settings pages cannot tell you.
    ///
    /// Deliberately only things that are *checked*, never things that are merely configured — a setting
    /// saying "start with Windows: yes" is on a tile below; this asks Windows whether the task actually
    /// exists. That difference is the whole point: every hard-to-find problem in this app has been a
    /// setting that stopped being true without saying so.
    ///
    /// Ordered worst first, and empty on a healthy idle machine.
    /// </summary>
    private List<Alert> Alerts()
    {
        var list = new List<Alert>();

        // ── things that are wrong ──

        // The chosen display, asked of Windows rather than of the config.
        if (Config.TvDisplayPath is { Length: > 0 } && DisplayName(Config.TvDisplayPath) is null)
            list.Add(new("Couch display not detected",
                         Config.TvDisplayName is { Length: > 0 } n ? $"{n} — is the TV switched on?"
                                                                   : "is the TV switched on?",
                         AlertKind.Problem, DisplayPage));

        // The one that bit us: the setting says yes, the scheduled task is gone.
        try
        {
            if (_startWithWindows.Checked && !StartupRegistration.IsEnabled())
                list.Add(new("Start with Windows is not registered",
                             "the setting is on but the task is missing — save settings to repair",
                             AlertKind.Problem, GeneralPage));
        }
        catch { /* asking is best effort */ }

        if (!BigPictureLauncher.IsSteamInstalled())
            list.Add(new("Steam was not found", "a session opens Big Picture, so Steam is required",
                         AlertKind.Problem, GeneralPage));

        if (_shortcutController.Value == 0 && _shortcutKeyboard.Value == 0 && !_guideEndsSession.Checked)
            list.Add(new("No way to start a session from the couch",
                         "set a hotkey, or turn the Guide button on", AlertKind.Problem, HotkeysPage));

        // ── what is happening ──

        if (SessionNow)
        {
            var game = GameNow();

            list.Add(game is not null
                ? new("Session running", game, AlertKind.Good, -1)
                : new("Session running", "Big Picture, no game yet", AlertKind.Good, -1));
        }

        // No alert row for an available update any more.
        //
        // It said "see About to install it", which is three clicks and a page nobody opens. The offer
        // is now a card at the top of this page with the button on it, so hearing about an update and
        // taking it are the same gesture.

        // ── anything the log grumbled about ──
        // Counted the same way the list is built, so the number and the rows agree.
        //
        // It used to count logged events, which after de-duplication no longer matched what opening it
        // showed: "7 warnings" over four rows invites the reader to go looking for the other three.
        int fresh = Log.RecentTrouble.Count(n => n.Seq > _troubleSeen);

        if (fresh > 0)
        {
            // "since the app started" only while none have been dismissed. After that the honest
            // description is "new", because that is the question the row is answering.
            string what = _troubleSeen == 0
                          ? fresh == 1 ? "1 warning since the app started"
                                       : $"{fresh} warnings since the app started"
                          : fresh == 1 ? "1 new warning" : $"{fresh} new warnings";

            list.Add(new(what, _troubleOpen ? "" : Log.LastTrouble ?? "",
                         AlertKind.Notice, -1, Expandable: true));
        }

        return list;
    }

    /// <summary>The running game's name, or null. Best effort — this is decoration, not a decision.</summary>
    private static string? GameNow()
    {
        try
        {
            int appId = BigPictureLauncher.RunningAppId();
            if (appId == 0) return null;

            return GameLibrary.SteamGameByAppId(appId)?.Name;
        }
        catch { return null; }
    }

    /// <summary>The newer version found by the update check, or null. Set by the About page's check.</summary>
    /// <summary>
    /// A newer release the tray's launch check already found, if there is one.
    ///
    /// Set by TrayApp — either when the window is constructed, or later if the check lands while it is
    /// open. Setting it rebuilds the Home page so the offer appears without anyone doing anything.
    /// </summary>
    public UpdateInfo? PendingUpdate
    {
        get => _pendingUpdate;
        set
        {
            if (value is null || _pendingUpdate?.Version == value.Version) return;

            _pendingUpdate = value;
            _update = value.Version;

            // Only if Home is the page on screen; anywhere else it is built fresh on arrival anyway.
            if (_currentPage == HomePage) BeginInvoke(() => RebuildPage(HomePage));
        }
    }

    private UpdateInfo? _pendingUpdate;

    private string? _update;

    /// <summary>Connected pads by name, or null when there are none.</summary>
    private static string? SafePadNames()
    {
        try
        {
            var pads = ControllerWatcher.Attached();
            return pads.Count == 0 ? null : string.Join(", ", pads.Select(p => p.Name));
        }
        catch { return null; }
    }

    private static int SafePadCount()
    {
        try { return ControllerWatcher.Attached().Count; }
        catch { return 0; }
    }

    /// <summary>The chosen couch display's friendly name, or null when it is not set or not present.</summary>
    private static string? DisplayName(string devicePath)
    {
        if (devicePath.Length == 0) return null;

        try
        {
            foreach (var d in DisplayManager.ListDisplays())
                if (d.DevicePath == devicePath) return d.FriendlyName;
        }
        catch { /* fall through */ }

        return null;
    }

    private string HotkeySummary()
    {
        var parts = new List<string>();

        if (Config.ShortcutController != 0)
            parts.Add(PadShortcut.Describe(Config.ShortcutController,
                                           (ushort)Config.ShortcutControllerVendor,
                                           (ushort)Config.ShortcutControllerProduct));

        if (Config.ShortcutKeyboard != 0) parts.Add(KeyShortcut.Describe(Config.ShortcutKeyboard));

        return parts.Count == 0 ? "not set" : string.Join("  ·  ", parts);
    }

    private Card? _litCard;

    /// <summary>When this window opened, for the settling window below.</summary>
    private readonly DateTime _openedAt = DateTime.UtcNow;

    /// <summary>How long the home page is left alone while the machine is being enumerated.</summary>
    private static readonly TimeSpan SettleFor = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The one row a control belongs to.
    ///
    /// Every card holds its settings in a stack named "rows", so the row is whichever ancestor that
    /// stack holds directly — a toggle is three or four levels below it, inside a table for the two
    /// columns and a stack for the wrapped description. Walking all the way up to the Card instead was
    /// the bug that lit an entire page of settings: the card's direct child is the stack, not a row.
    /// </summary>
    private static Control? RowContainer(Control control)
    {
        var here = control;

        while (here.Parent is not null)
        {
            if (here.Parent is FlowLayoutPanel { Name: "rows" }) return here;
            here = here.Parent;
        }

        return null;
    }

    /// <summary>
    /// The band a row's mark or hover is drawn in, in the card's own coordinates.
    ///
    /// The row sits inside the "rows" stack, so its position has to be walked up through that stack
    /// to mean anything to the card that paints it. Inflated a little so the band reads as sitting
    /// behind the row rather than as a box drawn tight around the words.
    /// </summary>
    private static Rectangle RowBand(Control container, int lead = 0)
    {
        if (container.Parent is null) return Rectangle.Empty;

        var area = new Rectangle(container.Left + container.Parent.Left,
                                 container.Top + container.Parent.Top,
                                 container.Width, container.Height);
        area.Inflate(6, 6);

        // Extra room on the leading edge, for a band that draws a bar there.
        //
        // Six pixels is enough air for a plain wash and not enough to put anything in: a four-pixel
        // bar inside it lands hard against the first letter of the title, which is what the marker
        // did. The bar needs a lane of its own rather than a share of the margin.
        if (lead > 0)
        {
            area.X -= lead;
            area.Width += lead;
        }

        return area;
    }

    /// <summary>Extra left margin on the jump marker, so its bar clears the text.</summary>
    private const int MarkLead = 8;

    /// <summary>
    /// Make a whole settings row act as its own switch.
    ///
    /// The switch is 44 by 24 in a row up to 880 wide and 80 tall — roughly a thousand live pixels
    /// against sixty thousand dead ones, on the most repeated interaction in the app, aimed with a
    /// stick from across a room. Every miss lands on the card and does nothing, so the user re-aims
    /// and tries again. Handing the whole row the click removes that, and costs a desk user nothing.
    ///
    /// Acts on the click rather than on mouse-down, unlike the switch itself. A target this large is
    /// easy to press on the way to somewhere else, and releasing away from the row is the ordinary
    /// way out of a press you did not mean — which a mouse-down row would take from you.
    /// </summary>
    private void RowActsAsToggle(Card card, Control row, ToggleSwitch toggle)
    {
        Wire(row);

        void Wire(Control control)
        {
            foreach (Control child in control.Controls)
            {
                // Anything the user can operate keeps its own meaning. A row-wide click must never
                // reach past itself and take a press that belonged to a control sitting inside it.
                if (child is ToggleSwitch or Dropdown or Slider or InlineCheck
                          or FlatButton or ShortcutBox or ListBox) continue;

                Wire(child);
            }

            control.Click += (_, _) => { if (toggle.Enabled) toggle.Checked = !toggle.Checked; };
            control.MouseEnter += (_, _) => ShowRowHover(card, toggle);
            control.MouseLeave += (_, _) => ClearRowHover(card, toggle);
        }
    }

    private void ShowRowHover(Card card, Control anchor)
    {
        if (!anchor.Enabled || card.IsDisposed) return;
        if (RowContainer(anchor) is not { } container) return;

        card.Hover = RowBand(container);
    }

    private void ClearRowHover(Card card, Control anchor)
    {
        if (card.IsDisposed) return;
        if (RowContainer(anchor) is not { } container || container.IsDisposed) return;

        // Moving onto a child raises the parent's leave, and the pointer has not left the row at all.
        // Without this the band would blink off and on again every time it crossed a word.
        if (container.RectangleToScreen(container.ClientRectangle).Contains(Cursor.Position)) return;

        if (card.Hover == RowBand(container)) card.Hover = Rectangle.Empty;
    }

    /// <summary>Put the last highlighted row back to normal.</summary>
    private void ClearHighlight()
    {
        if (_litCard is { IsDisposed: false }) _litCard.Highlight = Rectangle.Empty;
        _litCard = null;
    }

    /// <summary>
    /// Mark the setting that was jumped to, and leave it marked.
    ///
    /// It stays until the next time a page is opened rather than fading on a timer. A highlight that
    /// times out is a race against reading speed, and losing that race on a page of near-identical
    /// switches puts you back to hunting for the row you were sent to — which is the thing this exists
    /// to prevent. Navigating anywhere clears it, so nothing is left marked once it has been used.
    /// </summary>
    private void HighlightSetting(Control? anchor)
    {
        ClearHighlight();

        if (anchor is null) return;

        var row = RowContainer(anchor);
        if (row?.Parent?.Parent is not Card card) return;

        _litCard = card;
        card.Highlight = RowBand(row, MarkLead);

        // Onto the screen: a highlight below the fold is no highlight at all.
        //
        // This used to ask _pageHost, which is a plain Panel with AutoScroll off — and
        // ScrollControlIntoView does nothing at all in that state. So every one of the twenty-one
        // Home tiles opened the right page, marked the right row, and left it wherever it happened
        // to be, usually below the fold. The mark was real; the scroll never happened once.
        //
        // Posted rather than called. ShowPage may have just rebuilt this page, and a rebuilt page has
        // not been laid out yet — RefreshVisiblePage does that from outside, on this same queue, and
        // asking a row where it is before then measures a stack nothing has positioned.
        if (PageOf(row) is not { } host) return;

        if (IsHandleCreated) BeginInvoke(() => { if (!host.IsDisposed && !row.IsDisposed) host.ScrollIntoView(row); });
        else host.ScrollIntoView(row);
    }

    /// <summary>
    /// Copy the pointer settings into the config and hand them straight to the running pointer.
    ///
    /// Written to Config rather than to a copy because the debounced save writes exactly these
    /// fields moments later anyway, so a second source of truth would only be something to keep in
    /// step. Nothing is written to disk here — that is still the save's job.
    /// </summary>
    private void PointerChanged()
    {
        if (_loading) return;

        Config.MouseControlEnabled = _mouseControl.Checked;
        Config.MouseInGames = _mouseInGames.Checked;
        Config.MouseHoldToActivate = _mouseHold.Checked;
        Config.MouseHoldButton = IndexToHoldButton(Math.Max(0, _mouseHoldButton.SelectedIndex));
        Config.MouseSpeed = _mouseSpeed.Value;
        Config.MouseDeadzone = _mouseDeadzone.Value;
        Config.MouseUseTrackpad = _useTrackpad.Checked;
        Config.MouseCursorStick = Math.Max(0, _cursorStick.SelectedIndex);

        PointerPreview?.Invoke(Config);
    }

    /// <summary>The scrolling page a control sits on, or null when it is not on one.</summary>
    private static ScrollHost? PageOf(Control control)
    {
        for (var here = control; here is not null; here = here.Parent)
            if (here is ScrollHost host) return host;

        return null;
    }

    private void ShowPage(int index)
    {
        // Whatever was marked by the last jump is done with the moment another page is opened.
        ClearHighlight();

        _currentPage = index;

        if (_pageTitle is not null && index >= 0 && index < _pages.Count)
            _pageTitle.Page = _pages[index].Nav.Text;

        // The whole switch under one freeze: a rebuild if the page is stale, then hiding one
        // page and showing another. Each of those paints on its own otherwise, and the eye
        // reads the sequence as a flash rather than as a tab change.
        Frozen(() =>
        {
            if (index >= 0 && index < _pageWidth.Length && _pageWidth[index] != _contentWidth)
                RebuildPage(index);

            for (int i = 0; i < _pages.Count; i++)
            {
                _pages[i].Nav.Active = i == index;
                _pages[i].Nav.Invalidate();
                _pages[i].Page.Visible = i == index;
            }

            // Opening a page starts it at its heading. Same reason as above — the old call could
            // not scroll anything — but here the fix is simply to rewind, so a page never opens
            // part-way down because that is where it was left last time.
            if (_pages[index].Page is ScrollHost host) host.Offset = 0;
        });

        UpdatePadSuspension();
    }

    private Control BuildFooter()
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = FooterHeight, BackColor = Theme.Rail };

        // Everything in this bar is centred against FooterHeight rather than sitting at a Y someone
        // measured once. The two rows had drifted apart already — the left pair at 17 and 18 for the
        // same 32-pixel height — and a bar that changes height cannot carry hard-coded offsets.
        const int SmallButton = 32, BigButton = 38;

        int smallTop = (FooterHeight - SmallButton) / 2;
        int bigTop = (FooterHeight - BigButton) / 2;

        var donate = new RoundedButton
        {
            Text = Words.ButtonSupport,
            Size = new Size(186, SmallButton),
            Location = new Point(20, smallTop),
            Font = Theme.BodySemi,
            Image = AppIcon.Heart(16, Color.White),
            IconGap = 8,
            CornerRadius = 8,
            NormalColor = Theme.Donate,
            HoverColor = Theme.DonateHi,
            PressedColor = Theme.Mix(Theme.DonateHi, Color.Black, 0.15f),
        };
        donate.Click += (_, _) => OpenDonatePage();
        _tips.SetToolTip(donate, Wrapped(Words.TipDonate));

        // In the footer for as long as this is a beta.
        //
        // On the About page it was three clicks from wherever something went wrong, which is two
        // too many for the one action that makes a bug report useful. Beside the support button it
        // is on screen from every page — and the pairing is honest about what is being asked for
        // at this stage.
        var bug = new FlatButton
        {
            Text = Words.ReportBug,
            Size = new Size(142, SmallButton),
            Location = new Point(donate.Right + 10, smallTop),
            Icon = AppIcon.Bug(16, Theme.Text),
            IconGap = 8,
        };

        bug.Click += (_, _) => ReportBug();
        _tips.SetToolTip(bug, Wrapped(Words.TipReportBug));

        // Reset sits where Close used to.
        //
        // Close was redundant: the title bar X does the same thing, and settings save
        // themselves, so the button confirmed nothing and dismissed nothing. Reset is the one
        // action in this window with no other route to it.
        // The primary button's control is bigger than the button drawn in it.
        //
        // FlatButton.Emphasis lays a glow under the pill, a Control cannot paint outside its own
        // bounds, so the glow needs FlatButton.Bleed of transparent room on every side.
        //
        // [BUG] That room was first taken back out with a negative Margin, on the reasoning that the
        // flow panel would simply add it. It does not — FlowLayoutPanel clamps a negative margin to
        // zero — so nothing was subtracted and the pill landed nine pixels below and left of where
        // the old rectangular button had been, out of line with Reset beside it and with its glow
        // sliced off by the bottom of the window.
        //
        // The room is now made instead of borrowed: the panel starts a bleed higher, and Reset gets
        // a matching top margin to put it back on the same line. Both written against the constant,
        // so changing the bleed cannot knock the footer out of alignment again.
        var reset = new FlatButton
        {
            Text = Words.ButtonReset,
            Size = new Size(150, BigButton),

            // Top margin puts it back on the primary button's line — see the note below.
            //
            // The right margin makes the gap to its neighbour match the one between the two pills,
            // which cannot be closer than two bleeds. Written as the constant rather than as the
            // number it works out to, so all three gaps stay equal if the bleed ever changes.
            Margin = new Padding(0, FlatButton.Bleed, FlatButton.Bleed, 3),
        };

        reset.Click += (_, _) => ResetEverything();
        _tips.SetToolTip(reset, Wrapped(Words.TipReset));

        // The one button this window exists for, so the one button drawn as a primary action.
        _action = new FlatButton
        {
            Emphasis = true,
            IconGap = 10,
            Size = new Size(186 + FlatButton.Bleed * 2, BigButton + FlatButton.Bleed * 2),

            // No margin of its own: the bleed already holds it clear of Reset, and adding to that
            // would open a gap wider than the one between any other pair of buttons in the window.
            Margin = new Padding(0),
        };

        _action.Click += (_, _) => OnActionButton();

        // Created here and hidden, so the flow panel already knows its size when the state that
        // wants it arrives. Same height as Reset, and quiet rather than red: it is the smaller of
        // the two things offered, and a second coloured button beside the primary one would make
        // the pair read as a choice of equals.
        // Styled as a primary action in red, beside the green one.
        //
        // Two emphasised buttons in a row is normally the wrong answer — two competing primaries is
        // the same as none — but this pair is the exception the rule is about. They are the two
        // halves of one decision, asked at the same moment about the same game, and giving one of
        // them the quiet treatment would say "this is the minor option" about an action that closes
        // the thing you were playing. Red carries the weight instead, exactly as it does on End
        // Couch Session, which is the same shape of action.
        _closeGame = new FlatButton
        {
            Emphasis = true,
            IconGap = 10,
            Text = Words.ButtonCloseGame,
            Size = new Size(152 + FlatButton.Bleed * 2, BigButton + FlatButton.Bleed * 2),
            Fill = Theme.Bad,
            ForeColor = Color.White,
            Line = Color.Empty,
            Icon = AppIcon.Stop(15, Color.White),
            Margin = new Padding(0),
            Visible = false,
        };

        // One bitmap, made once and let go once. The primary button replaces its icon as the session
        // state moves and disposes the old one for the same reason; this one never changes, so the
        // window closing is the only moment it can be released.
        FormClosed += (_, _) =>
        {
            var mark = _closeGame?.Icon;
            if (_closeGame is not null) _closeGame.Icon = null;
            mark?.Dispose();
        };

        _closeGame.Click += (_, _) => OnCloseGameButton();
        _tips.SetToolTip(_closeGame, Wrapped(Words.TipCloseGame));

        RefreshActionButton();

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,

            // A starting value only. The real width is worked out from what is actually in the row
            // — see SizeFooterButtons, and the bug it was written for.
            Width = 420,
            BackColor = Color.Transparent,

            // Right inset reduced by the bleed, because the outermost control is now the primary
            // button and nine pixels of it are transparent. Without this the pill would sit further
            // from the window edge than every other button in the footer.
            Padding = new Padding(0, bigTop - FlatButton.Bleed, 20 - FlatButton.Bleed, 0),
        };

        // Start on the outside, Reset inboard of it. The flow is right-to-left, so the first control
        // added is the rightmost.
        //
        // The primary action belongs at the end of the row: it is where the eye finishes and where
        // the hand already is, and it puts the most-used button furthest from Reset, which is the one
        // in this pair worth never hitting by accident.
        right.Controls.AddRange([_action, _closeGame, reset]);

        _footerButtons = right;
        SizeFooterButtons();

        // Settings save themselves, so there is nothing to press. This only confirms it
        // happened — an interface that saves silently and says nothing invites the user to
        // wonder whether it did.
        _saveNote = new Label
        {
            Text = "",
            Font = Theme.Small,

            // Green: a change landing is the one thing in this window worth noticing at a
            // glance, and grey made it read as disabled text rather than confirmation.
            ForeColor = Theme.Good,
            AutoSize = true,
            BackColor = Color.Transparent,

            // Placed after the buttons rather than at a fixed spot.
            //
            // It used to sit at a hard-coded x that happened to be clear, right up until Report a
            // bug moved into the footer beside Support — at which point the label landed on top of
            // it and swallowed the clicks, because a Label added later is above the button in the
            // z-order and still takes the mouse even with nothing written in it.
            // Centred on the bar like the buttons either side of it, rather than at a Y that only
            // happened to line up at the old footer height.
            Location = new Point(bug.Right + 14, (FooterHeight - Theme.Small.Height) / 2),
        };
        bar.Controls.Add(_saveNote);

        bar.Controls.Add(right);
        bar.Controls.Add(donate);
        bar.Controls.Add(bug);

        // Added last so it docks first and spans the full width. Added first, it was laid out
        // after the button strip had already claimed the right-hand side, so the divider ran
        // only part way across and read as a stray line in the bottom-left corner.
        bar.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Hairline });

        return bar;
    }

    // ================= pages =================

    private Panel BuildDisplayPage()
    {
        var page = NewPage(Words.PageDisplayAudio, Words.PageDisplayAudioWhy);

        NewSection(page, Words.SectionDisplay);
        var card = NewCard(page);
        // Dimmed but selectable, exactly as switched-off controllers are — see Dropdown.IsMuted.
        _tvDisplay.IsMuted = item => item is DisplayInfo { IsActive: false };

        AddPick(card, Words.TvDisplay, _tvDisplay, Words.TvDisplayWhy);

        // Resolution sits directly under the display picker: the modes on offer are a property of
        // whichever display is chosen above, and the list is rebuilt whenever that choice changes.
        AddPick(card, Words.TvVideoMode, _tvVideoMode, Words.TvVideoModeWhy, Words.VideoModeAuto,
                indent: Indent);

        // Info blue, not red: a television being switched off is a state, not a fault.
        //
        // Indented to match the resolution picker it explains, but without an elbow of its own —
        // it is the same setting continuing, not a new one hanging off the display.
        _videoModeNote = new RichNote("", RowWidth - Indent, Theme.Small, Theme.SmallBold,
                                      Theme.Info, Theme.Info)
        {
            Visible = false,
        };
        AddRow(card, _videoModeNote, Indent, elbow: false);

        // Disconnect-on-exit sits below the resolution now, per the chosen order.
        AddToggle(card, Words.TurnOffTvOnExit, _turnOffTvOnExit, Words.TurnOffTvOnExitWhy, Indent, setting: nameof(AppConfig.TurnOffTvOnExit));

        // Refilled, not appended to.
        //
        // This page is rebuilt on every resize and every return to the tab, and the controls
        // outlive the rebuild — so AddRange onto a list nobody cleared left one more copy of
        // the options each pass. Three rebuilds, three identical blocks of choices. Every
        // other list in this window clears first; these two were the exceptions.
        SetOptions(_displayMode, Words.DisplayModeOptions);
        AddPick(card, Words.DisplayMode, _displayMode, Words.DisplayModeWhy,
                Words.DisplayModeOptions[(int)Defaults.DisplayMode]);

        // Why "turn them off" is sometimes the only thing that works: a game hardwired to a monitor
        // ignores which one is primary, and no window move can pull it off a display it owns.
        AddRow(card, new RichNote(Words.DisplayModeGameNote, RowWidth, Theme.Small, Theme.SmallBold,
                                  Theme.Info, Theme.Info), 0, elbow: false);

        // The mode list belongs to whichever display is chosen, so it is rebuilt when that
        // changes as well as when the page is built.
        //
        // Detached before it is attached: this runs again on every rebuild, and += on its own
        // stacked another copy of the handler each time, so one change to the display ended up
        // rebuilding the mode list once per rebuild the window had ever done.
        _tvDisplay.SelectedIndexChanged -= OnTvDisplayChanged;
        _tvDisplay.SelectedIndexChanged += OnTvDisplayChanged;
        RefreshVideoModes();

        NewSection(page, Words.SectionAudio);
        var audio = NewCard(page);
        AddToggle(audio, Words.SwitchAudio, _switchAudio, Words.SwitchAudioWhy, setting: nameof(AppConfig.SwitchAudio));

        // Dimmed but selectable when off, exactly like the display picker — a TV playing sound over
        // HDMI only shows its audio device while it is switched on.
        _tvAudio.IsMuted = item => item is AudioDeviceInfo { IsActive: false };
        _desktopAudio.IsMuted = item => item is AudioDeviceInfo { IsActive: false };

        AddPick(audio, Words.TvAudio, _tvAudio, Words.TvAudioWhy);

        // Under the couch-mode device, since that is the one usually driven over HDMI.
        AddRow(audio, new RichNote(Words.TvAudioHdmiNote, RowWidth, Theme.Small, Theme.SmallBold,
                                   Theme.Info, Theme.Info), 0, elbow: false);

        AddPick(audio, Words.DesktopAudio, _desktopAudio, Words.DesktopAudioWhy);

        _deviceNote = new Label
        {
            Text = "",
            ForeColor = Theme.Good,
            Font = Theme.Small,
            AutoSize = true,
            Margin = new Padding(4, 6, 0, 0),
            BackColor = Color.Transparent,
        };
        Stack(page).Controls.Add(_deviceNote);

        // On this page rather than with the settings that cause it.
        //
        // Leaving a game running on the desktop happens two ways — the prompt's "swap to desktop"
        // and a controller dropping out — which are on two different pages, so putting it beside
        // either would hide it from the other. It is a question about sound, and somebody hunting
        // for "why is my game still making noise" comes looking under Audio.
        AddToggle(audio, Words.MuteOnDesktop, _muteOnDesktop, Words.MuteOnDesktopWhy,
                  titleEmphasis: Theme.Good, setting: nameof(AppConfig.MuteGameOnDesktop));

        AddPageReset(page, DisplayPage);

        return page;
    }

    /// <summary>
    /// Everything to do with Steam, on its own.
    ///
    /// These lived under Display &amp; Audio for a while on the grounds that switching to the TV
    /// is what they cause. That reads the wrong way round: what they have in common is Steam,
    /// and someone looking for a Steam setting looks for the word Steam.
    /// </summary>
    /// <summary>
    /// A key and a button combination that start or end a session.
    ///
    /// Kept apart from the Controller page even though one of them is a controller thing. That
    /// page answers "which pad, and what should switching it on do"; this one answers "what do
    /// I press to switch over" — and the pad shortcut has far more in common with the keyboard
    /// shortcut beside it than with the trigger settings it would otherwise sit under.
    /// </summary>
    /// <summary>The shortcuts page, for the same reasons the controller page is remembered.</summary>
    private Panel? _shortcutsPage;

    private Panel BuildShortcutsPage()
    {
        var page = NewPage(Words.PageShortcuts, Words.PageShortcutsWhy);
        _shortcutsPage = page;

        var card = NewCard(page);

        // One action, two boxes: set a keyboard combination, a controller one, or both.
        AddShortcutPair(card, Words.ShortcutSession, _shortcutKeyboard, _shortcutController, Words.ShortcutSessionWhy);

        AddShortcutPair(card, Words.ShortcutHdr, _shortcutKeyboardHdr, _shortcutControllerHdr, Words.ShortcutHdrWhy);

        AddToggle(card, Words.GuideEndsSession + Words.BadgeRecommended, _guideEndsSession,
                  Words.GuideEndsSessionWhy,
                  setting: nameof(AppConfig.EndSessionOnGuideButton), titleEmphasis: Theme.Good);

        // The "always end with your hotkey" warning is gone. Quitting Big Picture is now handled
        // properly — the watcher sees it, closes the game and ends the session — so the advice
        // described a limitation that no longer exists.

        // The "HDR hotkeys act on your main display only" note is gone. The HDR hotkey's own
        // description already opens with "Toggles HDR on your main display", so the note was the
        // same fact said twice on one page — and it was the wider of the two, so it read as the more
        // important one.

        AddNote(card, Words.ShortcutControllerNote);

        AddPageReset(page, HotkeysPage);

        return page;
    }

    /// <summary>
    /// One action with both a keyboard box and a controller box under it — the two ways to trigger
    /// the same thing, in one place rather than duplicated across separate keyboard and controller
    /// sections. Either, both, or neither can be set.
    /// </summary>
    private void AddShortcutPair(Card card, string title, ShortcutBox keyboard, ShortcutBox controller,
                                string description)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(RowWidth, 0),
            BackColor = Color.Transparent,
        };

        row.Controls.Add(new RichNote(title, RowWidth, Theme.BodySemi, Theme.BodyBold, Theme.Text));
        row.Controls.Add(new RichNote(description, RowWidth) { Margin = new Padding(0, 2, 0, 7) });

        row.Controls.Add(ShortcutCaption(Words.SectionShortcutKeyboard, first: true));
        row.Controls.Add(ShortcutLine(keyboard));
        row.Controls.Add(ShortcutCaption(Words.SectionShortcutController, first: false));
        row.Controls.Add(ShortcutLine(controller));

        AddRow(card, row, 0);
    }

    private Label ShortcutCaption(string text, bool first)
    {
        var caption = NewLabel(text, Theme.Caption, Theme.TextFaint);
        caption.Margin = new Padding(0, first ? 0 : 9, 0, 3);
        return caption;
    }

    /// <summary>A shortcut box with a Clear button beside it, on its own line.</summary>
    private FlowLayoutPanel ShortcutLine(ShortcutBox box)
    {
        box.Size = new Size(Math.Min(RowWidth, DropdownWidth), 34);
        box.Margin = new Padding(0);

        var clear = new FlatButton
        {
            Text = Words.ShortcutClear,
            Size = new Size(90, 34),
            Margin = new Padding(10, 0, 0, 0),
        };
        clear.Click += (_, _) => { box.Clear(); MarkDirty(); };
        _tips.SetToolTip(clear, Wrapped(Words.ShortcutClearWhy));

        var line = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            BackColor = Color.Transparent,
        };
        line.Controls.Add(box);
        line.Controls.Add(clear);
        return line;
    }

    private Panel? _controllerPage;

    /// <summary>
    /// Pause controller triggers only where they would actually get in the way.
    ///
    /// Suspending for the whole time the settings window was open was too broad. The reason for
    /// suspending at all is narrow — switching a pad on to pick it out of the list must not also
    /// start a session — and that only applies on the page with the list on it. Everywhere else
    /// the window is merely open, and someone reaching for their controller on the Display page
    /// means it exactly as they would with the window closed.
    ///
    /// Never suspended during a session either: by then the display is already on the
    /// television, and suspending would disable the disconnect that ends the session.
    /// </summary>
    /// <summary>
    /// Name the picker after what it currently does.
    ///
    /// It gates the connect and the disconnect independently, so with only one of the two switches
    /// on, calling it "starts and ends a session" describes something that is not happening. The
    /// title is the only place that can say so — the dropdown itself looks identical either way.
    /// </summary>
    // UpdateTriggerPadTitle stood here as an empty method. It used to rewrite the trigger picker's
    // title from two other controls' states and got that wrong twice — once by assuming disconnect
    // handling was always on, once by titling the picker "Controller to watch" while nothing was
    // watching. TriggerPad is a fixed title now and TriggerPadIdle carries the "this is deciding
    // nothing" case in a sentence, so there was nothing left for it to do.
    //
    // It was kept as a no-op on the grounds that "several places call it after changing a switch,
    // and the call sites are the reminder that this used to need doing". There was one call site,
    // and a method that does nothing is a poor reminder — it reads at every call as though something
    // is being kept in step. This comment is the reminder instead.

    /// <summary>
    /// The hold button is stored as a PadControl; the picker is a short fixed list. These two map
    /// between them, and the order here must match Words.MouseHoldOptions.
    /// </summary>
    // L1 and L2 are deliberately absent: L1 is the right-click, and L2 is left free as a natural
    // partner to it, so neither can be picked as the hold modifier and clash with clicking.
    private static readonly PadControl[] HoldButtons =
    [
        PadControl.R2, PadControl.L3, PadControl.R3,
        PadControl.FaceDown, PadControl.FaceRight, PadControl.FaceLeft, PadControl.FaceUp,
        PadControl.DpadUp, PadControl.DpadDown, PadControl.DpadLeft, PadControl.DpadRight,
        PadControl.Select, PadControl.Start,
    ];

    private static int HoldButtonToIndex(int control)
    {
        int at = Array.IndexOf(HoldButtons, (PadControl)control);
        return at < 0 ? 0 : at;
    }

    private static int IndexToHoldButton(int index)
        => (int)HoldButtons[Math.Clamp(index, 0, HoldButtons.Length - 1)];

    /// <summary>The pointer-speed slider's range. The curve that turns a step into a cursor
    /// speed lives in PointerControl; here we only need the end points to size the track.</summary>
    private const int SpeedMin = 1;
    private const int SpeedMax = 20;

    /// <summary>The deadzone slider's range, as a percentage of full stick deflection.</summary>
    private const int DeadzoneMin = 0;
    private const int DeadzoneMax = 60;

    /// <summary>
    /// Whether this window is the one the user is looking at.
    ///
    /// Tracked from the events rather than read from ContainsFocus on demand: focus moves through
    /// intermediate states while a window is being shown or a dropdown is opening, and a question
    /// asked in the middle of one gets an answer that is true for a few milliseconds.
    /// </summary>
    private bool _windowActive = true;

    private void UpdatePadSuspension()
    {
        if (_pads is null) return;
        if (_currentPage < 0 || _currentPage >= _pages.Count) return;

        var showing = _pages[_currentPage].Page;

        bool onControllerPage = ReferenceEquals(showing, _controllerPage);
        bool onShortcutsPage = ReferenceEquals(showing, _shortcutsPage);

        // The shortcuts page counts too. Setting a controller shortcut means switching the pad
        // on and pressing buttons on it, which is exactly the sequence that would otherwise
        // start a session and throw the display over to the television mid-configuration.
        //
        // But only while this window is the one you are looking at.
        //
        // [BUG] It was the page alone, so leaving the settings window open on the Controller page and
        // going off to do something else left every controller trigger dead — switching a pad on did
        // nothing, and neither did using it as a mouse — with the only clue being a window sitting
        // behind whatever you were actually doing. The reason for suspending is that buttons pressed
        // *at this window* must not also fire triggers, and that reason evaporates the moment the
        // window is not in front. Minimised counts as not in front, obviously.
        bool watching = _windowActive && WindowState != FormWindowState.Minimized;

        bool suspend = (onControllerPage || onShortcutsPage) && watching && !SessionNow;
        bool wasSuspended = _pads.Suspended;

        _pads.Suspended = suspend;

        // Button reports arrive through the watcher's raw input registration, so on the page
        // where buttons are being recorded it has to be running — whatever the trigger settings
        // say. Without this, someone with both controller triggers switched off gets a box that
        // reports no controller while their pad sits plainly connected in front of them.
        if (onShortcutsPage) _pads.Start();

        // Leaving one of those pages takes the brakes off, so the state of the pads right now
        // becomes the new normal.
        //
        // Suspending only stops changes being *acted* on. Without this, a pad switched on to
        // record a shortcut is still a pad that arrived, and walking to another page released
        // the brake with that arrival still sitting there waiting to be noticed — the session
        // then starting several clicks after the thing that caused it, which is impossible to
        // connect back to switching the controller on.
        if (wasSuspended && !suspend) _pads.Rebaseline();
    }

    private Panel BuildControllerPage()
    {
        var page = NewPage(Words.PageController, Words.PageControllerWhy);
        _controllerPage = page;

        // ── one question per card ──
        //
        // The page used to be a status line, then one card holding five settings under a heading
        // that named two of them, then the mouse. Reorganised so each card answers a single question
        // a reader might arrive with — which controller, what starts a session, what happens if it
        // goes away, and using it as a mouse — because a card you can identify from its heading is a
        // card you can skip, and skipping is most of what anyone does on a settings page.
        //
        // The order is the order the questions occur: what the app can see, then which of those you
        // mean, then what it does. Every card below the first is about the controller chosen in it.

        // ── what is connected ──
        //
        // First, and above every heading, so it reads as the state of things rather than as a
        // footnote to whatever setting happens to be nearest. Visibility of system status is the
        // oldest heuristic there is and this page had none: the only answer to "can the app see my
        // controller?" was the title-bar strip vanishing, and an absent element is indistinguishable
        // from one nobody noticed.
        var status = NewCard(page);
        _padPresence = AddNote(status, Words.PadsNone, tight: true);

        // ── which controller ──
        //
        // Its own card now, ahead of everything it governs. It used to sit in the middle of the
        // starting-a-session card, below a switch it is not part of, which is a strange place for
        // the one control that decides what every other row on the page is talking about.
        NewSection(page, Words.SectionYourController);
        var whose = NewCard(page);

        // Controllers that are switched off are shown dimmed, and stay selectable — see
        // Dropdown.IsMuted. An empty Path is what marks one as not currently here.
        _triggerPad.IsMuted = item => item is ControllerDevice { Path.Length: 0 };

        // The line above answers "is the one I picked here?", so it has to hear about the picking.
        _triggerPad.SelectedIndexChanged -= OnTriggerPadPicked;
        _triggerPad.SelectedIndexChanged += OnTriggerPadPicked;

        RefreshControllers();

        AddPick(whose, Words.TriggerPad, _triggerPad, Words.TriggerPadWhy,
                Words.AnyController, action: MakeControllerButtons());

        // Only when the choice is genuinely deciding nothing, which is a state the page can be left
        // in and gave no account of.
        WhenOn(() => !_startOnController.Checked
                  && DisconnectOf(_onControllerOff, DisconnectAction.ComeBack) == DisconnectAction.Ignore
                  && DisconnectOf(_onControllerLost, DisconnectAction.Ignore) == DisconnectAction.Ignore,
               whose, () => AddNote(whose, Words.TriggerPadIdle));

        AddNote(whose, Words.ControllerTriggerNote);

        // ── starting a session ──
        NewSection(page, Words.SectionController);
        var starting = NewCard(page);

        AddToggle(starting, Words.StartOnController, _startOnController, Words.StartOnControllerWhy,
                  setting: nameof(AppConfig.StartOnControllerConnect));

        // Which connection counts, directly under the switch it belongs to. An indented row says
        // "I am part of the row above me", and this one used to appear below the controller picker
        // — a nested row whose parent was two rows up.
        WhenOn(() => _startOnController.Checked, starting, () =>
        {
            SetOptions(_padLink, Words.PadLinkOptions);
            AddPick(starting, Words.PadLinkPick, _padLink, Words.PadLinkPickWhy,
                    Words.PadLinkOptions[(int)PadLink.Either], indent: Indent);
        });

        // ── losing it mid-session ──
        //
        // Its own card, because it is its own question. Under "Starting a session" it was the one
        // row the heading did not describe, and renaming the heading to cover both only made the
        // heading vaguer.
        NewSection(page, Words.SectionDisconnect);
        var losing = NewCard(page);

        // What the answers mean, once, above both questions.
        AddNote(losing, Words.DisconnectNote);

        SetOptions(_onControllerOff, Words.DisconnectOptions);
        AddPick(losing, Words.DisconnectOff, _onControllerOff, Words.DisconnectOffWhy,
                Words.DisconnectOptions[(int)DisconnectAction.ComeBack]);

        // The shorter list: no "close the game" for an event nobody chose. See DisconnectOptions.
        //
        // The default label is read from the same list the picker was filled from. It used to come
        // from DisconnectOptions while the picker took DisconnectOptionsLost, and printed the right
        // words only because both lists happen to open with "Do nothing" — a coincidence, and one
        // that would have started quietly mislabelling the default the moment either list changed at
        // the front. An index into one list is only meaningful against that list.
        SetOptions(_onControllerLost, Words.DisconnectOptionsLost);
        AddPick(losing, Words.DisconnectLost, _onControllerLost, Words.DisconnectLostWhy,
                Words.DisconnectOptionsLost[(int)DisconnectAction.Ignore]);

        // ── the mouse ──
        //
        // One switch in a card of its own, then two cards beneath it that only exist while it is on.
        //
        // It was seven rows in a single card, every one of them a title, two lines of explanation and
        // a control — a column with no shape to it, which has to be read rather than scanned. Split by
        // the question each answers, the same seven rows come in fours and threes under headings that
        // say which is which, and the card you want can be found without reading the one above it.
        NewSection(page, Words.SectionMouse);
        var mouse = NewCard(page);

        AddToggle(mouse, Words.MouseControl, _mouseControl, Words.MouseControlWhy,
                  setting: nameof(AppConfig.MouseControlEnabled));

        var stack = Stack(page);

        // How the pointer moves.
        WhenOn(() => _mouseControl.Checked, stack, () =>
        {
            NewSection(page, Words.SectionPointer);
            var how = NewCard(page);

            SetOptions(_cursorStick, Words.CursorStickOptions);
            AddPick(how, Words.CursorStick, _cursorStick, Words.CursorStickWhy,
                    Words.CursorStickOptions[Defaults.MouseCursorStick]);

            AddSlider(how, Words.MouseSpeed, _mouseSpeed, Words.MouseSpeedWhy,
                      Words.MouseSpeedSlow, Words.MouseSpeedFast, SpeedMin, SpeedMax,
                      Defaults.MouseSpeed, format: v => $"{v} / {SpeedMax}");

            AddSlider(how, Words.MouseDeadzone, _mouseDeadzone, Words.MouseDeadzoneWhy,
                      Words.MouseDeadzoneLow, Words.MouseDeadzoneHigh, DeadzoneMin, DeadzoneMax,
                      Defaults.MouseDeadzone, format: v => $"{v}%");

            AddToggle(how, Words.UseTrackpad, _useTrackpad, Words.UseTrackpadWhy,
                      setting: nameof(AppConfig.MouseUseTrackpad));

            // Where and when it is allowed to drive.
            NewSection(page, Words.SectionPointerWhen);
            var when = NewCard(page);

            AddToggle(when, Words.MouseInGames, _mouseInGames, Words.MouseInGamesWhy,
                      setting: nameof(AppConfig.MouseInGames));

            AddToggle(when, Words.MouseHold, _mouseHold, Words.MouseHoldWhy,
                      setting: nameof(AppConfig.MouseHoldToActivate));

            // And the button picker only while there is a hold to pick a button for.
            WhenOn(() => _mouseControl.Checked && _mouseHold.Checked, when, () =>
            {
                SetOptions(_mouseHoldButton, Words.MouseHoldOptions);
                AddPick(when, Words.MouseHoldButton, _mouseHoldButton, Words.MouseHoldButtonWhy,
                        Words.MouseHoldOptions[HoldButtonToIndex(Defaults.MouseHoldButton)],
                        indent: Indent);
            });
        });

        // Also re-read on the way into the page, in case anything changed while it was hidden.
        page.VisibleChanged += (_, _) => { if (page.Visible) RefreshControllers(); };

        AddPageReset(page, ControllerPage);

        return page;
    }

    private Panel BuildHdrPage()
    {
        var page = NewPage(Words.PageAutoHDR, Words.PageAutoHDRWhy);

        NewSection(page, Words.SectionAutomaticHDR);
        var card = NewCard(page);
        // One three-way choice rather than two switches. They were never independent — whole-session
        // HDR made the per-game switch do nothing — and a pair of toggles that cancel each other can
        // only say so in prose. Off / per game / whole session says it in the shape of the control.
        SetOptions(_hdrMode, Words.HdrModeOptions);
        AddPick(card, Words.HdrMode, _hdrMode, Words.HdrModeWhy, Words.HdrModeOptions[(int)HdrMode.Off]);

        // Whether either display can do HDR at all — the fact that decides whether anything on this
        // page does anything, and the one thing it never said.
        _hdrDisplayNote = AddNote(card, Words.HdrDisplaysUnknown);
        RefreshHdrDisplayNote();

        // Re-read on the way back in: a television that was off when this window opened is the
        // ordinary case, and the answer changes the moment it is switched on.
        page.VisibleChanged += (_, _) => { if (page.Visible) RefreshHdrDisplayNote(); };

        NewSection(page, Words.SectionGames);

        var listCard = NewCard(page);

        // Only while the list is doing nothing. A card full of greyed-out controls with no
        // explanation reads as a fault; one sentence turns it into a consequence of the choice made
        // three rows above it.
        WhenOn(() => HdrModeOf(_hdrMode) == HdrMode.WholeSession, listCard,
               () => AddNote(listCard, Words.HdrWholeSessionNote));

        // Two deliberate rows rather than one wrapping row.
        //
        // Wrapping left a single button stranded on a line of its own, which reads as a mistake.
        // Splitting by purpose instead — find a game above, act on the selection below — gives
        // two full rows and makes the grouping mean something.
        var finding = new FlowLayoutPanel
        {
            Size = new Size(RowWidth, 44),
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 8),
        };

        var acting = _actingRow = new FlowLayoutPanel
        {
            Size = new Size(RowWidth, HeaderHeight),
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 10),
        };

        var selectAll = _selectAll = new FlatButton { Text = Words.ButtonAll, Size = new Size(78, HeaderHeight), Radius = 6, Font = Theme.Small };
        selectAll.Click += (_, _) => SetAllGames(true);

        var clearAll = _clearAll = new FlatButton { Text = Words.ButtonNone, Size = new Size(78, HeaderHeight), Radius = 6, Font = Theme.Small };
        clearAll.Click += (_, _) => SetAllGames(false);

        var browse = _browseButton = new FlatButton { Text = Words.ButtonBrowse, Size = new Size(BrowseWidth - 8, 40), Radius = 6, Font = Theme.Small };
        browse.Click += (_, _) => BrowseForGame();

        var scan = _scanButton = new FlatButton { Text = Words.ButtonScan, Size = new Size(ScanWidth - 8, 40), Radius = 6, Font = Theme.Small };
        scan.Click += (_, _) => ScanForGames();
        _tips.SetToolTip(scan, Wrapped(Words.TipScan));

        _hdrBulk = new FlatButton { Text = Words.ButtonNativeHdr, Size = new Size(132, HeaderHeight), Radius = 6, Font = Theme.Small };
        _hdrBulk.Click += (_, _) => ToggleNativeHdrGames();
        _tips.SetToolTip(_hdrBulk, Wrapped(Words.TipHdrBulk));

        _tips.SetToolTip(selectAll, Wrapped(Words.TipSelectAll));
        _tips.SetToolTip(clearAll, Wrapped(Words.TipClearAll));
        _tips.SetToolTip(browse, Wrapped(Words.TipBrowse));

        _hdrBulkOther = new FlatButton
        {
            Text = Words.ButtonNonNative,
            Size = new Size(140, HeaderHeight),
            Radius = 6,
            Font = Theme.Small,
        };

        _hdrBulkOther.Click += (_, _) => ToggleNonNativeHdrGames();
        _tips.SetToolTip(_hdrBulkOther, Wrapped(Words.TipHdrBulkOther));

        foreach (Control c in new Control[] { selectAll, clearAll, _hdrBulk, _hdrBulkOther })
            c.Margin = new Padding(0, 0, 6, 0);

        browse.Margin = new Padding(0, 0, 8, 4);
        scan.Margin = new Padding(0, 0, 0, 4);

        // Sits on the search row rather than as a full setting: a compact checkbox for the "the HDR
        // hotkey adds/removes the game from this list" behaviour, right where that list is.
        _hdrHotkeyRemember = new InlineCheck { Text = Words.HdrHotkeyRemember, Height = 40, Font = Theme.Small };
        _hdrHotkeyRemember.SetQuietly(Config.HdrHotkeyRemembersGame);
        _hdrHotkeyRemember.Width = _hdrHotkeyRemember.PreferredWidth();
        _hdrHotkeyRemember.CheckedChanged += (_, _) => { MarkDirty(); };
        _hdrHotkeyRemember.Margin = new Padding(0, 0, 0, 6);
        _tips.SetToolTip(_hdrHotkeyRemember, Wrapped(Plain(Words.HdrHotkeyRememberWhy)));

        // The search box takes the row, less the two buttons beside it.
        //
        // It shared that row with a checkbox whose label ran off the right-hand edge, which is the
        // clearest sign a control is in the wrong place: nothing about "remember games from the HDR
        // hotkey" is a way of finding a game. Browse and Scan are — they both put games in this list —
        // so they move up here and the row becomes one idea instead of three.
        _search = new SearchBox("Search your games…")
        {
            Width = Math.Max(200, RowWidth - BrowseWidth - ScanWidth - CountWidth - 16),
            Height = 40,
            Margin = new Padding(0, 0, 8, 4),
        };

        // Filtering never changes what is ticked — only what is shown. Narrowing the list to
        // find one game must not quietly drop the other fifty from your selection.
        _search.QueryChanged += () => RebuildGameList();

        _gameCount = NewLabel("", Theme.Small, Theme.TextFaint);
        _gameCount.AutoSize = false;
        _gameCount.Size = new Size(CountWidth - 8, 40);
        _gameCount.TextAlign = ContentAlignment.MiddleRight;
        _gameCount.Margin = new Padding(8, 0, 0, 4);
        _gameCount.Cursor = Cursors.Hand;

        // Clicking the count shows only what is ticked, and clicking again puts the rest back.
        //
        // A filter, never a selection: narrowing the view to check your thirty-two choices among a
        // hundred and twenty-six games must not change which thirty-two they are.
        _gameCount.Click += (_, _) =>
        {
            _onlyTicked = !_onlyTicked;
            RebuildGameList();
            UpdateSelectLabels();
        };

        _tips.SetToolTip(_gameCount, Wrapped("Click to show only the games that are on."));

        finding.Controls.AddRange([_search, browse, scan, _gameCount]);

        // One caption, then only the things that act on the ticks.
        //
        // Six buttons of three different kinds sat in this row looking identical — two that change the
        // selection, two that change it by category, and two that add games to the list entirely. A row
        // where every control looks the same but means something different is a row that has to be read
        // in full before anything can be pressed. This one now does one job, and says so.
        var selectLabel = NewLabel("SELECT", Theme.Caption, Theme.TextFaint);
        selectLabel.Margin = new Padding(0, 7, 10, 0);

        acting.Controls.Add(selectLabel);
        acting.Controls.AddRange([selectAll, clearAll, _hdrBulk, _hdrBulkOther]);

        // What a scan found, said where the scanning happened.
        //
        // It used to appear in the window footer, three inches from the button that caused it and
        // below everything else on the page — far enough away that pressing Scan looked like it did
        // nothing at all. Feedback belongs next to the control it is feedback for, so it sits at the
        // end of this row, directly over Browse and Scan.
        // Measured against what is actually beside it, not against a guess.
        //
        // A fixed width here was already wrong once: widening the category buttons to carry their
        // counts pushed the note narrower than its own text, so "93 non-native games turned on · Undo"
        // lost the word that mattered. Every control on this row has a known size, so the note simply
        // takes what is left.
        int used = TextRenderer.MeasureText("SELECT", Theme.Caption).Width + 10
                 + selectAll.Width + 6
                 + clearAll.Width + 6
                 + _hdrBulk.Width + 6
                 + _hdrBulkOther.Width + 6;

        _scanNote = NewLabel("", Theme.Small, Theme.Info);
        _scanNote.AutoSize = false;
        _scanNote.Size = new Size(Math.Max(150, RowWidth - used), HeaderHeight);
        _scanNote.TextAlign = ContentAlignment.MiddleRight;
        _scanNote.Margin = new Padding(0);

        _scanNote.Click += (_, _) => UndoBulk();

        acting.Controls.Add(_scanNote);

        // And the two settings that were wedged into those rows get a line each, at full width, where
        // their labels can be read to the end.
        var options = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(RowWidth, 0),
            BackColor = Color.Transparent,
            Margin = new Padding(0, 10, 0, 0),
        };

        _hdrHotkeyRemember.Width = RowWidth;
        _hdrHotkeyRemember.Height = 30;
        _hdrHotkeyRemember.Margin = new Padding(0, 0, 0, 2);

        // "Switch HDR for new games automatically" used to sit under this one and has been removed
        // along with the seeding that ticked native-HDR games on a first run. See AppConfig.HdrGames.
        options.Controls.Add(_hdrHotkeyRemember);

        var listHolder = new BufferedPanel
        {
            Size = new Size(RowWidth, MinListHeight),
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };

        // Wider than the panel that holds it, so the native scrollbar falls outside the clip
        // region and is simply never seen.
        _gameList = new GameListView(_icons, _checkedGames)
        {
            Location = new Point(0, 0),
            Size = new Size(RowWidth + GameListView.NativeBarWidth, MinListHeight),
            SupportOf = SupportOf,

            // A tag the user set by hand, as opposed to one the lookups produced.
            IsManuallyTagged = game => Config.HdrTags.ContainsKey(game.Key),
        };

        var listHeader = _listHeader = new GameListHeader(_gameList)
        {
            Width = RowWidth,
            Margin = new Padding(0),
        };

        listHeader.SortChanged += () => RebuildGameList();

        _gameList.ItemRightClicked += ShowTagMenu;
        _gameList.BadgeClicked += CycleTag;

        _gameList.CheckChanged += () =>
        {
            MarkDirty();
            UpdateHdrBulkLabel();

            // The count is part of the feedback for ticking a box, so it moves with the tick rather
            // than waiting for the next rebuild.
            UpdateGameCount();

            // Deliberately not re-sorted here. Moving a row the instant it is ticked scrolls the
            // list out from under you and turns selecting several games into a chase. The rows stay
            // put; the new order is applied the next time the list is rebuilt.
        };

        var listBar = _listBar = new ThinScrollBar
        {
            Backdrop = Theme.Surface,
            // Widened from 8 to match the page scrollbar. This is the longest scroll in the app —
            // a library can be hundreds of games — so it is the last place the indicator should be
            // the hardest one to see and to grab.
            Bounds = new Rectangle(RowWidth - 18, 4, 14, MinListHeight - 8),
            ContentHeight = () => _gameList.ContentHeight,
            ViewportHeight = () => _gameList.ClientSize.Height,
            Offset = () => _gameList.PixelOffset,
            ScrollTo = value => _gameList.PixelOffset = value,
        };
        _gameList.Scrolled += () => listBar.Invalidate();

        // Something to say when a search matches nothing.
        //
        // An empty list looks identical to a broken one: a blank panel where a hundred and twenty-six
        // games were a moment ago, with nothing to explain it and no obvious way back. This names the
        // reason and clicking it undoes whichever filter caused it.
        _emptyNote = new Label
        {
            Text = "",
            Font = Theme.Body,
            ForeColor = Theme.TextFaint,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
            Bounds = new Rectangle(0, 0, RowWidth, MinListHeight),
            Visible = false,
            Cursor = Cursors.Hand,
        };

        _emptyNote.Click += (_, _) =>
        {
            _onlyTicked = false;
            _search?.Clear();
            RebuildGameList();
            UpdateSelectLabels();
        };

        listHolder.Controls.Add(_emptyNote);
        listHolder.Controls.Add(listBar);
        listHolder.Controls.Add(_gameList);
        listBar.BringToFront();

        _listHolder = listHolder;
        page.SizeChanged += (_, _) => StretchGameList(page);

        // The header reads its column positions from the list, so it has to redraw whenever
        // anything that moves them changes.
        _gameList.SizeChanged += (_, _) => listHeader.Invalidate();

        // Rebuild whenever the page is shown again.
        //
        // The list is only refilled when something asks it to, so anything that changed while
        // the page was hidden — a background HDR result, a rescan, or simply the filter no
        // longer matching what is displayed — left the rows and the search box disagreeing.
        page.VisibleChanged += (_, _) =>
        {
            if (page.Visible) RebuildGameList();
        };

        // Selection first, then the filter, the actions and the count.
        //
        // That order is the one every toolbar guideline gives: a bulk selector is the first element,
        // filters and global actions sit to its right, and the item count is last. Two rows rather than
        // one because the width will not take them side by side, so the sequence runs top-left to
        // bottom-right instead — selection above, and the row that touches the list holding the things
        // that decide what is in it.
        Rows(listCard).Controls.Add(acting);
        Rows(listCard).Controls.Add(finding);
        Rows(listCard).Controls.Add(listHeader);
        Rows(listCard).Controls.Add(listHolder);
        Rows(listCard).Controls.Add(options);
        listHolder.Margin = new Padding(0);

        Stack(page).Controls.Add(new Label
        {
            Text = Words.GamesListNote,
            ForeColor = Theme.TextDim,
            Font = Theme.Small,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth - 8, 0),
            Margin = new Padding(4, 2, 0, 0),
            BackColor = Color.Transparent,
        });

        AddPageReset(page, HdrPage);

        return page;
    }

    private Panel BuildPerformancePage()
    {
        var page = NewPage(Words.PagePerformance, Words.PagePerformanceWhy);

        NewSection(page, Words.SectionWindows);
        var windows = NewCard(page);

        // The two security switches sit at the top: they are the most consequential things on this
        // page, and the ones that actually stop a game or launcher stranding a controller-only
        // session behind a prompt it cannot answer. Live system toggles, not saved settings — each
        // reflects the real Windows state and acts on it through its own elevation prompt. Red
        // titles, because both lower the machine's security.
        // Each toggle carries its own check now, run when it is switched on (or, for the two live
        // system switches, whenever the page is shown) and printed right underneath it — so there
        // is no separate "check" section to go and press.
        _disableUac.CheckedChanged -= OnUacToggled;
        _disableUac.CheckedChanged += OnUacToggled;
        // The two security switches keep the small target on purpose. Everywhere else a row-wide
        // click is a straight win; here it would mean a press aimed at the description below could
        // switch UAC off, and that is a change the user must have meant.
        AddToggle(windows, Words.DisableUac, _disableUac, Words.DisableUacWhy,
                  check: PerformanceCheck.CheckUac, checkAlways: true, rowClick: false);
        SetUacQuietly(!UacControl.IsEnabled());

        _disableFirewall.CheckedChanged -= OnFirewallToggled;
        _disableFirewall.CheckedChanged += OnFirewallToggled;
        AddToggle(windows, Words.DisableFirewall, _disableFirewall, Words.DisableFirewallWhy,
                  check: PerformanceCheck.CheckFirewall, checkAlways: true, rowClick: false);
        SetFirewallQuietly(!FirewallControl.IsEnabled());

        AddToggle(windows, Words.ChangePowerPlan, _changePowerPlan, Words.ChangePowerPlanWhy, setting: nameof(AppConfig.ChangePowerPlan),
                  check: () => PerformanceCheck.CheckPowerPlan(SelectedPowerPlan()));

        RefreshPowerPlans();
        AddPick(windows, Words.PowerPlanPick, _powerPlan, Words.PowerPlanPickWhy, Words.PowerPlanAuto, indent: Indent);

        // The picker follows the toggle, so a list of plans is not offered to someone who has
        // just said they do not want their power plan touched.
        _changePowerPlan.CheckedChanged -= OnChangePowerPlanToggled;
        _changePowerPlan.CheckedChanged += OnChangePowerPlanToggled;
        _powerPlan.Enabled = _changePowerPlan.Checked;

        // Re-run the power check when a different plan is picked, so its line under the toggle
        // updates to whether that plan can be switched to on this machine.
        _powerPlan.SelectedIndexChanged -= OnPowerPlanChanged;
        _powerPlan.SelectedIndexChanged += OnPowerPlanChanged;

        // "Keep the TV awake" is not offered at the moment.
        //
        // It solves the rarer half of the problem. The trouble people actually hit with this app is
        // screens that will *not* sleep — a connected controller pins Windows' input clock for as long
        // as anything is subscribed to it — so a switch whose job is to pin it deliberately is one more
        // thing to rule out when a display stays on, in exchange for a cutscene case that Steam and most
        // games already handle. The setting and its plumbing are still here; only the way in is gone,
        // and it is forced off on save so a previously saved "on" cannot outlive the switch.

        AddToggle(windows, Words.SilenceNotifications, _silenceNotifications, Words.SilenceNotificationsWhy, setting: nameof(AppConfig.SilenceNotifications),
                  check: PerformanceCheck.CheckNotifications);

        // Badged, because it is the one setting on this page that is a plain recommendation. The rest
        // are trades — security for reach, or a Windows-wide preference for a quieter session — and
        // sitting unlabelled among them made a switch worth having look like another thing to weigh up.
        // Applied the instant it is pressed, and read back from Windows rather than from our own
        // config — the same treatment UAC and the firewall get above, and for the same reason: this
        // is a Windows setting that anything on the machine can change, so a toggle showing what we
        // last wrote is a toggle that can be wrong.
        _enableGameMode.CheckedChanged -= OnGameModeToggled;
        _enableGameMode.CheckedChanged += OnGameModeToggled;

        // No "Default:" line. A default is what this app ships a setting as, and this is not this
        // app's setting — Windows decides what it ships as, and saying "Default: On" underneath a
        // mirror of somebody else's switch claims an authorship we do not have.
        AddToggle(windows, Words.EnableGameMode + Words.BadgeRecommended, _enableGameMode,
                  Words.EnableGameModeWhy,
                  titleEmphasis: Theme.Good, check: PerformanceCheck.CheckGameMode);

        SetQuietly(_enableGameMode, GameModeControl.IsEnabled());

        // Priority sits here rather than under a heading of its own.
        //
        // It was under "Graphics", which it never was — process priority is CPU scheduling and
        // has nothing to do with the GPU. That heading only existed to hold the NVIDIA option
        // beside it, and with that gone there is no reason to keep a section for one item that
        // belongs with the rest of the Windows tuning anyway.
        AddToggle(windows, Words.GamePriority, _gamePriority, Words.GamePriorityWhy, setting: nameof(AppConfig.GamePriorityEnabled),
                  check: PerformanceCheck.CheckPriority);

        // The Game Bar toggle lives here, not on the Controller page: it is an Xbox-controller
        // quirk — the Guide button opening the overlay — and belongs with the rest of the Windows
        // behaviour Couch Mode tames, next to Game Mode and notifications.
        // Live mirror, like Game Mode and the two security switches. No "Default:" line either:
        // this is Windows' setting and Windows' default, not one this app ships.
        _disableGameBarButton.CheckedChanged -= OnGameBarToggled;
        _disableGameBarButton.CheckedChanged += OnGameBarToggled;

        AddToggle(windows, Words.DisableGameBarButton, _disableGameBarButton, Words.DisableGameBarButtonWhy,
                  check: PerformanceCheck.CheckGameBar);

        // Re-read the two live system switches every time the page is opened, so they always show
        // the machine's real current state.
        page.VisibleChanged -= OnPerformancePageShown;
        page.VisibleChanged += OnPerformancePageShown;

        AddPageReset(page, PerformancePage, Words.ResetPageKeepsWindows);

        return page;
    }

    /// <summary>
    /// The offer to install a newer version, on the Home page where it will actually be seen.
    ///
    /// Deliberately not silent. Installing replaces the executable and restarts the app, so it happens
    /// when someone presses the button and at no other time — an update that restarted the app by
    /// itself, mid-game, would be indistinguishable from a crash.
    ///
    /// And not during a session either. The restart would take the television with it.
    /// </summary>
    private void AddUpdateCard(Panel page, UpdateInfo ready)
    {
        var card = NewCard(page);

        var title = NewLabel(string.Format(Words.UpdateCardTitle, ready.Version),
                             Theme.BodySemi, Theme.Good);
        title.Margin = new Padding(0, 0, 0, 2);
        AddRow(card, title, 0, elbow: false);

        var body = NewLabel(string.Format(Words.UpdateCardBody, AppInfo.Version),
                            Theme.Small, Theme.TextDim, RowWidth - 8);
        body.Margin = new Padding(0, 0, 0, 10);
        AddRow(card, body, 0, elbow: false);

        if (SessionNow)
        {
            var waiting = NewLabel(Words.UpdateCardDuringSession, Theme.Small, Theme.TextFaint,
                                   RowWidth - 8);
            AddRow(card, waiting, 0, elbow: false);
            return;
        }

        var button = new FlatButton
        {
            Text = Words.UpdateCardButton,
            Size = new Size(150, 34),
            Fill = Theme.Accent,
            ForeColor = Color.White,
        };

        // [BUG] This used to disable the button, relabel it "Updating…" and fire OfferUpdate without
        // waiting. Declining the dialog then left the card sitting there permanently claiming to be
        // updating, with the only way out being to close and reopen the window. The busy state has to
        // last exactly as long as the work does.
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            button.Text = Words.UpdateCardBusy;

            await OfferUpdate(ready);

            // Only reached when nothing was installed — a successful install closes this window.
            if (button.IsDisposed) return;

            button.Enabled = true;
            button.Text = Words.UpdateCardButton;
        };

        var line = new Panel
        {
            Width = RowWidth,
            Height = 40,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };

        button.Location = new Point(0, 2);
        line.Controls.Add(button);
        AddRow(card, line, 0, elbow: false);
    }

    private Panel BuildHomePage()
    {
        var page = NewPage(Words.PageHome, Words.PageHomeWhy);

        // The status card that used to sit here is gone.
        //
        // It said three things and none of them earned the space: "Ready" restated the power button
        // beside it, the sentence under it restated the page's own subtitle, and the connected-pad line
        // now lives in the title bar where it is visible from every page rather than just this one.
        // Starting and ending a session is the footer button and the PS button on the pad — both of
        // which are always to hand — so a third control for it was one too many.

        // ---- a new version, if there is one ----
        //
        // Above even the alerts. Everything else on this page describes the app you are running; this
        // is the only thing offering to change it, and it is gone the moment there is nothing to say.
        if (_pendingUpdate is { } ready) AddUpdateCard(page, ready);

        // ---- what is happening right now ----
        //
        // Above everything, and absent entirely when there is nothing to say — so the card appearing at
        // all is itself the message. Everything below it on this page is *configuration*: things chosen,
        // and therefore not news. This is the only part that reports reality, which is where the failures
        // that cannot be diagnosed from the outside actually live: a display that is not there right now,
        // a startup task that has quietly gone missing, a warning logged while you were playing.
        var alerts = Alerts();

        if (alerts.Count > 0)
        {
            var card = NewCard(page);

            foreach (var alert in alerts)
            {
                var row = new AlertRow
                {
                    Text = alert.Expandable
                           ? (_troubleOpen ? "\u25BE  " : "\u25B8  ") + alert.Message
                           : alert.Message,
                    Detail = alert.Detail,
                    Kind = alert.Kind,
                    Actionable = alert.Page >= 0 || alert.Expandable,
                    Dismissible = alert.Expandable,
                    Width = RowWidth,
                    Margin = new Padding(0),
                };

                if (alert.Expandable)
                {
                    // Opens in place rather than sending anyone to the log file. The count answers "did
                    // anything go wrong"; the list answers "what", which is the question that actually
                    // matters and the one that previously meant finding a text file in AppData.
                    row.Click += (_, _) => { _troubleOpen = !_troubleOpen; AlertsChanged(); RebuildPage(0); };

                    // Dismissing marks what has been read rather than silencing the row: anything
                    // logged after this brings it back, counted from here.
                    row.Dismissed += (_, _) =>
                    {
                        _troubleSeen = Log.TroubleCount;
                        _troubleOpen = false;
                        AlertsChanged();
                        RebuildPage(0);
                    };
                }
                else if (alert.Page >= 0)
                {
                    int target = alert.Page;
                    row.Click += (_, _) => ShowPage(target);
                }

                AddRow(card, row, 0, elbow: false);

                if (alert.Expandable && _troubleOpen) AddTroubleList(card);
            }
        }

        // ---- the one thing to know, said in pictures ----
        //
        // Top of the page, above the facts, because it is the thing that makes the whole app usable from
        // a sofa and the thing nobody discovers on their own. Both buttons are drawn rather than named:
        // "the PS or Xbox button" is a phrase people read past, while the shapes are recognised without
        // reading at all.
        var banner = NewCard(page);

        AddRow(banner, new GuideBanner
        {
            Line1 = Words.HomeGuideBannerTitle,
            Line2 = Words.HomeGuideBannerBody,
            Width = RowWidth,
            Margin = new Padding(0),
        }, 0, elbow: false);

        // The setup checklist has been removed.
        //
        // Four green ticks restating what the tiles below already say, on a page whose whole job is to
        // say those things once. It earned its place on a first run and nowhere else, and a card that
        // is only useful for the first five minutes of the app's life is a card in the way for the
        // rest of it. The alerts above cover the case it really existed for: something essential not
        // being set is a problem, and problems belong in the alerts.

        // ---- the facts ----
        //
        // Headed, where it used to start with no heading at all. A grid of twenty-odd tiles arriving
        // under the guide banner with nothing said about it has to be worked out before it can be
        // read; four words in front of it means it is understood before the first tile is.
        NewSection(page, Words.SectionGlance);

        var glance = NewCard(page);

        _homeGlance = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MaximumSize = new Size(RowWidth, 0),
            MinimumSize = new Size(RowWidth, 0),
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };

        RefreshHomeGlance();
        AddRow(glance, _homeGlance, 0, elbow: false);

        // A row of jump buttons used to sit here and has been removed.
        //
        // It duplicated the navigation rail three inches to its left — same five destinations, same
        // words, one of them clipped — which is noise rather than convenience. The rail is always on
        // screen and always in the same place; a second copy of it only adds something to read.

        // ---- the two things nobody would guess ----
        var tips = NewCard(page);

        // The guide button is the banner's job now, so only the one thing left that nobody guesses.
        AddNote(tips, Words.HomeBorderlessTip, tight: true);

        return page;
    }

    private Panel BuildGeneralPage()
    {
        var page = NewPage(Words.PageGeneral, Words.PageGeneralWhy);

        NewSection(page, Words.SectionStartup);
        var start = NewCard(page);
        // No "Default:" line: this one lives in Task Scheduler and is read back from there, so what
        // it ships as is Windows' business rather than a default this app can claim.
        AddToggle(start, Words.StartWithWindows, _startWithWindows, Words.StartWithWindowsWhy);
        AddToggle(start, Words.StartOnLaunch, _startOnLaunch, Words.StartOnLaunchWhy, setting: nameof(AppConfig.StartSessionOnLaunch));
        AddToggle(start, Words.StartOnWake, _startOnWake, Words.StartOnWakeWhy, setting: nameof(AppConfig.StartSessionOnWake));
        AddToggle(start, Words.StartOnWakeControllerOnly, _startOnWakeControllerOnly, Words.StartOnWakeControllerOnlyWhy, Indent, setting: nameof(AppConfig.StartOnWakeControllerOnly));

        AddToggle(start, Words.MinimizeOnClose, _minimizeOnClose, Words.MinimizeOnCloseWhy, setting: nameof(AppConfig.MinimizeToTrayOnClose));

        // The switch that finally does something. AppConfig.CheckForUpdates has defaulted to true since
        // it was added and nothing read it, so there was a setting on record that governed nothing.
        AddToggle(start, Words.CheckForUpdates, _checkForUpdates, Words.CheckForUpdatesWhy,
                  setting: nameof(AppConfig.CheckForUpdates));

        // The one thing the Steam page was still carrying.
        //
        // A whole tab for a single dropdown was a tab people opened once, and what it controls is not
        // really about Steam — it is what happens to a window when a session ends, which is what this
        // page is for.
        NewSection(page, Words.SectionBigPicture);
        var bigPicture = NewCard(page);

        // The explanatory note that stood here is gone, with Words.BigPictureIsSessionNote. It opened
        // a card whose only control is a dropdown about the Steam window, and four lines of background
        // on what a couch session *is* are not what somebody came to this card to find out. The one
        // fact in it worth keeping — that closing Big Picture closes the game — was only ever true of
        // the moment it happens, not of a settings page read at the desk, and the session-end prompt
        // is where a reader meets that decision with the game actually running.
        SetOptions(_steamAfter, Words.SteamAfterOptions);
        AddPick(bigPicture, Words.SteamAfter, _steamAfter, Words.SteamAfterWhy,
                Words.SteamAfterOptions[(int)Defaults.SteamAfterBigPicture]);

        // Its own section rather than a line in Notifications.
        //
        // This is not a notification. A notification tells you something happened; this stands in front
        // of something happening and refuses to let it until it is answered. Filing it with the toasts
        // would put the one setting that protects unsaved work under a heading people turn off wholesale.
        NewSection(page, Words.SectionEndingSession);
        var ending = NewCard(page);
        AddToggle(ending, Words.ConfirmClosingGame + Words.BadgeRecommended, _confirmClosingGame,
                  Words.ConfirmClosingGameWhy, setting: nameof(AppConfig.ConfirmClosingGame),
                  titleEmphasis: Theme.Good);

        NewSection(page, Words.SectionNotifications);
        var notify = NewCard(page);
        // One master, then a row for each kind the app actually shows.
        //
        // These were three siblings, which read as three unrelated switches and left no way to say
        // "none of this" in one press. Worse, the set was not the set: several notifications answered
        // to none of the three and could not be turned off from anywhere. Every toast in the app now
        // has a row here, and every one of them passes through the master.
        AddToggle(notify, Words.ShowNotifications, _showNotifications, Words.ShowNotificationsWhy,
                  setting: nameof(AppConfig.ShowNotifications));

        WhenOn(() => _showNotifications.Checked, notify, () =>
        {
            AddToggle(notify, Words.ShowSessionNotifications, _showSessionNotifications,
                      Words.ShowSessionNotificationsWhy, Indent,
                      setting: nameof(AppConfig.ShowSessionNotifications));

            AddToggle(notify, Words.ShowHdrNotifications, _showHdrNotifications,
                      Words.ShowHdrNotificationsWhy, Indent,
                      setting: nameof(AppConfig.ShowHdrNotifications));

            AddToggle(notify, Words.ShowMinimizeNotification, _showMinimizeNotification,
                      Words.ShowMinimizeNotificationWhy, Indent,
                      setting: nameof(AppConfig.ShowMinimizeNotification));

            AddToggle(notify, Words.ShowProblemNotifications, _showProblemNotifications,
                      Words.ShowProblemNotificationsWhy, Indent,
                      setting: nameof(AppConfig.ShowProblemNotifications));

            AddToggle(notify, Words.ShowActionNotifications, _showActionNotifications,
                      Words.ShowActionNotificationsWhy, Indent,
                      setting: nameof(AppConfig.ShowActionNotifications));
        });

        AddPageReset(page, GeneralPage);

        return page;
    }

    private Panel BuildAboutPage()
    {
        var page = NewPage(Words.PageAbout, Words.PageAboutWhy);

        NewSection(page, Words.SectionAbout);
        var card = NewCard(page);
        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(RowWidth, 0),
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };

        stack.Controls.Add(new Label
        {
            Text = AppInfo.Name,
            Font = Theme.Heading,
            ForeColor = Theme.Text,
            AutoSize = true,
            BackColor = Color.Transparent,
        });
        stack.Controls.Add(new Label
        {
            Text = string.Format(Words.AboutVersion, AppInfo.DisplayVersion, AppInfo.Author),
            ForeColor = Theme.TextDim,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 10),
            BackColor = Color.Transparent,
        });
        stack.Controls.Add(new Label
        {
            Text = Words.AboutBlurb,
            ForeColor = Theme.TextDim,
            AutoSize = true,
            // Wraps to the column, not to a fixed 560. The window is far wider than that now,
            // so a hard cap left the paragraph in a narrow strip with the rest of the page
            // empty beside it.
            MaximumSize = new Size(RowWidth, 0),
            Margin = new Padding(0, 0, 0, 14),
            BackColor = Color.Transparent,
        });

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 8),
        };

        var openFolder = new FlatButton { Text = "Open settings folder", Size = new Size(158, 34) };
        openFolder.Click += (_, _) => OpenDataFolder();
        openFolder.Margin = new Padding(0, 0, 8, 0);

        var diag = new FlatButton { Text = "Save diagnostics", Size = new Size(140, 34) };
        diag.Click += (_, _) => SaveDiagnosticsReport();
        diag.Margin = new Padding(0, 0, 8, 0);

        var repo = new FlatButton { Text = Words.OpenRepository, Size = new Size(150, 34) };
        repo.Click += (_, _) => OpenExternally(AppInfo.RepositoryUrl);
        repo.Margin = new Padding(0, 0, 8, 0);

        buttons.Controls.AddRange([openFolder, diag, repo]);
        stack.Controls.Add(buttons);

        // Added to the stack, not the card: the card's rows are laid out in the order they are
        // added, and the stack itself goes in last — so anything added to the card here would
        // appear above the version heading instead of under the buttons it explains.
        stack.Controls.Add(new RichNote(Words.ReportBugWhy, RowWidth, Theme.Small,
                                        Theme.SmallBold, Theme.Info, Theme.Info)
        {
            Margin = new Padding(0, 0, 0, 12),
        });

        BuildUpdateRow(stack);

        stack.Controls.Add(new Label
        {
            Text = AppConfig.Directory,
            ForeColor = Theme.TextFaint,
            Font = Theme.Small,
            AutoSize = true,
            BackColor = Color.Transparent,
        });

        Rows(card).Controls.Add(stack);
        return page;
    }

    // ================= page building blocks =================

    /// <summary>A dropdown in the app's own style. See <see cref="Dropdown"/>.</summary>
    private static Dropdown NewDropdown() => new();

    /// <summary>Label that does not eat ampersands. "Display &amp; Audio" needs this.</summary>
    private static Label NewLabel(string text, Font font, Color colour, int maxWidth = 0) => new()
    {
        Text = text,
        Font = font,
        ForeColor = colour,
        AutoSize = true,
        UseMnemonic = false,
        MaximumSize = maxWidth > 0 ? new Size(maxWidth, 0) : Size.Empty,
        Margin = new Padding(0),
        BackColor = Color.Transparent,
    };

    private Panel NewPage(string title, string subtitle)
    {
        var page = new ScrollHost
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Window,
            Padding = new Padding(30, 22, 30, 18),
        };

        // Not docked. A docked child contributes nothing to its parent's preferred size, so
        // docking inside an auto-sizing chain makes the parent collapse to its padding — which
        // is exactly what turned every card into a thin vertical sliver. Width is pinned with
        // MinimumSize instead, which auto-sizing does respect.
        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(ContentWidth, 0),
            BackColor = Theme.Window,
            Name = "stack",
            Margin = new Padding(0),
        };

        var heading = NewLabel(title, Theme.Title, Theme.Text);
        heading.Margin = new Padding(2, 0, 0, 3);
        stack.Controls.Add(heading);

        if (subtitle.Length > 0)
        {
            var sub = NewLabel(subtitle, Theme.Body, Theme.TextDim, ContentWidth - 24);
            sub.Margin = new Padding(3, 0, 0, 16);
            stack.Controls.Add(sub);
        }

        page.SetContent(stack);
        return page;
    }

    private static FlowLayoutPanel Stack(Panel page) => (FlowLayoutPanel)page.Controls["stack"]!;

    /// <summary>
    /// A card that sizes itself to its rows.
    ///
    /// The previous version computed heights by hand from measured text, which silently
    /// clipped or left gaps whenever a description wrapped differently than estimated. Letting
    /// the layout engine do it removes that whole class of bug.
    /// </summary>
    private Card NewCard(Panel page)
    {
        // Sized by hand rather than by AutoSize.
        //
        // AutoSize computes a preferred width from the children and then applies Padding to it,
        // so the inset depends on what the children happen to measure — which is why the gap
        // never matched CardPad and the content sat almost against the border. Placing the row
        // stack at an explicit offset makes the padding literal: it is the position, not a hint.
        var card = new Card
        {
            AutoSize = false,
            Width = ContentWidth,
            Margin = new Padding(0, 0, 0, 14),
            Pad = CardPad,
        };

        // Buffered, like the rows it holds. This stack repaints whenever a row is hovered, shown or
        // hidden, and an unbuffered transparent container paints its parent and then itself — two
        // passes over the same pixels, which is what a flicker is.
        var rows = new BufferedStack
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(RowWidth, 0),
            MaximumSize = new Size(RowWidth, 0),
            Name = "rows",
            Location = new Point(CardPad, CardPad),
            Margin = new Padding(0),
        };

        // The card is exactly its content plus an equal inset all round, recomputed whenever a
        // description wraps to a different number of lines.
        void Fit() => card.Height = rows.Bottom + CardPad;
        rows.SizeChanged += (_, _) => Fit();

        card.Controls.Add(rows);
        Fit();
        Stack(page).Controls.Add(card);
        return card;
    }

    private static FlowLayoutPanel Rows(Card card) => (FlowLayoutPanel)card.Controls["rows"]!;

    /// <summary>
    /// Build rows that only show while <paramref name="when"/> holds.
    ///
    /// Captured by position rather than by naming each control: whatever the block adds — a picker,
    /// a slider, the divider in front of it — is the thing to hide, and listing them by hand is a
    /// list that goes stale the first time a row is added to the block and not to the list.
    ///
    /// Nests. An inner group registered inside an outer one is applied after it, so a child of a
    /// child answers to both without either needing to know about the other.
    /// </summary>
    private void WhenOn(Func<bool> when, Card card, Action build)
        => WhenOn(when, Rows(card), build);

    /// <summary>
    /// The same, for whole cards and their headings rather than rows inside one.
    ///
    /// Grouping a long card into several short ones only helps if the headings come and go with the
    /// cards they name — a heading left behind over nothing reads as a section that failed to load.
    /// </summary>
    private void WhenOn(Func<bool> when, Control container, Action build)
    {
        var rows = container;
        int before = rows.Controls.Count;

        // Where this group will sit in the list, taken before the block runs.
        //
        // A group nested inside this one registers itself during build(), so appending afterwards
        // would put the parent *after* its own child — and since the parent's rows include the
        // child's, the parent would then re-show rows the child had just hidden. That is not
        // theoretical: it left the hold-button picker visible and greyed with hold switched off,
        // which is the exact thing hiding was meant to replace.
        int at = _nested.Count;

        build();

        var added = new List<Control>();
        for (int i = before; i < rows.Controls.Count; i++) added.Add(rows.Controls[i]);

        _nested.Insert(at, (when, added));
    }

    /// <summary>
    /// An uppercase group label above a card.
    ///
    /// Cards alone gave no clue why two settings were together — the grouping was there but
    /// unlabelled, so a page read as an undifferentiated run of switches. Naming each group is
    /// what makes a long page scannable.
    /// </summary>
    private void NewSection(Panel page, string title)
    {
        var caption = NewLabel(title.ToUpperInvariant(), Theme.Caption, Theme.TextFaint);
        caption.Margin = new Padding(4, 6, 0, 7);
        Stack(page).Controls.Add(caption);
    }

    /// <summary>
    /// Add a row, preceded by a divider once there is something to divide it from.
    ///
    /// Whitespace alone stopped working as soon as rows carried two lines of description: the
    /// gap inside a row and the gap between rows looked the same, so it was ambiguous which
    /// description belonged to which control.
    /// </summary>
    /// <param name="elbow">
    /// False for a row that continues the nested setting above it rather than being a nested
    /// setting itself — a note explaining the dropdown it sits under, for instance. It lines up
    /// with that setting but draws no second elbow, because there is nothing new to join.
    /// </param>
    private void AddRow(Card card, Control row, int indent, bool elbow = true)
    {
        var rows = Rows(card);

        // No divider above a nested row: the elbow already shows what it belongs to, and a rule
        // across the gap would cut the connection it is drawing.
        if (rows.Controls.Count > 0 && indent == 0)
            rows.Controls.Add(new Hairline(RowWidth, indent) { Margin = new Padding(indent, 0, 0, 12) });

        if (indent > 0 && elbow)
        {
            rows.Controls.Add(new IndentRow(row, indent, RowWidth)
            {
                Margin = new Padding(0, 14, 0, 12),
            });
            return;
        }

        row.Margin = new Padding(indent, 0, 0, 12);
        rows.Controls.Add(row);
    }

    /// <summary>
    /// Title and explanation on the left, switch pinned right.
    ///
    /// <paramref name="indent"/> marks a setting that only applies while the one above it is on.
    /// Stepping those in shows the dependency directly, instead of leaving the user to infer it
    /// from a row greying out after they toggle something.
    /// </summary>
    /// <summary>
    /// A fresh config, purely to read shipped defaults from.
    ///
    /// Read from an actual AppConfig rather than repeated in the wording, so a default can
    /// never be changed in one place and left stale in the other — the label is derived from
    /// the same property the app itself uses.
    /// </summary>
    private static readonly AppConfig Defaults = new();

    /// <summary>The shipped value of a bool setting, by property name.</summary>
    private static bool DefaultOf(string setting)
        => typeof(AppConfig).GetProperty(setting)?.GetValue(Defaults) as bool? ?? false;

    /// <param name="titleEmphasis">
    /// Colour for **emphasis** in the title. Blue unless a caller says otherwise, which one does:
    /// the Steam audio restart is the only setting here with a real reason to be warned off, so
    /// it is the only one that gets red. Passed per row rather than made the default again,
    /// because when most emphasis is red none of it reads as a warning.
    /// </param>
    /// <param name="rowClick">
    /// Whether clicking anywhere on the row flips the switch. True for every ordinary setting; false
    /// for the two that change Windows security, where a click landing slightly wide of where it was
    /// aimed must not be enough to turn the firewall off.
    /// </param>
    private void AddToggle(Card card, string title, ToggleSwitch toggle, string description,
                           int indent = 0, string? setting = null, Color? titleEmphasis = null,
                           Func<CheckResult>? check = null, bool checkAlways = false,
                           bool rowClick = true)
    {
        // Buffered: this row and the stack inside it are repainted on every pointer move now that a
        // hover wash sits behind them, and a transparent container that is not buffered paints its
        // parent and then itself where the eye can see both.
        var row = new BufferedTable
        {
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(RowWidth - indent, 0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ToggleColumn));

        int textWidth = RowWidth - indent - ToggleColumn - 12;

        var heading = new RichNote(title, textWidth, Theme.BodySemi, Theme.BodyBold, Theme.Text,
                                   titleEmphasis);

        // RichNote rather than a Label, so **emphasis** in the wording is honoured.
        var detail = new RichNote(description, textWidth);

        var text = new BufferedStack
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
        };

        text.Controls.Add(heading);
        text.Controls.Add(detail);

        // What this setting ships as, so it is obvious which way is "untouched" without having
        // to reset anything to find out.
        if (setting is not null)
        {
            var note = NewLabel(string.Format(Words.DefaultIs,
                                              DefaultOf(setting) ? Words.DefaultOn : Words.DefaultOff),
                                Theme.Caption, Theme.TextFaint);
            note.Margin = new Padding(0, 5, 0, 0);
            text.Controls.Add(note);
        }

        // The inline check result, shown right under the setting it belongs to. The note is remade
        // with the row on every rebuild but is looked up by title, so anything kept from before is
        // shown again and the wired handler always writes to the current one.
        if (check is not null)
        {
            var checkNote = new RichNote("", textWidth) { Visible = false, Margin = new Padding(0, 6, 0, 0) };
            text.Controls.Add(checkNote);
            _toggleNotes[title] = checkNote;

            void Run()
            {
                try { ShowToggleNote(title, check()); }
                catch (Exception ex) { Log.Warn($"Inline check for {title} failed: {ex.Message}"); }
            }

            if (checkAlways)
            {
                // A live system state (UAC, firewall): show what it is now, on or off. Its own
                // toggle handler and the page-shown refresh keep the note current after that, so
                // there is no CheckedChanged wired here — which also sidesteps any ordering race
                // with the handler that actually applies the change.
                Run();
            }
            else
            {
                // A capability check: run it when the setting is switched on, clear it when off.
                if (_toggleChecks.TryGetValue(title, out var kept)) ShowToggleNote(title, kept);

                if (_checkWired.Add(toggle))
                    toggle.CheckedChanged += (_, _) =>
                    {
                        if (_loading) return;
                        if (toggle.Checked) Run();
                        else HideToggleNote(title);
                    };
            }
        }

        row.Controls.Add(text, 0, 0);
        row.RowCount = 1;

        // Right-anchored and row-spanning, so the switch lines up with every other switch on the
        // page and sits centred against however many lines the description wraps to.
        toggle.Anchor = AnchorStyles.Right;
        toggle.Margin = new Padding(12, 0, 0, 0);
        row.Controls.Add(toggle, 1, 0);

        _tips.SetToolTip(toggle, Wrapped(Plain(description)));
        AddRow(card, row, indent);

        // After AddRow, deliberately: at an indent the row is wrapped in an IndentRow, and the band
        // to light up is the wrapper the stack actually holds, not the table inside it.
        if (rowClick) RowActsAsToggle(card, row, toggle);
    }

    /// <summary>
    /// Whether to confirm something the user just pressed.
    ///
    /// Read from the live toggles rather than from Config, so turning notifications off silences the
    /// very next confirmation instead of waiting for the debounced save to land.
    /// </summary>
    private bool Actions()
        => _showNotifications.Checked && _showActionNotifications.Checked;

    /// <summary>A leading tick or cross for a check line, matching the old check rows.</summary>
    private static string Mark(bool worked) => worked ? "✓  " : "✗  ";

    /// <summary>Show a check result under its toggle, green if it worked, red if it did not.</summary>
    private void ShowToggleNote(string title, CheckResult result)
    {
        _toggleChecks[title] = result;

        if (_toggleNotes.TryGetValue(title, out var note))
        {
            note.ForeColor = result.Worked ? Theme.Good : Theme.Bad;
            note.SetMarkup(Mark(result.Worked) + result.Detail);
            note.Visible = true;
        }
    }

    private void HideToggleNote(string title)
    {
        _toggleChecks.Remove(title);
        if (_toggleNotes.TryGetValue(title, out var note)) note.Visible = false;
    }

    /// <summary>Show a plain message under a toggle — used when the app itself just made the change,
    /// so it reads "Couch Session did this" rather than "reflecting your setting".</summary>
    private void ShowToggleMessage(string title, string markup, Color colour)
    {
        if (_toggleNotes.TryGetValue(title, out var note))
        {
            note.ForeColor = colour;
            note.SetMarkup(Mark(colour != Theme.Bad) + markup);
            note.Visible = true;
        }
    }

    private void OnPerformancePageShown(object? sender, EventArgs e)
    {
        if (sender is Control { Visible: true }) RefreshSecurityState();
    }

    /// <summary>
    /// Re-read UAC and the firewall from Windows and reflect them on the switches and their notes.
    ///
    /// Called at build (so it is right the moment settings opens), when the Performance page is
    /// shown, and whenever the window regains focus — that last one is what catches a change made
    /// in Windows itself while Couch Session was in the background, which no event of ours would see
    /// otherwise. Safe to call before the page exists: the switches always do, and the notes are
    /// looked up and skipped if not built yet.
    /// </summary>
    /// <summary>
    /// Put every switch that mirrors a Windows setting back in step with Windows.
    ///
    /// [BUG] Only UAC and the firewall were re-read here, because for a long time they were the only
    /// two switches that mirrored anything. Game Mode and the Game Bar became mirrors as well and
    /// were not added, so each read the real state exactly once — when the window was built — and
    /// then held that answer for as long as the window lived. Changing Game Mode in Windows and
    /// coming back to this page showed the old state with complete confidence, which is worse than
    /// showing nothing: the entire argument for reading these live is that they cannot be wrong.
    ///
    /// Named for what it does now rather than for security, since half of what it reconciles is not
    /// a security setting.
    /// </summary>
    private void RefreshSecurityState()
    {
        SetUacQuietly(!UacControl.IsEnabled());
        SetFirewallQuietly(!FirewallControl.IsEnabled());

        SetQuietly(_enableGameMode, GameModeControl.IsEnabled());
        SetQuietly(_disableGameBarButton, !GameBarControl.IsEnabled());

        RefreshSecurityNote(Words.DisableUac, PerformanceCheck.CheckUac);
        RefreshSecurityNote(Words.DisableFirewall, PerformanceCheck.CheckFirewall);
    }

    /// <summary>When the app last changed each security setting itself, so the reflecting note does
    /// not immediately overwrite its own "Couch Session just did this" line.</summary>
    private readonly Dictionary<string, DateTime> _securityChangedAt = [];

    private void RefreshSecurityNote(string title, Func<CheckResult> check)
    {
        // The elevation prompt makes the window lose and regain focus, so an OnActivated lands
        // right after a change the user made through the toggle. Holding the just-made message for
        // a few seconds stops that refocus replacing "Couch Session turned it off" with "this is
        // your existing Windows setting" — which reads as the app denying it did anything.
        if (_securityChangedAt.TryGetValue(title, out var at)
            && DateTime.UtcNow - at < TimeSpan.FromSeconds(8))
            return;

        ShowToggleNote(title, check());
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // Only once the form is up and its controls exist. OnActivated can fire during creation.
        if (IsHandleCreated && !IsDisposed && !_loading) RefreshSecurityState();
    }

    /// <summary>
    /// Title, explanation, and a button that does something now rather than changing a setting.
    ///
    /// Shaped like the rows around it so it does not read as a foreign object in a list of
    /// settings, but it saves nothing — pressing it is the whole effect.
    /// </summary>
    private void AddAction(Card card, string title, string description, string button,
                           Action onClick, int indent = 0)
    {
        int width = RowWidth - indent;

        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(width, 0),
            BackColor = Color.Transparent,
        };

        row.Controls.Add(new RichNote(title, width, Theme.BodySemi, Theme.BodyBold, Theme.Text));
        row.Controls.Add(new RichNote(description, width) { Margin = new Padding(0, 2, 0, 8) });

        var press = new FlatButton { Text = button, Size = new Size(180, 34), Margin = new Padding(0) };
        press.Click += (_, _) => onClick();

        row.Controls.Add(press);

        AddRow(card, row, indent, elbow: false);
    }

    private void SetUacQuietly(bool disabled)
    {
        _settingUac = true;
        _disableUac.Checked = disabled;
        _settingUac = false;
    }

    private void OnGameBarToggled(object? sender, EventArgs e)
    {
        if (_loading) return;

        // The switch says "switch the Game Bar off", so its on state is the Game Bar's off state.
        bool wantsBarOn = !_disableGameBarButton.Checked;

        if (GameBarControl.Set(wantsBarOn))
        {
            MarkDirty();
            ShowFooterNote(wantsBarOn ? Words.NoticeGameBarOn : Words.NoticeGameBarOff);
            return;
        }

        SetQuietly(_disableGameBarButton, !GameBarControl.IsEnabled());
        Warn(Words.WarnGameBarFailed);
    }

    /// <summary>Set a switch without its handler firing — these reconcile, they do not act.</summary>
    private void SetQuietly(ToggleSwitch toggle, bool on)
    {
        bool was = _loading;
        _loading = true;

        try { toggle.Checked = on; }
        finally { _loading = was; }
    }

    /// <summary>Writing this to Windows is the whole action; there is nothing to save later.</summary>
    private void OnGameModeToggled(object? sender, EventArgs e)
    {
        if (_loading) return;

        bool wanted = _enableGameMode.Checked;

        if (GameModeControl.Set(wanted))
        {
            MarkDirty();
            ShowFooterNote(wanted ? Words.NoticeGameModeOn : Words.NoticeGameModeOff);
            return;
        }

        // Windows would not have it. Put the switch back rather than leaving it claiming something
        // that is not so — the whole point of reading this setting live is that it cannot lie.
        SetQuietly(_enableGameMode, GameModeControl.IsEnabled());
        Warn(Words.WarnGameModeFailed);
    }

    private void OnUacToggled(object? sender, EventArgs e)
    {
        if (_settingUac) return;

        // Checked means "UAC disabled", so UAC-enabled is the opposite of the switch.
        bool applied = UacControl.SetEnabled(!_disableUac.Checked);

        // Match the switch to what the registry now says — reverting it if the user dismissed the
        // elevation prompt or the change did not go through.
        SetUacQuietly(!UacControl.IsEnabled());

        if (applied)
        {
            // Checked means UAC is now off — the couch-friendly state, green tick. Unchecked means
            // it is back on, red cross.
            bool off = _disableUac.Checked;
            var msg = off ? Words.NoticeUacRestart : Words.NoticeUacOn;
            if (Actions()) Toast.Show(Words.NoticeUacChanged, msg, Theme.Warn);
            _securityChangedAt[Words.DisableUac] = DateTime.UtcNow;
            ShowToggleMessage(Words.DisableUac, msg, off ? Theme.Good : Theme.Bad);
        }
        else
        {
            ShowToggleNote(Words.DisableUac, PerformanceCheck.CheckUac());   // dismissed — just reflect
        }
    }

    private void SetFirewallQuietly(bool disabled)
    {
        _settingFirewall = true;
        _disableFirewall.Checked = disabled;
        _settingFirewall = false;
    }

    private void OnFirewallToggled(object? sender, EventArgs e)
    {
        if (_settingFirewall) return;

        // Checked means "firewall disabled", so firewall-on is the opposite of the switch.
        bool applied = FirewallControl.SetEnabled(!_disableFirewall.Checked);

        SetFirewallQuietly(!FirewallControl.IsEnabled());

        if (applied)
        {
            bool off = _disableFirewall.Checked;
            var msg = off ? Words.NoticeFirewallOff : Words.NoticeFirewallOn;
            if (Actions()) Toast.Show(Words.NoticeFirewallChanged, msg, Theme.Warn);
            _securityChangedAt[Words.DisableFirewall] = DateTime.UtcNow;
            ShowToggleMessage(Words.DisableFirewall, msg, off ? Theme.Good : Theme.Bad);
        }
        else
        {
            ShowToggleNote(Words.DisableFirewall, PerformanceCheck.CheckFirewall());
        }
    }

    /// <summary>Title, explanation, then a full-width dropdown.</summary>
    /// <returns>
    /// The title, so a caller that needs it to change with the state of other controls can keep
    /// hold of it. Ignored by every other caller.
    /// </returns>
    private RichNote AddPick(Card card, string title, Dropdown combo, string description,
                             string? defaultOption = null, Control? action = null, int indent = 0)
    {
        // The text has to be told the narrower width, not just moved. RichNote wraps to the
        // width it is given, so leaving it at the full RowWidth would push an indented
        // description past the right edge of the card.
        int width = RowWidth - indent;

        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(width, 0),
            BackColor = Color.Transparent,
        };

        var heading = new RichNote(title, width, Theme.BodySemi, Theme.BodyBold, Theme.Text);
        row.Controls.Add(heading);

        var desc = new RichNote(description, width) { Margin = new Padding(0, 2, 0, 7) };
        row.Controls.Add(desc);

        // The shipped choice, named rather than numbered. Device pickers pass nothing, because
        // "whatever Windows called your HDMI output" is not a default worth printing.
        if (defaultOption is { } option)
        {
            desc.Margin = new Padding(0, 2, 0, 3);

            var note = NewLabel(string.Format(Words.DefaultIs, option), Theme.Caption, Theme.TextFaint);
            note.Margin = new Padding(0, 0, 0, 7);
            row.Controls.Add(note);
        }

        // Capped, not full width.
        //
        // A dropdown stretched the entire row was tolerable at 780 and looks absurd at 940: the
        // longest thing any of these holds is a device name, and a control four times wider
        // than its content reads as a mistake. It also puts the chevron a very long way from
        // the text it belongs to.
        combo.Size = new Size(Math.Min(width, DropdownWidth), 34);
        combo.Margin = new Padding(0);

        if (action is null)
        {
            row.Controls.Add(combo);
        }
        else
        {
            // The dropdown and its button share a line, so the button reads as belonging to
            // that setting rather than floating below it.
            var line = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0),
                BackColor = Color.Transparent,
            };

            combo.Margin = new Padding(0, 0, 8, 0);
            line.Controls.Add(combo);
            line.Controls.Add(action);

            row.Controls.Add(line);
        }

        _tips.SetToolTip(combo, Wrapped(Plain(description)));
        AddRow(card, row, indent);

        return heading;
    }

    /// <summary>
    /// Title and explanation, then a drag slider flanked by a "slower" and "faster" caption.
    ///
    /// The captions sit on the same line as the track, one at each end, so the direction of the
    /// drag needs no explaining — pull toward the word you want. The slider stretches to fill the
    /// space between them, capped like the dropdowns so it does not run the full absurd width of
    /// the card.
    /// </summary>
    /// <param name="format">
    /// How the current value reads. Both NN/g and Microsoft's own guidance say to show it: a thumb
    /// position tells you where you are relative to the ends and nothing else, so it cannot be
    /// described, compared against a default, or returned to deliberately.
    /// </param>
    private void AddSlider(Card card, string title, Slider slider, string description,
                           string lowLabel, string highLabel, int min, int max, int defaultValue,
                           int indent = 0, Func<int, string>? format = null)
    {
        int width = RowWidth - indent;

        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(width, 0),
            BackColor = Color.Transparent,
        };

        row.Controls.Add(new RichNote(title, width, Theme.BodySemi, Theme.BodyBold, Theme.Text));
        row.Controls.Add(new RichNote(description, width) { Margin = new Padding(0, 2, 0, 7) });

        var low = NewLabel(lowLabel, Theme.Caption, Theme.TextFaint);
        var high = NewLabel(highLabel, Theme.Caption, Theme.TextFaint);

        // A small button to put the slider back where it shipped. Setting Value raises the change
        // event, so it marks the form dirty and saves like any other move of the thumb.
        var reset = new FlatButton
        {
            Text = Words.MouseSpeedDefault,
            // 34 to match every other button in the window. At 26 this was the smallest target on
            // the page, and it is the one a user reaches for precisely when the pointer speed they
            // just set has made the pointer hard to aim.
            Size = new Size(104, 34),
            Anchor = AnchorStyles.None,
            Font = Theme.Caption,
        };
        reset.Click += (_, _) => slider.Value = defaultValue;
        _tips.SetToolTip(reset, Wrapped(string.Format(Words.SliderResetTip, Show(defaultValue))));

        string Show(int v) => format?.Invoke(v) ?? v.ToString();

        // Sat at the end of the row rather than centred beneath the track.
        //
        // The guidance that puts it below is written against thumb occlusion — a finger or the thumb
        // itself covering the number at the moment it changes. Neither applies here: nothing covers
        // the end of the row, and putting both sliders' readouts in the same column lets the two be
        // compared down the page instead of hunted for under each track.
        var readout = NewLabel(Show(slider.Value), Theme.Caption, Theme.Text);
        readout.TextAlign = ContentAlignment.MiddleRight;
        readout.AutoSize = false;
        readout.Size = new Size(58, 20);
        readout.Anchor = AnchorStyles.None;

        slider.ValueChanged += (_, _) => readout.Text = Show(slider.Value);

        // Fixed size, not AutoSize: the middle column is a percentage, and a table sized to its
        // content would give that column nothing to stretch into and collapse the track to a dot.
        int barWidth = Math.Min(width, DropdownWidth);
        int lineWidth = Math.Min(width, barWidth + 10 + reset.Width + readout.Width);
        var line = new TableLayoutPanel
        {
            ColumnCount = 5,
            RowCount = 1,
            AutoSize = false,
            Margin = new Padding(0),
            Width = lineWidth,
            Height = 32,
            BackColor = Color.Transparent,
        };
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        line.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        slider.Minimum = min;
        slider.Maximum = max;
        slider.Margin = new Padding(8, 0, 8, 0);
        slider.Dock = DockStyle.Fill;

        low.Anchor = AnchorStyles.None;
        high.Anchor = AnchorStyles.None;
        reset.Margin = new Padding(10, 0, 0, 0);

        line.Controls.Add(low, 0, 0);
        line.Controls.Add(slider, 1, 0);
        line.Controls.Add(high, 2, 0);
        line.Controls.Add(readout, 3, 0);
        line.Controls.Add(reset, 4, 0);

        row.Controls.Add(line);

        _tips.SetToolTip(slider, Wrapped(Plain(description)));
        AddRow(card, row, indent);
    }

    /// <summary>
    /// Width of a settings card, and so of the window's content area.
    ///
    /// Decided once, from the screen the window opens on, and fixed from then on.
    ///
    /// It has to be a variable rather than a constant because 940 does not fit a 1366-wide
    /// laptop, and a window wider than the screen is worse than a cramped one. Every row, card
    /// and column is built from this, so choosing it before anything is constructed means the
    /// whole layout comes out right for the display — no reflowing afterwards.
    ///
    /// Reflowing afterwards was tried and is why this exists: cards derive their height from
    /// their rows, and reassigning widths across a built tree collapsed that chain and left
    /// every card at zero height. Deciding up front avoids the problem rather than managing it.
    /// </summary>
    /// <summary>The width the pages are currently built for. Changes when the window is resized.</summary>
    private int _contentWidth = ChooseContentWidth();

    private int ContentWidth => _contentWidth;

    /// <summary>Widest the content is ever drawn, on a large display.</summary>
    private const int PreferredContentWidth = 940;

    /// <summary>Narrowest it will go before the window simply becomes scrollable.</summary>
    private const int MinimumContentWidth = 560;

    private static int ChooseContentWidth()
    {
        try
        {
            // The primary screen, because that is where the window is centred.
            int work = Screen.PrimaryScreen?.WorkingArea.Width ?? 1920;

            // Leave room for the rail, the page padding and a little air either side, so the
            // window never fills the display edge to edge.
            int room = work - RailWidth - 60 - 80;

            return Math.Clamp(room, MinimumContentWidth, PreferredContentWidth);
        }
        catch
        {
            return PreferredContentWidth;
        }
    }

    /// <summary>Reserved width for the switch column, so every toggle aligns down the page.</summary>
    private const int ToggleColumn = 56;

    /// <summary>Widest a dropdown is allowed to get, however wide the window is.</summary>
    private const int DropdownWidth = 430;

    /// <summary>Step for a setting that depends on the one above it.</summary>
    private const int Indent = 26;

    /// <summary>Shortest the games list is allowed to get. Also what the window is sized to.</summary>
    private const int MinListHeight = 200;

    /// <summary>One height for every control in the games toolbar, dropdown included.</summary>
    private const int HeaderHeight = 30;

    /// <summary>Breathing room between a card's border and its contents.</summary>
    private const int CardPad = 22;

    /// <summary>Usable width inside a card.</summary>
    private int RowWidth => _contentWidth - CardPad * 2;

    // ================= data =================

    private void LoadDevices()
    {
        try
        {
            _tvDisplay.Items.Clear();

            // A display that is switched off is still a display you own, and still the one you
            // want to configure — usually more so, since setting this up at your desk with the
            // television cold is the normal case. Marked and dimmed, never removed. The device
            // path is left alone so the saved choice still matches.
            foreach (var d in DisplayManager.ListDisplays())
            {
                // A switched-off display is marked disconnected so it is clear it is not currently
                // usable, but still selectable — setting the TV up cold is the normal case.
                string suffix = !d.IsActive ? Words.DisplayDisconnected : "";

                _tvDisplay.Items.Add(suffix.Length == 0
                    ? d
                    : d with { FriendlyName = $"{d.FriendlyName} {suffix}" });
            }

            if (_tvDisplay.Items.Count == 0) _tvDisplay.Items.Add(Words.NoDisplaysFound);

            // Includes unplugged endpoints — a TV's HDMI audio while it is switched off — plus any
            // device seen before that is not present at all right now, so the couch audio device
            // stays selectable with the TV cold. Present devices refresh the remembered list.
            var audio = AudioDevicesForPicker();

            _tvAudio.Items.Clear();
            foreach (var a in audio) _tvAudio.Items.Add(a);

            _desktopAudio.Items.Clear();
            _desktopAudio.Items.Add(AutomaticAudio);
            foreach (var a in audio) _desktopAudio.Items.Add(a);
        }
        catch (Exception ex)
        {
            Log.Error("Reading devices failed", ex);
        }
    }

    /// <summary>
    /// The audio devices to show in the pickers: everything present now (including a TV's HDMI audio
    /// while it is unplugged), plus anything seen before that is gone entirely, marked off. Present
    /// devices refresh the remembered list so it never goes stale for long.
    /// </summary>
    private List<AudioDeviceInfo> AudioDevicesForPicker()
    {
        var live = AudioManager.ListPlaybackDevicesIncludingOff().ToList();

        foreach (var d in live)
            Config.KnownAudioDevices[d.Id] = d.FriendlyName;

        var present = new HashSet<string>(live.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);

        // Remembered devices that are not present at all right now — a TV the driver dropped rather
        // than merely unplugged, or one last seen in a previous run.
        foreach (var (id, name) in Config.KnownAudioDevices)
            if (present.Add(id))
                live.Add(new AudioDeviceInfo(id, name, false));

        // The configured couch device, even if an older config never remembered it, so the current
        // choice is never dropped just because the TV is off.
        if (Config.TvAudioDeviceId is { Length: > 0 } tvId && present.Add(tvId))
            live.Add(new AudioDeviceInfo(tvId, Config.TvAudioDeviceName, false));

        return live
            .OrderByDescending(d => d.IsActive)
            .ThenBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .Select(d => d.IsActive ? d : d with { FriendlyName = $"{d.FriendlyName} {Words.AudioDeviceOff}" })
            .ToList();
    }

    private static string PlainAudioName(string name) =>
        name.Replace(Words.AudioDeviceOff, "").TrimEnd();

    private void LoadGames()
    {
        try
        {
            foreach (var saved in Config.HdrGames)
                _checkedGames.Add(saved.InstallDir.TrimEnd('\\').ToLowerInvariant());

            _games = GameLibrary.DiscoverAll().ToList();

            // Keep hand-added games that automatic discovery cannot see.
            foreach (var saved in Config.HdrGames)
            {
                if (_games.Any(g => string.Equals(g.InstallDir.TrimEnd('\\'), saved.InstallDir.TrimEnd('\\'),
                                                  StringComparison.OrdinalIgnoreCase)))
                    continue;

                _games.Add(new GameEntry(saved.Name, saved.InstallDir, GameSource.Manual));
            }

            RebuildGameList();

            // Covers the periodic rescan as well as startup, so a game installed while the
            // window is open is checked without waiting for a restart. Anything already known
            // is skipped inside, so this is free after the first pass.
            HdrDatabase.RefreshInBackground(_games);
        }
        catch (Exception ex)
        {
            Log.Error("Listing games failed", ex);
        }
    }

    /// <summary>
    /// Tick every game known to render HDR natively, or untick them all if they are already on.
    ///
    /// One button rather than two, because the two states are mutually exclusive and a pair of
    /// buttons would leave one of them doing nothing most of the time.
    /// </summary>
    private void ToggleNativeHdrGames()
    {
        var before = new HashSet<string>(_checkedGames, StringComparer.OrdinalIgnoreCase);

        var native = _games.Where(g => SupportOf(g) == HdrSupport.Native).ToList();

        if (native.Count == 0)
        {
            Warn(Words.WarnNoNativeGames);
            return;
        }

        bool allOn = native.All(g => _checkedGames.Contains(g.Key));

        foreach (var game in native)
        {
            if (allOn) _checkedGames.Remove(game.Key);
            else _checkedGames.Add(game.Key);
        }

        UpdateHdrBulkLabel();
        RebuildGameList(keepView: true);
        MarkDirty();

        OfferUndo(before, allOn ? "native turned off" : "native turned on");
    }

    /// <summary>
    /// A game's HDR support: your own tag if you have set one, otherwise the database.
    /// </summary>
    private HdrSupport SupportOf(GameEntry game) =>
        Config.HdrTags.TryGetValue(game.Key, out var tag) ? (HdrSupport)tag : HdrDatabase.Lookup(game);

    /// <summary>
    /// Name the displays this page can act on, and say whether each can do HDR at all.
    ///
    /// Both, rather than just the primary. The primary display while anyone is reading this is the
    /// monitor in front of them, and the display the page is really about is the television — so a
    /// warning about "your display" would routinely be about the wrong screen, and be wrong in the
    /// direction that makes a working feature look broken.
    ///
    /// A television that is switched off is reported as unknown, never as unsupported. It has said
    /// nothing about HDR; nobody has been able to ask it, and printing a guess as a fact is exactly
    /// what this app does not do.
    /// </summary>
    private void RefreshHdrDisplayNote()
    {
        if (_hdrDisplayNote is null) return;

        try
        {
            string? primary = DisplayManager.CurrentPrimaryDevicePath();
            string couch = Config.TvDisplayPath;

            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in DisplayManager.ListDisplays()) names[d.DevicePath] = d.FriendlyName;

            // One line when the television is already the main display — during a session, or for
            // anyone who plays on it at the desk. Saying the same panel twice under two headings
            // reads as two displays.
            bool same = primary is not null && !string.IsNullOrWhiteSpace(couch)
                     && string.Equals(primary, couch, StringComparison.OrdinalIgnoreCase);

            var parts = new List<string>();

            if (primary is not null)
                parts.Add(Part(primary, same ? $"{Words.HdrDisplayMain} · {Words.HdrDisplayCouch}"
                                             : Words.HdrDisplayMain));

            if (!same && !string.IsNullOrWhiteSpace(couch)) parts.Add(Part(couch, Words.HdrDisplayCouch));

            // One display per line. Run together they read as a single sentence about one screen,
            // which is the opposite of the point — and RichNote honours \n as a real break.
            _hdrDisplayNote.SetMarkup(parts.Count == 0
                                          ? Words.HdrDisplaysUnknown
                                          : string.Join("\n", parts));

            string Part(string path, string role)
            {
                var status = Display.HdrControl.StatusFor(path);

                // ReallySupported, not Supported: Windows reports advanced colour, which a plain SDR
                // panel with a wide gamut also claims. The display's own EDID is the honest answer.
                string state = !status.Found          ? Words.HdrDisplayOff
                             : status.ReallySupported ? Words.HdrSupported
                                                      : Words.HdrNotSupported;

                string name = names.TryGetValue(path, out var found) && !string.IsNullOrWhiteSpace(found)
                                  ? found
                                  : Words.HdrDisplayUnnamed;

                return string.Format(Words.HdrDisplayPart, name, role, state);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read HDR support for the displays: {ex.Message}");
            _hdrDisplayNote.SetMarkup(Words.HdrDisplaysUnknown);
        }
    }

    /// <summary>Repaint, relabel and re-sort after a tag was set by either route.</summary>
    private void TagChanged()
    {
        _gameList?.Invalidate();
        UpdateHdrBulkLabel();
        if (_listHeader?.Sort == GameSort.Hdr) RebuildGameList(keepView: true);
        MarkDirty();
    }

    /// <summary>
    /// Click the badge to correct it.
    ///
    /// The most useful sentence on this page was that the tag can be wrong and you can fix it, and
    /// it was grey footnote text pointing at a right-click — the least advertised gesture on a
    /// mouse, and an awkward one from a sofa. The badge is now the control, so the fix is found by
    /// trying it rather than by reading about it.
    ///
    /// Every click flips what the badge says, so the gesture always answers. When the flip lands on
    /// what the lookups already thought, the manual tag is dropped rather than restated: an answer
    /// that agrees with the automatic one is not worth marking as yours, and the star clears with
    /// it. The menu is still there for setting a tag back to automatic outright.
    /// </summary>
    private void CycleTag(GameEntry game)
    {
        var wanted = SupportOf(game) == HdrSupport.Native ? HdrSupport.None : HdrSupport.Native;

        if (HdrDatabase.Lookup(game) == wanted) Config.HdrTags.Remove(game.Key);
        else Config.HdrTags[game.Key] = (int)wanted;

        TagChanged();
    }

    /// <summary>
    /// Right-click a game to correct its HDR tag.
    ///
    /// No lookup table will ever be complete — a game released after this build shipped cannot
    /// be in the bundled list, and the wiki has gaps. Rather than leave you waiting on data I
    /// cannot guarantee, this lets you set it yourself, and your answer wins.
    /// </summary>
    private void ShowTagMenu(GameEntry game, Point where)
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            ShowImageMargin = false,
            RenderMode = ToolStripRenderMode.System,
        };

        var current = SupportOf(game);

        void Item(string text, HdrSupport? tag)
        {
            var entry = new ToolStripMenuItem(text)
            {
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                Checked = tag is null ? !Config.HdrTags.ContainsKey(game.Key) : current == tag && Config.HdrTags.ContainsKey(game.Key),
            };

            entry.Click += (_, _) =>
            {
                if (tag is null) Config.HdrTags.Remove(game.Key);
                else Config.HdrTags[game.Key] = (int)tag;

                TagChanged();
            };

            menu.Items.Add(entry);
        }

        Item("Supports native HDR", HdrSupport.Native);
        Item("No native HDR", HdrSupport.None);
        menu.Items.Add(new ToolStripSeparator());
        Item("Use the automatic tag", null);

        // Only games added by hand can be removed. A discovered game would simply come back on
        // the next scan, so offering to delete it would be a button that does nothing.
        if (game.Source == GameSource.Manual)
        {
            menu.Items.Add(new ToolStripSeparator());

            var remove = new ToolStripMenuItem("Remove from list")
            {
                BackColor = Theme.Surface,
                ForeColor = Theme.Info,
            };

            remove.Click += (_, _) => RemoveGame(game);
            menu.Items.Add(remove);
        }

        menu.Show(_gameList, where);
    }

    /// <summary>
    /// Forget a game that was added with Browse.
    ///
    /// Everything keyed to it goes at once — the saved entry, the tick, and any HDR tag set by
    /// hand — because leaving any of them behind would silently reapply to a game added at the
    /// same path later.
    /// </summary>
    private void RemoveGame(GameEntry game)
    {
        _games.RemoveAll(g => g.Key == game.Key);
        _checkedGames.Remove(game.Key);
        Config.HdrTags.Remove(game.Key);

        Config.HdrGames.RemoveAll(saved =>
            string.Equals(saved.InstallDir.TrimEnd('\\'), game.InstallDir.TrimEnd('\\'),
                          StringComparison.OrdinalIgnoreCase));

        RebuildGameList();
        UpdateHdrBulkLabel();
        MarkDirty();
    }

    /// <summary>
    /// Tick every game not known to render HDR natively, or untick them if they are all on.
    ///
    /// The counterpart to the native button, and the reason both exist: Windows Auto HDR only
    /// applies to games that do not output HDR themselves, so "everything else" is a genuinely
    /// useful selection rather than just the inverse of a filter.
    /// </summary>
    private void ToggleNonNativeHdrGames()
    {
        var before = new HashSet<string>(_checkedGames, StringComparer.OrdinalIgnoreCase);

        var others = _games.Where(g => SupportOf(g) != HdrSupport.Native).ToList();

        if (others.Count == 0)
        {
            // "They are all native" and "there are none" are different answers, and the second is an
            // ordinary first-run state now that nothing is discovered until a scan finds it. Telling
            // somebody with no games that every game they have is native HDR is nonsense.
            Warn(_games.Count == 0 ? Words.WarnNoGames : Words.WarnAllNative);
            return;
        }

        bool allOn = others.All(g => _checkedGames.Contains(g.Key));

        foreach (var game in others)
        {
            if (allOn) _checkedGames.Remove(game.Key);
            else _checkedGames.Add(game.Key);
        }

        UpdateHdrBulkLabel();
        RebuildGameList(keepView: true);
        MarkDirty();

        OfferUndo(before, allOn ? "non-native turned off" : "non-native turned on");
    }

    private void UpdateHdrBulkLabel()
    {
        var native = _games.Where(g => SupportOf(g) == HdrSupport.Native).ToList();
        bool allOn = native.Count > 0 && native.All(g => _checkedGames.Contains(g.Key));

        // The number is the point: it says what you are about to tick before you tick it.
        _hdrBulk.Text = (allOn ? "Clear native" : "Native HDR") + $" ({native.Count})";
        _hdrBulk.Invalidate();

        var others = _games.Where(g => SupportOf(g) != HdrSupport.Native).ToList();
        bool othersOn = others.Count > 0 && others.All(g => _checkedGames.Contains(g.Key));

        _hdrBulkOther.Text = (othersOn ? "Clear non-native" : "Non-native") + $" ({others.Count})";
        _hdrBulkOther.Invalidate();

        UpdateSelectLabels();
    }

    /// <summary>
    /// Say what All and None will actually touch.
    ///
    /// Both act on what the search and filters have left, which is right — narrowing to find one game
    /// and having All tick the other hundred would be a nasty surprise — but a button that says "All"
    /// over a filtered list is claiming something it will not do. When a filter is active they say so.
    /// </summary>
    private void UpdateSelectLabels()
    {
        if (_selectAll is null || _clearAll is null) return;

        bool filtered = (_search?.Query.Length ?? 0) > 0 || _onlyTicked;

        _selectAll.Text = filtered ? "All shown" : Words.ButtonAll;
        _clearAll.Text = filtered ? "None shown" : Words.ButtonNone;

        _selectAll.Invalidate();
        _clearAll.Invalidate();
    }

    /// <summary>
    /// Take a copy of the selection before a bulk change, and offer to put it back.
    ///
    /// One press can move a hundred ticks, and the only other way back is finding every one of them by
    /// hand — which is exactly the case every guideline on bulk actions says needs an undo. It rides in
    /// the note above the buttons, so the confirmation and the way out are the same thing.
    /// </summary>
    private void OfferUndo(HashSet<string> before, string what)
    {
        _undoChecked = before;

        int moved = Math.Abs(_checkedGames.Count - before.Count);

        ShowScanNote(moved == 0 ? what : $"{moved} {what}  ·  Undo");

        if (_scanNote is { IsDisposed: false } note)
        {
            note.Cursor = Cursors.Hand;
            note.ForeColor = Theme.Accent;
        }
    }

    private void UndoBulk()
    {
        if (_undoChecked is not { } before) return;

        _checkedGames.Clear();
        foreach (var key in before) _checkedGames.Add(key);

        _undoChecked = null;

        RebuildGameList(keepView: true);
        UpdateHdrBulkLabel();
        MarkDirty();

        ShowScanNote("Put back.");

        if (_scanNote is { IsDisposed: false } note)
        {
            note.Cursor = Cursors.Default;
            note.ForeColor = Theme.TextFaint;
        }
    }

    /// <summary>The last sorted order, reused while only the width is changing.</summary>
    private List<GameEntry>? _sorted;

    private void RebuildGameList(bool keepView = false, bool resort = true)
    {
        if (_gameList is null) return;

        int top = keepView ? _gameList.TopIndex : 0;

        bool was = _loading;
        _loading = true;
        try
        {
            _gameList.BeginUpdate();
            _gameList.Items.Clear();
            // Re-sorting is skipped while a resize is in flight: nothing about the order can
            // have changed, only the width it is drawn at, and sorting a full library on every
            // step of a drag is exactly the sort of work that made this feel heavy.
            if (resort || _sorted is null) _sorted = SortedGames().ToList();

            foreach (var game in _sorted) _gameList.Items.Add(game);
            _gameList.EndUpdate();

            if (top > 0 && top < _gameList.Items.Count) _gameList.TopIndex = top;
        }
        finally { _loading = was; }

        UpdateGameCount();
        _listBar?.Invalidate();
    }

    /// <summary>
    /// Give the games list whatever vertical space the page is not using.
    ///
    /// It is the only thing on any page meant to scroll, so it should be the thing that absorbs
    /// a taller window — enlarging the window to see more of your library and having the list
    /// stay a fixed 268px while empty space grew beneath it would be a strange reward.
    /// </summary>
    private void StretchGameList(Panel page)
    {
        if (_listHolder is not { } holder || _listBar is not { } bar || _gameList is null) return;

        int otherContent = Stack(page).PreferredSize.Height - holder.Height;
        int available = page.ClientSize.Height - page.Padding.Vertical - otherContent;

        // Rounded down to whole rows. ListBox will not scroll past the last full row, so a
        // height that left a partial row at the bottom made that final game permanently
        // half-visible and impossible to scroll into view.
        int minRows = (MinListHeight + _gameList.ItemHeight - 1) / _gameList.ItemHeight;
        int rows = Math.Max(minRows, available / _gameList.ItemHeight);
        int height = rows * _gameList.ItemHeight;

        if (height == holder.Height) return;

        holder.Height = height;
        _gameList.Height = height;
        _listHeader?.Invalidate();
        bar.Height = height - 8;
        bar.Invalidate();
    }

    /// <summary>Explain an empty list, and offer the way out of it.</summary>
    private void ShowEmptyNote(int shown, int total)
    {
        if (_emptyNote is null || _emptyNote.IsDisposed) return;

        if (shown > 0)
        {
            if (_emptyNote.Visible) _emptyNote.Visible = false;
            return;
        }

        string query = _search?.Query ?? "";

        _emptyNote.Text = total == 0
            ? "No games found yet — press Scan, or add one with Browse."
            : query.Length > 0
                ? $"No games match \u201c{query}\u201d.  Click to clear the search."
                : "No games are turned on.  Click to show them all again.";

        _emptyNote.Visible = true;
        _emptyNote.BringToFront();
    }

    /// <summary>"240 games · 8 on", or "12 of 240 · 8 on" once the search has hidden some.</summary>
    private void UpdateGameCount()
    {
        if (_gameCount is null || _gameCount.IsDisposed) return;

        int shown = _gameList?.Items.Count ?? 0;
        int total = _games.Count;

        // Counted against the library rather than against the list, so a search cannot make it look
        // as though games have been switched off.
        int on = _games.Count(g => _checkedGames.Contains(g.Key));

        ShowEmptyNote(shown, total);

        if (_onlyTicked)
        {
            _gameCount!.Text = $"{shown} on  ·  showing only these";
            return;
        }

        string text = shown == total
                      ? $"{total} game{(total == 1 ? "" : "s")}  ·  {on} on"
                      : $"{shown} of {total}  ·  {on} on";

        if (_gameCount.Text != text) _gameCount.Text = text;
    }

    private IEnumerable<GameEntry> SortedGames()
    {
        var query = _search?.Query ?? "";

        var games = query.Length == 0
            ? _games
            : _games.Where(g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        // Applied after the search, so the two narrow together rather than fighting.
        if (_onlyTicked) games = games.Where(g => _checkedGames.Contains(g.Key)).ToList();

        var sort = _listHeader?.Sort ?? GameSort.Enabled;
        bool down = _listHeader?.Descending ?? true;

        // Name is always the tiebreak, so rows never shuffle unpredictably among equals — most
        // of these keys produce large ties (every unplayed game shares one date, every game
        // without a badge shares one state).
        IEnumerable<GameEntry> ordered = sort switch
        {
            GameSort.Name => down
                ? games.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase)
                : games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase),

            GameSort.LastPlayed => Then(down
                ? games.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue)
                : games.OrderBy(g => g.LastPlayed ?? DateTime.MaxValue)),

            GameSort.Installed => Then(down
                ? games.OrderByDescending(g => g.Installed ?? DateTime.MinValue)
                : games.OrderBy(g => g.Installed ?? DateTime.MaxValue)),

            GameSort.Hdr => Then(down
                ? games.OrderByDescending(g => SupportOf(g) == HdrSupport.Native)
                : games.OrderBy(g => SupportOf(g) == HdrSupport.Native)),

            _ => Then(down
                ? games.OrderByDescending(g => _checkedGames.Contains(g.Key))
                : games.OrderBy(g => _checkedGames.Contains(g.Key))),
        };

        return ordered;

        static IOrderedEnumerable<GameEntry> Then(IOrderedEnumerable<GameEntry> source)
            => source.ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the order depends on something ticking a box can change.
    ///
    /// Used to decide whether ticking a game should re-sort the list underneath the pointer.
    /// </summary>
    private bool SortsByState => _listHeader?.Sort is GameSort.Enabled or GameSort.Hdr;

    /// <summary>
    /// Tick or untick everything currently listed.
    ///
    /// Scoped to what the search is showing, not the whole library. With a filter active,
    /// "Select all" plainly means the games in front of you — and the old behaviour of clearing
    /// every tick in the library while you could only see three of them would be destructive in
    /// a way nothing on screen suggested.
    /// </summary>
    private void SetAllGames(bool selected)
    {
        var before = new HashSet<string>(_checkedGames, StringComparer.OrdinalIgnoreCase);

        foreach (var game in SortedGames())
        {
            if (selected) _checkedGames.Add(game.Key);
            else _checkedGames.Remove(game.Key);
        }

        RebuildGameList(keepView: true);
        UpdateHdrBulkLabel();
        MarkDirty();

        OfferUndo(before, selected ? "turned on" : "turned off");
    }

    private void BrowseForGame()
    {
        using var dialog = new OpenFileDialog
        {
            Title = Words.BrowseTitle,
            Filter = Words.BrowseFilter,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var entry = GameLibrary.FromPath(dialog.FileName);
        if (entry is null) { Warn(Words.WarnNotAGame); return; }

        if (!_games.Any(g => g.Key == entry.Key)) _games.Add(entry);
        _checkedGames.Add(entry.Key);

        RebuildGameList();
        MarkDirty();
    }

    /// <summary>
    /// The update line on the About page: what is running, and whether anything newer exists.
    ///
    /// Checking is a button rather than something that happens silently on open, because a
    /// network call on a page someone opened to read the version is a surprise. The automatic
    /// check at startup is separate and can be switched off.
    /// </summary>
    private void BuildUpdateRow(Control stack)
    {
        var line = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 14, 0, 0),
            BackColor = Color.Transparent,
        };

        var button = new FlatButton
        {
            Text = Words.CheckUpdates,
            Size = new Size(150, 34),
            Margin = new Padding(0, 0, 10, 0),
        };

        var status = NewLabel("", Theme.Small, Theme.TextDim);
        status.Margin = new Padding(0, 8, 0, 0);

        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            button.Text = Words.CheckUpdatesBusy;
            status.ForeColor = Theme.TextDim;
            status.Text = "";

            var found = await Updates.CheckAsync();

            button.Text = Words.CheckUpdates;
            button.Enabled = true;

            if (found.Error.Length > 0)
            {
                status.ForeColor = Theme.TextFaint;
                status.Text = found.Error;
                return;
            }

            if (!found.Available)
            {
                status.ForeColor = Theme.Good;
                status.Text = string.Format(Words.UpToDate, AppInfo.Version);
                return;
            }

            status.ForeColor = Theme.Info;
            status.Text = string.Format(Words.NoticeUpdateFound, found.Version);

            // Through the same property the launch check uses, so pressing the button here also puts
            // the offer on the Home page. The two routes should not disagree about what is available.
            PendingUpdate = found;

            await OfferUpdate(found);
        };

        line.Controls.Add(button);
        line.Controls.Add(status);
        stack.Controls.Add(line);
    }

    /// <summary>Ask before replacing the program, then do it and restart.</summary>
    private async Task OfferUpdate(UpdateInfo found)
    {
        // The app's own dialog, not a MessageBox.
        //
        // The box rendered in the system theme against this window's dark chrome, and worse, printed
        // GitHub's markdown raw — "## What's Changed", a list of pull-request URLs, and a
        // "**Full Changelog**:" line. The one question it exists to answer is "what am I getting",
        // and that was the part hardest to read. See Ui/UpdateDialog.cs.
        using var ask = new UpdateDialog(found, SessionNow);

        if (ask.ShowDialog(this) != DialogResult.Yes) return;

        if (await Updates.InstallAsync(found.DownloadUrl))
        {
            // The new copy is starting; this one has to go or there will be two.
            ExitRequested = true;
            Close();
            return;
        }

        Warn(Words.UpdateFailed);
    }

    /// <summary>
    /// A standalone note inside a card, for something the reader needs rather than a setting.
    ///
    /// Coloured, because it explains a limit that is otherwise invisible — the sort of thing
    /// people discover by concluding the feature is broken.
    /// </summary>
    /// <param name="tight">
    /// Drop the trailing gap under the note — for a card whose only content is the note.
    ///
    /// AddRow gives every row a bottom margin so rows do not touch each other, which on the last row
    /// in a card is a gap on top of the card's own padding: the text sits against the top border with
    /// a dozen spare pixels beneath it, and the box reads as badly centred. Only worth clearing when
    /// nothing follows it.
    /// </param>
    private RichNote AddNote(Card card, string text, int indent = 0, bool tight = false)
    {
        // Emphasis in the same blue rather than the default red: this is a limit to know
        // about, not something going wrong. Passed explicitly even though blue is now the
        // default, so this stays right if the default ever moves again.
        var note = new RichNote(text, RowWidth - indent, Theme.Small, Theme.SmallBold,
                                Theme.Info, Theme.Info)
        {
            Margin = new Padding(0, 2, 0, 0),
        };

        // Never its own elbow. A note is always about the row above it, so at an indent it
        // lines up with that row and says nothing further about the structure.
        AddRow(card, note, indent, elbow: false);

        // After AddRow, which sets the margin itself.
        if (tight) note.Margin = new Padding(indent, 0, 0, 0);

        return note;
    }

    /// <summary>
    /// A button that forgets the controller currently picked in the list.
    ///
    /// A pad is kept in the list while it is switched off, so the choice survives — which means
    /// there has to be a way to say "I am done with that one". This acts on the selection, so it
    /// forgets exactly the pad you are looking at rather than sweeping up everything that is off.
    /// </summary>
    private FlatButton MakeClearControllersButton()
    {
        var clear = new FlatButton
        {
            Text = Words.ClearControllers,
            Size = new Size(150, 34),
            Margin = new Padding(0),
        };

        clear.Click += (_, _) => ForgetSelectedController();
        _tips.SetToolTip(clear, Wrapped(Words.ClearControllersWhy));

        return clear;
    }

    /// <summary>
    /// Forget the controller selected in the picker, and only that one.
    ///
    /// Its remembered entry is dropped, and if it was the chosen pad the selection goes back to Any —
    /// leaving it pointing at something just forgotten would mean no controller could start a session,
    /// with nothing on screen to say why. The name given to it is deliberately kept: plug the same pad
    /// back in and its name comes with it. Dropping a name is what Revert on the Rename dialog is for.
    /// </summary>
    private void ForgetSelectedController()
    {
        if (_triggerPad.SelectedItem is not ControllerDevice device)
        {
            Warn(Words.WarnPickControllerFirst);
            return;
        }

        Config.KnownControllers.RemoveAll(k => k.Id == device.Id);

        if (string.Equals(Config.TriggerControllerId, device.Id, StringComparison.OrdinalIgnoreCase))
            Config.TriggerControllerId = "";

        Log.Info($"Forgot the selected controller ({device.Name}); its name is kept for next time.");

        MarkDirty();
        RefreshControllers();
    }

    /// <summary>
    /// Rename and Forget, side by side beside the controller picker.
    ///
    /// Both act on the selection, so they belong to it rather than sitting loose on the page.
    /// </summary>
    private Control MakeControllerButtons()
    {
        var rename = new FlatButton
        {
            Text = Words.RenameController,
            Size = new Size(96, 34),
            Margin = new Padding(0, 0, 8, 0),
        };

        rename.Click += (_, _) => RenameSelectedController();
        _tips.SetToolTip(rename, Wrapped(Words.RenameControllerWhy));

        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            BackColor = Color.Transparent,
        };

        row.Controls.Add(rename);
        row.Controls.Add(MakeClearControllersButton());

        return row;
    }

    /// <summary>
    /// Give the selected controller a name of the user's own.
    ///
    /// Stored against the device id rather than the model, so two identical pads can be told apart
    /// — which is the entire case for having this. An empty name removes the entry and the model
    /// name comes back.
    /// </summary>
    private void RenameSelectedController()
    {
        if (_triggerPad.SelectedItem is not ControllerDevice device)
        {
            Warn(Words.WarnPickControllerFirst);
            return;
        }

        Config.ControllerNames.TryGetValue(device.Id, out var current);

        // The way back is offered inside the dialog rather than as another button on the page.
        // Reverting is something you decide while looking at the name you gave it, not something
        // that needs its own permanent place in a row of settings.
        var name = Prompt.Ask(this, Words.RenameTitle, Words.RenamePrompt, current ?? "",
                              reset: current is { Length: > 0 } ? Words.RevertControllerName : null);
        if (name is null) return;

        name = name.Trim();

        if (name.Length == 0) Config.ControllerNames.Remove(device.Id);
        else Config.ControllerNames[device.Id] = name;

        MarkDirty();
        RefreshControllers();
    }

    /// <summary>
    /// Fill the refresh-rate list from what the chosen TV can actually do.
    ///
    /// Read from the display itself rather than offered as a fixed list, because a mode the
    /// panel does not support is worse than no choice at all — it is a black screen someone has
    /// to unplug the television to recover from.
    /// </summary>
    private void RefreshVideoModes()
    {
        var display = _tvDisplay.SelectedItem as DisplayInfo;
        var incoming = display?.DevicePath ?? "";

        // Put the outgoing choice away before loading the incoming one.
        //
        // The picker is one control shared by every display, so a change of display silently
        // discarded whatever was set for the previous one — pick the TV, set 120 Hz, glance at
        // the monitor, come back, and it is on "Leave it to Windows" again. What was chosen is
        // recorded against the display it was chosen for, and handed back on return.
        //
        // An empty string is a real answer here, not a missing one: it means "Leave it to
        // Windows", and someone who deliberately picked that should get it back too.
        if (_videoModeOwner.Length > 0 && !string.Equals(_videoModeOwner, incoming,
                                                         StringComparison.OrdinalIgnoreCase))
        {
            Config.VideoModeByDisplay[_videoModeOwner] =
                _tvVideoMode.SelectedItem is DisplayMode outgoing ? outgoing.Key : "";
        }

        // For a display never seen before, fall back to the saved setting — that is the one the
        // config was written with, and on first load it belongs to the configured display.
        var chosen = incoming.Length > 0 && Config.VideoModeByDisplay.TryGetValue(incoming, out var remembered)
                   ? remembered
                   : _videoModeOwner.Length == 0 ? Config.TvVideoMode : "";

        _videoModeOwner = incoming;

        bool was = _loading;
        _loading = true;

        try
        {
            _tvVideoMode.Items.Clear();
            _tvVideoMode.Items.Add(Words.VideoModeAuto);

            // DisplayManager, not LegacyDisplay.
            //
            // The legacy lookup walks the adapter tree and returns the first adapter that lists
            // a monitor with this ID — and an NVIDIA card reports the same television as a
            // phantom child of DISPLAY1 through DISPLAY4. So it answered "\\.\DISPLAY1", the
            // ultrawide, and the picker cheerfully offered that monitor's 3440×1440 modes as if
            // they belonged to a 4K TV. The CCD lookup maps target to source directly.
            var gdi = display is null ? null : DisplayManager.TryGetGdiName(display.DevicePath);

            var modes = ModesFor(display, gdi, out bool fromCache);

            foreach (var mode in modes) _tvVideoMode.Items.Add(mode);

            // Three different situations, three different things to say. An empty list with no
            // explanation is the one outcome that reads as a fault rather than a state.
            SetVideoModeNote(display is null || gdi is not null ? ""
                : fromCache   ? string.Format(Words.VideoModeRemembered, PlainName(display))
                             : string.Format(Words.VideoModeUnavailable, PlainName(display)));

            _tvVideoMode.SelectedIndex = chosen.Length == 0
                ? 0
                : Math.Max(0, IndexOf(_tvVideoMode, o => o is DisplayMode m && m.Key == chosen));
        }
        finally { _loading = was; }
    }

    /// <summary>
    /// Show or hide the explanation under the mode picker.
    ///
    /// Collapsed to nothing when empty rather than left as a blank gap, since the card is laid
    /// out by a flow panel and an empty control still takes its margins with it.
    /// </summary>
    private void SetVideoModeNote(string markup)
    {
        if (_videoModeNote is not { IsDisposed: false } note) return;

        note.Visible = markup.Length > 0;
        note.Margin = new Padding(0, markup.Length > 0 ? 8 : 0, 0, 0);

        if (markup.Length > 0) note.SetMarkup(markup);
    }

    /// <summary>
    /// The modes to offer for a display: live if it is on, remembered if it is not.
    ///
    /// Live always wins and always overwrites the cache, so a remembered list cannot outlive
    /// the truth by more than one switch-on. Displays come and go — a TV swapped for a newer
    /// one keeps the same port but not the same abilities — and quietly preferring stale data
    /// would offer modes the panel in the room cannot do.
    /// </summary>
    private List<DisplayMode> ModesFor(DisplayInfo? display, string? gdi, out bool remembered)
    {
        remembered = false;

        if (display is null) return [];

        if (gdi is not null)
        {
            var live = DisplayModes.Supported(gdi, DisplayManager.NativeMode(display.DevicePath));

            if (live.Count > 0) Remember(display.DevicePath, live);

            return live;
        }

        if (!Config.KnownVideoModes.TryGetValue(display.DevicePath, out var saved)) return [];

        // Rebuilt in the order they were stored, which was already best-first, so the markers go
        // back on the same modes without needing the display present to work them out again. The
        // scaled flag is carried in the cache too (a "|s" suffix), because it is decided from the
        // panel's native shape — which cannot be read once the display is gone.
        var restored = saved.Select(CachedMode).OfType<DisplayMode>().ToList();
        if (restored.Count == 0) return [];

        remembered = true;

        // Best is always a native-shaped mode, so never tag a scaled one as the best.
        if (!restored[0].Scaled) restored[0] = restored[0] with { Recommended = true };

        return restored;
    }

    /// <summary>Marks a cached mode string that was scaled when the display was last present.</summary>
    private const string ScaledCacheSuffix = "|s";

    /// <summary>Turn a cached mode string, with its optional scaled marker, back into a mode.</summary>
    private static DisplayMode? CachedMode(string cached)
    {
        bool scaled = cached.EndsWith(ScaledCacheSuffix, StringComparison.Ordinal);
        var key = scaled ? cached[..^ScaledCacheSuffix.Length] : cached;

        return DisplayMode.FromKey(key) is { } mode ? mode with { Scaled = scaled } : null;
    }

    /// <summary>
    /// Store a display's mode list, and save only when it actually changed.
    ///
    /// Written straight to disk rather than waiting for the user to press Save: this is not a
    /// preference they chose, it is something observed about their hardware, and it is worth
    /// nothing unless it survives to the next launch — when the television will be off again.
    /// The equality check is what stops that becoming a disk write every time the page is built.
    /// </summary>
    private void Remember(string devicePath, List<DisplayMode> modes)
    {
        var keys = modes.Select(m => m.Key).ToList();

        if (Config.KnownVideoModes.TryGetValue(devicePath, out var existing) &&
            existing.SequenceEqual(keys))
            return;

        Config.KnownVideoModes[devicePath] = keys;

        try { Config.Save(); }
        catch (Exception ex) { Log.Warn($"Could not save the remembered modes: {ex.Message}"); }
    }

    /// <summary>The display's name without the "— disconnected" suffix the list adds.</summary>
    private static string PlainName(DisplayInfo display) =>
        display.FriendlyName.Replace(Words.DisplayDisconnected, "").TrimEnd();

    /// <summary>
    /// Fill the power-plan list from what this machine actually has.
    ///
    /// Read rather than offered as a fixed list of the three Windows ships, for the same reason
    /// the display modes are: people duplicate schemes, rename them and write their own, and a
    /// list that does not include someone's plan is a list they cannot use.
    /// </summary>
    private void RefreshPowerPlans()
    {
        var chosen = _powerPlan.SelectedItem is PowerPlan picked ? picked.Id.ToString()
                   : Config.SessionPowerPlan;

        bool was = _loading;
        _loading = true;

        try
        {
            // Whether to touch the power plan at all is the toggle's question, not this list's,
            // so there is no "leave it alone" entry here — it would be a second way to say the
            // same thing, and two controls that can disagree is how the last version confused
            // itself.
            _powerPlan.Items.Clear();
            _powerPlan.Items.Add(Words.PowerPlanAuto);

            foreach (var plan in PowerPlans.Available().OrderByDescending(p => p.Rank))
                _powerPlan.Items.Add(plan);

            // A plan deleted since it was chosen falls back to Automatic rather than silently
            // selecting whatever now sits at that position in the list.
            _powerPlan.SelectedIndex = Guid.TryParse(chosen, out _)
                ? Math.Max(0, IndexOf(_powerPlan, o => o is PowerPlan p && p.Id.ToString() == chosen))
                : 0;
        }
        finally { _loading = was; }
    }

    private void OnChangePowerPlanToggled(object? sender, EventArgs e)
    {
        _powerPlan.Enabled = _changePowerPlan.Checked;
        _powerPlan.Invalidate();
    }

    /// <summary>The plan the dropdown is on, as a key: a scheme's GUID, or Automatic for the top item.</summary>
    private string SelectedPowerPlan() =>
        _powerPlan.SelectedItem is PowerPlan plan ? plan.Id.ToString() : AppConfig.Automatic;

    private void OnPowerPlanChanged(object? sender, EventArgs e)
    {
        // Only when the user picks a plan, and only while the power toggle is on — the check line
        // lives under that toggle, and there is nothing to show while it is off.
        if (_loading || !_changePowerPlan.Checked) return;

        ShowToggleNote(Words.ChangePowerPlan, PerformanceCheck.CheckPowerPlan(SelectedPowerPlan()));
    }

    private void OnTvDisplayChanged(object? sender, EventArgs e)
    {
        RefreshVideoModes();
    }

    /// <summary>
    /// Replace a dropdown's fixed choices, keeping whatever was selected.
    ///
    /// Idempotent, because it runs again on every page rebuild. Anything here that only ever
    /// adds will grow the list without bound, which is exactly what happened before.
    /// </summary>
    private static void SetOptions(Dropdown combo, string[] options)
    {
        int chosen = combo.SelectedIndex;

        combo.Items.Clear();
        combo.Items.AddRange(options);

        combo.SelectedIndex = chosen >= 0 && chosen < options.Length ? chosen : -1;
    }

    /// <summary>Raised from the watcher's thread, so it has to hop to the UI first.</summary>
    private void OnTriggerPadPicked(object? sender, EventArgs e)
    {
        if (_loading) return;
        RefreshPadPresenceNow();
    }

    /// <summary>
    /// Re-read what is connected and say so.
    ///
    /// Also on a timer, not only on the watcher's event. The watcher polls every 1.5 seconds and
    /// otherwise waits on a device-change message from Windows, which a Bluetooth pad switching off
    /// does not always produce promptly — so a line claiming your controller is missing could outlive
    /// the fact by a good while. Only while this page is the one being looked at.
    /// </summary>
    private void RefreshPadPresenceNow()
    {
        if (_padPresence is null) return;

        try { RefreshPadPresence(ControllerWatcher.Attached().Select(Renamed).ToList()); }
        catch (Exception ex) { Log.Warn($"Could not refresh the controller line: {ex.Message}"); }
    }

    private void OnControllersChanged()
    {
        // Straight away, rather than waiting for the strip's own poll: a pad being switched on is
        // the moment somebody looks to see whether the app noticed.
        _controllers?.Refresh(force: true);

        if (IsDisposed || !IsHandleCreated) return;

        try { BeginInvoke(RefreshControllers); }
        catch (ObjectDisposedException) { /* closing while a device changed */ }
    }

    /// <summary>
    /// Rebuild the controller list, keeping whatever was chosen.
    ///
    /// The selection is restored by id rather than by position: the list changes as pads come
    /// and go, so an index would silently come to mean a different controller.
    /// </summary>
    /// <summary>The user's name for a pad if they gave it one, with its hardware code kept.</summary>
    private ControllerDevice Renamed(ControllerDevice device)
    {
        if (!Config.ControllerNames.TryGetValue(device.Id, out var mine) || mine.Length == 0)
            return device;

        return device with { Name = $"{mine}{CodeOf(device.Name)}" };
    }

    /// <summary>The "  ·  ABCD" tail the enumeration appends, or nothing if it has none.</summary>
    private static string CodeOf(string name)
    {
        int tag = name.IndexOf("  ·  ", StringComparison.Ordinal);
        return tag < 0 ? "" : name[tag..];
    }

    /// <summary>
    /// Say what is connected, and how each one is powered.
    ///
    /// The power state is already worked out — PadPresence distinguishes running on a battery from
    /// charging — and until now it reached the screen as a single lightning bolt in the title bar or
    /// as nothing at all. On the page about controllers it is worth a word.
    /// </summary>
    private void RefreshPadPresence(List<ControllerDevice> attached)
    {
        if (_padPresence is null) return;

        if (attached.Count == 0)
        {
            _padPresence.SetMarkup(Words.PadsNone);
            _padPresence.ForeColor = Theme.TextFaint;
            return;
        }

        // Power comes from PadPresence rather than from the device list, because only PadPresence
        // asks XInput about it. Matched by name, which is what PadPresence reports — a miss simply
        // leaves the power off the line rather than guessing at it.
        IReadOnlyList<PadStatus> power;
        try { power = PadPresence.All(); }
        catch { power = []; }

        var named = attached.Select(d =>
        {
            var state = power.FirstOrDefault(p => string.Equals(p.Name, d.Name,
                                                                StringComparison.OrdinalIgnoreCase));

            string how = state.Power switch
            {
                PadPower.Charging => $" ({Words.PadCharging})",
                PadPower.OnBattery => $" ({Words.PadOnBattery})",
                _ => "",
            };

            return $"**{d.Name}**{how}";
        });

        // Taken from the picker rather than from Config.
        //
        // Config is only written when the debounced save lands, so reading it here meant choosing a
        // controller left this line talking about the previous one for the best part of a second —
        // and if nothing else prompted a refresh, until the next time a pad came or went.
        string chosenId = _triggerPad.SelectedItem is ControllerDevice picked
                              ? picked.Id
                              : Config.TriggerControllerId;

        // The chosen pad being away is the state worth flagging: everything on this page is about it,
        // and nothing else connecting will do what the page says.
        bool chosenAway = chosenId.Length > 0 && attached.All(d => d.Id != chosenId);

        string list = string.Join(", ", named);

        _padPresence.SetMarkup(chosenAway
                                   ? string.Format(Words.PadsChosenAway,
                                                   list.Length > 0
                                                       ? string.Format(Words.PadsConnected, list)
                                                       : Words.PadsChosenAwayNothing)
                                   : string.Format(Words.PadsConnected, list));

        _padPresence.ForeColor = chosenAway ? Theme.Warn : Theme.TextDim;
    }

    private void RefreshControllers()
    {
        var chosen = _triggerPad.SelectedItem is ControllerDevice picked ? picked.Id
                   : Config.TriggerControllerId;

        // Renamed here, once, so every later use — the list, the remembered entries, the title
        // bar strip — sees the name the user chose rather than each having to remember to ask.
        var attached = ControllerWatcher.Attached().Select(Renamed).ToList();

        RefreshPadPresence(attached);

        // Anything seen is remembered, whether or not it is the one chosen.
        //
        // A controller is a thing someone owns, not a thing that exists only while it is
        // powered. Building the list purely from what is switched on meant a second pad
        // arriving pushed the first out of the list, and a pad that had never been chosen
        // could not be chosen without switching it on first.
        foreach (var device in attached)
        {
            var known = Config.KnownControllers.FirstOrDefault(k => k.Id == device.Id);

            if (known is null)
                Config.KnownControllers.Add(new KnownController { Id = device.Id, Name = device.Name });
            else
                known.Name = device.Name;
        }

        bool was = _loading;
        _loading = true;

        try
        {
            _triggerPad.Items.Clear();
            _triggerPad.Items.Add(Words.AnyController);

            foreach (var device in attached) _triggerPad.Items.Add(device);

            // Then everything remembered that is not attached right now. An empty Path is what
            // marks these as unavailable — it is how the dropdown knows to draw them dimmed,
            // and how Save knows not to overwrite the remembered name with a decorated one.
            foreach (var known in Config.KnownControllers)
            {
                if (attached.Any(d => d.Id == known.Id)) continue;

                // Looked up again rather than trusting the remembered name: a controller can be
                // renamed while it is switched off, and this is the only list it appears in then.
                var label = Config.ControllerNames.TryGetValue(known.Id, out var mine) && mine.Length > 0
                          ? $"{mine}{CodeOf(known.Name)}"
                          : known.Name.Length > 0 ? known.Name : known.Id;
                _triggerPad.Items.Add(new ControllerDevice(known.Id, $"{label} {Words.NotConnected}", ""));
            }

            _triggerPad.SelectedIndex = chosen.Length == 0
                ? 0
                : Math.Max(0, IndexOf(_triggerPad, o => o is ControllerDevice d && d.Id == chosen));
        }
        finally { _loading = was; }

        // Reopen the popup on the new list if it happens to be showing, so a controller that
        // arrives while the list is open appears in it rather than after it is closed.
        _triggerPad.RefreshOpenList();
    }

    /// <summary>
    /// Rediscover games now, rather than waiting for the periodic rescan.
    ///
    /// The rescan already runs on a timer, so this is not new capability — it is the difference
    /// between installing a game and seeing it appear, versus installing a game and wondering
    /// whether the app noticed. That wait, however short, is exactly the moment someone reaches
    /// for the manual button, so it earns its place beside Browse.
    ///
    /// LoadGames does the actual work and is safe to call again: it rebuilds from a fresh
    /// discovery and keeps hand-added games. This only adds the user feedback, because a button
    /// that changes nothing visible reads as broken even when it worked.
    /// </summary>
    private void ScanForGames()
    {
        int before = _games.Count;

        LoadGames();

        int found = _games.Count - before;

        ShowScanNote(found > 0 ? string.Format(Words.NoticeScanFound, found)
                                : Words.NoticeScanNothing);
    }

    /// <summary>
    /// Preselect the likely display and audio devices, where nothing has been chosen yet.
    ///
    /// Only fills blanks. A saved choice is never second-guessed — including a deliberate one
    /// that happens to match nothing right now, because the TV being switched off is not a
    /// reason to reassign it to the monitor.
    /// </summary>
    private void GuessUnset()
    {
        DisplayInfo? tv = null;

        if (Config.TvDisplayPath.Length == 0 && _tvDisplay.SelectedIndex < 0)
        {
            tv = BestGuess.Tv(_tvDisplay.Items.OfType<DisplayInfo>());

            if (tv is not null)
            {
                _tvDisplay.SelectedIndex = IndexOf(_tvDisplay, o => ReferenceEquals(o, tv));
                Log.Info($"Guessed '{tv.FriendlyName}' as the TV. Change it if that is wrong.");
            }
        }

        tv ??= _tvDisplay.SelectedItem as DisplayInfo;

        if (Config.TvAudioDeviceId.Length == 0 && _tvAudio.SelectedIndex < 0)
        {
            var audio = BestGuess.TvAudio(_tvAudio.Items.OfType<AudioDeviceInfo>(), tv);

            if (audio is not null)
            {
                _tvAudio.SelectedIndex = IndexOf(_tvAudio, o => ReferenceEquals(o, audio));
                Log.Info($"Guessed '{audio.FriendlyName}' as the TV's sound output.");
            }
        }

        // The desktop device is deliberately left on Automatic.
        //
        // Naming a device here pins the return to that exact endpoint forever. Automatic
        // captures whatever was default at the moment the session started, which stays right
        // when a headset is plugged in, a Bluetooth speaker connects, or the device list
        // changes — none of which a guess made on first run could anticipate.
    }

    /// <summary>
    /// Put every setting back to its default, after asking.
    ///
    /// Destructive and not undoable, so it asks first and defaults to No. The chosen display,
    /// audio devices and the whole HDR game list all go, which is worth spelling out — someone
    /// reaching for this to fix one misbehaving option would not expect to lose the rest.
    /// </summary>
    private void ResetEverything()
    {
        if (!AskBox.Confirm(this, Words.ResetConfirmTitle, Words.ResetConfirm,
                            Words.ResetConfirmYes, Words.ResetConfirmNo))
            return;

        // Stopped first: it is running on a debounce, and letting it fire afterwards would
        // write the old values straight back over the defaults.
        _autoSave.Stop();

        Config.ResetToDefaults();
        _checkedGames.Clear();

        Config.Save();

        LoadFromConfig();
        UpdateDependentStates();
        RebuildGameList();

        // Tell the tray, so the watchers stop and start to match. Without this the old watch
        // list and controller triggers would keep running until the app was restarted, and the
        // window would disagree with what the app was actually doing.
        Saved?.Invoke(Config);

        ShowSaved();
    }

    /// <summary>
    /// Which settings each page owns, for the reset button at the bottom of it.
    ///
    /// Written out by hand rather than derived from the controls on the page, because a page and a
    /// setting are not one-to-one: several settings are stored as two properties, some controls read
    /// Windows rather than the config, and a couple of properties are bookkeeping that no page shows.
    /// A list that can be read start to finish is worth more here than one that is clever.
    ///
    /// Collections are absent on purpose — <see cref="ResetPage"/> clears those itself, because
    /// copying one from <see cref="Defaults"/> would hand the live config a reference to the static
    /// default's own list and every later edit would be writing into the defaults.
    /// </summary>
    private static string[] SettingsOn(int page) => page switch
    {
        DisplayPage =>
        [
            nameof(AppConfig.TvDisplayPath), nameof(AppConfig.TvDisplayName),
            nameof(AppConfig.TvVideoMode), nameof(AppConfig.DisplayMode),
            nameof(AppConfig.TurnOffTvOnExit),
            nameof(AppConfig.SwitchAudio),
            nameof(AppConfig.TvAudioDeviceId), nameof(AppConfig.TvAudioDeviceName),
            nameof(AppConfig.DesktopAudioDeviceId), nameof(AppConfig.DesktopAudioDeviceName),
            nameof(AppConfig.MuteGameOnDesktop),
        ],

        // HdrTags is deliberately kept, and is not a setting: it is looked-up data about which
        // games support HDR, and clearing it would throw away a download for nothing.
        //
        // Two other properties used to be listed here for a subtler reason, and no longer need to
        // be. HdrGamesChosen was the flag that stopped the first-run seeding from re-ticking every
        // native-HDR game, and resetting it re-armed exactly that: the list emptied as the
        // confirmation promised, then quietly re-ticked itself within five minutes and auto-saved.
        // SeenGames was the record that kept "new game" meaningful for the auto-tick setting.
        // Both the seeding and the setting are gone, so nothing can refill the list behind anyone
        // any more — which is a better guarantee than remembering to exclude two fields.
        HdrPage =>
        [
            nameof(AppConfig.AutoHdrEnabled), nameof(AppConfig.HdrForWholeSession),
            nameof(AppConfig.HdrHotkeyRemembersGame),
        ],

        ControllerPage =>
        [
            nameof(AppConfig.StartOnControllerConnect), nameof(AppConfig.StartOnControllerLink),
            nameof(AppConfig.OnControllerOff), nameof(AppConfig.OnControllerLost),
            nameof(AppConfig.TriggerControllerId), nameof(AppConfig.TriggerControllerName),
            nameof(AppConfig.MouseControlEnabled), nameof(AppConfig.MouseCursorStick),
            nameof(AppConfig.MouseSpeed), nameof(AppConfig.MouseDeadzone),
            nameof(AppConfig.MouseUseTrackpad), nameof(AppConfig.MouseInGames),
            nameof(AppConfig.MouseHoldToActivate), nameof(AppConfig.MouseHoldButton),
        ],

        HotkeysPage =>
        [
            nameof(AppConfig.ShortcutKeyboard),
            nameof(AppConfig.ShortcutController), nameof(AppConfig.ShortcutControllerVendor),
            nameof(AppConfig.ShortcutControllerProduct),
            nameof(AppConfig.ShortcutKeyboardHdr),
            nameof(AppConfig.ShortcutControllerHdr), nameof(AppConfig.ShortcutControllerHdrVendor),
            nameof(AppConfig.ShortcutControllerHdrProduct),
            nameof(AppConfig.EndSessionOnGuideButton),
        ],

        // The UAC and firewall switches on this page are absent because they are not stored here —
        // they read and write Windows itself, and are shown ticked or not by asking it. A button
        // that reset settings and silently left two of the switches alone would be lying, so the
        // note beside it says so out loud.
        PerformancePage =>
        [
            nameof(AppConfig.ChangePowerPlan), nameof(AppConfig.SessionPowerPlan),
            nameof(AppConfig.SilenceNotifications), nameof(AppConfig.EnableGameMode),
            nameof(AppConfig.GamePriorityEnabled), nameof(AppConfig.DisableGameBarButton),
        ],

        GeneralPage =>
        [
            nameof(AppConfig.StartWithWindows), nameof(AppConfig.StartSessionOnLaunch),
            nameof(AppConfig.StartSessionOnWake), nameof(AppConfig.StartOnWakeControllerOnly),
            nameof(AppConfig.MinimizeToTrayOnClose), nameof(AppConfig.SteamAfterBigPicture),
            nameof(AppConfig.ConfirmClosingGame), nameof(AppConfig.CheckForUpdates),
            nameof(AppConfig.ShowNotifications), nameof(AppConfig.ShowSessionNotifications),
            nameof(AppConfig.ShowHdrNotifications), nameof(AppConfig.ShowMinimizeNotification),
            nameof(AppConfig.ShowProblemNotifications), nameof(AppConfig.ShowActionNotifications),
        ],

        _ => [],
    };

    /// <summary>
    /// Put one page back to its defaults, after asking.
    ///
    /// Separate from the footer's Reset, which takes everything. That one is the right button about
    /// once — after making a mess of the whole app — and the wrong one every other time: someone who
    /// has spent ten minutes getting their television and audio right, and then tangled up the mouse
    /// settings, should not have to choose between living with it and starting over.
    /// </summary>
    private void ResetPage(int page)
    {
        string name = page >= 0 && page < Words.NavItems.Length ? Words.NavItems[page] : "this page";

        // Named consequences, not a generic warning. The switches on a page are obviously on it; the
        // chosen television and the ticked game list are the parts someone would not expect to go.
        string loses = page switch
        {
            DisplayPage => Words.ResetPageLosesDisplay,
            HdrPage => Words.ResetPageLosesHdr,
            ControllerPage => Words.ResetPageLosesController,
            HotkeysPage => Words.ResetPageLosesHotkeys,
            _ => "",
        };

        if (!AskBox.Confirm(this, string.Format(Words.ResetPageTitle, name),
                            string.Format(Words.ResetPageConfirm, name, loses),
                            Words.ResetPageYes, Words.ResetPageNo))
            return;

        // Stopped first, for the same reason the full reset stops it: it is running on a debounce and
        // would otherwise fire afterwards and write the old values straight back over the defaults.
        _autoSave.Stop();

        foreach (string setting in SettingsOn(page))
        {
            var property = typeof(AppConfig).GetProperty(setting);

            if (property is null || !property.CanRead || !property.CanWrite)
            {
                // Renamed or removed. Loud rather than silent: the symptom otherwise is a reset that
                // leaves one setting behind, which looks like the setting is stuck.
                Log.Warn($"Page reset: '{setting}' is not a settable property on AppConfig; skipped.");
                continue;
            }

            property.SetValue(Config, property.GetValue(Defaults));
        }

        // The lists, by hand. See the note on SettingsOn.
        if (page == HdrPage)
        {
            Config.HdrGames = [];
            _checkedGames.Clear();
        }
        else if (page == ControllerPage)
        {
            // The names given to controllers by hand, which nothing else clears.
            //
            // [BUG] These are edited only on this page, through the Rename button beside the picker,
            // and they are a collection rather than a property — so the reflection loop above never
            // saw them and "Reset this page" left every custom name in place. A reset that visibly
            // leaves something behind reads as a reset that failed.
            Config.ControllerNames = [];
        }
        else if (page == DisplayPage)
        {
            // The remembered video mode per display goes with the chosen display: leaving it would
            // pin a resolution to a television the app has just forgotten it ever picked.
            Config.VideoModeByDisplay = [];

            // And the field that remembers which display those modes belonged to.
            //
            // Left set, RefreshVideoModes treats the reset as an ordinary change of display and files
            // the outgoing display's mode back into the dictionary that was just emptied — so a page
            // reset would clear the list and immediately put one entry back, for the very display the
            // user was moving away from.
            _videoModeOwner = "";
        }

        // Start with Windows lives in Task Scheduler, not in the config file.
        //
        // The property here is only a mirror of it, and the mirror is not what is read back: the
        // toggle is loaded from StartupRegistration.IsEnabled(). Writing the default to the config
        // alone would leave the switch showing off after a reset that says it turned it on, and the
        // two would then disagree until something else happened to save.
        if (page == GeneralPage && StartupRegistration.IsEnabled() != Defaults.StartWithWindows)
            StartupRegistration.Set(Defaults.StartWithWindows);

        LoadFromConfig();
        UpdateDependentStates();

        if (page == HdrPage) RebuildGameList();

        // Written to disk from the controls, not straight from Config, and afterwards rather than
        // before.
        //
        // LoadFromConfig does not simply mirror the defaults into the page — GuessUnset fills the
        // emptied display and audio pickers with its best guess, which is the whole point of clearing
        // them. Saving first meant that guess existed only on screen: the file on disk still said
        // nothing was chosen, so the app considered itself unconfigured and the pad shortcut answered
        // with "Couch Session is not set up yet" while the window plainly showed a television picked.
        //
        // This also tells the tray, so whatever it is running stops and starts to match.
        Save(silent: true);

        Log.Info($"Reset the {name} page to defaults.");

        // After Save, which writes its own "all changes saved" into the same strip.
        ShowFooterNote(string.Format(Words.ResetPageDone, name));
    }

    /// <summary>
    /// The reset button at the foot of a settings page.
    ///
    /// At the end, right-aligned and drawn in the page's own background so it reads as an outline
    /// rather than a filled button. All three are deliberate. It is the most destructive control on
    /// the page, so it belongs past everything it can undo rather than above it, where a stray click
    /// on the way to the first setting would find it; and giving it the weight of a primary button
    /// would invite the press it is trying to make considered.
    /// </summary>
    private void AddPageReset(Panel page, int index, string note = "")
    {
        var stack = Stack(page);

        if (note.Length > 0)
        {
            var caveat = NewLabel(note, Theme.Small, Theme.TextFaint, ContentWidth - 8);
            caveat.Margin = new Padding(3, 6, 0, 0);
            stack.Controls.Add(caveat);
        }

        var button = new FlatButton
        {
            Text = Words.ResetPage,
            Size = new Size(148, 32),
            Fill = Theme.Window,
            ForeColor = Theme.TextDim,
        };

        button.Click += (_, _) => ResetPage(index);
        _tips.SetToolTip(button, Wrapped(Words.ResetPageWhy));

        // A plain panel rather than a right-to-left flow: the button is the only thing on the row,
        // and a fixed position needs no layout pass to settle at the width every page is built to.
        var row = new Panel
        {
            Width = ContentWidth,
            Height = 40,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 0),
        };

        button.Location = new Point(ContentWidth - button.Width, 4);
        row.Controls.Add(button);
        stack.Controls.Add(row);
    }

    private void LoadFromConfig()
    {
        _loading = true;
        try
        {
            _tvDisplay.SelectedIndex = IndexOf(_tvDisplay,
                o => o is DisplayInfo d && (d.DevicePath == Config.TvDisplayPath
                                            || d.FriendlyName == Config.TvDisplayName));

            _tvAudio.SelectedIndex = IndexOf(_tvAudio,
                o => o is AudioDeviceInfo a && (a.Id == Config.TvAudioDeviceId
                                                || a.FriendlyName == Config.TvAudioDeviceName));

            _desktopAudio.SelectedIndex = Config.DesktopAudioDeviceId.Length == 0
                ? 0
                : Math.Max(0, IndexOf(_desktopAudio,
                    o => o is AudioDeviceInfo a && a.Id == Config.DesktopAudioDeviceId));

            GuessUnset();

            _displayMode.SelectedIndex = Config.DisplayMode == TvDisplayMode.KeepOthers ? 1 : 0;
            _steamAfter.SelectedIndex = (int)Config.SteamAfterBigPicture;

            RefreshVideoModes();

            _triggerPad.SelectedIndex = Config.TriggerControllerId.Length == 0
                ? 0
                : Math.Max(0, IndexOf(_triggerPad,
                    o => o is ControllerDevice d && d.Id == Config.TriggerControllerId));

            _switchAudio.Checked = Config.SwitchAudio;
            _guideEndsSession.Checked = Config.EndSessionOnGuideButton;
            _restartSteam.Checked = Config.RestartSteamForAudio;
            _confirmClosingGame.Checked = Config.ConfirmClosingGame;
            _checkForUpdates.Checked = Config.CheckForUpdates;
            _hdrMode.SelectedIndex = (int)Config.HdrSwitching;
            _hdrHotkeyRemember?.SetQuietly(Config.HdrHotkeyRemembersGame);
            _minimizeOnClose.Checked = Config.MinimizeToTrayOnClose;
            _startOnLaunch.Checked = Config.StartSessionOnLaunch;
            _startOnWake.Checked = Config.StartSessionOnWake;
            _startOnWakeControllerOnly.Checked = Config.StartOnWakeControllerOnly;
            _showNotifications.Checked = Config.ShowNotifications;
            _showSessionNotifications.Checked = Config.ShowSessionNotifications;
            _showHdrNotifications.Checked = Config.ShowHdrNotifications;
            _showMinimizeNotification.Checked = Config.ShowMinimizeNotification;
            _showProblemNotifications.Checked = Config.ShowProblemNotifications;
            _showActionNotifications.Checked = Config.ShowActionNotifications;
            _shortcutKeyboard.Value = Config.ShortcutKeyboard;
            _shortcutController.Value = Config.ShortcutController;
            _shortcutController.Vendor = (ushort)Config.ShortcutControllerVendor;
            _shortcutController.Product = (ushort)Config.ShortcutControllerProduct;

            _shortcutKeyboardHdr.Value = Config.ShortcutKeyboardHdr;

            _mouseControl.Checked = Config.MouseControlEnabled;
            _mouseHold.Checked = Config.MouseHoldToActivate;
            _mouseInGames.Checked = Config.MouseInGames;
            _mouseHoldButton.SelectedIndex = HoldButtonToIndex(Config.MouseHoldButton);
            _cursorStick.SelectedIndex = Math.Clamp(Config.MouseCursorStick, 0, 1);
            _useTrackpad.Checked = Config.MouseUseTrackpad;
            _mouseSpeed.SetQuietly(Config.MouseSpeed);
            _mouseDeadzone.SetQuietly(Config.MouseDeadzone);
            _shortcutControllerHdr.Value = Config.ShortcutControllerHdr;
            _shortcutControllerHdr.Vendor = (ushort)Config.ShortcutControllerHdrVendor;
            _shortcutControllerHdr.Product = (ushort)Config.ShortcutControllerHdrProduct;

            _padLink.SelectedIndex = (int)Config.StartOnControllerLink;

            _changePowerPlan.Checked = Config.ChangePowerPlan;
            _powerPlan.Enabled = Config.ChangePowerPlan;

            _powerPlan.SelectedIndex = Guid.TryParse(Config.SessionPowerPlan, out _)
                ? Math.Max(0, IndexOf(_powerPlan, o => o is PowerPlan p
                                                    && p.Id.ToString() == Config.SessionPowerPlan))
                : 0;

            _turnOffTvOnExit.Checked = Config.TurnOffTvOnExit;
            _disableGameBarButton.Checked = !GameBarControl.IsEnabled();
            _keepDisplayAwake.Checked = Config.KeepDisplayAwake;
            _silenceNotifications.Checked = Config.SilenceNotifications;
            // Windows is the truth here, not the config — the same rule as Start with Windows below.
            _enableGameMode.Checked = GameModeControl.IsEnabled();
            _gamePriority.Checked = Config.GamePriorityEnabled;
            _startOnController.Checked = Config.StartOnControllerConnect;
            _onControllerOff.SelectedIndex = (int)Config.OnControllerOff;

            // Clamped, because this list is one shorter than the other and a config edited by hand —
            // or carried over from a build where both lists matched — could name an answer that is
            // deliberately not on offer here.
            _onControllerLost.SelectedIndex = Math.Min((int)Config.OnControllerLost,
                                                       (int)DisconnectAction.ComeBackAndAsk);
            _muteOnDesktop.Checked = Config.MuteGameOnDesktop;

            // Registry is the truth for startup, except on a first run where nothing is written yet.
            _startWithWindows.Checked = StartupRegistration.IsEnabled()
                                        || (!Config.IsConfigured && Config.StartWithWindows);
        }
        finally { _loading = false; }
    }

    private static int IndexOf(Dropdown box, Func<object, bool> match)
    {
        for (int i = 0; i < box.Items.Count; i++)
            if (box.Items[i] is { } item && match(item)) return i;
        return -1;
    }

    // ================= save =================

    /// <summary>
    /// Write the current state of the window to the config file.
    ///
    /// Saving happens on its own now, so it cannot refuse and cannot interrupt: a half-filled
    /// window is a normal intermediate state, not an error worth a dialog. Anything not chosen
    /// yet is simply left as it was, and starting a session is where a missing display is
    /// actually worth objecting to.
    /// </summary>
    private void Save(bool silent = false)
    {
        if (!silent)
        {
            if (_tvDisplay.SelectedItem is not DisplayInfo)
            {
                Warn(Words.WarnPickDisplay);
                return;
            }

            if (_switchAudio.Checked && _tvAudio.SelectedItem is not AudioDeviceInfo)
            {
                Warn(Words.WarnPickAudio);
                return;
            }
        }

        // Only overwrite a device when one is actually selected.
        //
        // Nothing selected does not mean "no device" — it usually means the device is not
        // present right now, most often because the TV is off and its endpoint has vanished
        // from the list. Writing that absence through was destroying a working configuration,
        // which is why sound stopped being restored to the right device.
        if (_tvDisplay.SelectedItem is DisplayInfo display)
        {
            Config.TvDisplayPath = display.DevicePath;
            Config.TvDisplayName = display.FriendlyName;
        }

        if (_tvAudio.SelectedItem is AudioDeviceInfo tv)
        {
            Config.TvAudioDeviceId = tv.Id;
            Config.TvAudioDeviceName = PlainAudioName(tv.FriendlyName);
        }

        if (_desktopAudio.SelectedItem is AudioDeviceInfo desk)
        {
            Config.DesktopAudioDeviceId = desk.Id;
            Config.DesktopAudioDeviceName = PlainAudioName(desk.FriendlyName);
        }
        else if (_desktopAudio.SelectedItem as string == AutomaticAudio)
        {
            // Blank means "restore whatever was default", and that is a real choice the user
            // can make — but only when they actually picked that row. It must never be inferred
            // from an empty selection.
            Config.DesktopAudioDeviceId = "";
            Config.DesktopAudioDeviceName = "";
        }

        Config.DisplayMode = _displayMode.SelectedIndex == 1 ? TvDisplayMode.KeepOthers : TvDisplayMode.TvOnly;
        Config.SteamAfterBigPicture = (SteamExitAction)Math.Max(0, _steamAfter.SelectedIndex);

        Config.SwitchAudio = _switchAudio.Checked;
        Config.EndSessionOnGuideButton = _guideEndsSession.Checked;
        // Forced off while the setting is withdrawn — see the note where its toggle used to be.
        Config.RestartSteamForAudio = false;
        Config.ConfirmClosingGame = _confirmClosingGame.Checked;
        Config.CheckForUpdates = _checkForUpdates.Checked;
        Config.MinimizeToTrayOnClose = _minimizeOnClose.Checked;
        Config.StartSessionOnLaunch = _startOnLaunch.Checked;
        Config.StartSessionOnWake = _startOnWake.Checked;
        Config.StartOnWakeControllerOnly = _startOnWakeControllerOnly.Checked;
        Config.ShowNotifications = _showNotifications.Checked;
        Config.ShowSessionNotifications = _showSessionNotifications.Checked;
        Config.ShowHdrNotifications = _showHdrNotifications.Checked;
        Config.ShowMinimizeNotification = _showMinimizeNotification.Checked;
        Config.ShowProblemNotifications = _showProblemNotifications.Checked;
        Config.ShowActionNotifications = _showActionNotifications.Checked;
        // Index 0 is Automatic; anything else is a real plan.
        Config.ShortcutKeyboard = _shortcutKeyboard.Value;
        Config.ShortcutController = _shortcutController.Value;
        Config.ShortcutControllerVendor = _shortcutController.Vendor;
        Config.ShortcutControllerProduct = _shortcutController.Product;

        Config.ShortcutKeyboardHdr = _shortcutKeyboardHdr.Value;

        Config.MouseControlEnabled = _mouseControl.Checked;
        Config.MouseHoldToActivate = _mouseHold.Checked;
        Config.MouseInGames = _mouseInGames.Checked;
        Config.MouseHoldButton = IndexToHoldButton(Math.Max(0, _mouseHoldButton.SelectedIndex));
        Config.MouseCursorStick = Math.Max(0, _cursorStick.SelectedIndex);
        Config.MouseUseTrackpad = _useTrackpad.Checked;
        Config.MouseSpeed = _mouseSpeed.Value;
        Config.MouseDeadzone = _mouseDeadzone.Value;
        Config.ShortcutControllerHdr = _shortcutControllerHdr.Value;
        Config.ShortcutControllerHdrVendor = _shortcutControllerHdr.Vendor;
        Config.ShortcutControllerHdrProduct = _shortcutControllerHdr.Product;

        Config.StartOnControllerLink = (PadLink)Math.Max(0, _padLink.SelectedIndex);

        Config.ChangePowerPlan = _changePowerPlan.Checked;
        Config.SessionPowerPlan = _powerPlan.SelectedItem is PowerPlan plan
                                ? plan.Id.ToString()
                                : AppConfig.Automatic;
        Config.TurnOffTvOnExit = _turnOffTvOnExit.Checked;
        // Recorded only; the switch wrote it to Windows when it was pressed.
        Config.DisableGameBarButton = _disableGameBarButton.Checked;
        // Forced off while the setting is not offered — see the note where its toggle used to be.
        Config.KeepDisplayAwake = false;
        Config.SilenceNotifications = _silenceNotifications.Checked;
        // Recorded rather than applied: the switch already wrote it to Windows when it was pressed.
        // Kept in the config so the bug report and the home page can say what it is without reading
        // the registry on every refresh.
        Config.EnableGameMode = _enableGameMode.Checked;
        Config.GamePriorityEnabled = _gamePriority.Checked;
        Config.StartOnControllerConnect = _startOnController.Checked;
        Config.OnControllerOff = DisconnectOf(_onControllerOff, DisconnectAction.ComeBack);
        Config.OnControllerLost = DisconnectOf(_onControllerLost, DisconnectAction.Ignore,
                                               most: DisconnectAction.ComeBackAndAsk);
        Config.MuteGameOnDesktop = _muteOnDesktop.Checked;

        // Only a real device sets this; the first entry is "any controller", which is empty.
        if (_triggerPad.SelectedItem is ControllerDevice pad)
        {
            Config.TriggerControllerId = pad.Id;

            // Only remember the name from a device that is actually attached — the offline
            // entry's name already carries the "not connected" suffix, and storing that would
            // see it accumulate every time the settings were saved.
            if (pad.Path.Length > 0) Config.TriggerControllerName = pad.Name;
        }
        else
        {
            Config.TriggerControllerId = "";
            Config.TriggerControllerName = "";
        }

        Config.TvVideoMode = _tvVideoMode.SelectedItem is DisplayMode video ? video.Key : "";

        // Recorded against the display it belongs to as well, so it survives being switched away
        // from and back. TvVideoMode stays the single value a session reads.
        if (_tvDisplay.SelectedItem is DisplayInfo chosenDisplay)
            Config.VideoModeByDisplay[chosenDisplay.DevicePath] = Config.TvVideoMode;

        Config.HdrSwitching = HdrModeOf(_hdrMode);
        if (_hdrHotkeyRemember is not null) Config.HdrHotkeyRemembersGame = _hdrHotkeyRemember.Checked;
        Config.HdrGames = _games
            .Where(g => _checkedGames.Contains(g.Key))
            .Select(g => new HdrGame { Name = g.Name, InstallDir = g.InstallDir })
            .ToList();

        // HdrTags is edited in place by the tag menu, so it needs no gathering here.
        Config.StartWithWindows = _startWithWindows.Checked;
        if (StartupRegistration.IsEnabled() != _startWithWindows.Checked)
            StartupRegistration.Set(_startWithWindows.Checked);

        try
        {
            Config.Save();
            ShowSaved();
            Saved?.Invoke(Config);
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
            Warn(string.Format(Words.WarnSaveFailed, ex.Message));
        }
    }

    private void MarkDirty()
    {
        if (_loading) return;

        _autoSave.Stop();
        _autoSave.Start();
    }

    private void OnHdrDataUpdated()
    {
        if (IsDisposed || !IsHandleCreated) return;

        BeginInvoke(() =>
        {
            _gameList?.Invalidate();
            UpdateHdrBulkLabel();
            if (_listHeader?.Sort == GameSort.Hdr) RebuildGameList(keepView: true);
        });
    }

    private void ShowSaved() => ShowFooterNote("All changes saved");

    /// <summary>
    /// Say something briefly in the footer, where the save confirmation appears.
    ///
    /// One place for transient status rather than a toast: a toast is for things that happen while
    /// the window is closed, and the scan button is pressed with the window open and the user
    /// looking right at it. A line in the footer is quieter and lands where their attention
    /// already is.
    /// </summary>
    /// <summary>Say what a scan turned up, on the page, and take it away again after a few seconds.</summary>
    private void ShowScanNote(string text)
    {
        if (_scanNote is null || _scanNote.IsDisposed)
        {
            // The page was rebuilt out from under it — the footer is still better than silence.
            ShowFooterNote(text);
            return;
        }

        _scanNote.Text = text;
        _scanNoteTimer.Stop();
        _scanNoteTimer.Start();
    }

    private void ShowFooterNote(string text)
    {
        _saveNote.Text = text;
        _savedNoteTimer.Stop();
        _savedNoteTimer.Start();
    }

    /// <summary>Wire every input to the dirty flag by walking the tree, so none can be missed.</summary>
    /// <summary>Controls already wired, so a rebuild does not subscribe to them twice.</summary>
    private readonly HashSet<Control> _wired = [];

    private void WireDirtyTracking(Control? root = null)
    {
        void Walk(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                // The switches, dropdowns and shortcut boxes survive a rebuild, so without this
                // every resize would add another handler to each of them — and MarkDirty, the
                // auto-save and UpdateDependentStates would all run once more per resize the
                // user had done.
                if (child is Dropdown or ToggleSwitch or ShortcutBox && !_wired.Add(child))
                {
                    Walk(child);
                    continue;
                }

                // Anything the user can change has to be listed here, or the change is made,
                // shown on screen, and then quietly thrown away when the window closes — which
                // is what happened to the controller shortcut: recorded, displayed, never saved.
                switch (child)
                {
                    // Dependent states too: a dropdown now decides one (the HDR mode), and a
                    // control that greys its dependants only on a toggle would leave them stale.
                    case Dropdown combo:
                        combo.SelectedIndexChanged += (_, _) => { MarkDirty(); UpdateDependentStates(); Live(child); };
                        break;
                    case ToggleSwitch toggle:
                        toggle.CheckedChanged += (_, _) => { MarkDirty(); UpdateDependentStates(); Live(child); };
                        break;
                    case ShortcutBox box: box.ShortcutChanged += (_, _) => MarkDirty(); break;

                    // A slider is the one control whose effect can be felt as it moves, so it does not
                    // wait for the debounced save — see PointerChanged.
                    case Slider slider: slider.ValueChanged += (_, _) => { MarkDirty(); Live(child); }; break;
                }
                Walk(child);
            }
        }
        Walk(root ?? this);
    }

    /// <summary>
    /// Hand a change straight to the running pointer, when it is one the pointer cares about.
    ///
    /// Kept to a named list rather than pushing on every change: the preview reconfigures the live
    /// pointer, and doing that because an unrelated toggle moved is work nobody asked for.
    /// </summary>
    private void Live(Control changed)
    {
        if (ReferenceEquals(changed, _mouseSpeed) || ReferenceEquals(changed, _mouseDeadzone)
         || ReferenceEquals(changed, _cursorStick) || ReferenceEquals(changed, _useTrackpad)
         || ReferenceEquals(changed, _mouseControl) || ReferenceEquals(changed, _mouseHold)
         || ReferenceEquals(changed, _mouseHoldButton) || ReferenceEquals(changed, _mouseInGames))
            PointerChanged();
    }

    /// <summary>The HDR mode a dropdown is showing. An unset dropdown reads as Off.</summary>
    /// <summary>
    /// The disconnect choice as its enum, clamped. Same shape and same reason as HdrModeOf: a
    /// dropdown with nothing selected reports -1, and the safe reading of "no answer" is the
    /// least surprising behaviour rather than the most.
    /// </summary>
    /// <param name="fallback">
    /// What "nothing selected" means, which differs by question: switching a controller off defaults
    /// to coming back, losing one defaults to doing nothing.
    /// </param>
    /// <param name="most">
    /// The last answer this particular picker offers. The two disconnect questions do not offer the
    /// same list — closing the game is on one and not the other — so the ceiling is per-picker
    /// rather than per-enum.
    /// </param>
    private static DisconnectAction DisconnectOf(Dropdown pick, DisconnectAction fallback,
                                                 DisconnectAction most = DisconnectAction.EndAndClose) =>
        pick.SelectedIndex is var i and >= 0 && i <= (int)most
            ? (DisconnectAction)i
            : fallback;

    private static HdrMode HdrModeOf(Dropdown pick) =>
        pick.SelectedIndex is var i and >= 0 && i <= (int)HdrMode.WholeSession ? (HdrMode)i : HdrMode.Off;

    /// <summary>Grey out options that depend on a parent toggle being on.</summary>
    private void UpdateDependentStates()
    {
        // Shown or hidden before anything is greyed, and inside one freeze: each Visible change
        // reflows the card it sits in, so a card's worth of them done unfrozen is a card's worth
        // of visible relayouts.
        // A rebuilt page disposes the rows the old registration points at, and setting Visible on a
        // disposed control throws — which would take this method, and every toggle on the form, down
        // with it after the first resize. Dropped rather than tracked: the page that replaced them
        // registered its own.
        _nested.RemoveAll(group => group.Rows.Count == 0 || group.Rows.Exists(r => r.IsDisposed));

        // Worked out before anything is touched, because entering Frozen is not free: it forces a
        // redraw of the whole window when it unwinds. Almost no toggle changes a row's visibility,
        // so paying that on every press meant one click flashed the entire page. Now the freeze
        // only happens on the presses that actually rearrange something.
        bool moves = false;

        foreach (var (when, rows) in _nested)
        {
            bool show = when();
            foreach (var row in rows)
                if (row.Visible != show) { moves = true; break; }

            if (moves) break;
        }

        if (moves)
        {
            // Suspended per stack rather than wrapped in Frozen.
            //
            // Frozen switches drawing off for the whole window and, on the way out, forces a redraw
            // of every control on it. That is the right trade for a page rebuild and much too heavy
            // for a parent toggle hiding four rows: the entire window repainted, which reads as a
            // flash of the page rather than as rows appearing. Suspending only the stacks that
            // actually change confines both the relayout and the repaint to the card involved.
            var stacks = new List<Control>();

            foreach (var (_, rows) in _nested)
                foreach (var row in rows)
                    if (row.Parent is { } stack && !stacks.Contains(stack)) stacks.Add(stack);

            foreach (var stack in stacks) stack.SuspendLayout();

            try
            {
                foreach (var (when, rows) in _nested)
                {
                    bool show = when();
                    foreach (var row in rows) row.Visible = show;
                }
            }
            finally
            {
                foreach (var stack in stacks) stack.ResumeLayout(true);
            }
        }

        bool audio = _switchAudio.Checked;
        _tvAudio.Enabled = _desktopAudio.Enabled = audio;

        // "Only when the controller wakes it" is meaningless unless start-on-wake is on.
        _startOnWakeControllerOnly.Enabled = _startOnWake.Checked;

        // The games list belongs to one of the three modes, not to two of them.
        //
        // It used to stay live under "whole session" as well, on the reasoning that the list is also
        // read by the priority boost on the Performance page, so greying it out would make that
        // setting uneditable from a page that has nothing to do with it. That is a real cost and it
        // is the wrong trade: whole-session HDR switches on with Big Picture and off when it closes,
        // so a list of games sitting live underneath it invites the reading that those games are the
        // ones it applies to. A live control that does nothing is worse than a dead one, because
        // nothing on screen says it does nothing. The note below covers the priority case in words.
        bool perGame = HdrModeOf(_hdrMode) == HdrMode.PerGame;

        if (_hdrHotkeyRemember is not null) _hdrHotkeyRemember.Enabled = perGame;

        if (_gameList is not null) _gameList.Enabled = perGame;
        if (_listHeader is not null) _listHeader.Enabled = perGame;
        if (_listBar is not null) _listBar.Enabled = perGame;

        _search.Enabled = perGame;

        if (_actingRow is not null) _actingRow.Enabled = perGame;
        if (_browseButton is not null) _browseButton.Enabled = perGame;
        if (_scanButton is not null) _scanButton.Enabled = perGame;

        // Live only while something reads it. With neither end switched on, the choice of controller
        // decides nothing — see UpdateTriggerPadTitle for the same three states in words.
        _triggerPad.Enabled = _startOnController.Checked
                           || DisconnectOf(_onControllerOff, DisconnectAction.ComeBack) != DisconnectAction.Ignore
                           || DisconnectOf(_onControllerLost, DisconnectAction.Ignore) != DisconnectAction.Ignore;

        // Only about starting, so it follows only that switch.
        _padLink.Enabled = _startOnController.Checked;

        // The mouse sub-options follow the master toggle, and the hold-button picker follows the
        // hold toggle inside that.
        bool mouse = _mouseControl.Checked;
        _mouseSpeed.Enabled = mouse;
        _mouseDeadzone.Enabled = mouse;
        _mouseHold.Enabled = mouse;
        // Dead while hold-to-activate is on, because it genuinely is.
        //
        // With holding switched on, the pointer stands down whenever the button is not held and
        // works over a game whenever it is — the in-games setting is never consulted either way.
        // Its own description says as much ("while held it works over a game too"). Leaving it live
        // is the same fault this file argues against for the HDR game list: a control that does
        // nothing is worse than a dead one, because nothing on screen says it does nothing.
        _mouseInGames.Enabled = mouse && !_mouseHold.Checked;
        _mouseHoldButton.Enabled = mouse && _mouseHold.Checked;
        _cursorStick.Enabled = mouse;
        _useTrackpad.Enabled = mouse;
    }

    // ================= actions =================

    private Input.PadFamily? _shownPadFamily;

    /// <summary>
    /// Redraw the shortcut boxes when the controller in your hands changes.
    ///
    /// They already ask which pad is in use before drawing — a combination is stored as positions, so
    /// one recorded on a DualSense really does fire from the equivalent Xbox buttons, and it should be
    /// drawn in the symbols printed on the pad you are actually holding. What they were missing is any
    /// reason to repaint: a control only redraws when something invalidates it, and picking up the
    /// other controller invalidates nothing. So they sat there showing the last pad to have touched
    /// them, which is exactly the complaint.
    ///
    /// Asked on the same timer as everything else on this page, and only acted on when the answer has
    /// actually changed, so this costs a comparison every couple of seconds and nothing else.
    /// </summary>
    private void RefreshPadGlyphs()
    {
        Input.PadFamily now;

        // Xbox as the fallback is only what FamilyInUse returns when it cannot tell — no pad connected
        // at all — so it stands in for "nothing to follow" rather than claiming an Xbox pad is present.
        try { now = Input.PadShortcut.FamilyInUse(Input.PadFamily.Xbox); }
        catch { return; }

        if (_shownPadFamily == now) return;
        _shownPadFamily = now;

        foreach (var box in new[] { _shortcutController, _shortcutControllerHdr })
            if (!box.IsDisposed) box.Invalidate();
    }

    /// <summary>The three things the footer can be saying, so it is only redone on a change.</summary>
    private enum FooterState { Start, Resume, End }

    private FooterState? _actionShows;

    /// <summary>
    /// The footer button, matched to what is actually happening.
    ///
    /// Three states, not two. "On the TV" and "at the desk" were the only ones this knew about, and
    /// that left the commonest interesting case wearing the wrong label: a game left running behind
    /// the desktop is not a session waiting to be *started*, it is one waiting to be gone back to,
    /// and the difference matters because pressing the button does something different in each case.
    ///
    /// Green to go, red to stop — the same pair the power button on the home page uses, so the
    /// colour says which way it goes before the word is read. Resuming is green for that reason: it
    /// puts you back on the television, whatever the wording.
    /// </summary>
    private void RefreshActionButton()
    {
        var state = SessionNow ? FooterState.End
                  : LeftRunningNow ? FooterState.Resume
                                   : FooterState.Start;

        // The second button follows the same state, and has to be reconsidered even when the state
        // has not changed — it is created hidden and the panel only lays it out once it is shown.
        RefreshCloseGameButton(state);

        if (_actionShows == state) return;
        _actionShows = state;

        bool live = state == FooterState.End;

        _action.Text = state switch
        {
            FooterState.End => "End Couch Session",
            FooterState.Resume => "Resume Session",
            _ => "Start Couch Session",
        };

        _action.Line = Color.Empty;

        // White on both, by request.
        //
        // Worth writing down what that costs, because it was near-black on the green before and the
        // reason was not decorative. Green reads brighter to the eye than red or blue at the same
        // value, so white on this particular green measures about 2.35:1 against roughly 7.5:1 for
        // the near-black — the lowest-contrast text in the window, on the button the window exists
        // for. The fill is deepened below to claw some of that back.
        _action.ForeColor = Color.White;

        // A deeper green under white ink, and only under white ink.
        //
        // Theme.Good stays what it is: it is the colour of every "this is fine" state in the app and
        // is read against a dark surface, where it is right. Here it is a background with text on
        // it, which is a different job — and dropping it a fifth of the way toward black takes white
        // from 2.35:1 to about 3.6:1 while still reading as the same green from across a room.
        _action.Fill = live ? Theme.Bad : Theme.Mix(Theme.Good, Color.Black, 0.20f);

        // A play mark to start, a stop mark to end, in whichever ink the fill needs.
        //
        // Replaced rather than kept, and the old one disposed: this runs on every change of session
        // state for the life of the window, and a GDI+ bitmap abandoned each time is a handle leak in
        // a process that is meant to sit in the tray for weeks.
        var was = _action.Icon;

        _action.Icon = live ? AppIcon.Stop(15, _action.ForeColor) : AppIcon.Play(15, _action.ForeColor);
        was?.Dispose();

        _action.Invalidate();
    }

    /// <summary>
    /// "Close Game", beside the main button and only while there is a game to close.
    ///
    /// Deliberately not called "End Session". There is no session running at this point — that is
    /// the entire situation this state describes — so naming it after one would be describing
    /// something that is not happening, and would read as the opposite of the Resume button beside
    /// it rather than as the separate, smaller thing it is.
    ///
    /// Second in the row, inboard of Resume, so the destructive one is not the button nearest the
    /// corner your hand goes to.
    /// </summary>
    private void RefreshCloseGameButton(FooterState state)
    {
        if (_closeGame is null) return;

        // A game, specifically. "Something is waiting behind the desktop" also covers a Big Picture
        // left minimized with nothing running in it, and offering to close a game there is offering
        // to close nothing.
        bool wanted = state == FooterState.Resume && GameRunningNow;

        if (_closeGame.Visible == wanted) return;

        _closeGame.Visible = wanted;
        SizeFooterButtons();
    }

    /// <summary>
    /// Make the button strip exactly as wide as the buttons in it.
    ///
    /// [BUG] It was a hard-coded 420, chosen when the row held two buttons and noted at the time as
    /// having "room to spare". Adding Close Game put 513 pixels of button into it, and a docked
    /// panel does not push back — it simply clipped, so the leftmost button read "…ttings" instead
    /// of "Reset all settings". A fixed width for a row whose contents come and go was always going
    /// to end here; the arithmetic is three lines and cannot go out of date.
    ///
    /// Hidden controls are skipped, so the strip shrinks again the moment Close Game goes away and
    /// the save-confirmation label beside it gets its room back.
    /// </summary>
    private void SizeFooterButtons()
    {
        if (_footerButtons is null) return;

        int wide = _footerButtons.Padding.Horizontal;

        foreach (Control child in _footerButtons.Controls)
            if (child.Visible) wide += child.Width + child.Margin.Horizontal;

        if (_footerButtons.Width != wide) _footerButtons.Width = wide;
    }

    private void OnCloseGameButton()
    {
        // Asked first, unless the user has switched that off — the same setting the session prompt
        // reads, because it is the same irreversible thing being done from a different button.
        if (_confirmClosingGame.Checked
         && !AskBox.Confirm(this, Words.CloseGameTitle, Words.CloseGameBody,
                            Words.CloseGameYes, Words.CloseGameNo))
            return;

        CloseGameRequested = true;
        Close();
    }

    private void OnActionButton()
    {
        if (SessionNow)
        {
            RevertRequested = true;
            Close();
            return;
        }

        Save(silent: true);

        if (!Config.IsConfigured)
        {
            Warn(Words.WarnPickDisplayFirst);
            return;
        }

        StartRequested = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing inside the debounce window would otherwise discard the last change.
        if (_autoSave.Enabled)
        {
            _autoSave.Stop();
            Save(silent: true);
        }

        // The setting is honoured exactly as set, with nothing overriding it.
        //
        // This used to refuse to quit while Auto HDR or a controller trigger was on, because
        // none of those can fire once the process is gone. That reasoning is sound but it is
        // not the app's call to make: someone who turns off "keep running" has said what they
        // want, and quietly ignoring it is worse than the consequence it was avoiding. Anything
        // that needs the app resident is listed in the log on the way out, so the trade is
        // visible rather than hidden.
        if (!StartRequested && !RevertRequested && !Config.MinimizeToTrayOnClose)
        {
            ExitRequested = true;

            var background = BackgroundFeatures();

            if (background.Count > 0)
                Log.Info("Quitting as asked. These stop working until it is started again: "
                       + string.Join(", ", background));
        }

        base.OnFormClosing(e);
    }

    /// <summary>Enabled settings that only work while the app keeps running.</summary>
    private List<string> BackgroundFeatures()
    {
        var features = new List<string>();

        if (Config.AutoHdrEnabled && Config.HdrGames.Count > 0)
            features.Add(string.Format(Words.FeatureAutoHdr, Config.HdrGames.Count));

        // Also needs the app running: the priority boost acts when a game starts, which means
        // something has to be watching for that.
        if (Config.GamePriorityEnabled && Config.HdrGames.Count > 0)
            features.Add(Words.FeatureGamePriority);

        if (Config.StartOnControllerConnect) features.Add(Words.FeatureControllerOn);

        // Conditional again, now that it is a setting rather than fixed behaviour. Listing something
        // the user has switched off as a reason the app has to keep running is how a list like this
        // stops being read.
        if (Config.OnControllerOff != DisconnectAction.Ignore
         || Config.OnControllerLost != DisconnectAction.Ignore)
            features.Add(Words.FeatureControllerOff);

        return features;
    }

    private void OpenDonatePage()
    {
        // No fixed amount and recurring left open, so the donor picks both:
        //  • Omitting "amount" makes PayPal show its own amount field instead of locking it to a preset
        //    (passing amount=5.00 was exactly what stopped the donor from changing it).
        //  • no_recurring=0 explicitly shows the "make this monthly" option so it can be ticked — or not.
        // The fully reliable version is a hosted Donate button (hosted_button_id) set up in the PayPal
        // dashboard with "let the donor choose" for amount and recurring; drop its id in here once it
        // exists and the query-string quirks go away entirely.
        const string Url = "https://www.paypal.com/donate/?business=alexbrandoncampbell%40gmail.com"
                         + "&item_name=Couch+Session&no_recurring=0&currency_code=USD";
        OpenExternally(Url);
    }

    private void OpenDataFolder()
    {
        try { Directory.CreateDirectory(AppConfig.Directory); } catch { /* best effort */ }
        OpenExternally(AppConfig.Directory);
    }

    /// <summary>
    /// Gather everything a maintainer would ask for, then open the issue form.
    ///
    /// In that order, and with the folder opened in between, because the attachment is the part
    /// people forget. By the time the browser appears the file is already saved, already
    /// selected in Explorer, and the issue body says what to do with it.
    /// </summary>
    private void ReportBug()
    {
        string path;

        try { path = BugReport.Write(Config); }
        catch (Exception ex)
        {
            Log.Error("Writing the bug report failed", ex);
            Warn(string.Format(Words.WarnBugReportFailed, ex.Message));
            return;
        }

        // Selected rather than opened: this is a zip, and opening it would launch whatever the
        // machine uses for archives instead of showing the user where the file is.
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Log.Warn($"Could not show the bug report: {ex.Message}"); }

        OpenExternally(BugReport.IssueUrl);

        if (Actions())
            Toast.Show(Words.NoticeBugReport, string.Format(Words.NoticeBugReportDetail,
                                                            Path.GetFileName(path)),
                       Theme.Info, delay: TimeSpan.FromMilliseconds(200));
    }

    private void SaveDiagnosticsReport()
    {
        try { OpenExternally(DisplayDiagnostics.Write(Config)); }
        catch (Exception ex)
        {
            Log.Error("Writing diagnostics failed", ex);
            Warn(string.Format(Words.WarnDiagnosticsFailed, ex.Message));
        }
    }

    private static void OpenExternally(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
            {
                UseShellExecute = true,
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {target}: {ex.Message}");
        }
    }

    private void Warn(string message) => AskBox.Tell(this, AppInfo.Name, message);

    // ================= background refresh =================

    private void StartWatchers()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        _lastDeviceSignature = SafeSignature();

        // The home page reads from settings that live on other pages, so it has to be re-read rather
        // than written once — otherwise turning Auto HDR on and coming back here still says "off".
        _deviceWatch.Tick += (_, _) =>
        {
            RefreshDevicesIfChanged();

            // Windows can change these from outside this window at any moment, and the tiles below
            // read the switches rather than the registry — so without this the home page reports a
            // Game Mode state that stopped being true minutes ago.
            RefreshSecurityState();

            RefreshHomeGlance();
            RefreshActionButton();
            RefreshPadGlyphs();

            if (_controllerPage is { Visible: true }) RefreshPadPresenceNow();

            // Alerts are built during the page build, so a new one (a display vanishing, a warning
            // logged) only appears when the page is rebuilt. Cheap, and it keeps the card stable
            // rather than shuffling rows under the reader every couple of seconds.
            // Nothing is rebuilt while the window is still finding its feet.
            //
            // Displays, controllers, Steam and the startup task are all discovered a beat apart, and
            // each discovery changed what the home page said — so the first few seconds after opening
            // were three or four full rebuilds of the page in quick succession, which is exactly the
            // flicker it looked like. The signatures are deliberately left unread until the window has
            // settled, so the first check afterwards sees every one of those changes at once and pays
            // for a single rebuild.
            if (DateTime.UtcNow - _openedAt < SettleFor) return;

            if (_currentPage == 0 && AlertsChanged()) RebuildPage(0);
        };
        _deviceWatch.Start();

        _gameWatch.Tick += (_, _) => LoadGames();
        _gameWatch.Start();

        _noteTimer.Tick += (_, _) => { _noteTimer.Stop(); _deviceNote.Text = ""; };

        _scanNoteTimer.Tick += (_, _) =>
        {
            _scanNoteTimer.Stop();
            _undoChecked = null;   // the offer expires with the message

            if (_scanNote is { IsDisposed: false } note)
            {
                note.Text = "";
                note.Cursor = Cursors.Default;
                note.ForeColor = Theme.TextFaint;
            }
        };
        _savedNoteTimer.Tick += (_, _) => { _savedNoteTimer.Stop(); _saveNote.Text = ""; };
    }

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing) return;
        BeginInvoke(() => { Thread.Sleep(400); RefreshDevicesIfChanged(); });
    }

    private void RefreshDevicesIfChanged()
    {
        if (IsDisposed || Disposing || _reloading) return;

        var signature = SafeSignature();
        if (signature == _lastDeviceSignature) return;
        _lastDeviceSignature = signature;

        var display = (_tvDisplay.SelectedItem as DisplayInfo)?.DevicePath;
        var tvAudio = (_tvAudio.SelectedItem as AudioDeviceInfo)?.Id;
        var deskAudio = (_desktopAudio.SelectedItem as AudioDeviceInfo)?.Id;

        _reloading = true;
        _loading = true;
        try
        {
            LoadDevices();

            // Restore the pending choice, so a refresh never silently changes what is selected.
            if (display is not null)
            {
                int i = IndexOf(_tvDisplay, o => o is DisplayInfo d && d.DevicePath == display);
                if (i >= 0) _tvDisplay.SelectedIndex = i;
            }
            if (tvAudio is not null)
            {
                int i = IndexOf(_tvAudio, o => o is AudioDeviceInfo a && a.Id == tvAudio);
                if (i >= 0) _tvAudio.SelectedIndex = i;
            }
            if (deskAudio is not null)
            {
                int i = IndexOf(_desktopAudio, o => o is AudioDeviceInfo a && a.Id == deskAudio);
                if (i >= 0) _desktopAudio.SelectedIndex = i;
            }
        }
        finally { _loading = false; _reloading = false; }

        // Always re-read the modes after a device refresh, rather than relying on the selection
        // event to do it.
        //
        // The list was cleared and refilled, so the same index can now mean a different screen
        // — and when the restored position happens to match the old one, SelectedIndex sees no
        // change and raises nothing. The resolutions would then still be the previous display's.
        RefreshVideoModes();

        int count = _tvDisplay.Items.OfType<DisplayInfo>().Count();
        _deviceNote.Text = string.Format(count == 1 ? Words.DeviceNoteOne : Words.DeviceNoteMany, count);
        _noteTimer.Stop();
        _noteTimer.Start();
    }

    private static string SafeSignature()
    {
        try
        {
            var displays = string.Join("|", DisplayManager.ListDisplays().Select(d => d.DevicePath + d.IsActive));
            var audio = string.Join("|", AudioManager.ListPlaybackDevices().Select(a => a.Id));
            return displays + "##" + audio;
        }
        catch { return ""; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
            _deviceWatch.Stop(); _deviceWatch.Dispose();
            _gameWatch.Stop(); _gameWatch.Dispose();
            _noteTimer.Stop(); _noteTimer.Dispose();
            _savedNoteTimer.Stop(); _savedNoteTimer.Dispose();
            _autoSave.Stop(); _autoSave.Dispose();
            _scanNoteTimer.Stop(); _scanNoteTimer.Dispose();
            HdrDatabase.Updated -= OnHdrDataUpdated;

            // The watcher outlives this window, so it would otherwise keep a reference to a
            // disposed form and call into it on the next device change.
            if (_pads is not null) _pads.DevicesChanged -= OnControllersChanged;
            _tips.Dispose();
            _icons.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- interop for dragging a borderless window ----

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, int w, int l);

    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ERASE = 0x0004;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_UPDATENOW = 0x0100;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr update, IntPtr region, uint flags);
}

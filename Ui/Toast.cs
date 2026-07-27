using System.Drawing.Drawing2D;

namespace CouchMode.Ui;

/// <summary>
/// A small notification that slides in above everything else.
///
/// Deliberately not a tray balloon: balloons are suppressed by Focus Assist, are easy to miss,
/// and never appear over a fullscreen game — which is exactly when this needs to be seen. This
/// is a topmost, click-through-free layered window drawn in the app's own style.
///
/// It is also shown on a delay. A display switch takes a second or two to settle and the TV
/// may not be showing a picture yet; appearing immediately means appearing on a screen nobody
/// is looking at.
/// </summary>
internal sealed class Toast : Form
{
    /// <summary>
    /// Where a toast sits on screen.
    ///
    /// Named Spot rather than the obvious Anchor: Control already has an Anchor property, and a
    /// nested type of the same name hides it.
    /// </summary>
    public enum Spot
    {
        /// <summary>Bottom left of the primary display. The default, and what switching uses.</summary>
        Corner,

        /// <summary>
        /// Beside the notification area. Only for messages *about* the tray — a toast saying
        /// the app is now in the tray is far more use pointing at where it went than sitting
        /// in the opposite corner of the screen.
        /// </summary>
        Tray,
    }

    private readonly string _title;
    private readonly string _detail;
    private readonly Color _accent;
    private readonly Spot _anchor;
    private readonly System.Windows.Forms.Timer _life = new();

    // Spacing between and around toasts stays fixed; only each toast's own content scales.
    private const int Gap = 10;            // between stacked toasts
    private const int Inset = 24;          // clearance from the screen edge
    private const int TaskbarGap = 12;     // clearance from the taskbar

    // Content is drawn at a scale so it reads from across a room on a TV, and tracks the monitor's
    // DPI when this process renders per-monitor. The base sizes below are the design pixels; every
    // one is multiplied by _scale. See ComputeScale.
    private readonly float _scale = ComputeScale();
    private readonly int _width, _pad, _chip, _textLeft, _closeGlyph, _closeInset, _boxRadius;
    private readonly Font _titleFont, _detailFont;
    private readonly Bitmap _logo;

    private bool _closeHover;

    private int S(int v) => (int)Math.Round(v * _scale);

    private Rectangle CloseRect => new(Width - _closeInset - _closeGlyph, _closeInset, _closeGlyph, _closeGlyph);

    private int TextWidth => _width - _textLeft - _pad - (_closeGlyph + S(6));

    private const TextFormatFlags TextFlags =
        TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    /// <summary>Toasts currently on screen, oldest first. See Restack.</summary>
    private static readonly List<Toast> Live = [];

    private Toast(string title, string detail, Color accent, Spot anchor)
    {
        _title = title;
        _detail = detail;
        _accent = accent;
        _anchor = anchor;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Surface;
        Cursor = Cursors.Hand;   // the whole toast dismisses on a click

        // Scaled layout, worked out once from _scale.
        _width = S(340);
        _pad = S(16);
        _chip = S(38);
        _textLeft = _pad + _chip + S(12);
        _closeGlyph = S(11);
        _closeInset = S(11);
        _boxRadius = S(8);
        _titleFont = Scaled(Theme.BodySemi);
        _detailFont = Scaled(Theme.Small);

        // The brand mark on the left, at the chip's size — so every notification reads as ours at a
        // glance instead of a generic coloured dot. Built once here; the status colour rides along as
        // a small badge in OnPaint.
        _logo = AppIcon.Monogram(_chip);

        // Measured, not assumed.
        //
        // A fixed height meant a longer message was quietly clipped or ellipsised — and the
        // message is the entire point of the notification. The detail wraps and the box grows
        // to fit however many lines that turns out to be.
        // [BUG] The title was measured at Size.Empty, which asks "how wide would this be on one
        // line?" and answers without reference to the box it has to fit in. Anything longer than the
        // toast was then drawn into a rectangle narrower than its own measurement and simply cut off
        // mid-word, which is the failure the note above says was fixed — it was fixed for the detail
        // only, and the title kept the old behaviour for a while afterwards.
        //
        // Measured against the real width now, with the same wrapping the detail gets, so a long
        // title takes two lines and the box grows for them.
        int titleHeight = TextRenderer.MeasureText(_title, _titleFont,
                                                   new Size(TextWidth, 0),
                                                   TextFlags | TextFormatFlags.WordBreak).Height;

        int detailHeight = TextRenderer.MeasureText(_detail, _detailFont,
                                                    new Size(TextWidth, 0),
                                                    TextFlags | TextFormatFlags.WordBreak).Height;

        int content = titleHeight + S(4) + detailHeight;

        Size = new Size(_width, Math.Max(_chip, content) + _pad * 2);

        // Corners come from the compositor, not a colour key.
        //
        // The old approach painted magenta outside the rounded shape and asked Windows to treat
        // that colour as transparent. Colour-keying is all-or-nothing per pixel, so the
        // antialiased edge — part magenta, part surface — survived as a bright fringe. That
        // fringe was the odd outline.
        WindowCorners.Round(this, _boxRadius);

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint, true);

        DoubleBuffered = true;
    }

    /// <summary>A copy of a theme font at the toast's scale. Owned by the toast; freed on dispose.</summary>
    private Font Scaled(Font source)
    {
        var scaled = new Font(source.Name, source.SizeInPoints * _scale, source.Style, GraphicsUnit.Point);
        source.Dispose();
        return scaled;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont?.Dispose();
            _detailFont?.Dispose();
            _logo?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// How much to enlarge a toast: the monitor's own scaling, or an enlargement for panels wider than
    /// 2K, whichever asks for more. Unchanged at 2K and below. See <see cref="UiScale"/>.
    /// </summary>
    private static float ComputeScale() => UiScale.ForNotifications();

    /// <summary>Never steal focus — the user may be mid-game or typing.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    /// <summary>
    /// The UI thread's anchor. Set once at startup.
    ///
    /// Notifications are raised from the worker thread that performs the switch, and neither a
    /// Form nor a WinForms Timer works there: the timer posts WM_TIMER to the calling thread's
    /// queue, and a thread-pool thread has no message loop to drain it, so the tick never
    /// arrives and nothing is ever shown. Every call is therefore hopped to the UI thread here
    /// rather than at each call site, so no future caller can reintroduce the bug.
    /// </summary>
    private static Control? _ui;

    public static void BindTo(Control uiAnchor) => _ui = uiAnchor;

    /// <summary>
    /// Show a toast, by default in the corner of the primary display.
    /// </summary>
    /// <param name="delay">How long to wait before appearing, so a display switch can settle.</param>
    /// <param name="anchor">Where to put it. Corner unless the message is about the tray.</param>
    public static void Show(string title, string detail, Color accent,
                            TimeSpan? delay = null, TimeSpan? duration = null,
                            Spot anchor = Spot.Corner)
    {
        if (_ui is { IsHandleCreated: true } ui && ui.InvokeRequired)
        {
            ui.BeginInvoke(() => Show(title, detail, accent, delay, duration, anchor));
            return;
        }

        var wait = delay ?? TimeSpan.FromMilliseconds(1200);
        var live = duration ?? TimeSpan.FromSeconds(7);

        var timer = new System.Windows.Forms.Timer { Interval = (int)wait.TotalMilliseconds };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();

            try { Present(title, detail, accent, live, anchor); }
            catch (Exception ex) { Log.Warn($"Could not show the notification: {ex.Message}"); }
        };
        timer.Start();
    }

    private static void Present(string title, string detail, Color accent, TimeSpan duration,
                                Spot anchor)
    {
        var toast = new Toast(title, detail, accent, anchor);

        Live.Add(toast);

        toast._life.Interval = (int)duration.TotalMilliseconds;
        toast._life.Tick += (_, _) =>
        {
            toast._life.Stop();
            toast.Close();
            toast.Dispose();
        };

        Restack();

        toast.Show();
        toast.BringToFront();
        toast._life.Start();
    }

    /// <summary>
    /// Handles of the toasts currently on screen. Big Picture's front guard re-asserts itself
    /// topmost five times a second during a session, which would otherwise bury a toast that
    /// appeared over it — most visibly the "HDR off" one, which lands just as a game closes and Big
    /// Picture returns to the front. The guard passes these into a single deferred z-order update so
    /// Big Picture and the toasts are re-stacked together in one commit, rather than the toast being
    /// lifted back in a second call that makes it flicker.
    /// </summary>
    public static IReadOnlyList<IntPtr> LiveHandles()
    {
        var handles = new List<IntPtr>(Live.Count);
        foreach (var toast in Live)
        {
            try { if (toast.IsHandleCreated && toast.Visible) handles.Add(toast.Handle); }
            catch { /* the toast may be closing */ }
        }
        return handles;
    }

    /// <summary>
    /// Lay the live toasts out one above the other.
    ///
    /// Entering a couch session raises two at once — the switch itself and HDR coming on — and
    /// they used to be given the same coordinates, so the second landed exactly on top of the
    /// first and the first was never read. They are stacked upward from the corner instead,
    /// newest nearest the bottom, and the stack closes up as each one expires.
    ///
    /// Positioned on whichever display is primary at that moment: during a session that is the
    /// TV, which is where the user is looking.
    /// </summary>
    private static void Restack()
    {
        // Two independent stacks. A corner toast and a tray toast can be up at the same time
        // and they are nowhere near each other, so sharing one running offset would leave a
        // gap in one stack for every toast in the other.
        StackInCorner(Live.Where(t => !t.IsDisposed && t._anchor == Spot.Corner).ToList());
        StackByTray(Live.Where(t => !t.IsDisposed && t._anchor == Spot.Tray).ToList());
    }

    /// <summary>Unchanged: bottom left of whichever display is primary at that moment.</summary>
    private static void StackInCorner(List<Toast> toasts)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);

        // Bottom left, deliberately: the bottom right is where Windows puts its own
        // notifications, and two stacks fighting over the same corner is worse than either.
        int bottom = area.Bottom - Inset;

        // Newest at the bottom, so the one that just arrived is where the eye already is.
        for (int i = toasts.Count - 1; i >= 0; i--)
        {
            toasts[i].Location = new Point(area.Left + Inset, bottom - toasts[i].Height);
            bottom -= toasts[i].Height + Gap;
        }
    }

    /// <summary>
    /// Beside the notification area, wherever that turns out to be.
    ///
    /// The taskbar can be docked to any of the four edges, so the toast is placed on the inward
    /// side of the tray and the stack grows away from the taskbar — never across it and never
    /// off the screen. If the shell does not answer, this falls back to the corner rather than
    /// putting the toast somewhere invented.
    /// </summary>
    private static void StackByTray(List<Toast> toasts)
    {
        if (toasts.Count == 0) return;

        if (TrayLocation.Find() is not { } spot)
        {
            StackInCorner(toasts);
            return;
        }

        var tray = spot.Bounds;
        var screen = Screen.FromRectangle(tray).WorkingArea;

        // Grows downward only when the taskbar is along the top, since that is the one case
        // where "away from the taskbar" means down.
        bool downward = spot.Docked == TrayLocation.Edge.Top;

        int cursor = spot.Docked switch
        {
            TrayLocation.Edge.Top => tray.Bottom + TaskbarGap,
            TrayLocation.Edge.Bottom => tray.Top - TaskbarGap,

            // On a vertical taskbar the tray is at the bottom of the bar, so the toasts still
            // rise from there — only the horizontal placement changes.
            _ => Math.Min(tray.Bottom, screen.Bottom - Inset),
        };

        // Newest nearest the taskbar, so the one that just arrived is closest to the thing it
        // is talking about.
        for (int i = toasts.Count - 1; i >= 0; i--)
        {
            var toast = toasts[i];

            int x = spot.Docked switch
            {
                // Right-hand edge lined up with the tray's, so it reads as belonging to it.
                TrayLocation.Edge.Top or TrayLocation.Edge.Bottom => tray.Right - toast.Width,
                TrayLocation.Edge.Left => tray.Right + TaskbarGap,
                _ => tray.Left - TaskbarGap - toast.Width,
            };

            int y = downward ? cursor : cursor - toast.Height;

            // Clamped last, so a tray near a screen corner cannot push any part of the toast
            // off the display it is meant to be read on.
            toast.Location = new Point(
                Math.Clamp(x, screen.Left + Inset, Math.Max(screen.Left + Inset, screen.Right - toast.Width - Inset)),
                Math.Clamp(y, screen.Top + Inset, Math.Max(screen.Top + Inset, screen.Bottom - toast.Height - Inset)));

            cursor += downward ? toast.Height + Gap : -(toast.Height + Gap);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // A gradient backdrop in the app's palette — violet easing into deep blue, corner to corner.
        // Painted across the whole client; the window's rounded region trims the corners. The brush
        // rectangle is inflated a pixel so GDI+ does not wrap the gradient at the very edges.
        using (var backdrop = new LinearGradientBrush(
                   new Rectangle(-1, -1, Width + 2, Height + 2),
                   Color.FromArgb(48, 41, 72),   // dark violet, top-left
                   Color.FromArgb(20, 24, 40),   // deep blue, bottom-right
                   LinearGradientMode.ForwardDiagonal))
        {
            g.FillRectangle(backdrop, 0, 0, Width, Height);
        }

        // The brand logo on the left, vertically centred against the whole box, which now varies in
        // height with the message.
        float chipTop = (Height - _chip) / 2f;

        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(_logo, new Rectangle(_pad, (int)Math.Round(chipTop), _chip, _chip));

        // The status colour rides along as a small badge on the logo's corner, so on/off still reads
        // at a glance without losing the mark. A surface-coloured ring lifts it off the tile.
        int badge = S(11);
        float bx = _pad + _chip - badge * 0.72f;
        float by = chipTop + _chip - badge * 0.72f;
        using (var ring = new SolidBrush(Theme.Surface))
            g.FillEllipse(ring, bx - S(2), by - S(2), badge + S(4), badge + S(4));
        using (var dot = new SolidBrush(_accent))
            g.FillEllipse(dot, bx, by, badge, badge);

        // Drawn with exactly the flags and width the height was measured with, so what fits in
        // the measurement fits on screen. No ellipsis: the box was sized to hold all of it.
        int titleHeight = TextRenderer.MeasureText(g, _title, _titleFont,
                                                   new Size(TextWidth, 0),
                                                   TextFlags | TextFormatFlags.WordBreak).Height;

        TextRenderer.DrawText(g, _title, _titleFont,
                              new Rectangle(_textLeft, _pad, TextWidth, titleHeight),
                              Theme.Text, TextFlags | TextFormatFlags.WordBreak);

        TextRenderer.DrawText(g, _detail, _detailFont,
                              new Rectangle(_textLeft, _pad + titleHeight + S(4),
                                            TextWidth, Height - _pad * 2 - titleHeight - S(4)),
                              Theme.TextDim, TextFlags | TextFormatFlags.WordBreak);

        // A small X, top-right: brighter on hover so it reads as the thing to click, though a
        // click anywhere on the toast dismisses it.
        var x = CloseRect;
        using var pen = new Pen(_closeHover ? Theme.Text : Theme.TextFaint, 1.6f * _scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawLine(pen, x.Left, x.Top, x.Right, x.Bottom);
        g.DrawLine(pen, x.Right, x.Top, x.Left, x.Bottom);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool over = CloseRect.Contains(e.Location);
        if (over != _closeHover) { _closeHover = over; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_closeHover) { _closeHover = false; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Dismiss();
        base.OnMouseDown(e);
    }

    private bool _closing;

    /// <summary>Close now, from a click, without waiting for the life timer.</summary>
    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;

        try { _life.Stop(); } catch { /* already gone */ }
        Close();
        Dispose();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Live.Remove(this);
        _life.Dispose();

        // The gap this leaves closes up, rather than sitting empty until the rest expire.
        Restack();

        base.OnFormClosed(e);
    }

    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
}

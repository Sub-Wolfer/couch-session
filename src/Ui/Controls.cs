using System.Drawing.Drawing2D;

namespace CouchMode.Ui;

/// <summary>A rounded surface panel. The basic building block of every page.</summary>
internal sealed class Card : Panel
{
    public int Radius { get; set; } = 10;

    /// <summary>Inset around the row stack. The card's height is its rows plus this.</summary>
    public int Pad { get; set; }

    /// <summary>
    /// Height comes from the layout pass, not from a SizeChanged handler.
    ///
    /// Relying on the event meant the height was only correct if that event happened to fire —
    /// and during construction, or a rebuild, it often had not yet. Doing it here means any
    /// layout pass at all, from any source, leaves the card the right size.
    /// </summary>
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);

        if (Pad > 0 && Controls["rows"] is { } rows)
        {
            int wanted = rows.Bottom + Pad;
            if (Height != wanted) Height = wanted;
        }
    }
    public Color Fill { get; set; } = Theme.Surface;
    public Color Border { get; set; } = Theme.Line;

    /// <summary>
    /// A row to mark, in this card's own coordinates. Empty for none.
    ///
    /// Lives on the card rather than on the row because the card is what actually paints this area —
    /// every custom control on it fills its own background with the card's surface, so a colour set on
    /// a row is painted over by each of its children and survives only in the gaps between them.
    /// </summary>
    public Rectangle Highlight
    {
        get => _highlight;
        set
        {
            if (_highlight == value) return;

            _highlight = value;
            Invalidate(true);   // children too: they draw the surface, so they draw this
        }
    }

    private Rectangle _highlight;

    /// <summary>
    /// The row the pointer is over, in this card's own coordinates. Empty for none.
    ///
    /// Kept apart from <see cref="Highlight"/> because the two answer different questions — one says
    /// "this is the row you asked to be shown", the other "a click will land here" — and a row can be
    /// both at once. Lives on the card for the same reason Highlight does: every control on it paints
    /// the card's own surface, so anything set behind them is covered by each in turn.
    /// </summary>
    public Rectangle Hover
    {
        get => _hover;
        set
        {
            if (_hover == value) return;

            var old = _hover;
            _hover = value;

            // The two bands only, never the whole card.
            //
            // Hover changes on every pointer move between rows, so it cannot cost what Highlight
            // costs — Highlight fires once per jump and can afford to invalidate everything. Children
            // are included because the wash sits behind them and they paint the surface it is drawn
            // on, but the region is one row rather than a page of them.
            InvalidateBand(old);
            InvalidateBand(_hover);
        }
    }

    private void InvalidateBand(Rectangle area)
    {
        if (area.IsEmpty) return;

        area.Inflate(3, 3);
        Invalidate(area, true);
    }

    private Rectangle _hover;

    /// <summary>
    /// Draw the hovered row: a wash, and nothing else.
    ///
    /// Deliberately the same weight as the sidebar's own hover, which lifts the rail by five percent
    /// and draws no edge. A settings row is a much larger shape than a nav item, so an outline that
    /// reads as gentle around a 38px tab reads as a box being drawn around half the screen — the
    /// same ink over a longer perimeter is simply more of it.
    ///
    /// A fill sits *behind* the row's contents, and every control on these cards paints the card's
    /// own surface — so this only appears if the row's children repaint with it. That is affordable
    /// because the invalidate is limited to this band rather than the whole card, and because the
    /// two containers a row is built from compose off-screen. Both halves matter: without the first
    /// every pointer move repaints a page, and without the second the containers paint their parent
    /// and then themselves, which is the two-stage paint the eye reads as a flicker.
    /// </summary>
    public static void PaintHover(Graphics g, Rectangle area)
    {
        if (area.Width <= 0 || area.Height <= 0) return;

        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var box = new RectangleF(area.X + 0.5f, area.Y + 0.5f, area.Width - 1f, area.Height - 1f);

        Theme.FillRounded(g, box, 8, Theme.Mix(Theme.Surface, Color.White, 0.055f));

        g.SmoothingMode = old;
    }

    /// <summary>
    /// Draw the marked row. Shared with Theme.PaintBackdrop so the card and everything sitting on it
    /// draw the same shape in the same place.
    ///
    /// Stronger than the hover wash on purpose, and they are not the same kind of thing. Hover is
    /// ambient — it follows the pointer and is gone a moment later, so it should be barely there.
    /// This is the answer to "which of these fourteen near-identical rows did I ask for?", it appears
    /// once, and it has to win at a glance or the jump that produced it was pointless.
    /// </summary>
    public static void PaintHighlight(Graphics g, Rectangle area)
    {
        if (area.Width <= 0 || area.Height <= 0) return;

        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var box = new RectangleF(area.X + 0.5f, area.Y + 0.5f, area.Width - 1f, area.Height - 1f);

        Theme.FillRounded(g, box, 8, Theme.Mix(Theme.Surface, Theme.Accent, 0.30f));
        Theme.DrawRounded(g, box, 8, Theme.Mix(Theme.Surface, Theme.Accent, 0.75f), 2f);

        // A solid bar down the leading edge.
        //
        // The fill and the outline are both tints of the card's own colour, and can only depart from
        // it so far before the row stops looking like part of the card. The bar is not competing with
        // anything, so a small amount of full-strength accent does more than a large amount of a
        // diluted one — which is exactly the device the sidebar already uses to mark the open page.
        //
        // It sits in the margin the caller adds for it (see RowBand's lead), not in the row's own
        // padding, so it never crowds the title it is pointing at.
        var bar = new RectangleF(box.Left + 4f, box.Top + 7f, 4f, Math.Max(0f, box.Height - 14f));
        if (bar.Height > 0) Theme.FillRounded(g, bar, 2, Theme.Accent);

        g.SmoothingMode = old;
    }

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.PaintBackdrop(g, this);

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

        var (top, bottom) = Theme.Sheen(Fill, Theme.CardSheen);
        Theme.FillRoundedGradient(g, bounds, Radius, top, bottom);
        Theme.DrawRounded(g, bounds, Radius, Border);

        // Hover under the mark, so a row that is both still reads as the one that was asked for.
        if (!_hover.IsEmpty && _hover != _highlight) PaintHover(g, _hover);
        if (!_highlight.IsEmpty) PaintHighlight(g, _highlight);
    }
}

/// <summary>
/// A transparent stack that composes off-screen.
///
/// WinForms has no real transparency: a control with a transparent background paints its parent
/// first and then itself, two separate passes over the same pixels. At rest nobody notices. The
/// moment something repaints them on every pointer move — a row hover — both passes become visible,
/// and that is the flicker.
///
/// Double buffering does not remove the second pass, it moves both off-screen, so what reaches the
/// display is the finished result. Used for the containers a settings row is built from, which are
/// the only ones repainted that often.
/// </summary>
internal sealed class BufferedStack : FlowLayoutPanel
{
    public BufferedStack()
    {
        DoubleBuffered = true;
        BackColor = Color.Transparent;
    }
}

/// <summary>The two-column equivalent, for the row itself. See <see cref="BufferedStack"/>.</summary>
internal sealed class BufferedTable : TableLayoutPanel
{
    public BufferedTable()
    {
        DoubleBuffered = true;
        BackColor = Color.Transparent;
    }
}

/// <summary>
/// The pill toggle from the mock-ups.
///
/// A CheckBox cannot be restyled into this shape — its glyph is drawn by the OS theme — so
/// this is painted from scratch. It keeps CheckBox's semantics (Checked, CheckedChanged) so
/// it can be swapped in wherever a CheckBox was used.
/// </summary>
internal sealed class ToggleSwitch : Control
{
    private bool _checked;
    private bool _hover;

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Set the state without raising CheckedChanged — for reconciling one switch against
    /// another (e.g. two mutually exclusive options) without kicking off a cascade of handlers.</summary>
    public void SetQuietly(bool value)
    {
        if (_checked == value) return;
        _checked = value;
        Invalidate();
    }

    /// <summary>
    /// Draw this one as a mode switch rather than a setting.
    ///
    /// Bigger, and green when on instead of the violet every other switch uses. There is exactly one
    /// of these in the app and it deserves the distinction: every other toggle changes what happens
    /// during a session, while this one decides whether sessions exist at all — it empties two pages
    /// out of the rail, takes the main button off the footer and halves the grid on the home page.
    ///
    /// A switch that reshapes the window should not look identical to the switch beside it that
    /// silences a notification.
    /// </summary>
    public bool Prominent
    {
        get => _prominent;
        set
        {
            if (_prominent == value) return;
            _prominent = value;
            Size = value ? new Size(56, 30) : new Size(44, 24);
            Invalidate();
        }
    }

    private bool _prominent;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);

        // Every second click in a fast sequence arrives as WM_LBUTTONDBLCLK, not as another
        // click — so with StandardClick the switch ignored it and appeared to have a cooldown.
        // Acting on the button going down instead makes it respond as fast as it is clicked.
        SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);

        Size = new Size(44, 24);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Enabled && e.Button == MouseButtons.Left) Checked = !Checked;
        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Enabled && e.KeyCode is Keys.Space or Keys.Enter) { Checked = !Checked; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.PaintBackdrop(g, this);

        var track = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

        Color on = _prominent
            ? (_hover ? Theme.Mix(Theme.Good, Color.White, 0.15f) : Theme.Good)
            : (_hover ? Theme.AccentHi : Theme.Accent);
        Color off = _hover ? Theme.Mix(Theme.SurfaceHi, Theme.Line, 0.6f) : Theme.SurfaceHi;
        Color fill = !Enabled ? Theme.Mix(Theme.Surface, Theme.Line, 0.5f) : _checked ? on : off;

        var (trackTop, trackBottom) = Theme.Sheen(fill, _checked ? 0.10f : 0.05f);
        Theme.FillRoundedGradient(g, track, Height / 2f, trackTop, trackBottom);

        if (!_checked) Theme.DrawRounded(g, track, Height / 2f, Theme.Line);

        // knob
        float pad = 3f;
        float size = Height - pad * 2;
        float x = _checked ? Width - size - pad : pad;

        using var knob = new SolidBrush(Enabled ? Color.White : Theme.TextFaint);
        g.FillEllipse(knob, x, pad, size, size);
    }
}

/// <summary>A flat, rounded button that matches the dark theme.</summary>
internal sealed class FlatButton : Control
{
    public int Radius { get; set; } = 8;
    public Color Fill { get; set; } = Theme.SurfaceHi;
    public Color FillHover { get; set; } = Color.Empty;
    public Color Line { get; set; } = Theme.Line;
    public Image? Icon { get; set; }
    public int IconGap { get; set; } = 8;

    /// <summary>
    /// Draw this as the primary action rather than as one more button.
    ///
    /// Three changes, and they only make sense together. The shape becomes a pill, because a 34-pixel
    /// button with an 8-pixel radius is the default shape of every button in every toolkit and reads
    /// as a control rather than as an invitation. A glow in the button's own colour is laid under it,
    /// which is the one cue that actually separates a primary action from a secondary one on a dark
    /// surface — a shadow says "raised", but a *tinted* shadow says "this thing is lit". And a rim
    /// light along the top edge gives the fill somewhere for the light to come from, so the gradient
    /// underneath reads as a curved surface instead of a colour ramp.
    ///
    /// Off by default. Exactly one button in a window should have this; two competing primaries is
    /// the same as none.
    /// </summary>
    public bool Emphasis { get; set; }

    /// <summary>
    /// Transparent margin inside the control, reserved for the glow.
    ///
    /// A Control cannot paint outside its own bounds, so a glow that spreads past the button has to
    /// be given room *inside* a control that is larger than the button drawn in it. Callers that set
    /// <see cref="Emphasis"/> therefore size the control to the button plus twice this, and pull the
    /// difference back out with a negative margin so nothing around it moves.
    /// </summary>
    public const int Bleed = 9;

    private int Inset => Emphasis ? Bleed : 0;

    private bool _hover;
    private bool _down;

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;
        ForeColor = Theme.Text;
        Font = Theme.BodySemi;
        Cursor = Cursors.Hand;
        Size = new Size(120, 32);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.PaintBackdrop(g, this);

        var hover = FillHover == Color.Empty ? Theme.Mix(Fill, Color.White, 0.08f) : FillHover;
        var fill = !Enabled ? Theme.Mix(Fill, Theme.Window, 0.5f)
                 : _down ? Theme.Mix(Fill, Color.Black, 0.12f)
                 : _hover ? hover : Fill;

        int pad = Inset;

        var bounds = new RectangleF(pad + 0.5f, pad + 0.5f, Width - pad * 2 - 1f, Height - pad * 2 - 1f);

        // A pill when this is the primary action. Worth being literal about why: the radius is half
        // the height rather than a larger fixed number, so the shape stays a pill at any size the
        // caller picks instead of turning back into a rounded rectangle the moment the button grows.
        float radius = Emphasis ? bounds.Height / 2f : Radius;

        if (Emphasis && Enabled) Glow(g, bounds, radius);

        // More lift on the primary button than the rest. The shallow 7% sheen is right for a row of
        // secondary buttons, where anything stronger bands visibly on a dark theme; on a saturated
        // fill at pill size it disappears, and the surface goes flat.
        //
        // Taken from 15% to 10%. Fifteen put a visible light-to-dark ramp down the pill, which is the
        // look of a button trying to appear physical, and against flat cards and flat rows it read as
        // the one component from an older toolkit. Ten still separates the top edge from the bottom
        // without the gradient becoming the thing you notice.
        var (top, bottom) = Theme.Sheen(fill, Emphasis ? 0.10f : 0.07f);
        Theme.FillRoundedGradient(g, bounds, radius, top, bottom);

        // The rim light. Drawn all the way round rather than only across the top: clipping it to the
        // top half would need a hard-edged rectangular clip, and a hard edge cutting across an
        // anti-aliased curve leaves a visible nick at the two points where it crosses.
        if (Emphasis)
        {
            // Softened from 58 at rest. A bright hairline all the way round drew the pill's outline
            // as hard as its fill, so the eye read an edge rather than a surface. Enough is left to
            // catch the top curve, and the hover value still lifts on approach.
            int strength = !Enabled ? 0 : _down ? 18 : _hover ? 58 : 38;

            if (strength > 0)
                Theme.DrawRounded(g, RectangleF.Inflate(bounds, -0.5f, -0.5f), radius - 0.5f,
                                  Color.FromArgb(strength, 255, 255, 255));
        }

        if (Line != Color.Empty) Theme.DrawRounded(g, bounds, radius, Line);

        var textSize = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        int iconW = Icon?.Width ?? 0;
        int gap = Icon is not null && Text.Length > 0 ? IconGap : 0;

        // Centred on the button, not on the control. They are the same thing until the glow bleed
        // makes them different, and then the label sits off-centre in the pill by the width of it.
        int left = (int)(bounds.X + (bounds.Width - (iconW + gap + textSize.Width)) / 2f);
        int middle = (int)(bounds.Y + bounds.Height / 2f);

        if (Icon is not null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(Icon, new Rectangle(left, middle - Icon.Height / 2, Icon.Width, Icon.Height));
        }

        var textRect = new Rectangle(left + iconW + gap, middle - textSize.Height / 2,
                                     textSize.Width, textSize.Height);

        TextRenderer.DrawText(g, Text, Font, textRect,
                              Enabled ? ForeColor : Theme.TextFaint,
                              TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    /// <summary>
    /// The tinted glow under the primary button.
    ///
    /// Built by stacking translucent copies of the same pill, each one slightly larger and slightly
    /// fainter than the last. That is a cheap approximation of a blur, and it is here because the
    /// real thing is not available: GDI+ has no blur, and doing it properly would mean rendering to
    /// an off-screen bitmap and convolving it by hand on every repaint of a button that repaints on
    /// every mouse move. Eight rings at a few percent alpha are indistinguishable at this size.
    ///
    /// Offset downward, because light in this window comes from above — the sheen on every card and
    /// the rim light on this button both assume it, and a glow centred on the shape would contradict
    /// them. It tightens and dims on press, which is what makes the button feel like it moves without
    /// anything actually moving.
    /// </summary>
    private void Glow(Graphics g, RectangleF bounds, float radius)
    {
        const int Rings = 8;

        float spread = _down ? Bleed * 0.55f : _hover ? Bleed : Bleed * 0.8f;
        float drop = _down ? 0.5f : 2f;

        // Quieter at rest, brighter on approach. At 32 the halo was legible as a shape in its own
        // right, which on a violet fill against a near-black footer looked like a glow effect rather
        // than like light coming off the button. Twenty-two reads as lift; the hover value is where
        // the emphasis now lives, so the button answers the pointer instead of shouting at it.
        int peak = _down ? 16 : _hover ? 40 : 22;

        for (int i = Rings; i >= 1; i--)
        {
            float grow = spread * i / Rings;

            var halo = RectangleF.Inflate(bounds, grow, grow);
            halo.Offset(0, drop);

            // Fades to nothing at the outside. The innermost ring is fully under the button, so the
            // alpha piling up in the middle is never seen.
            int alpha = (int)(peak * (1f - (i - 1) / (float)Rings));
            if (alpha < 1) continue;

            using var brush = new SolidBrush(Color.FromArgb(alpha, Fill));
            using var path = Theme.Rounded(halo, radius + grow);

            g.FillPath(brush, path);
        }
    }
}

/// <summary>A hairline divider between rows inside a card.</summary>
/// <summary>
/// A panel that composes itself and its children off-screen.
///
/// This is where WS_EX_COMPOSITED belongs. Putting it on the ListBox directly stopped the
/// control being created at all — CreateWindowEx refused the combination and WinForms reported
/// it as "Error creating window handle" the moment the Auto HDR page was opened.
///
/// On a container it is both safe and better suited: Windows composes every descendant in
/// bottom-to-top order and blits the result once, so the native ListBox painting inside is
/// covered too — which plain double buffering could never reach.
/// </summary>
internal sealed class BufferedPanel : Panel
{
    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_COMPOSITED = 0x02000000;

            var parameters = base.CreateParams;
            parameters.ExStyle |= WS_EX_COMPOSITED;

            return parameters;
        }
    }
}

internal sealed class Hairline : Panel
{
    public Hairline(int width, int indent = 0)
    {
        Size = new Size(width - indent, 1);
        BackColor = Theme.Hairline;
        Margin = new Padding(indent, 0, 0, 0);
    }
}

/// <summary>A sidebar entry: accent bar, label, and a highlight when it is the current page.</summary>
internal sealed class NavItem : Control
{
    private bool _hover;
    public bool Active { get; set; }

    /// <summary>The line-icon drawn to the left of the label. Set once when the item is built.</summary>
    public NavGlyph Glyph { get; set; }

    public NavItem()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

        Size = new Size(186, 38);
        BackColor = Theme.Rail;
        ForeColor = Theme.TextDim;
        Font = Theme.Body;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Rail);

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

        if (Active)
        {
            Theme.FillRounded(g, bounds, 8, Theme.AccentDim);
            Theme.DrawRounded(g, bounds, 8, Theme.Mix(Theme.Accent, Theme.Rail, 0.45f));
        }
        else if (_hover)
        {
            Theme.FillRounded(g, bounds, 8, Theme.Mix(Theme.Rail, Color.White, 0.05f));
        }

        // A bar on the leading edge, not a dot: it marks the row rather than decorating it, and
        // stays legible from across a room.
        if (Active)
            Theme.FillRounded(g, new RectangleF(0, Height / 2f - 9f, 3, 18), 1.5f, Theme.Accent);

        var glyphColor = Active ? Theme.Text : ForeColor;
        NavGlyphs.Draw(g, Glyph, new RectangleF(14, (Height - 18) / 2f, 18, 18), glyphColor);

        var textRect = new Rectangle(42, 0, Width - 48, Height);
        TextRenderer.DrawText(g, Text, Active ? Theme.BodySemi : Font, textRect,
                              Active ? Theme.Text : ForeColor,
                              TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>The icon shown beside a navigation item.</summary>
internal enum NavGlyph { None, Home, Display, Steam, Controller, Hotkeys, Hdr, Performance, General, About }

/// <summary>
/// Draws the small line-icons in the sidebar. Vectors rather than shipped bitmaps so they stay crisp
/// at any DPI and take their colour from the row state (active vs. resting) with no second asset.
/// </summary>
internal static class NavGlyphs
{
    public static void Draw(Graphics g, NavGlyph glyph, RectangleF r, Color color)
    {
        if (glyph == NavGlyph.None) return;

        float u = r.Width, x = r.X, y = r.Y;
        float w = Math.Max(1.4f, u * 0.09f);

        using var pen = new Pen(color, w)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        using var fill = new SolidBrush(color);

        RectangleF Rect(float ax, float ay, float aw, float ah) => new(x + u * ax, y + u * ay, u * aw, u * ah);
        PointF P(float ax, float ay) => new(x + u * ax, y + u * ay);
        void Line(float x1, float y1, float x2, float y2) => g.DrawLine(pen, P(x1, y1), P(x2, y2));
        void Ring(float ax, float ay, float aw, float ah) => g.DrawEllipse(pen, Rect(ax, ay, aw, ah));
        void Dot(float cx, float cy, float rad) { float d = u * rad * 2; g.FillEllipse(fill, x + u * cx - u * rad, y + u * cy - u * rad, d, d); }

        switch (glyph)
        {
            case NavGlyph.Home:
                Line(0.12f, 0.50f, 0.50f, 0.14f); Line(0.50f, 0.14f, 0.88f, 0.50f);
                Line(0.22f, 0.44f, 0.22f, 0.86f); Line(0.78f, 0.44f, 0.78f, 0.86f);
                Line(0.22f, 0.86f, 0.78f, 0.86f);
                Line(0.42f, 0.86f, 0.42f, 0.62f); Line(0.42f, 0.62f, 0.58f, 0.62f); Line(0.58f, 0.62f, 0.58f, 0.86f);
                break;

            case NavGlyph.Display:
                using (var p = Rounded(Rect(0.06f, 0.12f, 0.88f, 0.56f), u * 0.10f)) g.DrawPath(pen, p);
                Line(0.50f, 0.68f, 0.50f, 0.82f); Line(0.32f, 0.86f, 0.68f, 0.86f);
                break;

            case NavGlyph.Steam:
                Ring(0.06f, 0.20f, 0.62f, 0.62f); Dot(0.37f, 0.51f, 0.10f);
                Ring(0.58f, 0.06f, 0.30f, 0.30f); Line(0.41f, 0.47f, 0.69f, 0.23f);
                break;

            case NavGlyph.Controller:
                using (var p = Rounded(Rect(0.04f, 0.30f, 0.92f, 0.40f), u * 0.18f)) g.DrawPath(pen, p);
                Line(0.22f, 0.50f, 0.34f, 0.50f); Line(0.28f, 0.44f, 0.28f, 0.56f);
                Dot(0.68f, 0.45f, 0.045f); Dot(0.77f, 0.53f, 0.045f);
                break;

            case NavGlyph.Hotkeys:
                using (var p = Rounded(Rect(0.04f, 0.24f, 0.92f, 0.52f), u * 0.10f)) g.DrawPath(pen, p);
                Dot(0.24f, 0.40f, 0.035f); Dot(0.42f, 0.40f, 0.035f); Dot(0.60f, 0.40f, 0.035f); Dot(0.76f, 0.40f, 0.035f);
                Line(0.32f, 0.60f, 0.68f, 0.60f);
                break;

            case NavGlyph.Hdr:
                Ring(0.34f, 0.34f, 0.32f, 0.32f);
                Line(0.50f, 0.06f, 0.50f, 0.18f); Line(0.50f, 0.82f, 0.50f, 0.94f);
                Line(0.06f, 0.50f, 0.18f, 0.50f); Line(0.82f, 0.50f, 0.94f, 0.50f);
                Line(0.17f, 0.17f, 0.26f, 0.26f); Line(0.74f, 0.74f, 0.83f, 0.83f);
                Line(0.74f, 0.26f, 0.83f, 0.17f); Line(0.17f, 0.83f, 0.26f, 0.74f);
                break;

            case NavGlyph.Performance:
                g.DrawArc(pen, Rect(0.10f, 0.22f, 0.80f, 0.80f), 180, 180);
                Line(0.50f, 0.62f, 0.68f, 0.34f); Dot(0.50f, 0.62f, 0.05f);
                break;

            case NavGlyph.General:
                Line(0.12f, 0.28f, 0.88f, 0.28f); Dot(0.66f, 0.28f, 0.06f);
                Line(0.12f, 0.50f, 0.88f, 0.50f); Dot(0.36f, 0.50f, 0.06f);
                Line(0.12f, 0.72f, 0.88f, 0.72f); Dot(0.60f, 0.72f, 0.06f);
                break;

            case NavGlyph.About:
                Ring(0.10f, 0.10f, 0.80f, 0.80f); Dot(0.50f, 0.32f, 0.05f); Line(0.50f, 0.46f, 0.50f, 0.70f);
                break;
        }
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// The power button on the home page: a status light you can press.
///
/// Green while a couch session is running, red while it is not, and pressing it moves between the two.
/// Deliberately the shape everyone already knows from the front of a console or a PC case, because the
/// home page is the one screen that has to be readable from across a room and at a glance — a coloured
/// ring answers "is it on?" before any label has been read.
/// </summary>
internal sealed class PowerButton : Control
{
    private bool _hover;
    private bool _down;

    /// <summary>Whether a session is running. Green when true, red when not.</summary>
    public bool On { get; set; }

    public PowerButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(56, 56);
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Theme.PaintBackdrop(g, this);

        var colour = On ? Theme.Good : Theme.Bad;
        if (_down) colour = Theme.Mix(colour, Color.Black, 0.25f);
        else if (_hover) colour = Theme.Mix(colour, Color.White, 0.18f);

        float size = Math.Min(Width, Height) - 6f;
        var box = new RectangleF((Width - size) / 2f, (Height - size) / 2f, size, size);

        // A soft disc behind the mark, so the symbol reads against the card rather than floating on it.
        using (var fill = new SolidBrush(Theme.Mix(colour, Theme.Surface, 0.82f)))
            g.FillEllipse(fill, box);

        using (var rim = new Pen(colour, 2f))
            g.DrawEllipse(rim, box);

        // The power mark itself: a ring broken at the top, with a stroke rising through the gap.
        float pad = size * 0.30f;
        var arc = new RectangleF(box.X + pad, box.Y + pad, box.Width - pad * 2, box.Height - pad * 2);

        using var mark = new Pen(colour, Math.Max(2f, size * 0.075f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        // Starting at -65° and sweeping 310° leaves the break centred at the top, where the stroke goes.
        g.DrawArc(mark, arc, -65f, 310f);

        float cx = box.X + box.Width / 2f;
        g.DrawLine(mark, cx, box.Y + size * 0.17f, cx, box.Y + size * 0.44f);
    }
}

/// <summary>
/// One line of the home page's setup list: a state dot, a short label, and a click that takes you to
/// the page where it is done.
///
/// The dot is the whole point. A list of sentences has to be read; a column of ticks and empty rings is
/// understood before it is read, and the one empty ring is where the eye lands. Text is kept to a few
/// words for the same reason — this is signage, not documentation.
/// </summary>
internal sealed class StepRow : Control
{
    private bool _hover;

    /// <summary>Whether this step is done. Filled tick when true, hollow ring when not.</summary>
    public bool Done { get; set; }

    /// <summary>The short right-hand note: what it is set to, or what to pick.</summary>
    public string Detail { get; set; } = "";

    public StepRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = Theme.Body;
        Height = 34;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Theme.PaintBackdrop(g, this);

        if (_hover)
            Theme.FillRounded(g, new RectangleF(0, 0, Width, Height), 8, Theme.SurfaceHi);

        var ink = Done ? Theme.TextDim : Theme.Text;
        var mark = Done ? Theme.Good : Theme.Accent;

        // The state dot: a filled tick once done, an open ring while it is still the next thing to do.
        const int D = 18;
        int cy = Height / 2;
        var dot = new RectangleF(10, cy - D / 2f, D, D);

        if (Done)
        {
            using var fill = new SolidBrush(mark);
            g.FillEllipse(fill, dot);

            using var tick = new Pen(Theme.Surface, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(tick,
            [
                new PointF(dot.Left + 4.5f, cy),
                new PointF(dot.Left + 7.5f, cy + 3.5f),
                new PointF(dot.Right - 4.0f, cy - 4.0f),
            ]);
        }
        else
        {
            using var ring = new Pen(mark, 2f);
            g.DrawEllipse(ring, dot);
        }

        var textRect = new Rectangle(38, 0, Width - 48, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, ink,
                              TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        if (Detail.Length == 0) return;

        TextRenderer.DrawText(g, Detail, Theme.Small, textRect, Done ? Theme.TextFaint : mark,
                              TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>
/// A slim progress bar for the setup checklist: a track with a rounded fill, green at complete.
///
/// A number ("2 of 4") says the same thing, but a bar is read pre-attentively — before reading — and
/// research on setup checklists is unambiguous that visible progress is what pulls people through the
/// remaining steps.
/// </summary>
internal sealed class ProgressStrip : Control
{
    /// <summary>0 to 1.</summary>
    public float Fraction { get; set; }

    public ProgressStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;
        Height = 6;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Theme.PaintBackdrop(g, this);

        float r = Height / 2f;
        Theme.FillRounded(g, new RectangleF(0, 0, Width, Height), r, Theme.Input);

        float w = Math.Clamp(Fraction, 0f, 1f) * Width;
        if (w < Height) w = Fraction > 0f ? Height : 0f;   // never a sliver narrower than it is tall

        if (w > 0f)
            Theme.FillRounded(g, new RectangleF(0, 0, w, Height), r,
                              Fraction >= 1f ? Theme.Good : Theme.Accent);
    }
}

/// <summary>
/// One fact on the home page: a dim caption over a bright value.
///
/// Tiles instead of "name: value" lines because a column of prose has to be read top to bottom, while a
/// grid of captioned values is scanned in any order — the reader finds "Auto HDR" by shape and position
/// and never reads the rest. This is how every dashboard worth copying presents small facts.
/// </summary>
internal sealed class GlanceTile : Control
{
    public string Caption { get; set; } = "";
    public string Value { get; set; } = "";

    /// <summary>
    /// A controller combination to draw as buttons instead of the value text. Zero means words.
    ///
    /// "Back + X" is a translation, and a lossy one: the pad in your hands has no word "Back" written
    /// anywhere on it, and X is a different button depending on which pad that is. The symbols are what
    /// is actually printed on the thing, so they need no translating at all — and they are already what
    /// the hotkey pages show, so the home page now agrees with them.
    /// </summary>
    public int Combo { get; set; }

    /// <summary>The pad the combination was recorded on, so it can be decoded back to positions.</summary>
    public ushort Vendor { get; set; }

    public ushort Product { get; set; }

    /// <summary>The family the glyphs were last painted in, so a repaint can be asked for only when
    /// picking up the other pad has actually changed them.</summary>
    private Input.PadFamily _drawnFamily;

    private bool _everDrawnGlyphs;

    /// <summary>
    /// Whether the buttons on this tile would now be drawn in the other console's symbols.
    ///
    /// Asked by the home page's refresh instead of repainting the tile every couple of seconds on the
    /// off-chance. Repainting regardless is what made this row flicker the first time around.
    /// </summary>
    public bool FamilyChanged()
    {
        if (Combo == 0 || !_everDrawnGlyphs) return false;

        var (recorded, _) = Input.PadShortcut.Decode(new Input.PadReading(Combo, Vendor, Product));

        return Input.PadShortcut.FamilyInUse(recorded) != _drawnFamily;
    }

    /// <summary>Dim the value — for a feature that is switched off rather than in trouble.</summary>
    public bool Muted { get; set; }

    /// <summary>Set when clicking goes to the page that owns this setting, which also makes it
    /// look clickable.</summary>
    public bool Actionable { get; set; }

    private bool _hover;

    public GlanceTile()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;
        Size = new Size(220, PreferredHeight());
    }

    /// <summary>
    /// Tall enough for both lines at whatever size the fonts currently are.
    ///
    /// The height was a literal 46 in two places. That is right at 100% scaling and only there: these
    /// tiles are built at run time, so the number is not scaled with the display, while the fonts are —
    /// which puts a machine at 150% in the same position the caption was already in, with the value line
    /// running out of the bottom of the tile instead of the caption running out of its band. Floored at
    /// the old 46 so nothing shrinks on the machines where it was already correct.
    /// </summary>
    public static int PreferredHeight()
    {
        int caption = TextRenderer.MeasureText("Ag", Theme.Small).Height;
        int value = TextRenderer.MeasureText("Ag", Theme.Body).Height;

        return Math.Max(46, 2 + caption + 2 + value + 2);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        if (Actionable) { _hover = true; Cursor = Cursors.Hand; Invalidate(); }
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.PaintBackdrop(g, this);

        // A wash on hover, and only then: twenty-four permanently outlined boxes would read as
        // twenty-four buttons and bury the values, which are the reason the row exists. Lit under the
        // pointer, it says "this one is a way in" at the moment that is worth knowing.
        if (_hover)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRounded(g, new RectangleF(0, 0, Width, Height), 8, Theme.SurfaceHi);
        }

        const TextFormatFlags Flat = TextFormatFlags.Left | TextFormatFlags.NoPrefix
                                   | TextFormatFlags.EndEllipsis;

        // [BUG] The caption used to be given a fixed 16px band and the value a fixed start at y=20.
        // Sixteen is shorter than the line height of the font it is drawn in, so the bottom of the band
        // fell between the baseline and the descender — and TextRenderer clips to the rectangle it is
        // handed. Captions without a descender looked perfect, which is why this survived: it only shows
        // on a g, j, p, q or y, and until "Before closing a game" arrived no caption on this page had
        // one. "Pad that starts it", "Game priority", "Admin prompts" — all fine, all luck.
        //
        // Measured from the font instead, against a string with both an ascender and a descender so the
        // answer does not depend on which letters this particular caption happens to use.
        int captionH = TextRenderer.MeasureText(g, "Ag", Theme.Small, Size.Empty, Flat).Height;

        TextRenderer.DrawText(g, Caption, Theme.Small, new Rectangle(0, 2, Width, captionH),
                              Theme.TextFaint, Flat);

        var ink = Muted ? Theme.TextFaint : Theme.Text;

        int below = 2 + captionH + 2;
        var area = new Rectangle(0, below, Width, Math.Max(1, Height - below));

        if (Combo != 0)
        {
            var (family, presses) =
                Input.PadShortcut.Decode(new Input.PadReading(Combo, Vendor, Product));

            // Redrawn in the symbols of the pad in use, not the one it was recorded on — the same rule
            // the hotkey boxes follow. A combination is stored as positions, so one set on a DualSense
            // genuinely fires from the equivalent buttons on an Xbox pad, and showing it as Square while
            // an Xbox controller is the one being held would be describing a pad that is not there.
            family = Input.PadShortcut.FamilyInUse(family);

            if (presses.Count > 0)
            {
                // Design size: this window is scaled for DPI by Windows itself. The session prompt sets
                // its own scale before it draws, and that setting is shared, so it has to be put back.
                PadGlyphs.UseScale(1f);

                int width = PadGlyphs.Measure(g, family, presses);

                // Measured and given exactly its own width, because Draw centres inside what it is
                // handed and everything else on this tile is left-aligned.
                PadGlyphs.Draw(g, new Rectangle(area.Left, area.Top, width, area.Height),
                               family, presses, ink);

                _drawnFamily = family;
                _everDrawnGlyphs = true;
                return;
            }
        }

        TextRenderer.DrawText(g, Value, Theme.Body, area, ink, Flat);
    }
}

/// <summary>
/// The banner at the top of the home page: the two console buttons, drawn, and what they do.
///
/// Drawn rather than described because this is the single most useful thing a new user can be told, and
/// the one thing no amount of text gets across — "the PS or Xbox button" is a phrase people read past,
/// while the shapes themselves are recognised without reading. Both are shown, not just the one that is
/// plugged in: the point is "the big button in the middle, whichever pad you have".
/// </summary>
internal sealed class GuideBanner : Control
{
    public string Line1
    {
        get => _line1;
        set { value = Plain(value); if (_line1 == value) return; _line1 = value; Relayout(); Invalidate(); }
    }

    public string Line2
    {
        get => _line2;
        set { value = Plain(value); if (_line2 == value) return; _line2 = value; Relayout(); Invalidate(); }
    }

    private string _line1 = "", _line2 = "";

    /// <summary>
    /// Drop the ** emphasis markers, which this control cannot render.
    ///
    /// [BUG] Everything else that shows prose here goes through RichNote, which turns **like this**
    /// into bold blue. This banner paints its own text with TextRenderer, which knows nothing about
    /// the convention, so it drew the asterisks — "the **PS button** on a PlayStation one" — on the
    /// home page, in the one paragraph a first-time user is most likely to read.
    ///
    /// Stripping them here rather than only fixing the wording, because the wording lives in Words.cs
    /// with a hundred other strings that all use ** freely. Whoever edits that file next should not
    /// have to know that these two entries are special; if they use the marker it simply has no
    /// effect, which is a great deal better than it appearing on screen.
    /// </summary>
    private static string Plain(string text) => text.Replace("**", string.Empty);

    public GuideBanner()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;
        Height = MinHeight;
    }

    /// <summary>Never shorter than the button art it has to hold.</summary>
    private const int MinHeight = 92;

    /// <summary>Air above and below the two lines when they need more room than the art does.</summary>
    private const int VerticalPad = 16;

    private const int BeforeText = 18;

    /// <summary>
    /// Room on the right for the dismiss cross, so the text never runs underneath it.
    ///
    /// Wide enough for the glyph and its hit area rather than only the mark itself: a target the size
    /// of what is drawn is a target that has to be aimed at, and this one is pressed once ever.
    /// </summary>
    private const int RightPad = 44;

    private const int CloseSize = 20, CloseInset = 12;

    private Rectangle CloseRect => new(Width - CloseInset - CloseSize, CloseInset, CloseSize, CloseSize);

    private bool _overClose;

    /// <summary>
    /// Raised when the reader presses the cross. The caller decides what "dismissed" means and
    /// remembers it; this control only reports the press.
    /// </summary>
    public event Action? Dismissed;

    /// <summary>
    /// Where the sentence starts: past the mark and the air after it.
    ///
    /// Worked out rather than guessed, and worked out in one place, because both the measuring pass
    /// and the painting pass have to agree about it — a banner measured for one text width and drawn
    /// at another either clips its last line or leaves a gap under it.
    /// </summary>
    private static int TextStart() => 20 + MarkWidth + BeforeText;

    /// <summary>Room for the pad diagram. See <see cref="GuideMark"/>.</summary>
    private const int MarkWidth = 78;

    private const TextFormatFlags Wrapped = TextFormatFlags.Left | TextFormatFlags.Top
                                          | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;

    private static int Measure(string text, Font font, int width)
        => text.Length == 0 ? 0 : TextRenderer.MeasureText(text, font, new Size(width, 0), Wrapped).Height;

    /// <summary>
    /// Grow to fit the words instead of cutting them off.
    ///
    /// The body used to be one line with an ellipsis, which on this banner meant the sentence
    /// explaining the single most important control in the app stopped halfway through. A callout
    /// nobody can finish reading is worse than no callout.
    /// </summary>
    private void Relayout()
    {
        if (Width <= 0) return;

        int available = Width - TextStart() - RightPad;
        if (available < 80) return;

        int wanted = Measure(Line1, Theme.BodySemi, available)
                   + 4
                   + Measure(Line2, Theme.Small, available)
                   + VerticalPad * 2;

        int height = Math.Max(MinHeight, wanted);
        if (Height != height) Height = height;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Relayout();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        bool over = CloseRect.Contains(e.Location);
        if (over == _overClose) return;

        _overClose = over;
        Cursor = over ? Cursors.Hand : Cursors.Default;

        // Only the corner repaints. Invalidating the whole banner redraws the mark and both blocks of
        // wrapped text every time the pointer crosses it, which is the flicker this file argues
        // against everywhere else.
        Invalidate(Rectangle.Inflate(CloseRect, 2, 2));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if (!_overClose) return;

        _overClose = false;
        Cursor = Cursors.Default;
        Invalidate(Rectangle.Inflate(CloseRect, 2, 2));
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);

        if (e.Button == MouseButtons.Left && CloseRect.Contains(e.Location)) Dismissed?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Theme.PaintBackdrop(g, this);

        // Before anything is measured against it: the panel is drawn at the full height, so growing
        // afterwards would leave one frame with the border in the old place.
        Relayout();

        // A tinted panel, so it reads as a callout rather than as another row of settings.
        var box = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Theme.FillRounded(g, box, 10, Theme.Mix(Theme.Accent, Theme.Surface, 0.86f));
        Theme.DrawRounded(g, box, 10, Theme.Mix(Theme.Accent, Theme.Surface, 0.55f));

        int cy = Height / 2;
        int x = 20;

        // One mark, not two logos joined by "or".
        //
        // The logos were trademarks the app had to draw lookalikes of, and they named two brands while
        // the app supports anything Windows recognises. See GuideMark for what replaced them and why
        // the first attempt at replacing them — a whole gamepad — was worse than either.
        GuideMark.Draw(g, new RectangleF(x, cy - MarkWidth / 2f, MarkWidth, MarkWidth), Theme.Accent);

        x += MarkWidth + BeforeText;

        // Both lines wrap, and the pair is centred as a block.
        //
        // Centring the block rather than each line means a one-line body sits beside the buttons as
        // before, while a three-line one grows the banner symmetrically instead of drifting downwards
        // out of the panel.
        int available = Width - x - RightPad;

        int h1 = Measure(Line1, Theme.BodySemi, available);
        int h2 = Measure(Line2, Theme.Small, available);

        int top = (Height - (h1 + 4 + h2)) / 2;

        TextRenderer.DrawText(g, Line1, Theme.BodySemi,
                              new Rectangle(x, top, available, h1), Theme.Text, Wrapped);

        if (Line2.Length > 0)
            TextRenderer.DrawText(g, Line2, Theme.Small,
                                  new Rectangle(x, top + h1 + 4, available, h2), Theme.TextDim, Wrapped);

        DrawClose(g);
    }

    /// <summary>
    /// The dismiss cross, top right.
    ///
    /// Faint until the pointer is on it. This is a banner somebody reads once and then wants gone, so
    /// the way out has to exist — but a bright cross beside the one sentence explaining the app's main
    /// control invites closing it before it has been read.
    /// </summary>
    private void DrawClose(Graphics g)
    {
        var box = CloseRect;

        if (_overClose)
            Theme.FillRounded(g, box, 5, Theme.Mix(Theme.Accent, Theme.Surface, 0.6f));

        // Inset from the hit area, so the mark stays small while the target stays easy.
        float pad = box.Width * 0.32f;

        using var pen = new Pen(_overClose ? Theme.Text : Theme.TextFaint, 1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        g.DrawLine(pen, box.Left + pad, box.Top + pad, box.Right - pad, box.Bottom - pad);
        g.DrawLine(pen, box.Right - pad, box.Top + pad, box.Left + pad, box.Bottom - pad);
    }
}

/// <summary>
/// The window's title: the app's name, then which page you are on.
///
/// One control rather than two labels side by side, because the gap between them is the whole point and
/// two auto-sizing labels cannot agree on one. An auto-size Label reserves a few pixels of padding
/// either side of its text that nothing exposes and nothing can switch off, so "app name, small gap,
/// dash" came out as a dash adrift in the middle of the strip however the offset was tuned. Measured
/// here with NoPadding and placed by hand, the spacing is exactly the number written below.
/// </summary>
internal sealed class TitleText : Control
{
    /// <summary>The air either side of the dash.</summary>
    private const int DashGap = 7;

    public TitleText()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;
        Font = Theme.BodySemi;
    }

    public string App
    {
        get => _app;
        set { if (_app == value) return; _app = value; Relayout(); Invalidate(); }
    }

    /// <summary>The current page's name, or empty for just the app name.</summary>
    public string Page
    {
        get => _page;
        set { if (_page == value) return; _page = value; Relayout(); Invalidate(); }
    }

    private string _app = "", _page = "";

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
                                        | TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                                        | TextFormatFlags.SingleLine;

    private int Measure(string text) =>
        text.Length == 0 ? 0 : TextRenderer.MeasureText(text, Font, Size.Empty, Flags).Width;

    /// <summary>Only as wide as it draws, so it can never reach the controller strip on its right.</summary>
    private void Relayout()
    {
        int width = Measure(_app);

        if (_page.Length > 0) width += DashGap + Measure("\u2014") + DashGap + Measure(_page);

        if (Width != width) Width = width;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.PaintBackdrop(g, this);

        Relayout();

        int x = 0;

        void Write(string text, Color ink)
        {
            int width = Measure(text);
            TextRenderer.DrawText(g, text, Font, new Rectangle(x, 0, width, Height), ink, Flags);
            x += width;
        }

        Write(_app, Theme.TextDim);

        if (_page.Length == 0) return;

        x += DashGap;
        Write("\u2014", Theme.TextFaint);
        x += DashGap;
        Write(_page, Theme.TextFaint);
    }
}

/// <summary>How an AlertRow reads: a problem, something merely worth knowing, or good news.</summary>
internal enum AlertKind { Problem, Notice, Good }

/// <summary>
/// One line of the home page's alerts: a coloured dot, what is happening, and what to do about it.
///
/// Separate from the fact tiles on purpose. A tile reports a setting — something chosen, and therefore
/// not news. These report *reality*: a display that is not there right now, a scheduled task that has
/// gone missing, a game currently running. The whole card is absent when there is nothing to say, so its
/// mere presence is the signal.
/// </summary>
internal sealed class AlertRow : Control
{
    public AlertKind Kind { get; set; } = AlertKind.Notice;

    /// <summary>The quiet half, under the message: what to do, or a detail.</summary>
    public string Detail
    {
        get => _detail;
        set { if (_detail == value) return; _detail = value; Relayout(); Invalidate(); }
    }

    private string _detail = "";

    private bool _hover;

    /// <summary>Set when clicking goes somewhere, which also makes it look clickable.</summary>
    public bool Actionable { get; set; }

    /// <summary>Show a dismiss cross at the right-hand end.</summary>
    public bool Dismissible { get; set; }

    /// <summary>Raised instead of Click when the cross itself is clicked.</summary>
    public event EventHandler? Dismissed;

    private bool _overCross;

    private const int CrossSize = 22;

    /// <summary>Where the cross sits: hard right, level with the first line.</summary>
    private Rectangle CrossBounds =>
        new(Width - RightPad - CrossSize, VerticalPad - 3, CrossSize, CrossSize);

    public AlertRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;
        Font = Theme.Body;
        Height = MinHeight;
    }

    private const int MinHeight = 30;
    private const int TextLeft = 30, RightPad = 12, VerticalPad = 6, Gap = 2;

    private const TextFormatFlags Wrapped = TextFormatFlags.Left | TextFormatFlags.Top
                                          | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;

    private static int Measure(string text, Font font, int width)
        => text.Length == 0 ? 0 : TextRenderer.MeasureText(text, font, new Size(width, 0), Wrapped).Height;

    /// <summary>
    /// Take as many lines as the words need.
    ///
    /// The detail used to run off the end with an ellipsis, which is the wrong trade here: these rows
    /// exist to explain something that has gone wrong, and half an explanation about a display that is
    /// missing or audio that did not switch is no explanation at all. The message keeps its own line and
    /// the detail wraps underneath it, so the row reads top to bottom however long either gets.
    /// </summary>
    private int Available => Width - TextLeft - RightPad - (Dismissible ? CrossSize + 8 : 0);

    private void Relayout()
    {
        if (Width <= 0) return;

        int available = Available;
        if (available < 80) return;

        int wanted = Measure(Text, Font, available) + VerticalPad * 2;

        if (Detail.Length > 0) wanted += Gap + Measure(Detail, Theme.Small, available);

        int height = Math.Max(MinHeight, wanted);
        if (Height != height) Height = height;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Relayout();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Relayout();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        if (Actionable) { _hover = true; Cursor = Cursors.Hand; Invalidate(); }
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _overCross = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Dismissible)
        {
            bool over = CrossBounds.Contains(e.Location);

            if (over != _overCross)
            {
                _overCross = over;
                Cursor = over || Actionable ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        base.OnMouseMove(e);
    }

    /// <summary>
    /// The cross takes the click when the pointer is on it, and nothing else happens.
    ///
    /// Handled here rather than as a child button because a button inside this row would need its own
    /// background against a control that paints itself, and would sit in the tab order for a control
    /// nobody tabs to. Overriding OnClick is what actually suppresses the row's own Click — WinForms
    /// raises that from here, so not calling through is the whole mechanism.
    /// </summary>
    protected override void OnClick(EventArgs e)
    {
        if (Dismissible && CrossBounds.Contains(PointToClient(MousePosition)))
        {
            Dismissed?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Theme.PaintBackdrop(g, this);

        if (_hover) Theme.FillRounded(g, new RectangleF(0, 0, Width, Height), 8, Theme.SurfaceHi);

        var colour = Kind switch
        {
            AlertKind.Problem => Theme.Bad,
            AlertKind.Good => Theme.Good,
            _ => Theme.Info,
        };

        // A filled dot rather than an icon per kind: colour alone carries "wrong / worth knowing / fine"
        // without asking anyone to learn a symbol set.
        // Level with the first line rather than the middle of the row: on a wrapped row the middle is
        // somewhere inside the sentence, and a dot floating beside the second line reads as a bullet.
        const int D = 9;
        float dotTop = VerticalPad + (Font.Height - D) / 2f;

        using (var dot = new SolidBrush(colour))
            g.FillEllipse(dot, 10, dotTop, D, D);

        int available = Available;

        int messageHeight = Measure(Text, Font, available);

        TextRenderer.DrawText(g, Text, Font,
                              new Rectangle(TextLeft, VerticalPad, available, messageHeight),
                              Theme.Text, Wrapped);

        if (Dismissible) DrawCross(g);

        if (Detail.Length == 0) return;

        int detailTop = VerticalPad + messageHeight + Gap;

        TextRenderer.DrawText(g, Detail, Theme.Small,
                              new Rectangle(TextLeft, detailTop, available,
                                            Measure(Detail, Theme.Small, available)),
                              Theme.TextFaint, Wrapped);
    }

    /// <summary>The dismiss cross: two strokes, lit when the pointer is on it.</summary>
    private void DrawCross(Graphics g)
    {
        var box = CrossBounds;

        if (_overCross) Theme.FillRounded(g, box, 6, Theme.SurfaceHi);

        var ink = _overCross ? Theme.Text : Theme.TextFaint;

        const int Inset = 7;

        using var pen = new Pen(ink, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        g.DrawLine(pen, box.Left + Inset, box.Top + Inset, box.Right - Inset, box.Bottom - Inset);
        g.DrawLine(pen, box.Right - Inset, box.Top + Inset, box.Left + Inset, box.Bottom - Inset);
    }
}

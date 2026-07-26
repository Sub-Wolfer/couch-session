using CouchMode.Games;

namespace CouchMode.Ui;

/// <summary>
/// The game picker: checkbox, icon, name, one row each.
///
/// Built on ListBox rather than CheckedListBox because CheckedListBox silently ignores
/// DrawMode — it always draws itself, so a DrawItem handler never fires and icons never
/// appear. ListBox honours OwnerDrawFixed, so the checkbox is drawn here too, which is what
/// makes a dark theme and an icon column possible at all.
///
/// Check state lives in the owner's set, not in the control, so it survives the control being
/// rebuilt or never being realised.
/// </summary>
internal sealed class GameListView : ListBox
{
    private readonly GameIcons _icons;
    private readonly HashSet<string> _checked;

    /// <summary>
    /// Explains the manual-tag mark on hover, so the mark needs no printed key beneath the list.
    ///
    /// A star with a legend made the reader look away from the row to a caption to learn what the
    /// row meant. A word that appears on the mark itself, only when pointed at, teaches the same
    /// thing where the attention already is and takes no permanent space from the list.
    /// </summary>
    private readonly ToolTip _tagTip = new() { InitialDelay = 250, ReshowDelay = 100 };
    private int _tagTipRow = -1;

    /// <summary>Raised when a row is ticked or unticked.</summary>
    public event Action? CheckChanged;

    /// <summary>Raised on a right-click over a row, for the tag menu.</summary>
    public event Action<GameEntry, Point>? ItemRightClicked;

    /// <summary>
    /// Raised when the HDR badge itself is clicked, for cycling the tag.
    ///
    /// The badge is the one thing on this row that is regularly wrong, and until now the only way
    /// to correct it was a right-click nothing advertised. A pill that responds to being clicked
    /// says what it is for by being clicked, which is the whole point.
    /// </summary>
    public event Action<GameEntry>? BadgeClicked;

    /// <summary>Raised whenever the visible range changes, so an external bar can repaint.</summary>
    public event Action? Scrolled;

    /// <summary>Total height of all rows, for a scrollbar to measure against.</summary>
    public int ContentHeight => Items.Count * ItemHeight;

    /// <summary>Scroll position in pixels. ListBox thinks in items, everything else in pixels.</summary>
    public int PixelOffset
    {
        get => TopIndex * ItemHeight;
        set
        {
            int max = Math.Max(0, Items.Count - ClientSize.Height / ItemHeight);
            int wanted = Math.Clamp(value / ItemHeight, 0, max);
            if (wanted == TopIndex) return;
            TopIndex = wanted;
            Scrolled?.Invoke();
        }
    }

    /// <summary>Shows the last-played date beside the name. Set when sorting by it.</summary>
    /// <summary>Which games the user has tagged by hand, so those rows can be marked.</summary>
    public Func<GameEntry, bool>? IsManuallyTagged { get; set; }



    /// <summary>
    /// How to decide a game's HDR support.
    ///
    /// Supplied by the owner rather than read straight from the database, so a tag the user has
    /// set by hand takes precedence over anything looked up.
    /// </summary>
    public Func<GameEntry, HdrSupport> SupportOf { get; init; } = HdrDatabase.Lookup;

    public GameListView(GameIcons icons, HashSet<string> checkedKeys)
    {
        _icons = icons;
        _checked = checkedKeys;

        // ListBox paints its background and then each item on top, in separate messages, so
        // every scroll and every icon arriving showed a flash of bare background first. These
        // two together — draw into a buffer, and never erase — remove it.
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 30;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        IntegralHeight = false;

        _icons.IconLoaded += OnIconLoaded;
        _repaint.Tick += (_, _) => { _repaint.Stop(); Invalidate(); };
    }

    /// <summary>
    /// The OS scrollbar cannot be themed, so it is never shown.
    ///
    /// The control is deliberately made wider than the panel that clips it, which puts the
    /// native bar outside the visible area entirely. Hiding it with ShowScrollBar does not
    /// stick — ListBox puts it straight back on the next item change — and intercepting the
    /// non-client paint fights the control on every repaint. Clipping is simply true.
    /// </summary>
    public const int NativeBarWidth = 20;

    /// <summary>
    /// The rightmost pixel a row can actually use.
    ///
    /// The control is wider than the panel that clips it — that is how the OS scrollbar is kept
    /// out of sight — so ClientSize is misleading and anything right-aligned against it lands
    /// outside the visible area. This leaves room for the clipped-off strip and our own bar.
    /// </summary>
    private int VisibleRight => ClientSize.Width - NativeBarWidth - 14;

    /// <summary>Right edge of a row's background, stopping just before the scrollbar lane.</summary>
    private int RowRight => ClientSize.Width - NativeBarWidth - 12;

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        Scrolled?.Invoke();
        base.OnSelectedIndexChanged(e);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_ERASEBKGND = 0x0014, WM_VSCROLL = 0x0115, WM_MOUSEWHEEL = 0x020A;

        // Erase only what no row will paint over.
        //
        // Filling the whole client area here meant every repaint went background-then-row, and
        // that two-stage paint is visible as a flash across the text — which is what "the text
        // flickers when highlighting" was. Rows paint themselves completely, so erasing under
        // them achieves nothing except the flash.
        //
        // What still needs erasing is everything a row will not reach: the space below the last
        // one, and the strip beside the scrollbar.
        if (m.Msg == WM_ERASEBKGND)
        {
            using (var g = Graphics.FromHdc(m.WParam))
            using (var back = new SolidBrush(Theme.Surface))
            {
                int rows = Math.Max(0, Items.Count - TopIndex);
                int covered = Math.Min(ClientSize.Height, rows * ItemHeight);

                if (covered < ClientSize.Height)
                    g.FillRectangle(back, 0, covered, ClientSize.Width, ClientSize.Height - covered);

                if (RowRight < ClientSize.Width)
                    g.FillRectangle(back, RowRight, 0, ClientSize.Width - RowRight, covered);
            }

            m.Result = 1;
            return;
        }

        base.WndProc(ref m);

        if (m.Msg is WM_VSCROLL or WM_MOUSEWHEEL) Scrolled?.Invoke();
    }

    /// <summary>
    /// Repaint once for a burst of icons, not once per icon.
    ///
    /// Icons resolve on background threads and a large library finishes dozens within a second;
    /// repainting the whole list for each one was most of the flicker while the page loaded.
    /// </summary>
    private readonly System.Windows.Forms.Timer _repaint = new() { Interval = 150 };

    private void OnIconLoaded()
    {
        if (IsDisposed || !IsHandleCreated) return;

        BeginInvoke(() =>
        {
            _repaint.Stop();
            _repaint.Start();
        });
    }

    /// <summary>Where the last plain click landed, for Shift to measure a range from.</summary>
    private int _anchor = -1;

    /// <summary>
    /// How far from the left edge counts as clicking the box.
    ///
    /// Wider than the box itself — it is a 16px target, which is small for a mouse and unkind
    /// on a touchpad, so the gap either side of it is treated as part of it.
    /// </summary>
    private const int CheckboxHitWidth = 34;

    /// <summary>Row under the pointer, or -1. Drawn a shade below selection.</summary>
    private int _hovered = -1;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int index = IndexFromPoint(e.Location);
        if (index >= Items.Count) index = -1;

        // Ahead of the row-change guard below, deliberately.
        //
        // Moving sideways onto the badge does not change which row is hovered, so gating this on a
        // row change meant the pointer shape and the tooltip only ever appeared if you happened to
        // arrive at the badge across a row boundary — which is not how anyone reaches it.
        UpdateTagTip(index, e.Location);

        if (index == _hovered) return;

        // Repaint the two rows involved, never the whole list.
        //
        // Invalidating everything on each hover change meant every row redrew — including its
        // icon — as the pointer crossed a boundary, and at 100+ rows that reads as a flicker
        // across the whole page. Only the row being left and the row being entered can look
        // any different, so only those two are redrawn.
        int previous = _hovered;
        _hovered = index;

        InvalidateRow(previous);
        InvalidateRow(index);
    }

    /// <summary>
    /// Whether an x lands on the HDR badge — the pill, and the star that sits against it.
    ///
    /// One definition, used by the hit test, the pointer shape and the tooltip, so the area that
    /// looks clickable, behaves clickable and explains itself can never drift apart.
    /// </summary>
    private bool OverBadge(int x) => x >= VisibleRight - BadgeColumn() - 4 && x <= VisibleRight + 4;

    /// <summary>
    /// Put the badge tooltip up, and take it down again.
    ///
    /// Shown only while the pointer is actually over the badge, not anywhere on the row: the thing
    /// being explained lives there, and a tip that followed the whole row would float over the name
    /// where it means nothing.
    ///
    /// Every row gets one now, not only the hand-tagged ones. A pill that can be clicked has to say
    /// so somewhere, and the row it is most worth saying on is the one nobody has corrected yet.
    /// </summary>
    private void UpdateTagTip(int index, Point at)
    {
        bool onRow = index >= 0 && index < Items.Count && Items[index] is GameEntry;

        if (!onRow || !OverBadge(at.X))
        {
            if (_tagTipRow != -1) { _tagTip.Hide(this); _tagTipRow = -1; }
            if (Cursor != Cursors.Default) Cursor = Cursors.Default;
            return;
        }

        // The pointer shape is the other half of the affordance, and the half that shows up before
        // the tooltip's delay has elapsed. Assigned only on a change: this now runs on every mouse
        // move, and setting Cursor unconditionally would repaint the pointer for all of them.
        if (Cursor != Cursors.Hand) Cursor = Cursors.Hand;

        if (_tagTipRow == index) return;

        _tagTipRow = index;

        bool tagged = Items[index] is GameEntry g && IsManuallyTagged?.Invoke(g) == true;

        _tagTip.Show(tagged ? Words.ManualTagHint : Words.BadgeHint, this, at.X + 14, at.Y + 18, 4000);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if (_tagTipRow != -1) { _tagTip.Hide(this); _tagTipRow = -1; }

        Cursor = Cursors.Default;

        if (_hovered < 0) return;

        int previous = _hovered;
        _hovered = -1;

        InvalidateRow(previous);
    }

    /// <summary>Repaint one row, if it exists and is on screen.</summary>
    private void InvalidateRow(int index)
    {
        if (index < 0 || index >= Items.Count) return;

        var bounds = GetItemRectangle(index);

        // Widened to the full control: GetItemRectangle stops at the item's own width, and the
        // row background is drawn out to RowRight.
        Invalidate(new Rectangle(0, bounds.Top, ClientSize.Width, bounds.Height));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int index = IndexFromPoint(e.Location);
        var game = index >= 0 && index < Items.Count ? Items[index] as GameEntry : null;

        if (game is not null && e.Button == MouseButtons.Right)
        {
            ItemRightClicked?.Invoke(game, e.Location);
            return;   // a right-click must not also tick the row
        }

        if (game is null || e.Button != MouseButtons.Left)
        {
            base.OnMouseDown(e);
            return;
        }

        // The badge is its own control. Handled before anything else and returned from outright, so
        // correcting a tag never also selects the row or moves the range anchor.
        if (OverBadge(e.X))
        {
            BadgeClicked?.Invoke(game);
            return;
        }

        // Only the checkbox toggles. Making the whole row a hit target meant every attempt to
        // read a title, or to right-click for the tag menu, risked flipping the setting.
        if (e.X > CheckboxHitWidth)
        {
            base.OnMouseDown(e);
            return;
        }

        // Shift extends from the last plain click and applies that row's state to everything
        // in between, the way a file browser does. Ctrl is an individual toggle that also moves
        // the anchor, so a Ctrl-click followed by Shift-click reads the way people expect.
        if (ModifierKeys.HasFlag(Keys.Shift) && _anchor >= 0 && _anchor < Items.Count)
        {
            ApplyToRange(_anchor, index);
        }
        else
        {
            if (!_checked.Remove(game.Key)) _checked.Add(game.Key);
            _anchor = index;
        }

        Invalidate();
        CheckChanged?.Invoke();

        base.OnMouseDown(e);
    }

    /// <summary>
    /// Double-clicking anywhere on a row toggles it, as a shortcut for hitting the small box.
    ///
    /// Only outside the checkbox, though. A double-click there already produced one toggle from
    /// the first press — Windows delivers the second press as WM_LBUTTONDBLCLK rather than
    /// another button-down — so toggling again here would undo it and look like nothing
    /// happened.
    /// </summary>
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);

        // The badge is excluded as well as the checkbox: a second click there is another tag change,
        // and letting it fall through to here would tick the row as well.
        if (e.Button != MouseButtons.Left || e.X <= CheckboxHitWidth || OverBadge(e.X)) return;

        int index = IndexFromPoint(e.Location);
        if (index < 0 || index >= Items.Count || Items[index] is not GameEntry game) return;

        if (!_checked.Remove(game.Key)) _checked.Add(game.Key);
        _anchor = index;

        Invalidate();
        CheckChanged?.Invoke();
    }

    /// <summary>Give every row between two indices the same state as the anchor row.</summary>
    private void ApplyToRange(int from, int to)
    {
        if (Items[from] is not GameEntry anchor) return;

        bool wanted = _checked.Contains(anchor.Key);

        int start = Math.Min(from, to);
        int end = Math.Max(from, to);

        for (int i = start; i <= end; i++)
        {
            if (Items[i] is not GameEntry game) continue;

            if (wanted) _checked.Add(game.Key);
            else _checked.Remove(game.Key);
        }
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;

        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var game = Items[e.Index] as GameEntry;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool ticked = game is not null && _checked.Contains(game.Key);

        // Row background, clipped short of the scrollbar lane.
        //
        // e.Bounds spans the control's full width, and the control is deliberately wider than
        // the panel that clips it — so filling it ran the highlight under the scrollbar and off
        // the visible edge. Rows have to stop where the content area stops.
        // Three states, deliberately far enough apart to tell apart at a glance: selection is
        // the strongest, hover sits between it and the plain row so it reads as "you are here"
        // rather than "this is chosen".
        bool hovered = e.Index == _hovered && !selected;

        var rowFill = selected ? Theme.SurfaceHi
                    : hovered ? Theme.Mix(Theme.Surface, Theme.SurfaceHi, 0.45f)
                    : Theme.Surface;

        using (var back = new SolidBrush(rowFill))
            g.FillRectangle(back, e.Bounds.Left, e.Bounds.Top,
                            RowRight - e.Bounds.Left, e.Bounds.Height);

        var bounds = e.Bounds;

        // Separator. Brighter than the dividers between settings rows on purpose: those sit in
        // a card with generous spacing, whereas these rows are tight and evenly sized, so the
        // eye needs a firmer edge to track along a long list.
        if (e.Index < Items.Count - 1)
        {
            using var rule = new SolidBrush(Theme.RowLine);
            g.FillRectangle(rule, bounds.Left + 10, bounds.Bottom - 1, RowRight - bounds.Left - 20, 1);
        }
        int mid = bounds.Top + bounds.Height / 2;

        // checkbox
        var box = new RectangleF(bounds.Left + 10, mid - 8, 16, 16);
        if (ticked)
        {
            Theme.FillRounded(g, box, 4, Enabled ? Theme.Accent : Theme.TextFaint);

            // tick
            using var pen = new Pen(Color.White, 2f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            g.DrawLines(pen,
            [
                new PointF(box.Left + 3.5f, box.Top + 8f),
                new PointF(box.Left + 6.5f, box.Top + 11f),
                new PointF(box.Left + 12.5f, box.Top + 5f),
            ]);
        }
        else
        {
            Theme.DrawRounded(g, box, 4, Theme.Mix(Theme.Line, Theme.Text, 0.25f), 1.4f);
        }

        // icon
        int iconLeft = (int)box.Right + 10;
        var icon = game is null ? null : _icons.Get(game);

        if (icon is not null)
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, new Rectangle(iconLeft, mid - 9, 18, 18));
        }
        else
        {
            // A lettered tile, not an empty square. Plenty of games have no usable icon in any
            // of their executables, and a blank placeholder is indistinguishable from an icon
            // that failed to load — this way every row is identifiable and the column is even.
            DrawLetterTile(g, new RectangleF(iconLeft, mid - 9, 18, 18), game?.Name ?? "?");
        }

        // The badge column is reserved on every row, drawn or not.
        //
        // Sizing it per row is what knocked the dates out of line: a row with a badge pushed its
        // date left by the badge's width, a row without one did not, so the column zig-zagged.
        // Columns have to be positioned from the row's geometry, never from its content.
        var support = game is null ? HdrSupport.Unknown : SupportOf(game);
        int badgeRoom = BadgeColumn();

        // Two outcomes, never three.
        //
        // A blank used to mean "nothing known", which was a distinction without a difference:
        // for a game like DiRT 2 — released years before HDR displays existed, absent from
        // Steam's capability list and from the wiki — no answer *is* the answer. Leaving those
        // rows empty just looked like the list had failed to load.
        DrawBadge(g, bounds,
                  support == HdrSupport.Native ? NativeText : NoneText,
                  support == HdrSupport.Native ? Theme.Good : Theme.TextDim);

        // Both dates, always.
        //
        // They used to appear one at a time depending on the sort, which meant the column moved
        // and changed meaning as you changed sort order — you could not compare two games on the
        // same basis without changing what you were looking at.
        int dateRoom = DateColumn * 2;

        DrawColumn(g, bounds, InstalledLeft(badgeRoom), Ago(game?.Installed?.ToLocalTime()));
        DrawColumn(g, bounds, PlayedLeft(badgeRoom), Ago(game?.LastPlayed));

        // name
        var textRect = new Rectangle(iconLeft + 26, bounds.Top,
                                     VisibleRight - iconLeft - 34 - badgeRoom - dateRoom,
                                     bounds.Height);

        TextRenderer.DrawText(g, game?.Name ?? "", Font, textRect,
                              Enabled ? Theme.Text : Theme.TextFaint,
                              TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // A star for a hand-set tag, against the badge so the two read together.
        //
        // Worth marking because the automatic answer and the user's answer are different kinds
        // of claim: the automatic one can change under you as the lists refresh, whereas this
        // one was a decision and will not.
        if (game is not null && IsManuallyTagged?.Invoke(game) == true)
            DrawStar(g, new PointF(VisibleRight - BadgeColumn() + 1, mid), Theme.Accent);
    }

    /// <summary>One right-aligned date cell.</summary>
    private void DrawColumn(Graphics g, Rectangle bounds, int left, string text)
    {
        var rect = new Rectangle(left, bounds.Top, DateColumn - 12, bounds.Height);

        TextRenderer.DrawText(g, text, Theme.Small, rect, Theme.TextFaint,
                              TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>Left edge of the installed column, which sits directly left of the badge.</summary>
    public int InstalledLeft(int badgeRoom) => VisibleRight - badgeRoom - DateColumn;

    /// <summary>Left edge of the last-played column, left of that again.</summary>
    public int PlayedLeft(int badgeRoom) => VisibleRight - badgeRoom - DateColumn * 2;

    /// <summary>Column geometry, so the header above the list can line up with the rows.</summary>
    public (int Played, int Installed, int Badge, int Right) Columns()
    {
        int badgeRoom = BadgeColumn();

        return (PlayedLeft(badgeRoom), InstalledLeft(badgeRoom),
                VisibleRight - badgeRoom, VisibleRight);
    }

    /// <summary>A small five-pointed star. Shared with the legend, so the two always match.</summary>
    public static void DrawStar(Graphics g, PointF centre, Color colour)
    {
        const float Outer = 5.5f;
        const float Inner = 2.3f;

        var points = new PointF[10];

        for (int i = 0; i < 10; i++)
        {
            double angle = -Math.PI / 2 + i * Math.PI / 5;
            float radius = i % 2 == 0 ? Outer : Inner;

            points[i] = new PointF(centre.X + (float)(Math.Cos(angle) * radius),
                                   centre.Y + (float)(Math.Sin(angle) * radius));
        }

        using var brush = new SolidBrush(colour);
        g.FillPolygon(brush, points);
    }

    /// <summary>
    /// Width reserved for the date column, so the dates line up.
    ///
    /// Wide enough for the labelled form: "3d ago" alone was ambiguous once a second date could
    /// appear in the same place.
    /// </summary>
    public const int DateColumn = 116;

    private int _badgeWidth;

    /// <summary>
    /// Width of the badge column, measured once from the widest label it can hold.
    /// </summary>
    /// <summary>
    /// Width of the badge column, measured once from the widest label it can hold.
    ///
    /// Deliberately measured without a Graphics. The header above the list needs this number
    /// too, and reaching it through CreateGraphics meant asking a control for a device context
    /// during someone else's paint — which forces handle creation at the worst possible moment.
    /// The static overload measures against the screen and needs no window at all.
    /// </summary>
    private int BadgeColumn()
    {
        if (_badgeWidth == 0)
        {
            // The longer of the two labels, so both pills match and the column edge stays
            // straight down the list.
            int native = TextRenderer.MeasureText(NativeText, Theme.Caption).Width;
            int none = TextRenderer.MeasureText(NoneText, Theme.Caption).Width;

            _badgeWidth = Math.Max(native, none) + 14;
        }

        return _badgeWidth + 12;
    }

    /// <summary>
    /// The HDR pills.
    ///
    /// Both answers are now shown. Marking only the native games left the rest ambiguous —
    /// a blank column reads the same whether the game has no HDR or has simply not been looked
    /// up yet, and those are different things. Games with no answer stay blank, which is now
    /// meaningful. The grey is deliberately quiet: "no native HDR" is the common case and
    /// Windows Auto HDR may still help, so it is information rather than a warning.
    /// </summary>
    private const string NativeText = Words.BadgeNative;
    private const string NoneText = Words.BadgeNonNative;

    private void DrawBadge(Graphics g, Rectangle bounds, string label, Color ink)
    {
        int width = BadgeColumn() - 12;
        int height = 17;

        var pill = new RectangleF(VisibleRight - width, bounds.Top + (bounds.Height - height) / 2f,
                                  width, height);

        Theme.FillRounded(g, pill, height / 2f, Theme.Mix(ink, Theme.Surface, 0.72f));

        TextRenderer.DrawText(g, label, Theme.Caption, Rectangle.Round(pill), ink,
                              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                            | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    /// <summary>
    /// A coloured square with the game's initial, used when no icon can be extracted.
    ///
    /// The colour is derived from the name, so a given game always gets the same one and the
    /// list stays recognisable between sessions.
    /// </summary>
    private static readonly string[] Articles = ["the", "a", "an"];

    private static void DrawLetterTile(Graphics g, RectangleF box, string name)
    {
        // First letter or digit, skipping punctuation and articles so "The Witcher" reads W
        // rather than T and ".hack" reads H.
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var word = words.FirstOrDefault(w => !Articles.Contains(w, StringComparer.OrdinalIgnoreCase))
                ?? words.FirstOrDefault() ?? "?";

        var letter = word.FirstOrDefault(char.IsLetterOrDigit);
        string initial = letter == default ? "?" : char.ToUpperInvariant(letter).ToString();

        // Colour derived from the name, so a game keeps the same tile every session.
        int hash = 0;
        foreach (char c in name) hash = hash * 31 + c;

        var palette = new[] { Theme.Accent, Theme.Good, Theme.Warn, Theme.Bad, Theme.Donate };
        var tint = palette[Math.Abs(hash) % palette.Length];

        Theme.FillRounded(g, box, 5, Theme.Mix(tint, Theme.Surface, 0.42f));

        using var font = new Font("Segoe UI Semibold", 9F);
        var text = Rectangle.Round(box);
        text.Offset(0, -1);   // optical centring: glyphs sit low in their line box

        TextRenderer.DrawText(g, initial, font, text, Color.White,
                              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                            | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }


    public static string Ago(DateTime? played)
    {
        if (played is not { } when) return "";

        var since = DateTime.Now - when;

        return since.TotalDays < 1 ? "today"
             : since.TotalDays < 2 ? "yesterday"
             : since.TotalDays < 30 ? $"{(int)since.TotalDays}d ago"
             : when.ToString("MMM yyyy");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icons.IconLoaded -= OnIconLoaded;
            _repaint.Stop();
            _repaint.Dispose();
            _tagTip.Dispose();
        }
        base.Dispose(disposing);
    }
}

using System.Drawing.Drawing2D;

namespace CouchMode.Ui;

/// <summary>
/// A themed dropdown.
///
/// Replaces ComboBox outright. A ComboBox draws its own drop button through the OS theme, so
/// on a dark window it always showed a pale square with a 3D arrow at the right-hand end, and
/// its corners are square no matter what — neither can be styled away. This paints the whole
/// control and opens a popup that is also ours, so the list matches the window too.
///
/// The public surface mirrors ComboBox (Items, SelectedIndex, SelectedItem,
/// SelectedIndexChanged) so it drops into existing code unchanged.
/// </summary>
internal sealed class Dropdown : Control
{
    public List<object> Items { get; } = [];

    public int Radius { get; set; } = 8;

    /// <summary>
    /// Which entries are drawn dimmed. They stay selectable — this is a state, not a bar.
    ///
    /// A controller that is switched off is still one you own and still a valid choice; it
    /// simply is not here at the moment. Disabling it would mean the only way to pick your pad
    /// is to go and switch it on first, which is the opposite of a preference.
    /// </summary>
    public Func<object, bool>? IsMuted { get; set; }

    private int _selected = -1;
    private bool _hover;
    private bool _open;

    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex
    {
        // Clamped on read: Items is a plain list the caller may clear and refill (the device
        // list does exactly that), and a stale index would silently point at a different item.
        get => _selected >= Items.Count ? -1 : _selected;
        set
        {
            int clamped = value < 0 || value >= Items.Count ? -1 : value;
            if (clamped == _selected) return;

            _selected = clamped;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public object? SelectedItem => _selected >= 0 && _selected < Items.Count ? Items[_selected] : null;

    public Dropdown()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
               | ControlStyles.SupportsTransparentBackColor, true);

        // Same reason as the switches: a fast second click arrives as a double-click and would
        // otherwise be dropped, making the control feel unresponsive.
        SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);

        BackColor = Color.Transparent;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Cursor = Cursors.Hand;
        Size = new Size(240, 34);
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Enabled && e.Button == MouseButtons.Left) Open();
        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter or Keys.Down) { Open(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    /// <summary>The wheel over a closed dropdown must not change the value — see WheelRouter.</summary>
    private DropdownPopup? _popup;

    private void Open()
    {
        if (_open || Items.Count == 0) return;

        _open = true;
        Invalidate();

        _popup = new DropdownPopup(this);
        _popup.Dismissed += () => { _open = false; _popup = null; Invalidate(); };
        _popup.Show();
    }

    /// <summary>
    /// Rebuild the open list, if there is one.
    ///
    /// The popup is sized and laid out when it opens, so an item added afterwards would be
    /// outside it and invisible. Someone holding the list open while switching a controller on
    /// should see it appear — that is the moment they are looking.
    /// </summary>
    public void RefreshOpenList()
    {
        if (_popup is null || _popup.IsDisposed) return;

        _popup.PlaceAgainst(this);
        _popup.Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.PaintBackdrop(g, this);

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

        var fill = !Enabled ? Theme.Mix(Theme.Input, Theme.Window, 0.45f)
                 : _open || _hover ? Theme.Mix(Theme.Input, Color.White, 0.06f)
                 : Theme.Input;

        var (top, bottom) = Theme.Sheen(fill, 0.06f);
        Theme.FillRoundedGradient(g, bounds, Radius, top, bottom);

        Theme.DrawRounded(g, bounds, Radius, _open ? Theme.Accent : Theme.InputLine);

        var text = SelectedItem?.ToString() ?? "";
        var textRect = new Rectangle(12, 0, Width - 42, Height);

        var ink = !Enabled || (SelectedItem is not null && IsMuted?.Invoke(SelectedItem) == true)
                ? Theme.TextFaint
                : ForeColor;

        TextRenderer.DrawText(g, text, Font, textRect, ink,
                              TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        DrawChevron(g, new PointF(Width - 20, Height / 2f), Enabled ? Theme.TextDim : Theme.TextFaint);
    }

    private static void DrawChevron(Graphics g, PointF centre, Color colour)
    {
        using var pen = new Pen(colour, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        g.DrawLines(pen,
        [
            new PointF(centre.X - 4.5f, centre.Y - 2f),
            new PointF(centre.X, centre.Y + 2.5f),
            new PointF(centre.X + 4.5f, centre.Y - 2f),
        ]);
    }

    /// <summary>
    /// The open list.
    ///
    /// A borderless tool window rather than a ToolStripDropDown, because ToolStrip insists on
    /// rendering its own margins and borders around whatever it hosts. This is dismissed when
    /// it loses activation, which is what makes clicking elsewhere close it.
    /// </summary>
    private sealed class DropdownPopup : Form
    {
        private readonly Dropdown _owner;
        private int _hovered = -1;

        /// <summary>Index of the first drawn row. Non-zero once the list is scrolled.</summary>
        private int _top;

        /// <summary>How many rows actually fit. Decided in PlaceAgainst, never assumed.</summary>
        private int _rows;

        /// <summary>
        /// Height of one row in the open list.
        ///
        /// Raised from 30. These rows are how a display, an audio device and a controller are all
        /// chosen, and a real machine offers eight to fifteen audio endpoints — so a mis-aimed press
        /// does not miss the list, it picks the wrong device. At 30px, aimed with a stick from a
        /// sofa, that is a routine outcome rather than a rare one.
        ///
        /// PlaceAgainst already sizes the popup from the room actually available, so a taller row
        /// costs visible rows on a cramped screen rather than running off the edge.
        /// </summary>
        private const int RowHeight = 38;
        private const int Pad = 5;
        private const int MaxRows = 12;

        /// <summary>Width of the scroll indicator, and of the gap kept clear for it.</summary>
        private const int BarWidth = 4;

        private bool Scrollable => _owner.Items.Count > _rows;

        /// <summary>Raised once the popup has gone, so the control can redraw unfocused.</summary>
        public event Action? Dismissed;

        public DropdownPopup(Dropdown owner)
        {
            _owner = owner;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Surface;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            PlaceAgainst(owner);
            WindowCorners.Round(this, 8);
        }

        /// <summary>
        /// Size and place the list against its control. Re-run when the items change.
        ///
        /// Named PlaceAgainst rather than Resize: Control already has a Resize event, and a
        /// method of the same name hides it.
        /// </summary>
        public void PlaceAgainst(Dropdown owner)
        {
            var screen = Screen.FromControl(owner).WorkingArea;
            var below = owner.PointToScreen(new Point(0, owner.Height + 4));

            // Never ask for more rows than the taller side of the control can actually show.
            // On a 1080p TV with the window low down there may be room for only three or four,
            // and a list sized past the screen edge silently loses whatever falls off it.
            int roomBelow = screen.Bottom - below.Y - 8;
            int roomAbove = owner.PointToScreen(Point.Empty).Y - screen.Top - 8;
            int room = Math.Max(roomBelow, roomAbove);

            int fits = Math.Max(2, (room - Pad * 2) / RowHeight);

            _rows = Math.Min(owner.Items.Count, Math.Min(MaxRows, fits));
            Size = new Size(owner.Width, _rows * RowHeight + Pad * 2);

            // Flip above the control if there is no room below, so the list is never half
            // off-screen for someone with the window near the bottom of their display.
            Location = roomBelow >= Height
                     ? below
                     : owner.PointToScreen(new Point(0, -Height - 4));

            ScrollTo(owner.SelectedIndex);
        }

        /// <summary>Scroll so a given item is on screen. Keeps the current view when it already is.</summary>
        private void ScrollTo(int index)
        {
            if (index < 0) return;
            if (index < _top) _top = index;
            else if (index >= _top + _rows) _top = index - _rows + 1;
            Clamp();
        }

        private void Clamp() =>
            _top = Math.Max(0, Math.Min(_top, _owner.Items.Count - _rows));

        protected override bool ShowWithoutActivation => false;

        protected override void OnDeactivate(EventArgs e)
        {
            Dismiss();
            base.OnDeactivate(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int row = RowAt(e.Y);
            if (row != _hovered) { _hovered = row; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int row = RowAt(e.Y);
            if (row >= 0) _owner.SelectedIndex = row;

            Dismiss();
            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Scrollable)
            {
                _top -= Math.Sign(e.Delta) * 3;
                Clamp();
                _hovered = RowAt(PointToClient(MousePosition).Y);
                Invalidate();
            }
            base.OnMouseWheel(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int last = _owner.Items.Count - 1;

            switch (e.KeyCode)
            {
                case Keys.Escape:
                    Dismiss();
                    break;

                case Keys.Down:
                case Keys.Up:
                case Keys.PageDown:
                case Keys.PageUp:
                case Keys.Home:
                case Keys.End:
                {
                    int step = e.KeyCode switch
                    {
                        Keys.Down     => 1,
                        Keys.Up       => -1,
                        Keys.PageDown => _rows,
                        Keys.PageUp   => -_rows,
                        Keys.Home     => -_owner.Items.Count,
                        _             => _owner.Items.Count,
                    };

                    int next = Math.Max(0, Math.Min(last, Math.Max(_owner.SelectedIndex, 0) + step));
                    _owner.SelectedIndex = next;
                    ScrollTo(next);
                    Invalidate();
                    e.Handled = true;
                    break;
                }

                case Keys.Enter:
                    Dismiss();
                    e.Handled = true;
                    break;
            }

            base.OnKeyDown(e);
        }

        private void Dismiss()
        {
            Dismissed?.Invoke();
            Close();
            Dispose();
        }

        /// <summary>
        /// The item under a y position, counting from the first *drawn* row rather than the
        /// first item. Without the offset a scrolled list picks whatever was originally there.
        /// </summary>
        private int RowAt(int y)
        {
            int row = (y - Pad) / RowHeight;
            if (row < 0 || row >= _rows) return -1;

            int index = _top + row;
            return index < _owner.Items.Count ? index : -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);

            var edge = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Theme.DrawRounded(g, edge, 8, Theme.InputLine);

            // Keep the text clear of the scroll indicator, but only when one is drawn.
            int gutter = Scrollable ? BarWidth + 5 : 0;

            for (int seat = 0; seat < _rows; seat++)
            {
                int i = _top + seat;
                if (i >= _owner.Items.Count) break;

                var row = new RectangleF(Pad, Pad + seat * RowHeight,
                                         Width - Pad * 2 - gutter, RowHeight);

                if (i == _owner.SelectedIndex)
                    Theme.FillRounded(g, row, 6, Theme.AccentDim);
                else if (i == _hovered)
                    Theme.FillRounded(g, row, 6, Theme.SurfaceHi);

                var item = _owner.Items[i];
                var text = item?.ToString() ?? "";

                // Dimmed, not disabled — see Dropdown.IsMuted.
                var ink = item is not null && _owner.IsMuted?.Invoke(item) == true
                        ? Theme.TextFaint
                        : Theme.Text;

                var textRect = Rectangle.Round(row);
                textRect.X += 8;
                textRect.Width -= 14;

                TextRenderer.DrawText(g, text, _owner.Font, textRect, ink,
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            if (Scrollable) DrawScrollBar(g);
        }

        /// <summary>
        /// A slim thumb down the right edge. Its only job is to say "there is more below" —
        /// without it a truncated list looks exactly like a complete one, which is how the
        /// audio picker managed to hide half the devices on the machine.
        /// </summary>
        private void DrawScrollBar(Graphics g)
        {
            int count = _owner.Items.Count;
            float trackTop = Pad;
            float trackHeight = _rows * RowHeight;

            float thumbHeight = Math.Max(24f, trackHeight * _rows / count);
            float travel = trackHeight - thumbHeight;
            float thumbTop = trackTop + travel * _top / Math.Max(1, count - _rows);

            float x = Width - Pad - BarWidth;

            Theme.FillRounded(g, new RectangleF(x, trackTop, BarWidth, trackHeight),
                              BarWidth / 2f, Theme.Hairline);
            Theme.FillRounded(g, new RectangleF(x, thumbTop, BarWidth, thumbHeight),
                              BarWidth / 2f, Theme.InputLine);
        }
    }
}

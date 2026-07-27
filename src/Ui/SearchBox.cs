using System.Drawing.Drawing2D;

namespace CouchMode.Ui;

/// <summary>
/// A themed search field.
///
/// A TextBox cannot be given rounded corners or a themed border — like ComboBox, its frame is
/// drawn by the OS — so the frame is painted here and a borderless TextBox is placed inside it.
/// That also leaves room to draw the magnifier, which is what makes the field readable as a
/// search box without a label taking up a row.
/// </summary>
internal sealed class SearchBox : Panel
{
    /// <summary>
    /// Built at the field rather than in the constructor body.
    ///
    /// OnLayout reads it, and setting any size or style property in the constructor queues a
    /// layout pass immediately — so a field assigned later in the constructor is still null the
    /// first time OnLayout runs. That is what crashed the app on startup.
    /// </summary>
    private readonly PlaceholderBox _input = new()
    {
        BorderStyle = BorderStyle.None,
        BackColor = Theme.Input,
        ForeColor = Theme.Text,
        Font = new Font("Segoe UI", 13F),

        // Without this, Height is ignored: a single-line TextBox auto-sizes by default and
        // pins its own height to PreferredHeight, discarding whatever SetBounds asks for.
        AutoSize = false,
        Multiline = false,
    };

    private bool _focused;
    private bool _overClear;

    /// <summary>Width of the clear button's hit area at the right-hand end.</summary>
    private const int ClearWidth = 30;

    private bool HasText => _input.Text.Length > 0;

    /// <summary>Raised as the user types.</summary>
    public event Action? QueryChanged;

    public string Query => _input.Text.Trim();

    public SearchBox(string placeholder)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

        BackColor = Color.Transparent;
        Height = 40;

        _input.Placeholder = placeholder;
        _input.TextChanged += (_, _) => QueryChanged?.Invoke();
        _input.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        _input.LostFocus += (_, _) => { _focused = false; Invalidate(); };

        // Escape clears rather than closing the window, which is what a search field should do.
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;

            Clear();
            e.Handled = e.SuppressKeyPress = true;
        };

        Controls.Add(_input);
    }

    public void Clear()
    {
        if (_input.Text.Length == 0) return;

        _input.Text = "";
        _overClear = false;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool over = HasText && e.X >= Width - ClearWidth;
        if (over != _overClear) { _overClear = over; Invalidate(); }

        Cursor = over ? Cursors.Hand : Cursors.IBeam;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_overClear) { _overClear = false; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (HasText && e.X >= Width - ClearWidth) Clear();
        _input.Focus();

        base.OnMouseDown(e);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_input is null) return;

        // Height comes from the glyphs, not from PreferredHeight.
        //
        // The box clips to its client area, so it has to be tall enough for a descender before
        // anything else matters. "Agy" is measured rather than the font's line height because it
        // contains both an ascender and two descenders — exactly what "Search your games…" has.
        using var graphics = CreateGraphics();

        int glyphs = TextRenderer.MeasureText(graphics, "Agy", _input.Font, Size.Empty,
                                              TextFormatFlags.NoPadding).Height;

        // Two pixels of slack, no more.
        //
        // An edit control draws its text from the top of its client area, so any surplus height
        // all lands underneath and the text rides high in the frame. The box is therefore made
        // just tall enough to clear the descenders and then centred as a whole.
        int height = Math.Clamp(glyphs + 2, 1, Height - 2);

        _input.SetBounds(34, (Height - height) / 2, Width - 34 - ClearWidth - 4, height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.PaintBackdrop(g, this);

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

        // Flat, not graded.
        //
        // The typing area is a real child TextBox, and a child control fills its own rectangle
        // with one solid colour — there is no way to make it follow a gradient. Any sheen on
        // the frame therefore showed up as a visible rectangle where the two met, which is the
        // pale block that kept appearing at the right-hand end.
        Theme.FillRounded(g, bounds, 8, Theme.Input);

        Theme.DrawRounded(g, bounds, 8, _focused ? Theme.Accent : Theme.InputLine);

        DrawMagnifier(g, new PointF(17, Height / 2f), _focused ? Theme.Text : Theme.TextFaint);

        if (HasText) DrawClear(g, new PointF(Width - ClearWidth / 2f, Height / 2f));
    }

    /// <summary>
    /// A text box that draws its own placeholder.
    ///
    /// TextBox.PlaceholderText cannot be used here. WinForms renders it into a rectangle offset
    /// by one pixel down and one across from where the edit control itself draws text, so the
    /// prompt and the typing sat a pixel apart and the typed text looked like it had jumped up.
    /// It also hard-codes SystemColors.GrayText, which is a light theme's grey.
    ///
    /// Drawing it after the control has painted, at the control's own text origin, makes the
    /// two agree exactly.
    /// </summary>
    private sealed class PlaceholderBox : TextBox
    {
        private const int WM_PAINT = 0x000F;
        private const int EM_SETMARGINS = 0x00D3;
        private const int EC_LEFTMARGIN = 0x0001;
        private const int EC_RIGHTMARGIN = 0x0002;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet =
            System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public string Placeholder { get; set; } = "";

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);

            // Repaint the whole field, not just the changed run.
            //
            // The edit control only invalidates the text it actually altered, so on the first
            // keystroke everything the placeholder had drawn to the right of the caret was
            // never erased — leaving a ghost of "Search your games…" sitting behind whatever
            // was being typed.
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // Zero the edit control's internal margins, so its text origin really is (0, 0) and
            // the placeholder below can be drawn at the same place.
            SendMessage(Handle, EM_SETMARGINS, EC_LEFTMARGIN | EC_RIGHTMARGIN, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg != WM_PAINT || Placeholder.Length == 0 || TextLength > 0) return;

            using var g = CreateGraphics();

            TextRenderer.DrawText(g, Placeholder, Font, new Point(0, 0), Theme.TextFaint, BackColor,
                                  TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
                                | TextFormatFlags.Top | TextFormatFlags.Left);
        }
    }

    /// <summary>The clear button: a cross in a circle that fills in on hover.</summary>
    private void DrawClear(Graphics g, PointF centre)
    {
        if (_overClear)
        {
            var disc = new RectangleF(centre.X - 9, centre.Y - 9, 18, 18);
            Theme.FillRounded(g, disc, 9, Theme.Mix(Theme.Input, Color.White, 0.14f));
        }

        using var pen = new Pen(_overClear ? Theme.Text : Theme.TextFaint, 1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        g.DrawLine(pen, centre.X - 4, centre.Y - 4, centre.X + 4, centre.Y + 4);
        g.DrawLine(pen, centre.X + 4, centre.Y - 4, centre.X - 4, centre.Y + 4);
    }

    private static void DrawMagnifier(Graphics g, PointF centre, Color colour)
    {
        using var pen = new Pen(colour, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        g.DrawEllipse(pen, centre.X - 5f, centre.Y - 5.5f, 8.5f, 8.5f);
        g.DrawLine(pen, centre.X + 3.5f, centre.Y + 3f, centre.X + 6f, centre.Y + 5.5f);
    }
}

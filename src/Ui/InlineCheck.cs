using System.Drawing.Drawing2D;

namespace CouchMode.Ui;

/// <summary>
/// A small tick box with a label beside it, sized to sit on a row of buttons.
///
/// The toggle switches on the settings pages are a deliberate statement — a whole row given over
/// to one decision. This is the opposite: a quiet option that lives among other controls, where a
/// full-width switch would shout. A checkbox is the right weight for "and also do this" sitting
/// next to Browse and Scan.
///
/// Drawn rather than a WinForms CheckBox for the same reason everything else here is: the stock
/// control renders in the system theme and looks like a visitor against this window's dark chrome.
/// </summary>
internal sealed class InlineCheck : Control
{
    private bool _checked;
    private bool _hover;

    public event EventHandler? CheckedChanged;

    public InlineCheck()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;
        ForeColor = Theme.TextDim;
        Font = Theme.BodySemi;
        Cursor = Cursors.Hand;
        Height = 32;
    }

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

    /// <summary>Set without raising the change event, for loading from config.</summary>
    public void SetQuietly(bool value)
    {
        _checked = value;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Checked = !Checked; base.OnMouseUp(e); }

    /// <summary>
    /// A short tag drawn after the label in its own colour, for "(Recommended)".
    ///
    /// The full settings rows get this from AddToggle, which builds a title out of two runs of text.
    /// This control draws one string in one colour, so a badge appended to Text would come out the
    /// same grey as the label and read as part of its name. Kept as a separate run for the same
    /// reason the rows do it: the tag is a judgement about the setting, not part of what it is called.
    /// </summary>
    public string Suffix { get; set; } = "";

    public Color SuffixColour { get; set; } = Theme.Good;

    /// <summary>Width the label needs, so the caller can size the control to fit its text.</summary>
    public int PreferredWidth()
    {
        using var g = CreateGraphics();

        int text = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        int tag = Suffix.Length == 0
                      ? 0
                      : TextRenderer.MeasureText(g, Suffix, Font, Size.Empty, TextFormatFlags.NoPadding).Width;

        return BoxSize + Gap + text + tag + 2;
    }

    private const int BoxSize = 16;
    private const int Gap = 8;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.PaintBackdrop(g, this);

        int top = (Height - BoxSize) / 2;
        var box = new RectangleF(1.5f, top + 0.5f, BoxSize - 1, BoxSize - 1);

        var line = !Enabled ? Theme.Line
                 : _checked ? Theme.Accent
                 : _hover ? Theme.InputLine : Theme.Line;

        if (_checked && Enabled)
            Theme.FillRounded(g, box, 4, Theme.Accent);
        else
            Theme.FillRounded(g, box, 4, Theme.Input);

        Theme.DrawRounded(g, box, 4, line);

        if (_checked)
        {
            // A tick, drawn by hand — two strokes, the short one down-left and the long one
            // up-right, which is the shape the eye reads as a checkmark.
            using var pen = new Pen(Enabled ? Color.White : Theme.TextFaint, 2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };

            float bx = box.X, by = box.Y, s = BoxSize - 1;
            g.DrawLines(pen, new[]
            {
                new PointF(bx + s * 0.26f, by + s * 0.52f),
                new PointF(bx + s * 0.44f, by + s * 0.70f),
                new PointF(bx + s * 0.74f, by + s * 0.32f),
            });
        }

        var textRect = new Rectangle(BoxSize + Gap, 0, Width - BoxSize - Gap, Height);

        const TextFormatFlags Flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                                    | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        TextRenderer.DrawText(g, Text, Font, textRect,
                              Enabled ? ForeColor : Theme.TextFaint, Flags);

        if (Suffix.Length == 0) return;

        // Placed by measuring the label rather than by padding the string, so the two runs sit
        // together however the font renders.
        int after = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;

        TextRenderer.DrawText(g, Suffix, Font,
                              new Rectangle(textRect.X + after, 0, textRect.Width - after, Height),
                              Enabled ? SuffixColour : Theme.TextFaint, Flags);
    }
}

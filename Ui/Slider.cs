using System.Drawing.Drawing2D;

namespace CouchMode.Ui;

/// <summary>
/// A horizontal drag slider: pull the thumb left for slower, right for faster.
///
/// Drawn by hand for the same reason every other control here is — the stock WinForms TrackBar
/// renders in the system theme and looks like a visitor against this window's dark chrome. The
/// track fills with the accent up to the thumb so the current position reads at a glance, and the
/// whole bar is a hit target: click or drag anywhere and the thumb jumps to the pointer.
///
/// Values are plain integers between <see cref="Minimum"/> and <see cref="Maximum"/>. What a given
/// step *means* for cursor speed is decided by the code that reads it, not here — this control only
/// knows a number and how to let you drag it.
/// </summary>
internal sealed class Slider : Control
{
    private int _min = 1;
    private int _max = 20;
    private int _value = 10;
    private bool _dragging;
    private bool _hover;

    public event EventHandler? ValueChanged;

    public Slider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
               | ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Height = 32;
        TabStop = true;
    }

    public int Minimum { get => _min; set { _min = value; Clamp(); } }
    public int Maximum { get => _max; set { _max = value; Clamp(); } }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, _min, _max);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Set without raising the change event, for loading from config.</summary>
    public void SetQuietly(int value)
    {
        _value = Math.Clamp(value, _min, _max);
        Invalidate();
    }

    private void Clamp()
    {
        if (_max < _min) _max = _min;
        _value = Math.Clamp(_value, _min, _max);
        Invalidate();
    }

    // Geometry. The thumb is inset by its own radius at each end so it never overhangs the control.
    private const int ThumbRadius = 9;
    private const int TrackHeight = 6;

    private float TrackLeft => ThumbRadius + 1;
    private float TrackRight => Width - ThumbRadius - 1;
    private float TrackSpan => Math.Max(1f, TrackRight - TrackLeft);

    private float ThumbX
    {
        get
        {
            float t = _max > _min ? (_value - _min) / (float)(_max - _min) : 0f;
            return TrackLeft + t * TrackSpan;
        }
    }

    private void SetFromX(int x)
    {
        float t = Math.Clamp((x - TrackLeft) / TrackSpan, 0f, 1f);
        Value = _min + (int)Math.Round(t * (_max - _min));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Enabled && e.Button == MouseButtons.Left)
        {
            _dragging = true;
            Capture = true;
            Focus();
            SetFromX(e.X);
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) SetFromX(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        Capture = false;
        base.OnMouseUp(e);
    }

    protected override bool IsInputKey(Keys keyData)
        => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Left:  Value -= 1; e.Handled = true; break;
            case Keys.Right: Value += 1; e.Handled = true; break;
            case Keys.Home:  Value = _min; e.Handled = true; break;
            case Keys.End:   Value = _max; e.Handled = true; break;
        }

        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.PaintBackdrop(g, this);

        float mid = Height / 2f;
        float top = mid - TrackHeight / 2f;
        float thumb = ThumbX;

        // Unfilled track, full width.
        var rail = new RectangleF(TrackLeft, top, TrackSpan, TrackHeight);
        Theme.FillRounded(g, rail, TrackHeight / 2f, Enabled ? Theme.Input : Theme.Line);

        // Filled portion, left of the thumb.
        float fillW = Math.Max(0f, thumb - TrackLeft);
        if (fillW > 0.5f)
        {
            var fill = new RectangleF(TrackLeft, top, fillW, TrackHeight);
            Theme.FillRounded(g, fill, TrackHeight / 2f, Enabled ? Theme.Accent : Theme.InputLine);
        }

        // Thumb.
        var knob = new RectangleF(thumb - ThumbRadius, mid - ThumbRadius, ThumbRadius * 2, ThumbRadius * 2);
        var knobFill = !Enabled ? Theme.Input
                     : (_dragging || _hover) ? Theme.AccentHi : Color.White;
        var knobLine = !Enabled ? Theme.Line : Theme.Accent;

        using (var b = new SolidBrush(knobFill)) g.FillEllipse(b, knob);
        using (var p = new Pen(knobLine, Focused && Enabled ? 2.5f : 1.5f)) g.DrawEllipse(p, knob);
    }
}

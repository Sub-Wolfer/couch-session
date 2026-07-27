using System.Drawing.Drawing2D;

namespace CouchMode.Ui;

/// <summary>
/// The app's scroll indicator: a rounded bar over the content, no track, no arrows, no buttons.
///
/// Driven by callbacks rather than owning any scrolling itself, so the same control works for
/// anything that scrolls — a panel that moves its content, or a ListBox that scrolls by item.
/// Both were previously showing the OS scrollbar, which cannot be themed and looked like a
/// white slab dropped onto a dark window.
/// </summary>
internal sealed class ThinScrollBar : Control
{
    /// <summary>Total height of the content, in pixels.</summary>
    public Func<int> ContentHeight { get; init; } = () => 0;

    /// <summary>Height of the visible window onto that content, in pixels.</summary>
    public Func<int> ViewportHeight { get; init; } = () => 1;

    /// <summary>Current scroll position, in pixels from the top.</summary>
    public Func<int> Offset { get; init; } = () => 0;

    /// <summary>Move to a new scroll position, in pixels from the top.</summary>
    public Action<int> ScrollTo { get; init; } = _ => { };

    /// <summary>Colour behind the bar, when it is not simply the parent's.</summary>
    public Color? Backdrop { get; init; }

    private bool _hover;
    private bool _dragging;
    private int _grabAt;
    private int _grabOffset;

    private const int MinThumb = 40;

    public ThinScrollBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
               | ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;

        // Raised from 10.
        //
        // Ten pixels is under the size of a comfortable pointer target, and at rest the bar sat at
        // about 1.7:1 against the page behind it — below the 3:1 that non-text controls are expected
        // to reach. Between the two it was something you could see was there once you knew, rather
        // than something that told you the page continued.
        Width = 14;
    }

    public int Range => Math.Max(0, ContentHeight() - ViewportHeight());

    /// <summary>True when there is anything to scroll to.</summary>
    public bool Needed => Range > 0;

    private int ThumbHeight
    {
        get
        {
            int content = Math.Max(1, ContentHeight());
            return Math.Clamp(Height * ViewportHeight() / content, MinThumb, Height);
        }
    }

    private int ThumbTop
    {
        get
        {
            int travel = Height - ThumbHeight;
            return Range <= 0 ? 0 : Math.Clamp(travel * Offset() / Range, 0, travel);
        }
    }

    private bool Over(int y) => y >= ThumbTop && y <= ThumbTop + ThumbHeight;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            int travel = Math.Max(1, Height - ThumbHeight);
            ScrollTo(_grabOffset + (e.Y - _grabAt) * Range / travel);
            Invalidate();
        }
        else if (Over(e.Y) != _hover)
        {
            _hover = Over(e.Y);
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }

        if (Over(e.Y))
        {
            _dragging = true;
            _grabAt = e.Y;
            _grabOffset = Offset();
            Capture = true;
        }
        else
        {
            // Click the empty lane to page towards the click, the way a real scrollbar does.
            ScrollTo(Offset() + (e.Y < ThumbTop ? -ViewportHeight() : ViewportHeight()));
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        Capture = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (!_dragging && _hover) { _hover = false; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var behind = Backdrop ?? Theme.BackdropOf(this);
        g.Clear(behind);

        if (!Needed) return;

        // Tinted from whatever is behind it rather than a fixed grey, so it stays a subtle
        // lightening of the surface it sits on instead of a bright bar.
        //
        // The resting mix was 0.18, which on this window's greys came out around 1.7:1 — visible
        // once you were looking for it, which is the wrong way round for the one thing on screen
        // whose whole job is to say "there is more below". Raised to 0.28, and the two active states
        // lifted with it so the bar still reacts by a noticeable step rather than converging on the
        // resting colour.
        Color fill = _dragging ? Theme.Mix(behind, Color.White, 0.52f)
                   : _hover ? Theme.Mix(behind, Color.White, 0.40f)
                   : Theme.Mix(behind, Color.White, 0.28f);

        Theme.FillRounded(g, new RectangleF(0, ThumbTop, Width, ThumbHeight), Width / 2f, fill);
    }
}

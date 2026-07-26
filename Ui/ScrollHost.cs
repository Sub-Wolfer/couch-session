using System.Runtime.InteropServices;

namespace CouchMode.Ui;

/// <summary>
/// A scrolling container with a thin overlay scrollbar instead of the Windows one.
///
/// WinForms' AutoScroll draws the classic grey scrollbar, which cannot be themed — it is
/// rendered by the OS, not the control — and it is always reserved even when it is not needed,
/// eating layout width. This scrolls by moving the content control instead, and paints its own
/// indicator that only appears when there is something to scroll to.
/// </summary>
internal class ScrollHost : Panel
{
    private Control? _content;
    private int _offset;
    private bool _reflowing;
    private readonly ThinScrollBar _thumb;

    /// <summary>
    /// Width of the indicator lane, including the gap from the right edge.
    ///
    /// Four wider than the bar itself: Reflow lays the bar out as <c>Lane - 4</c>, so this number and
    /// <see cref="ThinScrollBar"/>'s own width have to move together or the bar is cropped.
    /// </summary>
    private const int Lane = 18;

    static ScrollHost() => Application.AddMessageFilter(new WheelRouter());

    public ScrollHost()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint, true);

        BackColor = Theme.Window;
        _thumb = new ThinScrollBar
        {
            Visible = false,
            Backdrop = Theme.Window,
            ContentHeight = () => (_content?.Height ?? 0) + Padding.Vertical,
            ViewportHeight = () => ClientSize.Height,
            Offset = () => _offset,
            ScrollTo = value => Offset = value,
        };
        Controls.Add(_thumb);
    }

    public void SetContent(Control content)
    {
        if (_content is not null) Controls.Remove(_content);

        _content = content;
        content.Margin = Padding.Empty;
        Controls.Add(content);
        content.SendToBack();
        _thumb.BringToFront();

        // The content grows as labels wrap, so the scroll range has to be recomputed then.
        content.SizeChanged += (_, _) => Reflow();
        Reflow();
    }

    /// <summary>How far the content can travel. Zero means everything already fits.</summary>
    public int Range => Math.Max(0, (_content?.Height ?? 0) + Padding.Vertical - ClientSize.Height);

    public int Offset
    {
        get => _offset;
        set
        {
            int clamped = Math.Clamp(value, 0, Range);
            if (clamped == _offset) return;
            _offset = clamped;
            Reflow();
        }
    }

    /// <summary>
    /// Scroll until a control somewhere inside this host is on screen.
    ///
    /// Exists because <c>ScrollControlIntoView</c> cannot do it here. That method is part of
    /// WinForms' AutoScroll, which this class deliberately does not use — scrolling is done by moving
    /// the content control instead — so calling it was a no-op, and the jump-to-a-setting feature it
    /// was carrying had never once scrolled anything.
    ///
    /// Placed a little below the top rather than flush against it: a row pinned to the very top edge
    /// reads as the start of the page, which loses the fact that it was singled out.
    /// </summary>
    public void ScrollIntoView(Control target)
    {
        if (_content is null || Range <= 0) return;

        // Where the target sits inside the content, walked up through however many stacks and tables
        // are between the two. Bounds cannot be used directly: they are relative to the parent.
        int top = 0;

        for (var here = target; here is not null && here != _content; here = here.Parent)
        {
            if (here.Parent is null) return;   // not inside this host after all; leave the view alone
            top += here.Top;
        }

        int margin = Math.Min(80, Math.Max(0, ClientSize.Height / 4));
        int bottom = top + target.Height;

        // Only when it is not already comfortably in view. Scrolling a row that the user can already
        // see moves the page under them for no reason.
        if (top - _offset >= margin && bottom - _offset <= ClientSize.Height) return;

        Offset = top - margin;
    }

    /// <summary>
    /// Reposition the content and the indicator.
    ///
    /// Guarded against re-entry: this is called from OnLayout, and moving or showing a child
    /// queues another layout pass. Without the guard the two call each other forever and the
    /// window never finishes constructing — the app appears to launch and then do nothing.
    /// </summary>
    private void Reflow()
    {
        if (_content is null || _reflowing) return;

        _reflowing = true;
        try
        {
            _offset = Math.Clamp(_offset, 0, Range);

            var where = new Point(Padding.Left, Padding.Top - _offset);
            if (_content.Location != where) _content.Location = where;

            bool needed = Range > 0;
            if (_thumb.Visible != needed) _thumb.Visible = needed;

            if (needed)
            {
                var bar = new Rectangle(ClientSize.Width - Lane, 2, Lane - 4, ClientSize.Height - 4);
                if (_thumb.Bounds != bar) _thumb.Bounds = bar;
                _thumb.Invalidate();
            }
        }
        finally
        {
            _reflowing = false;
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Reflow();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Offset -= e.Delta;
        base.OnMouseWheel(e);
    }

    /// <summary>
    /// Sends the wheel to whichever ScrollHost is under the pointer.
    ///
    /// Windows delivers the wheel to the focused control, so without this the page only scrolls
    /// after clicking it — and worse, a focused dropdown swallows the wheel and silently changes
    /// its value while the user thinks they are scrolling the page.
    /// </summary>
    private sealed class WheelRouter : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;

            var target = Control.FromHandle(WindowFromPoint(Cursor.Position));

            // Set once the pointer is over a closed picker. The notch must not reach it, but
            // that is not the same as throwing the notch away — see below.
            bool overPicker = false;

            for (var c = target; c is not null; c = c.Parent)
            {
                // A list scrolls itself. Reaching past it to the page is what stopped the game
                // list from responding to the wheel at all: the page had nothing to scroll, so
                // the message was swallowed and the list never saw it.
                if (c is ListBox) return false;

                // A slider being dragged owns the wheel's worth of attention.
                //
                // The page still scrolls under a slider the pointer merely rests on — deliberately,
                // since this window is mostly controls and swallowing the notch there would make it
                // feel dead. But mid-drag the page sliding out from under the thumb looks like the
                // control broke, even though the drag itself tracks correctly.
                if (c is Slider { Capture: true }) return true;

                // A dropdown must never treat the wheel as "change my value" — that silently
                // rewrites a setting while the user believes they are scrolling.
                //
                // It used to stop here and swallow the message, which fixed that but created a
                // worse one: the page would not scroll at all while the pointer happened to be
                // over a closed dropdown, and this window is mostly dropdowns. Carry on up to
                // the page instead, so the notch does what the user meant by it.
                //
                // An open list is a separate top-level window and never reaches this loop, so
                // scrolling the list itself still works.
                if (c is ComboBox or Dropdown) { overPicker = true; continue; }

                if (c is not ScrollHost host) continue;
                if (host.Range <= 0) return overPicker;

                host.Offset -= unchecked((short)((long)m.WParam >> 16));
                return true;
            }

            // Nothing to scroll. Still swallowed if it was aimed at a picker, since letting it
            // through is the value-changing behaviour this exists to prevent.
            return overPicker;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point point);
    }
}

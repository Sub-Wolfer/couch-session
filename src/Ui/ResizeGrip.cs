using System.Runtime.InteropServices;

namespace CouchMode.Ui;

/// <summary>
/// Edge resizing for a borderless window.
///
/// A borderless form still gets WM_NCHITTEST, so returning HTBOTTOMRIGHT and friends is enough
/// to resize — but only over the form's own background. Here the rail, footer and page host
/// cover every edge, and a child control's hit test never reaches the form. So the edge check
/// is done on the children instead, and the resize is handed to the window manager by faking a
/// click on the border the user was near.
/// </summary>
internal static class ResizeGrip
{
    private const int Grip = 6;

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                      HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    /// <summary>
    /// Attach to the form and every control beneath it.
    ///
    /// Naming the surfaces by hand does not work: whichever control is topmost at a given pixel
    /// is the one that receives the mouse, and that is easy to get wrong. The right edge was
    /// covered by the scrolling page rather than the page host, and the bottom-right corner by
    /// the footer's button strip rather than the footer — so both were dead. Walking the whole
    /// tree means no edge can be missed, and controls that are never near an edge simply never
    /// report a hit.
    /// </summary>
    public static void Enable(Form form)
    {
        AttachTree(form, form);
    }

    private static void AttachTree(Form form, Control root)
    {
        if (!ReferenceEquals(form, root)) Attach(form, root);

        foreach (Control child in root.Controls)
            AttachTree(form, child);

        // Pages and rows are built as the window is used, so later arrivals are covered too.
        root.ControlAdded += (_, e) => { if (e.Control is not null) AttachTree(form, e.Control); };
    }

    private static void Attach(Form form, Control surface)
    {
        var restore = surface.Cursor;

        surface.MouseMove += (_, e) =>
        {
            if (form.WindowState == FormWindowState.Maximized) return;
            int hit = HitTest(form, surface, e.Location);
            surface.Cursor = CursorFor(hit) ?? restore;
        };

        surface.MouseLeave += (_, _) => surface.Cursor = restore;

        surface.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || form.WindowState == FormWindowState.Maximized) return;

            int hit = HitTest(form, surface, e.Location);
            if (hit == 0) return;

            ReleaseCapture();
            SendMessage(form.Handle, WM_NCLBUTTONDOWN, hit, 0);
        };
    }

    /// <summary>
    /// Whether a point on a control is over a resize edge.
    ///
    /// Public so the title bar can ask before starting a window drag. Both handlers are on the
    /// same control and both fire; the drag was subscribed first, so it moved the window and
    /// only then did the resize begin — which is the "jumps, then resizes" behaviour along the
    /// top edge. The edge has to win, so the drag checks this and stands down.
    /// </summary>
    public static bool OnEdge(Form form, Control surface, Point local)
        => HitTest(form, surface, local) != 0;

    private static int HitTest(Form form, Control surface, Point local)
    {
        var p = form.PointToClient(surface.PointToScreen(local));

        bool left = p.X <= Grip;
        bool right = p.X >= form.ClientSize.Width - Grip;
        bool top = p.Y <= Grip;
        bool bottom = p.Y >= form.ClientSize.Height - Grip;

        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        return 0;
    }

    private static Cursor? CursorFor(int hit) => hit switch
    {
        HTLEFT or HTRIGHT => Cursors.SizeWE,
        HTTOP or HTBOTTOM => Cursors.SizeNS,
        HTTOPLEFT or HTBOTTOMRIGHT => Cursors.SizeNWSE,
        HTTOPRIGHT or HTBOTTOMLEFT => Cursors.SizeNESW,
        _ => null,
    };

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}

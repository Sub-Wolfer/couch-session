using System.Runtime.InteropServices;

namespace CouchMode.Ui;

/// <summary>
/// Let a borderless dialog be dragged by its own body.
///
/// [BUG] Nothing on screen could be moved while one of these was up. Every dialog in this app is
/// <see cref="FormBorderStyle.None"/>, so none of them had a title bar to drag — and Windows disables
/// the window that owns a modal dialog, which took the settings window's own drag away at the same
/// moment. A welcome or update dialog opening over something the user wanted to read left them with
/// no way to move either window until they had answered it.
///
/// The settings window has done this since it was written; this is the same two lines, put somewhere
/// the dialogs can reach. Handing the click straight to the window manager is what makes it feel like
/// a real title bar — the drag is the system's own, so it snaps, it obeys Aero Snap, and it does not
/// stutter behind a message loop.
///
/// Deliberately not every surface. Only labels, pictures and the form's own background start a drag;
/// buttons, toggles, pickers and anything inside a scrolling panel keep their clicks, because a
/// control that acts on a press must not also be a handle to move the window by.
/// </summary>
internal static class WindowDrag
{
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, int w, int l);

    /// <summary>Make <paramref name="form"/> draggable by its text, its images and its background.</summary>
    public static void Enable(Form form) => Hook(form, form);

    private static void Hook(Form form, Control control)
    {
        if (control == form || Inert(control))
            control.MouseDown += (_, e) => Begin(form, e);

        foreach (Control child in control.Controls) Hook(form, child);

        // Dialogs here build themselves in their constructors, so the walk above is usually the whole
        // of it. Watching for later arrivals costs nothing and means a dialog that grows a row after
        // the fact does not quietly become a dead spot.
        control.ControlAdded += (_, e) => { if (e.Control is { } added) Hook(form, added); };
    }

    /// <summary>
    /// Whether a control does nothing of its own with a press, and so can be used as a handle.
    ///
    /// A short allow-list rather than a list of things to exclude. Getting it the other way round
    /// means every control added to this app in future is draggable until somebody remembers to say
    /// otherwise, and the failure is silent: a button that moves the window instead of being pressed.
    /// </summary>
    private static bool Inert(Control control) =>
        control is Label or PictureBox or RichNote && !InsideScroller(control);

    /// <summary>
    /// Text inside a scrolling panel is left alone. The notes in the update dialog are the case: they
    /// are a RichNote like any other, but they sit in something the user drags to scroll, and one
    /// gesture cannot mean both.
    /// </summary>
    private static bool InsideScroller(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            if (parent is ScrollHost) return true;

        return false;
    }

    private static void Begin(Form form, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !form.IsHandleCreated) return;

        ReleaseCapture();
        SendMessage(form.Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }
}

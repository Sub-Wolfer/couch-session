using System.Runtime.InteropServices;

namespace CouchMode.Ui;

/// <summary>
/// Where the notification area actually is on this machine.
///
/// None of this can be assumed. The taskbar can sit on any of the four edges, it can be
/// hidden, it can be on a secondary display, and the notification area is at the far end of it
/// — which is the right-hand end normally but the left-hand end on a right-to-left Windows
/// install. Guessing "bottom right" is wrong for a meaningful number of people, and a
/// notification that points at nothing is worse than one in a neutral corner.
///
/// So it is asked for, in order of precision:
///
///   1. The TrayNotifyWnd child of the taskbar, which is the notification area itself and
///      gives its exact rectangle.
///   2. Failing that, the taskbar's own rectangle and edge from the shell's appbar interface,
///      from which the tray end can be worked out.
///
/// Both are read fresh every time rather than cached, because the taskbar moves: the user can
/// drag it to another edge, and switching to the TV changes which display it is on.
/// </summary>
internal static class TrayLocation
{
    /// <summary>Which screen edge the taskbar is docked to.</summary>
    public enum Edge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

    /// <summary>The notification area's rectangle and the edge its taskbar is docked to.</summary>
    public readonly record struct Spot(Rectangle Bounds, Edge Docked);

    /// <summary>The notification area, or null if the shell did not answer.</summary>
    public static Spot? Find()
    {
        var edge = TaskbarEdge();

        if (NotifyAreaBounds() is { } tray && tray.Width > 0 && tray.Height > 0)
            return new Spot(tray, edge ?? EdgeFromShape(tray));

        // No TrayNotifyWnd. Fall back to the tray end of the taskbar as a whole.
        if (TaskbarBounds() is { } bar && edge is { } docked)
            return new Spot(TrayEndOf(bar, docked), docked);

        return null;
    }

    /// <summary>
    /// The notification area window.
    ///
    /// Shell_TrayWnd is the taskbar and TrayNotifyWnd is the clock-and-icons portion of it.
    /// These class names have been stable across every Windows version this app supports,
    /// including Windows 11, where the taskbar was rewritten but kept them.
    /// </summary>
    private static Rectangle? NotifyAreaBounds()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return null;

        var notify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero) return null;

        return GetWindowRect(notify, out var rc) ? rc.ToRectangle() : null;
    }

    private static Rectangle? TaskbarBounds()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        return SHAppBarMessage(ABM_GETTASKBARPOS, ref data) != IntPtr.Zero
             ? data.rc.ToRectangle()
             : null;
    }

    private static Edge? TaskbarEdge()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == IntPtr.Zero) return null;

        return data.uEdge switch
        {
            ABE_LEFT   => Edge.Left,
            ABE_TOP    => Edge.Top,
            ABE_RIGHT  => Edge.Right,
            _          => Edge.Bottom,
        };
    }

    /// <summary>
    /// Last resort when the appbar call fails: a wide, short tray is on a horizontal taskbar,
    /// a tall narrow one is on a vertical taskbar. Which side is then decided by which half of
    /// its screen it sits in.
    /// </summary>
    private static Edge EdgeFromShape(Rectangle tray)
    {
        var screen = Screen.FromRectangle(tray).Bounds;

        return tray.Width >= tray.Height
             ? (tray.Top < screen.Top + screen.Height / 2 ? Edge.Top : Edge.Bottom)
             : (tray.Left < screen.Left + screen.Width / 2 ? Edge.Left : Edge.Right);
    }

    /// <summary>
    /// The end of the taskbar the notification area lives at, as a rough stand-in for the tray
    /// itself. Right-to-left layouts put it at the opposite end, which Windows reports through
    /// the shell's layout direction rather than anything we can measure.
    /// </summary>
    private static Rectangle TrayEndOf(Rectangle bar, Edge docked)
    {
        const int Guess = 200;   // a plausible notification-area length

        bool rtl = CultureInfoIsRightToLeft();

        if (docked is Edge.Top or Edge.Bottom)
        {
            int x = rtl ? bar.Left : bar.Right - Guess;
            return new Rectangle(x, bar.Top, Guess, bar.Height);
        }

        int y = rtl ? bar.Top : bar.Bottom - Guess;
        return new Rectangle(bar.Left, y, bar.Width, Guess);
    }

    private static bool CultureInfoIsRightToLeft() =>
        System.Globalization.CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;

    // ---- interop ----

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle ToRectangle() =>
            new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    private const uint ABM_GETTASKBARPOS = 0x00000005;
    private const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2;

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after,
                                              string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}

using System.Runtime.InteropServices;

namespace CouchMode.Ui;

/// <summary>
/// How much to enlarge the app's notification windows — the toasts and the session-end prompt.
///
/// These are the only windows meant to be read from a sofa rather than from a desk chair, and they are
/// the ones that go up on the television, so they cannot be sized like ordinary dialogs. Two things make
/// them too small to read, and both are handled here.
///
/// The first is Windows' own scaling, which this process opts out of by rendering per-monitor: at 150%
/// every other app's text grows and ours would not, so the monitor's scaling is read back and applied by
/// hand.
///
/// The second is resolution. A window sized in pixels occupies less and less of the screen as the panel
/// gets wider, so the same badge that reads comfortably on a 1080p or 1440p monitor turns into fine print
/// on an ultrawide and something close to unreadable on a 4K television across a room. Above 2K the window
/// is enlarged to keep roughly the same share of the screen.
///
/// At 2K and below with no scaling in play this returns exactly 1.0, so those screens keep the sizes they
/// have always had.
/// </summary>
internal static class UiScale
{
    /// <summary>
    /// The multiplier for notification windows on the primary monitor — during a session that is the
    /// television, which is the screen these have to be legible on.
    ///
    /// The larger of the two factors wins rather than their product. They are two descriptions of the
    /// same problem, not two separate ones: a 4K monitor set to 175% is already being enlarged by that
    /// 175%, and multiplying the resolution bump on top would produce a window half the width of the
    /// screen.
    /// </summary>
    /// <param name="screen">
    /// The monitor the window will actually appear on. Not optional in spirit: sizing against the
    /// primary display and then placing the window somewhere else is what made the prompt come back
    /// small and off-centre after a trip to the desktop. Primary stopped being the couch display when
    /// that display started surviving a session end, so the two are no longer the same monitor — the
    /// window was being measured for one screen and shown on another, and Windows then rescaled it
    /// again on arrival for the DPI difference.
    /// </param>
    public static float ForNotifications(Screen? screen = null)
    {
        float dpi = DpiScale(screen);
        float resolution = ResolutionScale(screen);
        float chosen = Math.Max(1f, Math.Max(dpi, resolution));

        // Said once per run, because the numbers behind this are not visible from looking at the result
        // — a window that came out too small could be a monitor reporting an unexpected width, scaling
        // being folded in when it should not be, or the ladder simply not reaching. This says which.
        if (!_reported)
        {
            _reported = true;
            Log.Info($"Notification scale: target panel {PhysicalWidth(screen)}px wide, "
                   + $"dpi factor {dpi:0.00}, resolution factor {resolution:0.00} → using {chosen:0.00}.");
        }

        return chosen;
    }

    private static bool _reported;

    /// <summary>
    /// The primary monitor's scaling, as a multiplier, or 1.0 when Windows is handling it for us.
    ///
    /// Only applied when this process is per-monitor aware. Otherwise Windows bitmap-scales our windows
    /// itself and doing it again here would scale them twice.
    /// </summary>
    private static float DpiScale(Screen? screen)
    {
        try
        {
            if (GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) != DPI_PER_MONITOR_AWARE)
                return 1f;

            // The named screen's own monitor, so a window bound for the television is measured against
            // the television rather than against whatever Windows currently calls primary.
            IntPtr monitor = screen is null
                ? MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY)
                : MonitorFromPoint(new POINT { X = screen.Bounds.Left + 1, Y = screen.Bounds.Top + 1 },
                                   MONITOR_DEFAULTTONEAREST);
            if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX >= 96)
                return dpiX / 96f;
        }
        catch { /* fall through to no scaling */ }

        return 1f;
    }

    /// <summary>
    /// The enlargement the panel's own width asks for, in steps rather than a formula.
    ///
    /// Steps because the sizes in between do not exist in practice — displays cluster hard around these
    /// widths — and because a continuous curve would hand out fractional pixel sizes that make text
    /// blurry for no benefit.
    ///
    /// Everything up to and including 3440 is deliberately left at 1.0. An ultrawide is wide but no
    /// taller than the 1440p monitor it is derived from, and enlarging for width alone made these
    /// windows overbearing on one without helping legibility. Only a genuine step up in vertical
    /// pixels — 4K and beyond — actually shrinks them enough to need it.
    /// </summary>
    private static float ResolutionScale(Screen? screen) => PhysicalWidth(screen) switch
    {
        >= 3840 => 2.00f,   // 4K and beyond
        _ => 1f,            // everything up to and including 3440 — unchanged, as designed
    };

    /// <summary>
    /// The primary panel's width in real pixels.
    ///
    /// Asked of the display mode rather than of Screen.Bounds, which is the whole reason this missed 4K
    /// before. Screen.Bounds is given in DPI-virtualised coordinates unless the process happens to be
    /// per-monitor aware — so a 4K panel running at 150% reports itself as 2560 wide, lands on the
    /// bottom rung of the ladder, and gets no enlargement at exactly the resolution that needs it most.
    /// The display mode is the panel's own answer and is not scaled by anything.
    /// </summary>
    private static int PhysicalWidth(Screen? screen = null)
    {
        try
        {
            var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

            // The named adapter, not null. Null means "the primary display", which is the
            // whole bug: it answered for a screen the window was not going to appear on.
            if (EnumDisplaySettings(screen?.DeviceName, EnumCurrentSettings, ref mode) && mode.dmPelsWidth > 0)
                return (int)mode.dmPelsWidth;
        }
        catch { /* fall through to the virtualised answer, which is better than nothing */ }

        return (screen ?? Screen.PrimaryScreen)?.Bounds.Width ?? 0;
    }

    private const int EnumCurrentSettings = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int mode, ref DEVMODE devMode);

    private const int DPI_PER_MONITOR_AWARE = 2;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}

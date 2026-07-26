using System.Runtime.InteropServices;

namespace CouchMode.Display;

/// <summary>
/// ChangeDisplaySettingsEx-based display switching. Older API than CCD, but far more
/// forgiving about "detach these, make that one primary" — which is exactly our case.
/// Used as a fallback when the CCD path returns an error.
/// </summary>
internal static class LegacyDisplay
{
    /// <summary>
    /// Map a CCD monitor device path (\\?\DISPLAY#...) to its GDI adapter name (\\.\DISPLAY1).
    /// Only works for monitors currently attached to the desktop.
    /// </summary>
    public static string? TryGetGdiName(string monitorDevicePath)
    {
        for (uint adapter = 0; ; adapter++)
        {
            var dd = DisplayDevice.Create();
            if (!EnumDisplayDevices(null, adapter, ref dd, 0)) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            for (uint mon = 0; ; mon++)
            {
                var md = DisplayDevice.Create();
                if (!EnumDisplayDevices(dd.DeviceName, mon, ref md, EDD_GET_DEVICE_INTERFACE_NAME))
                    break;

                // DeviceID here is the same interface path CCD reports as MonitorDevicePath.
                if (string.Equals(md.DeviceID, monitorDevicePath, StringComparison.OrdinalIgnoreCase))
                    return dd.DeviceName;
            }
        }
        return null;
    }

    /// <summary>
    /// Whether this display is at the desktop origin, which is what primary means.
    ///
    /// Asked of the driver every time rather than through Screen.AllScreens. WinForms caches
    /// that list and only refreshes it when the UI thread pumps WM_DISPLAYCHANGE — and every
    /// display change in this app happens on a background thread, moments before anyone asks.
    /// The cache therefore still describes the arrangement from before the change, which is
    /// exactly the wrong answer at exactly the wrong time.
    /// </summary>
    public static bool IsPrimary(string gdiName)
    {
        var dm = Devmode.Create();

        return EnumDisplaySettings(gdiName, ENUM_CURRENT_SETTINGS, ref dm)
            && dm.dmPositionX == 0
            && dm.dmPositionY == 0;
    }

    /// <summary>
    /// Where a display sits on the desktop, straight from the driver.
    ///
    /// Same reasoning as IsPrimary: this is asked for immediately after the topology changes,
    /// which is precisely when the cached screen list is still describing the old arrangement.
    /// Big Picture is placed against this rectangle, so a stale answer puts the couch interface
    /// on the wrong screen.
    /// </summary>
    public static Rectangle? BoundsOf(string gdiName)
    {
        var dm = Devmode.Create();
        if (!EnumDisplaySettings(gdiName, ENUM_CURRENT_SETTINGS, ref dm)) return null;
        if (dm.dmPelsWidth == 0 || dm.dmPelsHeight == 0) return null;

        return new Rectangle(dm.dmPositionX, dm.dmPositionY,
                             (int)dm.dmPelsWidth, (int)dm.dmPelsHeight);
    }

    /// <summary>How many displays are attached to the desktop right now.</summary>
    public static int AttachedCount()
    {
        int count = 0;

        for (uint adapter = 0; ; adapter++)
        {
            var dd = DisplayDevice.Create();
            if (!EnumDisplayDevices(null, adapter, ref dd, 0)) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0) count++;
        }

        return count;
    }

    /// <summary>Flat listing of every adapter and its monitors, for diagnostics.</summary>
    public static IEnumerable<string> Inventory()
    {
        for (uint adapter = 0; ; adapter++)
        {
            var dd = DisplayDevice.Create();
            if (!EnumDisplayDevices(null, adapter, ref dd, 0)) break;

            bool attached = (dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
            yield return $"[{adapter}] {dd.DeviceName,-16} {(attached ? "attached" : "detached")}  \"{dd.DeviceString}\"  flags=0x{dd.StateFlags:X}";

            var mode = Devmode.Create();
            if (EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref mode))
                yield return $"      mode: {mode.dmPelsWidth}x{mode.dmPelsHeight} @ {mode.dmDisplayFrequency}Hz at {mode.dmPositionX},{mode.dmPositionY}";

            for (uint mon = 0; ; mon++)
            {
                var md = DisplayDevice.Create();
                if (!EnumDisplayDevices(dd.DeviceName, mon, ref md, EDD_GET_DEVICE_INTERFACE_NAME)) break;
                yield return $"      monitor[{mon}]: \"{md.DeviceString}\"";
                yield return $"        id: {md.DeviceID}";
            }
        }
    }

    /// <summary>Detach every other display and make <paramref name="gdiName"/> the primary at (0,0).</summary>
    public static void ActivateOnly(string gdiName)
    {
        var toDetach = new List<string>();

        for (uint adapter = 0; ; adapter++)
        {
            var dd = DisplayDevice.Create();
            if (!EnumDisplayDevices(null, adapter, ref dd, 0)) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            if (string.Equals(dd.DeviceName, gdiName, StringComparison.OrdinalIgnoreCase)) continue;
            toDetach.Add(dd.DeviceName);
        }

        // Stage every change with CDS_NORESET, then commit once. Applying them one at a time
        // makes the desktop rearrange repeatedly and can leave no primary display mid-sequence.
        foreach (var name in toDetach)
        {
            var dm = Devmode.Create();
            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
            dm.dmPelsWidth = 0;
            dm.dmPelsHeight = 0;
            dm.dmPositionX = 0;
            dm.dmPositionY = 0;

            int rc = ChangeDisplaySettingsEx(name, ref dm, IntPtr.Zero,
                                             CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            if (rc != DISP_CHANGE_SUCCESSFUL)
                Log.Warn($"Could not detach {name} (code {rc}).");
        }

        var target = Devmode.Create();
        if (!EnumDisplaySettings(gdiName, ENUM_CURRENT_SETTINGS, ref target))
            throw new InvalidOperationException($"Could not read current mode for {gdiName}.");

        target.dmFields |= DM_POSITION;
        target.dmPositionX = 0;
        target.dmPositionY = 0;

        int prc = ChangeDisplaySettingsEx(gdiName, ref target, IntPtr.Zero,
                                          CDS_UPDATEREGISTRY | CDS_NORESET | CDS_SET_PRIMARY, IntPtr.Zero);
        if (prc != DISP_CHANGE_SUCCESSFUL)
            throw new InvalidOperationException($"Could not make {gdiName} primary (code {prc}).");

        int arc = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (arc != DISP_CHANGE_SUCCESSFUL)
            throw new InvalidOperationException($"Applying the display change failed (code {arc}).");
    }

    /// <summary>
    /// Make <paramref name="gdiName"/> the primary display without detaching anything.
    ///
    /// Windows defines the primary display as the one at (0,0), so this can't just move one
    /// monitor — every other display has to be translated by the same delta, or the desktop
    /// ends up with overlapping or gapped regions.
    /// </summary>
    public static void MakePrimaryKeepOthers(string gdiName)
    {
        var attached = new List<string>();
        for (uint adapter = 0; ; adapter++)
        {
            var dd = DisplayDevice.Create();
            if (!EnumDisplayDevices(null, adapter, ref dd, 0)) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                attached.Add(dd.DeviceName);
        }

        var target = Devmode.Create();
        if (!EnumDisplaySettings(gdiName, ENUM_CURRENT_SETTINGS, ref target))
            throw new InvalidOperationException($"Could not read current mode for {gdiName}.");

        int dx = target.dmPositionX;
        int dy = target.dmPositionY;

        if (dx == 0 && dy == 0)
        {
            Log.Info($"{gdiName} is already at the origin; nothing to move.");
            return;
        }

        foreach (var name in attached)
        {
            var dm = Devmode.Create();
            if (!EnumDisplaySettings(name, ENUM_CURRENT_SETTINGS, ref dm))
            {
                Log.Warn($"Could not read mode for {name}; leaving it where it is.");
                continue;
            }

            dm.dmPositionX -= dx;
            dm.dmPositionY -= dy;
            dm.dmFields |= DM_POSITION;

            bool isTarget = string.Equals(name, gdiName, StringComparison.OrdinalIgnoreCase);
            uint flags = CDS_UPDATEREGISTRY | CDS_NORESET | (isTarget ? CDS_SET_PRIMARY : 0);

            int rc = ChangeDisplaySettingsEx(name, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
            if (rc != DISP_CHANGE_SUCCESSFUL)
                Log.Warn($"Could not reposition {name} (code {rc}).");
        }

        int arc = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (arc != DISP_CHANGE_SUCCESSFUL)
            throw new InvalidOperationException($"Applying the primary-display change failed (code {arc}).");
    }

    /// <summary>
    /// Detach a display from the desktop.
    ///
    /// A zero width and height with DM_POSITION set is the documented way to say "this monitor
    /// is no longer part of the desktop" — there is no dedicated detach call. NORESET on the
    /// device write and then a null apply, so the change lands in one step rather than the
    /// desktop reflowing twice.
    /// </summary>
    public static void Detach(string gdiName)
    {
        var dm = Devmode.Create();
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_POSITION;
        dm.dmPelsWidth = 0;
        dm.dmPelsHeight = 0;

        int rc = ChangeDisplaySettingsEx(gdiName, ref dm, IntPtr.Zero,
                                         CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);

        if (rc != DISP_CHANGE_SUCCESSFUL)
        {
            Log.Warn($"Could not detach {gdiName} (code {rc}).");
            return;
        }

        int arc = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (arc != DISP_CHANGE_SUCCESSFUL)
            Log.Warn($"Applying the detach failed (code {arc}).");
    }

    // ---- interop ----

    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

    private const uint DM_POSITION = 0x00000020;
    private const uint DM_PELSWIDTH = 0x00080000;
    private const uint DM_PELSHEIGHT = 0x00100000;

    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const uint CDS_SET_PRIMARY = 0x00000010;
    private const uint CDS_NORESET = 0x10000000;

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;

        public static DisplayDevice Create() => new()
        {
            cb = Marshal.SizeOf<DisplayDevice>(),
            DeviceName = "", DeviceString = "", DeviceID = "", DeviceKey = "",
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Devmode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;              // POINTL dmPosition
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;

        public static Devmode Create() => new()
        {
            dmDeviceName = "",
            dmFormName = "",
            dmSize = (ushort)Marshal.SizeOf<Devmode>(),
        };
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DisplayDevice info, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref Devmode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref Devmode devMode,
                                                      IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? deviceName, IntPtr devMode,
                                                      IntPtr hwnd, uint flags, IntPtr lParam);
}

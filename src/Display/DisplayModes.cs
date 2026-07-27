using System.Runtime.InteropServices;

namespace CouchMode.Display;

/// <summary>A resolution and refresh rate a display can run at.</summary>
public sealed record DisplayMode(int Width, int Height, int Refresh)
{
    /// <summary>
    /// The best this display can do: its full resolution at its highest refresh rate.
    ///
    /// Set when the list is built rather than stored, so it never ends up in a saved config.
    /// Deliberately not part of <see cref="Key"/> for the same reason — the setting records a
    /// resolution, not an opinion about it.
    /// </summary>
    public bool Recommended { get; init; }

    /// <summary>
    /// A mode whose shape does not match the panel's, so the display has to scale it.
    ///
    /// The case this exists for is DCI 4K — 4096×2160 — which televisions accept and then
    /// squeeze onto a 3840×2160 panel. It works, and it is a legitimate thing to pick, but it
    /// cannot be sharper than native and should never be presented as the best option.
    /// </summary>
    public bool Scaled { get; init; }

    public override string ToString() =>
        $"{Width} × {Height} at {Refresh} Hz"
      + (Recommended ? CouchMode.Ui.Words.VideoModeBest : "")
      + (Scaled ? CouchMode.Ui.Words.VideoModeScaled : "");

    /// <summary>How the choice is stored, so it survives a restart without a lookup table.</summary>
    public string Key => $"{Width}x{Height}@{Refresh}";

    public static DisplayMode? FromKey(string key)
    {
        var parts = key.Split('x', '@');

        return parts.Length == 3
            && int.TryParse(parts[0], out int w)
            && int.TryParse(parts[1], out int h)
            && int.TryParse(parts[2], out int hz)
             ? new DisplayMode(w, h, hz)
             : null;
    }
}

/// <summary>
/// Reads and sets what resolution and refresh rate a display is running at.
///
/// This exists because of a specific and very common disappointment: Windows routinely brings a
/// television up at 60 Hz even when it is capable of 120, because 60 is what the mode list
/// defaults to and nothing ever asks for better. The picture works, so nobody investigates —
/// they simply get half the refresh rate their television can do.
///
/// Whatever the display was on is captured before anything changes and restored afterwards, in
/// the same way the topology already is.
/// </summary>
public static class DisplayModes
{
    /// <summary>
    /// Every mode a display can genuinely use, best first, with duplicates removed.
    ///
    /// Interlaced and low colour depth modes are dropped: they are still enumerated by Windows
    /// for compatibility and are never what anyone wants on a modern television.
    ///
    /// Everything else the display reports is kept, including lower resolutions and odd shapes.
    ///
    /// An earlier version filtered these to the panel's own aspect ratio, on the grounds that a
    /// stretched mode is nobody's real choice. That was the wrong call: dropping a resolution
    /// someone deliberately wants — to hit a higher refresh rate, to run an older game, to make
    /// text readable from a sofa — is a worse failure than showing one they will not pick. The
    /// best mode is marked instead, which solves the same problem without taking anything away.
    /// </summary>
    /// <param name="native">
    /// The panel's own resolution, from <see cref="DisplayManager.NativeMode"/>. When it is
    /// known, modes of a different shape are marked as scaled and sorted to the bottom, and the
    /// recommendation is made from the native-shaped ones. When it is not, everything is
    /// treated as equal and the largest mode wins, which is the old behaviour.
    /// </param>
    public static List<DisplayMode> Supported(string gdiName, DisplayMode? native = null)
    {
        var found = new HashSet<DisplayMode>();

        try
        {
            var mode = Devmode.Create();

            for (int i = 0; EnumDisplaySettings(gdiName, i, ref mode); i++)
            {
                if (mode.dmBitsPerPel < 32) continue;
                if ((mode.dmDisplayFlags & DM_INTERLACED) != 0) continue;
                if (mode.dmDisplayFrequency <= 1) continue;   // 0 and 1 mean "driver default"

                found.Add(new DisplayMode((int)mode.dmPelsWidth, (int)mode.dmPelsHeight,
                                          (int)mode.dmDisplayFrequency));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not list display modes for {gdiName}: {ex.Message}");
        }

        if (found.Count == 0) return [];

        // Nothing is hidden. Modes the panel has to scale are pushed below the ones it can show
        // directly, which answers the same question — "which of these is right?" — without
        // taking away a choice someone may have a reason for.
        double? shape = native is { Width: > 0, Height: > 0 }
                      ? (double)native.Width / native.Height
                      : null;

        bool IsScaled(DisplayMode m) =>
            shape is { } s && Math.Abs((double)m.Width / m.Height - s) / s > ShapeTolerance;

        var ordered = found
            .Select(m => m with { Scaled = IsScaled(m) })
            .OrderBy(m => m.Scaled)                          // native shape first
            .ThenByDescending(m => m.Width * m.Height)
            .ThenByDescending(m => m.Refresh)
            .ToList();

        // The first entry is now the largest native-shaped mode at its highest refresh rate.
        // Only recommended when the panel actually told us its shape — without that this is a
        // guess, and "best" is not a word to attach to a guess about someone's television.
        if (shape is not null && !ordered[0].Scaled)
            ordered[0] = ordered[0] with { Recommended = true };

        int scaled = ordered.Count(m => m.Scaled);

        // Logged once per distinct answer, not once per call.
        //
        // Building the settings pages asks for this six times over, and every page rebuild asks
        // again, so a plain log line here filled the file with the same sentence repeated —
        // which is how the last log came to be mostly noise. The interesting event is the
        // answer changing, not the question being asked.
        var summary = $"{gdiName}: {ordered.Count} mode(s)"
                    + (native is null ? ", native resolution unknown" : $", native {native.Width}×{native.Height}")
                    + (scaled > 0 ? $", {scaled} of them scaled" : "")
                    + $". Best: {ordered[0].Width} × {ordered[0].Height} at {ordered[0].Refresh} Hz";

        if (LastLogged.TryGetValue(gdiName, out var previous) && previous == summary) return ordered;

        LastLogged[gdiName] = summary;
        Log.Info(summary);

        return ordered;
    }

    /// <summary>
    /// How far a mode's shape may sit from the panel's before it counts as scaled.
    ///
    /// Two percent. Loose enough that 2560×1080 and 3440×1440 — both sold as 21:9, actually
    /// 2.370 and 2.389 — agree with each other, and tight enough to separate DCI 4K's 1.896
    /// from 16:9's 1.778, which is the case this exists for.
    /// </summary>
    private const double ShapeTolerance = 0.02;

    /// <summary>The last thing said about each display, so the same line is not written twice.</summary>
    private static readonly Dictionary<string, string> LastLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the display is running at now, or null if it cannot be read.</summary>
    public static DisplayMode? Current(string gdiName)
    {
        var mode = Devmode.Create();

        if (!EnumDisplaySettings(gdiName, ENUM_CURRENT_SETTINGS, ref mode)) return null;

        return new DisplayMode((int)mode.dmPelsWidth, (int)mode.dmPelsHeight,
                               (int)mode.dmDisplayFrequency);
    }

    /// <summary>
    /// Put a display into a given mode.
    ///
    /// Tested before it is committed. ChangeDisplaySettingsEx will happily accept a mode the
    /// display cannot actually show and leave the screen black, so the test call is what stands
    /// between a wrong setting and a television the user has to unplug to recover.
    /// </summary>
    public static bool Apply(string gdiName, DisplayMode wanted)
    {
        try
        {
            var mode = Devmode.Create();

            if (!EnumDisplaySettings(gdiName, ENUM_CURRENT_SETTINGS, ref mode))
            {
                Log.Warn($"Could not read the current mode for {gdiName}.");
                return false;
            }

            mode.dmPelsWidth = (uint)wanted.Width;
            mode.dmPelsHeight = (uint)wanted.Height;
            mode.dmDisplayFrequency = (uint)wanted.Refresh;
            mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

            int test = ChangeDisplaySettingsEx(gdiName, ref mode, IntPtr.Zero, CDS_TEST, IntPtr.Zero);

            if (test != DISP_CHANGE_SUCCESSFUL)
            {
                Log.Warn($"{gdiName} will not accept {wanted} (code {test}); leaving it alone.");
                return false;
            }

            int rc = ChangeDisplaySettingsEx(gdiName, ref mode, IntPtr.Zero,
                                             CDS_UPDATEREGISTRY, IntPtr.Zero);

            if (rc != DISP_CHANGE_SUCCESSFUL)
            {
                Log.Warn($"Setting {gdiName} to {wanted} failed with code {rc}.");
                return false;
            }

            Log.Info($"{gdiName} set to {wanted}.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not set the display mode for {gdiName}", ex);
            return false;
        }
    }

    // ---- interop ----

    private const int ENUM_CURRENT_SETTINGS = -1;

    private const uint DM_PELSWIDTH = 0x00080000;
    private const uint DM_PELSHEIGHT = 0x00100000;
    private const uint DM_DISPLAYFREQUENCY = 0x00400000;

    private const uint DM_INTERLACED = 0x00000002;

    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const uint CDS_TEST = 0x00000002;

    private const int DISP_CHANGE_SUCCESSFUL = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Devmode
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

        public static Devmode Create() => new()
        {
            dmDeviceName = "",
            dmFormName = "",
            dmSize = (ushort)Marshal.SizeOf<Devmode>(),
        };
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref Devmode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref Devmode devMode,
                                                      IntPtr hwnd, uint flags, IntPtr param);
}

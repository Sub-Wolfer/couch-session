using System.Runtime.InteropServices;
using static CouchMode.Display.Ccd;

namespace CouchMode.Display;

/// <summary>
/// Turns HDR on and off for a single display.
///
/// Windows split this API in two. Windows 11 24H2 introduced DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE,
/// which toggles HDR specifically; earlier builds only have SET_ADVANCED_COLOR_STATE, which
/// conflates HDR with wide colour gamut. Both are used here, newest first, because on 24H2 the
/// legacy call no longer reliably controls HDR on its own.
///
/// Everything is addressed per display path, never globally — see <see cref="SetPrimaryHdr"/>.
/// </summary>
public static class HdrControl
{
    private const uint GetAdvancedColorInfo = 9;
    private const uint SetAdvancedColorState = 10;
    private const uint SetHdrState = 15;          // Windows 11 24H2 and later

    /// <summary>24H2 is build 26100; below that, only the legacy call exists.</summary>
    private static bool HasModernHdrApi => Environment.OSVersion.Version.Build >= 26100;

    /// <summary>Displays already described in the log, so each is reported once per run.</summary>
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

    /// <summary>
    /// What is known about the primary display's HDR.
    ///
    /// <paramref name="Found"/> exists because its absence was being reported as "no HDR
    /// support": failing to resolve the primary display returned the same all-false value as a
    /// display that genuinely cannot do HDR, and the log said the latter in both cases. Those
    /// need different answers, and more importantly different messages.
    /// </summary>
    /// <param name="Raw">
    /// The reply's bitfield exactly as Windows returned it, kept for diagnosis.
    ///
    /// Bit 0 is <c>advancedColorSupported</c>, and "advanced colour" is not the same claim as HDR —
    /// it also covers wide colour gamut, which plenty of SDR panels advertise. So a display can
    /// report supported here and still have no HDR to offer, which is exactly what an SDR 1080p
    /// screen showing "HDR supported" looks like. Windows 11 24H2 added a second call that splits
    /// the two apart; until it is known this build can make it, the raw value is logged so the
    /// question is settled by evidence rather than by guessing which bit meant what.
    /// </param>
    /// <param name="Capable">
    /// What the monitor's own EDID says about HDR, or null when that could not be read.
    ///
    /// Kept apart from <paramref name="Supported"/> rather than replacing it, deliberately. Supported
    /// is Windows' answer and stays the one the control path acts on, so nothing here can stop HDR
    /// being switched on a display Windows would have allowed. This is the answer worth *telling*
    /// someone, because it is the one that is true.
    /// </param>
    public readonly record struct HdrStatus(bool Supported, bool Enabled, bool Found = true,
                                            uint Raw = 0, uint Encoding = 0, uint Bits = 0,
                                            bool? Capable = null)
    {
        public static readonly HdrStatus Unknown = new(false, false, Found: false);

        /// <summary>The best answer available, preferring the display's own claim.</summary>
        public bool ReallySupported => Capable ?? Supported;
    }

    /// <summary>
    /// An identifier for the primary display, for logs.
    ///
    /// Deliberately the raw adapter and target ids rather than a friendly name: reading the
    /// name needs another interop call, and this only has to be unique enough to line up with
    /// the diagnostics report and to tell one run from another. Never throws.
    /// </summary>
    public static string PrimaryId()
    {
        try
        {
            var path = PrimaryPath();
            if (path is null) return "(none resolved)";

            var adapter = path.Value.TargetInfo.AdapterId;
            return $"adapter {adapter.HighPart:X}:{adapter.LowPart:X}, target {path.Value.TargetInfo.Id}";
        }
        catch (Exception ex)
        {
            return $"(unreadable: {ex.Message})";
        }
    }

    /// <summary>HDR capability and current state for whichever display is primary right now.</summary>
    public static HdrStatus PrimaryStatus()
    {
        var path = PrimaryPath();
        return path is null ? HdrStatus.Unknown : StatusOf(path.Value);
    }

    /// <summary>
    /// The same, for one named display rather than whichever is primary.
    ///
    /// Exists because the display this app is really about is usually not the primary one at the
    /// moment anybody is looking. Settings are read at a desk, where the primary display is the
    /// monitor, while the display a session will actually use is the television — so answering
    /// only about the primary would answer about the wrong screen.
    ///
    /// A display that is not attached comes back as Unknown rather than unsupported. It has not
    /// said it cannot do HDR; nobody has been able to ask it.
    /// </summary>
    public static HdrStatus StatusFor(string monitorDevicePath)
    {
        if (string.IsNullOrWhiteSpace(monitorDevicePath)) return HdrStatus.Unknown;

        try
        {
            var path = DisplayManager.ActivePathFor(monitorDevicePath);
            if (path is null) return HdrStatus.Unknown;

            var status = StatusOf(path.Value) with
            {
                Capable = MonitorNames.AdvertisesHdr(monitorDevicePath),
            };

            // Once per display per run, with both answers side by side — the one Windows gives and
            // the one the panel gives. Where they disagree, the log now says so outright instead of
            // leaving it to be guessed at from a screenshot.
            var adapter = path.Value.TargetInfo.AdapterId;
            string id = $"adapter {adapter.HighPart:X}:{adapter.LowPart:X}, target {path.Value.TargetInfo.Id}";

            if (Reported.Add(id))
                Log.Info($"HDR capability for {id}: raw 0x{status.Raw:X} "
                       + $"(advanced color {status.Supported}, on {status.Enabled}), "
                       + $"encoding {status.Encoding}, {status.Bits} bits per channel; "
                       + $"EDID says HDR {status.Capable?.ToString() ?? "unknown"}. "
                       + $"Windows build {Environment.OSVersion.Version.Build}.");

            return status;
        }
        catch (Exception ex)
        {
            Log.Warn($"HDR: could not read support for a display: {ex.Message}");
            return HdrStatus.Unknown;
        }
    }

    /// <summary>
    /// Enable or disable HDR on the primary display only.
    ///
    /// Deliberately resolves the primary display at call time rather than taking a display as a
    /// parameter: during a session the TV is primary, and on the desktop the monitor is, and the
    /// caller should not have to care which. Every other display is left untouched.
    /// </summary>
    public static bool SetPrimaryHdr(bool enable)
    {
        var path = PrimaryPath();
        if (path is null)
        {
            Log.Warn("HDR: could not identify the primary display.");
            return false;
        }

        var status = StatusOf(path.Value);
        if (!status.Supported)
        {
            Log.Info("HDR: the primary display does not report HDR support; leaving it alone.");
            return false;
        }

        if (status.Enabled == enable) return true;   // already where we want it

        bool ok = Apply(path.Value, enable);
        Log.Info($"HDR {(enable ? "on" : "off")} for the primary display: {(ok ? "done" : "failed")}.");
        return ok;
    }

    /// <summary>
    /// Enable or disable HDR on one named display, whatever is primary.
    ///
    /// The counterpart to SetPrimaryHdr, for the caller that knows exactly which screen it means.
    /// Auto HDR needs this: it has to be able to switch a display back off after the primary has
    /// moved away from it, and "the primary" is by then the wrong answer.
    /// </summary>
    public static bool SetHdrFor(string monitorDevicePath, bool enable)
    {
        if (string.IsNullOrWhiteSpace(monitorDevicePath)) return false;

        try
        {
            var path = DisplayManager.ActivePathFor(monitorDevicePath);
            if (path is null)
            {
                Log.Info("HDR: that display is not attached; leaving it alone.");
                return false;
            }

            var status = StatusOf(path.Value);
            if (!status.Supported)
            {
                Log.Info("HDR: that display does not report HDR support; leaving it alone.");
                return false;
            }

            if (status.Enabled == enable) return true;

            bool ok = Apply(path.Value, enable);
            Log.Info($"HDR {(enable ? "on" : "off")} for a named display: {(ok ? "done" : "failed")}.");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn($"HDR: could not change that display: {ex.Message}");
            return false;
        }
    }

    private static bool Apply(PathInfo path, bool enable)
    {
        if (HasModernHdrApi && SetViaHdrState(path, enable)) return true;

        // Older builds, or the modern call refused — fall back to advanced colour.
        return SetViaAdvancedColor(path, enable);
    }

    private static bool SetViaHdrState(PathInfo path, bool enable)
    {
        var request = new SetHdrStateRequest
        {
            Header = Header(SetHdrState, (uint)Marshal.SizeOf<SetHdrStateRequest>(), path),
            Value = enable ? 1u : 0u,
        };

        int rc = DisplayConfigSetDeviceInfo(ref request);
        if (rc != ERROR_SUCCESS) Log.Warn($"HDR: SET_HDR_STATE returned {rc}.");
        return rc == ERROR_SUCCESS;
    }

    private static bool SetViaAdvancedColor(PathInfo path, bool enable)
    {
        var request = new SetAdvancedColorRequest
        {
            Header = Header(SetAdvancedColorState, (uint)Marshal.SizeOf<SetAdvancedColorRequest>(), path),
            Value = enable ? 1u : 0u,
        };

        int rc = DisplayConfigSetDeviceInfo(ref request);
        if (rc != ERROR_SUCCESS) Log.Warn($"HDR: SET_ADVANCED_COLOR_STATE returned {rc}.");
        return rc == ERROR_SUCCESS;
    }

    private static HdrStatus StatusOf(PathInfo path)
    {
        var request = new GetAdvancedColorRequest
        {
            Header = Header(GetAdvancedColorInfo, (uint)Marshal.SizeOf<GetAdvancedColorRequest>(), path),
        };

        if (DisplayConfigGetDeviceInfo(ref request) != ERROR_SUCCESS)
            return new HdrStatus(false, false);

        // Bit 0: advancedColorSupported. Bit 1: advancedColorEnabled.
        bool supported = (request.Value & 0x1) != 0;
        bool enabled = (request.Value & 0x2) != 0;

        return new HdrStatus(supported, enabled, Found: true,
                             Raw: request.Value,
                             Encoding: request.ColorEncoding,
                             Bits: request.BitsPerColorChannel);
    }

    /// <summary>The active CCD path whose source sits at the origin — Windows' definition of primary.</summary>
    private static PathInfo? PrimaryPath()
    {
        try
        {
            var (paths, modes) = QueryActive();

            foreach (var path in paths)
            {
                if ((path.Flags & PathActive) == 0) continue;

                uint idx = path.SourceInfo.ModeInfoIdx;
                if (idx == InvalidModeIndex || idx >= modes.Length) continue;

                var mode = modes[idx];
                if (mode.InfoType != ModeInfoType.Source) continue;

                if (mode.SourceMode.Position.X == 0 && mode.SourceMode.Position.Y == 0)
                    return path;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"HDR: could not read the display configuration: {ex.Message}");
        }
        return null;
    }

    private static (PathInfo[] paths, ModeInfo[] modes) QueryActive()
    {
        int rc = GetDisplayConfigBufferSizes(QueryFlags.OnlyActivePaths, out int pathCount, out int modeCount);
        if (rc != ERROR_SUCCESS) throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed ({rc}).");

        var paths = new PathInfo[pathCount];
        var modes = new ModeInfo[modeCount];

        rc = QueryDisplayConfig(QueryFlags.OnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (rc != ERROR_SUCCESS) throw new InvalidOperationException($"QueryDisplayConfig failed ({rc}).");

        Array.Resize(ref paths, pathCount);
        Array.Resize(ref modes, modeCount);
        return (paths, modes);
    }

    private static DeviceInfoHeader Header(uint type, uint size, PathInfo path) => new()
    {
        Type = (DeviceInfoType)type,
        Size = size,
        AdapterId = path.TargetInfo.AdapterId,
        Id = path.TargetInfo.Id,
    };

    // ---- interop ----

    [StructLayout(LayoutKind.Sequential)]
    private struct GetAdvancedColorRequest
    {
        public DeviceInfoHeader Header;
        public uint Value;             // bitfield: supported / enabled / forced / ...
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SetAdvancedColorRequest
    {
        public DeviceInfoHeader Header;
        public uint Value;             // bit 0: enableAdvancedColor
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SetHdrStateRequest
    {
        public DeviceInfoHeader Header;
        public uint Value;             // bit 0: enableHdr
    }

    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref GetAdvancedColorRequest request);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref SetAdvancedColorRequest request);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref SetHdrStateRequest request);
}

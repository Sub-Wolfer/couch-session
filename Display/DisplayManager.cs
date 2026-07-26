using System.Runtime.InteropServices;
using static CouchMode.Display.Ccd;

namespace CouchMode.Display;

public sealed record DisplayInfo(string DevicePath, string FriendlyName, uint TargetId, bool IsActive)
{
    public override string ToString() =>
        string.IsNullOrWhiteSpace(FriendlyName) ? $"Display {TargetId}" : FriendlyName;
}

/// <summary>A complete display topology, captured verbatim so it can be re-applied exactly.</summary>
public sealed class DisplaySnapshot
{
    internal PathInfo[] Paths = [];
    internal ModeInfo[] Modes = [];
    public int ActivePathCount { get; internal set; }

    /// <summary>The monitor that was at the desktop origin when this was captured.</summary>
    public string PrimaryDevicePath { get; internal set; } = "";
}

/// <summary>
/// Replaces DisplayFusion. Uses the CCD API directly to enumerate monitors, switch to a
/// TV-only topology, and restore the previous topology byte-for-byte afterwards.
///
/// Restoring a captured snapshot is strictly better than named profiles: it reproduces
/// whatever arrangement the user was actually in, not a preset they had to define first.
/// </summary>
public static class DisplayManager
{
    static DisplayManager()
    {
        // Cheap guard against struct-layout drift; wrong sizes here fail in confusing ways at runtime.
        if (Marshal.SizeOf<PathInfo>() != 72)
            throw new InvalidOperationException($"PathInfo is {Marshal.SizeOf<PathInfo>()} bytes, expected 72.");
        if (Marshal.SizeOf<ModeInfo>() != 64)
            throw new InvalidOperationException($"ModeInfo is {Marshal.SizeOf<ModeInfo>()} bytes, expected 64.");
    }

    /// <summary>Every monitor Windows knows about, connected or not.</summary>
    public static IReadOnlyList<DisplayInfo> ListDisplays()
    {
        var (paths, _) = Query(QueryFlags.AllPaths);
        var seen = new Dictionary<string, DisplayInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (TryGetTargetName(path, out var friendly, out var devicePath) && devicePath.Length > 0)
            {
                bool active = (path.Flags & PathActive) != 0;
                // An inactive duplicate must never overwrite the active record for the same panel.
                if (!seen.TryGetValue(devicePath, out var existing) || (active && !existing.IsActive))
                    seen[devicePath] = new DisplayInfo(devicePath, BestName(devicePath, friendly),
                                                      path.TargetInfo.Id, active);
            }
        }

        // Tack the current resolution onto whatever's attached — the cheapest way to tell two
        // similarly-named panels apart in a dropdown.
        var result = new List<DisplayInfo>();
        foreach (var d in seen.Values)
        {
            var bounds = d.IsActive ? BoundsOf(d.DevicePath) : null;
            result.Add(bounds is { } b
                ? d with { FriendlyName = $"{d.FriendlyName} ({b.Width}×{b.Height})" }
                : d);
        }

        return result.OrderByDescending(d => d.IsActive).ThenBy(d => d.FriendlyName).ToList();
    }

    /// <summary>
    /// The active CCD path driving a named monitor, or null when it is not attached.
    ///
    /// Null is a real answer and not a failure: a television that is switched off has no active
    /// path, and anything asked about it can only honestly reply that it does not know.
    /// </summary>
    internal static PathInfo? ActivePathFor(string monitorDevicePath)
    {
        if (string.IsNullOrWhiteSpace(monitorDevicePath)) return null;

        var (paths, _) = Query(QueryFlags.OnlyActivePaths);

        foreach (var path in paths)
        {
            if ((path.Flags & PathActive) == 0) continue;

            if (TryGetTargetName(path, out _, out var devicePath)
             && string.Equals(devicePath, monitorDevicePath, StringComparison.OrdinalIgnoreCase))
                return path;
        }

        return null;
    }

    /// <summary>
    /// The monitor sitting at the desktop origin right now — Windows' definition of primary.
    ///
    /// <see cref="Capture"/> already works this out, but only as part of snapshotting the whole
    /// topology, which is far more than a caller that just wants to name the main display needs.
    /// </summary>
    public static string? CurrentPrimaryDevicePath()
    {
        try
        {
            var (paths, modes) = Query(QueryFlags.OnlyActivePaths);
            return FindPrimary(paths, modes);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not work out which display is primary: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Prefer the EDID name. Drivers frequently report generic strings like "Display" or
    /// "Generic PnP Monitor" through CCD, which is useless for telling monitors apart.
    /// </summary>
    private static string BestName(string devicePath, string ccdName)
    {
        var edidName = MonitorNames.FromEdid(devicePath);
        if (!string.IsNullOrWhiteSpace(edidName)) return edidName;

        if (!string.IsNullOrWhiteSpace(ccdName) && !IsGeneric(ccdName)) return ccdName;

        return string.IsNullOrWhiteSpace(ccdName) ? "Unknown display" : ccdName;
    }

    private static bool IsGeneric(string name) =>
        name.Trim() is "Display" or "Generic PnP Monitor" or "Default Monitor" or "PnP Monitor";

    /// <summary>Capture the current topology so <see cref="Restore"/> can put it back.</summary>
    public static DisplaySnapshot Capture()
    {
        var (paths, modes) = Query(QueryFlags.OnlyActivePaths);
        return new DisplaySnapshot
        {
            Paths = paths,
            Modes = modes,
            ActivePathCount = paths.Count(p => (p.Flags & PathActive) != 0),
            PrimaryDevicePath = FindPrimary(paths, modes) ?? "",
        };
    }

    /// <summary>
    /// Which monitor is at the desktop origin — which is what "primary" means to Windows.
    ///
    /// Recorded separately rather than relied upon to come back with the topology, because
    /// SetDisplayConfig is asked to restore with SDC_ALLOW_CHANGES, and that flag explicitly
    /// permits Windows to deviate from the supplied configuration. Keeping the current primary
    /// is one of the deviations it makes.
    /// </summary>
    private static string? FindPrimary(PathInfo[] paths, ModeInfo[] modes)
    {
        foreach (var path in paths)
        {
            if ((path.Flags & PathActive) == 0) continue;

            uint index = path.SourceInfo.ModeInfoIdx;
            if (index == InvalidModeIndex || index >= modes.Length) continue;

            var mode = modes[index];
            if (mode.InfoType != ModeInfoType.Source) continue;
            if (mode.SourceMode.Position.X != 0 || mode.SourceMode.Position.Y != 0) continue;

            if (TryGetTargetName(path, out _, out var devicePath) && devicePath.Length > 0)
                return devicePath;
        }

        return null;
    }

    /// <summary>
    /// Put the primary display back if the topology restore did not.
    ///
    /// Extended mode moves every monitor's coordinates so the television sits at the origin.
    /// Restoring the captured topology usually undoes that, but not always — and when it does
    /// not, the desktop is left with the TV as primary, which is where new windows open, where
    /// the taskbar lives, and why everything ended up rearranged.
    /// </summary>
    public static void EnsurePrimary(string monitorDevicePath)
    {
        if (monitorDevicePath.Length == 0) return;

        try
        {
            var gdi = TryGetGdiName(monitorDevicePath);
            if (gdi is null)
            {
                Log.Warn("The display that was primary is not attached; leaving primary alone.");
                return;
            }

            if (LegacyDisplay.IsPrimary(gdi))
            {
                Log.Info($"Primary came back on its own as {gdi}.");
                return;
            }

            Log.Info($"Primary did not return on its own; putting it back to {gdi}.");
            LegacyDisplay.MakePrimaryKeepOthers(gdi);

            // Said out loud either way. The previous version of this check was wrong and silent,
            // which is the combination that costs the most time — the log looked like a clean
            // teardown while the desktop plainly was not.
            Log.Info(LegacyDisplay.IsPrimary(gdi)
                     ? $"Primary restored to {gdi}."
                     : $"Primary is still not {gdi}; Windows would not move it.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not restore the primary display: {ex.Message}");
        }
    }

    /// <summary>
    /// Switch a display off, leaving the rest of the desktop as it is.
    ///
    /// Only ever called after the desktop topology has been restored, so the machine already
    /// has somewhere else to put its windows before this display goes away.
    /// </summary>
    public static void Detach(string monitorDevicePath)
    {
        try
        {
            var gdi = TryGetGdiName(monitorDevicePath);
            if (gdi is null)
            {
                Log.Info("The couch display is already off; nothing to switch off.");
                return;
            }

            // Counted from the driver, not Screen.AllScreens — same stale-cache trap as above,
            // and getting this one wrong means switching off the last display someone has.
            if (LegacyDisplay.AttachedCount() <= 1)
            {
                Log.Warn("Refusing to switch off the only display left.");
                return;
            }

            LegacyDisplay.Detach(gdi);
            Log.Info($"Switched off the couch display ({gdi}).");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not switch off the couch display: {ex.Message}");
        }
    }

    /// <summary>
    /// Switch to a topology containing only the given monitor, positioned at the origin
    /// (which is what "primary" means to Windows).
    /// </summary>
    public static void ActivateOnly(string monitorDevicePath)
    {
        // Windows is picky about what shape of config it will accept here, and the rules aren't
        // fully documented. Rather than guess once, try the known-valid forms in order of
        // preference and log which one landed — that log line is the whole diagnostic story
        // if this ever breaks on someone else's hardware.
        var failures = new List<string>();

        if (TryCcdFullArray(monitorDevicePath, out var err1)) { Log.Info("Switched via CCD (full path array)."); return; }
        failures.Add($"CCD full array: {err1}");

        if (TryCcdFullArrayNoModes(monitorDevicePath, out var err2)) { Log.Info("Switched via CCD (no mode array)."); return; }
        failures.Add($"CCD without modes: {err2}");

        if (TryLegacy(monitorDevicePath, out var err3)) { Log.Info("Switched via ChangeDisplaySettingsEx."); return; }
        failures.Add($"ChangeDisplaySettingsEx: {err3}");

        foreach (var f in failures) Log.Error(f);
        throw new InvalidOperationException(
            "Couldn't switch to the TV display. Details are in the log:\n\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    /// Preferred: hand back the whole path array we were given, with only the TV marked active.
    /// Windows matches the supplied config against what it already knows.
    /// </summary>
    private static bool TryCcdFullArray(string monitorDevicePath, out string error)
    {
        var (paths, modes) = Query(QueryFlags.AllPaths);

        int keep = FindPath(paths, monitorDevicePath);
        if (keep < 0) { error = "display not found in the path list"; return false; }

        for (int i = 0; i < paths.Length; i++)
        {
            if (i == keep) paths[i].Flags |= PathActive;
            else paths[i].Flags &= ~PathActive;
        }

        const SdcFlags flags = SdcFlags.Apply
                             | SdcFlags.UseSuppliedDisplayConfig
                             | SdcFlags.AllowChanges
                             | SdcFlags.SaveToDatabase;

        int rc = SetDisplayConfig(paths.Length, paths, modes.Length, modes, flags);
        error = $"error {rc}";
        return rc == ERROR_SUCCESS;
    }

    /// <summary>
    /// Same idea, but drop the mode array and invalidate every mode index so Windows
    /// recomputes modes from its own database. Helps when the queried modes are stale.
    /// </summary>
    private static bool TryCcdFullArrayNoModes(string monitorDevicePath, out string error)
    {
        var (paths, _) = Query(QueryFlags.AllPaths);

        int keep = FindPath(paths, monitorDevicePath);
        if (keep < 0) { error = "display not found in the path list"; return false; }

        for (int i = 0; i < paths.Length; i++)
        {
            if (i == keep) paths[i].Flags |= PathActive;
            else paths[i].Flags &= ~PathActive;

            paths[i].SourceInfo.ModeInfoIdx = InvalidModeIndex;
            paths[i].TargetInfo.ModeInfoIdx = InvalidModeIndex;
        }

        const SdcFlags flags = SdcFlags.Apply
                             | SdcFlags.UseSuppliedDisplayConfig
                             | SdcFlags.AllowChanges;

        int rc = SetDisplayConfig(paths.Length, paths, 0, null, flags);
        error = $"error {rc}";
        return rc == ERROR_SUCCESS;
    }

    /// <summary>Fallback to the pre-CCD API, which is much more permissive about detaching displays.</summary>
    private static bool TryLegacy(string monitorDevicePath, out string error)
    {
        try
        {
            var gdiName = TryGetGdiName(monitorDevicePath);
            if (gdiName is null)
            {
                error = "display isn't currently attached to the desktop";
                return false;
            }
            LegacyDisplay.ActivateOnly(gdiName);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static int FindPath(PathInfo[] paths, string monitorDevicePath)
    {
        int found = -1;
        for (int i = 0; i < paths.Length; i++)
        {
            if (TryGetTargetName(paths[i], out _, out var dp) &&
                string.Equals(dp, monitorDevicePath, StringComparison.OrdinalIgnoreCase))
            {
                found = i;
                // Prefer an already-active path: its source and mode data are known-good.
                if ((paths[i].Flags & PathActive) != 0) return i;
            }
        }
        return found;
    }

    /// <summary>
    /// Make the TV primary while leaving every other monitor running.
    ///
    /// The TV usually isn't attached to the desktop at all when you're working — it's a
    /// disabled output, not just an idle one. So this has to extend onto it first and only
    /// then move primary; the earlier version assumed it was already attached, which is why
    /// this mode appeared to do nothing.
    /// </summary>
    public static void MakePrimaryKeepOthers(string monitorDevicePath)
    {
        var gdiName = TryGetGdiName(monitorDevicePath);

        if (gdiName is null)
        {
            Log.Info("TV isn't attached; extending onto it first.");
            AttachAdditional(monitorDevicePath);

            WaitFor(() => TryGetGdiName(monitorDevicePath) is not null,
                    TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(800));

            gdiName = TryGetGdiName(monitorDevicePath)
                ?? throw new InvalidOperationException(
                    "Couldn't enable the TV display. Make sure it's powered on and set to the right input.");
        }

        LegacyDisplay.MakePrimaryKeepOthers(gdiName);
    }

    /// <summary>
    /// Turn the TV on as an extra display, leaving the existing active set alone.
    /// Tries the same ladder of strategies as <see cref="ActivateOnly"/>.
    /// </summary>
    public static void AttachAdditional(string monitorDevicePath)
    {
        var failures = new List<string>();

        if (TryAttachWithModes(monitorDevicePath, out var e1)) { Log.Info("Extended onto the TV (full arrays)."); return; }
        failures.Add($"CCD full arrays: {e1}");

        if (TryAttachNoModes(monitorDevicePath, out var e2)) { Log.Info("Extended onto the TV (no mode array)."); return; }
        failures.Add($"CCD without modes: {e2}");

        if (TryTopologyExtend(out var e3)) { Log.Info("Extended onto the TV (SDC_TOPOLOGY_EXTEND)."); return; }
        failures.Add($"topology extend: {e3}");

        foreach (var f in failures) Log.Error(f);
        throw new InvalidOperationException(
            "Couldn't enable the TV display:\n\n  " + string.Join("\n  ", failures));
    }

    /// <summary>Every attached display and the rectangle it occupies on the desktop.</summary>
    public static IReadOnlyDictionary<string, Rectangle> AttachedBounds()
    {
        var found = new Dictionary<string, Rectangle>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var display in ListDisplays())
            {
                if (!display.IsActive) continue;
                if (BoundsOf(display.DevicePath) is not { Width: > 0, Height: > 0 } bounds) continue;

                found[display.DevicePath] = bounds;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the display arrangement: {ex.Message}");
        }

        return found;
    }

    /// <summary>
    /// Fold the current arrangement into the config, keeping anything already remembered.
    ///
    /// Additive on purpose. A display that is switched off right now is absent from the query and
    /// must keep the position it had when it was last seen — dropping it would throw away the one
    /// piece of information this exists to preserve.
    /// </summary>
    public static void RememberPositions(AppConfig config)
    {
        try
        {
            foreach (var (path, at) in Positions())
                config.DisplayPositions[path] = $"{at.X},{at.Y}";
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not record where the displays are: {ex.Message}");
        }
    }

    /// <summary>
    /// Put every attached display back where it was last seen.
    ///
    /// The safety net for the whole arrangement rather than one display. Restoring a topology
    /// reproduces the paths and modes that were captured, but a display that was *not* in that
    /// capture is placed by Windows, and placing one can shove the others along to make room.
    /// So after everything else is back, each display is asked to return to the spot it is
    /// remembered at.
    ///
    /// Cheap when nothing moved: a display already in the right place is left untouched without
    /// a call to Windows at all.
    /// </summary>
    public static void RestoreAllPositions(AppConfig config)
    {
        if (config.DisplayPositions.Count == 0) return;

        int moved = 0;

        foreach (var (path, _) in config.DisplayPositions)
        {
            try
            {
                if (TryGetGdiName(path) is null) continue;   // not attached; nothing to place
                if (RestorePosition(config, path)) moved++;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not place a display: {ex.Message}");
            }
        }

        if (moved > 0) Log.Info($"Checked {moved} display position(s) against what was remembered.");
    }

    /// <summary>Put a display back where it was last seen, if anything was recorded for it.</summary>
    public static bool RestorePosition(AppConfig config, string monitorDevicePath)
    {
        if (!config.DisplayPositions.TryGetValue(monitorDevicePath, out var at)) return false;

        var parts = at.Split(',');

        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out int x) ||
            !int.TryParse(parts[1], out int y)) return false;

        return MoveTo(monitorDevicePath, x, y);
    }

    /// <summary>
    /// Where every currently active display sits on the desktop, keyed by device path.
    ///
    /// The desktop is one coordinate space and each display occupies a rectangle in it, so a
    /// monitor being "on the left" is simply a negative X. Windows keeps this, but only for
    /// displays it currently has attached — detach one and the arrangement is gone. Reattaching
    /// then places it wherever Windows chooses, which is to the right of everything else.
    ///
    /// That is why this is recorded and persisted rather than read when needed: at the moment it
    /// is needed, the display is off and there is nothing left to ask.
    /// </summary>
    public static IReadOnlyDictionary<string, (int X, int Y)> Positions()
    {
        var found = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        var (paths, modes) = Query(QueryFlags.OnlyActivePaths);

        foreach (var path in paths)
        {
            if ((path.Flags & PathActive) == 0) continue;

            uint index = path.SourceInfo.ModeInfoIdx;
            if (index == InvalidModeIndex || index >= modes.Length) continue;

            var mode = modes[index];
            if (mode.InfoType != ModeInfoType.Source) continue;

            if (TryGetTargetName(path, out _, out var devicePath) && devicePath.Length > 0)
                found[devicePath] = (mode.SourceMode.Position.X, mode.SourceMode.Position.Y);
        }

        return found;
    }

    /// <summary>
    /// Move a display to a remembered spot on the desktop.
    ///
    /// Used after reattaching one, because attaching says nothing about position — Windows drops
    /// the display to the right of everything else and that is how a monitor that lived on the
    /// left ends up on the right, permanently, from one session that switched it off and on.
    ///
    /// Quiet about failure on purpose. Being in the wrong place is a nuisance; a thrown exception
    /// part way through restoring a desktop is worse than the nuisance.
    /// </summary>
    public static bool MoveTo(string monitorDevicePath, int x, int y)
    {
        try
        {
            var (paths, modes) = Query(QueryFlags.OnlyActivePaths);

            int at = FindPath(paths, monitorDevicePath);
            if (at < 0) return false;

            uint index = paths[at].SourceInfo.ModeInfoIdx;
            if (index == InvalidModeIndex || index >= modes.Length) return false;
            if (modes[index].InfoType != ModeInfoType.Source) return false;

            if (modes[index].SourceMode.Position.X == x &&
                modes[index].SourceMode.Position.Y == y) return true;

            modes[index].SourceMode.Position = new PointL { X = x, Y = y };

            // No AllowChanges here, deliberately. This asks for one specific arrangement, and
            // letting Windows deviate from it is the entire problem being solved.
            const SdcFlags flags = SdcFlags.Apply
                                 | SdcFlags.UseSuppliedDisplayConfig
                                 | SdcFlags.SaveToDatabase;

            int rc = SetDisplayConfig(paths.Length, paths, modes.Length, modes, flags);

            if (rc != ERROR_SUCCESS)
            {
                Log.Warn($"Could not move a display back to {x},{y}: error {rc}.");
                return false;
            }

            Log.Info($"Put a display back at {x},{y} on the desktop.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not move a display back into place: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Add the TV to the active set, passing both arrays exactly as queried.
    ///
    /// Supplying the mode array matters: with SDC_USE_SUPPLIED_DISPLAY_CONFIG the already-active
    /// paths carry mode indices that point into it, so handing over a null array while those
    /// indices are still set is self-inconsistent and Windows rejects the whole call with
    /// ERROR_INVALID_PARAMETER. The newly-activated path can keep its invalid index — Windows
    /// fills that one in itself under SDC_ALLOW_CHANGES.
    /// </summary>
    private static bool TryAttachWithModes(string monitorDevicePath, out string error)
    {
        var (paths, modes) = Query(QueryFlags.AllPaths);

        int keep = FindPath(paths, monitorDevicePath);
        if (keep < 0) { error = "the TV isn't in the path list at all"; return false; }

        paths[keep].Flags |= PathActive;

        const SdcFlags flags = SdcFlags.Apply
                             | SdcFlags.UseSuppliedDisplayConfig
                             | SdcFlags.AllowChanges
                             | SdcFlags.SaveToDatabase;

        int rc = SetDisplayConfig(paths.Length, paths, modes.Length, modes, flags);
        error = $"error {rc}";
        return rc == ERROR_SUCCESS;
    }

    /// <summary>Drop the mode array, but invalidate <em>every</em> index so it stays consistent.</summary>
    private static bool TryAttachNoModes(string monitorDevicePath, out string error)
    {
        var (paths, _) = Query(QueryFlags.AllPaths);

        int keep = FindPath(paths, monitorDevicePath);
        if (keep < 0) { error = "the TV isn't in the path list at all"; return false; }

        paths[keep].Flags |= PathActive;

        for (int i = 0; i < paths.Length; i++)
        {
            paths[i].SourceInfo.ModeInfoIdx = InvalidModeIndex;
            paths[i].TargetInfo.ModeInfoIdx = InvalidModeIndex;
        }

        const SdcFlags flags = SdcFlags.Apply
                             | SdcFlags.UseSuppliedDisplayConfig
                             | SdcFlags.AllowChanges;

        int rc = SetDisplayConfig(paths.Length, paths, 0, null, flags);
        error = $"error {rc}";
        return rc == ERROR_SUCCESS;
    }

    /// <summary>
    /// Blunt fallback: ask Windows to extend across everything it can see. Enables the TV as a
    /// side effect, and may also wake other outputs — acceptable, since the caller wanted the
    /// other displays left on anyway.
    /// </summary>
    private static bool TryTopologyExtend(out string error)
    {
        const SdcFlags SdcTopologyExtend = (SdcFlags)0x00000004;
        int rc = SetDisplayConfig(0, null, 0, null, SdcFlags.Apply | SdcTopologyExtend);
        error = $"error {rc}";
        return rc == ERROR_SUCCESS;
    }

    /// <summary>
    /// GDI device name (\\.\DISPLAYn) for a monitor device path, or null if it isn't attached.
    ///
    /// This deliberately goes through CCD rather than EnumDisplayDevices. On multi-output GPUs
    /// the driver can report the same monitor as a phantom child of *every* adapter — an NVIDIA
    /// card here listed the TV under DISPLAY1 through DISPLAY4 — so matching a device ID against
    /// the adapter tree picks an arbitrary wrong display. CCD maps target to source directly and
    /// only ever reports the real one.
    /// </summary>
    public static string? TryGetGdiName(string monitorDevicePath)
    {
        var (paths, _) = Query(QueryFlags.OnlyActivePaths);

        foreach (var path in paths)
        {
            if ((path.Flags & PathActive) == 0) continue;

            if (TryGetTargetName(path, out _, out var dp) &&
                string.Equals(dp, monitorDevicePath, StringComparison.OrdinalIgnoreCase) &&
                TryGetSourceGdiName(path, out var gdiName))
            {
                return gdiName;
            }
        }
        return null;   // not among the active paths, so genuinely not attached
    }

    private static bool TryGetSourceGdiName(PathInfo path, out string gdiName)
    {
        gdiName = "";

        var req = new SourceDeviceName
        {
            Header = new DeviceInfoHeader
            {
                Type = DeviceInfoType.GetSourceName,
                Size = (uint)Marshal.SizeOf<SourceDeviceName>(),
                AdapterId = path.SourceInfo.AdapterId,
                Id = path.SourceInfo.Id,
            }
        };

        if (DisplayConfigGetDeviceInfo(ref req) != ERROR_SUCCESS) return false;

        gdiName = req.ViewGdiDeviceName ?? "";
        return gdiName.Length > 0;
    }

    /// <summary>
    /// The monitor's native resolution, as the panel itself reports it, or null if it will not say.
    ///
    /// Read from EDID through the target's preferred mode. This matters because "biggest mode
    /// in the list" is not the same thing as "native": a 4K television also accepts 4096×2160
    /// DCI cinema, which is wider than its 3840×2160 panel and has to be scaled down to fit.
    /// Treating that as native would recommend the one mode guaranteed not to map one pixel to
    /// one pixel.
    ///
    /// All paths, not just active ones, so a television that is switched off still answers.
    /// </summary>
    public static DisplayMode? NativeMode(string monitorDevicePath)
    {
        try
        {
            var (paths, _) = Query(QueryFlags.AllPaths);

            foreach (var path in paths)
            {
                if (!TryGetTargetName(path, out _, out var dp) ||
                    !string.Equals(dp, monitorDevicePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var req = new TargetPreferredMode
                {
                    Header = new DeviceInfoHeader
                    {
                        Type = DeviceInfoType.GetTargetPreferredMode,
                        Size = (uint)Marshal.SizeOf<TargetPreferredMode>(),
                        AdapterId = path.TargetInfo.AdapterId,
                        Id = path.TargetInfo.Id,
                    }
                };

                if (DisplayConfigGetDeviceInfo(ref req) != ERROR_SUCCESS) continue;
                if (req.Width == 0 || req.Height == 0) continue;

                var signal = req.TargetMode.TargetVideoSignalInfo;

                int hz = signal.VSyncFreq.Denominator > 0
                       ? (int)Math.Round((double)signal.VSyncFreq.Numerator / signal.VSyncFreq.Denominator)
                       : 0;

                return new DisplayMode((int)req.Width, (int)req.Height, hz);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the native mode for {monitorDevicePath}: {ex.Message}");
        }

        return null;
    }

    /// <summary>Screen bounds of a specific monitor, or null if it isn't currently attached.</summary>
    public static Rectangle? BoundsOf(string monitorDevicePath)
    {
        var gdiName = TryGetGdiName(monitorDevicePath);
        return gdiName is null ? null : LegacyDisplay.BoundsOf(gdiName);
    }

    /// <summary>Re-apply a previously captured topology.</summary>
    public static void Restore(DisplaySnapshot snapshot)
    {
        if (snapshot.Paths.Length == 0) return;

        const SdcFlags flags = SdcFlags.UseSuppliedDisplayConfig
                             | SdcFlags.Apply
                             | SdcFlags.AllowChanges
                             | SdcFlags.SaveToDatabase;

        int rc = SetDisplayConfig(snapshot.Paths.Length, snapshot.Paths,
                                  snapshot.Modes.Length, snapshot.Modes, flags);

        if (rc != ERROR_SUCCESS)
        {
            // Last-ditch fallback: ask Windows to restore its own best-known configuration
            // rather than leaving the user staring at a single display with no way back.
            Log.Warn($"Snapshot restore failed (error {rc}); falling back to the display database.");
            SetDisplayConfig(0, null, 0, null, SdcFlags.Apply | (SdcFlags)0x00000004 /* SDC_TOPOLOGY_EXTEND */);
        }
    }

    /// <summary>Number of currently active monitors — used to confirm a switch actually landed.</summary>
    public static int ActiveDisplayCount()
    {
        var (paths, _) = Query(QueryFlags.OnlyActivePaths);
        return paths.Count(p => (p.Flags & PathActive) != 0);
    }

    /// <summary>Poll until <paramref name="predicate"/> holds. Display changes are asynchronous.</summary>
    public static bool WaitFor(Func<bool> predicate, TimeSpan timeout, TimeSpan settleBuffer)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                Thread.Sleep(settleBuffer);   // drivers report "done" slightly early
                return predicate();
            }
            Thread.Sleep(200);
        }
        return false;
    }

    // ---- internals ----

    private static (PathInfo[] paths, ModeInfo[] modes) Query(QueryFlags flags)
    {
        // Retry: the buffer can grow between sizing and reading if a display is hotplugged.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int rc = GetDisplayConfigBufferSizes(flags, out int pathCount, out int modeCount);
            if (rc != ERROR_SUCCESS)
                throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed (error {rc}).");

            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];

            rc = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);

            if (rc == ERROR_SUCCESS)
            {
                Array.Resize(ref paths, pathCount);
                Array.Resize(ref modes, modeCount);
                return (paths, modes);
            }
            if (rc != 122 /* ERROR_INSUFFICIENT_BUFFER */)
                throw new InvalidOperationException($"QueryDisplayConfig failed (error {rc}).");
        }
        throw new InvalidOperationException("Display configuration kept changing while being read.");
    }

    private static bool TryGetTargetName(PathInfo path, out string friendlyName, out string devicePath)
    {
        friendlyName = "";
        devicePath = "";

        var req = new TargetDeviceName
        {
            Header = new DeviceInfoHeader
            {
                Type = DeviceInfoType.GetTargetName,
                Size = (uint)Marshal.SizeOf<TargetDeviceName>(),
                AdapterId = path.TargetInfo.AdapterId,
                Id = path.TargetInfo.Id,
            }
        };

        if (DisplayConfigGetDeviceInfo(ref req) != ERROR_SUCCESS)
            return false;

        friendlyName = req.MonitorFriendlyDeviceName ?? "";
        devicePath = req.MonitorDevicePath ?? "";
        return true;
    }
}

using System.Diagnostics;

namespace CouchMode.Session;

/// <summary>
/// What woke the machine up, when it can be told.
///
/// A controller *can* wake a PC — a wired pad or an Xbox wireless adapter will do it when
/// "Allow this device to wake the computer" is ticked on it in Device Manager. Bluetooth pads
/// generally cannot, because most Bluetooth radios are not wake-capable.
///
/// Knowing whether that is what happened matters because the two cases want opposite treatment.
/// Waking with the mouse while a controller happens to be plugged in must do nothing at all;
/// waking by pressing the button on a controller is somebody sitting down to play, and is the
/// one moment where starting a session unasked is what they wanted.
///
/// Windows records this, and powercfg reads it back. There is no API for it that does not mean
/// parsing the power event log directly, and shelling out to a tool that ships with Windows is
/// both simpler and less likely to break — this runs once per wake, so the cost of a process is
/// irrelevant.
/// </summary>
public static class WakeSource
{
    /// <summary>
    /// Whether the last wake was caused by a game controller.
    ///
    /// Deliberately cautious: anything it cannot read, cannot parse, or cannot positively
    /// identify as a controller comes back false. Being wrong in this direction means a session
    /// someone has to start themselves; being wrong the other way means the television taking
    /// over while they are trying to read their email.
    /// </summary>
    public static bool WasController()
    {
        try
        {
            var report = Run();
            if (report.Length == 0) return false;

            // Only a device wake can be a controller. A timer, a network packet or a power
            // button all report differently, and none of them are a person picking up a pad.
            if (!report.Contains("Type: Device", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"Woken by something other than a device: {Summarise(report)}");
                return false;
            }

            bool controller = LooksLikeController(report);

            Log.Info(controller
                     ? $"Woken by a controller: {Summarise(report)}"
                     : $"Woken by a device that is not a controller: {Summarise(report)}");

            return controller;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read what woke the machine: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Whether the wake record names something that is a controller.
    ///
    /// Matched on the instance path's vendor and product, and on the words Windows uses for
    /// these devices. The vendor list is the one that matters: a HID device that woke the
    /// machine and belongs to Microsoft, Sony or Nintendo is a pad in every practical case,
    /// while "USB Input Device" on its own is equally a keyboard.
    /// </summary>
    private static bool LooksLikeController(string report)
    {
        foreach (var vendor in ControllerVendors)
            if (report.Contains(vendor, StringComparison.OrdinalIgnoreCase))
                return true;

        foreach (var word in ControllerWords)
            if (report.Contains(word, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// VID fragments as they appear in an instance path.
    ///
    /// Microsoft covers Xbox pads and the wireless adapter, Sony the DualShock and DualSense,
    /// Nintendo the Switch pads, and the rest are the common third-party makers. A pad not on
    /// this list falls through to the word match below, and failing that simply does not
    /// trigger — which is the safe outcome.
    /// </summary>
    private static readonly string[] ControllerVendors =
    [
        "VID_045E",   // Microsoft
        "VID_054C",   // Sony
        "VID_057E",   // Nintendo
        "VID_046D",   // Logitech
        "VID_0F0D",   // Hori
        "VID_2DC8",   // 8BitDo
        "VID_20D6",   // PowerA
        "VID_24C6",   // PDP / Afterglow
    ];

    private static readonly string[] ControllerWords =
    [
        "controller", "gamepad", "game pad", "joystick", "xbox", "dualsense", "dualshock",
    ];

    /// <summary>The interesting lines, for the log. The full report is several lines of preamble.</summary>
    private static string Summarise(string report)
    {
        var parts = new List<string>();

        foreach (var line in report.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("Instance Path:", StringComparison.OrdinalIgnoreCase)
             || trimmed.StartsWith("Friendly Name:", StringComparison.OrdinalIgnoreCase)
             || trimmed.StartsWith("Description:", StringComparison.OrdinalIgnoreCase)
             || trimmed.StartsWith("Type:", StringComparison.OrdinalIgnoreCase))
            {
                if (trimmed.Split(':', 2) is [_, { Length: > 0 } value] && value.Trim().Length > 0)
                    parts.Add(trimmed);
            }
        }

        return parts.Count == 0 ? "nothing recorded" : string.Join(" | ", parts);
    }

    /// <summary>
    /// powercfg /lastwake, captured.
    ///
    /// Needs no administrator rights for this query, unlike most of powercfg. Given a short
    /// timeout because a hung helper process must never hold up a wake — the app has displays
    /// and audio to think about, and this is only advisory.
    /// </summary>
    private static string Run()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            Arguments = "/lastwake",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        if (process is null) return "";

        string output = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit(3000))
        {
            try { process.Kill(); } catch { /* going away anyway */ }
            return "";
        }

        return output;
    }
}

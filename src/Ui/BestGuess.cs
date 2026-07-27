namespace CouchMode.Ui;

using CouchMode.Audio;
using CouchMode.Display;

/// <summary>
/// First-run guesses for the display and audio pickers.
///
/// Only ever used to preselect something on a fresh install, never to override a choice the
/// user has made. A wrong guess costs one dropdown change; no guess at all means the app does
/// nothing at all until two settings are found and set, which is a much worse first impression.
///
/// The rules are the same ones a person would apply reading the list: a television usually says
/// so in its name or hangs off HDMI, and TV sound almost always comes out of the graphics card.
/// </summary>
internal static class BestGuess
{
    /// <summary>Words that name a television outright.</summary>
    private static readonly string[] TvNames =
        ["tv", "television", "bravia", "regza", "aquos", "viera", "hisense", "vizio",
         "roku", "shield", "projector", "beamer"];

    /// <summary>Connections a television is normally on. HDMI first — it is the usual answer.</summary>
    private static readonly string[] TvConnections = ["hdmi", "hdtv", "displayport-hdmi"];

    /// <summary>Names that mean the sound is leaving through the graphics card.</summary>
    private static readonly string[] GpuAudio =
        ["nvidia", "amd", "ati", "radeon", "high definition audio device", "hd audio driver",
         "intel(r) display audio", "display audio", "digital display audio"];

    /// <summary>A monitor rather than a television, so it is never guessed at.</summary>
    private static readonly string[] MonitorNames =
        ["monitor", "generic pnp", "dell", "asus", "benq", "acer", "aoc", "msi", "gigabyte",
         "viewsonic", "lenovo", "hp ", "iiyama", "alienware", "odyssey", "predator"];

    /// <summary>
    /// The display most likely to be the television, or null when nothing stands out.
    ///
    /// Ordered by how strong the evidence is: something calling itself a TV is a better bet
    /// than something merely on HDMI, and an inactive display is a better bet than an active
    /// one — the TV is usually off while its owner sits at the desk setting this up.
    /// </summary>
    public static DisplayInfo? Tv(IEnumerable<DisplayInfo> displays)
    {
        var all = displays.ToList();
        if (all.Count == 0) return null;

        // With exactly one other display, the one that is not the desktop is the answer.
        var scored = all
            .Select(d => (Display: d, Score: ScoreDisplay(d)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();

        return scored.Count > 0 ? scored[0].Display : null;
    }

    private static int ScoreDisplay(DisplayInfo display)
    {
        var name = (display.FriendlyName ?? "").ToLowerInvariant();
        var path = (display.DevicePath ?? "").ToLowerInvariant();

        int score = 0;

        if (TvNames.Any(n => name.Contains(n, StringComparison.Ordinal))) score += 100;
        if (TvConnections.Any(c => name.Contains(c, StringComparison.Ordinal)
                                || path.Contains(c, StringComparison.Ordinal))) score += 60;

        // A named monitor brand is strong evidence against, but not proof — plenty of TVs are
        // made by the same companies — so it subtracts rather than disqualifies.
        if (MonitorNames.Any(n => name.Contains(n, StringComparison.Ordinal))) score -= 70;

        // Switched off right now, which is what a TV usually is while someone is at their desk.
        if (!display.IsActive) score += 25;

        return score;
    }

    /// <summary>
    /// The playback device most likely to be the television.
    ///
    /// Graphics-card outputs carry HDMI audio, so those come first. A device whose name matches
    /// the chosen display is better still — Windows often names the endpoint after the panel.
    /// </summary>
    public static AudioDeviceInfo? TvAudio(IEnumerable<AudioDeviceInfo> devices, DisplayInfo? tv)
    {
        var all = devices.ToList();
        if (all.Count == 0) return null;

        var display = (tv?.FriendlyName ?? "").ToLowerInvariant();

        var scored = all
            .Select(a => (Device: a, Score: ScoreAudio(a, display)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();

        return scored.Count > 0 ? scored[0].Device : null;
    }

    private static int ScoreAudio(AudioDeviceInfo device, string tvName)
    {
        var name = (device.FriendlyName ?? "").ToLowerInvariant();
        int score = 0;

        if (GpuAudio.Any(g => name.Contains(g, StringComparison.Ordinal))) score += 80;
        if (TvNames.Any(n => name.Contains(n, StringComparison.Ordinal))) score += 60;

        // Named after the display we picked, which is as direct a link as this gets.
        if (tvName.Length > 3)
        {
            var word = tvName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                             .FirstOrDefault(w => w.Length > 3);

            if (word is not null && name.Contains(word, StringComparison.Ordinal)) score += 90;
        }

        // Headsets, speakers and webcams are what the desktop uses, not the television.
        foreach (var no in new[] { "headset", "headphone", "earphone", "webcam", "microphone",
                                   "realtek", "speakers", "usb audio" })
            if (name.Contains(no, StringComparison.Ordinal)) score -= 60;

        return score;
    }

    /// <summary>
    /// The device to come back to: whatever is default right now.
    ///
    /// Correct by definition on a fresh install — the user is sitting at their desk hearing it.
    /// </summary>
    public static AudioDeviceInfo? DesktopAudio(IEnumerable<AudioDeviceInfo> devices)
        => devices.FirstOrDefault(d => d.IsActive);
}

using System.IO.Compression;
using System.Text;
using CouchMode.Display;

namespace CouchMode;

/// <summary>
/// Bundles everything needed to diagnose a problem into one file the user can attach.
///
/// This exists because of what bug reports normally look like. Someone says HDR did not switch
/// off, and answering that needs the log, the display topology, the audio devices and the
/// settings in force — four things kept in three places, none of which a person should have to
/// be told how to find. Asking for them one at a time costs a round trip each, and the log has
/// usually rolled over by the time the third reply arrives.
///
/// So it is one button and one file. The zip is deliberately readable rather than encoded:
/// anyone is free to open it and see exactly what they are about to send, which matters when
/// the contents include device names and file paths from their own machine.
/// </summary>
public static class BugReport
{
    /// <summary>
    /// Write the bundle and return where it went.
    ///
    /// Saved to the desktop rather than the app's own data folder, because the next thing that
    /// happens to it is being dragged into a browser.
    /// </summary>
    public static string Write(AppConfig config)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var folder = Directory.Exists(desktop) ? desktop : AppConfig.Directory;

        var path = Path.Combine(folder, $"couch-mode-bug-report-{DateTime.Now:yyyy-MM-dd-HHmm}.zip");

        // Overwritten rather than numbered. Two reports a minute apart are the same session and
        // the same problem, and a folder slowly filling with near-identical zips is its own
        // small annoyance.
        if (File.Exists(path)) File.Delete(path);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        Add(zip, "summary.txt", Summary(config));
        Add(zip, "diagnostics.txt", Safely(() => DisplayDiagnostics.Report(config)));
        Add(zip, "config.json", Safely(ReadConfigFile));
        Add(zip, "couchsession.log", Safely(ReadLog));

        Log.Info($"Bug report written to {path}");
        return path;
    }

    /// <summary>
    /// The context that is obvious to the user and invisible to everyone else: which build,
    /// which Windows, what hardware. Put first so it is the first thing opened.
    /// </summary>
    private static string Summary(AppConfig config)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"{AppInfo.Name} bug report");
        sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        sb.AppendLine(new string('=', 72));
        sb.AppendLine();
        sb.AppendLine("--- WHAT WENT WRONG ---");
        sb.AppendLine();
        sb.AppendLine("  (Please describe it here, or in the issue itself.)");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("--- BUILD ---");
        sb.AppendLine($"Version        : {AppInfo.Version}");
        sb.AppendLine($"Repository     : {AppInfo.Repository}");
        sb.AppendLine();
        sb.AppendLine("--- MACHINE ---");
        sb.AppendLine($"Windows        : {Environment.OSVersion.VersionString}");
        sb.AppendLine($"64-bit OS      : {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"Processors     : {Environment.ProcessorCount}");
        sb.AppendLine($"Displays seen  : {Screen.AllScreens.Length}");
        sb.AppendLine($".NET           : {Environment.Version}");
        sb.AppendLine();
        sb.AppendLine("--- SETTINGS IN FORCE ---");
        sb.AppendLine($"TV display     : {config.TvDisplayName}");
        sb.AppendLine($"Other displays : {config.DisplayMode}");
        sb.AppendLine($"Video mode     : {Blank(config.TvVideoMode)}");
        sb.AppendLine($"Switch audio   : {config.SwitchAudio}");
        sb.AppendLine($"Auto HDR       : {config.AutoHdrEnabled} ({config.HdrGames.Count} game(s))");
        sb.AppendLine($"Whole session  : {config.HdrForWholeSession}");
        sb.AppendLine($"Start on pad   : {config.StartOnControllerConnect}");
        sb.AppendLine($"Pad drop out   : {config.OnDisconnect}");
        sb.AppendLine($"Chosen pad     : {Blank(config.TriggerControllerId)}");
        sb.AppendLine($"Power plan     : {config.ChangePowerPlan} ({Blank(config.SessionPowerPlan)})");
        sb.AppendLine($"Keep awake     : {config.KeepDisplayAwake}");
        sb.AppendLine($"Quiet hours    : {config.SilenceNotifications}");
        sb.AppendLine($"Game Mode      : {config.EnableGameMode}");
        sb.AppendLine($"Game priority  : {config.GamePriorityEnabled}");
        sb.AppendLine();
        sb.AppendLine("--- IN THIS BUNDLE ---");
        sb.AppendLine("diagnostics.txt        every display and audio device Windows reports");
        sb.AppendLine("config.json            your saved settings, exactly as stored");
        sb.AppendLine("couchsession.log    what the app did, most recent last");

        return sb.ToString();
    }

    private static string Blank(string value) => value.Length == 0 ? "(not set)" : value;

    /// <summary>
    /// A part that cannot be gathered must not lose the parts that can.
    ///
    /// The whole point of this file is that it arrives complete. If reading the log throws
    /// because something else has it open, the report should still carry the diagnostics and
    /// say what is missing, rather than failing and leaving the user with nothing to send.
    /// </summary>
    private static string Safely(Func<string> read)
    {
        try { return read(); }
        catch (Exception ex) { return $"This part could not be collected: {ex.Message}"; }
    }

    private static string ReadLog()
    {
        var path = Path.Combine(AppConfig.Directory, "couchsession.log");
        if (!File.Exists(path)) return "No log file yet.";

        // Opened share-all: the app itself has this file open for appending, and a plain
        // File.ReadAllText would be denied.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ReadConfigFile()
    {
        return File.Exists(AppConfig.FilePath)
             ? File.ReadAllText(AppConfig.FilePath)
             : "No settings file yet.";
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);

        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }

    /// <summary>Where to file it, with the version already filled in.</summary>
    public static string IssueUrl =>
        $"{AppInfo.RepositoryUrl}/issues/new?labels=bug"
      + $"&title={Uri.EscapeDataString($"[{AppInfo.Version}] ")}"
      + $"&body={Uri.EscapeDataString(IssueTemplate)}";

    private static string IssueTemplate =>
        "**What happened**\n\n\n"
      + "**What you expected**\n\n\n"
      + "**Steps to reproduce**\n\n1. \n2. \n3. \n\n"
      + "**Bug report file**\n\nPlease attach the zip that was just saved to your desktop — "
      + "it contains the log, your settings and what Windows reports about your displays "
      + "and audio devices.\n";
}

using System.Text;
using CouchMode.Audio;

namespace CouchMode.Display;

/// <summary>
/// Dumps everything the app can see about displays and audio, so a mismatch between what
/// Windows reports and what the user actually has can be diagnosed from a single file
/// rather than inferred from behaviour.
/// </summary>
public static class DisplayDiagnostics
{
    public static string Report(AppConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Couch Session diagnostics");
        sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine(new string('=', 72));
        sb.AppendLine();

        sb.AppendLine("--- CONFIGURED ---");
        sb.AppendLine($"TV display name : {config.TvDisplayName}");
        sb.AppendLine($"TV display path : {config.TvDisplayPath}");
        sb.AppendLine($"Display mode    : {config.DisplayMode}");
        sb.AppendLine();

        sb.AppendLine("--- RESOLUTION OF THE CONFIGURED TV ---");
        try
        {
            var gdi = DisplayManager.TryGetGdiName(config.TvDisplayPath);
            sb.AppendLine($"TryGetGdiName   : {gdi ?? "(null - not attached)"}");

            var bounds = DisplayManager.BoundsOf(config.TvDisplayPath);
            sb.AppendLine($"BoundsOf        : {(bounds is null ? "(null)" : $"{bounds.Value.Width}x{bounds.Value.Height} at {bounds.Value.X},{bounds.Value.Y}")}");
        }
        catch (Exception ex) { sb.AppendLine($"  FAILED: {ex.Message}"); }
        sb.AppendLine();

        sb.AppendLine("--- CCD PATHS (all, including inactive) ---");
        try
        {
            foreach (var d in DisplayManager.ListDisplays())
            {
                sb.AppendLine($"  {(d.IsActive ? "ACTIVE  " : "inactive")} targetId={d.TargetId,-6} \"{d.FriendlyName}\"");
                sb.AppendLine($"           path={d.DevicePath}");
            }
        }
        catch (Exception ex) { sb.AppendLine($"  FAILED: {ex.Message}"); }
        sb.AppendLine();

        sb.AppendLine("--- GDI ADAPTERS AND MONITORS ---");
        try
        {
            foreach (var line in LegacyDisplay.Inventory())
                sb.AppendLine("  " + line);
        }
        catch (Exception ex) { sb.AppendLine($"  FAILED: {ex.Message}"); }
        sb.AppendLine();

        sb.AppendLine("--- WINFORMS SCREENS ---");
        try
        {
            foreach (var s in Screen.AllScreens)
                sb.AppendLine($"  {s.DeviceName,-16} {s.Bounds.Width}x{s.Bounds.Height} at {s.Bounds.X},{s.Bounds.Y}"
                            + (s.Primary ? "  [PRIMARY]" : ""));
        }
        catch (Exception ex) { sb.AppendLine($"  FAILED: {ex.Message}"); }
        sb.AppendLine();

        sb.AppendLine("--- AUDIO ---");
        try
        {
            var current = AudioManager.GetDefaultPlaybackDeviceId();
            sb.AppendLine($"  current default : {current}");
            sb.AppendLine($"  configured TV   : {config.TvAudioDeviceId}  ({config.TvAudioDeviceName})");
            sb.AppendLine($"  configured desk : {(config.DesktopAudioDeviceId.Length == 0 ? "(auto)" : config.DesktopAudioDeviceId)}");
            sb.AppendLine();
            foreach (var a in AudioManager.ListPlaybackDevices())
                sb.AppendLine($"  {(a.Id == current ? "* " : "  ")}{a.FriendlyName}\n      {a.Id}");
        }
        catch (Exception ex) { sb.AppendLine($"  FAILED: {ex.Message}"); }

        return sb.ToString();
    }



    public static string Write(AppConfig config)
    {
        Directory.CreateDirectory(AppConfig.Directory);
        var path = Path.Combine(AppConfig.Directory, "diagnostics.txt");
        File.WriteAllText(path, Report(config), Encoding.UTF8);
        return path;
    }
}

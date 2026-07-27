using Microsoft.Win32;

namespace CouchMode.Games;

/// <summary>
/// Game icons taken from Steam's own library cache.
///
/// This is the right answer wherever it applies. Steam already downloaded artwork for every
/// installed game and files it by app id, so there is no guessing which executable is the game
/// — the ambiguity that produced anti-cheat and crash-handler icons simply does not exist here.
///
/// Only Steam games have an app id, so executable extraction stays as the fallback for Epic,
/// GOG and hand-added entries.
/// </summary>
internal static class SteamIcons
{
    /// <summary>
    /// Cached artwork for an app id, or null if Steam has not downloaded any.
    ///
    /// Steam has used more than one cache layout, so several are checked. Files are read into
    /// memory rather than opened in place: Image.FromFile keeps a lock on the file for the
    /// lifetime of the image, and locking Steam's own cache would be rude at best.
    /// </summary>
    public static Image? Load(int appId)
    {
        foreach (var path in Candidates(appId))
        {
            try
            {
                if (!File.Exists(path)) continue;

                var bytes = File.ReadAllBytes(path);
                if (bytes.Length == 0) continue;

                using var stream = new MemoryStream(bytes);
                return Image.FromStream(stream);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read Steam artwork {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(int appId)
    {
        if (SteamPath is not { } steam) yield break;

        var cache = Path.Combine(steam, "appcache", "librarycache");

        // Current layout: a folder per app.
        var folder = Path.Combine(cache, appId.ToString());
        yield return Path.Combine(folder, "icon.jpg");
        yield return Path.Combine(folder, "logo.png");
        yield return Path.Combine(folder, "header.jpg");

        // Older layout: files named by app id.
        yield return Path.Combine(cache, $"{appId}_icon.jpg");
        yield return Path.Combine(cache, $"{appId}_logo.png");
        yield return Path.Combine(cache, $"{appId}_header.jpg");
    }

    /// <summary>Where Steam is installed, or null. Resolved once.</summary>
    private static string? SteamPath => _steamPath ??= FindSteam();

    private static string? _steamPath;

    private static string? FindSteam()
    {
        try
        {
            var path = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")
                              ?.GetValue("SteamPath") as string;

            return string.IsNullOrWhiteSpace(path) ? null : path.Replace('/', '\\');
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not locate Steam: {ex.Message}");
            return null;
        }
    }
}

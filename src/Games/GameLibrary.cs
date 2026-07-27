using System.Text.Json;
using Microsoft.Win32;

namespace CouchMode.Games;

public enum GameSource { Steam, Epic, Gog, Manual }

/// <summary>
/// A game we can recognise at runtime.
///
/// Identified by install folder rather than executable name. Games ship launchers, crash
/// handlers, anti-cheat services and redistributables, so picking "the" exe is guesswork —
/// whereas "is the running program inside this folder" is exact and needs no per-game data.
/// </summary>
public sealed record GameEntry(string Name, string InstallDir, GameSource Source)
{
    /// <summary>Stable key for the config file. The folder is what identifies the game.</summary>
    public string Key => InstallDir.TrimEnd('\\').ToLowerInvariant();

    /// <summary>
    /// When the game was last played, where the launcher records it. Steam does; Epic and GOG
    /// do not expose it, so those sort as "never" rather than being excluded.
    /// </summary>
    public DateTime? LastPlayed { get; init; }

    /// <summary>Best guess at the main executable, used only to pull an icon.</summary>
    public string? IconSource { get; init; }

    /// <summary>
    /// When the game's folder was created, as a stand-in for when it was installed.
    ///
    /// No launcher exposes an install date in a form worth reading, and the folder's creation
    /// time is right often enough to sort by — it is wrong mainly for games moved between
    /// drives, which reads as "recently installed" and is arguably fair.
    /// </summary>
    public DateTime? Installed { get; init; }

    /// <summary>
    /// Steam's own id for the game, when it came from Steam.
    ///
    /// The only identifier that matches an external database exactly. Titles do not: store
    /// fronts, wikis and launchers all punctuate and subtitle them differently.
    /// </summary>
    public int? SteamAppId { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// Finds installed games by reading the launchers' own catalogues. No scanning of the whole
/// disk, and nothing runs in the background — this is only called when the settings window is
/// open, or when the user presses Refresh.
/// </summary>
public static class GameLibrary
{
    public static IReadOnlyList<GameEntry> DiscoverAll()
    {
        var found = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in Safe(FindSteamGames, "Steam")
                     .Concat(Safe(FindEpicGames, "Epic"))
                     .Concat(Safe(FindGogGames, "GOG")))
        {
            if (Directory.Exists(game.InstallDir))
                found.TryAdd(game.Key, game);
        }

        return Deduplicate(found.Values)
              .Select(g => g with { Installed = SafeCreated(g.InstallDir) })
              .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
              .ToList();
    }

    /// <summary>
    /// Collapse the same game owned in more than one place down to a single row.
    ///
    /// Install folder alone cannot do this: a game owned on both Steam and Epic sits in two
    /// directories, so it passed the folder check twice and appeared twice in the list. Worse,
    /// only one of those rows carried a Steam app id, so the duplicate looked like it had no
    /// HDR information while its twin did.
    ///
    /// Where two rows are the same game, the Steam one wins — it is the only one that can be
    /// looked up exactly.
    /// </summary>
    private static IEnumerable<GameEntry> Deduplicate(IEnumerable<GameEntry> games)
    {
        var best = new Dictionary<string, GameEntry>(StringComparer.Ordinal);

        foreach (var game in games)
        {
            var key = NameKey(game.Name);

            if (key.Length == 0) continue;

            if (!best.TryGetValue(key, out var existing))
            {
                best[key] = game;
                continue;
            }

            // Prefer the entry that can be identified precisely, then the one that has been
            // played, so the surviving row carries the most useful information.
            bool better = (game.SteamAppId is not null && existing.SteamAppId is null)
                       || (game.LastPlayed is not null && existing.LastPlayed is null);

            if (better) best[key] = game;
        }

        return best.Values;
    }

    private static DateTime? SafeCreated(string dir)
    {
        try { return Directory.GetCreationTimeUtc(dir); }
        catch { return null; }
    }

    /// <summary>A title reduced to letters and digits, for spotting the same game twice.</summary>
    private static string NameKey(string name)
    {
        var text = name.ToLowerInvariant();

        int bracket = text.IndexOf('(');
        if (bracket > 0) text = text[..bracket];

        return new string(text.Where(char.IsLetterOrDigit).ToArray());
    }

    private static IEnumerable<GameEntry> Safe(Func<IEnumerable<GameEntry>> discover, string label)
    {
        try
        {
            return discover().ToList();
        }
        catch (Exception ex)
        {
            // One broken launcher must not stop the others from being listed.
            Log.Warn($"Could not read the {label} library: {ex.Message}");
            return [];
        }
    }

    // ================= Steam =================

    /// <summary>The install folder for a single Steam app id, or null if it is not installed.</summary>
    public static string? SteamInstallDir(int appId) => SteamGameByAppId(appId)?.InstallDir;

    /// <summary>
    /// A single Steam game — name and install folder — by app id, or null if it is not installed.
    /// Cheap: it opens only that game's manifest, unlike <see cref="DiscoverAll"/> which reads every
    /// one. Used to close a running game at the end of a session, and to remember an HDR choice for
    /// whatever is running, even when the game was never on the Auto-HDR list.
    /// </summary>
    public static GameEntry? SteamGameByAppId(int appId)
    {
        if (appId <= 0) return null;

        var steamPath = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
        if (string.IsNullOrWhiteSpace(steamPath)) return null;

        steamPath = steamPath.Replace('/', '\\');

        foreach (var library in SteamLibraries(steamPath))
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;

            try
            {
                var text = File.ReadAllText(manifest);
                var name = VdfValue(text, "name");
                var dir = VdfValue(text, "installdir");

                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(dir))
                    return new GameEntry(name, Path.Combine(library, "steamapps", "common", dir), GameSource.Steam)
                    {
                        SteamAppId = appId,
                    };
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read the manifest for app {appId}: {ex.Message}");
            }
        }

        return null;
    }

    private static IEnumerable<GameEntry> FindSteamGames()
    {
        var steamPath = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
        if (string.IsNullOrWhiteSpace(steamPath)) yield break;

        steamPath = steamPath.Replace('/', '\\');

        foreach (var library in SteamLibraries(steamPath))
        {
            var appsDir = Path.Combine(library, "steamapps");
            if (!Directory.Exists(appsDir)) continue;

            foreach (var manifest in Directory.EnumerateFiles(appsDir, "appmanifest_*.acf"))
            {
                GameEntry? entry = null;
                try
                {
                    var text = File.ReadAllText(manifest);
                    var name = VdfValue(text, "name");
                    var dir = VdfValue(text, "installdir");

                    if (name is not null && dir is not null && !IsSteamTool(name, VdfValue(text, "appid")))
                    {
                        var full = Path.Combine(appsDir, "common", dir);

                        DateTime? lastPlayed = null;
                        if (long.TryParse(VdfValue(text, "LastPlayed"), out var unix) && unix > 0)
                            lastPlayed = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;

                        int? appId = int.TryParse(VdfValue(text, "appid"), out var id) ? id : null;

                        entry = new GameEntry(name, full, GameSource.Steam)
                        {
                            LastPlayed = lastPlayed,
                            SteamAppId = appId,
                        };
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Skipped Steam manifest {Path.GetFileName(manifest)}: {ex.Message}");
                }

                if (entry is not null) yield return entry;
            }
        }
    }

    /// <summary>
    /// Steam entries that are not games.
    ///
    /// Steam installs its redistributables and runtimes through the same manifest mechanism as
    /// games, so they appear in steamapps exactly like a title would — Steamworks Common
    /// Redistributables is the one everyone sees. There is no flag in the manifest saying
    /// "this is a tool", so they are recognised by id where possible and by name otherwise.
    /// </summary>
    private static bool IsSteamTool(string name, string? appId)
    {
        if (appId is not null && ToolAppIds.Contains(appId)) return true;

        return ToolNames.Any(tool => name.Contains(tool, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly HashSet<string> ToolAppIds =
    [
        "228980",    // Steamworks Common Redistributables
        "1070560",   // Steam Linux Runtime 1.0 (scout)
        "1391110",   // Steam Linux Runtime 2.0 (soldier)
        "1628350",   // Steam Linux Runtime 3.0 (sniper)
        "1493710",   // Proton Experimental
        "858280",    // Proton 3.7
        "961940",    // Proton 3.16
        "1054830",   // Proton 4.11
        "1113280",   // Proton 4.11 beta
        "1245040",   // Proton 5.0
        "1420170",   // Proton 5.13
        "1580130",   // Proton 6.3
        "1887720",   // Proton 7.0
        "2180100",   // Proton 8.0
        "2805730",   // Proton 9.0
        "1826330",   // Steam Deck related
        "1161040",   // GameMaker runtime
    ];

    private static readonly string[] ToolNames =
    [
        "Steamworks Common Redist", "Steam Linux Runtime", "Proton ", "Proton Experimental",
        "SteamVR", "Steam Controller Configs", "Steamworks Shared",
    ];

    /// <summary>Every Steam library folder, including drives other than the one Steam is on.</summary>
    private static IEnumerable<string> SteamLibraries(string steamPath)
    {
        yield return steamPath;

        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        // The format has changed between Steam versions; matching every quoted "path" value
        // works across all of them without a full VDF parser.
        foreach (var line in File.ReadLines(vdf))
        {
            var path = VdfValue(line, "path");
            if (path is not null) yield return path.Replace(@"\\", @"\");
        }
    }

    /// <summary>Pull "key" "value" out of VDF text. Good enough for the handful of fields we need.</summary>
    private static string? VdfValue(string text, string key)
    {
        var marker = $"\"{key}\"";
        int at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        int open = text.IndexOf('"', at + marker.Length);
        if (open < 0) return null;

        int close = text.IndexOf('"', open + 1);
        return close < 0 ? null : text[(open + 1)..close];
    }

    // ================= Epic =================

    private static IEnumerable<GameEntry> FindEpicGames()
    {
        var manifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (!Directory.Exists(manifestDir)) yield break;

        foreach (var file in Directory.EnumerateFiles(manifestDir, "*.item"))
        {
            GameEntry? entry = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;

                var name = root.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
                var dir = root.TryGetProperty("InstallLocation", out var d) ? d.GetString() : null;

                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(dir))
                    entry = new GameEntry(name, dir, GameSource.Epic);
            }
            catch (Exception ex)
            {
                Log.Warn($"Skipped Epic manifest {Path.GetFileName(file)}: {ex.Message}");
            }

            if (entry is not null) yield return entry;
        }
    }

    // ================= GOG =================

    private static IEnumerable<GameEntry> FindGogGames()
    {
        foreach (var root in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) continue;

            foreach (var id in key.GetSubKeyNames())
            {
                using var game = key.OpenSubKey(id);
                var name = game?.GetValue("gameName") as string;
                var dir = game?.GetValue("path") as string;

                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(dir))
                    yield return new GameEntry(name, dir, GameSource.Gog);
            }
        }
    }

    // ================= manual =================

    /// <summary>
    /// Build an entry from something the user picked. Accepts an executable or a folder;
    /// an executable is reduced to its folder, since matching is folder-based.
    /// </summary>
    public static GameEntry? FromPath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) return null;
                return new GameEntry(Path.GetFileNameWithoutExtension(path), dir, GameSource.Manual)
                {
                    IconSource = path,
                };
            }

            if (Directory.Exists(path))
                return new GameEntry(new DirectoryInfo(path).Name, path, GameSource.Manual);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not add {path}: {ex.Message}");
        }
        return null;
    }
}

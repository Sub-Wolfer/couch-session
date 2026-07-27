using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace CouchMode.Games;

/// <summary>
/// Icons for the game list, pulled from each game's own executable.
///
/// Extraction touches the disk, so it never happens on the UI thread and never blocks the list
/// from appearing. Icons load in the background and the list repaints as they arrive. Results
/// are cached for the lifetime of the window, including failures, so a game with no usable
/// icon is not retried on every repaint.
/// </summary>
internal sealed class GameIcons : IDisposable
{
    private readonly ConcurrentDictionary<string, Image?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when an icon finishes loading, so the caller can repaint.</summary>
    public event Action? IconLoaded;

    /// <summary>
    /// The icon if we already have it, otherwise null and a background load is started.
    /// Callers draw a placeholder for null and repaint when <see cref="IconLoaded"/> fires.
    /// </summary>
    public Image? Get(GameEntry game)
    {
        if (_cache.TryGetValue(game.Key, out var cached)) return cached;

        if (_inFlight.TryAdd(game.Key, 0))
            Task.Run(() => Load(game));

        return null;
    }

    private void Load(GameEntry game)
    {
        Image? image = null;
        try
        {
            // Steam first, always.
            //
            // Its cache is keyed by app id, so it identifies the game outright rather than
            // inferring it from the files on disk. Every wrong icon so far — anti-cheat
            // bootstrappers, crash handlers, redistributables — came from that inference, and
            // for Steam games it is simply unnecessary.
            if (game.SteamAppId is { } appId)
            {
                image = SteamIcons.Load(appId);

                if (image is not null)
                {

                    _cache[game.Key] = image;
                    _inFlight.TryRemove(game.Key, out _);
                    IconLoaded?.Invoke();
                    return;
                }
            }

            // Try candidates in order of how likely each is to be the game, and keep going
            // until one actually yields an icon. Picking a single "best" executable and giving
            // up if it had no icon resource was why plenty of games came back blank — the
            // right icon was often on the second or third candidate.
            foreach (var exe in Candidates(game))
            {
                image = Extract(exe);
                if (image is null) continue;

                break;
            }

            if (image is null) Log.Info($"No embedded icon found for {game.Name}; using a letter tile.");
        }
        catch (Exception ex)
        {
            Log.Warn($"No icon for {game.Name}: {ex.Message}");
        }

        // Cache failures too — a null result is an answer, not a reason to try again.
        _cache[game.Key] = image;
        _inFlight.TryRemove(game.Key, out _);

        IconLoaded?.Invoke();
    }

    /// <summary>
    /// Pick the executable most likely to be the game itself.
    ///
    /// Game folders are full of installers, crash handlers, anti-cheat services and
    /// redistributables, all of which have generic icons. Filtering those by name and then
    /// taking the largest remaining executable lands on the real game the vast majority of
    /// the time, and the cost of being wrong is a slightly odd icon.
    /// </summary>
    private static IEnumerable<string> Candidates(GameEntry game)
    {
        if (game.IconSource is { } known) yield return known;

        foreach (var exe in RankedExecutables(game.InstallDir).Take(MaxAttempts))
            yield return exe;
    }

    /// <summary>
    /// Pull the icon out of an executable, or null if it genuinely has none.
    ///
    /// Not Icon.ExtractAssociatedIcon. That never reports failure: for a file with no icon
    /// resource it hands back the generic Windows application icon, so a caller cannot tell
    /// "here is the icon" from "there was no icon". Every game whose executable lacks an icon
    /// therefore got a blank-looking generic square and never reached the letter-tile fallback.
    ///
    /// ExtractIconEx answers honestly — it returns a count, and zero means zero.
    /// </summary>
    private static Bitmap? Extract(string exe)
    {
        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;

        try
        {
            // Asking for one of each: large is sharper when scaled down to the row height, but
            // some executables only ship a small one.
            if (ExtractIconEx(exe, 0, out large, out small, 1) <= 0) return null;

            var handle = large != IntPtr.Zero ? large : small;
            if (handle == IntPtr.Zero) return null;

            using var icon = Icon.FromHandle(handle);
            var bitmap = icon.ToBitmap();

            if (HasVisiblePixels(bitmap)) return bitmap;

            bitmap.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read an icon from {Path.GetFileName(exe)}: {ex.Message}");
            return null;
        }
        finally
        {
            if (large != IntPtr.Zero) DestroyIcon(large);
            if (small != IntPtr.Zero) DestroyIcon(small);
        }
    }

    /// <summary>
    /// Whether an icon actually shows anything.
    ///
    /// A few executables carry an icon resource that is entirely or almost entirely
    /// transparent, which draws as an empty gap in the list. Treating those as "no icon" sends
    /// them to the letter tile, which at least identifies the game.
    /// </summary>
    private static bool HasVisiblePixels(Bitmap bitmap)
    {
        try
        {
            int opaque = 0, sampled = 0;

            // Sampling every other pixel: plenty for this decision and a quarter of the work.
            for (int y = 0; y < bitmap.Height; y += 2)
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                sampled++;
                if (bitmap.GetPixel(x, y).A > 16) opaque++;
            }

            return sampled > 0 && opaque * 100 / sampled >= 5;
        }
        catch
        {
            return true;   // unreadable is not the same as empty; keep what we have
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index,
                                            out IntPtr large, out IntPtr small, int count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>How many executables to try before accepting that a game has no icon.</summary>
    private const int MaxAttempts = 12;

    /// <summary>Cap on how many executables to look at, so a huge install cannot stall a thread.</summary>
    private const int ScanLimit = 3000;

    private static List<string> RankedExecutables(string installDir)
    {
        if (!Directory.Exists(installDir)) return [];

        try
        {
            // Every level, not a fixed depth. Engines bury the real executable at varying
            // depths — Game\\Binaries\\Win64\\Game.exe on Unreal, deeper on some launchers —
            // and any fixed limit leaves whichever layout sits one level past it with no icon.
            // Shallow files come first, since the game itself is rarely buried deep.
            var files = WalkExecutables(installDir).Take(ScanLimit).ToList();
            if (files.Count == 0) return [];

            // Ranked, never filtered.
            //
            // The previous version discarded anything whose name contained a noise word, which
            // was two separate mistakes. Substrings match innocent titles — "eac" is inside
            // "Reach", "server" inside "Observer" — so real games lost their only executable.
            // And discarding everything can leave nothing at all. Scoring instead means a
            // suspicious name simply sorts last, and is still tried if nothing better exists.
            var folder = Path.GetFileName(installDir.TrimEnd('\\'));

            return files
                .OrderByDescending(f => Score(f, folder))
                .ThenByDescending(SafeLength)
                .ThenBy(f => f.Count(c => c == '\\'))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not scan {installDir} for an icon: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Every executable under a folder, breadth first, skipping anything unreadable.
    ///
    /// Written by hand rather than with SearchOption.AllDirectories because that throws on the
    /// first inaccessible subfolder and abandons the entire walk — one permission-denied
    /// directory would cost the icon for the whole game.
    /// </summary>
    private static IEnumerable<string> WalkExecutables(string root)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();

            foreach (var file in SafeFiles(dir)) yield return file;
            foreach (var child in SafeDirs(dir)) queue.Enqueue(child);
        }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return []; }
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch { return 0; }
    }

    /// <summary>
    /// Executables that are almost never the game itself.
    ///
    /// Matched as whole names or clear prefixes rather than loose substrings. The anti-cheat
    /// bootstrappers matter most: The Outlast Trials launches through start_protected_game.exe,
    /// which sits at the install root and is larger than the real binary, so every ranking
    /// signal pointed at it — and the list showed the Easy Anti-Cheat icon.
    /// </summary>
    private static readonly string[] Noise =
    [
        "start_protected_game", "easyanticheat", "eac_launcher", "eaanticheat",
        "easyanticheat_eos_setup", "eac_setup", "eosbootstrapper", "epicgameslauncher",
        "battleye", "beservice", "vgc", "vanguard",
        "unins000", "uninstall", "unitycrashhandler32", "unitycrashhandler64",
        "ueprereqsetup", "ue4prereqsetup", "vc_redist", "vcredist", "dxsetup", "dxwebsetup",
        "crashreportclient", "crashhandler", "crashpad_handler", "epicwebhelper",
        "steamwebhelper", "bootstrapper", "installer", "activationui", "cefprocess",
        "quickboot", "touchup", "prereq",
    ];

    /// <summary>
    /// Folders whose contents are never the game.
    ///
    /// Where an executable lives is a stronger signal than what it is called, and it
    /// generalises: an anti-cheat installer might be named anything, but it will be under an
    /// EasyAntiCheat or BattlEye folder, and every redistributable on Steam sits under
    /// _CommonRedist. Matching the directory catches the whole family at once instead of
    /// requiring a new filename in a blocklist each time one turns up.
    /// </summary>
    /// <summary>Paths that mean "compiled game output", whatever the engine calls them.</summary>
    private static readonly string[] EngineFolders =
    [
        @"\binaries\win64", @"\binaries\win32", @"\bin\win_x64", @"\bin\win64",
        @"\bin\x64", @"\win_x64\", @"\win64\", @"\game\bin",
    ];

    private static readonly string[] NoiseFolders =
    [
        "easyanticheat", "eac", "battleye", "battleye_", "anticheat", "vanguard",
        "_commonredist", "commonredist", "redist", "redistributable", "prerequisites",
        "prereq", "directx", "dotnet", "dotnetfx", "vcredist", "openal", "physx",
        "installer", "install", "setup", "support", "tools", "editor", "sdk",
        "crashreport", "crashhandler", "crashpad", "bugreport",
        "thirdparty", "extras", "bonus", "soundtrack", "manual", "docs",
        "dxsetup", "uninstall",
    ];

    /// <summary>
    /// How likely an executable is to be the game. Higher is better.
    ///
    /// Ranked rather than filtered, so even a suspicious executable is still tried if nothing
    /// better exists — a game whose only binary happens to look like noise still gets an icon.
    /// </summary>
    private static int Score(string file, string folder)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        int score = Similar(name, folder) * 40;

        // Strongest signal: a parent folder that exists to hold something other than the game.
        var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        for (int i = 0; i < parts.Length - 1; i++)
        {
            var segment = parts[i].Trim().ToLowerInvariant();

            foreach (var bad in NoiseFolders)
            {
                if (segment != bad && !segment.StartsWith(bad + " ") &&
                    !segment.StartsWith(bad + "_") && !segment.StartsWith(bad + "-")) continue;

                score -= 2000;
                i = parts.Length;   // one penalty is enough
                break;
            }
        }

        // Next strongest: a filename that names a known non-game program.
        foreach (var bad in Noise)
        {
            if (!name.StartsWith(bad, StringComparison.OrdinalIgnoreCase) &&
                !name.Equals(bad, StringComparison.OrdinalIgnoreCase)) continue;

            score -= 1000;
            break;
        }

        // Positive signals for things that look like a shipped game binary.
        if (name.EndsWith("-win64-shipping", StringComparison.OrdinalIgnoreCase)) score += 60;
        if (name.EndsWith("-shipping", StringComparison.OrdinalIgnoreCase)) score += 40;

        // Engine output folders. Unreal uses Binaries\Win64, CryEngine uses bin\win_x64,
        // Unity and others use variations — all of them mean "this is the compiled game",
        // and only checking for Binaries missed every engine that spells it differently.
        foreach (var dir in EngineFolders)
            if (file.Contains(dir, StringComparison.OrdinalIgnoreCase)) { score += 30; break; }

        // Size, as a coarse signal.
        //
        // A game's own executable is normally far larger than the stubs around it. This is what
        // separates a real binary buried in an engine folder from a small launcher sitting at
        // the install root — which is the case the ordering used to get wrong, because it
        // preferred shallower files and the stub is always shallower.
        long bytes = SafeLength(file);
        if (bytes > 100_000_000) score += 40;
        else if (bytes > 20_000_000) score += 25;
        else if (bytes > 2_000_000) score += 10;
        else if (bytes > 0 && bytes < 200_000) score -= 20;

        // Weak negatives, only ever tie-breakers.
        if (name.Contains("launcher", StringComparison.OrdinalIgnoreCase)) score -= 300;
        if (name.Contains("setup", StringComparison.OrdinalIgnoreCase)) score -= 300;
        if (name.Contains("redist", StringComparison.OrdinalIgnoreCase)) score -= 300;
        if (name.Contains("anticheat", StringComparison.OrdinalIgnoreCase)) score -= 800;
        if (name.Contains("crash", StringComparison.OrdinalIgnoreCase)) score -= 500;
        if (name.Contains("report", StringComparison.OrdinalIgnoreCase)) score -= 200;
        if (name.Contains("helper", StringComparison.OrdinalIgnoreCase)) score -= 200;
        if (name.Contains("service", StringComparison.OrdinalIgnoreCase)) score -= 200;
        if (name.Contains("update", StringComparison.OrdinalIgnoreCase)) score -= 200;

        return score;
    }




    /// <summary>Rough closeness of an executable name to its folder name.</summary>
    private static int Similar(string exe, string folder)
    {
        var a = new string(exe.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var b = new string(folder.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 3;
        if (b.StartsWith(a) || a.StartsWith(b)) return 2;
        return b.Contains(a) || a.Contains(b) ? 1 : 0;
    }

    public void Dispose()
    {
        foreach (var image in _cache.Values) image?.Dispose();
        _cache.Clear();
    }
}

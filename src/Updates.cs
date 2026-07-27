using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CouchMode;

/// <summary>What a check found.</summary>
/// <param name="Available">Whether a newer version exists.</param>
/// <param name="Version">The newest version, or the current one when nothing is newer.</param>
/// <param name="DownloadUrl">Where the new executable can be fetched from.</param>
/// <param name="Notes">The release notes, for showing what changed.</param>
/// <param name="Error">Why the check failed, or empty when it did not.</param>
public sealed record UpdateInfo(bool Available, string Version, string DownloadUrl,
                                string Notes, string Error = "");

/// <summary>
/// Checks GitHub Releases for a newer build, and installs it in place.
///
/// The install works by renaming rather than overwriting. Windows will not let a running
/// executable be replaced, but it will happily let one be *renamed* while it runs — so the
/// current file is moved aside, the new one takes its name, and the app restarts into it. The
/// leftover is deleted on the next launch, once nothing is holding it open.
///
/// That avoids the usual approach of shipping a separate updater helper, which is another
/// executable to sign, another thing to go wrong, and another thing for antivirus to dislike.
/// </summary>
public static class Updates
{
    /// <summary>Suffix for the outgoing build while the new one takes over.</summary>
    private const string OldSuffix = ".old";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    static Updates()
    {
        // GitHub rejects requests without one, and asks that it identify the caller.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"CouchSession/{AppInfo.Version}");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>
    /// Ask GitHub what the newest release is.
    ///
    /// Never throws: a failed check is a normal outcome — no network, a rate limit, a repo that
    /// has no releases yet — and none of those are worth interrupting anyone over.
    /// </summary>
    public static async Task<UpdateInfo> CheckAsync()
    {
        var current = Parse(AppInfo.Version);

        try
        {
            var url = $"https://api.github.com/repos/{AppInfo.Repository}/releases/latest";
            var release = await Http.GetFromJsonAsync<Release>(url).ConfigureAwait(false);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                return new(false, AppInfo.Version, "", "", "No releases have been published yet.");

            var latest = Parse(release.TagName);

            // The Windows executable, whatever it ends up being called. Matching on extension
            // rather than an exact name means renaming the artifact does not break updates.
            var asset = release.Assets?.FirstOrDefault(
                a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            if (latest <= current)
                return new(false, AppInfo.Version, "", release.Body ?? "");

            if (asset is null)
                return new(false, AppInfo.Version, "", "",
                           $"Version {release.TagName} exists but has no downloadable build.");

            Log.Info($"Update available: {release.TagName} (running {AppInfo.Version}).");
            return new(true, Clean(release.TagName), asset.DownloadUrl, release.Body ?? "");
        }
        // [BUG] A repository with no published releases is a 404 from this endpoint, not an empty
        // answer — so GetFromJsonAsync threw, the general handler below caught it, and the check
        // reported "Could not reach GitHub." for a machine whose network was working perfectly. The
        // release-is-null branch above was written for this case and could never once run.
        //
        // Drafts and prereleases do not count as "latest" either, and a repository that cannot be
        // read at all answers the same way. All three are the same fact from here — there is no
        // release to offer — so the message stops short of guessing which. The repository name goes
        // in the log, because a wrong one is the reason that leaves no other trace.
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            Log.Info($"Update check: {AppInfo.Repository} has no published release to offer (404).");
            return new(false, AppInfo.Version, "", "", "No releases have been published yet.");
        }
        catch (Exception ex)
        {
            Log.Info($"Update check failed: {ex.Message}");
            return new(false, AppInfo.Version, "", "", "Could not reach GitHub.");
        }
    }

    /// <summary>
    /// Download the new build and put it in place, then restart into it.
    ///
    /// Returns false and changes nothing if any step fails. The download lands beside the
    /// current executable rather than in the temp folder, so the final move is within one
    /// volume — a cross-volume move is a copy, which is slower and can half-finish.
    /// </summary>
    public static async Task<bool> InstallAsync(string downloadUrl, Action<string>? progress = null)
    {
        var current = Environment.ProcessPath;

        if (string.IsNullOrEmpty(current))
        {
            Log.Warn("Update: cannot work out where this program lives.");
            return false;
        }

        var folder = Path.GetDirectoryName(current)!;
        var incoming = Path.Combine(folder, "CouchSession.new.exe");
        var outgoing = current + OldSuffix;

        try
        {
            progress?.Invoke("Downloading…");

            var bytes = await Http.GetByteArrayAsync(downloadUrl).ConfigureAwait(false);

            // A truncated download must never be installed. Anything this small is a GitHub
            // error page rather than a build.
            if (bytes.Length < 1_000_000)
            {
                Log.Warn($"Update: the download was only {bytes.Length} bytes; ignoring it.");
                return false;
            }

            await File.WriteAllBytesAsync(incoming, bytes).ConfigureAwait(false);

            progress?.Invoke("Installing…");

            // Clear any leftover from a previous update before reusing the name.
            if (File.Exists(outgoing)) File.Delete(outgoing);

            File.Move(current, outgoing);

            try
            {
                File.Move(incoming, current);
            }
            catch
            {
                // Put the old one back rather than leaving the app with no executable at all.
                File.Move(outgoing, current);
                throw;
            }

            Log.Info("Update installed; restarting.");

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = current,
                UseShellExecute = true,
            });

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);

            try { if (File.Exists(incoming)) File.Delete(incoming); } catch { }
            return false;
        }
    }

    /// <summary>
    /// Remove the previous build, once it is no longer running.
    ///
    /// Called at startup: by then the old executable has exited and released its lock. A
    /// failure here means it is still going, and the next launch will get it instead.
    /// </summary>
    public static void CleanUp()
    {
        try
        {
            var current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current)) return;

            var outgoing = current + OldSuffix;
            if (!File.Exists(outgoing)) return;

            File.Delete(outgoing);
            Log.Info("Removed the previous version.");
        }
        catch (Exception ex)
        {
            Log.Info($"Previous version still in use, will remove it later: {ex.Message}");
        }
    }

    /// <summary>
    /// Compare versions numerically, not as text.
    ///
    /// "1.10.0" is newer than "1.9.0" and a string comparison says otherwise, which would tell
    /// everyone on the newest build that an older one is available.
    /// </summary>
    private static Version Parse(string text)
    {
        var cleaned = Clean(text);

        return Version.TryParse(cleaned, out var version) ? version : new Version(0, 0, 0);
    }

    /// <summary>Tags are usually written "v1.2.3"; the leading letter is not part of the number.</summary>
    private static string Clean(string tag) => tag.TrimStart('v', 'V').Trim();

    private sealed class Release
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("assets")] public List<Asset>? Assets { get; set; }
    }

    private sealed class Asset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    }
}

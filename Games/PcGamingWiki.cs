using System.Text.RegularExpressions;

namespace CouchMode.Games;

/// <summary>
/// HDR support read from a game's PCGamingWiki page.
///
/// A third source behind Steam, for the gap Steam leaves: publishers who never set the "HDR
/// available" capability, and games from Epic, GOG or elsewhere that have no Steam entry at all.
/// Red Dead Redemption 2 and The Outlast Trials both fall in that gap.
///
/// This reads the rendered page rather than the wiki's API. The cargo endpoint could not be
/// reached during development, and the API's shape could not be confirmed, whereas an ordinary
/// article URL is plain and stable. The cost is that the answer lives in an image's alt text in
/// a settings table, so this is HTML scraping and will break if the wiki restyles that table.
///
/// Written to fail quietly and loudly at once: a page it cannot understand changes nothing, and
/// says so in the log with enough of the markup to fix the parser.
/// </summary>
internal static class PcGamingWiki
{
    /// <summary>Whether to consult the wiki at all. Off disables it without removing it.</summary>
    public static bool Enabled { get; set; } = true;

    private const string Agent = "CouchSession (+https://couchsession.local) HDR lookup";

    /// <summary>
    /// Look a game up. Null means no usable answer — not "no HDR".
    /// </summary>
    public static async Task<HdrSupport?> LookupAsync(HttpClient http, string gameName,
                                                      bool logMarkup)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(gameName)) return null;

        foreach (var title in Candidates(gameName))
        {
            var url = "https://www.pcgamingwiki.com/wiki/" + Uri.EscapeDataString(title);

            string html;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(Agent);

                var response = await http.SendAsync(request).ConfigureAwait(false);

                // A missing page is a normal outcome — most games are not titled exactly as the
                // wiki files them — so try the next spelling rather than treating it as failure.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
                if (!response.IsSuccessStatusCode) return null;

                html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"PCGamingWiki lookup for '{title}' failed: {ex.Message}");
                return null;
            }

            var verdict = Parse(html, title, logMarkup);
            if (verdict is not null) return verdict;
        }

        return null;
    }

    /// <summary>
    /// Page titles worth trying, most likely first.
    ///
    /// The wiki uses underscores and keeps punctuation, so the store's title usually works once
    /// spaces are converted. Edition suffixes are the common reason it does not.
    /// </summary>
    private static IEnumerable<string> Candidates(string name)
    {
        // Runs of whitespace collapse to one before underscores are applied. Stripping the
        // trademark symbol leaves a gap behind it, and "Battlefield__6" matches no page.
        var cleaned = Regex.Replace(name.Replace('™', ' ').Replace('®', ' ').Replace('©', ' '),
                                    @"\s+", " ").Trim();

        yield return cleaned.Replace(' ', '_');

        // Without an edition suffix.
        var trimmed = Regex.Replace(cleaned,
            @"\s*[:\-–]?\s*(Game of the Year|GOTY|Definitive|Complete|Ultimate|Deluxe|Enhanced|"
          + @"Special|Gold|Anniversary|Standard|Digital|Remastered)\s*(Edition)?$",
            "", RegexOptions.IgnoreCase).Trim();

        if (trimmed.Length > 0 && trimmed != cleaned)
            yield return trimmed.Replace(' ', '_');

        // The subtitle is deliberately never dropped.
        //
        // Falling back to the part before the colon looks harmless and is not: a spin-off would
        // silently fetch its parent's page and inherit an HDR answer that was never about it.
        // A missing page is a much better outcome than a confidently wrong one.
    }

    /// <summary>
    /// Find the HDR row in the graphics settings table and read its state.
    ///
    /// The table renders each setting as a row whose first cell names it and whose second holds
    /// a state icon. The icon's alt text is the machine-readable part — "true", "false",
    /// "hackable", "limited" — so the parse looks for the HDR row and then the first alt text
    /// after it, within a bounded window so a match cannot run into the next row.
    /// </summary>
    private static HdrSupport? Parse(string html, string title, bool logMarkup)
    {
        const string Marker = "High dynamic range display (HDR)";

        int at = html.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            // Not every page documents HDR. That is silence, not a negative answer.
            return null;
        }

        // Bounded so a row with no icon cannot borrow the next row's.
        int end = Math.Min(html.Length, at + 1200);
        var window = html[at..end];

        var state = Regex.Match(window, @"alt=""(true|false|hackable|limited|unknown|n/a)""",
                                RegexOptions.IgnoreCase);

        if (!state.Success)
        {
            if (logMarkup)
            {
                var sample = window.Length > 400 ? window[..400] : window;
                Log.Warn($"PCGamingWiki HDR row for '{title}' was not understood. Markup: {sample}");
            }
            return null;
        }

        return Interpret(state.Groups[1].Value);
    }

    /// <summary>
    /// Map the wiki's state onto ours.
    ///
    /// Only an unambiguous "true" counts as native. "Hackable" and "limited" mean HDR is
    /// reachable through a mod or a workaround, which is not the same claim and would make the
    /// badge misleading, so those are left unknown rather than guessed at.
    /// </summary>
    private static HdrSupport? Interpret(string raw) => raw.ToLowerInvariant() switch
    {
        "true" => HdrSupport.Native,
        "false" => HdrSupport.None,
        _ => null,
    };
}

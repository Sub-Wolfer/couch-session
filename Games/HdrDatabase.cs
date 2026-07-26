using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CouchMode.Games;

/// <summary>Whether a game renders in HDR itself, rather than relying on Windows Auto HDR.</summary>
public enum HdrSupport
{
    /// <summary>Nothing known about this game. Shown as no badge at all.</summary>
    Unknown = 0,

    /// <summary>The game has its own HDR renderer and output.</summary>
    Native = 1,

    /// <summary>The game is SDR only. Windows Auto HDR may still apply to it.</summary>
    None = 2,
}

/// <summary>
/// Which games support HDR natively.
///
/// Advisory only: Couch Mode turns HDR on for the display either way. The tag answers a
/// different question — whether the game will actually drive it, or whether you are relying on
/// Windows Auto HDR to approximate it.
///
/// Three sources, none of them complete on its own:
///
///   - Steam's store data. Exact and current, but only covers Steam games, and only those whose
///     publisher set the capability flag.
///   - PCGamingWiki, scraped per game. Covers everything else, including other stores, at the
///     cost of being HTML scraping.
///   - A compiled-in list, so something sensible shows offline and on first run.
///
/// A positive from any of them wins. A lookup that fails changes nothing.
/// </summary>
internal static class HdrDatabase
{
    /// <summary>Raised when a refresh brings in new data, so the list can repaint.</summary>
    public static event Action? Updated;

    private static readonly ConcurrentDictionary<string, HdrSupport> Entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Verdict by Steam app id.
    ///
    /// The only exact join available. Title matching is guesswork by comparison — it was why
    /// plenty of installed games went untagged even though the wiki had an entry for them.
    /// </summary>
    private static readonly ConcurrentDictionary<int, HdrSupport> ByAppId = new();

    /// <summary>
    /// App ids already asked about, so a game Steam has no answer for is not re-queried every
    /// launch. Stored as a map only because ConcurrentDictionary is the concurrent set here.
    /// </summary>
    private static readonly ConcurrentDictionary<int, byte> Tried = new();

    /// <summary>
    /// Verdicts looked up on the wiki, keyed by normalised title.
    ///
    /// Kept apart from the built-in list so the cache can record what was fetched rather than
    /// re-saving the compiled-in seed on every write.
    /// </summary>
    private static readonly ConcurrentDictionary<string, HdrSupport> FromWiki = new(StringComparer.Ordinal);

    /// <summary>Titles already looked up on the wiki, so a miss is not retried every launch.</summary>
    private static readonly ConcurrentDictionary<string, byte> AskedWiki = new(StringComparer.Ordinal);

    /// <summary>Memoised name-to-verdict, since the list asks on every repaint of every row.</summary>
    private static readonly ConcurrentDictionary<string, HdrSupport> Answers = new(StringComparer.OrdinalIgnoreCase);

    private static string CachePath => Path.Combine(AppConfig.Directory, "hdr-support.json");

    static HdrDatabase()
    {
        foreach (var name in SeedNative) Entries[Normalize(name)] = HdrSupport.Native;
        foreach (var name in SeedConfirmed) Entries[Normalize(name)] = HdrSupport.Native;

        LoadCache();
    }

    /// <summary>
    /// The verdict for a game, from the most trustworthy source that has an opinion.
    ///
    /// The ordering is not about precision. It is about what each source's silence means, and
    /// how far its claims can be trusted:
    ///
    ///   1. Steam saying yes. The publisher declared the capability, so it settles the question.
    ///   2. PCGamingWiki, either way. It documents HDR explicitly per game, so both its yes and
    ///      its no are real answers rather than an absence of data.
    ///   3. The built-in list. Useful offline and for non-Steam libraries, but compiled from
    ///      recall and known to contain mistakes, so anything above may correct it.
    ///   4. Steam saying nothing. The weakest signal there is — plenty of HDR games never had
    ///      the flag set — so it only applies when nothing else has an opinion.
    ///
    /// Two real cases pin this ordering down, and Steam alone cannot tell them apart:
    ///
    ///   - Red Dead Redemption 2 has HDR and carries no Steam flag. Reading that silence as a
    ///     "no" would be wrong, which is why 4 sits below 3.
    ///   - Darktide has no HDR and the built-in list wrongly claimed it did. Letting any
    ///     positive win outright made that mistake permanent and impossible to correct from a
    ///     better source, which is why 3 sits below 2.
    /// </summary>
    public static HdrSupport Lookup(GameEntry game)
    {
        var steam = game.SteamAppId is { } appId && ByAppId.TryGetValue(appId, out var byId)
                  ? byId : HdrSupport.Unknown;

        if (steam == HdrSupport.Native) return HdrSupport.Native;

        if (FromWiki.TryGetValue(Normalize(game.Name), out var wiki) && wiki != HdrSupport.Unknown)
            return wiki;

        var seed = Lookup(game.Name);
        if (seed != HdrSupport.Unknown) return seed;

        return steam;
    }

    public static HdrSupport Lookup(string gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return HdrSupport.Unknown;

        // Exact match only.
        //
        // This used to fall back to the title with its subtitle removed, on the theory that
        // "Some Game: Subtitle" and "Some Game" are the same thing. They frequently are not.
        // "Deep Rock Galactic: Survivor" is a different game by a different studio to "Deep
        // Rock Galactic", and it was inheriting the parent's native HDR tag. The same trap sits
        // under Halo, Batman, Warhammer and every other title with spin-offs.
        //
        // Nothing is lost by dropping it: genuine edition suffixes — Definitive, GOTY, Deluxe —
        // are already stripped by Normalize, which is the case the fallback was meant to cover.
        return Answers.GetOrAdd(gameName,
            name => Entries.TryGetValue(Normalize(name), out var exact) ? exact : HdrSupport.Unknown);
    }


    // ================= name matching =================

    /// <summary>
    /// Reduce a title to something two sources can agree on.
    ///
    /// This is the part that actually decides whether the feature works. Steam says
    /// "The Witcher 3: Wild Hunt", GOG says "The Witcher 3 Wild Hunt - Game of the Year
    /// Edition", and the wiki has its own page title — none match as plain strings.
    /// </summary>
    private static string Normalize(string name)
    {
        var text = name.ToLowerInvariant();

        // Drop anything parenthesised: platform notes, years, publisher tags.
        int open = text.IndexOf('(');
        if (open > 0) text = text[..open];

        text = text.Replace('&', ' ').Replace(" and ", " ");

        foreach (var noise in EditionNoise)
            text = text.Replace(noise, " ", StringComparison.Ordinal);

        if (text.StartsWith("the ", StringComparison.Ordinal)) text = text[4..];

        return new string(text.Where(char.IsLetterOrDigit).ToArray());
    }

    private static readonly string[] EditionNoise =
    [
        "game of the year edition", "game of the year", "goty edition", " goty",
        "definitive edition", "complete edition", "ultimate edition", "deluxe edition",
        "enhanced edition", "special edition", "gold edition", "anniversary edition",
        "director's cut", "directors cut", "remastered", "remaster", "next-gen",
        "standard edition", "digital edition", "legacy of thieves collection",
        "™", "®", "©",
    ];

    // ================= disk cache =================

    private static void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;

            var cache = JsonSerializer.Deserialize(File.ReadAllText(CachePath), HdrJson.Default.HdrCache);
            if (cache is null) return;

            // Entries is deliberately not read.
            //
            // Earlier builds saved the compiled-in list into the cache and loaded it back on
            // start, which meant a title could never be corrected: removing Darktide from the
            // source changed nothing, because the old claim had already been written to disk and
            // was reloaded over the top of the new list every launch. The cache is now only for
            // things actually fetched — the built-in list lives in the build, and nowhere else.
            foreach (var (key, value) in cache.AppIds)
                if (int.TryParse(key, out var id)) ByAppId[id] = (HdrSupport)value;

            // Only remember a game as asked if an answer came back with it.
            //
            // Older builds recorded the question without the answer, so any id listed as asked
            // but missing from the verdicts is a casualty of that and is forgotten here, which
            // lets it be queried again rather than staying permanently blank.
            int poisoned = 0;

            foreach (var key in cache.Asked)
            {
                if (!int.TryParse(key, out var id)) continue;

                if (ByAppId.ContainsKey(id)) Tried[id] = 1;
                else poisoned++;
            }

            if (poisoned > 0)
                Log.Info($"Discarded {poisoned} incomplete HDR cache entries; they will be re-checked.");

            foreach (var (key, value) in cache.Wiki) FromWiki[key] = (HdrSupport)value;
            foreach (var key in cache.AskedWiki) AskedWiki[key] = 1;

            Log.Info($"Loaded {cache.AppIds.Count} Steam and {cache.Wiki.Count} wiki HDR results from cache.");
        }
        catch (Exception ex)
        {
            // A bad cache is not worth failing over — the seed list still works.
            Log.Warn($"Could not read the HDR cache: {ex.Message}");
        }
    }

    private static void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Directory);

            var cache = new HdrCache
            {
                UpdatedUtc = DateTime.UtcNow,
                AppIds = ByAppId.ToDictionary(e => e.Key.ToString(), e => (int)e.Value),
                Asked = Tried.Keys.Select(k => k.ToString()).ToList(),
                Wiki = FromWiki.ToDictionary(e => e.Key, e => (int)e.Value),
                AskedWiki = AskedWiki.Keys.ToList(),
            };

            var temp = CachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cache, HdrJson.Default.HdrCache));
            File.Move(temp, CachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write the HDR cache: {ex.Message}");
        }
    }

    // ================= Steam store =================

    /// <summary>
    /// Ask Steam whether each installed game advertises HDR.
    ///
    /// Steam's own store data is the source, via the "HDR available" capability (category 61)
    /// that publishers set on their store page. This replaced a PCGamingWiki lookup, and is
    /// better on every axis that matters here:
    ///
    ///   - It is keyed by app id, so there is no title matching to get wrong.
    ///   - It is current. A game released last week is in it; a list compiled by hand is not.
    ///   - It is the publisher's own declaration rather than a community edit.
    ///
    /// Only games actually installed are looked up, and every answer is cached permanently, so
    /// this settles down to nothing after the first run.
    /// </summary>
    public static void RefreshInBackground(IEnumerable<GameEntry> games)
    {
        var all = games.ToList();

        var pendingSteam = all
            .Select(g => g.SteamAppId)
            .OfType<int>()
            .Distinct()
            .Where(id => !ByAppId.ContainsKey(id) && !Tried.ContainsKey(id))
            .ToList();

        // Only games still without a positive answer are worth asking the wiki about.
        var pendingWiki = all
            .Where(g => !AskedWiki.ContainsKey(Normalize(g.Name)))
            .Where(g => Lookup(g) != HdrSupport.Native)
            .Select(g => g.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pendingSteam.Count == 0 && pendingWiki.Count == 0) return;

        Task.Run(async () =>
        {
            try
            {
                if (pendingSteam.Count > 0)
                {
                    int found = await FetchAsync(pendingSteam).ConfigureAwait(false);
                    Log.Info($"Steam HDR lookup finished: {found} of {pendingSteam.Count} advertise HDR.");

                    SaveCache();
                    Answers.Clear();
                    Updated?.Invoke();
                }

                // Second pass, for everything Steam could not answer.
                if (pendingWiki.Count > 0)
                {
                    int found = await FetchFromWikiAsync(pendingWiki).ConfigureAwait(false);
                    Log.Info($"PCGamingWiki lookup finished: {found} of {pendingWiki.Count} report HDR.");

                    SaveCache();
                    Answers.Clear();
                    Updated?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"HDR lookup stopped: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Ask the wiki about each title in turn.
    ///
    /// Slower and gentler than the Steam pass: this is someone's community wiki being scraped,
    /// not an API meant for clients, so it goes one at a time with a real gap between requests
    /// and stops at the first sign it is unwelcome.
    /// </summary>
    private static async Task<int> FetchFromWikiAsync(List<string> names)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        int found = 0;
        bool logMarkup = true;   // only the first unparsed page is worth logging in full

        foreach (var name in names)
        {
            HdrSupport? verdict;
            try
            {
                verdict = await PcGamingWiki.LookupAsync(http, name, logMarkup).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"PCGamingWiki lookup stopped at '{name}': {ex.Message}");
                break;
            }

            if (verdict is { } known)
            {
                FromWiki[Normalize(name)] = known;
                if (known == HdrSupport.Native) found++;
                logMarkup = false;
            }

            AskedWiki[Normalize(name)] = 1;

            await Task.Delay(WikiSpacing).ConfigureAwait(false);
        }

        return found;
    }

    /// <summary>A deliberately unhurried gap between wiki requests.</summary>
    private static readonly TimeSpan WikiSpacing = TimeSpan.FromMilliseconds(1500);

    /// <summary>Steam's capability id for "HDR available".</summary>
    private const int HdrCategoryId = 61;

    /// <summary>
    /// Spacing between requests.
    ///
    /// The store API allows roughly 200 requests per five minutes. This stays comfortably under
    /// that; the work is invisible anyway, and every result is kept for good.
    /// </summary>
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(900);

    /// <summary>
    /// Consecutive unusable replies before the run is abandoned.
    ///
    /// One is ordinary — a delisted or region-locked game. Several in a row means Steam has
    /// started refusing, and continuing would only burn requests.
    /// </summary>
    private const int UnavailableLimit = 3;

    private static async Task<int> FetchAsync(List<int> appIds)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"CouchSession/{AppInfo.Version}");

        int found = 0;
        int unavailable = 0;

        foreach (var appId in appIds)
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=categories";

            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Network trouble is not the game's fault — leave it untried so it is retried.
                Log.Warn($"HDR lookup for {appId} failed: {ex.Message}");
                break;
            }

            if ((int)response.StatusCode == 429)
            {
                // Rate limited. Stop rather than hammer; the rest resume on the next launch.
                Log.Info("Steam rate-limited the HDR lookup; the rest will be checked later.");
                break;
            }

            var answer = Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false), appId);

            if (answer == SteamAnswer.NoData)
            {
                // Answered, but with nothing to record. Remember it so it is not asked again,
                // and treat the connection as healthy.
                unavailable = 0;
                Tried[appId] = 1;

                await Task.Delay(Spacing).ConfigureAwait(false);
                continue;
            }

            if (answer == SteamAnswer.Unavailable)
            {
                // Do not record this game as asked.
                //
                // It used to be recorded regardless, which quietly poisoned the cache: Steam's
                // soft rate limit replies HTTP 200 with success:false, so once throttled during
                // a run every remaining game was marked "already asked" while holding no answer,
                // and was never queried again. Path of Exile 2 advertises HDR on Steam and was
                // going untagged for exactly this reason.
                unavailable++;

                if (unavailable >= UnavailableLimit)
                {
                    Log.Info($"Steam stopped answering after {found} results; the rest will be "
                           + "checked on a later run.");
                    break;
                }

                await Task.Delay(Spacing).ConfigureAwait(false);
                continue;
            }

            // A run of failures means throttling; a single one is just that game.
            unavailable = 0;

            ByAppId[appId] = answer == SteamAnswer.Native ? HdrSupport.Native : HdrSupport.None;
            if (answer == SteamAnswer.Native) found++;

            Tried[appId] = 1;

            await Task.Delay(Spacing).ConfigureAwait(false);
        }

        return found;
    }

    /// <summary>What Steam actually told us about one game.</summary>
    private enum SteamAnswer
    {
        /// <summary>The capability list contained "HDR available".</summary>
        Native,

        /// <summary>The capability list came back complete and did not contain it.</summary>
        NoHdr,

        /// <summary>No usable reply. Says nothing about the game — only about the request.</summary>
        Unavailable,

        /// <summary>
        /// Steam answered about this app but the payload holds no capability list.
        ///
        /// Distinct from Unavailable: it is a fact about this one entry, not a sign that Steam
        /// has stopped talking to us, so it must neither be retried nor counted as a refusal.
        /// </summary>
        NoData,
    }

    /// <summary>
    /// Read the capability list.
    ///
    /// The distinction that matters is between a complete answer that omits HDR — a real "no",
    /// because Steam lists every capability a game has — and no answer at all. Conflating them
    /// is what caused games to be permanently mis-tagged: see FetchAsync.
    /// </summary>
    private static SteamAnswer Parse(string json, int appId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty(appId.ToString(), out var entry))
                return SteamAnswer.Unavailable;

            // Steam's soft rate limit answers HTTP 200 with success:false, so this is usually
            // throttling rather than anything to do with the game.
            if (!entry.TryGetProperty("success", out var ok) || !ok.GetBoolean())
                return SteamAnswer.Unavailable;

            if (!entry.TryGetProperty("data", out var data)) return SteamAnswer.NoData;

            // "data" is not always an object. Some entries — app 227260 among them — return an
            // empty array, and reading a property off that throws. The exception was caught and
            // reported as Unavailable, so three such apps in a row looked exactly like Steam
            // refusing and aborted the entire run at zero results.
            if (data.ValueKind != JsonValueKind.Object) return SteamAnswer.NoData;

            // No categories at all means a stub, not a game without capabilities.
            if (!data.TryGetProperty("categories", out var categories) ||
                categories.ValueKind != JsonValueKind.Array) return SteamAnswer.NoData;

            foreach (var category in categories.EnumerateArray())
                if (category.TryGetProperty("id", out var id) && id.GetInt32() == HdrCategoryId)
                    return SteamAnswer.Native;

            return SteamAnswer.NoHdr;
        }
        catch (Exception ex)
        {
            // A shape we cannot read is this entry's problem, not the connection's.
            Log.Warn($"Could not read Steam's reply for {appId}: {ex.Message}");
            return SteamAnswer.NoData;
        }
    }

    // ================= seed =================
    //
    // Snapshot of PCGamingWiki's "games that support HDR" list, taken 2026-07-20.
    //
    // This replaced a list written from recall, which was the source of the Darktide mistake:
    // it claimed native HDR for a game that has none, and no amount of precedence logic can
    // rescue a wrong claim in the lowest-authority source. This is transcribed data instead.
    //
    // Two limits remain, both structural rather than accidental. It is a snapshot, so nothing
    // released after that date is in it — Steam's live lookup covers new games. And the source
    // table renders at most 500 of its 611 rows, alphabetically, so titles after "Still Wakes
    // the Deep" are absent and will read as unknown until the rest is captured.
    //
    // Anything wrong here is still correctable: right-click a game to set its tag yourself.

    /// <summary>
    /// Games PCGamingWiki records as outputting HDR themselves.
    ///
    /// Transcribed from the wiki's generated list rather than written from memory. Three of its
    /// five ratings are included, because all three describe a game that drives HDR itself:
    ///
    ///   - Native support: an HDR option that works.
    ///   - Limited native support: native output with caveats. Red Dead Redemption 2 and
    ///     Cyberpunk 2077 sit here, so excluding it would drop two obvious HDR titles.
    ///   - Always on: HDR output with no in-game toggle.
    ///
    /// "Requires manual fix" is excluded: those need a mod or a config edit, and badging them
    /// would promise something the game does not offer on its own.
    /// </summary>
    /// <summary>
    /// Games confirmed by hand, kept separate from the transcribed list.
    ///
    /// Only for titles the wiki data does not currently reach. The saved list renders 500 of its
    /// 611 rows and stops alphabetically at "Still Wakes the Deep", so nothing from T to Z is in
    /// it — The Outlast Trials among them, which is why it lost its badge when the recalled list
    /// was replaced wholesale.
    ///
    /// Deliberately its own list so provenance stays obvious: everything in SeedNative came from
    /// the wiki verbatim, and everything here did not. Once the remaining rows are captured this
    /// should be empty again.
    /// </summary>
    private static readonly string[] SeedConfirmed =
    [
        "The Outlast Trials",
    ];

    private static readonly string[] SeedNative =
    [
        "007 First Light", "A Plague Tale: Requiem", "Abriss", "Ace Combat 8: Wings of Theve",
        "Ad Infinitum", "Age of Empires IV", "Agent Intercept", "Agents of Mayhem",
        "Agni: Village of Calamity", "Alan Wake II", "Alan Wake Remastered",
        "Alien Weapon Test Grounds", "Alien: Rogue Incursion Evolved Edition",
        "Alone in the Dark (2024)", "American Truck Simulator", "Anno 117: Pax Romana",
        "Anthem", "Arcade Paradise", "Arcadegeddon", "Arctic Awakening",
        "ARK: Survival Ascended", "Armored Core VI: Fires of Rubicon", "As Dusk Falls",
        "Assassin's Creed III Remastered", "Assassin's Creed Mirage",
        "Assassin's Creed Odyssey", "Assassin's Creed Origins", "Assassin's Creed Shadows",
        "Assassin's Creed Valhalla", "Assassin's Creed: Black Flag Resynced",
        "Assassin's Creed: Liberation HD", "Assetto Corsa Competizione",
        "Atelier Resleriana: The Red Alchemist & the White Guardian", "Atomfall",
        "Atomic Heart 2", "Avatar: Frontiers of Pandora", "Avowed", "Awaria", "Back 4 Blood",
        "Baldur's Gate 3", "Barony", "Battlefield 1", "Battlefield 2042", "Battlefield 6",
        "Battlefield V", "Black Widow: Recharged", "Blades of Fire", "Blair Witch",
        "Bleak Haven", "Bloodlines of Prima", "Borderlands 3", "Borderlands 4", "Brick Rigs",
        "Brickadia", "Brothers: A Tale of Two Sons Remake", "Building Relationships",
        "Bus Simulator 21", "Bzzzt", "Call of Duty: Black Ops 6", "Call of Duty: Black Ops 7",
        "Call of Duty: Black Ops Cold War", "Call of Duty: Black Ops IIII",
        "Call of Duty: Modern Warfare", "Call of Duty: Modern Warfare II",
        "Call of Duty: Modern Warfare III", "Call of Duty: Vanguard",
        "Call of Duty: Warzone 2.0", "Call of Duty: WWII", "Captain Blood (Seawolf Studio)",
        "Captain Gazman: Day of the Rage", "Catapult Quest", "Celestial Command",
        "Chasmal fear", "Chernobyl: Terrorist Attack", "Chernobylite", "Chess Ultra",
        "Chinese Paladin: Sword and Fairy 6", "Chrono Odyssey", "Comedy Night",
        "Contra: Rogue Corps", "ContractVille", "Control", "Crackdown 3", "Crimson Desert",
        "Cronos: The New Dawn", "Crush Zone: Demolition Derby", "Crysis Remastered",
        "Cthulhu: The Cosmic Abyss", "CUBE", "Cyberpunk 2077", "CyberTaxi", "Dark and Light",
        "Dawn Break -Origin-", "Days Gone", "Dead Format", "Dead Island 2",
        "Dead Rising Deluxe Remaster", "Dead Space (2023)", "Death Stranding",
        "Death Stranding 2: On the Beach", "Death Stranding Director's Cut", "Deathloop",
        "Deep Rock Galactic", "Deep Rock Galactic: Rogue Core", "Deliver Us Mars", "Desordre",
        "Destiny 2", "Detroit: Become Human", "Devil May Cry 5", "Diablo II: Resurrected",
        "Diablo IV", "Dinkum", "Directive 8020", "DIRT 5", "Disco Elysium", "Disney Speedstorm",
        "Disorder (2025)", "Distant Worlds 2", "Divinity: Original Sin II",
        "Divinity: Original Sin II - Definitive Edition", "Doom Eternal", "Doom: The Dark Ages",
        "Dragon Age: The Veilguard", "Dragon Ball: Sparking! Zero", "Dragon Quest Builders",
        "Dragon's Dogma II", "Dream Engines: Nomad Cities", "Dual Universe", "Duskfade",
        "Dying Light: The Beast", "Dynasty Warriors: Origins", "EA Sports College Football 27",
        "EA Sports FC 24", "EA Sports FC 25", "EA Sports FC 26", "EA Sports PGA Tour",
        "EA Sports WRC", "Echoes of the End", "Echoes of the Living", "Elden Ring",
        "Elden Ring Nightreign", "Elemental War", "Elin", "EreaDrone Simulator",
        "Euro Truck Simulator 2", "Everspace 2", "Everybody's Golf: Hot Shots",
        "Evil Genius 2: World Domination", "Evotinction", "Exodus (2027)", "Exoprimal",
        "F1 2017", "F1 2018", "F1 2019", "F1 2020", "F1 2021", "F1 22", "F1 23", "F1 24",
        "F1 25", "Factory Coin Mining", "Fantasy Life i: The Girl Who Steals Time", "Far Cry 5",
        "Far Cry 6", "Far Cry New Dawn", "Far Far West",
        "Fatal Frame II: Crimson Butterfly Remake", "FBC: Firebreak", "Fear the Dark Unknown",
        "Felt That: Boxing", "FIFA 23", "Final Fantasy Tactics - The Ivalice Chronicles",
        "Final Fantasy VII Rebirth", "Final Fantasy VII Remake Intergrade", "Final Fantasy XV",
        "Final Fantasy XVI", "Firmament", "Fishing: North Atlantic", "Football Manager 2020",
        "Football Manager 2020 Touch", "For Honor", "Forspoken", "Forza Horizon 4",
        "Forza Horizon 5", "Forza Horizon 6", "Forza Motorsport", "Forza Motorsport 7",
        "Fuck Putin", "Fuser", "Garten of Banban 7", "Gates of Hell", "Gears of War: E-Day",
        "Gears of War: Reloaded", "Gears Tactics", "Georgie-Yolkie",
        "Ghost of Tsushima Director's Cut", "Ghostbusters: Spirits Unleashed",
        "Ghostwire: Tokyo", "God of War", "God of War Ragnarök", "Godfall",
        "Gori: Cuddly Carnage", "Gotham Knights", "Gothic 1 Remake",
        "Grand Theft Auto: The Trilogy – The Definitive Edition", "Gravel", "GRID (2019)",
        "GRID Legends", "Gylt", "Halo Infinite", "Halo Wars 2", "Harold Halibut", "Hedon",
        "Hell is Us", "Hellblade II: Senua's Saga", "Hellblade: Senua's Sacrifice",
        "Helldivers 2", "Hellmart", "Hello Neighbor 2", "High on Life 2", "Hitman", "Hitman 2",
        "Hitman: World of Assassination", "Hogwarts Legacy", "Homeworld 3",
        "Horizon Forbidden West", "Horizon Zero Dawn", "Horizon Zero Dawn Remastered",
        "Hot Wheels Unleashed", "House of Ashes", "Hunt: Showdown 1896", "I Hate This Place",
        "Ikuma The Frozen Compass", "Ill", "Immortals Fenyx Rising", "Immortals of Aveum",
        "Inazuma Eleven: Victory Road", "Indiana Jones and the Great Circle", "InFlux Redux",
        "Injustice 2", "Inside the Backrooms", "IRacing", "Ironsight", "Jump Force",
        "Jurassic World Evolution 3", "Jusant", "Justice Online", "Karma: The Dark World",
        "Keeper", "Killing Floor 3", "Kingdom Come: Deliverance II", "Kingdom Hearts III",
        "Klaus Veen's Treason", "Knives Out", "Kunitsu-Gami: Path of the Goddess",
        "Layers of Fear (2023)", "Lego Batman: Legacy of the Dark Knight",
        "Lego Horizon Adventures", "Lego Star Wars: The Skywalker Saga", "Lies of P",
        "Life Is Strange: Double Exposure", "Life Is Strange: Reunion",
        "Life Is Strange: True Colors", "Like a Dragon Gaiden: The Man Who Erased His Name",
        "Like a Dragon: Infinite Wealth", "Like a Dragon: Pirate Yakuza in Hawaii",
        "Liminal Point", "Little Hope", "Lords of the Fallen (2023)", "Lords of the Fallen II",
        "Lost Ember: Rekindled Edition", "Lost Records: Bloom & Rage", "Lost Soul Aside",
        "Madden NFL 19", "Madden NFL 20", "Madden NFL 21", "Madden NFL 22", "Madden NFL 23",
        "Madden NFL 24", "Madden NFL 25", "Madden NFL 26", "Mafia: The Old Country",
        "Man of Medan", "Marathon (2026)", "Marvel's Avengers",
        "Marvel's Guardians of the Galaxy", "Marvel's Midnight Suns", "Marvel's Spider-Man 2",
        "Marvel's Spider-Man Remastered", "Marvel's Spider-Man: Miles Morales",
        "Mass Effect Legendary Edition", "Mass Effect: Andromeda", "Megaton Musashi: Wired",
        "Metal Gear Solid Δ: Snake Eater", "Metro Exodus", "Metro Exodus Enhanced Edition",
        "Microsoft Flight Simulator (2020)", "Microsoft Flight Simulator 2024",
        "Middle-earth: Shadow of War", "Military Conflict: Vietnam", "MindsEye",
        "Monopoly (2024)", "Monster Energy Supercross", "Monster Energy Supercross 3",
        "Monster Girl Island: Prologue", "Monster Hunter Rise", "Monster Hunter Wilds",
        "Monster Hunter: World", "Mortal Kombat 1", "Mortal Kombat 11", "MotoGP 18",
        "MotoGP 19", "MotoGP 20", "MotoGP 24", "Mutant Ops", "MXGP 2019", "MXGP Pro",
        "Myst (2021)", "NASCAR 25", "NASCAR Arcade Rush", "NBA 2K26", "NBB.EXE",
        "Need for Speed Heat", "Need for Speed Unbound", "New World", "Nex Machina",
        "Ni no Kuni II: Revenant Kingdom", "NieR: Automata", "Ninja Gaiden 2 Black",
        "Ninja Gaiden 4", "Nioh 2: The Complete Edition", "Nioh 3", "No Man's Sky",
        "No Rest for the Wicked", "Nuclear Nightmare", "Observer: System Redux", "Off The Grid",
        "Onimusha: Way of the Sword", "Ontos", "Orcs Must Die! 3",
        "Ori and the Will of the Wisps", "Osiris: New Dawn", "Outriders", "Overwatch 2",
        "Palia", "Paragon", "Path of Exile", "Path of Exile 2", "Persona 5: The Phantom X",
        "Phantasy Star Online 2 New Genesis", "Pinball FX", "Plan B: Terraform",
        "Planet Coaster 2", "Planet Zoo", "Planetary Annihilation: Titans", "Postal 2 Redux",
        "PowerWash Simulator", "Pragmata", "Prison Architect 2", "Pro Evolution Soccer 2016",
        "Pro Evolution Soccer 2016 myClub", "Pro Evolution Soccer 2017",
        "Pro Evolution Soccer 2017 Trial", "Pro Evolution Soccer 2018",
        "Pro Evolution Soccer 2018 Lite", "Pro Evolution Soccer 2019",
        "Project Colored Mountains", "Project Nightmares Case 36: Henrietta Kedward",
        "Pumpkin Jack", "Ratchet & Clank: Rift Apart", "Reanimal", "Red Dead Redemption",
        "Red Dead Redemption 2", "Redfall", "Redout", "Redout: Space Assault",
        "Resident Evil 2 (2019)", "Resident Evil 3 (2020)", "Resident Evil 4 (2023)",
        "Resident Evil 7: Biohazard", "Resident Evil Requiem", "Resident Evil Resistance",
        "Resident Evil Village", "Resonance: A Plague Tale Legacy", "Return to Nangrim",
        "Returnal", "Rhythm World - Master Project", "Ride 3", "Riders Republic",
        "Rise of the Ronin", "Riven (2024)", "Rolf Connect Coding", "Rolf Connect Math",
        "S.T.A.L.K.E.R. 2: Heart of Chornobyl", "Saints Row",
        "Saints Row: The Third Remastered", "Screamer (2026)", "Sea of Thieves",
        "Sekiro: Shadows Die Twice", "Sensorium", "Shadow of the Tomb Raider",
        "Shadow Warrior 2", "Sid Meier's Civilization VII", "Silent Hill 2", "Silent Hill f",
        "Skate Story", "Skull and Bones", "Sky Gamblers: Storm Raiders",
        "Sky: Children of the Light", "Sleep Awake", "Slender: The Arrival (2023)",
        "Slitterhead", "Sniper Elite 5", "Sniper Elite: Resistance", "Son and Bone",
        "Soulframe", "South of Midnight", "Spine", "Split Fiction", "Squadron 42",
        "Star Citizen", "Star Ocean: The Divine Force", "Star Wars Battlefront II (2017)",
        "Star Wars Jedi: Fallen Order", "Star Wars Jedi: Survivor", "Star Wars Outlaws",
        "Star Wars: Squadrons", "Steel Seed", "Stellar Blade", "Still Wakes the Deep",
    ];

}

internal sealed class HdrCache
{
    public DateTime UpdatedUtc { get; set; }

    /// <summary>
    /// Results fetched from Steam, by app id.
    ///
    /// Note there is no field for the built-in list. It is compiled into the build and must
    /// stay there: persisting it made stale claims permanent.
    /// </summary>
    public Dictionary<string, int> AppIds { get; set; } = [];
    public List<string> Asked { get; set; } = [];
    public Dictionary<string, int> Wiki { get; set; } = [];
    public List<string> AskedWiki { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(HdrCache))]
internal partial class HdrJson : JsonSerializerContext
{
}

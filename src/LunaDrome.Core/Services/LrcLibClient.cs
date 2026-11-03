using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using LunaDrome.Core.Models;

namespace LunaDrome.Core.Services;

/// <summary>
/// Online lyrics from LRCLIB (lrclib.net, free, no account) - the fallback when Navidrome has
/// none for a track, like Feishin's lyrics fetchers. Prefers time-synced (LRC) lyrics.
/// </summary>
public static partial class LrcLibClient
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(10) };
        // LRCLIB asks clients to identify themselves.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LunaDrome (https://github.com/Lun-Astra/LunaDrome)");
        return http;
    }

    private const double SyncedToleranceSec = 2;
    // Plain text isn't timed, so a remaster or "Reload" version a few seconds off still fits.
    private const double PlainToleranceSec = 15;

    public static async Task<(List<LyricLine> Lines, bool Synced)> GetLyricsAsync(string artist, string title, string album, int durationSeconds)
    {
        try
        {
            // #20: library tags often don't match LRCLIB's spelling - several artists joined
            // ("A, B"), "(feat. X)" / "(Official Video)" / "- Remastered" in the title, or a
            // YouTube-style "Artist - Title". Try the tags as they are first, then cleaned-up
            // variants; exact lookups (album + duration) before searches.
            var artists = ArtistVariants(artist);
            var titles = TitleVariants(title, artists);
            foreach (var t in titles)
                foreach (var a in artists)
                {
                    var query = $"api/get?artist_name={Uri.EscapeDataString(a)}&track_name={Uri.EscapeDataString(t)}"
                                + $"&album_name={Uri.EscapeDataString(album)}&duration={durationSeconds}";
                    using var response = await Http.GetAsync(query);
                    if (response.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                        if (FromRecord(doc.RootElement) is { Lines.Count: > 0 } found)
                            return found;
                    }
                    else if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        return (new(), false);   // rate limited: don't hammer it
                    }
                    // Anything else means "not found" here: for a song it doesn't have, api/get
                    // answers 503 (its own outside lookup came up empty), and now and then even
                    // for one it does have. Giving up on that skipped every other spelling and the
                    // search below - about half the songs ended up without lyrics.
                }

            // Synced lyrics are timed to one specific recording: a version a few seconds longer
            // or shorter (a music video's intro, a radio edit) puts every line off by that much.
            // So synced lyrics are only taken within SyncedToleranceSec of this file, the closest
            // one wins; anything else (up to PlainToleranceSec) is still fine as plain text.
            // A slowed / sped-up / nightcore edit is the same recording at another speed: any
            // length goes, and synced lines are stretched to this file's length.
            var speedEdit = SpeedEditRegex().IsMatch(title) || SpeedEditRegex().IsMatch(album);
            (List<LyricLine> Lines, bool Synced)? plainFallback = null;
            (List<LyricLine> Lines, bool Synced)? bestSynced = null;
            var bestDiff = double.MaxValue;

            void Consider(JsonElement record, Func<JsonElement, bool>? sameSong = null)
            {
                if (sameSong != null && !sameSong(record))
                    return;
                var recordSeconds = Seconds(record);
                var diff = durationSeconds > 0 && recordSeconds > 0 ? Math.Abs(recordSeconds - durationSeconds) : 0;
                // Skip versions whose length is way off (live cuts, extended mixes). Matched on
                // the title alone it has to be the same length, plain text too. A speed edit is
                // at most ~30% faster or slower.
                var tolerance = sameSong != null ? SyncedToleranceSec : PlainToleranceSec;
                var fits = speedEdit
                    ? recordSeconds <= 0 || durationSeconds <= 0 || durationSeconds / recordSeconds is > 0.7 and < 1.4
                    : diff <= tolerance;
                if (!fits || FromRecord(record) is not { Lines.Count: > 0 } candidate)
                    return;

                if (candidate.Synced && speedEdit && recordSeconds > 0 && durationSeconds > 0)
                {
                    var stretch = durationSeconds / recordSeconds;
                    foreach (var line in candidate.Lines)
                    {
                        line.Start = TimeSpan.FromSeconds(line.Start.TotalSeconds * stretch);
                        line.Words = line.Words?.Select(w => w with { Offset = TimeSpan.FromSeconds(w.Offset.TotalSeconds * stretch) }).ToList();
                    }
                    diff = 0;
                }
                if (candidate.Synced && diff <= SyncedToleranceSec)
                {
                    if (diff < bestDiff)
                        (bestSynced, bestDiff) = (candidate, diff);
                }
                else
                {
                    plainFallback ??= (candidate.Lines, false);
                }
            }

            async Task<bool> SearchAsync(string search, Func<JsonElement, bool>? sameSong = null)
            {
                using var response = await Http.GetAsync(search);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    return false;
                if (response.IsSuccessStatusCode)
                {
                    using var results = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    var records = results.RootElement.EnumerateArray().ToList();
                    // A speed edit can't be matched on length: take the song's usual length (the
                    // one most uploads share), not a live cut or a remix that happens to come first.
                    if (speedEdit)
                    {
                        var usual = records.GroupBy(r => Math.Round(Seconds(r))).ToDictionary(g => g.Key, g => g.Count());
                        records = records.OrderByDescending(r => usual[Math.Round(Seconds(r))]).ToList();
                    }
                    foreach (var record in records)
                        Consider(record, sameSong);
                }
                return true;
            }

            var searches = titles.Select(t => $"api/search?artist_name={Uri.EscapeDataString(artists[^1])}&track_name={Uri.EscapeDataString(t)}")
                .Append($"api/search?q={Uri.EscapeDataString(artists[^1] + " " + titles[^1])}");
            foreach (var search in searches)
            {
                if (!await SearchAsync(search))
                    return plainFallback ?? (new(), false);
                if (bestSynced != null)
                    return bestSynced.Value;
                if (plainFallback != null)
                    break;
            }

            // Soundtracks are often tagged with the studio or the game ("Supergiant Games",
            // "Hades II - Time Belongs to Us") while LRCLIB has the singer. Last try: the title
            // alone. Plenty of different songs share a title (and a length), so it has to be
            // exactly that title on the same album or by one of the artists - or a speed edit,
            // whose uploader is never the artist.
            if (plainFallback == null)
            {
                var albumKey = Normalize(album);
                var artistKeys = artists.Concat(Regex.Split(artist, @",|&| x | feat\.? | ft\.? ", RegexOptions.IgnoreCase))
                    .Select(Normalize).Where(k => k.Length > 0).Distinct().ToList();
                foreach (var bare in BareTitles(titles[^1]))
                {
                    var key = Normalize(bare);
                    bool SameSong(JsonElement record) =>
                        Normalize(Text(record, "trackName")) == key
                        && (speedEdit
                            || (albumKey.Length > 0 && Normalize(Text(record, "albumName")) == albumKey)
                            || artistKeys.Any(Normalize(Text(record, "artistName")).Contains));
                    if (!await SearchAsync($"api/search?track_name={Uri.EscapeDataString(bare)}", SameSong))
                        break;
                    if (bestSynced != null)
                        return bestSynced.Value;
                    if (plainFallback != null)
                        break;
                }
            }
            return plainFallback ?? (new(), false);
        }
        catch
        {
            return (new(), false);
        }
    }

    private static double Seconds(JsonElement record) =>
        record.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    [GeneratedRegex(@"slowed|reverb|sped[\s-]*up|speed[\s-]*up|nightcore|daycore", RegexOptions.IgnoreCase)]
    private static partial Regex SpeedEditRegex();

    /// <summary>The title without "Something - " in front ("Hades II - Time Belongs to Us"),
    /// and without " - something" after it ("Lil Crush - Live from Phoenix").</summary>
    private static IEnumerable<string> BareTitles(string title)
    {
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash < 0)
        {
            yield return title;
            yield break;
        }
        yield return title[(dash + 3)..].Trim();
        yield return title[..dash].Trim();
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", "");

    /// <summary>The artist as tagged, then just the first of several ("A, B" / "A & B" / "A x B").</summary>
    private static List<string> ArtistVariants(string artist)
    {
        var list = new List<string> { artist.Trim() };
        var primary = Regex.Split(artist, @",|&| x | feat\.? | ft\.? ", RegexOptions.IgnoreCase)[0].Trim();
        if (primary.Length > 0 && !list.Contains(primary, StringComparer.OrdinalIgnoreCase))
            list.Add(primary);
        return list;
    }

    /// <summary>The title as tagged, then without packaging such as "(feat. X)", "(Official Video)",
    /// "[Lyrics]" or "- Remastered 2011", then with an "Artist - " prefix removed.</summary>
    private static List<string> TitleVariants(string title, List<string> artists)
    {
        var list = new List<string> { title.Trim() };
        var cleaned = Regex.Replace(title,
            @"\s*[\(\[][^\)\]]*(feat\.?|ft\.?|with |official|video|audio|lyric|visuali[sz]er|remaster|hd|4k|mv|slowed|reverb|sped|speed|nightcore|daycore)[^\)\]]*[\)\]]",
            "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+-\s+(\d{4}\s+)?remaster(ed)?.*$", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+-\s+[^-]*(slowed|reverb|sped|speed up|nightcore|daycore)[^-]*$", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+-\s+[^-]*(slowed|reverb|sped|speed up|nightcore|daycore)[^-]*$", "", RegexOptions.IgnoreCase).Trim();
        foreach (var candidate in new[] { cleaned }
                     .Concat(artists.Select(a => cleaned.StartsWith(a + " - ", StringComparison.OrdinalIgnoreCase) ? cleaned[(a.Length + 3)..].Trim() : "")))
            if (candidate.Length > 0 && !list.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                list.Add(candidate);
        return list;
    }

    private static (List<LyricLine> Lines, bool Synced)? FromRecord(JsonElement record)
    {
        if (record.TryGetProperty("syncedLyrics", out var synced) && synced.GetString() is { Length: > 0 } lrc)
        {
            var lines = ParseLrc(lrc);
            if (lines.Count > 0)
                return (lines, true);
        }

        if (record.TryGetProperty("plainLyrics", out var plain) && plain.GetString() is { Length: > 0 } text)
            return (text.Replace("\r\n", "\n").Split('\n').Select(l => new LyricLine { Text = l }).ToList(), false);

        return null;
    }

    [GeneratedRegex(@"\[(\d+):(\d+(?:[.:]\d+)?)\]")]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"<(\d+):(\d+(?:[.:]\d+)?)>")]
    private static partial Regex WordStampRegex();

    private static double StampSeconds(Match m) =>
        int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60
        + double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);

    // "[01:23.45] text" lines; a line may carry several timestamps (repeated chorus). Enhanced LRC
    // also times each word ("[01:23.45]<01:23.45>Hello <01:23.90>world"): kept as Words, relative
    // to the line so a repeated line reuses them.
    public static List<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var matches = TimestampRegex().Matches(raw);
            if (matches.Count == 0)
                continue;

            var text = raw[(matches[^1].Index + matches[^1].Length)..].Trim();
            List<LyricWord>? words = null;
            var stamps = WordStampRegex().Matches(text);
            if (stamps.Count > 0)
            {
                var lineStart = StampSeconds(matches[0]);
                words = new();
                for (var i = 0; i < stamps.Count; i++)
                {
                    var from = stamps[i].Index + stamps[i].Length;
                    var to = i + 1 < stamps.Count ? stamps[i + 1].Index : text.Length;
                    var word = text[from..to];
                    if (word.Trim().Length > 0)
                        words.Add(new LyricWord(TimeSpan.FromSeconds(Math.Max(0, StampSeconds(stamps[i]) - lineStart)), word));
                }
                text = WordStampRegex().Replace(text, "").Trim();
                if (words.Count == 0)
                    words = null;
            }
            foreach (Match m in matches)
                lines.Add(new LyricLine { Start = TimeSpan.FromSeconds(StampSeconds(m)), Text = text, Words = words });
        }
        return lines.OrderBy(l => l.Start).ToList();
    }
}

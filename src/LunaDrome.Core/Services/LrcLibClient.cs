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
                    else if (response.StatusCode != HttpStatusCode.NotFound)
                    {
                        return (new(), false);   // rate limited / down: don't hammer it
                    }
                }

            (List<LyricLine> Lines, bool Synced)? plainFallback = null;
            var searches = titles.Select(t => $"api/search?artist_name={Uri.EscapeDataString(artists[^1])}&track_name={Uri.EscapeDataString(t)}")
                .Append($"api/search?q={Uri.EscapeDataString(artists[^1] + " " + titles[^1])}");
            foreach (var search in searches)
            {
                using var results = JsonDocument.Parse(await Http.GetStringAsync(search));
                foreach (var record in results.RootElement.EnumerateArray())
                {
                    // Skip versions whose length is way off (live cuts, extended mixes).
                    if (durationSeconds > 0 && record.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                        && Math.Abs(d.GetDouble() - durationSeconds) > 5)
                        continue;

                    if (FromRecord(record) is { Lines.Count: > 0 } candidate)
                    {
                        if (candidate.Synced)
                            return candidate;
                        plainFallback ??= candidate;
                    }
                }
                if (plainFallback != null)
                    break;
            }
            return plainFallback ?? (new(), false);
        }
        catch
        {
            return (new(), false);
        }
    }

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
            @"\s*[\(\[][^\)\]]*(feat\.?|ft\.?|with |official|video|audio|lyric|visuali[sz]er|remaster|hd|4k|mv)[^\)\]]*[\)\]]",
            "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+-\s+(\d{4}\s+)?remaster(ed)?.*$", "", RegexOptions.IgnoreCase).Trim();
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

    // "[01:23.45] text" lines; a line may carry several timestamps (repeated chorus).
    public static List<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var matches = TimestampRegex().Matches(raw);
            if (matches.Count == 0)
                continue;

            var text = raw[(matches[^1].Index + matches[^1].Length)..].Trim();
            foreach (Match m in matches)
            {
                var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                lines.Add(new LyricLine { Start = TimeSpan.FromSeconds(minutes * 60 + seconds), Text = text });
            }
        }
        return lines.OrderBy(l => l.Start).ToList();
    }
}

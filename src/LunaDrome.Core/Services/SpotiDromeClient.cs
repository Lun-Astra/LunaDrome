using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LunaDrome.Core.Services;

/// <summary>A YouTube Music song, or a plain YouTube video (covers, live versions).</summary>
public class SpotiDromeTrack
{
    [JsonPropertyName("video_id")] public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string? Album { get; set; }
    public int? Duration { get; set; }
    public string? Thumbnail { get; set; }
    public string? Url { get; set; }
    /// <summary>"song" (a YouTube Music release) or "video" (plain YouTube).</summary>
    public string Kind { get; set; } = "song";
    [JsonPropertyName("track_number")] public int? TrackNumber { get; set; }

    public string DurationDisplay => Duration is > 0 ? $"{Duration / 60}:{Duration % 60:D2}" : "";
    public string Subtitle => string.IsNullOrEmpty(Album) ? Artist : $"{Artist} • {Album}";
}

public class SpotiDromeAlbum
{
    [JsonPropertyName("browse_id")] public string BrowseId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string? Year { get; set; }
    public string? Type { get; set; }
    public string? Thumbnail { get; set; }
    [JsonPropertyName("track_count")] public int? TrackCount { get; set; }
    /// <summary>The playlist URL /ytmusic/download takes; only set by GetAlbumAsync.</summary>
    public string? Url { get; set; }
    public List<SpotiDromeTrack> Tracks { get; set; } = new();

    public string Subtitle => string.IsNullOrEmpty(Year) ? Artist : $"{Artist} • {Year}";
}

public class SpotiDromeSearchResult
{
    public List<SpotiDromeTrack> Songs { get; set; } = new();
    public List<SpotiDromeAlbum> Albums { get; set; } = new();
    public List<SpotiDromeTrack> Videos { get; set; } = new();
}

/// <summary>A SpotiDrome download job (GET /jobs/&lt;id&gt;).</summary>
public class SpotiDromeJob
{
    public string Id { get; set; } = "";
    /// <summary>pending, running, uploading, scanning, done.</summary>
    public string Status { get; set; } = "";
    public int Total { get; set; }
    public int Current { get; set; }
    public int Downloaded { get; set; }
    public int Failed { get; set; }
    [JsonPropertyName("current_track")] public string? CurrentTrack { get; set; }
    public List<string> Log { get; set; } = new();

    public bool IsDone => Status == "done";
}

/// <summary>
/// LunaDrome's optional link to a SpotiDrome instance (Luna's downloader for his Navidrome):
/// search YouTube Music through it and have it download songs/albums straight into the
/// Navidrome library. Talks to the API the SpotiDrome web UI uses (http://host:8080/api).
/// </summary>
public class SpotiDromeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Downloads are started server-side and polled, so no request here should take long -
    // except search, which runs yt-dlp too (~5-15s).
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };

    /// <summary>Base API URL, e.g. http://192.168.1.20:8080/api; empty = not configured.</summary>
    public string BaseUrl { get; private set; } = "";

    public bool IsConfigured => BaseUrl.Length > 0;

    /// <summary>Accepts "host:8080", "http://host:8080" or ".../api" and normalizes to the API root.</summary>
    public static string NormalizeUrl(string input)
    {
        var url = (input ?? "").Trim().TrimEnd('/');
        if (url.Length == 0)
            return "";
        if (!url.Contains("://"))
            url = "http://" + url;
        // The web frontend (8080) proxies the backend under /api; the backend itself (5000) doesn't.
        var uri = new Uri(url);
        if (uri.Port != 5000 && !uri.AbsolutePath.TrimEnd('/').EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            url += "/api";
        return url;
    }

    public void Configure(string? url) => BaseUrl = NormalizeUrl(url ?? "");

    /// <summary>Null when reachable, else what went wrong.</summary>
    public async Task<string?> TestConnectionAsync()
    {
        if (!IsConfigured)
            return "No address set.";
        try
        {
            using var response = await _http.GetAsync($"{BaseUrl}/ytdlp/version");
            if (!response.IsSuccessStatusCode)
                return $"SpotiDrome answered {(int)response.StatusCode} {response.ReasonPhrase}.";
            // The search API came with the LunaDrome integration; an older SpotiDrome lacks it.
            using var search = await _http.GetAsync($"{BaseUrl}/search?q=");
            return search.IsSuccessStatusCode
                ? null
                : "Connected, but this SpotiDrome has no search API yet - update it (git pull + docker compose up -d --build).";
        }
        catch (Exception ex)
        {
            return $"Couldn't reach SpotiDrome: {ex.Message}";
        }
    }

    public async Task<SpotiDromeSearchResult> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var json = await _http.GetStringAsync($"{BaseUrl}/search?q={Uri.EscapeDataString(query)}&limit=12", cancellationToken);
        return JsonSerializer.Deserialize<SpotiDromeSearchResult>(json, JsonOptions) ?? new();
    }

    public async Task<SpotiDromeAlbum> GetAlbumAsync(string browseId)
    {
        var json = await _http.GetStringAsync($"{BaseUrl}/search/album/{Uri.EscapeDataString(browseId)}");
        return JsonSerializer.Deserialize<SpotiDromeAlbum>(json, JsonOptions) ?? new();
    }

    /// <summary>Starts a library-only download (no Navidrome playlist, no auto-sync); returns the job id.
    /// Files land in album-named folders at the top of the music library ("LunaDrome" is only the
    /// job/staging label).</summary>
    public async Task<string> DownloadAsync(string url, bool isAlbum, string label, string? albumName)
    {
        using var response = await _http.PostAsJsonAsync($"{BaseUrl}/ytmusic/download", new Dictionary<string, object?>
        {
            ["url"] = url,
            ["is_playlist"] = isAlbum,
            ["playlist_name"] = "LunaDrome",
            ["sync_playlist"] = false,
            ["track_for_sync"] = false,
            ["album"] = albumName,
            ["job_label"] = $"[LunaDrome] {label}",
        });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"SpotiDrome refused the download ({(int)response.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("job_id").GetString() ?? throw new HttpRequestException("No job id returned.");
    }

    public async Task<SpotiDromeJob?> GetJobAsync(string jobId)
    {
        // Polled every few seconds - a stalled server should count as "not responding" quickly.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await _http.GetAsync($"{BaseUrl}/jobs/{Uri.EscapeDataString(jobId)}", cts.Token);
        if (!response.IsSuccessStatusCode)
            return null;
        return JsonSerializer.Deserialize<SpotiDromeJob>(await response.Content.ReadAsStringAsync(), JsonOptions);
    }

    public async Task CancelJobAsync(string jobId) =>
        (await _http.PostAsync($"{BaseUrl}/jobs/{Uri.EscapeDataString(jobId)}/cancel", null)).Dispose();
}

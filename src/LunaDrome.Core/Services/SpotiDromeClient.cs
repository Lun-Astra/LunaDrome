using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LunaDrome.Core.Services;

/// <summary>A search result that LunaDrome marks with a check when the library already has it.
/// Set after the results are shown (the library lookup takes a moment), hence the notification.</summary>
public abstract class LibraryMatchable : INotifyPropertyChanged
{
    private bool _inLibrary;

    [JsonIgnore]
    public bool InLibrary
    {
        get => _inLibrary;
        set
        {
            if (_inLibrary == value)
                return;
            _inLibrary = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InLibrary)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A YouTube Music song, or a plain YouTube video (covers, live versions).</summary>
public class SpotiDromeTrack : LibraryMatchable
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

public class SpotiDromeAlbum : LibraryMatchable
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
/// Since SpotiDrome cd9e111 every call needs an API key (SpotiDrome > Settings > Access, scope
/// "LunaDrome"), sent as a Bearer token; a SpotiDrome from before that has no /session and
/// simply ignores the header.
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

    public bool HasApiKey => _http.DefaultRequestHeaders.Authorization is not null;

    public void Configure(string? url, string? apiKey = null)
    {
        BaseUrl = NormalizeUrl(url ?? "");
        var key = (apiKey ?? "").Trim();
        _http.DefaultRequestHeaders.Authorization = key.Length > 0 ? new AuthenticationHeaderValue("Bearer", key) : null;
    }

    private const string NeedKeyMessage =
        "SpotiDrome needs an API key: create one in SpotiDrome > Settings > Access (scope \"LunaDrome\") and paste it here.";
    private const string RejectedKeyMessage =
        "SpotiDrome rejected the API key (mistyped or revoked) - create a new one in SpotiDrome > Settings > Access.";
    private const string ScopeMessage =
        "This SpotiDrome API key isn't allowed to do that - use a key with the \"LunaDrome\" scope.";

    /// <summary>A readable message for SpotiDrome's auth refusals (401 = no/bad key, 403 = key scope).</summary>
    private string? AuthProblem(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => HasApiKey ? RejectedKeyMessage : NeedKeyMessage,
        HttpStatusCode.Forbidden => ScopeMessage,
        _ => null,
    };

    private async Task<string> GetStringCheckedAsync(string url, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(url, cancellationToken);
        if (AuthProblem(response.StatusCode) is { } problem)
            throw new HttpRequestException(problem, null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>Null when reachable (and the key works), else what went wrong.</summary>
    public async Task<string?> TestConnectionAsync()
    {
        if (!IsConfigured)
            return "No address set.";
        try
        {
            // GET /session says whether the key is accepted. A SpotiDrome from before API keys
            // has no /session (404) and needs no key - fall back to the old check there.
            using var session = await _http.GetAsync($"{BaseUrl}/session");
            if (session.StatusCode != HttpStatusCode.NotFound)
            {
                if (!session.IsSuccessStatusCode)
                    return $"SpotiDrome answered {(int)session.StatusCode} {session.ReasonPhrase}.";
                using var doc = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
                if (!(doc.RootElement.TryGetProperty("logged_in", out var loggedIn) && loggedIn.GetBoolean()))
                    return HasApiKey ? RejectedKeyMessage : NeedKeyMessage;
            }

            using var response = await _http.GetAsync($"{BaseUrl}/ytdlp/version");
            if (AuthProblem(response.StatusCode) is { } problem)
                return problem;
            if (!response.IsSuccessStatusCode)
                return $"SpotiDrome answered {(int)response.StatusCode} {response.ReasonPhrase}.";
            // The search API came with the LunaDrome integration; an older SpotiDrome lacks it.
            using var search = await _http.GetAsync($"{BaseUrl}/search?q=");
            if (AuthProblem(search.StatusCode) is { } searchProblem)
                return searchProblem;
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
        var json = await GetStringCheckedAsync($"{BaseUrl}/search?q={Uri.EscapeDataString(query)}&limit=12", cancellationToken);
        return JsonSerializer.Deserialize<SpotiDromeSearchResult>(json, JsonOptions) ?? new();
    }

    public async Task<SpotiDromeAlbum> GetAlbumAsync(string browseId)
    {
        var json = await GetStringCheckedAsync($"{BaseUrl}/search/album/{Uri.EscapeDataString(browseId)}");
        return JsonSerializer.Deserialize<SpotiDromeAlbum>(json, JsonOptions) ?? new();
    }

    /// <summary>Starts a library-only download (no Navidrome playlist, no auto-sync); returns the job id.
    /// Files land in album-named folders at the top of the music library ("LunaDrome" is only the
    /// job/staging label). An album (isAlbum) is downloaded whole into its own album folder:
    /// tracks you already have elsewhere (e.g. as a single) are still added to it, all tagged
    /// with <paramref name="albumArtist"/> and track numbers.</summary>
    public async Task<string> DownloadAsync(string url, bool isAlbum, string label, string? albumName, string? albumArtist = null)
    {
        using var response = await _http.PostAsJsonAsync($"{BaseUrl}/ytmusic/download", new Dictionary<string, object?>
        {
            ["url"] = url,
            ["is_playlist"] = isAlbum,
            ["playlist_name"] = "LunaDrome",
            ["sync_playlist"] = false,
            ["track_for_sync"] = false,
            ["album"] = albumName,
            ["complete_album"] = isAlbum,
            ["album_artist"] = isAlbum ? albumArtist : null,
            ["job_label"] = $"[LunaDrome] {label}",
        });
        var body = await response.Content.ReadAsStringAsync();
        if (AuthProblem(response.StatusCode) is { } problem)
            throw new HttpRequestException(problem, null, response.StatusCode);
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
        // A key problem isn't "SpotiDrome lost the job" - the job is still running there; throw so
        // polling keeps trying (and picks up again once the key is fixed in Settings).
        if (AuthProblem(response.StatusCode) is { } problem)
            throw new HttpRequestException(problem, null, response.StatusCode);
        if (!response.IsSuccessStatusCode)
            return null;
        return JsonSerializer.Deserialize<SpotiDromeJob>(await response.Content.ReadAsStringAsync(), JsonOptions);
    }

    public async Task CancelJobAsync(string jobId) =>
        (await _http.PostAsync($"{BaseUrl}/jobs/{Uri.EscapeDataString(jobId)}/cancel", null)).Dispose();

    /// <summary>Discover Weekly / Daily Discovery: which Navidrome playlists hold this week's
    /// picks. Null when this SpotiDrome has no Discover yet.</summary>
    public async Task<SpotiDromeDiscover?> GetDiscoverAsync()
    {
        using var response = await _http.GetAsync($"{BaseUrl}/discover");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (AuthProblem(response.StatusCode) is { } problem)
            throw new HttpRequestException(problem, null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SpotiDromeDiscover>(await response.Content.ReadAsStringAsync(), JsonOptions);
    }

    /// <summary>Makes a mix now ("weekly" / "daily"); it shows up in the jobs like a download.</summary>
    public Task RunDiscoverAsync(string mix) =>
        PostCheckedAsync("discover/run", new Dictionary<string, object?> { ["mix"] = mix });

    /// <summary>"keep" stars the song (it moves into the library at the next mix), "unkeep"
    /// takes the star off, "reject" deletes it right away and it's never picked again.</summary>
    public Task DiscoverFeedbackAsync(string songId, string action) =>
        PostCheckedAsync("discover/feedback", new Dictionary<string, object?> { ["song_id"] = songId, ["action"] = action });

    private async Task PostCheckedAsync(string path, Dictionary<string, object?> body)
    {
        using var response = await _http.PostAsJsonAsync($"{BaseUrl}/{path}", body);
        if (AuthProblem(response.StatusCode) is { } problem)
            throw new HttpRequestException(problem, null, response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync();
            string? error = null;
            try { error = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString(); } catch { }
            throw new HttpRequestException(error ?? $"SpotiDrome answered {(int)response.StatusCode}.", null, response.StatusCode);
        }
    }
}

public class SpotiDromeDiscover
{
    public Dictionary<string, SpotiDromeDiscoverMix> Mixes { get; set; } = new();
    [JsonPropertyName("keep_plays")] public int KeepPlays { get; set; } = 2;
    [JsonPropertyName("kept_playlist")] public string KeptPlaylist { get; set; } = "";
}

public class SpotiDromeDiscoverMix
{
    public string Playlist { get; set; } = "";
    [JsonPropertyName("playlist_id")] public string? PlaylistId { get; set; }
    public bool Enabled { get; set; }
    public bool Running { get; set; }
    [JsonPropertyName("song_ids")] public List<string> SongIds { get; set; } = [];
    [JsonPropertyName("generated_at")] public long? GeneratedAt { get; set; }
    [JsonPropertyName("last_error")] public string? LastError { get; set; }
}

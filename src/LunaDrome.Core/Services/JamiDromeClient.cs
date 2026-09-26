using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LunaDrome.Core.Services;

/// <summary>A track in the jam: queued, downloading, ready or playing.</summary>
public class JamItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string? Album { get; set; }
    public string? Thumbnail { get; set; }
    public double? Duration { get; set; }
    /// <summary>queued, downloading, ready, playing, done, failed.</summary>
    public string Status { get; set; } = "";
    [JsonPropertyName("requested_by")] public string? RequestedBy { get; set; }
    public double? Progress { get; set; }
    public int Votes { get; set; }
    /// <summary>Only for what's playing (and only for hosts); relative, e.g. /api/stream/abc.</summary>
    [JsonPropertyName("stream_url")] public string? StreamUrl { get; set; }
    [JsonPropertyName("navidrome_song_id")] public string? NavidromeSongId { get; set; }
    /// <summary>"library" (added from Navidrome) or "youtube".</summary>
    public string? Source { get; set; }

    /// <summary>Absolute art URL (library covers come back relative); set by JamiDromeClient.</summary>
    [JsonIgnore] public string? ArtUrl { get; set; }

    [JsonIgnore] public int DurationSeconds => (int)Math.Round(Duration ?? 0);
    [JsonIgnore] public string DurationDisplay => DurationSeconds > 0 ? $"{DurationSeconds / 60}:{DurationSeconds % 60:D2}" : "";
    [JsonIgnore] public string RequestedByDisplay => string.IsNullOrWhiteSpace(RequestedBy) ? "" : $"requested by {RequestedBy}";
    [JsonIgnore]
    public string StatusDisplay => Status switch
    {
        "downloading" => $"Downloading {Math.Round(Progress ?? 0)}%",
        "queued" => "Queued",
        "ready" => "Ready",
        "failed" => "Failed",
        _ => "",
    };
}

public class JamSpeaker
{
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>GET /queue: the whole jam as the server sees it.</summary>
public class JamState
{
    [JsonPropertyName("server_time")] public double ServerTime { get; set; }
    public JamSpeaker? Speaker { get; set; }
    [JsonPropertyName("now_playing")] public JamItem? NowPlaying { get; set; }
    [JsonPropertyName("playback_started_at")] public double? PlaybackStartedAt { get; set; }
    public bool Paused { get; set; }
    [JsonPropertyName("pause_started_at")] public double? PauseStartedAt { get; set; }
    /// <summary>"host" (someone pressed pause) or "no_speaker" (nobody is playing the jam).</summary>
    [JsonPropertyName("pause_reason")] public string? PauseReason { get; set; }
    [JsonPropertyName("autoplay_enabled")] public bool AutoplayEnabled { get; set; }
    [JsonPropertyName("autofill_in_progress")] public bool AutofillInProgress { get; set; }
    public List<JamItem> Queue { get; set; } = new();
    public List<JamItem> History { get; set; } = new();

    /// <summary>How far into the current track the shared timeline is, given the server's "now".</summary>
    public double ElapsedAt(double serverNow)
    {
        if (PlaybackStartedAt is not { } started)
            return 0;
        var end = Paused && PauseStartedAt is { } pausedAt ? pausedAt : serverNow;
        return Math.Max(0, end - started);
    }
}

public class JamSearchResult
{
    [JsonPropertyName("video_id")] public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string? Album { get; set; }
    public double? Duration { get; set; }
    public string? Thumbnail { get; set; }
    public string? Url { get; set; }

    [JsonIgnore] public string DurationDisplay => Duration is > 0 ? $"{(int)Duration / 60}:{(int)Duration % 60:D2}" : "";
    [JsonIgnore] public string Subtitle => string.IsNullOrEmpty(Album) ? Artist : $"{Artist} • {Album}";
}

public class JamInvite
{
    public string Token { get; set; } = "";
    [JsonPropertyName("created_at")] public double CreatedAt { get; set; }
    [JsonPropertyName("expires_at")] public double ExpiresAt { get; set; }
}

public class JamiDromeException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public JamiDromeException(string message, HttpStatusCode? statusCode = null) : base(message) => StatusCode = statusCode;
    public bool IsAuthError => StatusCode == HttpStatusCode.Unauthorized;
}

/// <summary>
/// LunaDrome's link to JamiDrome (Luna's shared "jam" server, next to SpotiDrome): a host
/// logs in with Navidrome credentials, then controls the jam, shares invite links and can be
/// the jam's one "speaker". Talks to the /api the JamiDrome web pages use. UI-free, so the
/// phone app can reuse it.
/// </summary>
public class JamiDromeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Search runs yt-dlp on the server (~5-15s); everything else is quick.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(40) };

    /// <summary>API root, e.g. https://jam.example.com/api; empty = not configured.</summary>
    public string BaseUrl { get; private set; } = "";
    /// <summary>The host token from /host/login; empty = not logged in.</summary>
    public string HostToken { get; private set; } = "";
    /// <summary>Sent as X-Jam-Session (the server keys votes and bans off it).</summary>
    public string SessionId { get; set; } = "";

    public bool IsConfigured => BaseUrl.Length > 0;
    public bool IsLoggedIn => IsConfigured && HostToken.Length > 0;

    /// <summary>The site root (BaseUrl without /api) - where invite links and /api/... paths live.</summary>
    public string SiteRoot => BaseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? BaseUrl[..^4] : BaseUrl;

    /// <summary>Accepts "jam.example.com", "https://jam.example.com" or ".../api" and normalizes to the API root.</summary>
    public static string NormalizeUrl(string input)
    {
        var url = (input ?? "").Trim().TrimEnd('/');
        if (url.Length == 0)
            return "";
        if (!url.Contains("://"))
            url = "https://" + url;
        // The web frontend proxies the backend under /api; the backend itself (5000) doesn't.
        var uri = new Uri(url);
        if (uri.Port != 5000 && !uri.AbsolutePath.TrimEnd('/').EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            url += "/api";
        return url;
    }

    public void Configure(string? url, string? hostToken)
    {
        BaseUrl = NormalizeUrl(url ?? "");
        HostToken = hostToken ?? "";
    }

    /// <summary>Makes a relative "/api/..." path from the server absolute; absolute URLs pass through.</summary>
    public string? ResolveUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Contains("://"))
            return url;
        return SiteRoot + (url.StartsWith('/') ? url : "/" + url);
    }

    /// <summary>A URL the media player / an Image can load: absolute, with the host token as ?key=
    /// (neither can send headers).</summary>
    public string? AuthorizedUrl(string? url)
    {
        var absolute = ResolveUrl(url);
        if (string.IsNullOrEmpty(absolute) || !absolute.StartsWith(SiteRoot, StringComparison.OrdinalIgnoreCase))
            return absolute;
        return $"{absolute}{(absolute.Contains('?') ? '&' : '?')}key={Uri.EscapeDataString(HostToken)}";
    }

    public string InviteQrUrl(string link) => AuthorizedUrl($"{BaseUrl}/invite/qr?data={Uri.EscapeDataString(link)}")!;

    /// <summary>Exchanges Navidrome credentials (Subsonic token + salt, the password never leaves
    /// this device) for a host token. Returns the token; throws JamiDromeException with the reason.</summary>
    public async Task<string> LoginAsync(string username, string token, string salt, string label)
    {
        var body = await SendAsync(HttpMethod.Post, "/host/login",
            new { username, token, salt, label }, authenticated: false);
        HostToken = body.GetProperty("token").GetString() ?? throw new JamiDromeException("No token returned.");
        return HostToken;
    }

    public async Task LogoutAsync()
    {
        try { await SendAsync(HttpMethod.Post, "/host/logout", null); }
        catch { /* the token is dropped locally either way */ }
        HostToken = "";
    }

    public async Task<JamState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var state = await GetAsync<JamState>("/queue", cancellationToken);
        foreach (var item in state.Queue.Concat(state.History).Append(state.NowPlaying))
            if (item != null)
                item.ArtUrl = ResolveUrl(item.Thumbnail);
        return state;
    }

    // ----- Speaker -----

    public async Task ClaimSpeakerAsync(string deviceId, string name) =>
        await SendAsync(HttpMethod.Post, "/speaker/claim", new { device_id = deviceId, name });

    /// <summary>False = another device is the speaker now (or this one timed out).</summary>
    public async Task<(bool Active, JamSpeaker? Speaker)> HeartbeatAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var body = await SendAsync(HttpMethod.Post, "/speaker/heartbeat", new { device_id = deviceId }, cancellationToken: cancellationToken);
        var speaker = body.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.Object
            ? sp.Deserialize<JamSpeaker>(JsonOptions)
            : null;
        return (body.GetProperty("active").GetBoolean(), speaker);
    }

    public async Task ReleaseSpeakerAsync(string deviceId, CancellationToken cancellationToken = default) =>
        await SendAsync(HttpMethod.Post, "/speaker/release", new { device_id = deviceId }, cancellationToken: cancellationToken);

    // ----- Playback -----

    /// <summary>Finishes <paramref name="expectedItemId"/> and moves on; a no-op if something else
    /// is already playing (so a double "ended" can't skip two songs).</summary>
    public async Task AdvanceAsync(string? expectedItemId) =>
        await SendAsync(HttpMethod.Post, "/player/advance", new { expected_id = expectedItemId });

    public async Task PauseAsync() => await SendAsync(HttpMethod.Post, "/player/pause", null);
    public async Task ResumeAsync() => await SendAsync(HttpMethod.Post, "/player/resume", null);
    public async Task SetAutoplayAsync(bool enabled) => await SendAsync(HttpMethod.Post, "/player/autoplay", new { enabled });

    /// <summary>Clears the queue, revokes every invite, drops the speaker, turns Auto DJ off.</summary>
    public async Task EndJamAsync() => await SendAsync(HttpMethod.Post, "/jam/end", null);

    // ----- Queue -----

    public async Task<List<JamSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(await GetRawAsync($"/search?q={Uri.EscapeDataString(query)}", cancellationToken));
        return doc.RootElement.GetProperty("results").Deserialize<List<JamSearchResult>>(JsonOptions) ?? new();
    }

    public async Task AddYouTubeAsync(JamSearchResult result, string requestedBy) =>
        await SendAsync(HttpMethod.Post, "/queue/add", new
        {
            video_id = result.VideoId, url = result.Url, title = result.Title, artist = result.Artist,
            album = result.Album, duration = result.Duration, thumbnail = result.Thumbnail, requested_by = requestedBy,
        });

    /// <summary>Queues a song from the Navidrome library (streams from Navidrome, no download).</summary>
    public async Task AddLibrarySongAsync(string songId, string requestedBy) =>
        await SendAsync(HttpMethod.Post, "/queue/add-library", new { song_id = songId, requested_by = requestedBy });

    /// <summary>Host delete: removes a queued track, or skips it if it's playing.</summary>
    public async Task RemoveAsync(string itemId) =>
        await SendAsync(HttpMethod.Post, $"/host/queue/{Uri.EscapeDataString(itemId)}/delete", null);

    public async Task RequeueAsync(string historyItemId, string requestedBy) =>
        await SendAsync(HttpMethod.Post, $"/history/{Uri.EscapeDataString(historyItemId)}/requeue", new { requested_by = requestedBy });

    // ----- Invites -----

    /// <summary>Creates a share link valid for <paramref name="ttl"/>; returns its full URL.</summary>
    public async Task<string> CreateInviteAsync(TimeSpan ttl)
    {
        var body = await SendAsync(HttpMethod.Post, "/invite/create", new { ttl_seconds = (int)ttl.TotalSeconds });
        return InviteLink(body.GetProperty("token").GetString() ?? "");
    }

    public string InviteLink(string token) => $"{SiteRoot}/invite/{token}";

    public async Task<List<JamInvite>> ListInvitesAsync()
    {
        using var doc = JsonDocument.Parse(await GetRawAsync("/invite/list"));
        return doc.RootElement.GetProperty("invites").Deserialize<List<JamInvite>>(JsonOptions) ?? new();
    }

    public async Task RevokeInviteAsync(string token) =>
        await SendAsync(HttpMethod.Post, $"/invite/{Uri.EscapeDataString(token)}/revoke", null);

    // ----- HTTP -----

    private HttpRequestMessage Request(HttpMethod method, string path, bool authenticated = true)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        if (authenticated && HostToken.Length > 0)
            request.Headers.Add("X-Jam-Host-Token", HostToken);
        if (SessionId.Length > 0)
            request.Headers.Add("X-Jam-Session", SessionId);
        return request;
    }

    private async Task<string> GetRawAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, path);
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw Failure(response.StatusCode, body);
        return body;
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken) where T : new() =>
        JsonSerializer.Deserialize<T>(await GetRawAsync(path, cancellationToken), JsonOptions) ?? new T();

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? payload,
        bool authenticated = true, CancellationToken cancellationToken = default)
    {
        using var request = Request(method, path, authenticated);
        request.Content = JsonContent.Create(payload ?? new { });
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw Failure(response.StatusCode, body);
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static JamiDromeException Failure(HttpStatusCode status, string body)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
                message = error.GetString();
        }
        catch (JsonException) { }
        return new JamiDromeException(message ?? $"JamiDrome answered {(int)status} {status}.", status);
    }
}

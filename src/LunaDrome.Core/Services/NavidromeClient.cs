using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LunaDrome.Core.Models;

namespace LunaDrome.Core.Services;

/// <summary>
/// Minimal Subsonic-API client targeting Navidrome. Handles token auth,
/// browsing (artists/albums/songs), and building stream URLs for playback.
/// </summary>
public class NavidromeClient
{
    private const string ClientId = "musicplayer";
    private const string ApiVersion = "1.16.1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http = new();

    public string ServerUrl { get; private set; } = "";
    public string Username { get; private set; } = "";
    private string _password = "";

    public bool IsAuthenticated { get; private set; }

    public void Configure(string serverUrl, string username, string password)
    {
        ServerUrl = serverUrl.TrimEnd('/');
        Username = username;
        _password = password;
    }

    public async Task<(bool Success, string? Error)> LoginAsync()
    {
        try
        {
            var url = BuildUrl("ping.view");
            var json = await _http.GetStringAsync(url);
            var envelope = JsonSerializer.Deserialize<SubsonicEnvelope<PingResponse>>(json, JsonOptions);
            var resp = envelope?.Response;

            if (resp == null)
                return (false, "Unexpected response from server.");

            if (resp.Status == "ok")
            {
                IsAuthenticated = true;
                return (true, null);
            }

            return (false, resp.Error?.Message ?? "Login failed.");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Could not reach server: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Unexpected error: {ex.Message}");
        }
    }

    public async Task<List<Artist>> GetArtistsAsync()
    {
        var url = BuildUrl("getArtists.view");
        var json = await _http.GetStringAsync(url);
        var envelope = JsonSerializer.Deserialize<SubsonicEnvelope<ArtistsResponse>>(json, JsonOptions);
        var indices = envelope?.Response?.Artists?.Index ?? new List<ArtistIndex>();

        return indices
            .SelectMany(i => i.Artist)
            .Select(a => new Artist { Id = a.Id, Name = a.Name, AlbumCount = a.AlbumCount })
            .OrderBy(a => a.Name)
            .ToList();
    }

    public async Task<List<Album>> GetAlbumsForArtistAsync(string artistId)
    {
        var url = BuildUrl("getArtist.view", ("id", artistId));
        var json = await _http.GetStringAsync(url);
        var envelope = JsonSerializer.Deserialize<SubsonicEnvelope<ArtistDetailResponse>>(json, JsonOptions);
        var albums = envelope?.Response?.Artist?.Album ?? new List<AlbumDto>();

        return albums
            .Select(a => new Album { Id = a.Id, Name = a.Name, ArtistId = a.ArtistId, ArtistName = a.Artist, SongCount = a.SongCount })
            .ToList();
    }

    // Same endpoint as GetAlbumsForArtistAsync, but also surfaces the artist entity itself
    // (favorite state, cover art) for the artist detail header - callers that only need the
    // album list can keep using the simpler method above.
    public async Task<(Artist Artist, List<Album> Albums)> GetArtistDetailAsync(string artistId)
    {
        var url = BuildUrl("getArtist.view", ("id", artistId));
        var json = await _http.GetStringAsync(url);
        var envelope = JsonSerializer.Deserialize<SubsonicEnvelope<ArtistDetailResponse>>(json, JsonOptions);
        var dto = envelope?.Response?.Artist;
        var albumDtos = dto?.Album ?? new List<AlbumDto>();

        var artist = new Artist
        {
            Id = dto?.Id ?? artistId,
            Name = dto?.Name ?? "",
            AlbumCount = dto?.AlbumCount ?? albumDtos.Count,
            IsFavorited = !string.IsNullOrEmpty(dto?.Starred),
            // Navidrome-generated montage keyed by "coverArt", distinct from the artist's own id
            // (same pattern as Playlist.CoverArtUrl) - falls back to the first album's art below
            // if the server doesn't return one, since not every Subsonic server has this.
            CoverArtUrl = !string.IsNullOrEmpty(dto?.CoverArt) ? GetCoverArtUrl(dto!.CoverArt!, 480)
                : albumDtos.Count > 0 ? GetCoverArtUrl(albumDtos[0].Id, 480) : null
        };

        var albums = MapAlbums(albumDtos);
        return (artist, albums);
    }

    public Task StarArtistAsync(string artistId) =>
        _http.GetStringAsync(BuildUrl("star.view", ("artistId", artistId)));

    public Task UnstarArtistAsync(string artistId) =>
        _http.GetStringAsync(BuildUrl("unstar.view", ("artistId", artistId)));

    public async Task<List<Song>> GetSongsForAlbumAsync(string albumId)
    {
        var url = BuildUrl("getAlbum.view", ("id", albumId));
        var json = await _http.GetStringAsync(url);
        var envelope = JsonSerializer.Deserialize<SubsonicEnvelope<AlbumDetailResponse>>(json, JsonOptions);
        var songs = envelope?.Response?.Album?.Song ?? new List<SongDto>();

        return songs
            .OrderBy(s => s.Track)
            .Select(MapSong)
            .ToList();
    }

    // type: "newest", "alphabeticalByName", "recent", "frequent", etc. (Subsonic getAlbumList2 types).
    public async Task<List<Album>> GetAlbumListAsync(string type, int size = 60)
    {
        var url = BuildUrl("getAlbumList2.view", ("type", type), ("size", size.ToString()));
        var json = await _http.GetStringAsync(url);
        var envelope = JsonSerializer.Deserialize<SubsonicEnvelope<AlbumList2Response>>(json, JsonOptions);
        var albums = envelope?.Response?.AlbumList2?.Album ?? new List<AlbumDto>();

        return MapAlbums(albums);
    }

    public async Task<List<Album>> GetAlbumsByGenreAsync(string genre, int size = 60)
    {
        var url = BuildUrl("getAlbumList2.view", ("type", "byGenre"), ("genre", genre), ("size", size.ToString()));
        var json = await _http.GetStringAsync(url);
        var envelope = JsonSerializer.Deserialize<SubsonicEnvelope<AlbumList2Response>>(json, JsonOptions);
        var albums = envelope?.Response?.AlbumList2?.Album ?? new List<AlbumDto>();

        return MapAlbums(albums);
    }

    private static List<Album> MapAlbums(List<AlbumDto> albums) =>
        albums
            .Select(a => new Album
            {
                Id = a.Id, Name = a.Name, ArtistId = a.ArtistId, ArtistName = a.Artist, SongCount = a.SongCount,
                Genre = a.Genre, Year = a.Year
            })
            .ToList();

    // Ordered by album count so the most substantial genres in the library surface first -
    // there's no server-side "featured/popular" concept for genres, so this is the closest proxy.
    public async Task<List<Genre>> GetGenresAsync()
    {
        var url = BuildUrl("getGenres.view");
        var json = await _http.GetStringAsync(url);
        var envelope = JsonSerializer.Deserialize<SubsonicEnvelope<GenresResponse>>(json, JsonOptions);
        var genres = envelope?.Response?.Genres?.Genre ?? new List<GenreDto>();

        return genres
            .Where(g => !string.IsNullOrWhiteSpace(g.Value) && g.AlbumCount > 0)
            .OrderByDescending(g => g.AlbumCount)
            .Select(g => new Genre { Name = g.Value, AlbumCount = g.AlbumCount, SongCount = g.SongCount })
            .ToList();
    }

    /// <summary>The search bar: artists, albums and songs in the library matching the query.</summary>
    public async Task<(List<Artist> Artists, List<Album> Albums, List<Song> Songs)> SearchAsync(string query,
        int artistCount = 8, int albumCount = 18, int songCount = 30)
    {
        var url = BuildUrl("search3.view", ("query", query), ("artistCount", artistCount.ToString()),
            ("albumCount", albumCount.ToString()), ("songCount", songCount.ToString()));
        var json = await _http.GetStringAsync(url);
        var result = JsonSerializer.Deserialize<SubsonicEnvelope<Search3Response>>(json, JsonOptions)?.Response?.SearchResult3;
        if (result == null)
            return (new(), new(), new());

        var artists = result.Artist.Select(a => new Artist
        {
            Id = a.Id,
            Name = a.Name,
            AlbumCount = a.AlbumCount,
            CoverArtUrl = GetCoverArtUrl(string.IsNullOrEmpty(a.CoverArt) ? a.Id : a.CoverArt, 300),
            IsFavorited = !string.IsNullOrEmpty(a.Starred),
        }).ToList();
        return (artists, MapAlbums(result.Album), result.Song.Select(MapSong).ToList());
    }

    // Library-wide song search. An empty query matches everything on Navidrome; if a
    // different Subsonic-compatible server requires a non-empty pattern, this will need
    // to fall back to iterating albums instead.
    public async Task<List<Song>> GetAllSongsAsync(int pageSize = 500, int max = 20000)
    {
        // Paged: one request used to stop at 500 songs, so most of a bigger library (Luna's has
        // ~3300 tracks) - including anything newly downloaded - never showed on the Tracks page.
        var songs = new List<SongDto>();
        while (songs.Count < max)
        {
            var url = BuildUrl("search3.view", ("query", ""), ("songCount", pageSize.ToString()),
                ("songOffset", songs.Count.ToString()), ("albumCount", "0"), ("artistCount", "0"));
            var json = await _http.GetStringAsync(url);
            var page = JsonSerializer.Deserialize<SubsonicEnvelope<Search3Response>>(json, JsonOptions)?.Response?.SearchResult3?.Song;
            if (page == null || page.Count == 0)
                break;
            songs.AddRange(page);
            if (page.Count < pageSize)
                break;
        }

        return songs
            .OrderBy(s => s.Artist)
            .ThenBy(s => s.Title)
            .Select(MapSong)
            .ToList();
    }

    private static Song MapSong(SongDto s) => new()
    {
        Id = s.Id,
        Title = s.Title,
        AlbumId = s.AlbumId,
        AlbumName = s.Album,
        ArtistId = s.ArtistId,
        ArtistName = s.Artist,
        AlbumArtistName = string.IsNullOrEmpty(s.AlbumArtist) ? s.Artist : s.AlbumArtist,
        Genre = s.Genre,
        Track = s.Track,
        DurationSeconds = s.Duration,
        Year = s.Year,
        PlayCount = s.PlayCount,
        IsFavorited = !string.IsNullOrEmpty(s.Starred),
        CreatedAt = ParseSubsonicDate(s.Created),
        PlayedAt = ParseSubsonicDate(s.Played)
    };

    private static DateTime? ParseSubsonicDate(string? value) =>
        !string.IsNullOrEmpty(value) && DateTime.TryParse(value, out var parsed) ? parsed : null;

    public async Task<List<Playlist>> GetPlaylistsAsync()
    {
        var url = BuildUrl("getPlaylists.view");
        var json = await _http.GetStringAsync(url);
        var envelope = JsonSerializer.Deserialize<SubsonicEnvelope<PlaylistsResponse>>(json, JsonOptions);
        var playlists = envelope?.Response?.Playlists?.Playlist ?? new List<PlaylistDto>();

        return playlists
            .Select(p => new Playlist
            {
                Id = p.Id,
                Name = p.Name,
                SongCount = p.SongCount,
                CoverArtUrl = string.IsNullOrEmpty(p.CoverArt) ? null : GetCoverArtUrl(p.CoverArt, 240)
            })
            .ToList();
    }

    public async Task<List<Song>> GetPlaylistSongsAsync(string playlistId)
    {
        var url = BuildUrl("getPlaylist.view", ("id", playlistId));
        var json = await _http.GetStringAsync(url);
        var envelope = JsonSerializer.Deserialize<SubsonicEnvelope<PlaylistDetailResponse>>(json, JsonOptions);
        var entries = envelope?.Response?.Playlist?.Entry ?? new List<SongDto>();

        return entries.Select(MapSong).ToList();
    }

    // Returns the new playlist's id, or null if the server response didn't include one.
    public async Task<string?> CreatePlaylistAsync(string name)
    {
        var url = BuildUrl("createPlaylist.view", ("name", name));
        var json = await _http.GetStringAsync(url);
        var envelope = JsonSerializer.Deserialize<SubsonicEnvelope<CreatePlaylistResponse>>(json, JsonOptions);
        return envelope?.Response?.Playlist?.Id;
    }

    public Task AddSongToPlaylistAsync(string playlistId, string songId) =>
        _http.GetStringAsync(BuildUrl("updatePlaylist.view", ("playlistId", playlistId), ("songIdToAdd", songId)));

    public Task RemoveSongFromPlaylistAsync(string playlistId, int songIndex) =>
        _http.GetStringAsync(BuildUrl("updatePlaylist.view", ("playlistId", playlistId), ("songIndexToRemove", songIndex.ToString())));

    // Tries the OpenSubsonic per-song lookup first (supports synced/timed lyrics), then falls
    // back to the legacy artist+title endpoint (plain lyrics only) if that finds nothing -
    // covers both older servers and songs that only have plain lyrics on file. Best-effort:
    // most tracks won't have lyrics at all, so any failure just means an empty result.
    // Without time-synced lyrics on the server, LRCLIB (online) is tried as well, like Feishin.
    public async Task<(List<LyricLine> Lines, bool Synced)> GetLyricsAsync(Song song, bool allowOnline = true)
    {
        var server = await GetServerLyricsAsync(song.Id, song.ArtistName, song.Title);
        if (server.Synced || !allowOnline)
            return server;

        var online = await LrcLibClient.GetLyricsAsync(song.ArtistName, song.Title, song.AlbumName, song.DurationSeconds);
        if (online.Synced || server.Lines.Count == 0)
            return online;
        return server;
    }

    private async Task<(List<LyricLine> Lines, bool Synced)> GetServerLyricsAsync(string songId, string artist, string title)
    {
        try
        {
            var url = BuildUrl("getLyricsBySongId.view", ("id", songId));
            var json = await _http.GetStringAsync(url);
            var envelope = JsonSerializer.Deserialize<SubsonicEnvelope<LyricsBySongIdResponse>>(json, JsonOptions);
            var structured = envelope?.Response?.LyricsList?.StructuredLyrics?.FirstOrDefault(l => l.Line.Count > 0);
            if (structured != null)
            {
                var lines = structured.Line
                    .Select(l => new LyricLine { Start = TimeSpan.FromMilliseconds(l.Start), Text = l.Value })
                    .ToList();
                return (lines, structured.Synced);
            }
        }
        catch { /* fall through to the legacy endpoint below */ }

        try
        {
            var url = BuildUrl("getLyrics.view", ("artist", artist), ("title", title));
            var json = await _http.GetStringAsync(url);
            var envelope = JsonSerializer.Deserialize<SubsonicEnvelope<LegacyLyricsResponse>>(json, JsonOptions);
            var text = envelope?.Response?.Lyrics?.Value;
            if (!string.IsNullOrWhiteSpace(text))
            {
                var lines = text.Replace("\r\n", "\n").Split('\n')
                    .Select(line => new LyricLine { Text = line })
                    .ToList();
                return (lines, false);
            }
        }
        catch { /* no lyrics available anywhere */ }

        return (new List<LyricLine>(), false);
    }

    public Task DeletePlaylistAsync(string playlistId) =>
        _http.GetStringAsync(BuildUrl("deletePlaylist.view", ("id", playlistId)));

    public Task StarSongAsync(string songId) =>
        _http.GetStringAsync(BuildUrl("star.view", ("id", songId)));

    public Task UnstarSongAsync(string songId) =>
        _http.GetStringAsync(BuildUrl("unstar.view", ("id", songId)));

    // submission=true reports a completed play (what drives Navidrome's play count / recently
    // played); Subsonic also has a submission=false "now playing" variant, not used here.
    public Task ScrobbleAsync(string songId) =>
        _http.GetStringAsync(BuildUrl("scrobble.view", ("id", songId), ("submission", "true")));

    public string GetStreamUrl(string songId) => BuildUrl("stream.view", ("id", songId));

    /// <summary>
    /// Copies a track's stream (the same URL playback uses, so the same transcoding) into
    /// <paramref name="destination"/> - used to pre-buffer the next queue entry.
    /// </summary>
    public async Task DownloadStreamAsync(string songId, Stream destination, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(GetStreamUrl(songId), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Subsonic reports errors (e.g. missing file) as a 200 with a JSON/XML body, not audio.
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Contains("json") || mediaType.Contains("xml"))
            throw new HttpRequestException($"Server returned {mediaType} instead of audio.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await source.CopyToAsync(destination, cancellationToken);
    }

    // Navidrome resolves cover art for an album, artist, or track ID directly.
    public string GetCoverArtUrl(string id, int size = 300) =>
        BuildUrl("getCoverArt.view", ("id", id), ("size", size.ToString()));

    private string BuildUrl(string endpoint, params (string Key, string Value)[] extraParams)
    {
        var salt = GenerateSalt();
        var token = ComputeToken(_password, salt);

        var sb = new StringBuilder();
        sb.Append(ServerUrl).Append("/rest/").Append(endpoint);
        sb.Append("?u=").Append(Uri.EscapeDataString(Username));
        sb.Append("&t=").Append(token);
        sb.Append("&s=").Append(Uri.EscapeDataString(salt));
        sb.Append("&v=").Append(ApiVersion);
        sb.Append("&c=").Append(ClientId);
        sb.Append("&f=json");

        foreach (var (key, value) in extraParams)
            sb.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value));

        return sb.ToString();
    }

    private static string GenerateSalt()
    {
        var bytes = RandomNumberGenerator.GetBytes(8);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ComputeToken(string password, string salt)
    {
        var input = Encoding.UTF8.GetBytes(password + salt);
        var hash = MD5.HashData(input);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

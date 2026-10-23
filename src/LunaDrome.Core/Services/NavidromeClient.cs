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
/// <summary>Streaming quality (#28), like Spotify's: Original is the file as it is on the server.</summary>
public enum StreamQuality
{
    Original,   // the file as is (FLAC in this library)
    VeryHigh,   // MP3 320 kbit/s
    High,       // Opus 160 kbit/s
    Normal,     // Opus 96 kbit/s
    Low,        // Opus 24 kbit/s
}

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

    /// <summary>
    /// Albums an artist appears on without being their album artist (a featured singer on
    /// someone else's album). Navidrome's getArtist lists those only with
    /// Subsonic.ArtistParticipations on, so they're found from the artist's songs instead: a
    /// search for the name, keeping songs that list this artist (by id, so a namesake doesn't
    /// count), grouped by album. SongCount is how many of the album's songs they're on.
    /// </summary>
    public async Task<List<Album>> GetAppearsOnAlbumsAsync(string artistId, string artistName, ISet<string> knownAlbumIds,
        int pageSize = 500, int max = 3000)
    {
        var songs = new List<SongDto>();
        while (songs.Count < max && !string.IsNullOrWhiteSpace(artistName))
        {
            var url = BuildUrl("search3.view", ("query", artistName), ("songCount", pageSize.ToString()),
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
            .Where(s => !knownAlbumIds.Contains(s.AlbumId) && !string.IsNullOrEmpty(s.AlbumId)
                        && (s.ArtistId == artistId || s.Artists?.Any(a => a.Id == artistId) == true))
            .GroupBy(s => s.AlbumId)
            .Select(g => new Album
            {
                Id = g.Key,
                Name = g.First().Album,
                ArtistId = g.First().AlbumArtists?.FirstOrDefault()?.Id ?? "",
                ArtistName = g.First().AlbumArtist ?? g.First().Artist,
                Year = g.First().Year,
                SongCount = g.Count(),
            })
            .ToList();
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
        Artists = s.Artists is { Count: > 1 } list
            ? list.Where(a => !string.IsNullOrEmpty(a.Id)).Select(a => new ArtistRef(a.Id, a.Name)).ToList()
            : [],
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
                CoverArtId = string.IsNullOrEmpty(p.CoverArt) ? null : p.CoverArt,
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

        // PlaylistIndex = position on the server: what "remove from playlist" needs (it was lost
        // in the switch to the Uno app, so removing silently did nothing).
        return entries.Select((entry, index) =>
        {
            var song = MapSong(entry);
            song.PlaylistIndex = index;
            return song;
        }).ToList();
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

    public Task RenamePlaylistAsync(string playlistId, string name) =>
        _http.GetStringAsync(BuildUrl("updatePlaylist.view", ("playlistId", playlistId), ("name", name)));

    /// <summary>Replaces the playlist's songs with <paramref name="songIds"/> in that order (how a
    /// reorder is saved - Subsonic has no "move"). createPlaylist with a playlistId overwrites
    /// it; the ids go in a form body (OpenSubsonic formPost), since a long playlist's ids would
    /// make the URL too long for a reverse proxy.</summary>
    public async Task SetPlaylistSongsAsync(string playlistId, IEnumerable<string> songIds)
    {
        var form = new FormUrlEncodedContent(songIds.Select(id => new KeyValuePair<string, string>("songId", id)));
        using var response = await _http.PostAsync(BuildUrl("createPlaylist.view", ("playlistId", playlistId)), form);
        response.EnsureSuccessStatusCode();
        var envelope = JsonSerializer.Deserialize<SubsonicEnvelope<CreatePlaylistResponse>>(
            await response.Content.ReadAsStringAsync(), JsonOptions);
        if (envelope?.Response?.Status != "ok")
            throw new InvalidOperationException(envelope?.Response?.Error?.Message ?? "The server didn't save the new order.");
    }

    /// <summary>Songs like this song or artist (Navidrome asks Last.fm and keeps the ones in the
    /// library). Can be empty or short for less known music.</summary>
    public async Task<List<Song>> GetSimilarSongsAsync(string songOrArtistId, int count = 50)
    {
        var json = await _http.GetStringAsync(BuildUrl("getSimilarSongs2.view", ("id", songOrArtistId), ("count", count.ToString())));
        var songs = JsonSerializer.Deserialize<SubsonicEnvelope<SimilarSongs2Response>>(json, JsonOptions)?.Response?.SimilarSongs2?.Song;
        return (songs ?? new List<SongDto>()).Select(MapSong).ToList();
    }

    public async Task<List<Song>> GetRandomSongsAsync(string? genre, int count = 50)
    {
        var args = new List<(string, string)> { ("size", count.ToString()) };
        if (!string.IsNullOrEmpty(genre))
            args.Add(("genre", genre));
        var json = await _http.GetStringAsync(BuildUrl("getRandomSongs.view", args.ToArray()));
        var songs = JsonSerializer.Deserialize<SubsonicEnvelope<RandomSongsResponse>>(json, JsonOptions)?.Response?.RandomSongs?.Song;
        return (songs ?? new List<SongDto>()).Select(MapSong).ToList();
    }

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

    private readonly Dictionary<string, PublicCover> _publicCovers = new();
    private readonly Dictionary<string, bool> _httpsHosts = new();

    /// <summary>
    /// An album cover anyone can load without logging in, for places outside LunaDrome (Discord's
    /// servers fetch the image themselves): Navidrome's /share/img link from getAlbumInfo2, whose
    /// token only unlocks that one image. Null when there is none, or when the server is only on
    /// this network (nobody outside could load it anyway); <see cref="PublicCover.WhyNot"/> then
    /// says why. Looked up once per album.
    /// </summary>
    public async Task<PublicCover> GetPublicCoverAsync(string albumId)
    {
        if (string.IsNullOrEmpty(albumId))
            return new(null, "the song has no album");
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var server) || IsLocalOnly(server))
            return new(null, $"the server address ({server?.Host ?? ServerUrl}) is only reachable on this network");
        lock (_publicCovers)
            if (_publicCovers.TryGetValue(albumId, out var known))
                return known;

        PublicCover cover;
        try
        {
            var json = await _http.GetStringAsync(BuildUrl("getAlbumInfo2.view", ("id", albumId)));
            var info = JsonSerializer.Deserialize<SubsonicEnvelope<AlbumInfoResponse>>(json, JsonOptions)?.Response?.AlbumInfo;
            var link = info?.MediumImageUrl ?? info?.LargeImageUrl ?? info?.SmallImageUrl;
            if (!Uri.TryCreate(link, UriKind.Absolute, out var url) || !url.AbsolutePath.Contains("/share/img/", StringComparison.Ordinal))
                cover = new(null, "the server gives no public cover link (Navidrome 0.53+ needed)");
            else if (await ToHttpsAsync(url, server) is { } https)
                cover = new(https.AbsoluteUri, null);
            else
                cover = new(null, $"the server doesn't answer on https at {url.Host} (Discord only loads https images)");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(null, $"looking it up failed: {ex.Message}");   // not remembered: may work next time
        }
        lock (_publicCovers)
            _publicCovers[albumId] = cover;
        return cover;
    }

    // Behind a reverse proxy Navidrome writes http:// links. When the user signed in over https the
    // host answers on https; when they signed in over http it may still (a proxy with a
    // certificate) - asked once per host.
    private async Task<Uri?> ToHttpsAsync(Uri url, Uri server)
    {
        if (url.Scheme == Uri.UriSchemeHttps)
            return url;
        var sameHost = string.Equals(url.Host, server.Host, StringComparison.OrdinalIgnoreCase);
        if (sameHost && server.Scheme == Uri.UriSchemeHttps)
            return new UriBuilder(url) { Scheme = Uri.UriSchemeHttps, Port = server.Port }.Uri;
        var https = new UriBuilder(url) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        bool? known;
        lock (_httpsHosts)
            known = _httpsHosts.TryGetValue(url.Host, out var k) ? k : null;
        var works = known ?? false;
        if (known == null)
        {
            try
            {
                using var probe = new HttpRequestMessage(HttpMethod.Get, https);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                using var response = await _http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                works = response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType?.StartsWith("image/") == true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                works = false;
            }
            lock (_httpsHosts)
                _httpsHosts[url.Host] = works;
        }
        return works ? https : null;
    }

    private static bool IsLocalOnly(Uri server)
    {
        if (server.IsLoopback || server.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !server.Host.Contains('.'))
            return true;
        if (!System.Net.IPAddress.TryParse(server.Host, out var ip))
            return false;
        var b = ip.GetAddressBytes();
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] < 128)
            : ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
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

    /// <summary>A public link to a song on the Navidrome server (Feishin's "Share"): anyone with it
    /// can play the song in a browser, without an account. Needs sharing turned on in Navidrome
    /// (ND_ENABLESHARING); <paramref name="expires"/> null = the server's default (one year).</summary>
    /// <exception cref="InvalidOperationException">The server refused (e.g. sharing is off).</exception>
    public async Task<string> CreateShareAsync(string songId, string? description, DateTimeOffset? expires)
    {
        var args = new List<(string, string)> { ("id", songId) };
        if (!string.IsNullOrWhiteSpace(description))
            args.Add(("description", description.Trim()));
        if (expires is { } at)
            args.Add(("expires", at.ToUnixTimeMilliseconds().ToString()));
        var json = await _http.GetStringAsync(BuildUrl("createShare.view", args.ToArray()));
        var resp = JsonSerializer.Deserialize<SubsonicEnvelope<SharesResponse>>(json, JsonOptions)?.Response;
        if (resp?.Status != "ok")
            throw new InvalidOperationException(resp?.Error?.Message ?? "Unexpected response from server.");
        return resp.Shares?.Share.FirstOrDefault()?.Url is { Length: > 0 } link
            ? MatchServerScheme(link)
            : throw new InvalidOperationException("The server didn't return a link.");
    }

    // Navidrome builds the link from the request it sees, so behind a proxy that talks plain http to
    // it the link starts with http:// even though the server is reached over https.
    private string MatchServerScheme(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var shareUri) || !Uri.TryCreate(ServerUrl, UriKind.Absolute, out var server)
            || shareUri.Scheme != Uri.UriSchemeHttp || server.Scheme != Uri.UriSchemeHttps
            || !string.Equals(shareUri.Host, server.Host, StringComparison.OrdinalIgnoreCase))
            return link;
        return new UriBuilder(shareUri) { Scheme = Uri.UriSchemeHttps, Port = server.Port }.Uri.ToString();
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

    /// <summary>Quality for playback, prefetch and downloads (#28). Navidrome converts on the fly
    /// (ffmpeg on the server) for anything but Original.</summary>
    public StreamQuality StreamQuality { get; set; } = StreamQuality.Original;

    /// <param name="forPlayback">Adds estimateContentLength for a converted stream: without it
    /// there's no Content-Length and no byte ranges, so the player can't seek. Downloads must not
    /// use it: on a song's first conversion the estimate is too big, and an HTTP client then fails
    /// with "response ended prematurely" when the real (shorter) stream ends.</param>
    public string GetStreamUrl(string songId, bool forPlayback = true) => StreamQuality switch
    {
        StreamQuality.VeryHigh => Converted(songId, "mp3", 320, forPlayback),
        StreamQuality.High => Converted(songId, "opus", 160, forPlayback),
        StreamQuality.Normal => Converted(songId, "opus", 96, forPlayback),
        StreamQuality.Low => Converted(songId, "opus", 24, forPlayback),
        _ => BuildUrl("stream.view", ("id", songId)),
    };

    /// <summary>A stream for another device to fetch (a DLNA renderer): the same quality, but MP3
    /// instead of Opus, which few TVs and receivers play. Original stays the original file.
    /// Renderers can't seek in a converted stream, so it starts at <paramref name="from"/>
    /// instead (Subsonic's timeOffset) and Converted says a seek means fetching it again.</summary>
    public (string Url, string MimeType, bool Converted) GetDeviceStreamUrl(string songId, TimeSpan from = default)
    {
        var kbps = StreamQuality switch
        {
            StreamQuality.VeryHigh or StreamQuality.High => 320,
            StreamQuality.Normal => 192,
            StreamQuality.Low => 128,
            _ => 0,
        };
        if (kbps == 0)
            return (BuildUrl("stream.view", ("id", songId)), "*", false);
        // No estimateContentLength: from an offset the estimate (for the whole song) would be
        // too long, and the renderer would wait at the end for bytes that never come.
        var offset = (int)from.TotalSeconds;
        var url = offset > 0
            ? BuildUrl("stream.view", ("id", songId), ("format", "mp3"), ("maxBitRate", kbps.ToString()), ("timeOffset", offset.ToString()))
            : BuildUrl("stream.view", ("id", songId), ("format", "mp3"), ("maxBitRate", kbps.ToString()));
        return (url, "audio/mpeg", true);
    }

    private string Converted(string songId, string format, int kbps, bool estimateLength) => estimateLength
        ? BuildUrl("stream.view", ("id", songId), ("format", format), ("maxBitRate", kbps.ToString()),
                   ("estimateContentLength", "true"))
        : BuildUrl("stream.view", ("id", songId), ("format", format), ("maxBitRate", kbps.ToString()));

    /// <summary>
    /// Copies a track's stream (the same URL playback uses, so the same transcoding) into
    /// <paramref name="destination"/> - used to pre-buffer the next queue entry.
    /// </summary>
    public async Task DownloadStreamAsync(string songId, Stream destination, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(GetStreamUrl(songId, forPlayback: false), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

    /// <summary>A fresh Subsonic token + salt for this account - lets another service (JamiDrome)
    /// check the login with Navidrome without the password leaving this device.</summary>
    public (string Token, string Salt) CreateAuthToken()
    {
        var salt = GenerateSalt();
        return (ComputeToken(_password, salt), salt);
    }

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

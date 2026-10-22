using System.Text.Json.Serialization;

namespace LunaDrome.Core.Services;

// Raw JSON shapes returned by the Subsonic API (as implemented by Navidrome).
// These mirror the wire format; NavidromeClient maps them to the app's own Models.

internal class SubsonicEnvelope<T>
{
    [JsonPropertyName("subsonic-response")]
    public T? Response { get; set; }
}

internal class SubsonicResponseBase
{
    public string Status { get; set; } = "";
    public SubsonicError? Error { get; set; }
}

internal class SubsonicError
{
    public int Code { get; set; }
    public string Message { get; set; } = "";
}

internal class PingResponse : SubsonicResponseBase
{
}

internal class AlbumInfoResponse : SubsonicResponseBase
{
    public AlbumInfoDto? AlbumInfo { get; set; }
}

internal class AlbumInfoDto
{
    public string? SmallImageUrl { get; set; }
    public string? MediumImageUrl { get; set; }
    public string? LargeImageUrl { get; set; }
}

internal class ArtistsResponse : SubsonicResponseBase
{
    public ArtistsPayload? Artists { get; set; }
}

internal class ArtistsPayload
{
    public List<ArtistIndex> Index { get; set; } = new();
}

internal class ArtistIndex
{
    public string Name { get; set; } = "";
    public List<ArtistDto> Artist { get; set; } = new();
}

internal class ArtistDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int AlbumCount { get; set; }
    public string? CoverArt { get; set; }
    public string? Starred { get; set; }
}

internal class ArtistDetailResponse : SubsonicResponseBase
{
    public ArtistDetailDto? Artist { get; set; }
}

internal class ArtistDetailDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int AlbumCount { get; set; }
    public string? CoverArt { get; set; }
    public string? Starred { get; set; }
    public List<AlbumDto> Album { get; set; } = new();
}

internal class AlbumDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ArtistId { get; set; } = "";
    public string Artist { get; set; } = "";
    public int SongCount { get; set; }
    public string? Genre { get; set; }
    public int? Year { get; set; }
}

internal class AlbumList2Response : SubsonicResponseBase
{
    public AlbumList2Payload? AlbumList2 { get; set; }
}

internal class AlbumList2Payload
{
    public List<AlbumDto> Album { get; set; } = new();
}

internal class Search3Response : SubsonicResponseBase
{
    public Search3Payload? SearchResult3 { get; set; }
}

internal class Search3Payload
{
    public List<SongDto> Song { get; set; } = new();
    public List<AlbumDto> Album { get; set; } = new();
    public List<ArtistDto> Artist { get; set; } = new();
}

internal class AlbumDetailResponse : SubsonicResponseBase
{
    public AlbumDetailDto? Album { get; set; }
}

internal class AlbumDetailDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<SongDto> Song { get; set; } = new();
}

internal class ArtistRefDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

internal class SongDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string AlbumId { get; set; } = "";
    public string Album { get; set; } = "";
    public string ArtistId { get; set; } = "";
    public string Artist { get; set; } = "";
    /// <summary>OpenSubsonic: each artist separately (Navidrome 0.55+).</summary>
    public List<ArtistRefDto>? Artists { get; set; }
    public string? AlbumArtist { get; set; }
    public List<ArtistRefDto>? AlbumArtists { get; set; }
    public string Genre { get; set; } = "";
    public int Track { get; set; }
    public int Duration { get; set; }
    public int? Year { get; set; }
    public int PlayCount { get; set; }
    // Subsonic returns this as an ISO-8601 timestamp when the track is starred, and omits it otherwise.
    public string? Starred { get; set; }
    public string? Created { get; set; }
    public string? Played { get; set; }
}

internal class PlaylistsResponse : SubsonicResponseBase
{
    public PlaylistsPayload? Playlists { get; set; }
}

internal class PlaylistsPayload
{
    public List<PlaylistDto> Playlist { get; set; } = new();
}

internal class PlaylistDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int SongCount { get; set; }
    // Navidrome generates a montage from the playlist's own tracks; pass this straight to
    // getCoverArt.view. Not the same value as Id, and absent server-side for empty playlists.
    public string? CoverArt { get; set; }
}

internal class PlaylistDetailResponse : SubsonicResponseBase
{
    public PlaylistDetailDto? Playlist { get; set; }
}

internal class PlaylistDetailDto : PlaylistDto
{
    public List<SongDto> Entry { get; set; } = new();
}

internal class CreatePlaylistResponse : SubsonicResponseBase
{
    public PlaylistDetailDto? Playlist { get; set; }
}

internal class SharesResponse : SubsonicResponseBase
{
    public SharesPayload? Shares { get; set; }
}

internal class SharesPayload
{
    public List<ShareDto> Share { get; set; } = new();
}

internal class ShareDto
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
}

internal class GenresResponse : SubsonicResponseBase
{
    public GenresPayload? Genres { get; set; }
}

internal class GenresPayload
{
    public List<GenreDto> Genre { get; set; } = new();
}

internal class GenreDto
{
    public string Value { get; set; } = "";
    public int SongCount { get; set; }
    public int AlbumCount { get; set; }
}

// OpenSubsonic extension, per-song ID lookup - supports synced (timed) lyrics.
internal class LyricsBySongIdResponse : SubsonicResponseBase
{
    public LyricsListPayload? LyricsList { get; set; }
}

internal class LyricsListPayload
{
    public List<StructuredLyricsDto> StructuredLyrics { get; set; } = new();
}

internal class StructuredLyricsDto
{
    public bool Synced { get; set; }
    public List<LyricLineDto> Line { get; set; } = new();
}

internal class LyricLineDto
{
    public long Start { get; set; }
    public string Value { get; set; } = "";
}

// Legacy Subsonic endpoint, artist+title lookup - plain unsynced lyrics only. Used as a
// fallback when the OpenSubsonic extension above finds nothing (older servers, or a song
// with only plain lyrics on file).
internal class LegacyLyricsResponse : SubsonicResponseBase
{
    public LegacyLyricsPayload? Lyrics { get; set; }
}

internal class LegacyLyricsPayload
{
    public string? Value { get; set; }
}

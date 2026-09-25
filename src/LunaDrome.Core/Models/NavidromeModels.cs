namespace LunaDrome.Core.Models;

public class Artist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int AlbumCount { get; set; }
    public string? CoverArtUrl { get; set; }
    public bool IsFavorited { get; set; }
}

public class Album
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ArtistId { get; set; } = "";
    public string ArtistName { get; set; } = "";
    public int SongCount { get; set; }
    public string? CoverArtUrl { get; set; }
    public string? Genre { get; set; }
    public int? Year { get; set; }
}

public class Genre
{
    public string Name { get; set; } = "";
    public int AlbumCount { get; set; }
    public int SongCount { get; set; }
}

public class Song
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string AlbumId { get; set; } = "";
    public string AlbumName { get; set; } = "";
    public string ArtistId { get; set; } = "";
    public string ArtistName { get; set; } = "";
    public string AlbumArtistName { get; set; } = "";
    public string Genre { get; set; } = "";
    public int Track { get; set; }
    public int DurationSeconds { get; set; }
    public string? CoverArtUrl { get; set; }
    public int? Year { get; set; }
    public int PlayCount { get; set; }
    public bool IsFavorited { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? PlayedAt { get; set; }

    // Only meaningful when this Song came from GetPlaylistSongsAsync: its position within
    // that playlist, needed by Subsonic's updatePlaylist songIndexToRemove. -1 otherwise.
    public int PlaylistIndex { get; set; } = -1;

    public string DurationDisplay => $"{DurationSeconds / 60}:{DurationSeconds % 60:D2}";

    public Song Clone() => (Song)MemberwiseClone();
}

public class LyricLine
{
    public TimeSpan Start { get; set; }
    public string Text { get; set; } = "";
}

public class Playlist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int SongCount { get; set; }
    public string? CoverArtUrl { get; set; }
}

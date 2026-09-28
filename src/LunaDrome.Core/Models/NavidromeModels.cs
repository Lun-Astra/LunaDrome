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

    /// <summary>"12 albums · 140 songs" (Genres page tiles).</summary>
    public string Summary =>
        $"{AlbumCount} album{(AlbumCount == 1 ? "" : "s")} · {SongCount} song{(SongCount == 1 ? "" : "s")}";
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

public class LyricLine : System.ComponentModel.INotifyPropertyChanged
{
    public TimeSpan Start { get; set; }
    public string Text { get; set; } = "";

    private bool _isActive, _isDimmed;

    /// <summary>The line being sung right now (synced lyrics only) - shown highlighted.</summary>
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive != value) { _isActive = value; Changed(nameof(IsActive)); Changed(nameof(NormalOpacity)); } }
    }

    /// <summary>Synced lyrics dim every line but the active one; plain lyrics stay fully readable.</summary>
    public bool IsDimmed
    {
        get => _isDimmed;
        set { if (_isDimmed != value) { _isDimmed = value; Changed(nameof(NormalOpacity)); } }
    }

    /// <summary>Opacity of the normal rendition (the highlighted one takes over while active).</summary>
    public double NormalOpacity => _isActive ? 0 : _isDimmed ? 0.5 : 1;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public class Playlist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int SongCount { get; set; }
    public string? CoverArtUrl { get; set; }
}

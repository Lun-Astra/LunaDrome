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

public class Song : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string AlbumId { get; set; } = "";
    public string AlbumName { get; set; } = "";
    public string ArtistId { get; set; } = "";
    public string ArtistName { get; set; } = "";
    /// <summary>Each artist of the song separately (Navidrome's OpenSubsonic "artists"), when it
    /// has several - "Jeff Williams, Casey Lee Williams" is then two entries. Empty otherwise.</summary>
    public IReadOnlyList<ArtistRef> Artists { get; set; } = [];
    public string AlbumArtistName { get; set; } = "";
    public string Genre { get; set; } = "";
    public int Track { get; set; }
    public int DurationSeconds { get; set; }
    public string? CoverArtUrl { get; set; }
    public int? Year { get; set; }
    public int PlayCount { get; set; }
    private bool _isFavorited;

    /// <summary>Notifies, so a heart in a (recycled) list row follows a toggle made anywhere.</summary>
    public bool IsFavorited
    {
        get => _isFavorited;
        set
        {
            if (_isFavorited == value)
                return;
            _isFavorited = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsFavorited)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public DateTime? CreatedAt { get; set; }
    public DateTime? PlayedAt { get; set; }

    // Only meaningful when this Song came from GetPlaylistSongsAsync: its position within
    // that playlist, needed by Subsonic's updatePlaylist songIndexToRemove. -1 otherwise.
    public int PlaylistIndex { get; set; } = -1;

    public string DurationDisplay => $"{DurationSeconds / 60}:{DurationSeconds % 60:D2}";

    public Song Clone()
    {
        var copy = (Song)MemberwiseClone();
        copy.PropertyChanged = null;   // the copy doesn't notify the original's rows
        return copy;
    }
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
    public string? CoverArtId { get; set; }
}

/// <summary>One artist of a song: enough to open the artist's page.</summary>
public sealed record ArtistRef(string Id, string Name);

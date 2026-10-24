using LunaDrome.Controls;
using LunaDrome.Core.Models;
using Microsoft.UI.Xaml.Shapes;

namespace LunaDrome;

// Listening stats, built from what Navidrome keeps per song: how often it was played and when it
// was last played. There's no play history, so the tops are all-time; "recently played" is the
// last-played date. Built in code - it's one read-only page of computed rows.
public sealed partial class MainPage
{
    private int _statsLoad;

    private async Task LoadStatsAsync()
    {
        var load = ++_statsLoad;
        StatsContent.Children.Clear();
        StatsContent.Children.Add(StatsNote("Counting your plays…"));

        List<Song> songs;
        try
        {
            songs = await _client.GetAllSongsAsync();
        }
        catch (Exception ex)
        {
            if (load == _statsLoad)
            {
                StatsContent.Children.Clear();
                StatsContent.Children.Add(StatsNote($"Couldn't load the library: {ex.Message}"));
            }
            return;
        }
        if (load != _statsLoad)
            return;   // opened again meanwhile
        PopulateSongArt(songs);
        BuildStats(songs);
    }

    private void BuildStats(List<Song> library)
    {
        StatsContent.Children.Clear();
        var played = library.Where(s => s.PlayCount > 0).ToList();
        if (played.Count == 0)
        {
            StatsContent.Children.Add(StatsNote("Nothing played yet - your stats show up here once you start listening."));
            return;
        }

        var plays = played.Sum(s => s.PlayCount);
        var seconds = played.Sum(s => (long)s.PlayCount * s.DurationSeconds);
        var artists = played
            .SelectMany(s => s.Artists.Count > 0
                ? s.Artists.Select(a => (a.Id, a.Name, s.PlayCount))
                : new[] { (Id: s.ArtistId, Name: s.ArtistName, s.PlayCount) })
            .Where(a => !string.IsNullOrEmpty(a.Name))
            .GroupBy(a => a.Id)
            .Select(g => (g.Key, Name: g.First().Name, Plays: g.Sum(a => a.PlayCount)))
            .OrderByDescending(a => a.Plays)
            .ToList();

        // Tiles
        var tiles = new Grid { ColumnSpacing = 12, RowSpacing = 12, Margin = new Thickness(0, 0, 0, 28) };
        var tileData = new[]
        {
            ("PLAYS", plays.ToString("N0"), "all time"),
            ("LISTENING TIME", FormatListeningTime(seconds), $"≈ {seconds / 3600.0 / 24:0.#} days of music"),
            ("SONGS PLAYED", played.Count.ToString("N0"), $"of {library.Count:N0} ({100.0 * played.Count / library.Count:0}%)"),
            ("ARTISTS", artists.Count.ToString("N0"), "with at least one play"),
        };
        var columns = IsCompact ? 2 : 4;
        for (var c = 0; c < columns; c++)
            tiles.ColumnDefinitions.Add(new ColumnDefinition());
        for (var r = 0; r < (tileData.Length + columns - 1) / columns; r++)
            tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < tileData.Length; i++)
        {
            var tile = StatTile(tileData[i].Item1, tileData[i].Item2, tileData[i].Item3);
            Grid.SetColumn(tile, i % columns);
            Grid.SetRow(tile, i / columns);
            tiles.Children.Add(tile);
        }
        StatsContent.Children.Add(tiles);

        // Top artists + top songs side by side on a wide window
        var topArtists = artists.Take(10).ToList();
        var artistRows = StatsSection("TOP ARTISTS");
        foreach (var (artist, i) in topArtists.Select((a, i) => (a, i)))
            artistRows.Children.Add(BarRow(i + 1, artist.Name, null, artist.Plays, topArtists[0].Plays,
                () => _ = NavigateToArtistAsync(artist.Key, artist.Name)));

        var topSongs = played.OrderByDescending(s => s.PlayCount).ThenBy(s => s.Title).Take(10).ToList();
        var songRows = StatsSection("TOP SONGS");
        foreach (var (song, i) in topSongs.Select((s, i) => (s, i)))
            songRows.Children.Add(BarRow(i + 1, song.Title, song.CoverArtUrl, song.PlayCount, topSongs[0].PlayCount,
                () => PlayQueueFrom(topSongs, i), song.ArtistName));

        StatsContent.Children.Add(SideBySide(artistRows, songRows));

        // Top albums as cards
        var topAlbums = played
            .Where(s => !string.IsNullOrEmpty(s.AlbumId))
            .GroupBy(s => s.AlbumId)
            .Select(g => (Song: g.First(), Plays: g.Sum(s => s.PlayCount)))
            .OrderByDescending(a => a.Plays)
            .Take(IsCompact ? 6 : 8)
            .ToList();
        var albums = StatsSection("TOP ALBUMS");
        var cards = new Grid { ColumnSpacing = 14, RowSpacing = 14 };
        var perRow = IsCompact ? 2 : 4;
        for (var c = 0; c < perRow; c++)
            cards.ColumnDefinitions.Add(new ColumnDefinition());
        for (var r = 0; r < (topAlbums.Count + perRow - 1) / perRow; r++)
            cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var ((song, albumPlays), i) in topAlbums.Select((a, i) => (a, i)))
        {
            var album = new Album
            {
                Id = song.AlbumId, Name = song.AlbumName, ArtistId = song.ArtistId, ArtistName = song.AlbumArtistName,
                CoverArtUrl = song.CoverArtUrl
            };
            var card = AlbumStatCard(album, albumPlays);
            Grid.SetColumn(card, i % perRow);
            Grid.SetRow(card, i / perRow);
            cards.Children.Add(card);
        }
        albums.Children.Add(cards);
        albums.Margin = new Thickness(0, 0, 0, 28);
        StatsContent.Children.Add(albums);

        // Genres + recently played
        var genres = played
            .Where(s => !string.IsNullOrWhiteSpace(s.Genre))
            .GroupBy(s => s.Genre, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First().Genre, Plays: g.Sum(s => s.PlayCount)))
            .OrderByDescending(g => g.Plays)
            .Take(8)
            .ToList();
        var genreRows = StatsSection("TOP GENRES");
        foreach (var (genre, i) in genres.Select((g, i) => (g, i)))
            genreRows.Children.Add(BarRow(i + 1, genre.Name, null, genre.Plays, genres[0].Plays,
                () => _ = OpenGenreAsync(new Genre { Name = genre.Name })));

        var recent = played.Where(s => s.PlayedAt != null).OrderByDescending(s => s.PlayedAt).Take(8).ToList();
        var recentRows = StatsSection("RECENTLY PLAYED");
        foreach (var (song, i) in recent.Select((s, i) => (s, i)))
            recentRows.Children.Add(BarRow(null, song.Title, song.CoverArtUrl, null, 0,
                () => PlayQueueFrom(recent, i), $"{song.ArtistName} · {Ago(song.PlayedAt!.Value)}"));

        StatsContent.Children.Add(SideBySide(genreRows, recentRows));
        StatsContent.Children.Add(StatsNote(
            "Navidrome keeps how often and when each song was last played, not a full history - so these are all-time numbers."));
    }

    private static string FormatListeningTime(long seconds)
    {
        var hours = seconds / 3600;
        return hours >= 1 ? $"{hours:N0} h {seconds % 3600 / 60} m" : $"{seconds / 60} m";
    }

    private static string Ago(DateTime when)
    {
        var span = DateTime.Now - when.ToLocalTime();
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : span.TotalDays < 2 ? "yesterday"
            : span.TotalDays < 30 ? $"{(int)span.TotalDays} days ago"
            : when.ToLocalTime().ToString("d MMM yyyy");
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private static TextBlock StatsNote(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = Res("TextSecondaryBrush"), TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 4, 0, 16)
    };

    private static StackPanel StatsSection(string title)
    {
        var section = new StackPanel { Spacing = 2 };
        section.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SectionHeaderStyle"] });
        return section;
    }

    private UIElement SideBySide(FrameworkElement left, FrameworkElement right)
    {
        if (IsCompact)
        {
            left.Margin = right.Margin = new Thickness(0, 0, 0, 28);
            var stack = new StackPanel();
            stack.Children.Add(left);
            stack.Children.Add(right);
            return stack;
        }
        var grid = new Grid { ColumnSpacing = 32, Margin = new Thickness(0, 0, 0, 28) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private static Border StatTile(string label, string value, string detail)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Res("TextSecondaryBrush") });
        panel.Children.Add(new TextBlock { Text = value, FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = Res("TextPrimaryBrush") });
        panel.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = Res("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        return new Border { Background = Res("PanelBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 16, 18, 16), Child = panel };
    }

    // A ranked row: # · (cover) · name (+ subtitle) · play count, with a bar behind the count
    // scaled to the top entry. Tap = action. Count null = no bar (recently played).
    private Border BarRow(int? rank, string name, string? coverUrl, int? count, int max, Action onTap, string? subtitle = null)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 6, 10, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(rank == null ? 0 : 22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(count == null ? 0 : 90) });

        if (rank != null)
            grid.Children.Add(new TextBlock
            {
                Text = rank.Value.ToString(), FontSize = 13, Foreground = Res("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right
            });

        if (coverUrl != null)
        {
            var cover = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(4), Background = Res("RaisedBrush") };
            var art = new Border { CornerRadius = new CornerRadius(4) };
            SetImage(art, coverUrl);
            var coverBox = new Grid();
            coverBox.Children.Add(cover);
            coverBox.Children.Add(art);
            Grid.SetColumn(coverBox, 1);
            grid.Children.Add(coverBox);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("TextPrimaryBrush") });
        if (subtitle != null)
            text.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("TextSecondaryBrush") });
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        if (count != null)
        {
            // Bar: two star columns, count : (max - count), so no measuring is needed.
            var bar = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 34, 0) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(count.Value, 0.01), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(max - count.Value, 0.01), GridUnitType.Star) });
            var track = new Rectangle { RadiusX = 3, RadiusY = 3, Fill = Res("RaisedBrush") };
            Grid.SetColumnSpan(track, 2);
            bar.Children.Add(track);
            bar.Children.Add(new Rectangle { RadiusX = 3, RadiusY = 3, Fill = Res("TextSecondaryBrush") });
            var countText = new TextBlock
            {
                Text = count.Value.ToString("N0"), FontSize = 12, Foreground = Res("TextSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            var countBox = new Grid();
            countBox.Children.Add(bar);
            countBox.Children.Add(countText);
            Grid.SetColumn(countBox, 3);
            grid.Children.Add(countBox);
        }

        var row = new Border { CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Child = grid };
        Motion.SetHoverBackground(row, Res("RaisedBrush"));
        Motion.SetHandCursor(row);
        row.Tapped += (_, e) =>
        {
            e.Handled = true;
            onTap();
        };
        return row;
    }

    private Border AlbumStatCard(Album album, int plays)
    {
        var art = new Border { CornerRadius = new CornerRadius(6) };
        SetImage(art, album.CoverArtUrl);
        var artBox = new Grid();
        artBox.SizeChanged += (_, e) => artBox.Height = e.NewSize.Width;   // square, like the other album cards
        artBox.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = Res("RaisedBrush") });
        artBox.Children.Add(art);

        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(artBox);
        panel.Children.Add(new TextBlock { Text = album.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("TextPrimaryBrush") });
        panel.Children.Add(new TextBlock { Text = $"{album.ArtistName} · {plays:N0} plays", FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("TextSecondaryBrush") });

        var card = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Child = panel };
        Motion.SetHoverScale(card, 1.03);
        Motion.SetHandCursor(card);
        card.Tapped += (_, e) =>
        {
            e.Handled = true;
            _ = OpenAlbumAsync(album);
        };
        return card;
    }
}

using LunaDrome.Core.Models;

namespace LunaDrome;

// Song / artist radio: a queue of similar songs from the library. Navidrome's getSimilarSongs2
// asks Last.fm and keeps what's in the library - plenty for well-known music, but it can come
// back short (David Bowie: 1 song), so it's topped up with songs like the artist and then
// random songs from the same genre.
public sealed partial class MainPage
{
    private const int RadioLength = 50;

    private async Task StartSongRadioAsync(Song seed)
    {
        ShowToast($"Starting radio for “{seed.Title}”…");
        try
        {
            var picks = new List<Song>();
            AddRadioSongs(picks, await _client.GetSimilarSongsAsync(seed.Id, 60), seed.Id);
            if (picks.Count < RadioLength / 2 && !string.IsNullOrEmpty(seed.ArtistId))
                AddRadioSongs(picks, await _client.GetSimilarSongsAsync(seed.ArtistId, 40), seed.Id);
            if (picks.Count < RadioLength / 2)
                AddRadioSongs(picks, await _client.GetRandomSongsAsync(seed.Genre, 40), seed.Id);

            var queue = new List<Song> { seed };
            queue.AddRange(picks.OrderBy(_ => _random.Next()).Take(RadioLength - 1));
            PopulateSongArt(queue);
            PlayQueueFrom(queue, 0);
            ShowToast(picks.Count == 0 ? "No similar songs found - playing just this one" : $"Radio: {queue.Count} songs like “{seed.Title}”");
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Couldn't start the radio: {ex.Message}");
        }
    }

    private async void ArtistRadioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentArtist is not { } artist)
            return;
        ShowToast($"Starting {artist.Name} radio…");
        try
        {
            var picks = new List<Song>();
            AddRadioSongs(picks, await _client.GetSimilarSongsAsync(artist.Id, 80), null);
            if (picks.Count == 0)
            {
                ShowToast($"No songs like {artist.Name} found");
                return;
            }
            var queue = picks.OrderBy(_ => _random.Next()).Take(RadioLength).ToList();
            PopulateSongArt(queue);
            PlayQueueFrom(queue, 0);
            ShowToast($"{artist.Name} radio: {queue.Count} songs");
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Couldn't start the radio: {ex.Message}");
        }
    }

    private static void AddRadioSongs(List<Song> picks, IEnumerable<Song> found, string? seedId)
    {
        foreach (var song in found)
            if (song.Id != seedId && picks.All(p => p.Id != song.Id))
                picks.Add(song);
    }
}

using LunaDrome.Controls;
using LunaDrome.Core.Models;

namespace LunaDrome;

// Song / artist radio: a queue of similar songs from the library that keeps going. Navidrome's
// getSimilarSongs2 asks Last.fm and keeps what's in the library - plenty for well-known music,
// but it can come back short (David Bowie: 1 song), so it's topped up with songs like the artist
// and then random songs from the same genre.
//
// While a radio runs, the queue shows a banner (what it's based on + Stop), the playback bar a
// RADIO label, and when 5 or fewer songs are left after the current one, more songs like the
// one that's playing are added. Starting anything else (PlayQueueFrom) or clearing the queue
// ends the radio; the songs already queued stay.
public sealed partial class MainPage
{
    private const int RadioLength = 50;
    private const int RadioTopUpWhenLeft = 5;
    private const int RadioTopUpCount = 20;

    private sealed record RadioSession(string Name);

    private RadioSession? _radio;
    private bool _radioToppingUp;

    // The song plays right away; the similar songs (a few seconds: Navidrome asks Last.fm)
    // are added behind it while the banner says it's looking.
    private async Task StartSongRadioAsync(Song seed)
    {
        var name = $"“{seed.Title}”";
        PopulateSongArt(new[] { seed });
        PlayQueueFrom(new List<Song> { seed }, 0);
        var radio = _radio = new RadioSession(name);
        _radioToppingUp = true;   // this first search is the top-up; don't start a second one
        UpdateRadioUi(finding: true);
        ShowToast($"Radio started - finding songs like {name}…");
        try
        {
            var picks = await FindRadioSongsAsync(seed, RadioLength * 2, exclude: new HashSet<string> { seed.Id });
            if (_radio != radio)
                return;   // something else was started meanwhile
            AppendRadioSongs(picks.OrderBy(_ => _random.Next()).Take(RadioLength - 1).ToList());
            UpdateRadioUi();
            ShowToast(picks.Count == 0
                    ? $"No songs like {name} found in your library - the radio ends after this one"
                    : $"Radio: {Math.Min(picks.Count, RadioLength - 1)} songs like {name} queued, and it keeps going",
                IsCompact ? null : "Show queue", IsCompact ? null : ShowQueuePanel);
            if (picks.Count == 0)
                EndRadio();
        }
        catch (Exception ex)
        {
            if (_radio == radio)
                EndRadio();
            await ShowErrorAsync($"Couldn't start the radio: {ex.Message}");
        }
        finally
        {
            _radioToppingUp = false;
        }
    }

    private async void ArtistRadioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentArtist is not { } artist)
            return;
        var button = sender as Button;
        var label = (button?.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault();
        if (button != null)
            button.IsEnabled = false;
        if (label != null)
            label.Text = "Starting…";
        ShowRadioStarting($"Finding songs like {artist.Name}…");
        try
        {
            var picks = new List<Song>();
            AddRadioSongs(picks, await _client.GetSimilarSongsAsync(artist.Id, 80), new HashSet<string>());
            if (picks.Count == 0)
            {
                UpdateRadioUi();
                ShowToast($"No songs like {artist.Name} found in your library");
                return;
            }
            StartRadio(picks.OrderBy(_ => _random.Next()).Take(RadioLength).ToList(), artist.Name, null);
        }
        catch (Exception ex)
        {
            UpdateRadioUi();
            await ShowErrorAsync($"Couldn't start the radio: {ex.Message}");
        }
        finally
        {
            if (button != null)
                button.IsEnabled = true;
            if (label != null)
                label.Text = "Radio";
        }
    }

    private void StartRadio(List<Song> queue, string name, string? message)
    {
        PopulateSongArt(queue);
        PlayQueueFrom(queue, 0);   // ends any previous radio...
        _radio = new RadioSession(name);   // ...then this one starts
        UpdateRadioUi();
        ShowToast(message ?? $"Radio started: {queue.Count} songs like {name}, and it keeps going",
            IsCompact ? null : "Show queue", IsCompact ? null : ShowQueuePanel);
    }

    /// <summary>Called by PlayQueueFrom (something else was started) and Clear queue.</summary>
    private void EndRadio()
    {
        if (_radio == null)
            return;
        _radio = null;
        UpdateRadioUi();
    }

    private void StopRadioButton_Click(object sender, RoutedEventArgs e)
    {
        EndRadio();
        ShowToast("Radio stopped - the queued songs stay");
    }

    // Song changed: keep the radio going when it's about to run out.
    private async void MaybeTopUpRadio()
    {
        if (_radio == null || _radioToppingUp || _current is not { } current)
            return;
        var left = _queue.Count - _queue.IndexOf(current) - 1;
        if (left > RadioTopUpWhenLeft)
            return;

        _radioToppingUp = true;
        var radio = _radio;
        try
        {
            var queued = new HashSet<string>(_queue.Select(q => q.Song.Id));
            var picks = await FindRadioSongsAsync(current.Song, RadioTopUpCount * 2, queued);
            if (_radio != radio || picks.Count == 0)
                return;
            var added = picks.OrderBy(_ => _random.Next()).Take(RadioTopUpCount).ToList();
            AppendRadioSongs(added);
            Log($"radio: added {added.Count} songs like '{current.Song.Title}'");
        }
        catch (Exception ex)
        {
            Log($"radio: top-up failed - {ex.Message}");
        }
        finally
        {
            _radioToppingUp = false;
        }
    }

    private void AppendRadioSongs(List<Song> songs)
    {
        PopulateSongArt(songs);
        foreach (var song in songs)
        {
            var entry = new QueueEntry(song);
            _queue.Add(entry);
            _unshuffledOrder?.Add(entry);
        }
    }

    // All three lookups at once (each similar-songs call takes 2-3 s however many it asks for),
    // then used in order: like the song, like its artist, then the genre.
    private async Task<List<Song>> FindRadioSongsAsync(Song seed, int want, HashSet<string> exclude)
    {
        var bySong = _client.GetSimilarSongsAsync(seed.Id, want);
        var byArtist = string.IsNullOrEmpty(seed.ArtistId) ? Task.FromResult(new List<Song>()) : _client.GetSimilarSongsAsync(seed.ArtistId, want);
        var byGenre = _client.GetRandomSongsAsync(seed.Genre, want);

        var picks = new List<Song>();
        AddRadioSongs(picks, await bySong, exclude);
        if (picks.Count < want / 2)
            AddRadioSongs(picks, await Quietly(byArtist), exclude);
        if (picks.Count < want / 2)
            AddRadioSongs(picks, await Quietly(byGenre), exclude);
        return picks;

        // The fallbacks are only extras: one failing mustn't fail the radio.
        static async Task<List<Song>> Quietly(Task<List<Song>> lookup)
        {
            try { return await lookup; }
            catch { return new List<Song>(); }
        }
    }

    private static void AddRadioSongs(List<Song> picks, IEnumerable<Song> found, HashSet<string> exclude)
    {
        foreach (var song in found)
            if (!exclude.Contains(song.Id) && picks.All(p => p.Id != song.Id))
                picks.Add(song);
    }

    private void ShowRadioStarting(string text)
    {
        ShowToast(text);
        SideRadioBanner.Visibility = Visibility.Visible;
        SideRadioTitle.Text = "Starting radio…";
        SideRadioSubtitle.Text = text;
        SideRadioStopButton.Visibility = Visibility.Collapsed;
    }

    private void UpdateRadioUi(bool finding = false)
    {
        var on = _radio != null;
        SideRadioBanner.Visibility = NowPlayingRadioBadge.Visibility = FullScreenRadioText.Visibility =
            on ? Visibility.Visible : Visibility.Collapsed;
        SideRadioStopButton.Visibility = Visibility.Visible;
        if (!on)
            return;
        SideRadioTitle.Text = "Radio";
        SideRadioSubtitle.Text = finding
            ? $"Finding songs like {_radio!.Name}…"
            : $"Songs like {_radio!.Name} · more are added as it plays";
        FullScreenRadioText.Text = $"RADIO · SONGS LIKE {_radio.Name.ToUpperInvariant()}";
        ToolTipService.SetToolTip(NowPlayingRadioBadge, $"Radio: songs like {_radio.Name}");
        Motion.FadeIn(SideRadioBanner, rise: 6);
        Motion.PopIn(NowPlayingRadioBadge);
    }

    private void ShowQueuePanel()
    {
        if (QueueSidePanel.Visibility != Visibility.Visible || FullScreenPlayerOverlay.Visibility == Visibility.Visible)
            QueueToggleButton_Click(this, new RoutedEventArgs());
    }
}

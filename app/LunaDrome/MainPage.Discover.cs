using LunaDrome.Core.Models;
using LunaDrome.Core.Services;
using Microsoft.UI.Xaml.Input;

namespace LunaDrome;

// Discover: SpotiDrome's Discover Weekly / Daily Discovery. SpotiDrome picks songs that aren't in
// the library yet and keeps them in a separate Navidrome library ("Discover"; NavidromeClient
// leaves it out of browsing). Here they're a playlist with two buttons per song: the heart keeps
// it (a starred song moves into the library when the next mix is made) and the cross removes it
// right away. Songs played all the way through a few times stay too; the rest is deleted.
public sealed partial class MainPage
{
    private string _discoverMix = "weekly";
    private readonly DispatcherTimer _discoverPollTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private int _discoverToken;

    private void UpdateDiscoverNav()
    {
        NavDiscover.Visibility = _spotiDrome.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadDiscoverAsync()
    {
        var token = ++_discoverToken;
        SetDiscoverTabStyle(DiscoverWeeklyButton, _discoverMix == "weekly");
        SetDiscoverTabStyle(DiscoverDailyButton, _discoverMix == "daily");
        if (!_spotiDrome.IsConfigured)
        {
            ShowDiscover(token, [], "Connect SpotiDrome in Settings to get Discover Weekly and Daily Discovery.", canMake: false);
            return;
        }

        SpotiDromeDiscover? info;
        try
        {
            info = await _spotiDrome.GetDiscoverAsync();
        }
        catch (Exception ex)
        {
            ShowDiscover(token, [], $"Couldn't reach SpotiDrome: {ex.Message}", canMake: false);
            return;
        }
        if (info is null || !info.Mixes.TryGetValue(_discoverMix, out var mix))
        {
            ShowDiscover(token, [], "This SpotiDrome has no Discover yet - update it in SpotiDrome > Settings.", canMake: false);
            return;
        }

        var songs = new List<Song>();
        if (mix.PlaylistId is { } playlistId)
        {
            try
            {
                songs = await _client.GetPlaylistSongsAsync(playlistId);
                PopulateSongArt(songs);
            }
            catch { /* shown as empty */ }
        }

        string status;
        if (mix.Running)
            status = $"A new {mix.Playlist} is being made - this takes a few minutes.";
        else if (mix.LastError is { } error)
            status = $"The last {mix.Playlist} couldn't be made: {error}";
        else if (mix.GeneratedAt is not { } made)
            status = mix.Enabled
                ? $"No {mix.Playlist} yet - SpotiDrome makes the first one soon, or tap \"Make a new one\"."
                : $"{mix.Playlist} is off - switch it on in SpotiDrome > Settings > Discover, or tap \"Make a new one\".";
        else
            status = $"{songs.Count} songs you don't have yet, picked {DateTimeOffset.FromUnixTimeSeconds(made).LocalDateTime:ddd d MMM}. " +
                     $"♥ the ones you like and they move into your library when the next one is made; " +
                     $"songs you play {info.KeepPlays}× all the way through stay too. The rest is removed then. ✕ removes a song now.";
        ShowDiscover(token, songs, status, canMake: !mix.Running);

        if (mix.Running && !_discoverPollTimer.IsEnabled)
        {
            _discoverPollTimer.Tick -= DiscoverPoll_Tick;
            _discoverPollTimer.Tick += DiscoverPoll_Tick;
            _discoverPollTimer.Start();
        }
        else if (!mix.Running)
            _discoverPollTimer.Stop();
    }

    // The picked mix looks like a primary button; the other keeps the app's implicit button style
    // (ClearValue, not Style = null - null would drop that too and show the bare platform look).
    private static void SetDiscoverTabStyle(Button button, bool selected)
    {
        if (selected)
            button.Style = (Style)Application.Current.Resources["PrimaryButtonStyle"];
        else
            button.ClearValue(FrameworkElement.StyleProperty);
    }

    private void DiscoverPoll_Tick(object? sender, object e)
    {
        if (DiscoverPage.Visibility != Visibility.Visible)
        {
            _discoverPollTimer.Stop();
            return;
        }
        _ = LoadDiscoverAsync();
    }

    private void ShowDiscover(int token, List<Song> songs, string status, bool canMake)
    {
        if (token != _discoverToken)
            return;   // the other mix was picked in the meantime
        DiscoverSongsList.ItemsSource = songs;
        DiscoverStatusText.Text = status;
        DiscoverMakeButton.IsEnabled = canMake;
        DiscoverPlayButton.IsEnabled = songs.Count > 0;
    }

    private void DiscoverMix_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mix } && mix != _discoverMix)
        {
            _discoverMix = mix;
            _ = LoadDiscoverAsync();
        }
    }

    private void DiscoverPlay_Click(object sender, RoutedEventArgs e)
    {
        if (DiscoverSongsList.ItemsSource is List<Song> { Count: > 0 } songs)
            PlayQueueFrom(songs, 0);
    }

    private async void DiscoverMake_Click(object sender, RoutedEventArgs e)
    {
        var name = _discoverMix == "weekly" ? "Discover Weekly" : "Daily Discovery";
        var confirm = new ContentDialog
        {
            Title = $"New {name}",
            Content = $"Make a new {name} now? The songs in it now are judged first: the ones you ♥'d or played " +
                      "a few times move into your library, the rest is removed.",
            PrimaryButtonText = "Make it",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };
        try
        {
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;
        }
        catch { return; /* another dialog already open */ }

        try
        {
            await _spotiDrome.RunDiscoverAsync(_discoverMix);
        }
        catch (Exception ex)
        {
            ShowToast($"Couldn't start it: {ex.Message}");
            return;
        }
        await LoadDiscoverAsync();
    }

    private async void DiscoverReject_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: Song song })
            await RejectDiscoverSongAsync(song);
    }

    private async Task RejectDiscoverSongAsync(Song song)
    {
        // Off the list straight away; SpotiDrome deletes the file and won't pick it again.
        if (DiscoverSongsList.ItemsSource is List<Song> songs)
            DiscoverSongsList.ItemsSource = songs.Where(s => s.Id != song.Id).ToList();
        try
        {
            await _spotiDrome.DiscoverFeedbackAsync(song.Id, "reject");
            ShowToast($"Removed {song.Title} - it won't be suggested again");
        }
        catch (Exception ex)
        {
            ShowToast($"Couldn't remove {song.Title}: {ex.Message}");
            _ = LoadDiscoverAsync();
        }
    }
}

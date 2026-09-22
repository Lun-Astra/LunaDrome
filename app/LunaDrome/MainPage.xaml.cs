using LunaDrome.Core.Models;
using LunaDrome.Core.Services;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace LunaDrome;

public sealed partial class MainPage : Page
{
    private readonly NavidromeClient _client = new();
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _progressTimer;
    private Song? _currentSong;

    public MainPage()
    {
        this.InitializeComponent();

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _progressTimer.Tick += ProgressTimer_Tick;
        _progressTimer.Start();

        _player.Volume = VolumeSlider.Value / 100.0;
        _player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            PlayPauseButton.Content = "Play";
        });
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "";

        var serverUrl = ServerUrlBox.Text.Trim();
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(username))
        {
            StatusText.Text = "Server URL and username are required.";
            return;
        }

        LoginButton.IsEnabled = false;
        StatusText.Text = "Signing in...";

        _client.Configure(serverUrl, username, password);
        var (success, error) = await _client.LoginAsync();

        if (!success)
        {
            StatusText.Text = error ?? "Login failed.";
            LoginButton.IsEnabled = true;
            return;
        }

        var artists = await _client.GetArtistsAsync();
        ArtistsList.ItemsSource = artists;

        var tracks = await _client.GetAllSongsAsync(limit: 100);
        TracksList.ItemsSource = tracks;

        LoginPanel.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = Visibility.Visible;
    }

    private void TracksList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (TracksList.SelectedItem is Song song)
            PlaySong(song);
    }

    private void PlaySong(Song song)
    {
        _currentSong = song;
        _player.Source = MediaSource.CreateFromUri(new Uri(_client.GetStreamUrl(song.Id)));
        _player.Play();
        PlayPauseButton.Content = "Pause";
        NowPlayingText.Text = $"{song.Title} — {song.ArtistName}";
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSong == null)
            return;

        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
            PlayPauseButton.Content = "Play";
        }
        else
        {
            _player.Play();
            PlayPauseButton.Content = "Pause";
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _player.Pause();
        _player.PlaybackSession.Position = TimeSpan.Zero;
        PlayPauseButton.Content = "Play";
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _player.Volume = e.NewValue / 100.0;
    }

    private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        var duration = _player.PlaybackSession.NaturalDuration;
        if (duration <= TimeSpan.Zero)
            return;

        _player.PlaybackSession.Position = TimeSpan.FromSeconds(ProgressSlider.Value * duration.TotalSeconds);
    }

    private void ProgressTimer_Tick(object? sender, object e)
    {
        var session = _player.PlaybackSession;
        if (session == null || session.NaturalDuration <= TimeSpan.Zero)
            return;

        var position = session.Position;
        var duration = session.NaturalDuration;

        ProgressSlider.Value = position.TotalSeconds / duration.TotalSeconds;
        ElapsedText.Text = $"{(int)position.TotalMinutes}:{position.Seconds:D2}";
        DurationText.Text = $"{(int)duration.TotalMinutes}:{duration.Seconds:D2}";
    }
}

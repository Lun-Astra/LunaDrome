using LunaDrome.Controls;
using LunaDrome.Core.Models;
using LunaDrome.Services;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;

namespace LunaDrome;

// Karaoke: the playing song's synced lyrics full screen over its blurred cover. The line being sung
// lights up word by word (from the lyrics' own word timings when they have them, else spread over
// the line at a singing pace), the next lines wait below it, and instrumental breaks count down to
// the next line. Opened from the Lyrics tab, or the now-playing title's menu.
public sealed partial class MainPage
{
    private readonly DispatcherTimer _karaokeTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _karaokeTimerHooked;
    private int _karaokeLine = int.MinValue;   // the line on screen (-1 = before the first one)
    private bool _karaokeBreak;                // showing the countdown instead of a line
    private readonly List<(Run Run, TimeSpan At)> _karaokeWords = new();
    private string? _karaokeArtUrl;
    private int _karaokeArtToken;

    // A line timed only as a whole is sung at roughly this pace; never slower than the gap allows.
    private const double SecondsPerCharacter = 0.075;
    // A gap this long after a line has been sung (or before the first one) shows the countdown.
    private static readonly TimeSpan BreakAfter = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan MinBreak = TimeSpan.FromSeconds(4);

    private void KaraokeOpen_Click(object sender, RoutedEventArgs e) => OpenKaraoke();

    private void KaraokeClose_Click(object sender, RoutedEventArgs e) => CloseKaraoke();

    private void OpenKaraoke()
    {
        if (_current == null)
            return;
        if (!_karaokeTimerHooked)
        {
            _karaokeTimer.Tick += (_, _) => UpdateKaraoke();
            _karaokeTimerHooked = true;
        }
        var compact = IsCompact;
        KaraokeContent.Margin = compact ? new Thickness(20, 24, 20, 20) : new Thickness(48, 32, 48, 28);
        KaraokeLines.Spacing = compact ? 14 : 22;
        KaraokeCurrent.FontSize = compact ? 30 : 46;
        KaraokeCurrent.LineHeight = compact ? 38 : 58;
        KaraokeNext.FontSize = compact ? 20 : 28;
        KaraokePrev.FontSize = KaraokeNext2.FontSize = compact ? 17 : 24;
        KaraokeCountdown.FontSize = compact ? 24 : 30;
        // Phones: no room for the timing note next to the buttons.
        KaraokeTimingButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        KaraokePlayPauseButton.Margin = new Thickness(compact ? 6 : 14, 0, compact ? 6 : 14, 0);

        KaraokeOverlay.Visibility = Visibility.Visible;
        Motion.SlideIn(KaraokeOverlay, 0, 40);
        KaraokeSongChanged();
        if (_lyricsSongId != _current.Song.Id)
            _ = LoadLyricsForCurrentSongAsync();   // ends in ShowKaraokeLyrics
        else
            ShowKaraokeLyrics();
        _karaokeTimer.Start();
        KaraokeCloseButton.Focus(FocusState.Programmatic);   // so Esc reaches the overlay
    }

    private void CloseKaraoke()
    {
        _karaokeTimer.Stop();
        Motion.SlideOut(KaraokeOverlay, 0, 40, () => KaraokeOverlay.Visibility = Visibility.Collapsed);
    }

    private void KaraokeOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CloseKaraoke();
            e.Handled = true;
        }
    }

    /// <summary>Title, artist and the blurred cover of the playing song (called on every song change).</summary>
    private async void KaraokeSongChanged()
    {
        if (KaraokeOverlay.Visibility != Visibility.Visible || _current?.Song is not { } song)
            return;
        KaraokeTitleText.Text = song.Title;
        KaraokeArtistText.Text = song.Artists.Count > 1 ? string.Join(", ", song.Artists.Select(a => a.Name)) : song.ArtistName;
        var art = _offlineMode ? OfflineCover(song) : _client.GetCoverArtUrl(song.AlbumId, 300);
        SetImage(KaraokeArt, art);
        // The lyrics of the previous song stay off until the new ones are in.
        if (_lyricsSongId != song.Id)
            ShowKaraokeLyrics();

        if (art == _karaokeArtUrl)
            return;
        _karaokeArtUrl = art;
        var token = ++_karaokeArtToken;
        var image = await BlurredArt.LoadBackdropAsync(art);
        if (token != _karaokeArtToken)
            return;
        KaraokeBackdrop.Source = image;
        if (image != null)
            Motion.FadeIn(KaraokeBackdrop);
    }

    /// <summary>Called when the lyrics change (a new song, the lyrics coming in): starts over.</summary>
    private void ShowKaraokeLyrics()
    {
        if (KaraokeOverlay.Visibility != Visibility.Visible)
            return;
        _karaokeLine = int.MinValue;
        _karaokeBreak = false;
        UpdateKaraoke();
    }

    private void KaraokeNext_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Jump to the line shown as next.
        if (!_lyricsAreSynced || KaraokeNext.Tag is not LyricLine line || JamOwnsPlayback)
            return;
        var target = line.Start + TimeSpan.FromSeconds(_lyricsOffset);
        _player.PlaybackSession.Position = target < TimeSpan.Zero ? TimeSpan.Zero : target;
    }

    private void ShowKaraokeMessage(string? message)
    {
        KaraokeMessage.Text = message ?? "";
        KaraokeMessage.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        KaraokeLines.Visibility = message == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateKaraoke()
    {
        if (KaraokeOverlay.Visibility != Visibility.Visible || _current?.Song is not { } song)
            return;

        var playing = _loadedEntry == _current ? _player.PlaybackSession.Position : _pendingSeek;
        var duration = TimeSpan.FromSeconds(song.DurationSeconds);
        KaraokeElapsedText.Text = FormatTime(playing);
        KaraokeDurationText.Text = FormatTime(duration);
        KaraokeProgress.Value = duration > TimeSpan.Zero ? Math.Clamp(playing / duration, 0, 1) : 0;

        if (JamOwnsPlayback)
        {
            ShowKaraokeMessage("Karaoke follows the songs this device plays itself - not the jam.");
            return;
        }
        if (_lyricsSongId != song.Id)
        {
            ShowKaraokeMessage("Looking for lyrics…");
            return;
        }
        if (_lyricsLines.Count == 0 || !_lyricsAreSynced)
        {
            ShowKaraokeMessage(_lyricsLines.Count == 0
                ? "No lyrics found for this song."
                : "This song only has plain lyrics, without timing - karaoke needs timed ones.");
            return;
        }
        ShowKaraokeMessage(null);

        var lines = _lyricsLines;
        var position = playing - TimeSpan.FromSeconds(_lyricsOffset);
        var index = -1;
        for (var i = 0; i < lines.Count && lines[i].Start <= position; i++)
            index = i;

        // A break: before the first line, on an empty line, or a long gap after a line was sung.
        var nextIndex = NextSungLine(lines, index + 1);
        var nextStart = nextIndex >= 0 ? lines[nextIndex].Start : duration;
        var inBreak = index < 0
            ? nextStart - position > TimeSpan.FromSeconds(1)
            : string.IsNullOrWhiteSpace(lines[index].Text)
              || (position > lines[index].Start + SungFor(lines, index) + BreakAfter && nextStart - position > MinBreak);

        if (index != _karaokeLine || inBreak != _karaokeBreak)
        {
            var wasShowing = _karaokeLine != int.MinValue;
            _karaokeLine = index;
            _karaokeBreak = inBreak;
            ShowKaraokeLines(lines, index, nextIndex, inBreak);
            if (wasShowing)
                Motion.SlideIn(KaraokeLines, 0, 26, 320);
        }

        if (inBreak)
        {
            // Three dots that light up over the last three seconds before the next line.
            var left = (nextStart - position).TotalSeconds;
            var lit = left > 3 ? 0 : 3 - (int)Math.Ceiling(left) + 1;
            for (var i = 0; i < KaraokeCountdown.Inlines.Count; i++)
                Paint((Run)KaraokeCountdown.Inlines[i], i < lit ? AccentRunBrush() : UnsungBrush);
            return;
        }

        var sung = AccentRunBrush();
        foreach (var (run, at) in _karaokeWords)
            Paint(run, position >= at ? sung : UnsungBrush);
    }

    // Only on a change: a new brush every tick would redraw the text 25 times a second.
    private static void Paint(Run run, Brush brush)
    {
        if (!ReferenceEquals(run.Foreground, brush))
            run.Foreground = brush;
    }

    private static int NextSungLine(List<LyricLine> lines, int from)
    {
        for (var i = from; i < lines.Count; i++)
            if (!string.IsNullOrWhiteSpace(lines[i].Text))
                return i;
        return -1;
    }

    /// <summary>How long the line is sung: to its last word when it's timed per word, else at
    /// SecondsPerCharacter, at most up to (just before) the next line.</summary>
    private static TimeSpan SungFor(List<LyricLine> lines, int index)
    {
        var line = lines[index];
        var gap = index + 1 < lines.Count ? lines[index + 1].Start - line.Start : TimeSpan.FromSeconds(10);
        if (line.Words is { Count: > 0 } words)
            return words[^1].Offset + TimeSpan.FromSeconds(0.4);
        var paced = TimeSpan.FromSeconds(Math.Max(1, line.Text.Length * SecondsPerCharacter));
        var room = gap * 0.92;
        return paced < room ? paced : room;
    }

    private void ShowKaraokeLines(List<LyricLine> lines, int index, int nextIndex, bool inBreak)
    {
        var previous = inBreak
            ? (index >= 0 && !string.IsNullOrWhiteSpace(lines[index].Text) ? lines[index] : PreviousSungLine(lines, index - 1))
            : PreviousSungLine(lines, index - 1);
        KaraokePrev.Text = previous?.Text ?? "";

        _karaokeWords.Clear();
        KaraokeCurrent.Inlines.Clear();
        KaraokeCountdown.Inlines.Clear();
        KaraokeCurrent.Visibility = inBreak ? Visibility.Collapsed : Visibility.Visible;
        KaraokeCountdown.Visibility = inBreak ? Visibility.Visible : Visibility.Collapsed;
        LyricLine? next;
        if (inBreak)
        {
            for (var i = 0; i < 3; i++)
                KaraokeCountdown.Inlines.Add(new Run { Text = i < 2 ? "● " : "●", Foreground = UnsungBrush });
            next = nextIndex >= 0 ? lines[nextIndex] : null;
        }
        else
        {
            var line = lines[index];
            foreach (var (text, at) in KaraokeWords(lines, index))
            {
                var run = new Run { Text = text, Foreground = UnsungBrush };
                KaraokeCurrent.Inlines.Add(run);
                _karaokeWords.Add((run, line.Start + at));
            }
            next = nextIndex >= 0 ? lines[nextIndex] : null;
        }
        KaraokeNext.Text = next?.Text ?? "";
        KaraokeNext.Tag = next;
        var after = next == null ? -1 : NextSungLine(lines, lines.IndexOf(next) + 1);
        KaraokeNext2.Text = after >= 0 ? lines[after].Text : "";
        KaraokeTimingButton.Content = !inBreak && index >= 0 && lines[index].Words != null ? "Word timing: from the lyrics" : "Word timing: guessed";
    }

    private static LyricLine? PreviousSungLine(List<LyricLine> lines, int from)
    {
        for (var i = Math.Min(from, lines.Count - 1); i >= 0; i--)
            if (!string.IsNullOrWhiteSpace(lines[i].Text))
                return lines[i];
        return null;
    }

    /// <summary>The line's words (each with its trailing space) and when each one is sung,
    /// relative to the line's start.</summary>
    private static List<(string Text, TimeSpan At)> KaraokeWords(List<LyricLine> lines, int index)
    {
        var line = lines[index];
        if (line.Words is { Count: > 0 } timed)
            return timed.Select(w => (w.Text, w.Offset)).ToList();

        // Spread over the time it's sung, by length: a long word takes longer than "a".
        var words = System.Text.RegularExpressions.Regex.Matches(line.Text, @"\S+\s*").Select(m => m.Value).ToList();
        var total = Math.Max(1, words.Sum(w => w.TrimEnd().Length));
        var span = SungFor(lines, index);
        var result = new List<(string, TimeSpan)>();
        var before = 0;
        foreach (var word in words)
        {
            result.Add((word, span * ((double)before / total)));
            before += word.TrimEnd().Length;
        }
        return result;
    }

    private static readonly SolidColorBrush UnsungBrush = new(Windows.UI.Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF));
    private SolidColorBrush? _karaokeAccent;

    private SolidColorBrush AccentRunBrush()
    {
        if (_karaokeAccent?.Color != Themes.CurrentAccent)
            _karaokeAccent = new SolidColorBrush(Themes.CurrentAccent);
        return _karaokeAccent;
    }
}

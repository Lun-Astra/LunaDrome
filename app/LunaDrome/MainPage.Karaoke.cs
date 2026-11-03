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

    // A line timed only as a whole is sung at the song's own pace (see SongPace); this is the
    // fallback when there are too few lines to tell.
    private const double DefaultSecondsPerSyllable = 0.45;
    private List<LyricLine>? _paceFor;
    private double _pace = DefaultSecondsPerSyllable;
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
        // Before the first line it's always the countdown (there's no line to show yet).
        var inBreak = index < 0
            || string.IsNullOrWhiteSpace(lines[index].Text)
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
            Paint(run, position >= at ? sung : UnsungBrush);   // per letter: a smooth sweep, not word jumps
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

    /// <summary>How long the line is sung. Timed per word: to the end of its last word. Else
    /// singers mostly stretch a line to the next one, so it fills the gap (minus a breath) -
    /// unless the gap is far longer than the syllables need, then there's a pause after it.</summary>
    private TimeSpan SungFor(List<LyricLine> lines, int index)
    {
        var line = lines[index];
        var gap = (index + 1 < lines.Count ? lines[index + 1].Start - line.Start : TimeSpan.FromSeconds(10)).TotalSeconds;
        if (line.Words is { Count: > 0 } words)
            return words[^1].Offset + TimeSpan.FromSeconds(Math.Clamp(gap - words[^1].Offset.TotalSeconds, 0.2, 0.8));
        var natural = Math.Max(0.6, Syllables(line.Text) * SongPace(lines));
        var room = gap * 0.92;
        return TimeSpan.FromSeconds(room <= natural * 1.8 ? room : natural * 1.2);
    }

    /// <summary>Seconds per syllable this song is sung at: the median over its lines of the time to
    /// the next line per syllable (lines followed by a long pause left out). A ballad like "I Burn"
    /// is ~0.58, a fast rap ~0.2; one fixed pace made slow songs light up far ahead of the singer.</summary>
    private double SongPace(List<LyricLine> lines)
    {
        if (ReferenceEquals(lines, _paceFor))
            return _pace;
        var rates = new List<double>();
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            var gap = (lines[i + 1].Start - lines[i].Start).TotalSeconds;
            var syllables = string.IsNullOrWhiteSpace(lines[i].Text) ? 0 : Syllables(lines[i].Text);
            if (syllables >= 3 && gap is > 0.5 and < 8)
                rates.Add(gap / syllables);
        }
        rates.Sort();
        _pace = rates.Count >= 4 ? Math.Clamp(rates[rates.Count / 2], 0.18, 0.9) : DefaultSecondsPerSyllable;
        _paceFor = lines;
        return _pace;
    }

    // Vowel groups ≈ syllables (a silent final e doesn't count); for scripts without Latin vowels
    // (Japanese, Korean, Chinese) every letter is about one syllable.
    private static int Syllables(string text)
    {
        var count = 0;
        foreach (System.Text.RegularExpressions.Match word in System.Text.RegularExpressions.Regex.Matches(text, @"\S+"))
            count += WordSyllables(word.Value);
        return Math.Max(1, count);
    }

    private static int WordSyllables(string word)
    {
        var letters = new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (letters.Length == 0)
            return 0;
        var vowels = System.Text.RegularExpressions.Regex.Matches(letters, "[aeiouyàáâãäåæèéêëìíîïòóôõöøùúûüý]+").Count;
        if (vowels == 0)
            return letters.Any(c => c > 0x2E80) ? letters.Length : 1;
        if (vowels > 1 && letters.EndsWith('e') && !letters.EndsWith("le"))
            vowels--;
        return vowels;
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
            var words = KaraokeWords(lines, index);
            for (var w = 0; w < words.Count; w++)
            {
                // One run per letter, lit across the word's time: the light sweeps through it.
                var (text, at) = words[w];
                var until = w + 1 < words.Count ? words[w + 1].At : SungFor(lines, index);
                var length = TimeSpan.FromSeconds(Math.Min((until - at).TotalSeconds, 1.2));   // a long held note: the word is lit, then held
                var letters = text.TrimEnd().Length;
                for (var c = 0; c < text.Length; c++)
                {
                    var run = new Run { Text = text[c].ToString(), Foreground = UnsungBrush };
                    KaraokeCurrent.Inlines.Add(run);
                    _karaokeWords.Add((run, line.Start + at + length * ((double)Math.Min(c, letters) / Math.Max(1, letters))));
                }
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
    private List<(string Text, TimeSpan At)> KaraokeWords(List<LyricLine> lines, int index)
    {
        var line = lines[index];
        if (line.Words is { Count: > 0 } timed)
            return timed.Select(w => (w.Text, w.Offset)).ToList();

        // Spread over the time it's sung by syllables: "strength" is one beat, "ivory" three.
        var words = System.Text.RegularExpressions.Regex.Matches(line.Text, @"\S+\s*").Select(m => m.Value).ToList();
        var weights = words.Select(w => Math.Max(1, WordSyllables(w))).ToList();
        var total = (double)weights.Sum();
        var span = SungFor(lines, index);
        var result = new List<(string, TimeSpan)>();
        var before = 0;
        for (var i = 0; i < words.Count; i++)
        {
            result.Add((words[i], span * (before / total)));
            before += weights[i];
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

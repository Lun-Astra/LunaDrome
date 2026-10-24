using Microsoft.UI.Xaml.Shapes;

namespace LunaDrome.Controls;

/// <summary>
/// Marks the song that's playing in song lists (album tracks, Tracks, playlists, search). Rows
/// bind <c>controls:NowPlaying.SongId="{Binding Id}"</c> on an element: with ShowWhenCurrent
/// (default) it is only visible on the playing song's row, with ShowWhenCurrent="False" it is
/// hidden there (the track number that the equalizer bars replace). MainPage sets
/// <see cref="SetCurrent"/> / <see cref="SetPlaying"/>.
/// </summary>
public static class NowPlaying
{
    private static readonly List<WeakReference<FrameworkElement>> Tracked = new();

    public static string? CurrentId { get; private set; }
    public static bool IsPlaying { get; private set; }

    public static readonly DependencyProperty SongIdProperty = DependencyProperty.RegisterAttached(
        "SongId", typeof(string), typeof(NowPlaying), new PropertyMetadata(null, (d, e) =>
        {
            if (d is not FrameworkElement element)
                return;
            if (e.OldValue == null)
                Tracked.Add(new WeakReference<FrameworkElement>(element));
            Apply(element);
        }));

    public static string? GetSongId(DependencyObject d) => (string?)d.GetValue(SongIdProperty);
    public static void SetSongId(DependencyObject d, string? value) => d.SetValue(SongIdProperty, value);

    public static readonly DependencyProperty ShowWhenCurrentProperty = DependencyProperty.RegisterAttached(
        "ShowWhenCurrent", typeof(bool), typeof(NowPlaying), new PropertyMetadata(true, (d, _) =>
        {
            if (d is FrameworkElement element)
                Apply(element);
        }));

    public static bool GetShowWhenCurrent(DependencyObject d) => (bool)d.GetValue(ShowWhenCurrentProperty);
    public static void SetShowWhenCurrent(DependencyObject d, bool value) => d.SetValue(ShowWhenCurrentProperty, value);

    public static void SetCurrent(string? songId)
    {
        if (CurrentId == songId)
            return;
        CurrentId = songId;
        Tracked.RemoveAll(weak => !weak.TryGetTarget(out _));
        foreach (var weak in Tracked)
            if (weak.TryGetTarget(out var element))
                Apply(element);
    }

    public static void SetPlaying(bool playing)
    {
        IsPlaying = playing;
        EqualizerBars.PlayingChanged();
    }

    private static void Apply(FrameworkElement element)
    {
        if (GetSongId(element) is not { } id)
            return;
        var current = id == CurrentId;
        element.Visibility = current == GetShowWhenCurrent(element) ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>
/// Three little bars that bounce while the song plays and rest low while paused. They step on
/// one shared 120ms timer instead of a per-frame animation: on Uno's Skia host anything that
/// animates every frame keeps the whole window redrawing (see the visualizer notes).
/// </summary>
public sealed class EqualizerBars : StackPanel
{
    private static readonly List<EqualizerBars> Live = new();
    private static DispatcherTimer? _timer;
    private static int _tick;

    // Heights per step for each bar (out of 12), offset so the three never move in step.
    private static readonly double[][] Steps =
    {
        new[] { 5.0, 9, 12, 7, 4, 10, 6, 11 },
        new[] { 11.0, 6, 4, 9, 12, 5, 10, 7 },
        new[] { 7.0, 12, 8, 4, 9, 11, 5, 8 },
    };

    private readonly Rectangle[] _bars = new Rectangle[3];

    public EqualizerBars()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 2;
        Height = 12;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        for (var i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Rectangle
            {
                Width = 3, RadiusX = 1, RadiusY = 1, VerticalAlignment = VerticalAlignment.Bottom,
                Fill = (Brush)Application.Current.Resources["AccentBrush"]
            };
            Children.Add(_bars[i]);
        }
        ShowStep();
        Loaded += (_, _) =>
        {
            Live.Add(this);
            PlayingChanged();
        };
        Unloaded += (_, _) => Live.Remove(this);
    }

    internal static void PlayingChanged()
    {
        if (NowPlaying.IsPlaying && Live.Count > 0)
        {
            if (_timer == null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                _timer.Tick += (_, _) =>
                {
                    _tick++;
                    foreach (var bars in Live)
                        if (bars.Visibility == Visibility.Visible)
                            bars.ShowStep();
                };
            }
            _timer.Start();
        }
        else
        {
            _timer?.Stop();
        }
        foreach (var bars in Live)
            bars.ShowStep();
    }

    private void ShowStep()
    {
        for (var i = 0; i < _bars.Length; i++)
            _bars[i].Height = NowPlaying.IsPlaying ? Steps[i][_tick % Steps[i].Length] : 3 + i % 2 * 2;
    }
}

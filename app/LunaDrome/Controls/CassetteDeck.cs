using LunaDrome.Core.Models;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace LunaDrome.Controls;

/// <summary>
/// The "cassette player" phone player (Settings > Appearance): a Walkman-style deck instead of
/// the Spotify-like now-playing view. The song is a cassette whose label carries the title in
/// handwriting; the reels turn while it plays and the tape winds from the left reel to the right
/// one as the song goes on. Chunky keys on a silver strip: PLAY, FF / REW (tap = next / previous
/// song, hold = wind through the song), STOP/EJECT, volume and LOVE (favorite).
///
/// Three layouts, picked from the size it gets (MainPage.Cassette.cs feeds it):
/// - Portrait (a folded phone): the deck; OPEN shows the cassette rack (playlists) over it.
/// - Wide (unfolded, or a phone on its side): the rack on the left, the deck on the right.
/// - Tabletop (a foldable standing half-open, hinge horizontal): the cassette on the top half,
///   the keys in a row on the bottom half.
/// Everything is drawn here with shapes, in fixed colors - it looks the same in every theme.
/// </summary>
public sealed class CassetteDeck : UserControl
{
    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action? StopRequested;
    public event Action? FavoriteRequested;
    public event Action? CloseRequested;
    /// <summary>The ⇄ button: back to the normal player.</summary>
    public event Action? ExitRequested;
    /// <summary>Holding FF / REW: seconds to move (negative = back).</summary>
    public event Action<double>? SeekByRequested;
    public event Action<double>? VolumeChanged;
    public event Action<Playlist>? TapeChosen;

    private enum Layout { Portrait, Wide, Tabletop }

    private static readonly FontFamily HandFont = new("ms-appx:///Assets/Fonts/Kalam-Regular.ttf#Kalam");
    private static readonly FontFamily DeckFont = new("ms-appx:///Assets/Fonts/BebasNeue-Regular.ttf#Bebas Neue");

    private static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    private static SolidColorBrush B(uint argb) => new(C(argb));

    private static readonly Color Cream = C(0xFFEFE8DA);
    private static readonly Color Orange = C(0xFFF08A24);

    private readonly Grid _root = new();
    private readonly Grid _main = new();
    private readonly Grid _strip = new();
    private readonly Grid _rack = new();
    private readonly StackPanel _keys = new() { Spacing = 14 };
    private readonly StackPanel _stripExtras = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 1, StepFrequency = 0.01 };
    private readonly StackPanel _tapeList = new() { Spacing = 6 };
    private readonly TextBlock _tapeCount = new();
    private readonly Button _openButton = new();
    private readonly Button _closeRackButton = new();

    // The cassette
    private readonly Grid _cassetteHost = new();
    private readonly StackPanel _brand = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _bottom = new() { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TranslateTransform _cassetteMove = new();
    // Upright on a folded phone (it fills the tall screen, like the video), lying down otherwise.
    private readonly Canvas _cassetteFrame = new() { Width = 320, Height = 200 };
    private readonly CompositeTransform _cassetteTurn = new();
    private readonly TextBlock _labelTitle = new();
    private readonly TextBlock _labelArtist = new();
    private readonly Border _labelArt = new();
    private readonly Ellipse _tapeLeft = new(), _tapeRight = new();
    private readonly RotateTransform _hubLeft = new(), _hubRight = new();
    private const double LeftX = 112, RightX = 208, ReelY = 106, MinTape = 13, MaxTape = 33;

    // Status
    private readonly Ellipse _led = new(), _ledGlow = new();
    private readonly TextBlock[] _digits = new TextBlock[4];
    private readonly TextBlock _albumText = new();
    private Key? _playKey;
    private Path? _playIcon;
    private Border? _loveButton;
    private TextBlock? _loveLabel;

    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private DateTime _lastFrame = DateTime.UtcNow;
    private double _progress;
    private bool _playing, _active, _halfOpen, _rackOpen, _ejected, _settingVolume;
    private int _winding;   // -1 rewinding, 1 fast-forwarding, 0 not
    private string? _songId;
    private string _lastCounter = "";
    private Layout _layout = (Layout)(-1);

    public CassetteDeck()
    {
        Content = _root;
        _root.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops = { new GradientStop { Color = C(0xFF2C3139), Offset = 0 }, new GradientStop { Color = C(0xFF1B1F25), Offset = 1 } },
        };
        _root.ColumnDefinitions.Add(new ColumnDefinition());
        _root.ColumnDefinitions.Add(new ColumnDefinition());
        _root.ColumnDefinitions.Add(new ColumnDefinition());
        _root.RowDefinitions.Add(new RowDefinition());
        _root.RowDefinitions.Add(new RowDefinition());

        BuildMain();
        BuildStrip();
        BuildRack();
        _root.Children.Add(_main);
        _root.Children.Add(_strip);
        _root.Children.Add(_rack);

        _frame.Tick += (_, _) => Animate();
        _hold.Tick += (_, _) => SeekByRequested?.Invoke(_winding * 6);
        SizeChanged += (_, _) => ApplyLayout();
        Unloaded += (_, _) => SetActive(false);
    }

    // ================= Public state (MainPage.Cassette.cs) =================

    /// <summary>A foldable standing half-open (hinge sensor); with a landscape window = tabletop.</summary>
    public bool HalfOpen
    {
        get => _halfOpen;
        set
        {
            if (_halfOpen == value)
                return;
            _halfOpen = value;
            ApplyLayout();
        }
    }

    /// <summary>Runs the reel animation only while the deck is on screen.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        UpdateTimer();
        if (active)
            ApplyLayout();
    }

    public void SetSong(string? songId, string title, string artist, string? album, Brush? art)
    {
        _labelTitle.Text = title;
        _labelArtist.Text = artist;
        _albumText.Text = string.IsNullOrEmpty(album) ? "" : album.ToUpperInvariant();
        _labelArt.Background = art;
        _labelArt.Visibility = art != null ? Visibility.Visible : Visibility.Collapsed;
        if (songId == _songId)
            return;
        var swap = _songId != null && songId != null;
        _songId = songId;
        if (swap)
            SwapCassette();
    }

    public void SetState(bool playing, double elapsedSeconds, double durationSeconds)
    {
        if (playing && _ejected)
            Eject(false);
        _playing = playing;
        _progress = durationSeconds > 0 ? Math.Clamp(elapsedSeconds / durationSeconds, 0, 1) : 0;
        SetCounter(elapsedSeconds);
        _led.Fill = B(playing ? 0xFF4CFF7A : 0xFF24402C);
        _ledGlow.Opacity = playing ? 0.55 : 0;
        if (_playIcon != null)
            _playIcon.Data = Geometry(playing ? "M0,0 L4,0 L4,14 L0,14 Z M8,0 L12,0 L12,14 L8,14 Z" : "M0,0 L12,7 L0,14 Z");
        UpdateReels();
        UpdateTimer();
    }

    /// <summary>null = no song (LOVE dimmed).</summary>
    public void SetFavorite(bool? favorite)
    {
        if (_loveButton == null || _loveLabel == null)
            return;
        _loveButton.Opacity = favorite == null ? 0.4 : 1;
        _loveLabel.Text = favorite == true ? "♥ LOVED" : "LOVE";
    }

    public void SetVolume(double volume)
    {
        _settingVolume = true;
        _volume.Value = volume;
        _settingVolume = false;
    }

    public void SetTapes(IReadOnlyList<Playlist> playlists)
    {
        _tapeList.Children.Clear();
        foreach (var playlist in playlists)
            _tapeList.Children.Add(Spine(playlist));
        _tapeCount.Text = $"{playlists.Count} TAPE{(playlists.Count == 1 ? "" : "S")}";
    }

    // ================= Layout =================

    private void ApplyLayout()
    {
        var (w, h) = (ActualWidth, ActualHeight);
        if (w <= 0 || h <= 0)
            return;
        var layout = _halfOpen && w > h ? Layout.Tabletop
            : w >= 560 && w / h >= 0.8 ? Layout.Wide
            : Layout.Portrait;
        if (layout != _layout)
        {
            _layout = layout;
            var cols = _root.ColumnDefinitions;
            var rows = _root.RowDefinitions;
            switch (layout)
            {
                case Layout.Portrait:
                    cols[0].Width = new GridLength(0);
                    cols[1].Width = new GridLength(1, GridUnitType.Star);
                    cols[2].Width = new GridLength(96);
                    rows[1].Height = new GridLength(0);
                    Place(_main, 1, 0); Place(_strip, 2, 0);
                    break;
                case Layout.Wide:
                    cols[0].Width = new GridLength(1, GridUnitType.Star);
                    cols[1].Width = new GridLength(1, GridUnitType.Star);
                    cols[2].Width = new GridLength(96);
                    rows[1].Height = new GridLength(0);
                    Place(_rack, 0, 0); Place(_main, 1, 0); Place(_strip, 2, 0);
                    break;
                case Layout.Tabletop:
                    cols[0].Width = new GridLength(0);
                    cols[1].Width = new GridLength(1, GridUnitType.Star);
                    cols[2].Width = new GridLength(0);
                    rows[1].Height = new GridLength(1, GridUnitType.Star);
                    Place(_main, 1, 0); Place(_strip, 1, 1);
                    break;
            }
            var row = layout == Layout.Tabletop;
            _keys.Orientation = row ? Orientation.Horizontal : Orientation.Vertical;
            _keys.HorizontalAlignment = row ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            _stripExtras.Orientation = row ? Orientation.Horizontal : Orientation.Vertical;
            _stripExtras.VerticalAlignment = row ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            _stripExtras.Margin = new Thickness(0, row ? 28 : 0, 0, 0);
            _stripExtras.Spacing = row ? 32 : 10;
            _volume.Orientation = row ? Orientation.Horizontal : Orientation.Vertical;
            _volume.Width = row ? 200 : double.NaN;
            _volume.Height = row ? double.NaN : 130;
            _strip.Padding = row ? new Thickness(24, 20, 24, 20) : new Thickness(10, 18, 10, 18);
            _strip.CornerRadius = row ? new CornerRadius(14, 14, 0, 0) : new CornerRadius(14, 0, 0, 14);
            _strip.Margin = row ? new Thickness(16, 10, 16, 0) : new Thickness(0, 10, 0, 10);
            _openButton.Visibility = layout == Layout.Portrait ? Visibility.Visible : Visibility.Collapsed;
            // Half-open, the top half is short: just the cassette and the counter.
            _brand.Visibility = _bottom.Visibility = row ? Visibility.Collapsed : Visibility.Visible;
            // Rotated a quarter turn: x,y -> y, 320 - x (reads bottom to top).
            var upright = layout == Layout.Portrait;
            _cassetteFrame.Width = upright ? 200 : 320;
            _cassetteFrame.Height = upright ? 320 : 200;
            _cassetteTurn.Rotation = upright ? -90 : 0;
            _cassetteTurn.TranslateY = upright ? 320 : 0;
        }
        ShowRack(layout == Layout.Wide || (layout == Layout.Portrait && _rackOpen));
    }

    private void Place(FrameworkElement element, int column, int row)
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        Grid.SetColumnSpan(element, 1);
    }

    private void ShowRack(bool show)
    {
        _rack.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_layout == Layout.Portrait && show)
        {
            // Over the whole deck, with a way back.
            Grid.SetColumn(_rack, 0);
            Grid.SetColumnSpan(_rack, 3);
        }
        _closeRackButton.Visibility = _layout == Layout.Portrait ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================= Deck body =================

    private void BuildMain()
    {
        _main.Padding = new Thickness(18, 6, 14, 14);
        _main.RowSpacing = 10;
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            _main.RowDefinitions.Add(new RowDefinition { Height = height });

        // ▾ (close the player) and ⇄ (back to the normal player)
        var top = new Grid();
        top.Children.Add(IconButton("M1,1 L8,8 L15,1", 16, 9, "Close", () => CloseRequested?.Invoke(), HorizontalAlignment.Left));
        top.Children.Add(IconButton("M2,5 L16,5 M12,1 L16,5 L12,9 M18,15 L4,15 M8,11 L4,15 L8,19", 18, 18, "Normal player",
                                    () => ExitRequested?.Invoke(), HorizontalAlignment.Right));
        _main.Children.Add(top);

        var brand = _brand;
        brand.Children.Add(new TextBlock
        {
            Text = "LUNAMAN", FontFamily = DeckFont, FontSize = 40, CharacterSpacing = 160,
            Foreground = new SolidColorBrush(Cream), HorizontalAlignment = HorizontalAlignment.Center,
        });
        brand.Children.Add(new TextBlock
        {
            Text = "STEREO CASSETTE PLAYER  ·  LD-8", FontFamily = DeckFont, FontSize = 13, CharacterSpacing = 120,
            Foreground = B(0xFF9EA4AE), HorizontalAlignment = HorizontalAlignment.Center,
        });
        Grid.SetRow(brand, 1);
        _main.Children.Add(brand);

        // The cassette well: a dark recess with the cassette in it, as big as it fits.
        var well = new Border
        {
            Background = B(0xFF15181C), BorderBrush = B(0xFF3A404A), BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14),
            MaxWidth = 560, VerticalAlignment = VerticalAlignment.Center,
            Child = new Viewbox { Child = _cassetteFrame, Stretch = Stretch.Uniform },
        };
        var cassette = BuildCassette();
        cassette.RenderTransform = _cassetteTurn;
        _cassetteFrame.Children.Add(cassette);
        _cassetteHost.Children.Add(well);
        _cassetteHost.RenderTransform = _cassetteMove;
        Grid.SetRow(_cassetteHost, 2);
        _main.Children.Add(_cassetteHost);

        // OPERATION light + tape counter
        var status = new Grid { ColumnSpacing = 18, HorizontalAlignment = HorizontalAlignment.Center };
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var light = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var ledBox = new Grid { Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Center };
        _ledGlow.Width = _ledGlow.Height = 26;
        _ledGlow.Fill = new RadialGradientBrush
        {
            GradientStops = { new GradientStop { Color = C(0xAA4CFF7A), Offset = 0 }, new GradientStop { Color = C(0x004CFF7A), Offset = 1 } },
        };
        _ledGlow.Opacity = 0;
        _led.Width = _led.Height = 11;
        _led.Fill = B(0xFF24402C);
        _led.Stroke = B(0xFF0E1410);
        ledBox.Children.Add(_ledGlow);
        ledBox.Children.Add(_led);
        light.Children.Add(ledBox);
        light.Children.Add(Caption("OPERATION", 0xFF9EA4AE));
        status.Children.Add(light);

        var counter = new StackPanel { Spacing = 4 };
        var digits = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
        for (var i = 0; i < 4; i++)
        {
            _digits[i] = new TextBlock { Text = "0", FontFamily = DeckFont, FontSize = 26, Foreground = new SolidColorBrush(Cream),
                                         HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            digits.Children.Add(new Border
            {
                Width = 24, Height = 34, CornerRadius = new CornerRadius(3), Background = B(0xFF0D0F12),
                BorderBrush = B(0xFF2E333B), BorderThickness = new Thickness(1), Child = _digits[i],
            });
            if (i == 1)
                digits.Children.Add(new TextBlock { Text = ":", FontFamily = DeckFont, FontSize = 24, Foreground = new SolidColorBrush(Cream), VerticalAlignment = VerticalAlignment.Center });
        }
        counter.Children.Add(digits);
        counter.Children.Add(Caption("TAPE COUNTER", 0xFF9EA4AE));
        Grid.SetColumn(counter, 1);
        status.Children.Add(counter);
        Grid.SetRow(status, 3);
        _main.Children.Add(status);

        // Album + OPEN ▶ (the rack, on a folded phone)
        var bottom = _bottom;
        _albumText.FontFamily = DeckFont;
        _albumText.FontSize = 14;
        _albumText.CharacterSpacing = 80;
        _albumText.Foreground = B(0xFF9EA4AE);
        _albumText.TextTrimming = TextTrimming.CharacterEllipsis;
        _albumText.HorizontalAlignment = HorizontalAlignment.Center;
        bottom.Children.Add(_albumText);
        _openButton.Content = new TextBlock { Text = "OPEN  ▶", FontFamily = DeckFont, FontSize = 20, CharacterSpacing = 120, Foreground = new SolidColorBrush(Cream) };
        StyleFlat(_openButton);
        _openButton.HorizontalAlignment = HorizontalAlignment.Center;
        _openButton.Click += (_, _) => { _rackOpen = true; ApplyLayout(); };
        bottom.Children.Add(_openButton);
        Grid.SetRow(bottom, 4);
        _main.Children.Add(bottom);
    }

    /// <summary>The cassette, drawn on a 320 x 200 canvas (the Viewbox scales it).</summary>
    private Canvas BuildCassette()
    {
        var c = new Canvas { Width = 320, Height = 200 };

        c.Children.Add(At(new Rectangle
        {
            Width = 320, Height = 200, RadiusX = 12, RadiusY = 12, Stroke = B(0xFFC9BFA9), StrokeThickness = 1.5,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops = { new GradientStop { Color = C(0xFFF3EDE0), Offset = 0 }, new GradientStop { Color = C(0xFFE1D8C4), Offset = 1 } },
            },
        }, 0, 0));
        foreach (var (x, y) in new[] { (14.0, 14.0), (306.0, 14.0), (14.0, 186.0), (306.0, 186.0) })
            c.Children.Add(Screw(x, y));

        // The label: two stripes, ruled lines, the title in handwriting, the cover as a sticker.
        c.Children.Add(At(new Rectangle { Width = 280, Height = 122, RadiusX = 6, RadiusY = 6, Fill = B(0xFFFBF8F0), Stroke = B(0xFFE3DCCB), StrokeThickness = 1 }, 20, 14));
        c.Children.Add(At(new Rectangle { Width = 280, Height = 7, Fill = new SolidColorBrush(Orange) }, 20, 20));
        c.Children.Add(At(new Rectangle { Width = 280, Height = 3, Fill = B(0xFF35609A) }, 20, 29));
        foreach (var y in new[] { 62.0, 78.0 })
            c.Children.Add(At(new Rectangle { Width = 200, Height = 1, Fill = B(0x22000000) }, 30, y));

        _labelTitle.FontFamily = HandFont;
        _labelTitle.FontSize = 19;
        _labelTitle.Foreground = B(0xFF2A2F3A);
        _labelTitle.Width = 206;
        _labelTitle.MaxLines = 1;
        _labelTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        c.Children.Add(At(_labelTitle, 30, 34));
        _labelArtist.FontFamily = HandFont;
        _labelArtist.FontSize = 13;
        _labelArtist.Foreground = B(0xFF5A5F6A);
        _labelArtist.Width = 206;
        _labelArtist.MaxLines = 1;
        _labelArtist.TextTrimming = TextTrimming.CharacterEllipsis;
        c.Children.Add(At(_labelArtist, 30, 60));
        c.Children.Add(At(new TextBlock { Text = "A", FontFamily = DeckFont, FontSize = 18, Foreground = B(0xFF2A2F3A) }, 26, 112));

        _labelArt.Width = _labelArt.Height = 44;
        _labelArt.CornerRadius = new CornerRadius(3);
        _labelArt.BorderBrush = B(0xFFFFFFFF);
        _labelArt.BorderThickness = new Thickness(2);
        _labelArt.RenderTransform = new RotateTransform { Angle = 5, CenterX = 22, CenterY = 22 };
        c.Children.Add(At(_labelArt, 246, 36));

        // The window with the reels: the tape on each reel (clipped by the window) and the hubs.
        var window = new Canvas { Width = 168, Height = 50, Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 168, 50) } };
        window.Children.Add(new Rectangle { Width = 168, Height = 50, RadiusX = 8, RadiusY = 8, Fill = B(0xFF3B3430) });
        window.Children.Add(Reel(_tapeLeft, _hubLeft, LeftX - 76));
        window.Children.Add(Reel(_tapeRight, _hubRight, RightX - 76));
        c.Children.Add(At(window, 76, ReelY - 25));
        c.Children.Add(At(new Rectangle { Width = 168, Height = 50, RadiusX = 8, RadiusY = 8, Stroke = B(0xFF8F8676), StrokeThickness = 2 }, 76, ReelY - 25));

        // The head opening at the bottom.
        c.Children.Add(new Path
        {
            Data = Geometry("M66,200 L82,152 L238,152 L254,200 Z"),
            Fill = B(0xFFD6CDB8), Stroke = B(0xFFC2B8A2), StrokeThickness = 1,
        });
        foreach (var x in new[] { 104.0, 136.0, 178.0, 210.0 })
            c.Children.Add(At(new Ellipse { Width = 8, Height = 8, Fill = B(0xFF3B3430) }, x, 172));
        return c;
    }

    private static UIElement Reel(Ellipse tape, RotateTransform spin, double cx)
    {
        var reel = new Canvas();
        tape.Fill = new RadialGradientBrush
        {
            GradientStops = { new GradientStop { Color = C(0xFF5A4030), Offset = 0.6 }, new GradientStop { Color = C(0xFF2E2018), Offset = 1 } },
        };
        reel.Children.Add(tape);
        var hub = new Canvas { Width = 24, Height = 24, RenderTransform = spin };
        spin.CenterX = spin.CenterY = 12;
        hub.Children.Add(new Ellipse { Width = 24, Height = 24, Fill = B(0xFFF5F2EA), Stroke = B(0xFFBDB5A3), StrokeThickness = 1 });
        hub.Children.Add(At(new Ellipse { Width = 10, Height = 10, Fill = B(0xFF3B3430) }, 7, 7));
        for (var i = 0; i < 6; i++)
        {
            var tooth = new Rectangle { Width = 3, Height = 4, Fill = B(0xFFF5F2EA) };
            tooth.RenderTransform = new RotateTransform { Angle = i * 60, CenterX = 1.5, CenterY = 5 };
            hub.Children.Add(At(tooth, 10.5, 7));
        }
        reel.Children.Add(At(hub, cx - 12, 25 - 12));
        reel.Tag = cx;
        return reel;
    }

    private static UIElement Screw(double x, double y)
    {
        var g = new Canvas();
        g.Children.Add(At(new Ellipse { Width = 10, Height = 10, Fill = B(0xFFBFB6A3), Stroke = B(0xFF9C9482), StrokeThickness = 1 }, x - 5, y - 5));
        g.Children.Add(At(new Rectangle { Width = 6, Height = 1.4, Fill = B(0xFF7E7666), RenderTransform = new RotateTransform { Angle = 35, CenterX = 3, CenterY = 0.7 } }, x - 3, y - 0.7));
        return g;
    }

    // ================= Keys strip =================

    private void BuildStrip()
    {
        _strip.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0),
            GradientStops = { new GradientStop { Color = C(0xFFE3E6EA), Offset = 0 }, new GradientStop { Color = C(0xFFB7BCC4), Offset = 1 } },
        };
        _strip.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _strip.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _playIcon = new Path();
        _playKey = new Key("PLAY", _playIcon, orange: true);
        _playKey.Clicked += () => PlayPauseRequested?.Invoke();
        var ff = new Key("FF", Icon("M0,0 L8,7 L0,14 Z M8,0 L16,7 L8,14 Z"));
        ff.Clicked += () => NextRequested?.Invoke();
        ff.Held += held => Wind(held ? 1 : 0);
        var rew = new Key("REW", Icon("M16,0 L8,7 L16,14 Z M8,0 L0,7 L8,14 Z"));
        rew.Clicked += () => PreviousRequested?.Invoke();
        rew.Held += held => Wind(held ? -1 : 0);
        var stop = new Key("STOP/EJECT", Icon("M0,0 L14,0 L14,9 L0,9 Z M0,12 L7,4 L14,12 Z M0,14 L14,14 L14,16 L0,16 Z", 14, 16));
        stop.Clicked += () =>
        {
            StopRequested?.Invoke();
            Eject(true);
        };
        SetState(false, 0, 0);
        foreach (var key in new[] { _playKey, ff, rew, stop })
            _keys.Children.Add(key);
        _strip.Children.Add(_keys);

        _volume.ValueChanged += (_, e) =>
        {
            if (!_settingVolume)
                VolumeChanged?.Invoke(e.NewValue);
        };
        var volume = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        volume.Children.Add(Caption("VOLUME", 0xFF3C424C));
        volume.Children.Add(_volume);
        _volume.HorizontalAlignment = HorizontalAlignment.Center;
        _stripExtras.Children.Add(volume);

        _loveLabel = Caption("LOVE", 0xFF3C424C);
        _loveButton = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17), HorizontalAlignment = HorizontalAlignment.Center,
            BorderBrush = B(0xFFB4621C), BorderThickness = new Thickness(1),
            Background = new RadialGradientBrush
            {
                GradientStops = { new GradientStop { Color = C(0xFFFFB061), Offset = 0 }, new GradientStop { Color = Orange, Offset = 1 } },
            },
        };
        _loveButton.Tapped += (_, e) =>
        {
            e.Handled = true;
            Services.Haptics.Confirm();
            Motion.Pop(_loveButton);
            FavoriteRequested?.Invoke();
        };
        var love = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        love.Children.Add(_loveButton);
        love.Children.Add(_loveLabel);
        _stripExtras.Children.Add(love);
        Grid.SetRow(_stripExtras, 1);
        _strip.Children.Add(_stripExtras);
        SetFavorite(null);
    }

    private void Wind(int direction)
    {
        _winding = direction;
        if (direction != 0)
        {
            SeekByRequested?.Invoke(direction * 6);
            _hold.Start();
        }
        else
        {
            _hold.Stop();
        }
        UpdateTimer();
    }

    /// <summary>A chunky silver key with its name above it; tap, or hold (FF / REW).</summary>
    private sealed class Key : StackPanel
    {
        public event Action? Clicked;
        public event Action<bool>? Held;
        private readonly Border _cap;
        private readonly TranslateTransform _press = new();
        private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
        private bool _down, _holding;

        public Key(string label, Path icon, bool orange = false)
        {
            Spacing = 4;
            Children.Add(Caption(label, 0xFF3C424C));
            icon.Fill = orange ? B(0xFF2A1A0A) : B(0xFF2A2F3A);
            icon.Width = 18;
            icon.Height = 14;
            icon.Stretch = Stretch.Uniform;
            _cap = new Border
            {
                Height = 40, MinWidth = 64, CornerRadius = new CornerRadius(6),
                BorderBrush = orange ? B(0xFFA9530F) : B(0xFF7D838D), BorderThickness = new Thickness(1, 1, 1, 4),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = orange ? C(0xFFFFA348) : C(0xFFF7F8FA), Offset = 0 },
                        new GradientStop { Color = orange ? C(0xFFE5761A) : C(0xFFC9CED6), Offset = 1 },
                    },
                },
                Child = icon,
                RenderTransform = _press,
            };
            Children.Add(_cap);

            _holdTimer.Tick += (_, _) =>
            {
                _holdTimer.Stop();
                if (!_down || Held == null)
                    return;
                _holding = true;
                Services.Haptics.Tick();
                Held(true);
            };
            _cap.PointerPressed += (_, e) =>
            {
                _down = true;
                _holding = false;
                _cap.CapturePointer(e.Pointer);
                Press(true);
                _holdTimer.Start();
                e.Handled = true;
            };
            _cap.PointerReleased += (_, e) =>
            {
                if (!_down)
                    return;
                var wasHolding = _holding;
                Release();
                if (wasHolding)
                    Held?.Invoke(false);
                else
                {
                    Services.Haptics.Confirm();
                    Clicked?.Invoke();
                }
                e.Handled = true;
            };
            _cap.PointerCaptureLost += (_, _) =>
            {
                if (!_down)
                    return;
                var wasHolding = _holding;
                Release();
                if (wasHolding)
                    Held?.Invoke(false);
            };
        }

        private void Release()
        {
            _down = false;
            _holding = false;
            _holdTimer.Stop();
            Press(false);
        }

        private void Press(bool down)
        {
            _press.Y = down ? 3 : 0;
            _cap.BorderThickness = new Thickness(1, 1, 1, down ? 1 : 4);
        }
    }

    // ================= Cassette rack =================

    private void BuildRack()
    {
        _rack.Background = B(0xFF14171B);
        _rack.Padding = new Thickness(14, 12, 14, 12);
        _rack.RowSpacing = 10;
        _rack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _rack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _rack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _rack.Visibility = Visibility.Collapsed;
        Canvas.SetZIndex(_rack, 5);

        var header = new Grid();
        header.Children.Add(new TextBlock
        {
            Text = "CASSETTE RACK", FontFamily = DeckFont, FontSize = 22, CharacterSpacing = 120,
            Foreground = new SolidColorBrush(Cream), VerticalAlignment = VerticalAlignment.Center,
        });
        _tapeCount.FontFamily = DeckFont;
        _tapeCount.FontSize = 14;
        _tapeCount.CharacterSpacing = 80;
        _tapeCount.Foreground = B(0xFF9EA4AE);
        _tapeCount.HorizontalAlignment = HorizontalAlignment.Right;
        _tapeCount.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_tapeCount);
        _rack.Children.Add(header);

        var shelf = new Border
        {
            Background = B(0xFF0C0E11), CornerRadius = new CornerRadius(8), Padding = new Thickness(8),
            BorderBrush = B(0xFF2A2F37), BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = _tapeList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        Grid.SetRow(shelf, 1);
        _rack.Children.Add(shelf);

        var footer = new Grid();
        footer.Children.Add(new TextBlock
        {
            Text = "TAP A TAPE TO PLAY IT", FontFamily = DeckFont, FontSize = 14, CharacterSpacing = 80,
            Foreground = B(0xFF9EA4AE), VerticalAlignment = VerticalAlignment.Center,
        });
        _closeRackButton.Content = new TextBlock { Text = "CLOSE LID  ▲", FontFamily = DeckFont, FontSize = 18, CharacterSpacing = 100, Foreground = new SolidColorBrush(Cream) };
        StyleFlat(_closeRackButton);
        _closeRackButton.HorizontalAlignment = HorizontalAlignment.Right;
        _closeRackButton.Click += (_, _) => { _rackOpen = false; ApplyLayout(); };
        footer.Children.Add(_closeRackButton);
        Grid.SetRow(footer, 2);
        _rack.Children.Add(footer);
    }

    private static readonly uint[] SpineColors =
        { 0xFFEDE3CC, 0xFFE9B949, 0xFF24346B, 0xFF2F5DA8, 0xFFF4F4F2, 0xFFB8402F, 0xFF2C7A7B, 0xFF1E1E1E, 0xFFD9A5B3, 0xFF6B8F4E };

    /// <summary>A playlist as a tape spine: its cover, its name in handwriting, its song count.</summary>
    private UIElement Spine(Playlist playlist)
    {
        var color = SpineColors[(int)((uint)StableHash(playlist.Id) % SpineColors.Length)];
        var light = ((color >> 16 & 0xFF) * 299 + (color >> 8 & 0xFF) * 587 + (color & 0xFF) * 114) / 1000 > 140;
        var ink = light ? B(0xFF23262D) : B(0xFFF4F1EA);

        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(6, 4, 10, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cover = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(3), Background = B(0x33000000) };
        if (!string.IsNullOrEmpty(playlist.CoverArtUrl))
            cover.Background = new ImageBrush
            {
                ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(playlist.CoverArtUrl)) { DecodePixelWidth = 64 },
                Stretch = Stretch.UniformToFill,
            };
        grid.Children.Add(cover);
        var name = new TextBlock
        {
            Text = playlist.Name, FontFamily = HandFont, FontSize = 17, Foreground = ink,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var count = new TextBlock
        {
            Text = $"C-{playlist.SongCount}", FontFamily = DeckFont, FontSize = 15, Foreground = ink, Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(count, 2);
        grid.Children.Add(count);

        var spine = new Border
        {
            Height = 46, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(C(color)),
            BorderBrush = B(0x40000000), BorderThickness = new Thickness(0, 0, 0, 3), Child = grid,
        };
        Motion.SetPressScale(spine, true);
        spine.Tapped += (_, e) =>
        {
            e.Handled = true;
            Services.Haptics.Confirm();
            TapeChosen?.Invoke(playlist);
            if (_layout == Layout.Portrait)
            {
                _rackOpen = false;
                ApplyLayout();
            }
        };
        return spine;
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 17;
            foreach (var ch in s)
                h = h * 31 + ch;
            return h;
        }
    }

    // ================= Animation =================

    private void UpdateTimer()
    {
        var run = _active && (_playing || _winding != 0);
        if (run && !_frame.IsEnabled)
        {
            _lastFrame = DateTime.UtcNow;
            _frame.Start();
        }
        else if (!run && _frame.IsEnabled)
        {
            _frame.Stop();
        }
    }

    /// <summary>Tape radius on each reel: the tape's area moves from left to right as the song plays.</summary>
    private (double left, double right) Radii()
    {
        var span = MaxTape * MaxTape - MinTape * MinTape;
        return (Math.Sqrt(MinTape * MinTape + (1 - _progress) * span), Math.Sqrt(MinTape * MinTape + _progress * span));
    }

    private void UpdateReels()
    {
        var (left, right) = Radii();
        SizeTape(_tapeLeft, LeftX - 76, left);
        SizeTape(_tapeRight, RightX - 76, right);
    }

    private static void SizeTape(Ellipse tape, double cx, double r)
    {
        tape.Width = tape.Height = r * 2;
        Canvas.SetLeft(tape, cx - r);
        Canvas.SetTop(tape, 25 - r);
    }

    private void Animate()
    {
        var now = DateTime.UtcNow;
        var dt = Math.Min((now - _lastFrame).TotalSeconds, 0.1);
        _lastFrame = now;
        // Constant tape speed: the reel with less tape on it turns faster, like a real one.
        var speed = (_winding != 0 ? 9 : 1) * 46.0 * (_winding < 0 ? -1 : 1);
        var (left, right) = Radii();
        _hubLeft.Angle = (_hubLeft.Angle + speed / left * dt * 180 / Math.PI) % 360;
        _hubRight.Angle = (_hubRight.Angle + speed / right * dt * 180 / Math.PI) % 360;
    }

    private void SetCounter(double seconds)
    {
        var total = (int)Math.Max(0, seconds);
        var text = $"{Math.Min(total / 60, 99):D2}{total % 60:D2}";
        if (text == _lastCounter)
            return;
        for (var i = 0; i < 4; i++)
        {
            if (_lastCounter.Length == 4 && _lastCounter[i] == text[i])
                continue;
            _digits[i].Text = text[i].ToString();
            if (_lastCounter.Length == 4)
                Roll(_digits[i]);
        }
        _lastCounter = text;
    }

    /// <summary>A digit rolls in from above, like a mechanical counter.</summary>
    private static void Roll(TextBlock digit)
    {
        var move = digit.RenderTransform as TranslateTransform ?? new TranslateTransform();
        digit.RenderTransform = move;
        Run(move, nameof(TranslateTransform.Y), -10, 0, 160);
    }

    private void Eject(bool eject)
    {
        if (_ejected == eject)
            return;
        _ejected = eject;
        Run(_cassetteMove, nameof(TranslateTransform.Y), eject ? 0 : -18, eject ? -18 : 0, 260);
    }

    /// <summary>A new song: the old cassette slides out, the new one in.</summary>
    private void SwapCassette()
    {
        _ejected = false;
        Run(_cassetteMove, nameof(TranslateTransform.X), -40, 0, 320);
        var fade = new DoubleAnimation { From = 0.2, To = 1, Duration = TimeSpan.FromMilliseconds(320) };
        Storyboard.SetTarget(fade, _cassetteHost);
        Storyboard.SetTargetProperty(fade, nameof(Opacity));
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Begin();
    }

    private static void Run(DependencyObject target, string property, double from, double to, int ms)
    {
        var animation = new DoubleAnimation
        {
            From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    // ================= Helpers =================

    private static T At<T>(T element, double x, double y) where T : UIElement
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        return element;
    }

    private static Geometry Geometry(string data) =>
        (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data);

    private static Path Icon(string data, double w = 18, double h = 14) =>
        new() { Data = Geometry(data), Width = w, Height = h, Stretch = Stretch.Uniform };

    private static TextBlock Caption(string text, uint color) => new()
    {
        Text = text, FontFamily = DeckFont, FontSize = 12, CharacterSpacing = 100,
        Foreground = B(color), HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static void StyleFlat(Button button)
    {
        button.Background = B(0x00000000);
        button.BorderThickness = new Thickness(0);
        button.Padding = new Thickness(10, 4, 10, 4);
    }

    private static Button IconButton(string data, double w, double h, string name, Action click, HorizontalAlignment align)
    {
        var button = new Button
        {
            Width = 40, Height = 40, HorizontalAlignment = align, Padding = new Thickness(0),
            Content = new Path { Data = Geometry(data), Width = w, Height = h, Stretch = Stretch.Uniform, Stroke = new SolidColorBrush(Cream), StrokeThickness = 2 },
        };
        StyleFlat(button);
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }
}

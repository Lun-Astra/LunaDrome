using LunaDrome.Controls;
using LunaDrome.Core.Models;
using LunaDrome.Core.Services;
using LunaDrome.Services;

namespace LunaDrome;

// Settings > Appearance, the parts past theme and accent: the custom theme's color wheel and the
// app background. The background is one layer under every page (AppBackdrop in MainPage.xaml):
// a glow in the accent color, the playing song's cover blurred, or an own picture; the strength
// setting is how much of it shows over the theme color.
public sealed partial class MainPage
{
    private static readonly (string Style, string Label, string Hint)[] BackgroundChoices =
    {
        ("Plain", "Plain", "Just the theme color."),
        ("Glow", "Glow", "A soft glow in your accent color from the top corner."),
        ("NowPlaying", "Now playing", "The playing song's cover, blurred, behind everything. It changes with the song."),
        ("Picture", "Picture", "A picture of your own behind everything."),
    };

    private string? _backdropCoverUrl;
    private string? _backdropKey;   // what AppBackdropImage shows now, so it isn't decoded again
    private int _backdropToken;
    private bool _settingBackgroundControls;
    private bool _settingThemePicker;
    private DispatcherTimer? _themeSaveTimer;

    private void ApplyAppearance()
    {
        Themes.Apply(_prefs.Theme, _prefs.Accent);
        ApplyBackdrop();
    }

    private async void ApplyBackdrop()
    {
        var style = _prefs.BackgroundStyle;
        var strength = Math.Clamp(_prefs.BackgroundStrength, 5, 60) / 100.0;
        if (style is not ("Glow" or "NowPlaying" or "Picture"))
        {
            AppBackdrop.Visibility = Visibility.Collapsed;
            AppBackdropImage.Source = null;
            _backdropKey = null;
            return;
        }
        AppBackdrop.Visibility = Visibility.Visible;

        if (style == "Glow")
        {
            AppBackdrop.Opacity = 1;   // the strength is in the gradient's alpha
            AppBackdropGlow.Fill = GlowBrush(Themes.CurrentAccent, strength);
            AppBackdropImage.Source = null;
            _backdropKey = null;
            return;
        }

        AppBackdropGlow.Fill = null;
        // On the layer, not the image: the image's own opacity is the fade-in when it changes.
        AppBackdrop.Opacity = strength;
        var path = PreferencesService.BackgroundPicturePath;
        var key = style == "Picture"
            ? $"picture|{_prefs.BackgroundBlur}|{(File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0)}"
            : $"cover|{_backdropCoverUrl}";
        if (key == _backdropKey)
            return;
        _backdropKey = key;

        // Decoding happens off the UI thread; a newer pick (or the next song) wins.
        var token = ++_backdropToken;
        var image = style == "Picture"
            ? await BlurredArt.LoadBackdropFileAsync(path, _prefs.BackgroundBlur)
            : await BlurredArt.LoadBackdropAsync(_backdropCoverUrl);
        if (token != _backdropToken)
            return;
        AppBackdropImage.Source = image;
        if (image != null)
            Motion.FadeIn(AppBackdropImage);
    }

    // The accent from the top-left corner, fading out by about two thirds down the window.
    private static LinearGradientBrush GlowBrush(Windows.UI.Color accent, double strength)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0.6, 1),
        };
        var alpha = (byte)Math.Round(Math.Min(1, strength * 1.4) * 255);
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(alpha, accent.R, accent.G, accent.B), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb((byte)(alpha / 4), accent.R, accent.G, accent.B), Offset = 0.45 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, accent.R, accent.G, accent.B), Offset = 0.8 });
        return brush;
    }

    /// <summary>Called whenever the playing song changes (null = nothing playing).</summary>
    private void ShowBackdropFor(Song? song)
    {
        _backdropCoverUrl = song == null ? null
            : !string.IsNullOrEmpty(song.AlbumId) ? _client.GetCoverArtUrl(song.AlbumId, 300)
            : song.CoverArtUrl;
        if (_prefs.BackgroundStyle == "NowPlaying")
            ApplyBackdrop();
    }

    // ================= Settings =================

    private void BuildBackgroundChoices()
    {
        BackgroundOptions.Children.Clear();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        BackgroundOptions.Children.Add(row);
        foreach (var (style, label, _) in BackgroundChoices)
        {
            if (row.Children.Count >= (IsCompact ? 3 : int.MaxValue))
            {
                row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                BackgroundOptions.Children.Add(row);
            }
            var selected = _prefs.BackgroundStyle == style;
            var chip = new Border
            {
                Padding = new Thickness(16, 7, 16, 7),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = (Brush)Application.Current.Resources[selected ? "AccentBrush" : "BorderBrush2"],
                Background = (Brush)Application.Current.Resources["RaisedBrush"],
                Child = new TextBlock { Text = label },
            };
            row.Children.Add(ChoiceButton(chip, label, () =>
            {
                _prefs.BackgroundStyle = style;
                if (style == "Picture" && !File.Exists(PreferencesService.BackgroundPicturePath))
                    _ = PickBackgroundPictureAsync();
            }));
        }

        var current = BackgroundChoices.FirstOrDefault(c => c.Style == _prefs.BackgroundStyle);
        var isPicture = _prefs.BackgroundStyle == "Picture";
        BackgroundPicturePanel.Visibility = isPicture ? Visibility.Visible : Visibility.Collapsed;
        BackgroundPictureButton.Content = File.Exists(PreferencesService.BackgroundPicturePath) ? "Change picture…" : "Choose picture…";
        BackgroundStrengthPanel.Visibility = current.Style is null or "Plain" ? Visibility.Collapsed : Visibility.Visible;
        BackgroundHintText.Text = isPicture && !File.Exists(PreferencesService.BackgroundPicturePath)
            ? "Choose a picture to show behind everything."
            : current.Hint ?? BackgroundChoices[0].Hint;

        _settingBackgroundControls = true;
        BackgroundBlurCheck.IsChecked = _prefs.BackgroundBlur;
        BackgroundStrengthSlider.Value = Math.Clamp(_prefs.BackgroundStrength, 5, 60);
        BackgroundStrengthText.Text = $"Strength: {Math.Clamp(_prefs.BackgroundStrength, 5, 60)}%";
        _settingBackgroundControls = false;
    }

    private void BackgroundStrengthSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_settingBackgroundControls)
            return;
        _prefs.BackgroundStrength = (int)Math.Round(e.NewValue);
        BackgroundStrengthText.Text = $"Strength: {_prefs.BackgroundStrength}%";
        ApplyBackdrop();
        PreferencesService.SaveInBackground(_prefs);
    }

    private void BackgroundBlurCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_settingBackgroundControls)
            return;
        _prefs.BackgroundBlur = BackgroundBlurCheck.IsChecked == true;
        ApplyBackdrop();
        PreferencesService.Save(_prefs);
    }

    private async void BackgroundPictureButton_Click(object sender, RoutedEventArgs e) => await PickBackgroundPictureAsync();

    // The picked file is copied (shrunk) into the settings folder, so moving or deleting the
    // original later doesn't take the background away.
    private async Task PickBackgroundPictureAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            };
            foreach (var type in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" })
                picker.FileTypeFilter.Add(type);
            var file = await picker.PickSingleFileAsync();
            if (file == null)
                return;

            byte[] bytes;
            using (var stream = await file.OpenStreamForReadAsync())
            using (var copy = new MemoryStream())
            {
                await stream.CopyToAsync(copy);
                bytes = copy.ToArray();
            }
            var shrunk = await Task.Run(() => BlurredArt.ShrinkPicture(bytes));
            if (shrunk == null)
            {
                ShowToast($"{file.Name} isn't a picture LunaDrome can open");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesService.BackgroundPicturePath)!);
            await File.WriteAllBytesAsync(PreferencesService.BackgroundPicturePath, shrunk);

            _prefs.BackgroundStyle = "Picture";
            PreferencesService.Save(_prefs);
            ApplyBackdrop();
            BuildSettingsChoices();
        }
        catch (Exception ex)
        {
            ShowToast($"Couldn't open the picture: {ex.Message}");
        }
    }

    // ================= Custom theme =================

    private void ShowThemeColorPicker()
    {
        if (!Themes.TryAccentColor(_prefs.CustomTheme, out var color))
            return;
        _settingThemePicker = true;
        ThemeColorPicker.Color = color;
        _settingThemePicker = false;
        ThemeColorPicker.Visibility = Visibility.Visible;
    }

    // Like the accent wheel: live while dragging, saved (and the chips redrawn) once it settles.
    private void ThemeColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_settingThemePicker)
            return;

        var hex = Themes.ToHex(args.NewColor);
        _prefs.CustomTheme = hex;
        _prefs.Theme = hex;
        ApplyAppearance();

        if (_themeSaveTimer == null)
        {
            _themeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _themeSaveTimer.Tick += (_, _) =>
            {
                _themeSaveTimer.Stop();
                PreferencesService.Save(_prefs);
                BuildSettingsChoices();
            };
        }
        _themeSaveTimer.Stop();
        _themeSaveTimer.Start();
    }
}

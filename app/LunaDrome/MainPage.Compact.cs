using Microsoft.UI.Xaml.Media;

namespace LunaDrome;

// Compact layout for narrow windows (phones, or a very narrow desktop window). The desktop
// layout uses fixed widths - a 170px sidebar, a 420px queue panel and a 300/*/200 playback
// bar - which don't fit on a ~400px phone screen. Below CompactWidth: the sidebar becomes a
// ☰ overlay, the queue panel opens full-width over the page, the playback bar keeps only the
// now-playing info and the transport controls, and the margins shrink.
public sealed partial class MainPage
{
    private const double CompactWidth = 700;
    private bool? _compact;
    private bool _compactSidebarOpen;

    private void InitCompactLayout()
    {
        SizeChanged += (_, e) =>
        {
            ApplyCompactLayout(e.NewSize.Width < CompactWidth);
            if (IsCompact)
                UpdateCompactWidths(e.NewSize.Width);
        };
#if ANDROID
        // Android draws the app edge-to-edge, under the status bar and the navigation bar;
        // keep the whole page inside the visible area instead.
        Uno.UI.Toolkit.VisibleBoundsPadding.SetPaddingMask(PageRoot, Uno.UI.Toolkit.VisibleBoundsPadding.PaddingMask.All);
        VisualizerUnavailableText.Visibility = Visibility.Visible;
        VisualizerCanvas.Visibility = Visibility.Collapsed;
#endif
    }

    private bool IsCompact => _compact == true;

    private void ApplyCompactLayout(bool compact)
    {
        if (_compact == compact)
            return;
        _compact = compact;

        MainContentArea.Margin = compact ? new Thickness(8, 8, 8, 6) : new Thickness(20, 20, 20, 12);
        SidebarColumn.Width = new GridLength(compact ? 0 : 170);
        SidebarGapColumn.Width = new GridLength(compact ? 0 : 20);
        // #25: phones navigate with the bottom bar; the ☰/✕ button only shows as ✕ while the
        // side menu ("More") is open.
        CompactNavBar.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetZIndex(CompactMenuButton, 20);   // stays above the open menu, so ✕ can close it
        PageArea.Margin = new Thickness(0);

        // Sidebar: its own column on desktop; on a phone the "More" menu, covering the whole page
        // (a narrow panel over a half-visible page looked broken). Home / Artists / Albums /
        // Tracks are in the bottom bar already, so the menu leaves them out.
        Grid.SetColumnSpan(SidebarPane, compact ? 4 : 1);
        SidebarPane.HorizontalAlignment = HorizontalAlignment.Stretch;
        SidebarPane.Width = double.NaN;
        SidebarPane.Padding = compact ? new Thickness(16, 52, 16, 8) : new Thickness(0);
        NavHome.Visibility = NavArtists.Visibility = NavAlbums.Visibility = NavSongs.Visibility =
            compact ? Visibility.Collapsed : Visibility.Visible;
        // Search is its own tab on a phone, with the box on the Search page.
        SidebarSearchGrid.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PageSearchGrid.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        SearchPageContent.Margin = compact ? new Thickness(0, 0, 0, 16) : new Thickness(0, 0, 16, 24);
        SidebarPane.Background = compact ? (Brush)Application.Current.Resources["PanelBrush"] : null;
        Canvas.SetZIndex(SidebarPane, compact ? 10 : 0);
        _compactSidebarOpen = false;
        UpdateCompactSidebar();

        // Queue / lyrics / visualizer panel: beside the page on desktop, over it in compact mode.
        Grid.SetColumn(QueueSidePanel, compact ? 0 : 3);
        Grid.SetColumnSpan(QueueSidePanel, compact ? 4 : 1);
        QueueSidePanel.Width = compact ? double.NaN : 420;
        QueueSidePanel.Margin = compact ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        Canvas.SetZIndex(QueueSidePanel, compact ? 9 : 0);

        // Home hero card and album header: smaller cover and title so the text isn't cut off.
        HomeHeroCard.Height = compact ? 150 : 232;
        HeroInnerGrid.Margin = compact ? new Thickness(44, 12, 44, 12) : new Thickness(58, 28, 58, 28);
        HeroArtBox.Width = HeroArtBox.Height = compact ? 104 : 176;
        HeroTextPanel.Margin = compact ? new Thickness(12, 0, 0, 0) : new Thickness(28, 0, 0, 0);
        HeroTitleText.FontSize = compact ? 20 : 30;
        HeroArtistText.FontSize = compact ? 14 : 16;
        AlbumHeaderGrid.Height = compact ? 190 : 290;
        AlbumHeaderInner.Margin = compact ? new Thickness(14, 0, 14, 14) : new Thickness(32, 0, 28, 32);
        AlbumHeaderArtBox.Width = AlbumHeaderArtBox.Height = compact ? 120 : 200;
        AlbumHeaderTextPanel.Margin = compact ? new Thickness(14, 0, 0, 0) : new Thickness(20, 0, 0, 0);
        AlbumHeaderTitleText.FontSize = compact ? 24 : 44;
        if (!compact)
            ((Controls.TrackColumnLayout)Resources["TrackColumns"]).SetCompact(false, 0);
        BuildSettingsChoices();   // theme chips wrap differently on a phone
        CompactActionButtons(AlbumActionButtons, compact);

        // Artist page: the same header treatment, and the smaller search/album-grid cards so two
        // fit side by side (the 172px ones left a single column, cut off on the right).
        ArtistHeaderGrid.Height = compact ? 190 : 290;
        ArtistHeaderInner.Margin = compact ? new Thickness(14, 0, 10, 14) : new Thickness(32, 0, 28, 32);
        ArtistHeaderArtBox.Width = ArtistHeaderArtBox.Height = compact ? 88 : 200;
        ArtistHeaderStatsText.FontSize = compact ? 12 : 14;
        // The 16px right gutter is for the desktop scrollbar; phones have 8px page margins already.
        ArtistDetailContent.Margin = AlbumPageContent.Margin = compact ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 16, 16);
        ArtistHeaderTextPanel.Margin = compact ? new Thickness(12, 0, 0, 0) : new Thickness(20, 0, 0, 0);
        ArtistHeaderNameText.FontSize = compact ? 24 : 44;
        CompactActionButtons(ArtistActionButtons, compact);
        ArtistAlbumsList.ItemTemplate = (DataTemplate)Resources[compact ? "LargeAlbumCardTemplate" : "FeishinAlbumCardTemplate"];
        ArtistAlbumsLayout.MinItemWidth = compact ? 162 : 184;
        ArtistAlbumsLayout.MinItemHeight = compact ? 224 : 268;

        // Full-screen player: cover + title as a row on top, Queue / Lyrics / Visualizer below it
        // (side by side they need ~900px).
        FullScreenLayoutGrid.Margin = compact ? new Thickness(16, 60, 16, 12) : new Thickness(80, 40, 80, 60);
        FullScreenGapColumn.Width = new GridLength(compact ? 0 : 60);
        FullScreenSideColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(FullScreenNowPanel, 0);
        Grid.SetRowSpan(FullScreenNowPanel, compact ? 1 : 2);
        Grid.SetColumnSpan(FullScreenNowPanel, compact ? 3 : 1);
        FullScreenNowPanel.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
        FullScreenNowPanel.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        FullScreenNowPanel.Margin = compact ? new Thickness(0, 0, 0, 16) : new Thickness(0);
        FullScreenArtBox.Width = FullScreenArtBox.Height = compact ? 88 : 360;
        FullScreenTextPanel.Margin = compact ? new Thickness(14, 0, 0, 0) : new Thickness(0, 28, 0, 0);
        FullScreenTitleText.FontSize = compact ? 18 : 24;
        FullScreenArtistText.FontSize = compact ? 14 : 16;
        FullScreenArtistLink.Margin = compact ? new Thickness(0, 4, 0, 0) : new Thickness(0, 8, 0, 0);
        var textAlign = compact ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        FullScreenTitleText.HorizontalAlignment = FullScreenArtistLink.HorizontalAlignment = textAlign;
        FullScreenTitleText.TextAlignment = FullScreenArtistText.TextAlignment = compact ? TextAlignment.Left : TextAlignment.Center;
        if (!compact)
            FullScreenTitleText.MaxWidth = FullScreenArtistText.MaxWidth = 360;
        Grid.SetColumn(FullScreenSidePanel, compact ? 0 : 2);
        Grid.SetColumnSpan(FullScreenSidePanel, compact ? 3 : 1);
        Grid.SetRow(FullScreenSidePanel, compact ? 1 : 0);
        Grid.SetRowSpan(FullScreenSidePanel, compact ? 1 : 2);
        FullScreenCloseButton.Margin = new Thickness(compact ? 12 : 24);

        // Playback bar: now-playing info + transport controls only.
        PlaybackBarBorder.Padding = compact ? new Thickness(10, 8, 10, 8) : new Thickness(24, 12, 24, 12);
        PlayInfoColumn.Width = compact ? GridLength.Auto : new GridLength(300);
        PlayExtraColumn.Width = new GridLength(compact ? 0 : 200);
        PlayExtraPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NowPlayingArtBox.Width = NowPlayingArtBox.Height = compact ? 44 : 56;
        NowPlayingTextPanel.Margin = compact ? new Thickness(10, 0, 0, 0) : new Thickness(12, 0, 0, 0);
        if (!compact)
        {
            NowPlayingTextPanel.MaxWidth = 220;
            PreviousButton.Visibility = Visibility.Visible;
        }
        PlayControlsPanel.Margin = compact ? new Thickness(8, 0, 0, 0) : new Thickness(24, 0, 24, 0);
        // Only previous / play-pause / next (+ queue) fit next to the now-playing info on a phone.
        var extras = compact ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = ShuffleButton.Visibility = RepeatButton.Visibility = extras;

        // "Download via SpotiDrome": the desktop rows (64px thumbnail, badge text, length column)
        // leave a phone's title a few letters.
        SpotiDromeSection.Padding = compact ? new Thickness(12, 12, 12, 12) : new Thickness(20, 16, 20, 16);
        var ytTemplate = (DataTemplate)Resources[compact ? "YtTrackCompactTemplate" : "YtTrackTemplate"];
        YtSongsList.ItemTemplate = YtVideosList.ItemTemplate = ytTemplate;
    }

    // Play keeps its label; Next / Last show just their icon on a phone (they don't fit otherwise).
    private static void CompactActionButtons(StackPanel buttons, bool compact)
    {
        buttons.Spacing = compact ? 6 : 12;
        foreach (var (button, index) in buttons.Children.OfType<Button>().Select((b, i) => (b, i)))
        {
            if (compact)
                button.Padding = new Thickness(11, 6, 11, 6);
            else
                button.ClearValue(Control.PaddingProperty);   // back to the pill style's padding
            if (index > 0 && button.Content is StackPanel { } content)
                foreach (var label in content.Children.OfType<TextBlock>())
                    label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Track lists: the title column fills what the time + favorite columns leave.</summary>
    private void UpdateCompactWidths(double width)
    {
        ((Controls.TrackColumnLayout)Resources["TrackColumns"]).SetCompact(true, width - 150);
        // Playback bar: the controls (~180px) and the cover/heart keep their size, the title gets
        // the rest. Narrow phones (~360px) drop Previous so the title stays readable.
        PreviousButton.Visibility = width < 380 ? Visibility.Collapsed : Visibility.Visible;
        var controlsWidth = width < 380 ? 145 : 181;
        NowPlayingTextPanel.MaxWidth = Math.Max(60, width - 20 - 44 - 10 - 36 - 8 - controlsWidth - 4);

        // Full-screen player title next to its 88px cover (16px margins, 14px gap).
        FullScreenTitleText.MaxWidth = FullScreenArtistText.MaxWidth = Math.Max(100, width - 32 - 88 - 14);
    }

    private void UpdateCompactSidebar()
    {
        SidebarPane.Visibility = !IsCompact || _compactSidebarOpen ? Visibility.Visible : Visibility.Collapsed;
        CompactMenuButton.Visibility = IsCompact && _compactSidebarOpen ? Visibility.Visible : Visibility.Collapsed;
        CompactMenuButton.Content = "✕";
        UpdateCompactNavHighlight(_compactSidebarOpen ? "More" : _compactNavTag);
    }

    // ----- Bottom navigation bar (#25) -----
    private string _compactNavTag = "Home";

    private void CompactNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;
        switch (tag)
        {
            case "More":
                _compactSidebarOpen = !_compactSidebarOpen;
                UpdateCompactSidebar();
                break;
            case "Search":
                // Its own page (it used to open the More menu, i.e. the same page as More).
                CloseCompactSidebar();
                OpenSearchPage();
                break;
            default:
                var nav = tag switch { "Artists" => NavArtists, "Albums" => NavAlbums, "Songs" => NavSongs, _ => NavHome };
                if (nav.IsChecked == true)
                    CloseCompactSidebar();
                else
                    nav.IsChecked = true;   // Nav_Checked shows the page (and closes the menu)
                break;
        }
    }

    /// <summary>Called from Nav_Checked, so the bar also follows navigation from elsewhere.</summary>
    private void OnCompactNavigated(string tag)
    {
        _compactNavTag = tag is "Home" or "Search" or "Artists" or "Albums" or "Songs" ? tag : "More";
        UpdateCompactNavHighlight(_compactNavTag);
    }

    private void UpdateCompactNavHighlight(string activeTag)
    {
        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        var normal = (Brush)Application.Current.Resources["TextSecondaryBrush"];
        foreach (var button in CompactNavGrid.Children.OfType<Button>())
        {
            var brush = (string)button.Tag == activeTag ? accent : normal;
            if (button.Content is StackPanel panel)
                foreach (var child in panel.Children)
                {
                    if (child is Microsoft.UI.Xaml.Shapes.Path path) path.Stroke = brush;
                    if (child is TextBlock text) text.Foreground = brush;
                }
        }
    }

    private void CompactMenuButton_Click(object sender, RoutedEventArgs e)
    {
        _compactSidebarOpen = !_compactSidebarOpen;
        UpdateCompactSidebar();
    }

    /// <summary>Picking a page in the overlay sidebar closes it again.</summary>
    private void CloseCompactSidebar()
    {
        if (!IsCompact || !_compactSidebarOpen)
            return;
        _compactSidebarOpen = false;
        UpdateCompactSidebar();
    }
}

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
        CompactMenuButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetZIndex(CompactMenuButton, 20);   // stays above the open menu, so ✕ can close it
        PageArea.Margin = compact ? new Thickness(0, 42, 0, 0) : new Thickness(0);

        // Sidebar: its own column on desktop, an overlay panel over the page in compact mode.
        Grid.SetColumnSpan(SidebarPane, compact ? 4 : 1);
        SidebarPane.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        SidebarPane.Width = compact ? 240 : double.NaN;
        SidebarPane.Padding = compact ? new Thickness(8, 46, 8, 8) : new Thickness(0);
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
        AlbumActionButtons.Spacing = compact ? 6 : 12;
        // Play keeps its label; Next / Last show just their icon on a phone (they don't fit otherwise).
        foreach (var (button, index) in AlbumActionButtons.Children.OfType<Button>().Select((b, i) => (b, i)))
        {
            if (compact)
                button.Padding = new Thickness(11, 6, 11, 6);
            else
                button.ClearValue(Control.PaddingProperty);   // back to the pill style's padding
            if (index > 0 && button.Content is StackPanel { } content)
                foreach (var label in content.Children.OfType<TextBlock>())
                    label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }

        // Playback bar: now-playing info + transport controls only.
        PlaybackBarBorder.Padding = compact ? new Thickness(10, 8, 10, 8) : new Thickness(24, 12, 24, 12);
        PlayInfoColumn.Width = compact ? GridLength.Auto : new GridLength(300);
        PlayExtraColumn.Width = new GridLength(compact ? 0 : 200);
        PlayExtraPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NowPlayingTextPanel.MaxWidth = compact ? 120 : 220;
        PlayControlsPanel.Margin = compact ? new Thickness(8, 0, 0, 0) : new Thickness(24, 0, 24, 0);
        // Only previous / play-pause / next (+ queue) fit next to the now-playing info on a phone.
        var extras = compact ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = ShuffleButton.Visibility = RepeatButton.Visibility = extras;
    }

    /// <summary>Track lists: the title column fills what the time + favorite columns leave.</summary>
    private void UpdateCompactWidths(double width)
    {
        ((Controls.TrackColumnLayout)Resources["TrackColumns"]).SetCompact(true, width - 150);
    }

    private void UpdateCompactSidebar()
    {
        SidebarPane.Visibility = !IsCompact || _compactSidebarOpen ? Visibility.Visible : Visibility.Collapsed;
        CompactMenuButton.Content = _compactSidebarOpen ? "✕" : "☰";
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

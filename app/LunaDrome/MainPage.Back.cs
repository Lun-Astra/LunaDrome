using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace LunaDrome;

// Back navigation: the pages are swapped in place (HideAllPages + ShowPage), so there was no
// history and Android's back button closed the app. Every page switch now passes how to reopen
// that page, and HandleBack() walks back through overlays first, then the pages.
public sealed partial class MainPage
{
    private const int MaxBackHistory = 30;
    private readonly List<Action> _backHistory = new();
    private Action? _currentPageReopen;
    private bool _navigatingBack;

    /// <summary>Called by ShowPage: remembers how to get back to the page that was showing.</summary>
    private void RecordPage(Action? reopen)
    {
        if (reopen == null)
            return;
        if (!_navigatingBack && _currentPageReopen != null)
        {
            _backHistory.Add(_currentPageReopen);
            if (_backHistory.Count > MaxBackHistory)
                _backHistory.RemoveAt(0);
        }
        _currentPageReopen = reopen;
    }

    // A nav item that's already checked doesn't raise Checked again, so uncheck it first.
    private static void Reselect(RadioButton nav)
    {
        nav.IsChecked = false;
        nav.IsChecked = true;
    }

    /// <summary>One step back. False when there's nothing left to go back to.</summary>
    private bool HandleBack()
    {
        if (InSelectionMode)
        {
            ExitSelection();
            return true;
        }
        if (FullScreenPlayerOverlay.Visibility == Visibility.Visible)
        {
            if (IsCompact && _compactPlayerTabs)
            {
                _compactPlayerTabs = false;
                UpdateCompactPlayerMode();
            }
            else
            {
                FullScreenPlayerOverlay.Visibility = Visibility.Collapsed;
                UpdateVisualizerCaptureState();
            }
            return true;
        }
        if (IsCompact && _compactSidebarOpen)
        {
            CloseCompactSidebar();
            return true;
        }
        if (QueueSidePanel.Visibility == Visibility.Visible)
        {
            QueueSidePanel.Visibility = Visibility.Collapsed;
            return true;
        }
        if (_backHistory.Count == 0)
            return false;

        var reopen = _backHistory[^1];
        _backHistory.RemoveAt(_backHistory.Count - 1);
        _navigatingBack = true;
        try
        {
            reopen();
        }
        finally
        {
            _navigatingBack = false;
        }
        return true;
    }

    // ----- #29 follow-up: the phone's bottom bar -----

    // Tapping the bar anywhere except its buttons / progress bar / heart opens the player.
    private void PlaybackBar_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!IsCompact || e.Handled || FullScreenPlayerOverlay.Visibility == Visibility.Visible
            || (_current == null && !JamOwnsPlayback))   // nothing to show yet
            return;
        for (var element = e.OriginalSource as DependencyObject; element != null && element != PlaybackBarBorder;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase or RangeBase || element == NowPlayingFavorite)
                return;
        }
        FullScreenPlayerOverlay.Visibility = Visibility.Visible;
        UpdateVisualizerCaptureState();
        e.Handled = true;
    }

    // Tooltips are for a mouse; on a touchscreen they pop up on a tap ("Show/hide Queue, Lyrics,
    // Visualizer" covering the bar). The phone layout drops the bottom bar's tooltips.
    private readonly Dictionary<DependencyObject, object> _barToolTips = new();

    private void ApplyBarToolTips(bool compact)
    {
        DependencyObject[] elements = { NowPlayingTitleLink, NowPlayingArtistLink, NowPlayingFavorite,
                                        PreviousButton, PlayPauseButton, NextButton, QueueToggleButton };
        foreach (var element in elements)
        {
            if (compact)
            {
                if (!_barToolTips.ContainsKey(element) && ToolTipService.GetToolTip(element) is { } tip)
                    _barToolTips[element] = tip;
                ToolTipService.SetToolTip(element, null);
            }
            else if (_barToolTips.TryGetValue(element, out var tip))
            {
                ToolTipService.SetToolTip(element, tip);
            }
        }
    }
}

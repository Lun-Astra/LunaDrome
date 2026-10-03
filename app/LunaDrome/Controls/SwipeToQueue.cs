using LunaDrome.Core.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LunaDrome.Controls;

/// <summary>
/// Android only (#29): swipe a song row to the right to add the song to the queue. Set
/// controls:SwipeToQueue.Enabled="True" on a row template's root (its DataContext is the Song).
///
/// Deliberately not a ManipulationMode gesture: a horizontal manipulation on rows inside a
/// scrolling list can take the touch away from the list, so it stops scrolling. This only
/// watches the pointer - no capture, nothing marked handled - and counts a swipe once the
/// finger has clearly moved sideways. If the list starts scrolling it takes the pointer
/// (PointerCaptureLost) and the swipe is dropped.
/// </summary>
public static class SwipeToQueue
{
    /// <summary>Raised for a finished swipe; MainPage adds the song to the queue.</summary>
    public static event Action<Song>? Swiped;

    private const double SwipeDistance = 80;   // px to the right to count
    private const double StartSlop = 16;       // px before deciding sideways vs. scrolling
    private const double MaxSlide = 120;       // how far the row follows the finger

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SwipeToQueue),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!OperatingSystem.IsAndroid() || d is not FrameworkElement row || !(bool)e.NewValue)
            return;
        new Tracker(row);
    }

    private sealed class Tracker
    {
        private readonly FrameworkElement _row;
        private readonly TranslateTransform _slide = new();
        private Windows.Foundation.Point _start;
        private bool _tracking;
        private bool _swiping;
        private double _dx;

        public Tracker(FrameworkElement row)
        {
            _row = row;
            row.RenderTransform = _slide;
            row.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Pressed), true);
            row.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Moved), true);
            row.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Released), true);
            row.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => Reset()), true);
            row.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => Reset()), true);
        }

        private void Pressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
                return;
            _start = e.GetCurrentPoint(null).Position;
            _tracking = true;
            _swiping = false;
            _dx = 0;
        }

        private void Moved(object sender, PointerRoutedEventArgs e)
        {
            if (!_tracking)
                return;
            var p = e.GetCurrentPoint(null).Position;
            _dx = p.X - _start.X;
            var dy = p.Y - _start.Y;
            if (!_swiping)
            {
                if (Math.Abs(dy) > StartSlop && Math.Abs(dy) >= Math.Abs(_dx))
                {
                    Reset();   // vertical: that's the list scrolling
                    return;
                }
                if (_dx > StartSlop && _dx > 2 * Math.Abs(dy))
                    _swiping = true;
                else
                    return;
            }
            _slide.X = Math.Clamp(_dx, 0, MaxSlide);
        }

        private void Released(object sender, PointerRoutedEventArgs e)
        {
            var swiped = _swiping && _dx >= SwipeDistance;
            Reset();
            if (swiped && _row.DataContext is Song song)
                Swiped?.Invoke(song);
        }

        private void Reset()
        {
            _tracking = false;
            _swiping = false;
            _slide.X = 0;
        }
    }
}

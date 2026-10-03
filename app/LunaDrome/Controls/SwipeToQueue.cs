using LunaDrome.Core.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LunaDrome.Controls;

/// <summary>
/// Android only (#29): swipe a song row to the right to add the song to the queue. Set
/// controls:SwipeToQueue.Enabled="True" on a row template's root (its DataContext is the Song).
///
/// It only watches the pointer - no capture, nothing marked handled - so the list keeps its
/// vertical scrolling. An emulator test (ci/swipe_test.sh) showed why waiting for the finger
/// to lift was unreliable: on Android the touch is taken away (PointerCaptureLost) as soon as
/// the finger drifts ~16px vertically, even with the list's scrolling locked and the pointer
/// captured. So the song is added the moment the swipe has gone far enough sideways, and a
/// lost touch after that doesn't matter. Every press starts a fresh gesture.
/// </summary>
public static class SwipeToQueue
{
    /// <summary>Raised for a finished swipe; MainPage adds the song to the queue.</summary>
    public static event Action<Song>? Swiped;

    private const double SwipeDistance = 60;   // px to the right: added right there
    private const double StartSlop = 12;       // px before deciding sideways vs. scrolling
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


    // The row being swiped; a press on another row resets it in case its own release never came.
    private static Tracker? _active;

    private sealed class Tracker
    {
        private readonly FrameworkElement _row;
        private readonly DispatcherTimer _snapBack = new() { Interval = TimeSpan.FromMilliseconds(350) };
        private readonly TranslateTransform _slide = new();
        private Windows.Foundation.Point _start;
        private uint _pointerId;
        private bool _tracking;
        private bool _swiping;
        private bool _added;

        public Tracker(FrameworkElement row)
        {
            _row = row;
            row.RenderTransform = _slide;
            // The touch is often taken away without a release reaching the row (emulator test),
            // which left it slid to the right. So: no move or release for a moment = slide back.
            _snapBack.Tick += (_, _) => { _snapBack.Stop(); Reset(); };
            row.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Pressed), true);
            row.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Moved), true);
            row.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Released), true);
            row.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => End()), true);
            row.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => End()), true);
        }

        private void Pressed(object sender, PointerRoutedEventArgs e)
        {
            if (_active != null && _active != this)
                _active.Reset();
            Reset();   // a new touch is always a new gesture
            _active = this;
            if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
                return;
            _start = e.GetCurrentPoint(null).Position;
            _pointerId = e.Pointer.PointerId;
            _tracking = true;
        }

        private void Moved(object sender, PointerRoutedEventArgs e)
        {
            if (!_tracking || e.Pointer.PointerId != _pointerId)
                return;
            var p = e.GetCurrentPoint(null).Position;
            var dx = p.X - _start.X;
            var dy = p.Y - _start.Y;
            if (!_swiping)
            {
                if (Math.Abs(dy) > StartSlop && Math.Abs(dy) >= Math.Abs(dx))
                {
                    Reset();   // vertical: that's the list scrolling
                    return;
                }
                if (dx > StartSlop && dx > 2 * Math.Abs(dy))
                    _swiping = true;
                else
                    return;
            }
            _slide.X = Math.Clamp(dx, 0, MaxSlide);
            _snapBack.Stop();
            _snapBack.Start();   // restarted by every move; fires once the moves stop
            if (dx >= SwipeDistance)
                Add();
        }

        // A quick flick sends only a few moves (the emulator test saw the last one at 57px of a
        // 150px swipe), so where the finger lifts counts too.
        private void Released(object sender, PointerRoutedEventArgs e)
        {
            if (_tracking && _swiping && e.Pointer.PointerId == _pointerId
                && e.GetCurrentPoint(null).Position.X - _start.X >= SwipeDistance)
                Add();
            End();
        }

        private void Add()
        {
            if (_added)
                return;
            _added = true;
            if (_row.DataContext is Song song)
                Swiped?.Invoke(song);
        }

        private void End() => Reset();

        private void Reset()
        {
            _snapBack.Stop();
            _tracking = false;
            _swiping = false;
            _added = false;
            _slide.X = 0;
        }
    }
}

using LunaDrome.Core.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LunaDrome.Controls;

/// <summary>
/// Android only. Song rows (#29): controls:SwipeToQueue.Enabled="True" - swipe right to add the
/// row's Song to the queue. Queue rows (#30): controls:SwipeToQueue.RemoveEnabled="True" - swipe
/// left to remove the row's entry from the queue.
///
/// It only watches the pointer - no capture, nothing marked handled - so the list keeps its
/// vertical scrolling. An emulator test (branch ci/android-ui-test) showed why waiting for the
/// finger to lift was unreliable: on Android the touch is taken away (PointerCaptureLost) as soon
/// as the finger drifts ~16px vertically, even with the list's scrolling locked and the pointer
/// captured, and a quick swipe sends only a few moves. So the action happens the moment the swipe
/// has gone far enough sideways (or where the finger lifts), every press starts a fresh gesture,
/// and the row slides back as soon as the moves stop. Build with -p:SwipeTrace=true to log every
/// touch event to logcat (tag LunaSwipe), as the emulator test does.
/// </summary>
public static class SwipeToQueue
{
    /// <summary>A song row was swiped right; MainPage adds the song to the queue.</summary>
    public static event Action<Song>? Swiped;

    /// <summary>A queue row was swiped left; MainPage removes that entry (the row's DataContext).</summary>
    public static event Action<object>? SwipedToRemove;

    private const double SwipeDistance = 60;   // px sideways: the action happens right there
    private const double StartSlop = 12;       // px before deciding sideways vs. scrolling
    private const double MaxSlide = 120;       // how far the row follows the finger

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SwipeToQueue),
            new PropertyMetadata(false, (d, e) => Attach(d, e, right: true)));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    public static readonly DependencyProperty RemoveEnabledProperty =
        DependencyProperty.RegisterAttached("RemoveEnabled", typeof(bool), typeof(SwipeToQueue),
            new PropertyMetadata(false, (d, e) => Attach(d, e, right: false)));

    public static bool GetRemoveEnabled(DependencyObject d) => (bool)d.GetValue(RemoveEnabledProperty);
    public static void SetRemoveEnabled(DependencyObject d, bool value) => d.SetValue(RemoveEnabledProperty, value);

    private static void Attach(DependencyObject d, DependencyPropertyChangedEventArgs e, bool right)
    {
        if (!OperatingSystem.IsAndroid() || d is not FrameworkElement row || !(bool)e.NewValue)
            return;
        new Tracker(row, right);
    }

    private static void Trace(string message)
    {
#if SWIPE_TRACE && ANDROID
        Android.Util.Log.Info("LunaSwipe", message);
#endif
    }

    // The row being swiped; a press on another row resets it in case its own release never came.
    private static Tracker? _active;

    private sealed class Tracker
    {
        private readonly FrameworkElement _row;
        private readonly int _direction;   // +1 = swipe right (add), -1 = swipe left (remove)
        private readonly TranslateTransform _slide = new();
        private readonly DispatcherTimer _snapBack = new() { Interval = TimeSpan.FromMilliseconds(350) };
        private Windows.Foundation.Point _start;
        private uint _pointerId;
        private bool _tracking;
        private bool _swiping;
        private bool _done;

        public Tracker(FrameworkElement row, bool right)
        {
            _row = row;
            _direction = right ? 1 : -1;
            row.RenderTransform = _slide;
            // The touch is often taken away without a release reaching the row (emulator test),
            // which left it slid sideways. So: no move or release for a moment = slide back.
            _snapBack.Tick += (_, _) => { _snapBack.Stop(); Reset(); };
            row.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Pressed), true);
            row.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Moved), true);
            row.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Released), true);
            row.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => Reset()), true);
            row.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => Reset()), true);
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
            Trace($"pressed dir={_direction} at {_start}");
        }

        private void Moved(object sender, PointerRoutedEventArgs e)
        {
            if (!_tracking || e.Pointer.PointerId != _pointerId)
                return;
            var p = e.GetCurrentPoint(null).Position;
            var along = (p.X - _start.X) * _direction;   // distance in the swipe's own direction
            var dy = p.Y - _start.Y;
            Trace($"moved along={along:F0} dy={dy:F0} swiping={_swiping} done={_done}");
            if (!_swiping)
            {
                if (Math.Abs(dy) > StartSlop && Math.Abs(dy) >= Math.Abs(p.X - _start.X))
                {
                    Reset();   // vertical: that's the list scrolling
                    return;
                }
                if (along > StartSlop && along > 2 * Math.Abs(dy))
                    _swiping = true;
                else
                    return;
            }
            _slide.X = _direction * Math.Clamp(along, 0, MaxSlide);
            _snapBack.Stop();
            _snapBack.Start();   // restarted by every move; fires once the moves stop
            if (along >= SwipeDistance)
                Act();
        }

        // A quick flick sends only a few moves (the emulator test saw the last one at 57px of a
        // 150px swipe), so where the finger lifts counts too.
        private void Released(object sender, PointerRoutedEventArgs e)
        {
            if (_tracking && _swiping && e.Pointer.PointerId == _pointerId
                && (e.GetCurrentPoint(null).Position.X - _start.X) * _direction >= SwipeDistance)
                Act();
            Reset();
        }

        private void Act()
        {
            if (_done)
                return;
            _done = true;
            Trace($"ACTED dir={_direction}");
            if (_direction > 0 && _row.DataContext is Song song)
                Swiped?.Invoke(song);
            else if (_direction < 0 && _row.DataContext is { } entry)
                SwipedToRemove?.Invoke(entry);
        }

        private void Reset()
        {
            _snapBack.Stop();
            _tracking = false;
            _swiping = false;
            _done = false;
            _slide.X = 0;
        }
    }
}

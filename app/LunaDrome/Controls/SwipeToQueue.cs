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

    /// <summary>A row was held still (~0.5s). ListView rows also get a RightTapped from a hold,
    /// the album page's ItemsRepeater rows don't (emulator test), so MainPage uses this there.</summary>
    public static event Action<FrameworkElement>? Held;

    private const int HoldMs = 500;

    private const double SwipeDistance = 60;   // px sideways: the action happens right there
    private const double StartSlop = 10;       // px before deciding sideways vs. scrolling
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

    internal static void Trace(string message)
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
        private readonly DispatcherTimer _snapBack = new() { Interval = TimeSpan.FromMilliseconds(600) };
        private Brush? _restBackground;
        private bool _ticked;
        private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(HoldMs) };
        private Windows.Foundation.Point _start;
        private Windows.Foundation.Point _rowStart;   // where the row was on screen at the press
        private uint _pointerId;
        private bool _tracking;
        private bool _swiping;
        private bool _done;

        public Tracker(FrameworkElement row, bool right)
        {
            _row = row;
            _direction = right ? 1 : -1;
            // The touch is often taken away without a release reaching the row (emulator test),
            // which left it slid sideways. So: no move or release for a moment = slide back.
            _snapBack.Tick += (_, _) => { _snapBack.Stop(); Reset(); };
            _hold.Tick += (_, _) =>
            {
                _hold.Stop();
                if (!_tracking || _swiping)
                    return;
                // Scrolling the list moves the row while Android stops sending the finger's
                // moves to it - that's not a hold (scrolling an album used to start selecting).
                var now = RowPosition();
                if (Math.Abs(now.X - _rowStart.X) > StartSlop / 2 || Math.Abs(now.Y - _rowStart.Y) > StartSlop / 2)
                {
                    Trace("HOLD ignored: the row moved (scrolling)");
                    Reset();
                    return;
                }
                Trace("HELD");
                Reset();   // a hold isn't a swipe
                Held?.Invoke(_row);
            };
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
            _rowStart = RowPosition();
            _pointerId = e.Pointer.PointerId;
            _tracking = true;
            _hold.Start();
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
            if (Math.Abs(p.X - _start.X) > StartSlop || Math.Abs(dy) > StartSlop)
                _hold.Stop();   // moving: not a hold
            if (!_swiping)
            {
                if (Math.Abs(dy) > StartSlop * 1.5 && Math.Abs(dy) > 1.2 * Math.Abs(p.X - _start.X))
                {
                    Reset();   // vertical: that's the list scrolling
                    return;
                }
                if (along > StartSlop && along > 1.2 * Math.Abs(dy))
                {
                    _swiping = true;
                    if (_row is Panel or Border)
                        _restBackground = _row is Panel panel ? panel.Background : ((Border)_row).Background;
                }
                else
                    return;
            }
            // The row follows the finger (with some resistance past the threshold) and tints
            // more the closer it gets; crossing the threshold ticks, the action bumps.
            var slide = along <= SwipeDistance ? along : SwipeDistance + (along - SwipeDistance) * 0.4;
            Motion.EnsureTransform(_row).TranslateX = _direction * Math.Clamp(slide, 0, MaxSlide);
            Tint(Math.Clamp(along / SwipeDistance, 0, 1));
            _snapBack.Stop();
            _snapBack.Start();   // restarted by every move; fires once the moves stop
            if (along >= SwipeDistance * 0.8 && !_ticked)
            {
                _ticked = true;
                LunaDrome.Services.Haptics.Tick();
            }
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

        private Windows.Foundation.Point RowPosition()
        {
            try { return _row.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0)); }
            catch { return default; }
        }

        private void Act()
        {
            if (_done)
                return;
            _done = true;
            Trace($"ACTED dir={_direction}");
            LunaDrome.Services.Haptics.Confirm();
            Tint(1.6);   // a flash: done
            if (_direction > 0 && _row.DataContext is Song song)
                Swiped?.Invoke(song);
            else if (_direction < 0 && _row.DataContext is { } entry)
                SwipedToRemove?.Invoke(entry);
        }

        private void Reset()
        {
            _hold.Stop();
            _snapBack.Stop();
            var wasSwiping = _swiping;
            _tracking = false;
            _swiping = false;
            _done = false;
            _ticked = false;
            if (!wasSwiping)
                return;
            SpringBack();
            if (_row is Panel panel) panel.Background = _restBackground;
            else if (_row is Border border) border.Background = _restBackground;
        }

        // Green-ish for "add to queue", red-ish for "remove", stronger the further the swipe.
        private void Tint(double amount)
        {
            var color = _direction > 0
                ? Windows.UI.Color.FromArgb((byte)Math.Clamp(amount * 40, 0, 90), 0x5B, 0xD6, 0x8A)
                : Windows.UI.Color.FromArgb((byte)Math.Clamp(amount * 40, 0, 90), 0xE5, 0x5A, 0x5A);
            if (_row is Panel panel) panel.Background = new SolidColorBrush(color);
            else if (_row is Border border) border.Background = new SolidColorBrush(color);
        }

        private void SpringBack()
        {
            var transform = Motion.EnsureTransform(_row);
            var back = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = 0, Duration = TimeSpan.FromMilliseconds(320),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.BackEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut, Amplitude = 0.35
                },
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(back, transform);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(back, nameof(CompositeTransform.TranslateX));
            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            storyboard.Children.Add(back);
            storyboard.Completed += (_, _) =>
            {
                transform.TranslateX = 0;
                storyboard.Stop();
            };
            storyboard.Begin();
        }
    }
}

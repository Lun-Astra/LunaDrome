using Microsoft.UI.Xaml.Input;

namespace LunaDrome.Controls;

/// <summary>
/// Fast *and* smooth mouse-wheel scrolling (Luna: "scrolling is very slow", then "laggy /
/// stutters"). Set controls:WheelScroll.Enabled="True" on a ScrollViewer or on a
/// ListView/GridView (its inner ScrollViewer is found on load).
///
/// History: Uno's default wheel scroll animates a short distance slowly; the first fix jumped a
/// full 150px per notch in a single frame, which a scroll log showed was exactly the "stutter"
/// (big instant jumps, no in-between frames). Now each notch adds to a target offset and the view
/// glides there over ~150ms with an ease-out (browser-style smooth scrolling): spinning the wheel
/// quickly keeps accumulating distance, a single notch still moves a good amount.
///
/// The handler sits on the ScrollViewer's *content*, so it sees the wheel event before the
/// ScrollViewer and marks it handled - otherwise the ScrollViewer's own scroll would run too.
/// </summary>
public static class WheelScroll
{
    public const double PixelsPerNotch = 140;

    // Fraction of the remaining distance covered per 60Hz frame (scaled for other frame times):
    // 0.25 = ~95% there after 10 frames' worth of time (~170ms).
    private const double Easing = 0.25;

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(WheelScroll),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && (bool)e.NewValue)
        {
            element.Loaded += (_, _) => Attach(element);
            if (element.IsLoaded)
                Attach(element);
        }
    }

    private static readonly DependencyProperty AttachedProperty =
        DependencyProperty.RegisterAttached("Attached", typeof(bool), typeof(WheelScroll), new PropertyMetadata(false));

    private static void Attach(FrameworkElement element)
    {
        if ((bool)element.GetValue(AttachedProperty))
            return;

        var scrollViewer = element as ScrollViewer ?? FindDescendant<ScrollViewer>(element);
        if (scrollViewer?.Content is not UIElement content)
            return;

        element.SetValue(AttachedProperty, true);
        var smoother = new Smoother(scrollViewer);
        content.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, args) =>
        {
            var props = args.GetCurrentPoint(scrollViewer).Properties;
            if (props.IsHorizontalMouseWheel || scrollViewer.ScrollableHeight <= 0)
                return;

            smoother.Add(-props.MouseWheelDelta / 120.0 * PixelsPerNotch);
            args.Handled = true;
        }), true);
    }

    /// <summary>Per-ScrollViewer glide toward an accumulated wheel target.</summary>
    /// <remarks>
    /// Stepped once per rendered frame (CompositionTarget.Rendering), with the easing scaled by the
    /// time since the last frame. It used to step on a 16ms DispatcherTimer with a fixed 25% per
    /// tick; Windows timers are ~15.6ms-grained, so that fired every 15.6 or 31.2ms, unevenly, and
    /// the view moved in uneven jumps at 30-60 steps a second on a 180Hz screen. Rendering is only
    /// subscribed while a glide runs: a permanent handler keeps Uno's Skia host redrawing at the
    /// monitor's refresh rate (see the visualizer timer in MainPage).
    /// </remarks>
    private sealed class Smoother
    {
        private const double FrameMs = 1000.0 / 60;   // Easing is defined per 60Hz frame

        private readonly ScrollViewer _scrollViewer;
        private readonly System.Diagnostics.Stopwatch _clock = new();
        private double _position;
        private double _target;
        private double _lastFrameMs;
        private bool _animating;

        public Smoother(ScrollViewer scrollViewer) => _scrollViewer = scrollViewer;

        public void Add(double delta)
        {
            // Start from where the view actually is (it may have moved via scrollbar/keyboard).
            if (!_animating)
                _position = _target = _scrollViewer.VerticalOffset;

            _target = Math.Clamp(_target + delta, 0, _scrollViewer.ScrollableHeight);
            if (!_animating)
            {
                _animating = true;
                _clock.Restart();
                _lastFrameMs = 0;
                CompositionTarget.Rendering += OnRendering;
                Step(1);
            }
        }

        private void OnRendering(object? sender, object e)
        {
            var now = _clock.Elapsed.TotalMilliseconds;
            // Capped so a long stall doesn't turn into one big jump.
            var frames = Math.Clamp((now - _lastFrameMs) / FrameMs, 0, 4);
            _lastFrameMs = now;
            Step(frames);
        }

        private void Step(double frames)
        {
            var remaining = _target - _position;
            if (Math.Abs(remaining) < 0.5)
            {
                _position = _target;
                CompositionTarget.Rendering -= OnRendering;
                _animating = false;
            }
            else
            {
                _position += remaining * (1 - Math.Pow(1 - Easing, frames));
            }

            _scrollViewer.ChangeView(null, _position, null, true);
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : class, DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }
}

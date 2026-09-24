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

    // Fraction of the remaining distance covered per ~16ms frame: 0.25 = ~95% there in 10 frames.
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
    private sealed class Smoother
    {
        private readonly ScrollViewer _scrollViewer;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
        private double _position;
        private double _target;
        private bool _animating;

        public Smoother(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;
            _timer.Tick += (_, _) => Step();
        }

        public void Add(double delta)
        {
            // Start from where the view actually is (it may have moved via scrollbar/keyboard).
            if (!_animating)
                _position = _target = _scrollViewer.VerticalOffset;

            _target = Math.Clamp(_target + delta, 0, _scrollViewer.ScrollableHeight);
            if (!_animating)
            {
                _animating = true;
                Step();
                _timer.Start();
            }
        }

        private void Step()
        {
            var remaining = _target - _position;
            if (Math.Abs(remaining) < 0.5)
            {
                _position = _target;
                _timer.Stop();
                _animating = false;
            }
            else
            {
                _position += remaining * Easing;
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

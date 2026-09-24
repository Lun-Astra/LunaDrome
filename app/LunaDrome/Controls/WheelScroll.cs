using Microsoft.UI.Xaml.Input;

namespace LunaDrome.Controls;

/// <summary>
/// Faster mouse-wheel scrolling (Luna: "scrolling is very slow"). Set
/// controls:WheelScroll.Enabled="True" on a ScrollViewer or on a ListView/GridView (its inner
/// ScrollViewer is found on load). Each wheel notch then moves <see cref="PixelsPerNotch"/>,
/// without the smooth-scroll animation that made Uno's default wheel feel sluggish.
///
/// The handler sits on the ScrollViewer's *content*, so it sees the wheel event before the
/// ScrollViewer does and marks it handled - otherwise the ScrollViewer would also apply its own
/// (slow, animated) scroll on top.
/// </summary>
public static class WheelScroll
{
    public const double PixelsPerNotch = 150;

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
        content.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, args) =>
        {
            var props = args.GetCurrentPoint(scrollViewer).Properties;
            if (props.IsHorizontalMouseWheel || scrollViewer.ScrollableHeight <= 0)
                return;

            var notches = props.MouseWheelDelta / 120.0;
            var target = Math.Clamp(scrollViewer.VerticalOffset - notches * PixelsPerNotch, 0, scrollViewer.ScrollableHeight);
            scrollViewer.ChangeView(null, target, null, true);
            args.Handled = true;
        }), true);
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
